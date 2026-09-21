using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Arbitration;
using Eztools.Host.Processes;
using Eztools.Host.Registry;
using Eztools.Host.Tray;

namespace Eztools.Desktop;

/// <summary>
/// 托盘本体：NotifyIcon + 右键菜单 + 进程内宿主。
///
/// **为什么是"进程内宿主"而不是"点一次起一个 ezt 子进程"**：
/// 托盘要当"治理者"而不是"启动器" —— `resident` 工具保活、热键仲裁、独占资源仲裁
/// 都需要一个长期活着的宿主。顺带每次点击省掉约 450ms 的宿主启动开销。
/// （探针实测子进程方式也可行，作为降级路径保留。）
///
/// 菜单数据来自 <see cref="TrayMenuBuilder"/> —— 那是**与 UI 框架无关**的合成结果，
/// 所以本文件只负责"把这些数据画成 WinForms 菜单"，不含任何业务判断。
/// </summary>
internal sealed class TrayApplication : IDisposable
{
    private readonly DesktopOptions _options;
    private readonly Icon _iconImage;
    private readonly NotifyIcon _icon;

    private EztoolsHost? _host;
private readonly Dictionary<int, TrayMenuItem> _hotkeyItems = new();
    private HotkeyHook? _hotkeys;

        private bool _busy;
    private bool _disposed;
    private IDisposable? _eventSubscription;

    /// <summary>
    /// 已打开的面板窗口（P4 Wave 2c），键 = <c>"{toolId}.{panelId}"</c>（小写）。
    /// **同一面板重复打开必须复用**（协议 §5.2）：不新开，而是激活到前台并立即重拉。
    /// 不做复用的话，用户每点一次菜单就多一个窗口，且每个窗口各自跑定时器。
    /// </summary>
    private readonly Dictionary<string, PanelWindow> _panelWindows =
        new(StringComparer.OrdinalIgnoreCase);

    public TrayApplication(DesktopOptions options)
    {
        _options = options;
        _iconImage = LoadIcon();

        _icon = new NotifyIcon
        {
            Icon = _iconImage,
            Text = "Eztools",       // 悬停提示（上限 63 字符）
            Visible = false,
        };

        _hotkeys = new HotkeyHook();
        _hotkeys.HotkeyFired += id =>
        {
            if (_hotkeyItems.TryGetValue(id, out var item))
            {
                // 热键触发与菜单点击走同一条调用路径（含剪贴板注入、气泡反馈、_busy 防重入）
                _ = InvokeAsync(item);
            }
        };

        _icon.ContextMenuStrip = new ContextMenuStrip();

        // 每次弹出都重建菜单：零开销，而且总是最新 ——
        // 用户在托盘里禁用了一个工具，下次弹菜单就看不到它。
        // 注意这里**只重建菜单模型**（不重扫清单）：重扫要重建进程层，会中断正在跑的调用，
        // 那是「刷新菜单」项的职责，不该在每次弹菜单时发生。
        _icon.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
    }

    public int Run()
    {
        // 宿主必须在 Application.Run **之前**建好：
        // 一旦消息循环跑起来，UI 线程上同步等待异步创建就会死锁
        //（WinForms 会装上同步上下文，续体排队回 UI 线程，而 UI 线程正在等它）。
        _host = EztoolsHost.CreateAsync(new HostOptions
        {
            Verbose = _options.Verbose,
            EchoLogToConsole = false,       // GUI 宿主没有控制台可回显，日志只落文件
            HostName = "ezt-desktop",
            ToolsDir = _options.ToolsDir,   // 自检/验收可指向隔离工具根（含负向夹具）
        }).GetAwaiter().GetResult();

        _host.Log.Info(
            $"桌面宿主启动：{_host.Registry.Tools.Count} 个工具可用（已启用 {_host.Registry.EnabledCount}）",
            "startup");

        // host.notify 的真实出口：工具（含 resident 工具在后台）请求弹通知 → 托盘气泡。
        // 必须在 CreateAsync 之后、RunStartupLifecycleAsync 之前订阅 —— 否则常驻工具
        // 启动时发的第一条通知会因为没有订阅者而被判成"没有 UI 宿主"。
        _host.Processes.NotifyRequested += ShowToolNotification;

        // 宿主内部事件总线（P4 Wave 2a，决策 D2）：状态变了主动反映到托盘，
        // 而不是等用户点菜单才发现。订阅必须保存 IDisposable 并在 Dispose 里退订 ——
        // 托盘重建/重载时漏退订会让已死的菜单对象继续收到事件。
        _eventSubscription = _host.Events.Subscribe(OnHostEvent);

        RebuildMenu();

        // P2 生命周期：常驻宿主启动时执行 task 工具（各一次）+ 拉起 resident 工具保活。
        // 异步执行不阻塞托盘消息循环；单工具失败在 RunStartupLifecycleAsync 内逐个吞掉。
        _ = _host.Processes.RunStartupLifecycleAsync().ContinueWith(
            t => _host.Log.Warn($"生命周期启动钩子异常：{t.Exception?.GetBaseException().Message}", "lifecycle"),
            TaskContinuationOptions.OnlyOnFaulted);

        // WPF 异常兜底：iNKORE 库内偶发无害 NRE（如圆角更新）不能拖垮整个托盘。
        // 只拦截「iNKORE 命名空间里的 NullReferenceException」这类库内异常并记日志；
        // 其余异常照旧崩溃 —— 兜底不能变成掩盖真 bug 的布。
        if (System.Windows.Application.Current is null)
        {
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        var wpfApp = System.Windows.Application.Current!;
        wpfApp.DispatcherUnhandledException += (_, e) =>
        {
            var fromInkore = e.Exception.StackTrace?.Contains("iNKORE") == true;
            _host?.Log.Error($"UI 未处理异常（{(fromInkore ? "iNKORE 库内，已拦截" : "未拦截")}）：{e.Exception}", "ui");
            e.Handled = fromInkore && e.Exception is NullReferenceException;
        };

        RegisterHotkeysFromRegistry();

        if (_options.SelfCheck)
        {
            _host.Log.Info("self-check 开始（宿主初始化完成）", "selfcheck");
            ReportSelfCheck();
            _host.Log.Info("self-check 完成，回收宿主", "selfcheck");
            // 🔴 短路退出也必须回收宿主：resident 进程不挂 kill-on-close Job，
            //    不 Dispose 就会泄漏（并持有继承的 stdout 句柄，把调用方的管道拖到不关闭）。
            Dispose();
            return 0;
        }

        // 通知通道的活体验证：走真实的 host.notify → 订阅者 → 气泡这条路，
        // 并把宿主回报的 delivered 原样落盘。订阅已在上方完成，所以这里测的是**真路径**。
        if (_options.ProbeNotify)
        {
            var code = RunProbeNotify();
            Dispose();
            return code;
        }

        if (_options.AutoClick is { } index)
        {
            var code = RunAutoClick(index);
            Dispose();
            return code;
        }

        // 热键路径的活体验证：--click 只能触发托盘项，热键项是另一批对象、Input 不同源。
        // 必须在 RegisterHotkeysFromRegistry（上方）之后 —— 探针复用注册结果，不重跑注册。
        if (_options.ProbeHotkey is { } hotkeyCommand)
        {
            var code = RunProbeHotkey(hotkeyCommand);
            Dispose();
            return code;
        }

        _icon.Visible = true;
        ShowOverflowHintOnce();

        WinForms.Application.Run();
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 面板窗口先关：它们订阅了宿主事件总线、并持有 DispatcherTimer。
        // 顺序很重要 —— 必须在退订总线与 Dispose 宿主**之前**关，
        // 否则下次宿主事件仍会推给一个即将销毁的窗口（P4 Wave 2a 的同一个坑的又一例）。
        ClosePanelWindows();

        // 退订必须在 Dispose 宿主**之前**：宿主 Dispose 时会发 ToolStopped 事件，
        // 那时图标已经销毁、_icon 调用会抛（虽然被 catch 吞掉，但日志会刷一堆噪音）。
        _eventSubscription?.Dispose();
        _eventSubscription = null;

        _icon.Visible = false;
        _icon.Dispose();
        _iconImage.Dispose();

        if (_host is not null)
        {
            // 退出前让宿主回收所有工具进程（先 tool.stop 再 kill tree）。
            // 带超时：退出路径上宁可留下孤儿进程，也不能让托盘卡住不退。
            try
            {
                if (!_host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)))
                {
                    _host.Log.Warn("回收工具进程超时（已放弃等待）", "shutdown");
                }
            }
            catch (Exception ex)
            {
                _host.Log.Warn($"回收工具进程时出错（忽略）：{ex.Message}", "shutdown");
            }
        }
    }

    /// <summary>关闭所有面板窗口并清空注册表（退出/回收路径用）。异常逐个吞掉，退出不该被窗口拖住。</summary>
    private void ClosePanelWindows()
    {
        foreach (var (key, window) in _panelWindows.ToList())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                _host?.Log.Warn($"关闭面板 {key} 失败（忽略）：{ex.Message}", "panel");
            }
        }

        _panelWindows.Clear();
    }

    // ── 菜单 ────────────────────────────────────────────────────────────────

    private void RebuildMenu()
    {
        var menu = _icon.ContextMenuStrip;
        if (menu is null)
        {
            return;
        }

        menu.Items.Clear();

        // 放在最前：托盘项少的工具只显示这一条，空的
        var model = TrayMenuBuilder.Build(_host!.Registry);

        if (model.Count == 0 && !AnyPanelsDeclared())
        {
            var none = new ToolStripMenuItem("（没有工具声明托盘菜单）") { Enabled = false };
            menu.Items.Add(none);
        }

        foreach (var group in model.Groups)
        {
            if (group.Title.Length == 0)
            {
                // 未声明 group 的条目直接挂顶层
                foreach (var item in group.Items)
                {
                    menu.Items.Add(CreateItem(item));
                }

                continue;
            }

            var sub = new ToolStripMenuItem(group.Title);
            foreach (var item in group.Items)
            {
                sub.DropDownItems.Add(CreateItem(item));
            }

            menu.Items.Add(sub);
        }

        // 面板子菜单（P4 Wave 2c，协议 §7.1）。
        // 放在菜单区**之后**、常驻服务区段之前 —— 面板是"看"的入口，与"做"的命令分开。
        AppendPanelSection(menu);

        menu.Items.Add(new ToolStripSeparator());
        AppendResidentSection(menu);
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("刷新菜单", null, async (_, _) => await RefreshAsync()));
        menu.Items.Add(new ToolStripMenuItem("打开配置目录", null, (_, _) => OpenPath(_host!.Paths.ConfigDir)));
        menu.Items.Add(new ToolStripMenuItem("查看日志", null, (_, _) => OpenPath(_host!.Paths.LogsDir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));
    }

    private ToolStripMenuItem CreateItem(TrayMenuItem item) =>
        new(item.Title, null, async (_, _) => await InvokeAsync(item));

    /// <summary>
    /// "常驻服务"区段（P2 决策 3 的 UI 半边）：每个 resident 工具一条状态项（不可点）
    /// + 一条「发送恢复」入口（绑 <c>PushRecoverAsync</c>）。没有 resident 工具时不渲染。
    /// </summary>
    private void AppendResidentSection(ContextMenuStrip menu)
    {
        var residents = _host!.Processes.GetResidentSnapshot();
        if (residents.Count == 0)
        {
            return;
        }

        var section = new ToolStripMenuItem("常驻服务");
        foreach (var r in residents)
        {
            var status = $"{r.ToolId} — {r.State}"
                         + (r.Pid is { } pid ? $"（pid {pid}）" : "")
                         + (r.CrashCount > 0 ? $" · 崩溃 {r.CrashCount} 次" : "");
            section.DropDownItems.Add(new ToolStripMenuItem(status) { Enabled = false });

            if (r.Running)
            {
                var toolId = r.ToolId;
                section.DropDownItems.Add(new ToolStripMenuItem(
                    $"发送恢复（recover）", null, async (_, _) => await RecoverAsync(toolId)));
            }
        }

        menu.Items.Add(section);
    }

    /// <summary>是否有任何工具声明了面板（用于"没有任何东西可显示"提示的判断）。</summary>
    private bool AnyPanelsDeclared() =>
        _host?.Registry.Tools.Any(t => t.Manifest.Contributes.Panels.Count > 0) == true;

    /// <summary>
    /// 「面板」区段（P4 Wave 2c，协议 §7.1）：每个声明了面板的工具一个子菜单，
    /// 里面每项对应一个面板。工具**只有面板没有托盘菜单**时也会出现在这里
    /// （否则面板无处可达 —— 这正是 §7.1 特别点出的情形）。
    ///
    /// 没有面板时**不渲染任何东西**（不留一个空的「面板」项）。
    /// </summary>
    private void AppendPanelSection(ContextMenuStrip menu)
    {
        var withPanels = _host!.Registry.Tools
            .Where(t => t.Manifest.Contributes.Panels.Count > 0)
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        if (withPanels.Count == 0)
        {
            return;
        }

        var section = new ToolStripMenuItem("面板");
        foreach (var tool in withPanels)
        {
            // 只有一个面板且工具只有一个：直接挂平（少一层点击，也不会有同名歧义）
            var panels = tool.Manifest.Contributes.Panels;
            if (panels.Count == 1)
            {
                var tool1 = tool;
                var panel1 = panels[0];
                section.DropDownItems.Add(new ToolStripMenuItem(
                    $"{tool.Manifest.Name} — {panel1.Title}", null, (_, _) => OpenPanel(tool1, panel1)));
                continue;
            }

            var toolSub = new ToolStripMenuItem(tool.Manifest.Name);
            foreach (var panel in panels)
            {
                var tool2 = tool;
                var panel2 = panel;
                toolSub.DropDownItems.Add(new ToolStripMenuItem(
                    panel.Title, null, (_, _) => OpenPanel(tool2, panel2)));
            }

            section.DropDownItems.Add(toolSub);
        }

        menu.Items.Add(section);
    }

    /// <summary>
    /// 打开面板窗口（协议 §5.2：同一面板已在 → 复用窗口并立即重拉，**不新开**）。
    ///
    /// 与 <see cref="OpenSettings"/> 同款互操作处理：托盘跑的是 WinForms 消息循环，
    /// WPF 窗口用 <c>Show</c>（非模态）—— 面板是"边看边操作托盘"的工具，
    /// 用 <c>ShowDialog</c> 会锁住整个托盘菜单，那对面板来说恰恰是错的。
    /// </summary>
    private void OpenPanel(RegisteredTool tool, ToolPanel panel)
    {
        var key = $"{tool.Id}.{panel.Id}";

        try
        {
            if (_panelWindows.TryGetValue(key, out var existing))
            {
                existing.Activate();    // 复用：激活到前台
                _host?.Log.Info($"面板 {key} 已打开，激活已有窗口", "panel");
                return;
            }

            var window = new PanelWindow(_host!, tool, panel);
            window.Closed += (_, _) => _panelWindows.Remove(key);
            _panelWindows[key] = window;

            window.Show();
            _host?.Log.Info($"打开面板 {key}（{panel.Width}x{panel.Height}）", "panel");
        }
        catch (Exception ex)
        {
            _host?.Log.Error($"打开面板 {key} 失败：{ex.Message}", "panel");
            _icon.ShowBalloonTip(4000, "打开面板失败", Shorten(ex.Message, 200), ToolTipIcon.Warning);
        }
    }

    /// <summary>托盘 recover 入口：向常驻实例发送 <c>tool.recover</c>，气泡回执。</summary>
    private async Task RecoverAsync(string toolId)
    {
        try
        {
            var ok = await _host!.Processes.PushRecoverAsync(toolId);
            _icon.ShowBalloonTip(3000, "已发送恢复",
                $"{toolId}：{(ok ? "tool.recover 已送达" : "进程未在运行（无需恢复）")}", ToolTipIcon.Info);
            _host.Log.Info($"托盘触发 recover：{toolId}（送达={ok}）", "tray");
        }
        catch (Exception ex)
        {
            _host?.Log.Error($"托盘触发 recover {toolId} 失败：{ex.Message}", "tray");
            _icon.ShowBalloonTip(4000, "恢复失败", Shorten(ex.Message, 200), ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// <c>host.notify</c> 的托盘出口：把工具请求转成气泡，并**如实回报是否显示了**。
    ///
    /// 为什么返回值有意义：<c>ShowBalloonTip</c> 在 Win11 上会因"勿扰 / 通知设置"被静默丢弃
    /// （她实测踩到），所以"调用了 API"≠"用户看到了"。宿主只能保证"已提交给系统"——
    /// 这一点在返回值口径上要说清，不能让工具以为一定弹出来了。
    /// </summary>
    private bool ShowToolNotification(ToolNotificationEvent e)
    {
        try
        {
            var title = Shorten(string.IsNullOrWhiteSpace(e.Title) ? e.ToolName : e.Title, 60);
            var body = Shorten(string.IsNullOrWhiteSpace(e.Body) ? "（无内容）" : e.Body, 240);

            // 直接弹，**不做 UI 线程 marshal** —— 这是实测结论，不是省事：
            //   · NotifyIcon 不是 Control（没有 Invoke），本来就不能用它 marshal；
            //   · 探针实测（_scratch/uiprobe）确认后台线程直接调 ShowBalloonTip 不抛异常；
            //   · 而 marshal 到 UI 线程会**死锁**：调用它的线程恰恰是正在同步等工具返回的
            //     UI 线程（RunProbeNotify / RunAutoClick / --selfcheck 都走 GetAwaiter().GetResult()），
            //     没人能替它跑泵 → 工具等宿主应答、宿主等 UI 线程 → 双双挂到 30s 超时。
            _icon.ShowBalloonTip(4000, title, body, ToolTipIcon.Info);

            _host?.Log.Info($"工具 {e.ToolId} 的通知已提交给系统通知中心", "notify");
            return true;
        }
        catch (Exception ex)
        {
            // 弹不出不该让工具调用失败，但必须让 delivered=false 说话
            _host?.Log.Warn($"工具 {e.ToolId} 的通知显示失败：{ex.Message}", "notify");
            return false;
        }
    }

    // ── 宿主事件（P4 Wave 2a，决策 D2） ──────────────────────────────────────

    /// <summary>
    /// 宿主内部事件的托盘反应。**在事件源的后台线程上被调用**（见 <c>HostEventBus</c> 的投递模型）。
    ///
    /// 三条处理原则：
    /// <list type="number">
    /// <item><b>不做 UI 线程 marshal</b>：与 <see cref="ShowToolNotification"/> 同款结论 ——
    ///   气泡与菜单重建都能从后台线程调；marshal 到 UI 线程会与"UI 线程正同步等工具返回"撞死锁。</item>
    /// <item><b>只处理"用户该知道"的事件</b>：全量事件都弹气泡 = 每次调用都打扰用户。
    ///   当前只有<b>熔断</b>值得主动告知（工具静默失效是用户会困惑的）；其余只落日志、清菜单缓存。</item>
    /// <item><b>异常一律吞掉</b>：UI 反应失败不该反噬事件源（进程管理正在做的事比弹气泡重要）。</item>
    /// </list>
    /// </summary>
    private void OnHostEvent(HostEvent evt)
    {
        try
        {
            switch (evt.Kind)
            {
                case HostEventKind.ToolQuarantined:
                    // 唯一值得主动打扰的：工具因反复崩溃被自动禁用，用户下次想用它时会困惑
                    _icon.ShowBalloonTip(5000, "工具已被停用",
                        $"{evt.ToolId}：{Shorten(evt.Detail ?? "反复崩溃，已自动停用", 200)}", ToolTipIcon.Warning);
                    break;

                case HostEventKind.ToolEnabledChanged:
                case HostEventKind.RegistryReloaded:
                    // 菜单在每次弹出时重建（Opening 事件），所以这里不需要主动重建 —— 只落日志
                    break;
            }

            _host?.Log.Debug($"宿主事件：{evt}", "events");
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"处理宿主事件失败（已忽略，事件 {evt.Kind}）：{ex.Message}", "events");
        }
    }

    // ── 动作 ────────────────────────────────────────────────────────────────

    private async Task InvokeAsync(TrayMenuItem item)
    {
        if (_busy)
        {
            _icon.ShowBalloonTip(2000, "Eztools", "上一个操作还没结束", ToolTipIcon.Info);
            return;
        }

        _busy = true;
        try
        {
            // 必须异步：工具的默认超时是 30s（lite 档），
            // 在 UI 线程同步等待会让托盘在这段时间里完全没响应（连菜单都打不开）。
            var args = item.BuildArgs(ReadClipboardSafely());
            var result = await _host!.Processes.InvokeCommandAsync(item.CommandId, args);

            var text = result.Result is null ? "（无返回值）" : JsonText.Write(result.Result, indented: false);
            // 结果内容也要进日志：气泡会被 Win11 的勿扰/通知设置静默丢弃（她实测踩到），
            // 日志是唯一不依赖系统设置的验证出口。
            _host.Log.Info(
                $"托盘调用 {item.CommandId} 完成，用时 {result.Elapsed.TotalMilliseconds:F0}ms，结果 {text}",
                "tray");
            _icon.ShowBalloonTip(4000, Shorten(item.Title + " 完成", 60), Shorten(text, 240), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _host?.Log.Error($"托盘调用 {item.CommandId} 失败：{ex.Message}", "tray");
            _icon.ShowBalloonTip(5000, Shorten(item.Title + " 失败", 60), Shorten(ex.Message, 240),
                ToolTipIcon.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 「刷新菜单」：**完整重载**（重扫清单 + 重读启停 + 重建进程层）。
    ///
    /// 只在外部改过东西后需要 —— 命令行里 enable/disable 了工具，或往 tools/ 放了新工具。
    /// 走 <c>ReloadAsync</c> 而不是 <c>LoadRegistry</c>：后者只换注册表引用，
    /// 进程层还在用旧的，于是"新扫出来的工具在菜单里可见、点下去却报找不到"。
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            await _host!.ReloadAsync();
            ReRegisterHotkeys();
            RebuildMenu();
            _icon.ShowBalloonTip(2500, "Eztools",
                $"菜单已刷新：{_host.Registry.Tools.Count} 个工具", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _host?.Log.Error($"刷新菜单失败：{ex.Message}", "tray");
            _icon.ShowBalloonTip(4000, "刷新失败", Shorten(ex.Message, 200), ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// 打开设置窗口。
    ///
    /// 互操作注意：托盘的消息循环是 WinForms 的，WPF 窗口用 <c>ShowDialog</c> 的模态泵跑 ——
    /// 模态循环会处理 WPF 的消息，这是两栈互操作最稳的路径（探针实测共存成立，见设计文档 §2.1 注）。
    /// </summary>
    private void OpenSettings()
    {
        var window = new SettingsWindow(_host!);
        window.ShowDialog();
    }

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);    // 目录可能还没被创建过
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host?.Log.Error($"打开 {path} 失败：{ex.Message}", "tray");
        }
    }

    private void ExitApplication()
    {
        _host?.Log.Info("用户选择退出，停止工具进程", "tray");
        _icon.Visible = false;
        _hotkeys?.Dispose();
        _hotkeyItems.Clear();

        WinForms.Application.Exit();
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 加载托盘图标。用嵌入资源而不是 exe 的关联图标：后者只给一个尺寸，高 DPI 下会被拉糊。
    /// 按 <c>SystemInformation.SmallIconSize</c> 取，系统会挑最接近的那一帧
    /// （100% → 16px，150% → 24px）。
    /// </summary>
    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayApplication).Assembly.GetManifestResourceStream("eztools.ico");
        if (stream is null)
        {
            // 资源丢了不该让程序起不来 —— 退回系统图标（能看见，只是容易跟别的程序撞脸）
            return SystemIcons.Application;
        }

        // 窗口/DPI 上下文还没建立时 SmallIconSize 可能给 0，此时不传尺寸（取第一帧）
        var size = SystemInformation.SmallIconSize;
        return size.Width > 0 && size.Height > 0
            ? new Icon(stream, size)
            : new Icon(stream);
    }

    /// <summary>读剪贴板。失败按空处理 —— 剪贴板被别的程序独占是常事，不该让托盘崩。</summary>
    private string ReadClipboardSafely()
    {
        try
        {
            return WinForms.Clipboard.ContainsText() ? WinForms.Clipboard.GetText() : string.Empty;
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"读剪贴板失败（按空处理）：{ex.Message}", "tray");
            return string.Empty;
        }
    }

    /// <summary>
    /// 首次运行提示"图标在溢出区"。实测 Win11 默认把新的托盘图标放进溢出区
    /// （<c>NotifyIconSettings</c> 里 <c>IsPromoted</c> 为空），用户会以为程序没起来。
    ///
    /// 只提示一次，而且**不去改用户的桌面设置** —— 那是用户的桌面，不该被程序动。
    /// </summary>
    private void ShowOverflowHintOnce()
    {
        var marker = Path.Combine(_host!.Paths.Root, ".tray-hint-shown");

        try
        {
            if (File.Exists(marker))
            {
                return;
            }

            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            // 写不了标记就每次都提示 —— 比"该提示时没提示"轻
            _host.Log.Warn($"写首次提示标记失败（下次仍会提示）：{ex.Message}", "tray");
        }

        _icon.ShowBalloonTip(6000, "Eztools 已启动",
            "图标在任务栏通知区域（可能在溢出区，点小三角可看到，可拖出固定）。右键图标打开菜单。",
            ToolTipIcon.Info);
    }

    /// <summary>
    /// 按注册表与用户覆盖仲裁并注册全局热键（§6.3）。
    ///
    /// 三种落败互不相同，提示也不同：
    ///   · 输给 Eztools 内部仲裁（保留先注册者）→ 气泡说明让给了谁
    ///   · RegisterHotKey 失败（被第三方程序占用）→ 气泡说明"被其他程序占用"
    ///   · 组合键本身解析失败 → 清单阶段已有 HotkeyInvalid 诊断，这里直接跳过
    /// </summary>
    private void RegisterHotkeysFromRegistry()
    {
        _hotkeyItems.Clear();
        var order = 0;
        var claims = new List<HotkeyClaim>();

        foreach (var (tool, hotkey, effective) in _host!.Registry.Hotkeys(_host.StateStore))
        {
            var combo = HotkeyCombo.TryParse(effective);
            if (combo is null)
            {
                _host.Log.Warn($"热键 '{effective}'（{tool.Id}:{hotkey.Command}）无法解析，已跳过", "hotkey");
                continue;
            }

            claims.Add(new HotkeyClaim(tool.Id, hotkey.Command, combo, order++));
        }

        var result = HotkeyArbitration.Arbitrate(claims);

        foreach (var (resourceId, winner) in result.Winners)
        {
            var id = _hotkeys!.Register(winner.Combo);
            if (id is null)
            {
                _host.Log.Warn(
                    $"热键 {winner.Combo.Normalized}（{winner.ToolId}:{winner.Command}）注册失败：被其他程序占用",
                    "hotkey");
                _icon.ShowBalloonTip(5000, "热键注册失败",
                    $"{winner.Combo.Normalized} 被其他程序占用，{winner.ToolId} 的热键未生效", ToolTipIcon.Warning);
                continue;
            }

            _hotkeyItems[id.Value] = new TrayMenuItem
            {
                ToolId = winner.ToolId,
                CommandId = winner.Command,
                Title = $"{winner.Combo.Normalized}（{winner.ToolId}）",
                GroupKey = "hotkey",
                Input = MenuInput.Clipboard, // 热键与托盘菜单同语义：剪贴板作为上下文注入
            };

            // 注册成功 = 占住这个独占资源
            if (!_host.Exclusive.TryClaim(resourceId, winner.ToolId))
            {
                _host.Log.Warn($"热键资源 {resourceId} 的运行时 claim 冲突（持有者 {_host.Exclusive.HolderOf(resourceId)}）", "hotkey");
            }

            _host.Log.Info($"热键已注册：{winner.Combo.Normalized} → {winner.ToolId}:{winner.Command}", "hotkey");
        }

        foreach (var (loser, heldBy) in result.Losers)
        {
            _host.Log.Warn(
                $"热键 {loser.Combo.Normalized}（{loser.ToolId}:{loser.Command}）与 {heldBy} 冲突，保留先注册者",
                "hotkey");
            _icon.ShowBalloonTip(5000, "热键冲突",
                $"{loser.Combo.Normalized} 已被 {heldBy} 占用，{loser.ToolId}:{loser.Command} 未生效",
                ToolTipIcon.Warning);
        }
    }

    /// <summary>刷新后重注册（工具启停/增删、用户改键都可能改变归属）。</summary>
    private void ReRegisterHotkeys()
    {
        _hotkeys!.UnregisterAll();
        _hotkeyItems.Clear();
        RegisterHotkeysFromRegistry();
    }

    private void ReportSelfCheck()
    {
        var model = TrayMenuBuilder.Build(_host!.Registry);
        var line = $"自检：工具 {_host.Registry.Tools.Count} 个 · 托盘项 {model.Count} 个 · " +
                   $"热键注册 {_hotkeys!.RegisteredCount} 个 · 图标 {_iconImage.Width}x{_iconImage.Height} · " +
                   $"菜单组 {model.Groups.Count}";

        // P1b：设置窗口的 schema → 控件映射清单（不弹窗，纯数据装配）
        var settings = new SettingsWindow(_host).InventoryForSelfCheck();

        _host.Log.Info(line, "selfcheck");
        foreach (var s in settings)
        {
            _host.Log.Info(s, "selfcheck");
        }

        // P4 Wave 2c：面板渲染清单（协议 §9 注 —— GUI 断言由 --selfcheck 覆盖，不开窗口）。
        // 走**真实的** tool.panel.data 通道，把返回载荷交给 PanelWindow 装配控件树，
        // 于是"协议通"与"WPF 真的画出来了"两件事都被验到。
        var panels = PanelInventoryForSelfCheck();
        foreach (var p in panels)
        {
            _host.Log.Info(p, "selfcheck");
        }

        WriteReport(line, settings.Concat(panels).ToList());
    }

    /// <summary>
    /// 面板渲染自检：对每个声明了面板的工具，真拉一次数据并装配控件树（不显示窗口）。
    /// 单工具失败只记一行 —— 自检不该因为一个工具坏掉而整体失败。
    /// </summary>
    private List<string> PanelInventoryForSelfCheck()
    {
        var lines = new List<string>();
        var withPanels = _host!.Registry.Tools
            .Where(t => t.Manifest.Contributes.Panels.Count > 0)
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        if (withPanels.Count == 0)
        {
            lines.Add("面板清单：没有任何工具声明 contributes.panels");
            return lines;
        }

        foreach (var tool in withPanels)
        {
            foreach (var panel in tool.Manifest.Contributes.Panels)
            {
                try
                {
                    var result = _host.Processes
                        .PanelDataAsync(tool.Id, panel.Id, "selfcheck")
                        .GetAwaiter().GetResult();

                    var window = new PanelWindow(_host, tool, panel);
                    lines.AddRange(window.InventoryForSelfCheck(result.Data));
                    window.Close();
                }
                catch (Exception ex)
                {
                    lines.Add($"面板 {tool.Id}.{panel.Id} 自检失败：{ex.Message}");
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// 真实驱动一次 <c>host.notify</c>，把宿主回报的 <c>delivered</c> 写进自检文件。
    ///
    /// 返回码：0 = 工具调用成功且宿主回报 <c>delivered:true</c>；1 = 调用成功但没送达；
    /// 2 = 调用本身失败（工具/命令不通）。**区分 1 与 2** 是有意义的：1 说明是"通知没弹出来"，
    /// 2 说明是"整条工具调用链坏了"，两者的排查方向完全不同。
    /// </summary>
    private int RunProbeNotify()
    {
        _host!.Log.Info("probe-notify 开始", "selfcheck");

        try
        {
            var result = _host.Processes
                .InvokeCommandAsync("probe.notify", new JsonObject())
                .GetAwaiter().GetResult();

            var delivered = result.Result?["delivered"]?.GetValue<bool>() ?? false;
            var reason = result.Result?["reason"]?.GetValue<string>();
            var line = $"通知探针：delivered={delivered}" + (reason is null ? "" : $" · {reason}");

            _host.Log.Info(line, "selfcheck");
            WriteReport(line, new[] { $"commandId=probe.notify", $"delivered={delivered}" });
            return delivered ? 0 : 1;
        }
        catch (Exception ex)
        {
            _host.Log.Error($"通知探针调用失败：{ex.Message}", "selfcheck");
            WriteReport($"通知探针调用失败：{ex.Message}", Array.Empty<string>());
            return 2;
        }
    }

    /// <summary>把自检结果落盘 + 回显 stdout（两条出口共用，避免两处漂移）。</summary>
    private void WriteReport(string line, IReadOnlyList<string> details)
    {
        if (_options.OutFile is { Length: > 0 } path)
        {
            try
            {
                File.WriteAllText(
                    path,
                    line + Environment.NewLine + string.Join(Environment.NewLine, details) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _host.Log.Error($"自检结果写 {path} 失败：{ex.Message}", "selfcheck");
            }
        }

        // 仍然往 stdout 写一份：自动化里被重定向时能拿到（编码随进程默认，故不作为契约）
        Console.WriteLine(line);
        foreach (var d in details)
        {
            Console.WriteLine(d);
        }

        Console.Out.Flush();
    }

    private int RunAutoClick(int index)
    {
        var model = TrayMenuBuilder.Build(_host!.Registry);
        if (model.Count == 0)
        {
            _host.Log.Error("自动触发失败：没有托盘项", "selfcheck");
            return 4;
        }

        var item = model.Items[Math.Clamp(index, 0, model.Count - 1)];
        return InvokeBlocking(item) ? 0 : 5;
    }

    /// <summary>
    /// 触发一次**已注册热键**对应的命令（自动化用）。
    ///
    /// 覆盖 <c>--click</c> 到不了的那条路：热键项在 <c>RegisterHotkeysFromRegistry</c>
    /// 里单独构造，其 <c>Input</c> 与托盘项不同源（托盘项来自 <c>menus[].input</c>，
    /// 热键项历史上硬编码 Clipboard）。托盘/热键两组断言合起来，"上下文注入"
    /// 这条契约才算两端都有人守。
    ///
    /// 退出码：<c>0</c> 成功；<c>5</c> 调用失败（与 <c>--click</c> 同码）；
    /// <c>6</c> 该命令没有已注册热键 —— **必须非零**，静默成功会让断言变成恒真。
    /// </summary>
    private int RunProbeHotkey(string commandId)
    {
        var item = _hotkeyItems.Values.FirstOrDefault(x => x.CommandId == commandId);
        if (item is null)
        {
            _host!.Log.Error(
                $"热键探针：命令 {commandId} 没有已注册的热键项（被仲裁淘汰，或根本未声明热键）", "selfcheck");
            return 6;
        }

        var ok = InvokeBlocking(item);
        _host!.Log.Info(
            $"热键探针 {commandId}（input={item.Input.ToWire()}）→ {(ok ? "成功" : "失败")}", "selfcheck");
        return ok ? 0 : 5;
    }

    /// <summary>
    /// 同步触发一次（自动化用）。**只在没有消息循环时可用** ——
    /// 一旦 <c>Application.Run</c> 跑起来，在 UI 线程同步等待异步调用就会死锁。
    /// </summary>
    private bool InvokeBlocking(TrayMenuItem item)
    {
        try
        {
            var args = item.BuildArgs(ReadClipboardSafely());
            var result = _host!.Processes.InvokeCommandAsync(item.CommandId, args).GetAwaiter().GetResult();

            _host.Log.Info(
                $"自动触发 {item.CommandId} 成功，用时 {result.Elapsed.TotalMilliseconds:F0}ms", "selfcheck");
            return true;
        }
        catch (Exception ex)
        {
            _host?.Log.Error($"自动触发 {item.CommandId} 失败：{ex.Message}", "selfcheck");
            return false;
        }
    }

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
