// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using System.Windows.Interop;
using Eztools.Contracts;
using Eztools.ClipboardLib;
using Eztools.Host;
using Eztools.Host.Arbitration;
using Eztools.Host.Config;
using Eztools.Host.Processes;
using Eztools.Host.Registry;
using Eztools.Host.Search;
using Eztools.Host.Tray;
using Eztools.Ocr;
using System.Windows.Media.Imaging;

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

    // ── 搜索窗（W3-d-1）：宿主级功能，索引进程与窗口都**懒创建** ──
    // 索引进程不进工具生命周期（它不是 Python 工具，没有清单）；
    // 但退出路径必须回收（Dispose 里），否则残留 ezt-index.exe 进程。
    private SearchIndexProcess? _searchIndex;
    private SearchIndexClient? _searchClient;
    private SearchWindow? _searchWindow;

    /// <summary>剪贴板历史面板（W5-b）与其存储。面板生命周期同搜索窗：懒创建 + Closed 自愈 + X=隐藏。</summary>
    private ClipboardHistoryPanel? _clipPanel;
    private Eztools.ClipboardLib.HistoryStore? _clipStore;

    // ── 剪贴板监听与设置（W5-c）：生效值缓存在字段里（selfcheck / 菜单标签要用），配置重读走 ReloadHostSettings()
    private ClipboardMonitor? _clipMonitor;
    private readonly PrivacyFilter _clipFilter = new();
    private bool _clipEnabled = true;
    private string _clipHotkey = HostSettingsSchema.DefaultClipHotkey;
    private int _clipMaxItems = HostSettingsSchema.DefaultClipMaxItems;
    private int _clipImageRetentionDays = HostSettingsSchema.DefaultClipImageRetentionDays;
    private int? _clipHotkeyId;
    private int _clipCaptured;             // 本次会话捕获条数（selfcheck 展示）
    private bool _clipOversizeWarned;      // >20MB 一次性气泡（R5：不刷屏）
    private int? _searchHotkeyId;

    // ── 屏幕取字（W4-c）：遮罩管理器与热键 id，同样懒创建 ──
    private OcrOverlayManager? _ocrManager;
    private int? _ocrHotkeyId;

    // 宿主设置的生效值（W4-c 接 P1a）：合成优先级 = 命令行显式 > config/desktop.json > 代码默认。
    // 缓存在字段里（菜单标签 / selfcheck 都要用），配置重读走 ReloadHostSettings()
    // —— 不在每次弹菜单时读文件：菜单是同步构建的，磁盘 IO 不该在那条路径上。
    private string _searchHotkey = HostSettingsSchema.DefaultSearchHotkey;
    private string _ocrHotkey = HostSettingsSchema.DefaultOcrHotkey;
    private string? _ocrLanguage;

    /// <summary>最近一次由托盘侧确认的暂停态（null = 未知）。**只由协议返回的实际状态更新** —— 不猜。</summary>
    private bool? _traySearchPaused;

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
            if (id == _searchHotkeyId)
            {
                // 搜索窗是宿主功能，不走工具调用路径
                ToggleSearchWindow();
                return;
            }

            if (id == _ocrHotkeyId)
            {
                // 屏幕取字同为宿主功能（W4-c）
                StartOcrCapture();
                return;
            }

            if (id == _clipHotkeyId)
            {
                // 剪贴板历史面板（W5-c）：宿主功能，不走工具调用路径
                ToggleClipPanel();
                return;
            }

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

        // W4-c：宿主设置的生效值在 ConfigStore 可用之后、热键注册之前合成一次
        //（合成优先级 = 命令行显式 > config/desktop.json > 代码默认）。
        ReloadHostSettings();

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

        // 搜索窗热键（W3-d-1）：在工具热键**之后**注册 —— 工具清单声明优先，宿主给工具让路。
        RegisterSearchHotkey();

        // 屏幕取字热键（W4-c）：同为宿主级，排在搜索之后 —— 谁先注册谁赢，顺序即优先级。
        RegisterOcrHotkey();

        // 剪贴板热键（W5-c）：同为宿主级，排在最后。监听**不在此启动** ——
        // selfcheck/探针模式都会流经这里，而监听是个隐式副作用（复制会被捕获入库），
        // 只该在真托盘 / selfcheck 存在；启动点见 SelfCheck 分支与 _icon.Visible 之后。
        RegisterClipHotkey();

        if (_options.SelfCheck)
        {
            _host.Log.Info("self-check 开始（宿主初始化完成）", "selfcheck");
            // selfcheck 明确要验证"监听可启动 + 运行态"⇒ 显式启动（探针模式不走这里，无副作用）
            ApplyClipMonitorState();
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

        // 搜索通路的活体验证（W3-d-1）：懒启动 ezt-index → stdio → search.query 全链路。
        // GUI 不参与 —— 这里验的是传输与协议层（窗口行为在自动化之外，与真按键同口径）。
        if (_options.ProbeSearch)
        {
            var code = RunProbeSearch();
            Dispose();
            return code;
        }

        // 搜索窗渲染面验证（W3-d-2/3）：假传输 → 200 条 → Measure/Arrange → 结构化快照。
        // 与 --probe-search 互补：那个验证"能拿到数据"，这个验证"拿到数据后列表怎么表现"。
        if (_options.ProbeSearchUi)
        {
            var code = RunProbeSearchUi();
            Dispose();
            return code;
        }

        // 搜索窗唤出生命周期验证（C2/G1 手工三 bug 自动化面）：Toggle 循环 + 焦点采样
        // （+ --probe-search-live 真链路 lei/LEI）。注意 Phase A 会短暂抢真实焦点（与真热键同代价）。
        if (_options.ProbeSearchSummon)
        {
            var code = RunProbeSearchSummon();
            Dispose();
            return code;
        }

        if (_options.ProbeOcrOverlay)
        {
            var code = RunProbeOcrOverlay();
            Dispose();
            return code;
        }

        if (_options.OcrShow)
        {
            var code = RunOcrShow();
            Dispose();
            return code;
        }

        if (_options.ProbeHostSettings)
        {
            var code = RunProbeHostSettings();
            Dispose();
            return code;
        }

        if (_options.ProbeOcrHotkey)
        {
            var code = RunProbeOcrHotkey();
            Dispose();
            return code;
        }

        // 剪贴板面板探针（W5-b）：生命周期循环 + 真键 Enter 直贴契约（注入被抑制）。
        if (_options.ProbeClipPanel)
        {
            var code = RunProbeClipPanel();
            Dispose();
            return code;
        }

        // 剪贴板监听探针（W5-c）：真 listener → 真事件 → 真入库（改写一次系统剪贴板，不抢焦点）。
        if (_options.ProbeClipMonitor)
        {
            var code = RunProbeClipMonitor();
            Dispose();
            return code;
        }

        // 剪贴板热键真按键探针（W5-c，手工清单 0.1 的自动化面）。
        if (_options.ProbeClipHotkey)
        {
            var code = RunProbeClipHotkey();
            Dispose();
            return code;
        }

        // WinForms 打字判决探针（2026-09-27 换方案判决）。
        if (_options.ProbeWinFormsTyping)
        {
            var code = RunProbeWinFormsTyping();
            Dispose();
            return code;
        }

        _icon.Visible = true;
        ShowOverflowHintOnce();

        // 剪贴板监听（W5-c）只在**真托盘**启动：探针/自检之外的每一条早退路径都不经过这里
        //（监听是隐式副作用——宿主存续期的剪贴板变化会被捕获入库，不该发生在验收探针里）。
        ApplyClipMonitorState();

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

        // 搜索窗先关（Closed 会释放会话），然后停索引进程 —— 顺序与面板窗口同理：
        // 必须在退订总线与 Dispose 宿主之前，避免垂死窗口再碰宿主资源。
        // ★ 必须 RealClose：普通 Close() 会被"X=隐藏"拦截吞掉，退出时窗口关不掉。
        try
        {
            _searchWindow?.RealClose();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"关闭搜索窗失败（忽略）：{ex.Message}", "shutdown");
        }

        _searchWindow = null;

        // 剪贴板面板（W5-b）：同搜索窗收窗纪律（RealClose 绕过 X=隐藏拦截）
        try
        {
            _clipPanel?.RealClose();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"关闭剪贴板面板失败（忽略）：{ex.Message}", "shutdown");
        }

        _clipPanel = null;
        _clipStore?.Dispose();
        _clipStore = null;

        // 剪贴板监听（W5-c）：先摘监听再销毁 sink（Dispose 内部已按 IsRunning 判定）
        try
        {
            _clipMonitor?.Dispose();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"停止剪贴板监听失败（忽略）：{ex.Message}", "shutdown");
        }

        _clipMonitor = null;

        // OCR 遮罩（W4-c）：退出路径同样要收 —— 活着的遮罩是全屏置顶窗，留着就是"屏幕坏了"。
        try
        {
            _ocrManager?.CloseAll();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"关闭 OCR 遮罩失败（忽略）：{ex.Message}", "shutdown");
        }

        _ocrManager = null;

        try
        {
            _searchIndex?.Dispose();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"停止索引进程失败（忽略）：{ex.Message}", "shutdown");
        }

        _searchIndex = null;
        _searchClient = null;

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

    // ── 搜索窗（W3-d-1）────────────────────────────────────────────────────

    /// <summary>
    /// 注册搜索热键。失败**必须显式可见**（W3-d-1 验收③：被第三方占用 = 明确提示文案，
    /// 不静默 —— 热键静默失效正是 W3-d-1 风险表的第一条）。托盘菜单的「搜索文件」是兜底入口。
    /// W4-c 起失败同时弹气泡（与 OCR 热键同款：注册失败用户看不见日志，气泡是唯一主动出口）。
    /// </summary>
    private void RegisterSearchHotkey()
    {
        _searchHotkeyId = null;
        var combo = HotkeyCombo.TryParse(_searchHotkey);
        if (combo is null)
        {
            _host!.Log.Warn(
                $"搜索热键 '{_searchHotkey}' 无法解析（格式如 Ctrl+Alt+S）。"
                + "搜索窗仍可从托盘菜单打开", "hotkey");
            return;
        }

        _searchHotkeyId = _hotkeys!.Register(combo);
        if (_searchHotkeyId is null)
        {
            _host!.Log.Warn(
                $"搜索热键 {combo.Normalized} 注册失败——组合键可能已被其它程序占用"
                + "（Win32 RegisterHotKey 拿不到，占用方名字系统不提供）。"
                + "可改用托盘菜单「搜索文件」，或换一个组合键（--search-hotkey / 设置窗口）", "hotkey");
            _icon.ShowBalloonTip(5000, "搜索热键注册失败",
                $"{combo.Normalized} 被其他程序占用，搜索热键未生效——可用托盘菜单「搜索文件」，或在设置里换键",
                ToolTipIcon.Warning);
            return;
        }

        _host!.Log.Info($"搜索热键已注册：{combo.Normalized} → 搜索窗", "hotkey");
    }

    /// <summary>
    /// 注册屏幕取字热键（W4-c，FR-1 的入口半边）。失败 = Warn 日志 + **气泡**（设计方案
    /// "仲裁失败→气泡"的落点：日志用户不看，气泡是注册失败唯一的主动出口），
    /// 托盘菜单「屏幕取字…」是兜底入口。可重入（重注册路径先置空旧 id）。
    /// </summary>
    private void RegisterOcrHotkey()
    {
        _ocrHotkeyId = null;
        var combo = HotkeyCombo.TryParse(_ocrHotkey);
        if (combo is null)
        {
            _host!.Log.Warn(
                $"OCR 热键 '{_ocrHotkey}' 无法解析（格式如 Ctrl+Alt+O）。"
                + "屏幕取字仍可从托盘菜单打开", "hotkey");
            return;
        }

        _ocrHotkeyId = _hotkeys!.Register(combo);
        if (_ocrHotkeyId is null)
        {
            _host!.Log.Warn(
                $"OCR 热键 {combo.Normalized} 注册失败——组合键可能已被其它程序占用"
                + "（Win32 RegisterHotKey 拿不到，占用方名字系统不提供）。"
                + "可改用托盘菜单「屏幕取字」，或换一个组合键（--ocr-hotkey / 设置窗口）", "hotkey");
            _icon.ShowBalloonTip(5000, "屏幕取字热键注册失败",
                $"{combo.Normalized} 被其他程序占用，屏幕取字热键未生效——可用托盘菜单「屏幕取字…」，或在设置里换键",
                ToolTipIcon.Warning);
            return;
        }

        _host!.Log.Info($"OCR 热键已注册：{combo.Normalized} → 屏幕取字", "hotkey");
    }

    /// <summary>
    /// 注册剪贴板历史热键（W5-c）。失败 = Warn 日志 + 气泡（同款"仲裁失败→气泡"纪律），
    /// 托盘子菜单「剪贴板历史」是兜底入口。可重入。
    /// </summary>
    private void RegisterClipHotkey()
    {
        _clipHotkeyId = null;
        if (!_clipEnabled)
        {
            _host!.Log.Info("剪贴板热键未注册（clip.enabled=false）", "hotkey");
            return;
        }

        var combo = HotkeyCombo.TryParse(_clipHotkey);
        if (combo is null)
        {
            _host!.Log.Warn(
                $"剪贴板热键 '{_clipHotkey}' 无法解析（格式如 Ctrl+Alt+V）。"
                + "面板仍可从托盘子菜单打开", "hotkey");
            return;
        }

        _clipHotkeyId = _hotkeys!.Register(combo);
        if (_clipHotkeyId is null)
        {
            _host!.Log.Warn(
                $"剪贴板热键 {combo.Normalized} 注册失败——组合键可能已被其它程序占用。"
                + "可改用托盘子菜单「剪贴板历史」，或在设置里换键", "hotkey");
            _icon.ShowBalloonTip(5000, "剪贴板热键注册失败",
                $"{combo.Normalized} 被其他程序占用，剪贴板热键未生效——可用托盘子菜单「剪贴板历史」，或在设置里换键",
                ToolTipIcon.Warning);
            return;
        }

        _host!.Log.Info($"剪贴板热键已注册：{combo.Normalized} → 剪贴板历史面板", "hotkey");
    }

    // ── 宿主设置（W4-c，FR-9：热键与默认语言接 P1a 配置中心）────────────────

    /// <summary>
    /// 重读宿主设置并合成生效值。优先级：命令行显式开关 &gt; config/desktop.json &gt; 代码默认
    /// —— 命令行是一次性显式覆盖（自动化/临时换键），落盘配置才是设置窗口写的长期值。
    /// 触发点：宿主启动后、<see cref="ReRegisterHotkeys"/>、设置窗口保存回调。
    /// ConfigStore.Load 对损坏文件自动备份回落（P1a 硬要求），这里不需要额外兜底。
    /// </summary>
    private void ReloadHostSettings()
    {
        _searchHotkey = _options.SearchHotkey
            ?? HostSettingsSchema.TryGetString(_host!.Configs, HostSettingsSchema.KeySearchHotkey)
            ?? HostSettingsSchema.DefaultSearchHotkey;
        _ocrHotkey = _options.OcrHotkey
            ?? HostSettingsSchema.TryGetString(_host!.Configs, HostSettingsSchema.KeyOcrHotkey)
            ?? HostSettingsSchema.DefaultOcrHotkey;

        var lang = HostSettingsSchema.TryGetString(_host!.Configs, HostSettingsSchema.KeyOcrLanguage);
        _ocrLanguage = string.IsNullOrWhiteSpace(lang) ? null : lang.Trim();

        // W5-c：剪贴板设置。enabled=false = 不监听不注册热键（已有历史保留，不删除）。
        _clipEnabled = HostSettingsSchema.TryGetBool(_host!.Configs, HostSettingsSchema.KeyClipEnabled) ?? true;
        _clipHotkey = HostSettingsSchema.TryGetString(_host!.Configs, HostSettingsSchema.KeyClipHotkey)
            ?? HostSettingsSchema.DefaultClipHotkey;
        _clipMaxItems = Math.Clamp(
            HostSettingsSchema.TryGetInt(_host!.Configs, HostSettingsSchema.KeyClipMaxItems)
                ?? HostSettingsSchema.DefaultClipMaxItems,
            100, 50_000);
        _clipImageRetentionDays = Math.Clamp(
            HostSettingsSchema.TryGetInt(_host!.Configs, HostSettingsSchema.KeyClipImageRetentionDays)
                ?? HostSettingsSchema.DefaultClipImageRetentionDays,
            1, 3650);

        // 黑名单（进程名分号/逗号分隔；PrivacyFilter 内部做 .exe 归一化）
        var blacklist = HostSettingsSchema.TryGetString(_host!.Configs, HostSettingsSchema.KeyClipBlacklist) ?? "";
        _clipFilter.SetBlacklist(blacklist.Split(
            new[] { ';', '；', ',', '，' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>按 clip.enabled 的生效值启动/停止常驻监听（启动、刷新、改设置后都会走到）。</summary>
    private void ApplyClipMonitorState()
    {
        if (_clipEnabled && _clipMonitor is null)
        {
            var monitor = new ClipboardMonitor();
            monitor.ClipboardChanged += OnClipboardUpdate;
            if (monitor.Start())
            {
                _clipMonitor = monitor;
                _host?.Log.Info($"剪贴板监听已启动（消息驱动 · 上限 {_clipMaxItems} 条 · 黑名单 {_clipFilter.BlacklistSnapshot().Count} 个）", "clip");
            }
            else
            {
                monitor.Dispose();
                _host?.Log.Warn("剪贴板监听启动失败（AddClipboardFormatListener 被拒）—— 捕获不可用，禁静默", "clip");
                _icon.ShowBalloonTip(5000, "剪贴板监听失败",
                    "无法监听系统剪贴板（详见日志）。历史面板可打开，但不会有新内容进入", ToolTipIcon.Warning);
            }
        }
        else if (!_clipEnabled && _clipMonitor is not null)
        {
            _clipMonitor.Dispose();
            _clipMonitor = null;
            _host?.Log.Info("剪贴板监听已停止（clip.enabled=false）", "clip");
        }
    }

    /// <summary>
    /// 监听回调（托盘主线程）：隐私过滤 → 读剪贴板 → 按类型组装条目 → 入库。
    /// 任何异常只记日志 —— 捕获是旁路功能，绝不能拖垮托盘。
    /// </summary>
    private void OnClipboardUpdate()
    {
        try
        {
            if (_clipFilter.Paused)
            {
                return;   // 手动暂停：静默丢弃（开关时刻气泡已告知状态，这里不刷屏）
            }

            var (snapshot, error) = ClipboardReader.ReadCurrent();
            if (error is not null)
            {
                _host?.Log.Warn($"剪贴板读取失败（跳过本次捕获）：{error}", "clip");
                return;
            }

            if (snapshot.OwnerProcess is { } src
                && _clipFilter.Evaluate(src) == PrivacyDecision.Blocked)
            {
                _host?.Log.Info($"剪贴板黑名单命中，未保存（{src}）", "clip");
                return;
            }

            ClipEntry? draft = snapshot.Image is { } image
                ? CaptureImageEntry(image, snapshot.OwnerProcess)
                : CaptureService.FromSnapshot(snapshot);
            if (draft is null)
            {
                return;   // 空内容/大图拒收——常态，不提示
            }

            EnsureClipStore().Upsert(draft, _clipMaxItems);
            _clipCaptured++;
            _clipPanel?.OnExternalChange();
            _host?.Log.Info($"剪贴板已捕获：{draft.Kind}（来源 {snapshot.OwnerProcess ?? "?"}，会话累计 {_clipCaptured}）", "clip");
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"剪贴板捕获失败（忽略）：{ex.Message}", "clip");
        }
    }

    /// <summary>
    /// 图片条目：编码 PNG 落盘（&gt;20MB 拒收 + 一次性气泡，设计 §8 R5）→ 组装条目 →
    /// 顺带执行图片保留期清理（查询便宜；pinned 豁免在 store 内）。
    /// </summary>
    private ClipEntry? CaptureImageEntry(BitmapSource image, string? sourceApp)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var png = ms.ToArray();

        if (png.Length > 20 * 1024 * 1024)
        {
            if (!_clipOversizeWarned)
            {
                _clipOversizeWarned = true;
                _icon.ShowBalloonTip(5000, "剪贴板历史",
                    "有图片超过 20 MB，未保存到历史（阈值防止单图拖垮库，见设计 §8 R5）", ToolTipIcon.Warning);
            }

            return null;
        }

        var store = EnsureClipStore();
        var imagesDir = store.ImagesDirectory;
        if (imagesDir is null)
        {
            return null;
        }

        var hash = CaptureService.ComputeHash(png);
        var fileName = $"{hash[..16].ToLowerInvariant()}.png";
        File.WriteAllBytes(Path.Combine(imagesDir, fileName), png);

        _ = store.PurgeExpiredImages(_clipImageRetentionDays);
        return CaptureService.FromImagePng(png, image.PixelWidth, image.PixelHeight, fileName, sourceApp);
    }

    /// <summary>托盘菜单入口：暂停/恢复捕获（运行态，不落盘）。状态变化气泡告知。</summary>
    private void ToggleClipCapturePaused()
    {
        _clipFilter.Paused = !_clipFilter.Paused;
        _icon.ShowBalloonTip(2500, "剪贴板历史",
            _clipFilter.Paused ? "捕获已暂停（隐私模式，期间复制不入库）" : "捕获已恢复",
            ToolTipIcon.Info);
        _host?.Log.Info($"剪贴板捕获暂停态切换为 {_clipFilter.Paused}", "clip");
    }

    /// <summary>托盘菜单入口：清空历史（置顶豁免）。删除动作必须可感知——气泡报数字，禁静默。</summary>
    private void ClearClipHistoryFromTray()
    {
        try
        {
            var deleted = EnsureClipStore().Clear(keepPinned: true);
            _icon.ShowBalloonTip(2500, "剪贴板历史",
                deleted > 0 ? $"已清空 {deleted} 条（置顶条目保留）" : "历史本来就是空的", ToolTipIcon.Info);
            _host?.Log.Info($"托盘清空历史：删除 {deleted} 条", "clip");
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"托盘清空历史失败：{ex.Message}", "clip");
            _icon.ShowBalloonTip(4000, "清空失败", Shorten(ex.Message, 200), ToolTipIcon.Warning);
        }
    }

    /// <summary>托盘菜单 / 全局热键共用的屏幕取字唤出入口（W4-c）。</summary>
    private void StartOcrCapture() => StartOcrCaptureCore(_ocrLanguage);

    /// <summary>
    /// 屏幕取字唤出：语言校验 → 逐屏遮罩 → 气泡反馈。异常一律收敛为"日志 + 气泡"，
    /// 不能拖垮托盘（R1 语言包缺失是最常见的失败，文案里带安装引导 —— 禁静默，S 系红线）。
    /// </summary>
    private void StartOcrCaptureCore(string? language)
    {
        try
        {
            // 语言校验：配置了语言但本机语言包没有 → **显式失败**，不静默回落（R1 同族红线）。
            // 静默回落会让"配置生效了"变成谎言：用户以为在按配置识别，实际引擎换了语言。
            if (!string.IsNullOrWhiteSpace(language)
                && !OcrLanguages.AvailableTags().Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"配置的 OCR 语言 '{language}' 本机未安装。可用语言："
                    + (OcrLanguages.AvailableTags().Count > 0
                        ? string.Join("、", OcrLanguages.AvailableTags())
                        : "（无——语言包整体缺失，见日志引导）")
                    + "。请在设置里修改「OCR 识别语言」，或按日志安装语言包");
            }

            // 配置语言变了 ⇒ 引擎语言过期 ⇒ 丢弃整个 manager（遮罩不存活时可安全重建；
            // 引擎语言在创建时固定，没有"换语言"的 API）。
            if (_ocrManager is { } existing
                && !existing.AnyAlive
                && existing.CurrentLanguageTag is { } currentLang
                && !string.IsNullOrWhiteSpace(language)
                && !string.Equals(currentLang, language, StringComparison.OrdinalIgnoreCase))
            {
                _host?.Log.Info($"OCR 语言从 {currentLang} 切换为 {language}，重建遮罩管理器", "ocr");
                _ocrManager = null;
            }

            _ocrManager ??= CreateOcrManager();
            var monitors = _ocrManager.ShowAll(language);
            _host?.Log.Info(
                $"OCR 遮罩已唤出：{monitors} 扇（语言 {_ocrManager.Engine.LanguageTag}）", "ocr");
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"屏幕取字启动失败：{ex.Message}", "ocr");
            _icon.ShowBalloonTip(6000, "屏幕取字无法启动", Shorten(ex.Message, 240), ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// 遮罩管理器懒创建 + 反馈接线（只挂一次 —— manager 重建时随新实例重挂，不泄漏）。
    /// 气泡文案同时承载 FR-3（"已复制 N 字符"）与已知局限标注（§2.3：多栏排版可能乱序，
    /// 行数 &gt; 1 时提示 —— 单行文本没有乱序问题，不提示以免噪音）。
    /// </summary>
    private OcrOverlayManager CreateOcrManager()
    {
        var manager = new OcrOverlayManager(msg => _host?.Log.Warn(msg, "ocr"));
        manager.TextCopied += text =>
        {
            var chars = text.Length;
            var lines = text.Count(c => c == '\n') + 1;
            var body = lines > 1
                ? $"已复制 {chars} 字符（{lines} 行）——多栏排版可能乱序（已知局限）"
                : $"已复制 {chars} 字符";
            _icon.ShowBalloonTip(3000, "屏幕取字", body, ToolTipIcon.Info);
            _host?.Log.Info($"OCR 复制完成：{chars} 字符 / {lines} 行", "ocr");
        };
        return manager;
    }

    /// <summary>窗口与索引进程都懒创建（首次唤出才有开销）。</summary>
    private void EnsureSearchWindow()
    {
        if (_searchWindow is not null)
        {
            return;
        }

        _searchWindow = new SearchWindow(AcquireSearchClient());

        // ★ 自愈（2026-09-25 实测教训）：任何路径真关闭（探针/未来重构）后，
        //   持着已 Closed 的窗口引用只会让热键每次都抛"关闭窗口后无法 Show"被吞掉。
        //   置空引用 ⇒ 下次热键走懒创建重建窗口，热键永不因一次关闭而全灭。
        _searchWindow.Closed += (_, _) => _searchWindow = null;
    }

    /// <summary>
    /// 搜索客户端（惰性）。**与搜索窗共用同一个 ezt-index 进程** —— 托盘菜单的暂停/恢复
    /// 不该为此再拉一个索引进程（缺口① 落地时的取舍：宁可让进程提前一点起，也不重复起）。
    /// </summary>
    private SearchIndexClient AcquireSearchClient()
    {
        if (_searchClient is not null)
        {
            return _searchClient;
        }

        // 索引进程的 data root = 安装根（core.json 与 .ezidx 都在那里 —— 与 CLI 形态同一份索引）。
        _searchIndex ??= new SearchIndexProcess(_host!.Paths, _host.Paths.Root);
        _searchClient = new SearchIndexClient(_searchIndex);
        return _searchClient;
    }

    /// <summary>热键/菜单入口。异常只记日志 —— UI 入口不能拖垮托盘。</summary>
    private void ToggleSearchWindow()
    {
        try
        {
            EnsureSearchWindow();
            _searchWindow!.Toggle();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"打开搜索窗失败：{ex.Message}", "search");
        }
    }

    /// <summary>
    /// 剪贴板历史库（W5-b）：惰性创建，随托盘进程存活。
    /// 库位置 = <c>&lt;config-root&gt;/data/clip/</c>（与 `ezt clip` CLI 同一份 ——
    /// CLI 写入 / 面板读取靠 SQLite WAL 并存，设计 §3.1）。
    /// </summary>
    private Eztools.ClipboardLib.HistoryStore EnsureClipStore()
    {
        if (_clipStore is not null)
        {
            return _clipStore;
        }

        var clipRoot = Path.Combine(_host!.Paths.ConfigRoot, "data", "clip");
        _clipStore = new Eztools.ClipboardLib.HistoryStore(
            Path.Combine(clipRoot, "clips.db"),
            Path.Combine(clipRoot, "images"));
        return _clipStore;
    }

    private void EnsureClipPanel()
    {
        if (_clipPanel is not null)
        {
            return;
        }

        _clipPanel = new ClipboardHistoryPanel(
            EnsureClipStore(),
            msg => _icon.ShowBalloonTip(5000, "剪贴板历史", Shorten(msg, 240), ToolTipIcon.Info));

        // 同款自愈：真关闭后置空引用，下次热键懒创建重建（§2.25①）
        _clipPanel.Closed += (_, _) => _clipPanel = null;
    }

    /// <summary>热键/托盘入口（W5-c 接热键；当前探针与手工清单用）。异常只记日志。</summary>
    private void ToggleClipPanel()
    {
        try
        {
            EnsureClipPanel();
            _clipPanel!.Toggle();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn($"打开剪贴板面板失败：{ex.Message}", "clip");
        }
    }

    /// <summary>
    /// 搜索通路真进程探针（--probe-search，W3-d-1 验收面）：
    /// 懒启动 ezt-index → stdio JSON-RPC → search.query，把结构化结局写进 --out 文件。
    /// 无提权 Core 的环境里 query 必回 **-32001**（索引未就绪）—— 这正是可自动化断言的
    /// 确定性输出（证明"进程起来了、协议通了、错误分层了"）；命中路径随 7.1c 同口径
    /// 属提权依赖分支。WPF 窗口不参与（窗口行为与真按键一样在自动化之外）。
    /// </summary>
    private int RunProbeSearch()
    {
        var outJson = new JsonObject();

        try
        {
            using var index = new SearchIndexProcess(_host!.Paths, _host.Paths.Root);
            var client = new SearchIndexClient(index);

            var pong = index.PingAsync().GetAwaiter().GetResult();
            outJson["ping"] = new JsonObject
            {
                ["ok"] = pong?["ok"]?.GetValue<bool>() == true,
                ["version"] = pong?["version"]?.GetValue<string>() ?? "?",
                ["pid"] = pong?["pid"]?.GetValue<int>() ?? 0,
            };

            // ── 等自举落地（有界，W3-c 有界化后新增）──────────────────────────────
            // 宿主托管的 ezt-index 是**后台自举**：ping 立刻能回，索引还在建。原来 ping 一回来
            // 就 query ⇒ 必然 -32001（实测整探针 478 ms，连"自举完成"都没走到）。
            // 这里先查一次 status（**无论等不等**都落盘，让 -32001 自带解释），
            // 再按 --wait-ready 轮询到 ready 为止。默认 0 = 行为与从前一致（零提权冒烟不变慢）。
            var statusCalls = 0;
            var readySw = System.Diagnostics.Stopwatch.StartNew();
            var ready = false;
            while (true)
            {
                try
                {
                    var st = client.StatusAsync().GetAwaiter().GetResult();
                    statusCalls++;
                    ready = st.Ready;
                    outJson["status"] = new JsonObject
                    {
                        ["ready"] = st.Ready,
                        ["totalFiles"] = st.TotalFiles,
                        ["volumes"] = new JsonArray(st.Volumes.Select(v => (JsonNode)v).ToArray()),
                        ["skipped"] = st.Skipped.Count,
                        ["failed"] = st.Failed.Count,
                        ["detectedVolumes"] = st.DetectedVolumes,
                        ["paused"] = st.Paused,
                    };
                    if (st.Ready)
                    {
                        break;
                    }
                }
                catch (SearchIndexException ex)
                {
                    // 自举中 status 也可能失败（端点未就绪）—— 结构化记下来继续等，不当失败
                    outJson["status"] = new JsonObject { ["error"] = ex.Code, ["message"] = ex.Message };
                }

                if (readySw.ElapsedMilliseconds >= _options.WaitReadyMs)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            outJson["readyWaitMs"] = (int)readySw.ElapsedMilliseconds;
            outJson["statusCalls"] = statusCalls;

            try
            {
                var response = client.QueryAsync("ezt", substr: true, 10).GetAwaiter().GetResult();
                outJson["query"] = new JsonObject
                {
                    ["ok"] = true,
                    ["total"] = response.Total,
                    ["hits"] = response.Hits.Count,
                    ["elapsedMs"] = response.ElapsedMs,
                };
            }
            catch (SearchIndexException ex)
            {
                // 结构化错误原样落盘（-32001 not-ready 是验收期望值；其它码也是有效证据）
                outJson["query"] = new JsonObject
                {
                    ["ok"] = false,
                    ["code"] = ex.Code,
                    ["message"] = ex.Message,
                };
            }

            // ── G2 方案 A：传输段采样（2026-09-25）──────────────────────────────
            // `query` 保留它的原义 = **第一枪（冷态单次）**；`latency` 才是可比较的分布。
            // 为什么要预热：首查要 page-in `.ezidx`（208 万条），冷态与稳态差一个量级 ——
            // 拿冷态单样本去对 "P95 ≤ N ms" 的闸，第一天就红且红得没意义。
            // 两种模式分开测：前缀可走首字符位图预筛，子串必须全名 IndexOf（无提前退出），
            // 二者成本本就不同（selftest 100 万条实测 16.0 vs 21.9 ms），合并成一个数只会掩盖差异。
            var latency = new JsonObject();
            try
            {
                for (var i = 0; i < _options.ProbeWarmup; i++)
                {
                    _ = client.QueryAsync("ezt", substr: true, 10).GetAwaiter().GetResult();
                }

                latency["ok"] = true;
                latency["warmup"] = _options.ProbeWarmup;
                latency["repeat"] = _options.ProbeRepeat;
                latency["prefix"] = Measure("ezt", substr: false, _options.ProbeRepeat);
                latency["substr"] = Measure("ezt", substr: true, _options.ProbeRepeat);
            }
            catch (SearchIndexException ex)
            {
                latency["ok"] = false;
                latency["code"] = ex.Code;
                latency["message"] = ex.Message;
            }

            outJson["latency"] = latency;

            WriteOutFile(outJson.ToJsonString());

            // 同一查询连打 N 次，落分布（最近邻分位：排序后按 ceil(p*n)-1 取，N 小也成立）。
            JsonObject Measure(string q, bool substr, int repeat)
            {
                var ms = new List<int>(repeat);
                long total = 0;
                for (var i = 0; i < repeat; i++)
                {
                    var r = client.QueryAsync(q, substr, 10).GetAwaiter().GetResult();
                    ms.Add(r.ElapsedMs);
                    total = r.Total;
                }

                ms.Sort();
                int Pct(double p) => ms.Count == 0
                    ? 0
                    : ms[Math.Clamp((int)Math.Ceiling(p * ms.Count) - 1, 0, ms.Count - 1)];

                return new JsonObject
                {
                    ["ok"] = true,
                    ["total"] = total,
                    ["samples"] = ms.Count,
                    ["min"] = ms.Count == 0 ? 0 : ms[0],
                    ["p50"] = Pct(0.50),
                    ["p95"] = Pct(0.95),
                    ["max"] = ms.Count == 0 ? 0 : ms[^1],
                };
            }

            _host!.Log.Info(
                $"搜索通路探针完成：ping ok={outJson["ping"]!["ok"]} · "
                + $"query ok={outJson["query"]!["ok"]}", "search");
            return 0;
        }
        catch (Exception ex)
        {
            outJson["ok"] = false;
            outJson["error"] = ex.Message;
            WriteOutFile(outJson.ToJsonString());
            _host?.Log.Warn($"搜索通路探针失败：{ex}", "search");
            return 2;
        }
    }

    private void WriteOutFile(string content)
    {
        if (_options.OutFile is { } path)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// 搜索窗渲染探针（--probe-search-ui，W3-d-2/3 验收面）。全部逻辑在
    /// <see cref="SearchUiProbe"/>（假传输 + Measure/Arrange + 快照），这里只负责落盘与日志。
    /// </summary>
    private int RunProbeSearchUi()
    {
        try
        {
            var snap = SearchUiProbe.Run();
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info(
                $"搜索窗渲染探针完成：{snap["itemsCount"]} 条 / 实生成容器 {snap["realizedContainers"]} / "
                + $"布局 {snap["layoutMs"]} ms / {snap["statusRight"]}", "search");
            return 0;
        }
        catch (Exception ex)
        {
            WriteOutFile(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            _host?.Log.Warn($"搜索窗渲染探针失败：{ex}", "search");
            return 2;
        }
    }

    /// <summary>
    /// 搜索窗唤出生命周期探针（--probe-search-summon）。逻辑全在 <see cref="SearchSummonProbe"/>；
    /// 这里只负责按需创建真链路客户端（与托盘同一 <see cref="SearchIndexProcess"/> 形态）与落盘。
    /// </summary>
    private int RunProbeSearchSummon()
    {
        SearchIndexProcess? index = null;
        try
        {
            SearchIndexClient? liveClient = null;
            if (_options.ProbeSearchLive)
            {
                index = new SearchIndexProcess(_host!.Paths, _host.Paths.Root);
                liveClient = new SearchIndexClient(index);
            }

            var snap = SearchSummonProbe.Run(_options.ProbeSearchLive, liveClient, _options.WaitReadyMs);
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info($"搜索窗唤出探针完成：cycles={snap["cycles"]?["cycles"]?.AsArray().Count}", "search");
            return 0;
        }
        catch (Exception ex)
        {
            WriteOutFile(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            _host?.Log.Warn($"搜索窗唤出探针失败：{ex}", "search");
            return 2;
        }
        finally
        {
            index?.Dispose();
        }
    }

    /// <summary>OCR 遮罩生命周期探针（W4-b）：真鼠标拖拽 + Esc 真键，副作用 = 移动鼠标/可能改写剪贴板。</summary>
    private int RunProbeOcrOverlay()
    {
        try
        {
            var snap = OcrOverlayProbe.Run();
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info($"OCR 遮罩探针完成：monitors={snap["monitors"]} drag={snap["drag"]?["Branch"]}", "ocr");
            return snap["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteOutFile(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            _host?.Log.Warn($"OCR 遮罩探针失败：{ex}", "ocr");
            return 2;
        }
    }

    // ── W4-c 真机项自动化（两个探针，均不依赖"用户在物理键盘上按键"）──────────

    /// <summary>
    /// 宿主设置保存链探针（--probe-host-settings）：**程序化走真实设置窗口链路**
    /// （不 Show 对话框 —— selfcheck 先例：WPF 控件允许未显示构建），
    /// 验证"改热键 → 保存 → 落盘 → 回调重注册 → 立即生效 → 恢复默认"整条链。
    /// 无鼠标/键盘副作用，可进 verify-desktop。
    ///
    /// 断言点：① desktop.json 落盘新值 ② 生效值重合成（ReloadHostSettings）③ 热键真重注册
    /// （RegisterHotKey 成功 = _ocrHotkeyId 非空）④ unset 恢复后回落默认并重注册。
    /// </summary>
    private int RunProbeHostSettings()
    {
        var snap = new JsonObject();
        try
        {
            const string newHotkey = "Ctrl+Alt+K";

            snap["before"] = new JsonObject
            {
                ["effectiveHotkey"] = _ocrHotkey,
                ["registered"] = _ocrHotkeyId is not null,
            };

            // 程序化设置窗口：装配 → 选中宿主节 → 改值 → 保存（回调里 Reload + 重注册）
            var window = new SettingsWindow(
                _host!,
                new SettingsWindow.HostSettingsSection(
                    HostSettingsSchema.SectionId, "桌面宿主", HostSettingsSchema.SchemaJson),
                OnHostSettingsSaved);
            var selected = window.ProbeSelectHostSection();
            var setField = selected && window.ProbeSetField(HostSettingsSchema.KeyOcrHotkey, newHotkey);
            snap["probeUi"] = new JsonObject { ["hostSectionSelected"] = selected, ["fieldSet"] = setField };

            if (!selected || !setField)
            {
                snap["ok"] = false;
                snap["error"] = "设置窗口程序化装配失败（宿主节未选中或编辑器未找到）";
                WriteOutFile(snap.ToJsonString());
                _host!.Log.Warn("宿主设置探针：程序化装配失败", "probe");
                return 1;
            }

            window.ProbeSaveAsync().GetAwaiter().GetResult();

            var saved = HostSettingsSchema.TryGetString(_host!.Configs, HostSettingsSchema.KeyOcrHotkey);
            var after = new JsonObject
            {
                ["savedValue"] = saved,                    // ① desktop.json 落盘
                ["effectiveHotkey"] = _ocrHotkey,          // ② 回调链重合成
                ["registered"] = _ocrHotkeyId is not null, // ③ 热键真重注册
            };
            snap["after"] = after;

            // 恢复默认（unset → 重注册），并断言回落
            _host.Configs.Unset(HostSettingsSchema.SectionId, HostSettingsSchema.KeyOcrHotkey);
            ReRegisterHotkeys();
            snap["restored"] = new JsonObject
            {
                ["effectiveHotkey"] = _ocrHotkey,
                ["registered"] = _ocrHotkeyId is not null,
            };

            var ok = string.Equals(saved, newHotkey, StringComparison.OrdinalIgnoreCase)
                && string.Equals($"{after["effectiveHotkey"]}", newHotkey, StringComparison.OrdinalIgnoreCase)
                && after["registered"]?.GetValue<bool>() == true
                && string.Equals($"{snap["restored"]!["effectiveHotkey"]}", HostSettingsSchema.DefaultOcrHotkey, StringComparison.OrdinalIgnoreCase)
                && snap["restored"]!["registered"]?.GetValue<bool>() == true;
            snap["ok"] = ok;
            WriteOutFile(snap.ToJsonString());
            _host.Log.Info($"宿主设置探针完成：ok={ok}", "probe");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            snap["ok"] = false;
            snap["error"] = ex.Message;
            WriteOutFile(snap.ToJsonString());
            _host?.Log.Warn($"宿主设置探针失败：{ex}", "probe");
            return 2;
        }
    }

    /// <summary>
    /// OCR 热键真按键探针（--probe-ocr-hotkey）：**SendInput/keybd_event 注入真实组合键**，
    /// 验证"组合键 → RegisterHotKey → WM_HOTKEY → HotkeyFired → StartOcrCapture → 遮罩全屏出现
    /// → Esc 收窗"整条系统链 —— 这是热键链路里唯一没被 selfcheck 覆盖的一环（selfcheck 只证明
    /// 注册成功，不证明按键真触发）。
    ///
    /// ⚠️ 副作用：真实按下 Ctrl+Alt+O 与 Esc（与 --probe-ocr-overlay 的鼠标注入同族约束）
    /// —— 遮罩会全屏抢焦点 1~2 秒，只能跑在无人交互的会话；若热键未生效，注入的组合键
    /// 会落进当时的焦点窗口（探针先断言注册成功再注入，把该风险压到最低）。
    ///
    /// 泵说明：探针跑在 <see cref="WinForms.Application.Run"/> 之前，没有消息泵 ——
    /// WM_HOTKEY 投进 sink 队列后必须靠 <see cref="WinForms.Application.DoEvents"/> 手动泵出
    /// （遮罩出现阶段用 DoEvents 已够）；但 **Esc 收窗阶段必须裸泵**（PeekMessage→Dispatch）：
    /// 2026-09-26 实测，DoEvents 在「无主窗体的 WinForms 线程 + 后台启动」上下文里取出键盘
    /// 消息后不 dispatch 给 WPF 窗口（消息被泵内 PreTranslate 链吞掉，PreviewKeyDown 不触发）
    /// —— 正式 Application.Run 主泵无此问题，纯探针上下文特有，勿据此改产品代码。
    /// </summary>
    private int RunProbeOcrHotkey()
    {
        var snap = new JsonObject();
        try
        {
            if (_ocrHotkeyId is null)
            {
                snap["ok"] = false;
                snap["error"] = $"OCR 热键未注册（{_ocrHotkey}）—— 先修注册再谈按键链路";
                WriteOutFile(snap.ToJsonString());
                _host!.Log.Warn("OCR 热键探针：热键未注册，跳过注入", "probe");
                return 1;
            }

            snap["hotkey"] = _ocrHotkey;

            InjectHotkeyCombo(_ocrHotkey);
            var shown = false;
            for (var i = 0; i < 40 && !shown; i++)
            {
                Thread.Sleep(100);
                WinForms.Application.DoEvents();
                shown = _ocrManager is { AnyAlive: true };
            }

            snap["overlayShown"] = shown;
            snap["monitors"] = shown ? _ocrManager!.VisibleCount : 0;

            var closed = false;
            if (shown)
            {
                // ★ 真机上下文差异（§2.24① 变体）：探针从后台启动，遮罩 Show/Activate 抢前台
                //   被系统静默拒绝（diagForeground≠遮罩 hwnd，键盘输入流路由给别的前台线程）
                //   ⇒ 注入 Esc 前必须 AttachThreadInput 组合拳强制把前台让给遮罩。
                //   产品运行时无此问题：用户真按热键 ⇒ 进程收到 WM_HOTKEY ⇒ 天然获前台权限。
                //   Esc 是 VK 功能键走 PreviewKeyDown，不经 WM_CHAR 直通，AttachThreadInput 无副作用。
                var overlayHwnd = nint.Zero;
                foreach (var window in _ocrManager!.EnumerableWindows())
                {
                    overlayHwnd = new WindowInteropHelper(window).Handle;
                    break;
                }

                if (overlayHwnd != nint.Zero)
                {
                    ForceForeground(overlayHwnd);
                }

                // Win32 焦点（SetFocus）≠ WPF 焦点元素（Keyboard.FocusedElement）：
                // WM_KEYDOWN 到达 HwndSource 后由 WPF InputManager 查 FocusedElement 派发，
                // 没落位就静默丢弃（PreviewKeyDown 不触发）⇒ 必须 WPF 层再补一次 Focus。
                foreach (var window in _ocrManager!.EnumerableWindows())
                {
                    window.Focus();
                    break;
                }

                // 给激活/焦点协商（WM_ACTIVATE/WM_SETFOCUS 异步链）留落地时间，
                // 再泵一轮消化协商消息，随后才注入 Esc。
                Thread.Sleep(150);
                WinForms.Application.DoEvents();

                // scan code 传 0：与 W4-b 先例 OcrOverlayProbe.InjectEscape() 完全对齐。
                keybd_event(VK_ESCAPE, 0, 0, 0);
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);

                // ★ 收窗轮询必须用裸泵（PeekMessage→TranslateMessage→DispatchMessage），
                //   不能用 DoEvents：2026-09-26 实测定案 —— 在「无主窗体的 WinForms 线程 +
                //   后台启动」上下文里，DoEvents 取出键盘消息后不 dispatch 给 WPF 窗口
                //   （消息被泵内 PreTranslate 链吞掉，Esc 的 WM_KEYDOWN 永远到不了遮罩
                //   WndProc，PreviewKeyDown 不触发）；裸泵直接 dispatch 后一切正常。
                //   正式运行（Application.Run 主泵）无此问题，用户手工 Esc 收窗正常。
                //   pumpLog 留作失败诊断：失败时能看到队列里的关键消息与 hwnd 归属。
                var pumpLog = new JsonArray();
                var deadline = Environment.TickCount64 + 4000;
                while (Environment.TickCount64 < deadline && !closed)
                {
                    Thread.Sleep(100);
                    while (PeekMessage(out var m, nint.Zero, 0, 0, PM_REMOVE))
                    {
                        // 只记录关键消息（WM_ACTIVATE/SETFOCUS/KILLFOCUS/KEYDOWN/CHAR/SYSKEYDOWN）
                        if (m.Message is >= 0x0006 and <= 0x0008 or 0x0100 or 0x0102 or 0x0104 or 0x0106)
                        {
                            pumpLog.Add(new JsonObject
                            {
                                ["msg"] = $"0x{m.Message:X4}",
                                ["hwnd"] = m.Hwnd.ToInt64(),
                            });
                        }

                        _ = TranslateMessage(in m);
                        _ = DispatchMessage(in m);
                    }

                    closed = _ocrManager is { AnyAlive: false };
                }

                if (!closed)
                {
                    snap["diagPumpLog"] = pumpLog;
                }
            }

            snap["escClosed"] = closed;
            snap["ok"] = shown && closed;
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info($"OCR 热键真按键探针完成：ok={snap["ok"]}", "probe");
            return snap["ok"]!.GetValue<bool>() ? 0 : 1;
        }
        catch (Exception ex)
        {
            snap["ok"] = false;
            snap["error"] = ex.Message;
            WriteOutFile(snap.ToJsonString());
            _host?.Log.Warn($"OCR 热键真按键探针失败：{ex}", "probe");
            return 2;
        }
    }

    /// <summary>
    /// 剪贴板面板探针（--probe-clip-panel，W5-b）。逻辑全在 <see cref="ClipboardPanelProbe"/>
    /// （临时库预置 → 生命周期循环 → 真键 Enter 直贴契约），这里只负责落盘与日志。
    /// 副作用：Summon 真抢焦点 1~2 秒（与 --probe-search-summon 同代价）；Ctrl+V 真注入被抑制。
    /// </summary>
    private int RunProbeClipPanel()
    {
        try
        {
            var snap = ClipboardPanelProbe.Run();
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info(
                $"剪贴板面板探针完成：cycles={snap["cycles"]?["cycles"]?.AsArray().Count} "
                + $"paste.enterFired={snap["paste"]?["enterFired"]} clipboardMatches={snap["paste"]?["clipboardMatches"]}",
                "clip");
            return snap["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteOutFile(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            _host?.Log.Warn($"剪贴板面板探针失败：{ex}", "clip");
            return 2;
        }
    }

    /// <summary>
    /// 剪贴板热键真按键探针（--probe-clip-hotkey，手工清单 0.1 的自动化面，
    /// --probe-ocr-hotkey 同款链路）：keybd_event 注入真实 Ctrl+Alt+V → WM_HOTKEY →
    /// ToggleClipPanel → 面板唤出（Summon 真抢焦点）→ 注入真实 Esc → 收窗。
    ///
    /// 泵说明（§2.27）：WM_HOTKEY 投给 WinForms sink 用 DoEvents 够；Esc 收窗
    /// 走面板的 <b>GetAsyncKeyState 轮询兜底</b>（DispatcherTimer 由本探针的
    /// Dispatcher 泵驱动，轮询完全绕开消息层）—— 面板唤出后焦点在面板内，
    /// IsKeyboardFocusWithin 成立，Esc 按下沿必被轮询抓到。
    /// </summary>
    private int RunProbeClipHotkey()
    {
        var snap = new JsonObject();
        try
        {
            if (_clipHotkeyId is null)
            {
                snap["ok"] = false;
                snap["error"] = $"剪贴板热键未注册（{_clipHotkey}）—— 先修注册再谈按键链路";
                WriteOutFile(snap.ToJsonString());
                _host!.Log.Warn("剪贴板热键探针：热键未注册，跳过注入", "probe");
                return 1;
            }

            snap["hotkey"] = _clipHotkey;

            InjectHotkeyCombo(_clipHotkey);
            var shown = false;
            for (var i = 0; i < 40 && !shown; i++)
            {
                Thread.Sleep(100);
                WinForms.Application.DoEvents();
                shown = _clipPanel is { ProbeIsVisible: true };
            }

            snap["panelShown"] = shown;

            var closed = false;
            if (shown)
            {
                // 真实 Esc 抬手按下（面板焦点已抢到）→ 轮询兜底收窗（消息层在探针里不跑）
                keybd_event(VK_ESCAPE, 0, 0, 0);
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3000 && !closed)
                {
                    Thread.Sleep(50);
                    // 面板的 DispatcherTimer（15ms 轮询兜底）要 Dispatcher 泵才会 Tick
                    System.Windows.Application.Current?.Dispatcher?.Invoke(
                        () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    closed = _clipPanel is null || _clipPanel is { ProbeIsVisible: false };
                }
            }

            snap["escClosed"] = closed;
            snap["ok"] = shown && closed;
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info($"剪贴板热键真按键探针完成：ok={snap["ok"]}", "probe");
            return snap["ok"]!.GetValue<bool>() ? 0 : 1;
        }
        catch (Exception ex)
        {
            snap["ok"] = false;
            snap["error"] = ex.Message;
            WriteOutFile(snap.ToJsonString());
            _host?.Log.Warn($"剪贴板热键探针失败：{ex}", "probe");
            return 2;
        }
    }

    /// <summary>WinForms 打字判决探针（--probe-winforms-typing）。逻辑全在 <see cref="WinFormsTypingProbe.Run"/>。</summary>
    private int RunProbeWinFormsTyping()
    {
        try
        {
            var snap = WinFormsTypingProbe.Run();
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info($"WinForms 打字判决探针：ok={snap["ok"]} text={snap["text"]}", "probe");
            return snap["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteOutFile(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            _host?.Log.Warn($"WinForms 打字探针失败：{ex}", "probe");
            return 2;
        }
    }

    /// <summary>剪贴板监听探针（--probe-clip-monitor，W5-c）。逻辑全在 <see cref="ClipboardPanelProbe.RunMonitorProbe"/>。</summary>
    private int RunProbeClipMonitor()
    {
        try
        {
            var snap = ClipboardPanelProbe.RunMonitorProbe();
            WriteOutFile(snap.ToJsonString());
            _host!.Log.Info(
                $"剪贴板监听探针完成：started={snap["listenerStarted"]} captured={snap["captured"]} "
                + $"elapsedMs={snap["elapsedMs"]}", "clip");
            return snap["ok"]?.GetValue<bool>() == true ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteOutFile(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            _host?.Log.Warn($"剪贴板监听探针失败：{ex}", "clip");
            return 2;
        }
    }

    // keybd_event 声明与 SearchSummonProbe 同款（那边的先例：Alt+F4 / Enter 真注入）。
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;      // Alt
    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    private const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, nint hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in MSG msg);

    /// <summary>
    /// 强制把前台让给目标窗口（搜索窗唤出同款组合拳，§2.24①）：
    /// AttachThreadInput 绑定前台线程输入队列 → SetForegroundWindow + SetFocus → 解绑。
    /// </summary>
    private static void ForceForeground(nint hwnd)
    {
        var foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var thisThread = GetCurrentThreadId();
        var attached = foreThread != thisThread && foreThread != 0;
        if (attached)
        {
            _ = AttachThreadInput(thisThread, foreThread, true);
        }

        _ = SetForegroundWindow(hwnd);
        _ = SetFocus(hwnd);
        if (attached)
        {
            _ = AttachThreadInput(thisThread, foreThread, false);
        }
    }

    /// <summary>按 "Ctrl+Alt+O" 形态的字符串注入真实组合键（修饰键按下 → 主键按下 → 全部释放）。</summary>
    private static void InjectHotkeyCombo(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToUpperInvariant())
            .ToList();
        var main = parts.Count > 0 ? parts[^1] : string.Empty;
        var modifiers = parts.Take(parts.Count - 1).ToList();

        byte VkOf(string k) => k switch
        {
            "CTRL" or "CONTROL" => VK_CONTROL,
            "ALT" => VK_MENU,
            "SHIFT" => 0x10,
            "WIN" => 0x5B,
            var s when s.Length == 1 && char.IsAsciiLetterUpper(s[0]) => (byte)s[0],
            var s when s.Length == 1 && char.IsAsciiDigit(s[0]) => (byte)s[0],
            "ESC" => VK_ESCAPE,
            "ENTER" => 0x0D,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            var s when s.Length >= 2 && s[0] == 'F' && int.TryParse(s[1..], out var fn) && fn is >= 1 and <= 12
                => (byte)(0x70 + fn - 1),
            _ => 0,
        };

        var mainVk = VkOf(main);
        if (mainVk == 0)
        {
            throw new InvalidOperationException($"热键主键无法注入：{main}");
        }

        foreach (var mod in modifiers)
        {
            keybd_event(VkOf(mod), 0, 0, 0);
        }

        keybd_event(mainVk, 0, 0, 0);
        Thread.Sleep(40);   // 给系统一点合成时间（WM_HOTKEY 在主键 down 时即投递）
        keybd_event(mainVk, 0, KEYEVENTF_KEYUP, 0);
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            keybd_event(VkOf(modifiers[i]), 0, KEYEVENTF_KEYUP, 0);
        }
    }

    /// <summary>
    /// 人工模式（--ocr-show，W4-手工验收清单 M1~M6 的唤出入口）：
    /// **一次性**（与正式热键同款语义）：复制一次 ⇒ 遮罩收场 ⇒ 进程退出并落盘结局 + 逐窗诊断。
    /// 验证两块屏 = 跑两次命令、每次复制后立即粘贴对答案。
    /// （曾试过"连续模式"：复制后自动重唤 —— 但遮罩挡着屏幕没法粘贴验证，且剪贴板只留
    /// 最后一次，与"逐屏对照"的验证流程冲突，已回退。2026-09-26。）
    /// </summary>
    private int RunOcrShow()
    {
        try
        {
            var manager = new OcrOverlayManager(msg => _host?.Log.Warn(msg, "ocr"));
            string? copied = null;
            manager.TextCopied += text => copied = text;
            var finished = false;
            manager.Finished += () =>
            {
                finished = true;
                WinForms.Application.Exit();
            };

            var monitors = manager.ShowAll();
            _host!.Log.Info($"OCR 遮罩人工模式：{monitors} 扇已唤出", "ocr");

            WinForms.Application.Run();

            WriteOutFile(new JsonObject
            {
                ["ok"] = true,
                ["mode"] = "manual",
                ["monitors"] = monitors,
                ["copied"] = copied,
                ["finished"] = finished,
                ["diagnostics"] = manager.LastDiagnostics,
            }.ToJsonString());
            return 0;
        }
        catch (Exception ex)
        {
            // R1 常见入口：语言包缺失时 ShowAll 直接抛（含安装引导）—— 落盘让人看得见。
            WriteOutFile(new JsonObject { ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
            _host?.Log.Warn($"OCR 人工模式失败：{ex}", "ocr");
            return 2;
        }
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

        // 搜索文件（W3-d-1）：宿主功能入口，不来自任何工具清单 —— 热键失败时这是唯一入口。
        var searchLabel = _searchHotkeyId is null
            ? "搜索文件…（热键未注册，可改用此项；换键用 --search-hotkey 或设置窗口）"
            : $"搜索文件…（{_searchHotkey}）";
        menu.Items.Add(new ToolStripMenuItem(searchLabel, null, (_, _) => ToggleSearchWindow()));

        // 屏幕取字（W4-c）：同为宿主功能入口。热键是快路径，菜单是可见兜底 ——
        // 语言包缺失/热键被占时，用户至少能从这里点到功能并得到明确报错。
        var ocrLabel = _ocrHotkeyId is null
            ? "屏幕取字…（热键未注册，可改用此项；换键用 --ocr-hotkey 或设置窗口）"
            : $"屏幕取字…（{_ocrHotkey}）";
        menu.Items.Add(new ToolStripMenuItem(ocrLabel, null, (_, _) => StartOcrCapture()));

        // 剪贴板历史（W5-c）：子菜单 = 打开 / 暂停捕获 / 清空。标签缓存字段（selfcheck 同源），
        // 菜单每次 Opening 重建所以暂停态总是最新 —— 与托盘菜单"只重建菜单模型"的纪律一致。
        if (_clipEnabled)
        {
            var clipLabel = _clipHotkeyId is null
                ? "剪贴板历史…（热键未注册，可改用此项；换键用设置窗口）"
                : $"剪贴板历史…（{_clipHotkey}）";
            var clipMenu = new ToolStripMenuItem(clipLabel);
            clipMenu.DropDownItems.Add(new ToolStripMenuItem("打开历史面板", null, (_, _) => ToggleClipPanel()));
            clipMenu.DropDownItems.Add(new ToolStripMenuItem(
                _clipFilter.Paused ? "恢复捕获（当前已暂停）" : "暂停捕获（隐私模式）",
                null, (_, _) => ToggleClipCapturePaused()));
            clipMenu.DropDownItems.Add(new ToolStripMenuItem(
                "清空历史（保留置顶）", null, (_, _) => ClearClipHistoryFromTray()));
            menu.Items.Add(clipMenu);
        }

        AppendSearchIndexSection(menu);
        menu.Items.Add(new ToolStripSeparator());

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

    /// <summary>
    /// 「搜索索引」子菜单（2026-09-25 缺口①：暂停索引此前**只有协议、没有入口**）。
    ///
    /// <b>为什么是两个显式动作，而不是一个"勾选项"</b>：勾选态必须读一次
    /// <c>search.status</c> 才知道，而菜单是<b>在 UI 线程同步构建</b>的 —— 同步等一次管道往返
    /// 正是本项目禁掉的写法（尤其是索引进程刚起、还在自举时，这一等就是几百毫秒到几秒卡菜单）。
    /// 两个幂等的显式动作**不需要知道当前态就是正确的**：点哪个就是哪个，协议回的是实际状态。
    /// 状态行只在"已知"时才显示（来自搜索窗或上一次托盘操作），未知就如实说未知。
    /// </summary>
    private void AppendSearchIndexSection(ContextMenuStrip menu)
    {
        var known = _traySearchPaused ?? _searchWindow?.KnownPaused;
        var state = known switch
        {
            true => "当前：已暂停（搜索结果可能过时）",
            false => "当前：更新中",
            null => "当前：未知（点任一项即生效）",
        };

        var section = new ToolStripMenuItem("搜索索引");
        section.DropDownItems.Add(new ToolStripMenuItem(state) { Enabled = false });
        section.DropDownItems.Add(new ToolStripMenuItem(
            "暂停索引更新（暂停后搜索结果会变旧）", null, async (_, _) => await SetSearchPausedAsync(true)));
        section.DropDownItems.Add(new ToolStripMenuItem(
            "恢复索引更新", null, async (_, _) => await SetSearchPausedAsync(false)));
        menu.Items.Add(section);
    }

    /// <summary>
    /// 托盘出口的暂停 / 恢复。**结果以协议返回的实际状态为准**（幂等，连点不是错误），
    /// 并同步刷新搜索窗的徽标 —— 两处显示不一致比不显示更坏。
    /// </summary>
    private async Task SetSearchPausedAsync(bool pause)
    {
        try
        {
            var client = AcquireSearchClient();
            _traySearchPaused = pause
                ? await client.PauseIndexingAsync()
                : await client.ResumeIndexingAsync();

            _searchWindow?.RefreshVolumeSummary();
            _host?.Log.Info(
                $"托盘请求{(pause ? "暂停" : "恢复")}索引 ⇒ 实际 paused={_traySearchPaused}", "search");

            // 暂停是"用户看不见就会吃亏"的状态（结果静默变旧）⇒ 必须回显，不能只改菜单。
            _icon.ShowBalloonTip(3000, "Eztools 搜索索引",
                _traySearchPaused == true
                    ? "索引更新已暂停 —— 搜索结果可能过时。恢复：托盘菜单「搜索索引 → 恢复索引更新」"
                    : "索引更新已恢复",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // 失败必须显式可见，且**不更新状态缓存**（保持"未知"，避免显示一个假的已知值）
            _traySearchPaused = null;
            _host?.Log.Warn($"托盘{(pause ? "暂停" : "恢复")}索引失败：{ex.Message}", "search");
            _icon.ShowBalloonTip(4000, $"{(pause ? "暂停" : "恢复")}索引失败",
                Shorten(ex.Message, 200), ToolTipIcon.Warning);
        }
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
                // 复用**必须同时重拉** —— 窗口是活的，但内容是上次的。
                // 热键"速览选中项"走的正是这条路：只 Activate 不重拉的话，
                // 用户按了键、窗口跳到前台，里面还是上一个文件（看着像功能没生效）。
                _ = existing.RefreshAsync();
                _host?.Log.Info($"面板 {key} 已打开，激活已有窗口并重拉", "panel");
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

    /// <summary>
    /// 按面板 id 打开**指定工具**的面板（热键 <c>opensPanel</c> 的落点）。
    ///
    /// 找不到面板时只记日志、**不弹气泡**：这条路是"命令成功后的附加动作"，
    /// 面板名写错的诊断在加载期已经发过（<c>contributes.hotkey-unknown-panel</c>），
    /// 再弹一个气泡只会干扰用户刚完成任务的正反馈。
    /// </summary>
    private void OpenPanelById(string toolId, string panelId)
    {
        var tool = _host?.Registry.Tools
            .FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));

        var panel = tool?.Manifest.Contributes.Panels
            .FirstOrDefault(p => string.Equals(p.Id, panelId, StringComparison.OrdinalIgnoreCase));

        if (tool is null || panel is null)
        {
            _host?.Log.Warn($"要打开的面板 {toolId}.{panelId} 不存在（清单已改？），已忽略", "panel");
            return;
        }

        OpenPanel(tool, panel);
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
            var args = BuildInvocationArgs(item);
            var result = await _host!.Processes.InvokeCommandAsync(item.CommandId, args);

            var text = DescribeResult(result.Result);
            // 结果内容也要进日志：气泡会被 Win11 的勿扰/通知设置静默丢弃（她实测踩到），
            // 日志是唯一不依赖系统设置的验证出口。
            _host.Log.Info(
                $"托盘调用 {item.CommandId} 完成，用时 {result.Elapsed.TotalMilliseconds:F0}ms，结果 {text}",
                "tray");
            _icon.ShowBalloonTip(4000, Shorten(item.Title + " 完成", 60), Shorten(text, 240), ToolTipIcon.Info);

            // 命令成功后把声明的面板带到前台（hotkeys[].opensPanel）。
            // **放在成功分支里、气泡之后**：失败时不打开（用户该先看到失败原因）；
            // 气泡先弹（它是即时的正反馈），面板随后 Show/Activate。
            if (item.OpensPanel is { Length: > 0 } panelId)
            {
                OpenPanelById(item.ToolId, panelId);
            }
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
    ///
    /// W4-c：传入宿主设置节（热键/OCR 语言）与保存回调 —— 回调里重读配置并重注册热键，
    /// "改键立即生效"不用重启托盘。
    /// </summary>
    private void OpenSettings()
    {
        var window = new SettingsWindow(
            _host!,
            new SettingsWindow.HostSettingsSection(
                HostSettingsSchema.SectionId, "桌面宿主", HostSettingsSchema.SchemaJson),
            OnHostSettingsSaved);
        window.ShowDialog();
    }

    /// <summary>宿主设置保存后的托盘侧反应：重读生效值 + 重注册全部热键（W4-c FR-9 的"保存即生效"）。</summary>
    private void OnHostSettingsSaved()
    {
        ReRegisterHotkeys();
        _icon.ShowBalloonTip(2500, "Eztools 宿主设置",
            "已保存：热键已重新注册；OCR 语言下次唤出屏幕取字时生效", ToolTipIcon.Info);
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
        // 声明的上下文来源（toolId:command → input）。仲裁只裁决"键归谁"，
        // 不携带清单字段 —— 所以这里另建一张表，赢家再按它取声明的 Input。
        var declaredInputs = new Dictionary<string, MenuInput>(StringComparer.OrdinalIgnoreCase);
        // 同理：opensPanel 也要自己带过去（仲裁的 HotkeyClaim 里没有它的位置）。
        var declaredPanels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tool, hotkey, effective) in _host!.Registry.Hotkeys(_host.StateStore))
        {
            var combo = HotkeyCombo.TryParse(effective);
            if (combo is null)
            {
                _host.Log.Warn($"热键 '{effective}'（{tool.Id}:{hotkey.Command}）无法解析，已跳过", "hotkey");
                continue;
            }

            claims.Add(new HotkeyClaim(tool.Id, hotkey.Command, combo, order++));
            declaredInputs[$"{tool.Id}:{hotkey.Command}"] = hotkey.Input;

            if (hotkey.OpensPanel is { Length: > 0 } declaredPanel)
            {
                declaredPanels[$"{tool.Id}:{hotkey.Command}"] = declaredPanel;
            }
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

            // input：按清单声明注入。**未声明回落 Clipboard** —— 这是兼容默认值
            //（ToolHotkey.Input 的缺省也是它）：热键命令可能没有 menus 项可继承声明
            //（filehash.hash 正是这种），而热键路径历史上就一律注入剪贴板。
            // 上面的 4 条热键注入断言（verify-desktop.py §2b）守的就是这个回落语义。
            _hotkeyItems[id.Value] = new TrayMenuItem
            {
                ToolId = winner.ToolId,
                CommandId = winner.Command,
                Title = $"{winner.Combo.Normalized}（{winner.ToolId}）",
                GroupKey = "hotkey",
                Input = declaredInputs.TryGetValue($"{winner.ToolId}:{winner.Command}", out var declared)
                    ? declared
                    : MenuInput.Clipboard,
                // 触发成功后要带到前台的面板（缺省 null = 不打开）。
                // 与 Input 同款"另建表带回"的处理 —— 仲裁不携带清单字段。
                OpensPanel = declaredPanels.TryGetValue($"{winner.ToolId}:{winner.Command}", out var panelId)
                    ? panelId
                    : null,
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

    /// <summary>
    /// 刷新后重注册（工具启停/增删、用户改键都可能改变归属）。
    ///
    /// 🔴 W4-c 修复的既有 bug：这里原来只重注册**工具热键**，而 <see cref="HotkeyHook.UnregisterAll"/>
    /// 注销的是**全部**（含搜索热键）—— 也就是说「刷新菜单」一次，搜索热键就静默失效一次
    /// （症状：刷新后按键没反应，重启托盘才恢复；W3-d-1 落地以来一直存在，无人触发过该路径所以没暴露）。
    /// 现在宿主级热键统一在此重注册，这条链也是"设置窗口改热键 → 立即生效"的通路。
    /// </summary>
    private void ReRegisterHotkeys()
    {
        ReloadHostSettings();   // 配置文件可能已被设置窗口/手工编辑更新，先重新合成生效值
        _hotkeys!.UnregisterAll();
        _hotkeyItems.Clear();
        RegisterHotkeysFromRegistry();
        RegisterSearchHotkey();
        RegisterOcrHotkey();
        RegisterClipHotkey();
        ApplyClipMonitorState();   // clip.enabled 翻转要跟着启停监听（其余键已在 Reload 里生效）
    }

    private void ReportSelfCheck()
    {
        var model = TrayMenuBuilder.Build(_host!.Registry);
        var line = $"自检：工具 {_host.Registry.Tools.Count} 个 · 托盘项 {model.Count} 个 · " +
                   $"热键注册 {_hotkeys!.RegisteredCount} 个 · 图标 {_iconImage.Width}x{_iconImage.Height} · " +
                   $"菜单组 {model.Groups.Count}";

        // P1b：设置窗口的 schema → 控件映射清单（不弹窗，纯数据装配）。
        // W4-c：传入宿主设置节 —— desktop（热键/OCR 语言）与工具配置走同一条 schema 驱动渲染，
        // 自检清单里以 `设置清单 desktop: …` 行出现（verify-desktop.py 断言它）。
        var settings = new SettingsWindow(
            _host,
            new SettingsWindow.HostSettingsSection(
                HostSettingsSchema.SectionId, "桌面宿主", HostSettingsSchema.SchemaJson))
            .InventoryForSelfCheck();

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

        // 2026-09-21：热键 → 面板的绑定清单。
        // 为什么值得单列一行：这条链路上"清单字段解析 / 仲裁带回 / 触发后开面板"三段
        // 都在宿主内部，从外面只看得到"按了键没反应"。把绑定关系报出来，验收就有话可说
        // （`verify-desktop.py` 断言这一行），否则只能人工按键确认。
        var hotkeyPanels = _hotkeyItems.Values
            .Where(kv => kv.OpensPanel is { Length: > 0 })
            .Select(kv => $"{kv.CommandId}→{kv.OpensPanel}")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var bindLine = hotkeyPanels.Count == 0
            ? "热键直达面板：无（没有热键声明 opensPanel）"
            : $"热键直达面板 {hotkeyPanels.Count} 条：{string.Join(" / ", hotkeyPanels)}";
        _host.Log.Info(bindLine, "selfcheck");

        // 搜索窗热键（W3-d-1）：注册结局单列一行 —— 被第三方占用时的提示文案是断言面。
        var searchLine = _searchHotkeyId is null
            ? "搜索热键：未注册（组合键被占用或解析失败——托盘菜单「搜索文件」可用）"
            : $"搜索热键：{_searchHotkey} → 搜索窗（已注册）";
        _host.Log.Info(searchLine, "selfcheck");

        // 屏幕取字热键 + 语言（W4-c）：同款断言面。语言行把"生效值"打出来 ——
        // 配置文件写了什么与托盘实际用的是什么是两回事（合成优先级 + 修过 bug 的历史教训）。
        var ocrLine = _ocrHotkeyId is null
            ? "OCR 热键：未注册（组合键被占用或解析失败——托盘菜单「屏幕取字」可用）"
            : $"OCR 热键：{_ocrHotkey} → 屏幕取字（已注册）";
        var ocrLangLine = string.IsNullOrWhiteSpace(_ocrLanguage)
            ? "OCR 语言：auto（未配置，按引擎默认优先级自动选择）"
            : $"OCR 语言：{_ocrLanguage}";
        _host.Log.Info(ocrLine, "selfcheck");
        _host.Log.Info(ocrLangLine, "selfcheck");

        // 剪贴板热键 + 监听状态（W5-c）：注册结局与监听运行态单列成行 ——
        // "面板能唤出"与"复制真的进历史"是两条链路，断言面分开（后者看监听行）。
        var clipLine = !_clipEnabled
            ? "剪贴板热键：未注册（clip.enabled=false）"
            : _clipHotkeyId is null
                ? "剪贴板热键：未注册（组合键被占用或解析失败——托盘子菜单「剪贴板历史」可用）"
                : $"剪贴板热键：{_clipHotkey} → 剪贴板历史面板（已注册）";
        var monitorLine = !_clipEnabled
            ? "剪贴板监听：已停用（clip.enabled=false）"
            : _clipMonitor is { IsRunning: true }
                ? $"剪贴板监听：运行中（上限 {_clipMaxItems} 条 · 图片保留 {_clipImageRetentionDays} 天 · "
                  + $"黑名单 {_clipFilter.BlacklistSnapshot().Count} 个 · 暂停:{(_clipFilter.Paused ? "是" : "否")} · 会话捕获 {_clipCaptured} 条）"
                : "剪贴板监听：未运行（启动失败——详见日志）";
        _host.Log.Info(clipLine, "selfcheck");
        _host.Log.Info(monitorLine, "selfcheck");

        WriteReport(line, settings.Concat(panels).Append(bindLine).Append(searchLine)
            .Append(ocrLine).Append(ocrLangLine).Append(clipLine).Append(monitorLine).ToList());
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
                    // 拉取走 PanelWindow.PullAsync（请求构造的单一来源，含 inputs 快照）——
                    // 自检与生产**同一条请求路径**，否则生产侧的请求构造零断言覆盖
                    //（实测：自检自调 PanelDataAsync 时，"拉取不带快照"的突变没被抓）。
                    var window = new PanelWindow(_host, tool, panel);
                    var result = window.PullAsync("selfcheck").GetAwaiter().GetResult();
                    lines.AddRange(window.InventoryForSelfCheck(result.Data, result.DeclaredCommandIds));

                    // V1.3 input 节点（协议 §3.7.8）：21.3 / 21.6 / 21.4 / 21.5 的 WPF 侧验收。
                    // 编排在这里而不是 PanelWindow 内部：PanelWindow 只提供"模拟打字 / 模拟失焦"
                    // 两个钩子（走真实生产路径），拉取通道仍归本方法编排。
                    var inputKeys = PanelNodeValidator.CollectInputKeys(result.Data);
                    if (inputKeys.Count > 0)
                    {
                        // 固定夹具串 —— F3（§3.7.4）：绝不把真实用户输入写进诊断输出。
                        const string typed = "Eztools 自检输入";

                        // ① 打字（真实 TextChanged 路径）—— 值进宿主缓存，
                        //    下一次 PullAsync 自然带上（这正是 21.3 要验的回传路径）
                        window.SimulateInputForSelfCheck(inputKeys[0], typed);

                        // ② 再拉一次：工具应原样回显 inputs.q（21.3 的 E2E）
                        var echoed = window.PullAsync("selfcheck").GetAwaiter().GetResult();

                        // ③ 重绘（21.6：整体重绘不得吞字 —— 框里的字与快照都必须还在）
                        window.InventoryForSelfCheck(echoed.Data, result.DeclaredCommandIds);
                        lines.Add($"面板输入 {tool.Id}.{panel.Id}: 键 {inputKeys[0]} ← \"{typed}\""
                                  + $" · 重绘后框 {window.BoxValuesForSelfCheck().ToJsonString()}"
                                  + $" · 快照 {window.SnapshotForSelfCheck().ToJsonString()}");

                        // 回显行：把重绘后的全部文本交给验收脚本断言（21.3 / 21.9 WPF 侧）
                        lines.Add($"面板输入回显 {tool.Id}.{panel.Id}: {window.TextContentForSelfCheck()}");

                        // ③″ 拿掉再放回（21.6b，2026-09-24 复盘加固）：payload 定义"存在"（§3.7.2）。
                        //     工具把某 input 暂时拿掉 ⇒ 成功渲染路径裁剪值缓存；
                        //     再放回 ⇒ 该 key 视为**重新首次出现**，节点 value 重新生效一次（值复位）。
                        //     两份变体载荷用 JsonNode.Parse 深克隆构造 —— JsonNode 挂过父节点不能复用（§四.4）。
                        //     中间那次"拿掉"渲染的返回行刻意丢弃：只验状态迁移，不往断言流里塞重复清单。
                        const string reentryValue = "Eztools 重入值";

                        var stripped = JsonNode.Parse(result.Data.ToJsonString())!.AsObject();
                        var strippedNodes = stripped["nodes"]!.AsArray();
                        for (var i = strippedNodes.Count - 1; i >= 0; i--)
                        {
                            if (string.Equals(strippedNodes[i]?["type"]?.GetValue<string>(), "input",
                                    StringComparison.Ordinal))
                            {
                                strippedNodes.RemoveAt(i);
                            }
                        }

                        window.InventoryForSelfCheck(stripped, result.DeclaredCommandIds);   // 拿掉 ⇒ 裁剪

                        var readded = JsonNode.Parse(result.Data.ToJsonString())!.AsObject();
                        foreach (var n in readded["nodes"]!.AsArray())
                        {
                            if (n is JsonObject o
                                && string.Equals(o["type"]?.GetValue<string>(), "input", StringComparison.Ordinal)
                                && string.Equals(o["key"]?.GetValue<string>(), inputKeys[0], StringComparison.Ordinal))
                            {
                                o["value"] = reentryValue;
                            }
                        }

                        window.InventoryForSelfCheck(readded, result.DeclaredCommandIds);    // 放回 ⇒ 首现规则重新生效
                        lines.Add($"面板输入重入 {tool.Id}.{panel.Id}: {window.BoxValuesForSelfCheck().ToJsonString()}");

                        // ③′ 连续第二次输入（与第一次间隔 < 80 ms ⇒ 排队待发）。
                        //     若只输入一次，Report 立即放行、**无待发态**，失焦的"待发清空"
                        //     断言会因"无事可测"恒绿 —— S9 同族（断言挂在恒真前提上）。
                        window.SimulateInputForSelfCheck(inputKeys[0], typed + "2");

                        // ④ 失焦（21.4/21.5：快照清空、控件清空、节流待发清空 —— F1/F2 守卫）
                        window.BlurForSelfCheck();
                        lines.Add($"面板输入失焦 {tool.Id}.{panel.Id}: 快照 {window.SnapshotForSelfCheck().ToJsonString()}"
                                  + $" · 框 {window.BoxValuesForSelfCheck().ToJsonString()}"
                                  + $" · 待发 {window.HasPendingInputForSelfCheck}");
                    }

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
        return InvokeBlocking(item, out _) ? 0 : 5;
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

        var ok = InvokeBlocking(item, out var result);

        // 结果落盘（UTF-8 无 BOM，与自检输出同一约定）：脚本据此断言
        // "工具真的收到了注入的上下文"，而不只是"调用退出码为 0"。
        //
        // 落盘的是一个**包装对象**：`balloon`（宿主算出的气泡正文）+ 工具原返回值的各字段。
        // 前者让 §2d 能对"真·气泡正文"下判据（而不是照抄一份规则）；
        // 后者沿用既有断言（recorded / paths）。
        // 注入 `balloon` 不改变任何既有键，旧断言不受影响。
        //
        // `opensPanel` 是同一考虑下的第二个观测点（热键直达面板）：宿主**已经**解析出
        // "这条热键触发后该开哪个面板"，把它报出来，验收才算对这条链路有话可说 ——
        // 否则「热键 → 面板」只能靠人手按一次键去看，回归时无人接手。
        if (_options.OutFile is { Length: > 0 } outFile)
        {
            try
            {
                var payload = result?.DeepClone() as JsonObject ?? new JsonObject();
                payload["balloon"] = LastBalloonBody;
                payload["opensPanel"] = item.OpensPanel;

                File.WriteAllText(outFile, payload.ToJsonString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _host!.Log.Warn($"热键探针写 {outFile} 失败：{ex.Message}", "selfcheck");
            }
        }

        _host!.Log.Info(
            $"热键探针 {commandId}（input={item.Input.ToWire()}）→ {(ok ? "成功" : "失败")}", "selfcheck");
        return ok ? 0 : 5;
    }

    /// <summary>
    /// 按条目声明的 <c>Input</c> 取上下文并构造调用参数。
    ///
    /// 为什么单独一个助手：取上下文本身有代价（读剪贴板 / 枚举 Shell 窗口），
    /// **只应按声明取需要的那一种**。这里集中"取什么"，
    /// <see cref="TrayMenuItem.BuildArgs"/> 保持纯映射（只做"内容 → args"的形状转换）。
    /// </summary>
    private JsonObject BuildInvocationArgs(TrayMenuItem item) => item.Input switch
    {
        MenuInput.ShellSelection => item.BuildArgs(null, ReadShellSelectionLogged()),
        _ => item.BuildArgs(ReadClipboardSafely()),
    };

    /// <summary>
    /// 读 Shell 选中项并留痕。"拿不到"是常态而非异常（按空注入），
    /// 但**原因必须进日志** —— 否则"为什么速览拿不到文件"只能靠猜。
    /// </summary>
    private IReadOnlyList<string> ReadShellSelectionLogged()
    {
        var paths = ShellSelectionReader.ReadForegroundSelection();
        if (ShellSelectionReader.LastError is { } reason)
        {
            _host?.Log.Info($"未取得资源管理器选中项（按空注入）：{reason}", "tray");
        }

        return paths;
    }

    /// <summary>
    /// 同步触发一次（自动化用）。**只在没有消息循环时可用** ——
    /// 一旦 <c>Application.Run</c> 跑起来，在 UI 线程同步等待异步调用就会死锁。
    ///
    /// <paramref name="result"/> 带回工具的原始返回值（调用失败为 null）——
    /// 热键探针要把它写进 <c>--out</c>：shellSelection 注入的观测量是工具返回的
    /// <c>recorded / paths</c>，只看退出码分不清"拿到 1 项"和"什么都没拿到"。
    /// </summary>
    private bool InvokeBlocking(TrayMenuItem item, out JsonObject? result)
    {
        result = null;
        try
        {
            var args = BuildInvocationArgs(item);
            var invocation = _host!.Processes.InvokeCommandAsync(item.CommandId, args).GetAwaiter().GetResult();

            _host.Log.Info(
                $"自动触发 {item.CommandId} 成功，用时 {invocation.Elapsed.TotalMilliseconds:F0}ms", "selfcheck");
            result = invocation.Result as JsonObject;

            // 记录"这条结果会以什么文本进气泡"—— 探针把它一并落盘，
            // 于是**气泡摘要约定**有了真实观测量（详见 LastBalloonBody 的注释）。
            LastBalloonBody = DescribeResult(result);
            return true;
        }
        catch (Exception ex)
        {
            _host?.Log.Error($"自动触发 {item.CommandId} 失败：{ex.Message}", "selfcheck");
            return false;
        }
    }

    /// <summary>
    /// 最近一次调用会显示在气泡里的正文（由 <see cref="DescribeResult"/> 算出）。
    ///
    /// **为什么需要这个"侧信道"**：<c>InvokeAsync</c>（真实点击路径）与
    /// <c>InvokeBlocking</c>（自动化探针路径）是两条不同的代码路径，
    /// 探针怎么改都验不到前者。而 `verify-desktop.py` §2d 原先只在 Python 里
    /// **照抄**了 DescribeResult 的规则 —— 抄得再对也证明不了"宿主真的调了它"。
    /// 实测踩到过：方法写完、调用点却没接上（还原突变时被 `cp` 覆盖），
    /// **6 条断言全绿、验收 169/0，功能实际为零。**
    ///
    /// 现在两条路径共用 <see cref="DescribeResult"/> 算正文，探针落盘这个值，
    /// 断言就能落在"**宿主算出来的**气泡正文"上，而不是我手抄的副本。
    /// 代价是两处赋值（本类只有这两个调用点），换来判据不再能造假绿。
    /// </summary>
    public string? LastBalloonBody { get; private set; }

    /// <summary>
    /// 把工具返回值渲染成**给人看的一行字**：优先取 <c>result.hint</c>（结果里的一段短文本），
    /// 没有才回落成整份 JSON 内联。
    ///
    /// 为什么要有这个约定：工具返回值是**协议载荷**，不是给人读的话。`preview.show` 的载荷
    /// 动辄上百字符（路径、大小、行数、面板 id…），内联进气泡会被 <see cref="Shorten"/> 拦腰截断，
    /// 用户看到的是一段断了头的 JSON。约定"想让人看什么就放进 <c>hint</c>"，
    /// 把"载荷"与"摘要"分开：**工具自己最清楚哪几个字有意义**，托盘不该去猜字段语义。
    ///
    /// 回落 JSON 是为了兼容既有工具（它们的返回值本来就是短状态对象，如 `{"pong":true}`）。
    /// </summary>
    private static string DescribeResult(JsonNode? result)
    {
        if (result is null)
        {
            return "（无返回值）";
        }

        // hint 必须是 JSON 字符串才算数：`{"hint": {"a":1}}` 这种是工具写错了字段类型，
        // 静默内联成 JSON 比把对象 ToString() 成类名更有用。
        if (result is JsonObject obj
            && obj["hint"] is JsonValue hintValue
            && hintValue.TryGetValue<string>(out var hint)
            && !string.IsNullOrWhiteSpace(hint))
        {
            return hint;
        }

        return JsonText.Write(result, indented: false);
    }

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
