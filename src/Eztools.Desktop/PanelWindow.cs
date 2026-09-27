// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;            // input 节点（协议 §3.7）：Key / KeyEventArgs（Enter 提交）
using System.Windows.Media;
using System.Windows.Media.Imaging;   // image 节点（协议 §3.6）：BitmapImage / BitmapCacheOption
using System.Windows.Threading;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Processes;
using Eztools.Host.Registry;

namespace Eztools.Desktop;

/// <summary>
/// P4 Wave 2c 面板窗口：把工具返回的 <c>nodes[]</c> 声明式数据渲染成原生 WPF 控件树。
///
/// **架构前提（不可动摇）**：工具只返回**数据**，宿主负责渲染。工具进程永不向宿主注入 UI 代码。
/// 这是"工具进程隔离"的直接推论，也是本文件存在的全部理由 —— 见
/// <c>docs/P4-Wave2c-面板协议.md</c> §1.2（明确不做：工具自带 HTML/WPF 代码 / WebView2）。
///
/// 三条设计约束：
/// <list type="number">
/// <item><b>整体重绘</b>（协议 §3.4）：每次拉取返回完整 <c>nodes[]</c>，宿主清空重画。
///   无 diff、无节点 id ⇒ **工具侧零状态**（它只需回答"现在该显示什么"）。</item>
/// <item><b>面板错误不弹框、不中断宿主</b>（协议 §3.5 总原则）：载荷坏 / 超时 / 工具崩，
///   一律渲染成窗口里的一条提示。绝不让渲染失败冒泡成宿主异常。</item>
/// <item><b>绝不在 UI 线程同步等</b>（协议 §5.3）：拉数据是 <c>await</c>；
///   本项目的托盘历史里，"UI 线程同步等工具返回"已导致过两次死锁（P2 气泡 / RunProbeNotify）。</item>
/// </list>
/// </summary>
public sealed class PanelWindow : Window
{
    private readonly EztoolsHost _host;
    private readonly RegisteredTool _tool;
    private readonly ToolPanel _panel;

    private readonly StackPanel _body;
    private readonly TextBlock _status;
    private readonly Button _refreshButton;

    private DispatcherTimer? _timer;
    private IDisposable? _eventSubscription;
    private bool _busy;
    private bool _closed;

    // ── input 节点状态（协议 §3.7，V1.3）────────────────────────────────────
    //
    // 值的所有权（§3.7.1）：节点只声明"这里有个叫什么名字的输入框"；
    // 用户输入由**宿主**持有（_inputValues）并随下一次 tool.panel.data 请求回传。
    // 缓存跨重绘存活（全量重绘不得吞掉打字 —— 21.6），失焦/关窗即清（F2/F5）。
    private readonly InputThrottle _throttle;                                       // 节流（§3.7.3，契约层）
    private DispatcherTimer? _inputTimer;                                           // 节流的到点回调（单发）
    private readonly Dictionary<string, string> _inputValues = new(StringComparer.Ordinal);
    private List<TextBox> _inputBoxes = new();   // 当前渲染的输入框（失焦清空只作用于它们）
    private bool _suppressInputEvents;           // 程序化清空控件时，TextChanged 不得反过来进节流器
    private bool _selfCheck;                     // 自检窗口绝不自动拉取 —— 拉取由 TrayApplication 编排

    // §3.7.5：同一面板同一时刻只允许一个在途请求。_busy 期间到达的新拉取不排队，
    // 记一个标记，旧请求完成后**用最新快照补拉一次**（trailing 合并 —— 与输入合并同思想）。
    private bool _pullQueued;

    /// <param name="host">宿主（数据通道与事件总线都从这里取）。</param>
    /// <param name="tool">声明了该面板的工具。</param>
    /// <param name="panel">清单里的面板声明（尺寸、刷新间隔等）。</param>
    public PanelWindow(EztoolsHost host, RegisteredTool tool, ToolPanel panel)
    {
        _host = host;
        _tool = tool;
        _panel = panel;

        Title = $"{tool.Manifest.Name} — {panel.Title}";
        Width = panel.Width;
        Height = panel.Height;
        MinWidth = ToolPanelLimits.MinWidth;
        MinHeight = ToolPanelLimits.MinHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        ApplyModernTheme();

        _body = new StackPanel { Margin = new Thickness(16, 14, 16, 10) };

        _status = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _refreshButton = new Button
        {
            Content = "刷新",
            MinWidth = 72,
            IsDefault = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _refreshButton.Click += async (_, _) => await ReloadAsync("refresh");

        var bottom = new Grid { Margin = new Thickness(16, 0, 16, 12) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(_refreshButton, 1);
        bottom.Children.Add(_status);
        bottom.Children.Add(_refreshButton);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _body,
        });

        Content = root;

        // 首次拉取放在 Loaded：此时窗口句柄已建立，异常提示也能真的显示出来。
        // 同时在这里订阅宿主事件总线（协议 §5.4：ToolEnabledChanged / ToolStopped / RegistryReloaded）。
        Loaded += async (_, _) =>
        {
            SubscribeHostEvents();
            StartTimer();
            await ReloadAsync("open");
        };

        // 关闭必须停表 + 退订 —— 否则定时器会一直拉起已关闭窗口的拉取，
        // 事件总线也会往一个死窗口推（Wave 2a 的 Subscribe 返回 IDisposable，就是为了这个）。
        // 输入节流的到点回调一并停（F5：值不持久化，窗口死掉就该全部消失）。
        Closed += (_, _) =>
        {
            _closed = true;
            _timer?.Stop();
            _timer = null;
            _inputTimer?.Stop();
            _inputTimer = null;
            _eventSubscription?.Dispose();
            _eventSubscription = null;
        };

        // V1.3（协议 §3.7.4 F2）：窗口失焦 = 输入会话结束 —— 停止上报并清空全部已缓存值。
        // 用 Deactivated（整窗失去激活）而不是 TextBox.LostFocus：用户在面板内部把焦点
        // 从输入框移到按钮**不算失焦**，此时输入必须照常上报；切走整个窗口才算
        // （W3 计划 R19 预判的坑："点一下按钮输入就没了"）。
        Deactivated += (_, _) => HandleWindowBlur();

        _throttle = new InputThrottle(schedule: ScheduleInputFire);
    }

    /// <summary>
    /// 挂 iNKORE 主题资源。与 <see cref="SettingsWindow.ApplyModernTheme"/> **完全同款**（三步都必要）——
    /// 那里的注释解释了每一步的由来（尤其第 ③ 步预播 <c>OverlayCornerRadius</c>：iNKORE 0.10.2.1
    /// 的 ComboBoxHelper 会 TryFindResource 一个它自己字典里没有的键，返回 null 后拆箱 NRE）。
    /// 本窗口 V1 没有下拉框，但按钮/滚动条同样吃主题资源，且**共用 App 级字典**（已挂过就不重复挂）。
    /// </summary>
    private void ApplyModernTheme()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.ThemeResources());
        Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.Controls.XamlControlsResources());

        var themeManager = iNKORE.UI.WPF.Modern.ThemeManager.Current;
        if (themeManager is not null)
        {
            themeManager.ApplicationTheme = iNKORE.UI.WPF.Modern.ApplicationTheme.Light;
        }

        var appRes = app.Resources;
        if (!appRes.MergedDictionaries.OfType<iNKORE.UI.WPF.Modern.ThemeResources>().Any())
        {
            appRes.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.ThemeResources());
            appRes.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.Controls.XamlControlsResources());
        }

        const string overlayKey = "OverlayCornerRadius";
        if (!appRes.Contains(overlayKey))
        {
            appRes[overlayKey] = new System.Windows.CornerRadius(4);
        }
    }

    // ── 订阅与定时 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 订阅宿主事件总线（协议 §5.4 —— 这是 Wave 2a 事件总线的**第二个真实消费方**）。
    /// 只关心**本工具**的事件：别的工具启停与我无关，全量重拉就是白烧 CPU。
    /// </summary>
    private void SubscribeHostEvents()
    {
        try
        {
            _eventSubscription = _host.Events.Subscribe(evt =>
            {
                if (_closed)
                {
                    return;
                }

                var relevant = evt.Kind switch
                {
                    HostEventKind.ToolEnabledChanged or HostEventKind.ToolStopped
                        => string.Equals(evt.ToolId, _tool.Id, StringComparison.OrdinalIgnoreCase),
                    HostEventKind.RegistryReloaded => true,
                    _ => false,
                };

                if (!relevant)
                {
                    return;
                }

                // 事件在**后台线程**投递（HostEventBus 的模型），所以必须回到 UI 线程再碰控件。
                // 这里与托盘的 OnHostEvent 不同：托盘刻意不做 marshal（NotifyIcon 不是 Control、
                // 且 marshal 会与"UI 线程同步等工具返回"撞死锁）；而面板窗口是纯 WPF，
                // Dispatcher.BeginInvoke 是**非阻塞**投递，不会形成等待环 —— 两者的取舍条件不同。
                _ = Dispatcher.BeginInvoke(new Action(async () => await ReloadAsync("host-event")));
            });
        }
        catch (Exception ex)
        {
            // 订阅失败不该让面板打不开（顶多是不自动刷新）
            _host.Log.Warn($"面板 {_tool.Id}.{_panel.Id} 订阅宿主事件失败：{ex.Message}", "panel");
        }
    }

    private void StartTimer()
    {
        if (_panel.RefreshMs <= 0)
        {
            return;   // 0 = 不自动刷新（协议 §2.2；越界值已在解析期归 0）
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(_panel.RefreshMs),
        };
        _timer.Tick += async (_, _) => await ReloadAsync("refresh");
        _timer.Start();
    }

    // ── 数据拉取与渲染 ──────────────────────────────────────────────────────

    /// <summary>
    /// 拉一次数据并重绘。<paramref name="reason"/> 对应协议 §3.2 的
    /// <c>open</c> / <c>refresh</c> / <c>host-event</c>（工具可用它区分"为什么被拉"）。
    ///
    /// <c>_busy</c> 门闩：定时器与手动刷新可能重叠，重入会让窗口内容闪烁并浪费一次进程往返。
    /// </summary>
    /// <summary>
    /// 从外部请求重拉一次面板数据（托盘复用已开窗口时用）。
    ///
    /// **为什么需要它**：<see cref="TrayApplication.OpenPanel"/> 复用窗口时原实现只调
    /// <c>Activate()</c> —— 窗口是活的，但里面画的还是上次的数据。热键"速览选中项"复用时
    /// 必须重拉，否则用户按了键、窗口跳到前台，内容却还是上一个文件（看着像功能没生效）。
    ///
    /// `reason` 走 `"refresh"`（协议 §5.1 的既定取值之一）：对工具而言这与用户点刷新按钮
    /// 完全同义，不需要为"是热键触发的"新增一个取值。
    /// </summary>
    public Task RefreshAsync() => ReloadAsync("refresh");

    private async Task ReloadAsync(string reason)
    {
        if (_closed)
        {
            return;
        }

        if (_busy)
        {
            // §3.7.5：同一面板同一时刻只允许一个在途请求。新到达的拉取**不排队**——
            // 记标记，旧请求完成后用**最新快照**补拉一次（值缓存 _inputValues 一直在，
            // 补拉自然带上合并后的最终值 —— 请求堆积不会发生）。
            _pullQueued = true;
            return;
        }

        _busy = true;
        _refreshButton.IsEnabled = false;
        _status.Text = "正在拉取…";

        try
        {
            var result = await PullAsync(reason);

            if (_closed)
            {
                return;
            }

            _body.Children.Clear();
            RenderPayload(result.Data, result.DeclaredCommandIds);

            // 状态栏（协议 §5.1）：上次刷新时刻 · 耗时 · 节点数
            var nodeCount = CountNodes(result.Data);
            _status.Text = $"上次刷新 {DateTime.Now:HH:mm:ss} · 耗时 {result.Elapsed.TotalMilliseconds:F0}ms"
                           + $" · {nodeCount} 个节点";
        }
        catch (ToolRpcException ex)
        {
            // 档位 / 面板不存在这类拒绝：显示原因（不弹框）
            ShowError("无法打开面板", ex.Message);
        }
        catch (ToolProtocolException ex)
        {
            ShowError("工具调用失败", ex.Message);
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"面板 {_tool.Id}.{_panel.Id} 拉取失败：{ex.Message}", "panel");
            ShowError("拉取面板数据失败", ex.Message);
        }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                _refreshButton.IsEnabled = true;
            }

            // 尾随补拉（§3.7.5）：在途期间有新拉取到达 ⇒ 用最新快照再来一次。
            // reason 用 refresh：无论先前是什么理由触发的，补拉的意义都是"按最新值重画"。
            if (!_closed && _pullQueued)
            {
                _pullQueued = false;
                _ = ReloadAsync("refresh");
            }
        }
    }

    /// <summary>
    /// 把工具返回的载荷渲染进 <see cref="_body"/>。
    ///
    /// 容错分层（协议 §3.5）：**载荷级**错误显示整块提示；**节点级**错误跳过该节点。
    /// 两级分开的意义：一个坏节点不该让整块面板白屏，但工具返回了非对象时也确实无内容可画。
    /// </summary>
    private void RenderPayload(JsonNode? data, IReadOnlyList<string>? declaredCommands)
    {
        // 新渲染 ⇒ 旧输入框全部作废（失焦清空只作用于新框；值缓存在成功路径末尾裁剪）。
        _inputBoxes = new List<TextBox>();

        if (data is not JsonObject obj)
        {
            // 非对象载荷：把原始返回前 500 字符摆出来（截断），不然用户只看到"格式错"却不知错在哪
            var raw = data?.ToJsonString() ?? "null";
            if (raw.Length > 500)
            {
                raw = raw[..500] + "…";
            }

            AddNotice($"⚠ 工具返回格式不正确（期望 JSON 对象，实际是 {Shape(data)}）", isError: true);
            AddNotice(raw, isError: false, monospace: true);
            return;
        }

        if (obj["nodes"] is not JsonArray nodes)
        {
            AddNotice("⚠ 工具返回的对象里没有 nodes 数组（协议要求 nodes 为必填）", isError: true);
            return;
        }

        if (nodes.Count == 0)
        {
            AddNotice("（此面板当前无内容）", isError: false);
            return;
        }

        var renderedInputKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            try
            {
                RenderNode(node, declaredCommands, renderedInputKeys);
            }
            catch (Exception ex)
            {
                // 节点级容错：单个节点渲染异常 → 跳过它（其余照画），并留下可追查的痕迹
                _host.Log.Warn(
                    $"面板 {_tool.Id}.{_panel.Id} 的节点渲染失败（已跳过）：{ex.Message}", "panel");
                AddNotice($"⚠ 一个节点渲染失败（{ex.GetType().Name}），已跳过", isError: false);
            }
        }

        // 裁剪值缓存：工具本次没再给出的 key 不再占位（§3.7.2 —— payload 定义"存在"）。
        // 只在**成功渲染**路径执行：错误提示路径（上面的提前 return）不得动缓存 ——
        // 一次拉取失败不该把用户正在打的字顺带清掉。
        // 已知代价：工具把某 input 暂时拿掉再放回 ⇒ 该 key 视为重新首次出现（值复位）。
        foreach (var stale in _inputValues.Keys
                     .Where(k => !renderedInputKeys.Contains(k))
                     .ToList())
        {
            _inputValues.Remove(stale);
        }
    }

    /// <summary>协议 §3.4 的 6 种节点类型 + V1.2 <c>image</c> + V1.3 <c>input</c>。未知 type 跳过并提示（**不报错、不白屏**）。</summary>
    private void RenderNode(JsonNode? node, IReadOnlyList<string>? declaredCommands, HashSet<string> renderedInputKeys)
    {
        if (node is not JsonObject obj)
        {
            AddNotice("⚠ 跳过一个非法节点（不是对象）", isError: false);
            return;
        }

        var type = obj["type"]?.GetValue<string>();
        switch (type)
        {
            case "heading":
                Body(new TextBlock
                {
                    Text = Str(obj["text"]),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 6),
                });
                break;

            case "text":
                Body(new TextBlock
                {
                    Text = Str(obj["text"]),
                    TextWrapping = Bool(obj["wrap"], defaultValue: true)
                        ? TextWrapping.Wrap
                        : TextWrapping.NoWrap,
                    Margin = new Thickness(0, 2, 0, 6),
                });
                break;

            case "kv":
                RenderKv(obj["items"] as JsonArray);
                break;

            case "list":
                RenderList(obj["items"] as JsonArray);
                break;

            case "buttons":
                RenderButtons(obj["items"] as JsonArray);
                break;

            case "separator":
                Body(new Separator { Margin = new Thickness(0, 10, 0, 10) });
                break;

            case "image":
                RenderImage(obj);
                break;

            case "input":
                RenderInput(obj, declaredCommands, renderedInputKeys);
                break;

            default:
                AddNotice($"⚠ 未知节点类型 {type ?? "(无 type)"}（宿主版本过旧？）", isError: false);
                break;
        }
    }

    // ── image 节点（协议 §3.6，V1.2 增量）──────────────────────────────────

    /// <summary>图片节点的显示高度默认值与钳制区间（协议 §3.6.1）。</summary>
    private const int ImageDefaultMaxHeight = 320;
    private const int ImageMinMaxHeight = 32;
    private const int ImageMaxMaxHeight = 1200;

    /// <summary>单张图片的读取上限（协议 §3.6.2 的 S4）。超过则不读盘。</summary>
    private const long ImageMaxBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 渲染 <c>image</c> 节点。
    ///
    /// **安全边界（协议 §3.6.2）在这里逐条兑现**，其中 S2（不许远程）是本节的关键：
    /// <c>BitmapImage.UriSource</c> 原生支持 <c>http(s)://</c>，若把工具给的 <c>path</c>
    /// 直接透传给它，一个工具就能让**宿主替它发网络请求** —— 既绕过工具自己的
    /// <c>network</c> 能力闸门，也把"这台机器在看什么图"泄漏出去。
    /// 所以这里**只走 <c>StreamSource</c> + 自己打开的 <c>FileStream</c>**，
    /// 从 API 选择上就让远程加载不可能发生 —— 白名单不是靠"检查字符串前缀"，
    /// 而是靠"根本没有那条代码路径"。
    ///
    /// 与全项目一致：**任何失败都降级成 text 节点**（协议 §3.6.3），
    /// 且原因分类给（格式坏 / 太大 / 路径非法），不合并成一句"读取失败"。
    /// </summary>
    private void RenderImage(JsonObject obj)
    {
        var raw = Str(obj["path"]);
        var alt = Str(obj["alt"]) is { Length: > 0 } a
            ? a
            : (raw is { Length: > 0 } ? Path.GetFileName(raw) : "（未提供 path）");

        if (raw.Length == 0)
        {
            AddNotice($"⚠ 无法显示图片（image 节点缺少 path）：{alt}", isError: false);
            return;
        }

        // S2：远程协议一律拒绝 —— 注意这里**只是给出原因文案**，
        // 真正的防线是下面不用 UriSource / 不 new Uri(remote)。两道一起才稳。
        if (LooksRemote(raw))
        {
            AddNotice($"⚠ 无法显示图片（不支持远程地址，只接受本地文件）：{alt}", isError: false);
            return;
        }

        var path = raw;
        try
        {
            path = Path.GetFullPath(raw);
        }
        catch (Exception)
        {
            AddNotice($"⚠ 无法显示图片（路径非法）：{alt}", isError: false);
            return;
        }

        // S3：目录 / 不存在 / 读不到大小
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (info.Exists && (info.Attributes & FileAttributes.Directory) != 0)
            {
                AddNotice($"⚠ 无法显示图片（那是一个目录）：{alt}", isError: false);
                return;
            }

            if (!info.Exists)
            {
                AddNotice($"⚠ 无法显示图片（文件不存在）：{alt}", isError: false);
                return;
            }
        }
        catch (Exception ex)
        {
            AddNotice($"⚠ 无法显示图片（{ex.GetType().Name}）：{alt}", isError: false);
            return;
        }

        // S4：体积上限。为什么要单独一档原因：用户对此的动作是"用系统程序打开"，
        // 与"格式不受支持"（动作是换文件）完全不同，混成一句话等于没有信息。
        if (info.Length > ImageMaxBytes)
        {
            AddNotice(
                $"⚠ 图片过大（{info.Length / (1024.0 * 1024.0):F1} MB > 32 MB），未预览：{alt}",
                isError: false);
            return;
        }

        var maxHeight = ClampImageHeight(obj["maxHeight"]);

        // S5：OnLoad ⇒ 读盘在**构造时**一次完成（不会到绘制阶段才碰文件）；
        //     Freeze ⇒ 冻结成纯数据，布局不再触发重解码。
        var bitmap = new BitmapImage();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 读盘一次完成
            bitmap.StreamSource = stream;                    // ★ 不走 UriSource —— 见方法注释 S2
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        catch (Exception)
        {
            // 解码失败 / 格式不受支持 / 文件被独占锁住，都会到这里。
            // 不区分到更细：对用户来说下一步动作都是同一个（用系统程序打开）。
            AddNotice($"⚠ 无法显示图片（格式不受支持或文件损坏）：{alt}", isError: false);
            return;
        }

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,  // 小图**不放大**（放大只会糊）
            MaxHeight = maxHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 8),
            // 让看图的人知道"点开能看到更大的" —— 这是 V1 唯一的放大途径（协议 §11 第 8 条）
            ToolTip = $"{alt}\n{bitmap.PixelWidth}×{bitmap.PixelHeight} · 双击可用系统程序打开",
            Tag = path,
        };

        // 双击 → 交给系统默认图片查看器（复用 openExternal 的语义，但宿主自己就能做，
        // 不必绕一圈回工具 —— 这里打开的是**宿主自己已经校验过的**那个路径）。
        image.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                TryOpenExternally(path, alt);
            }
        };

        Body(image);
    }

    /// <summary>最大显示高度的钳制（协议 §3.6.1：默认 320，区间 [32, 1200]）。</summary>
    private static double ClampImageHeight(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<double>(out var d) || double.IsNaN(d))
        {
            return ImageDefaultMaxHeight;
        }

        return Math.Clamp(d, ImageMinMaxHeight, ImageMaxMaxHeight);
    }

    /// <summary>判断是否像远程地址。<b>只是给原因文案用</b>，真正的防线是不使用 UriSource。</summary>
    private static bool LooksRemote(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(@"\\", StringComparison.Ordinal);   // UNC：同样不是"本地单机路径"
    }

    /// <summary>用系统默认程序打开。失败只记日志 + 状态栏，不弹框（面板的既有约束）。</summary>
    private void TryOpenExternally(string path, string alt)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            _status.Text = $"已交给系统程序打开：{alt}（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"打开图片失败（{alt}）：{ex.Message}", "panel");
            _status.Text = $"打开失败：{ex.Message}";
        }
    }

    // ── input 节点（协议 §3.7，V1.3 增量）──────────────────────────────────

    /// <summary>
    /// 渲染 <c>input</c> 节点。
    ///
    /// **值的所有权（§3.7.1 / §3.7.6）**：节点只声明"这里有个叫什么名字的输入框"；
    /// 用户输入由宿主持有（<see cref="_inputValues"/>）并随下一次 <c>tool.panel.data</c>
    /// 回传。<c>value</c> 字段**只在 key 首次出现时生效** —— 整体重绘若把节点
    /// <c>value</c>（如 <c>""</c>）直接盖回控件，会抹掉用户正在打的字
    /// （21.6 守卫的"最刺眼的可用性 bug"；W3 计划 R18：此 bug 由"整体重绘 × 缓存缺失"
    /// 两模块组合产生，各自看都对，代码审查极难发现）。
    ///
    /// **失焦 = 会话结束（§3.7.4 F2）**：<see cref="HandleWindowBlur"/> 清空缓存与控件，
    /// 之后的重绘里节点 <c>value</c> 重新生效（新会话）。
    ///
    /// 降级口径与 CLI 完全一致（§3.7.6 / 21.8）：缺 key 跳过、重复 key 保留首个、
    /// <c>submitCommandId</c> 未声明 ⇒ **照渲染 + 警告**。
    /// </summary>
    private void RenderInput(
        JsonObject obj,
        IReadOnlyList<string>? declaredCommands,
        HashSet<string> renderedKeys)
    {
        var key = Str(obj["key"]);
        if (key.Length == 0)
        {
            AddNotice("⚠ 跳过一个 input 节点（缺少 key —— 协议必填）", isError: false);
            return;
        }

        if (!renderedKeys.Add(key))
        {
            AddNotice($"⚠ 跳过一个 input 节点（key 重复：{key}，保留首个）", isError: false);
            return;
        }

        // 首现规则（§3.7.6 / 21.6）：key 已在值缓存 ⇒ 忽略节点 value（重绘不吞字）；
        // 不在 ⇒ 本次是首次出现，初始值生效一次。
        if (!_inputValues.TryGetValue(key, out var current))
        {
            current = Str(obj["value"]);
            _inputValues[key] = current;
        }

        var placeholder = Str(obj["placeholder"]);
        var submitCommandId = Str(obj["submitCommandId"]);

        // Text 在构造时设好、**之后**才接 TextChanged —— 初始值不产生"变更"事件。
        // Tag 带 key：事件处理器靠它找回回传标识。
        var box = new TextBox
        {
            Text = current,
            Tag = key,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // WPF 原生 TextBox 没有 placeholder —— 用一层不挡鼠标的灰字叠在框上
        // （为省一个依赖或不存在的附加属性 API，自己画是最稳的）。
        var hint = new TextBlock
        {
            Text = placeholder,
            IsHitTestVisible = false,
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            Visibility = current.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        if (submitCommandId.Length > 0)
        {
            // 引用校验（21.8，口径同 CLI）：未声明 ⇒ 警告 + 照渲染，不静默。
            if (declaredCommands is { Count: > 0 } && !declaredCommands.Contains(submitCommandId))
            {
                AddNotice(
                    $"⚠ input 的 submitCommandId 未在工具的 contributes.commands 里声明：{submitCommandId}",
                    isError: false);
            }

            box.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter)
                {
                    return;
                }

                e.Handled = true;
                await RunSubmitAsync(submitCommandId);
            };
        }

        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            OnInputTextChanged(box);
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.Children.Add(box);
        grid.Children.Add(hint);

        _inputBoxes.Add(box);
        Body(grid);
    }

    /// <summary>
    /// 输入值变更的**唯一入口**（真实 <c>TextChanged</c> 与自检模拟同路 —— 自检的可信度来源）。
    ///
    /// 节流（§3.7.3）：<see cref="InputThrottle.Report"/> 立即放行 ⇒ 马上拉一次；
    /// 排队 ⇒ 到点由 <see cref="ScheduleInputFire"/> 拉。无论哪种，请求里的快照都从
    /// <see cref="_inputValues"/> **现取** —— 排队期间被合并的中途值不丢，最新值必然随下一次拉取送达。
    /// </summary>
    private void OnInputTextChanged(TextBox box)
    {
        if (_suppressInputEvents || box.Tag is not string key)
        {
            return;
        }

        _inputValues[key] = box.Text;

        if (_throttle.Report(key, box.Text) is not null && !_selfCheck)
        {
            _ = ReloadAsync("refresh");   // 输入触发的拉取 reason=refresh（协议 §3.7.3 触发表/示例）
        }
    }

    /// <summary>
    /// 节流器的到点调度（<see cref="InputThrottle"/> 的 schedule 钩子，契约层不引 WPF 的兑现点）。
    /// 单发 DispatcherTimer：到点 <see cref="InputThrottle.Fire"/>，交付非 null ⇒ 拉一次。
    /// 计时精度导致提前触发时 <see cref="InputThrottle.Fire"/> 会自己重新登记 —— 见契约层注释。
    /// </summary>
    private void ScheduleInputFire(long delayMs)
    {
        if (_closed || _selfCheck)
        {
            return;   // 自检窗口不真的到点拉取 —— 拉取由 TrayApplication 显式编排
        }

        _inputTimer?.Stop();
        _inputTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)),
        };
        _inputTimer.Tick += (_, _) =>
        {
            _inputTimer?.Stop();
            _inputTimer = null;

            if (_throttle.Fire() is not null)
            {
                _ = ReloadAsync("refresh");
            }
        };
        _inputTimer.Start();
    }

    /// <summary>
    /// 窗口失焦（§3.7.4 F1/F2 的兑现点）：停止上报 + 清空全部已缓存输入。
    ///
    /// 为什么连**控件里的字**一起清：缓存清掉后，下一次重绘按"key 首次出现"重新初始化
    /// （节点 value 生效）—— 若控件留旧字而缓存为空，两者不一致，且下一次重绘照样抹掉它。
    /// 显式清空比"看起来还在、其实已经不管了"诚实。代价是 alt-tab 回来输入没了 ——
    /// 这是"后台不回传、不留存"隐私保证的既定代价，协议 §3.7.4 明文接受。
    ///
    /// <see cref="_suppressInputEvents"/>：清空控件触发的 TextChanged 不得反过来进节流器
    /// （否则失焦会"上报"一次空值 —— 正是要避免的事）。
    ///
    /// F3~F5 的兑现：值只活在 <see cref="_inputValues"/>（内存）里 —— 本方法之外
    /// 没有任何日志/持久化路径碰它（自检输出用的是**固定夹具串**，不算用户数据）。
    /// </summary>
    private void HandleWindowBlur()
    {
        _inputTimer?.Stop();
        _inputTimer = null;
        _inputValues.Clear();
        _throttle.Reset();

        _suppressInputEvents = true;
        try
        {
            foreach (var box in _inputBoxes)
            {
                box.Text = "";
            }
        }
        finally
        {
            _suppressInputEvents = false;
        }
    }

    /// <summary>
    /// Enter 提交（§3.7.1 的 <c>submitCommandId</c>）：走 <c>tool.invoke</c>（同按钮通道，
    /// args 带 <c>panelId</c>），完成后重拉一次让工具按最新 inputs 重画结果。
    /// 注意 F3：这里不记输入值，只记命令与结果。
    /// </summary>
    private async Task RunSubmitAsync(string commandId)
    {
        if (_busy || _closed)
        {
            return;
        }

        _busy = true;
        _refreshButton.IsEnabled = false;
        _status.Text = $"正在执行 {commandId}…";

        try
        {
            var args = new JsonObject { ["panelId"] = _panel.Id };
            var result = await _host.Processes.InvokeCommandAsync(commandId, args);
            var text = result.Result is null ? "（无返回值）" : result.Result.ToJsonString();
            _host.Log.Info($"面板 {_tool.Id}.{_panel.Id} 提交 {commandId} 完成：{text}", "panel");
        }
        catch (Exception ex)
        {
            _host.Log.Warn($"面板提交 {commandId} 失败：{ex.Message}", "panel");
            _status.Text = $"提交失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                _refreshButton.IsEnabled = true;
            }
        }

        if (!_closed)
        {
            await ReloadAsync("refresh");
        }
    }

    /// <summary>
    /// 发起一次面板数据拉取 —— **请求构造的单一来源**（含 <c>inputs</c> 快照）。
    ///
    /// <b>为什么 public 且自检也必须走它</b>：21.3 的验收目标是"宿主请求真的带着快照"。
    /// 若自检绕过本方法自己调 <c>PanelDataAsync</c>，生产路径的请求构造就**零断言覆盖**
    /// —— 实测突变 W-M4（拉取点改传 null）没被抓，因为自检走的是自己的调用点
    /// （S14 的形状：契约写在断言没覆盖的路上）。收敛成单一来源后，
    /// "不带快照"的突变同时打断生产与自检两条路。
    /// </summary>
    public Task<PanelDataResult> PullAsync(string reason) =>
        _host.Processes.PanelDataAsync(_tool.Id, _panel.Id, reason, BuildInputsSnapshot());

    /// <summary>
    /// 当前 <c>inputs</c> 快照（§3.7.2：**发送那一刻**全部 input 的当前值，快照语义）。
    /// 失焦后缓存为空 ⇒ 自然退化成空对象（21.4 —— F1 的结构性兑现）。
    /// 交给 <c>PanelDataArgs.Build</c> 时会被 Clone —— 本对象不与渲染路径共享父节点。
    /// </summary>
    private JsonObject BuildInputsSnapshot()
    {
        var snapshot = new JsonObject();
        foreach (var (key, value) in _inputValues)
        {
            snapshot[key] = value;
        }

        return snapshot;
    }

    private void RenderKv(JsonArray? items)
    {
        if (items is null)
        {
            AddNotice("⚠ kv 节点缺少 items", isError: false);
            return;
        }

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        foreach (var pair in items)
        {
            if (pair is not JsonArray p || p.Count < 2)
            {
                continue;   // 节点级容错：跳过形状不对的条目（协议 §3.5）
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var key = new TextBlock
            {
                Text = Str(p[0]),
                Opacity = 0.62,
                Margin = new Thickness(0, 2, 14, 2),
            };
            var value = new TextBlock
            {
                Text = Str(p[1]),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            };

            Grid.SetRow(key, row);
            Grid.SetColumn(key, 0);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(key);
            grid.Children.Add(value);
            row++;
        }

        if (row > 0)
        {
            Body(grid);
        }
    }

    private void RenderList(JsonArray? items)
    {
        if (items is null)
        {
            AddNotice("⚠ list 节点缺少 items", isError: false);
            return;
        }

        foreach (var item in items)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 1, 0, 1),
            };
            row.Children.Add(new TextBlock
            {
                Text = "•",
                Width = 14,
                Opacity = 0.7,
            });
            row.Children.Add(new TextBlock
            {
                Text = Str(item is JsonArray inner && inner.Count > 0 ? inner[0] : item),
                TextWrapping = TextWrapping.Wrap,
            });
            Body(row);
        }

        Body(new Border { Height = 6 });   // 列表后留白，与下一块分隔
    }

    /// <summary>
    /// 按钮（协议 §4）：点击 → <c>tool.invoke</c>，<c>args</c> 带 <c>panelId</c>，
    /// 完成后**自动重拉**（这是 V1 的"局部刷新替代品"）。
    /// <c>style: "danger"</c> 只改配色，**不加二次确认** —— 确认属工具 handler 的业务判断（§4.3）。
    /// </summary>
    private void RenderButtons(JsonArray? items)
    {
        if (items is null)
        {
            AddNotice("⚠ buttons 节点缺少 items", isError: false);
            return;
        }

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        var any = false;

        foreach (var item in items)
        {
            if (item is not JsonObject b)
            {
                continue;
            }

            var commandId = Str(b["commandId"]);
            if (string.IsNullOrWhiteSpace(commandId))
            {
                continue;   // 没有 commandId 的按钮点了也没事发生，不渲染（协议 §3.4 必需字段）
            }

            var isDanger = string.Equals(Str(b["style"]), "danger", StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = string.IsNullOrWhiteSpace(Str(b["label"])) ? commandId : Str(b["label"]),
                Margin = new Thickness(0, 0, 8, 6),
                MinWidth = 76,
                Padding = new Thickness(12, 4, 12, 4),
                Tag = commandId,
            };

            if (isDanger)
            {
                button.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            }

            button.Click += async (_, _) => await RunButtonAsync(button, commandId);
            row.Children.Add(button);
            any = true;
        }

        if (any)
        {
            Body(row);
        }
    }

    private async Task RunButtonAsync(Button button, string commandId)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        button.IsEnabled = false;
        _refreshButton.IsEnabled = false;
        _status.Text = $"正在执行 {commandId}…";

        try
        {
            // args 带 panelId：让工具知道这是从哪个面板点过来的（协议 §4.1 第 2 条）
            var args = new JsonObject { ["panelId"] = _panel.Id };
            var result = await _host.Processes.InvokeCommandAsync(commandId, args);

            var text = result.Result is null ? "（无返回值）" : result.Result.ToJsonString();
            _host.Log.Info($"面板 {_tool.Id}.{_panel.Id} 按钮 {commandId} 完成：{text}", "panel");

            _busy = false;                  // 先放开，才能重拉（否则被 _busy 挡住）
            if (!_closed)
            {
                button.IsEnabled = true;
                _refreshButton.IsEnabled = true;
                await ReloadAsync("refresh");   // 完成后自动重拉（协议 §4.1 第 4 条）
            }

            return;
        }
        catch (Exception ex)
        {
            _host.Log.Error($"面板按钮 {commandId} 调用失败：{ex.Message}", "panel");
            ShowError("按钮命令执行失败", ex.Message);
        }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                button.IsEnabled = true;
                _refreshButton.IsEnabled = true;
            }
        }
    }

    // ── 渲染辅助 ────────────────────────────────────────────────────────────

    private void Body(UIElement element) => _body.Children.Add(element);

    private void AddNotice(string text, bool isError, bool monospace = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 3),
            Opacity = isError ? 1.0 : 0.75,
            FontSize = isError ? 13 : 12,
        };

        if (isError)
        {
            block.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        }

        if (monospace)
        {
            block.FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace");
            block.FontSize = 12;
        }

        Body(block);
    }

    /// <summary>把"工具返回了什么形状"落成一句话（与 CLI 的 dataShape 同口径）。</summary>
    private static string Shape(JsonNode? data) => data switch
    {
        null => "null",
        JsonObject o => o["nodes"] is JsonArray ? "带 nodes 的对象" : "不带 nodes 的对象",
        JsonArray => "数组",
        JsonValue => "标量",
        _ => "未知",
    };

    private static int CountNodes(JsonNode? data) =>
        data is JsonObject o && o["nodes"] is JsonArray nodes ? nodes.Count : 0;

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static bool Bool(JsonNode? node, bool defaultValue) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : defaultValue;

    private void ShowError(string title, string detail)
    {
        _body.Children.Clear();
        AddNotice($"⚠ {title}", isError: true);
        AddNotice(detail, isError: false);
        _status.Text = $"{title}（{DateTime.Now:HH:mm:ss}）";
    }

    // ── 自检（协议 §9 注：GUI 断言由 --selfcheck 覆盖，不显示窗口）────────────

    /// <summary>
    /// 装配控件树但**不显示窗口**，返回可断言的渲染清单。
    ///
    /// 为什么需要它：`acceptance.sh` 里不能开 GUI，但"协议通"不等于"WPF 真的画出来了"。
    /// 这个方法把"渲染"变成纯数据操作（WPF 控件树可以在没有窗口句柄时构造），
    /// 于是"节点数 / 控件类型 / 按钮数"都能进自动化。
    /// </summary>
    public IReadOnlyList<string> InventoryForSelfCheck(
        JsonNode? payload,
        IReadOnlyList<string>? declaredCommands = null)
    {
        _selfCheck = true;   // 自检窗口绝不自动拉取 —— 拉取由 TrayApplication 显式编排
        _body.Children.Clear();
        RenderPayload(payload, declaredCommands);

        var lines = new List<string>();
        lines.Add($"面板清单 {_tool.Id}.{_panel.Id}: 尺寸 {_panel.Width}x{_panel.Height} · "
                  + $"刷新 {(_panel.RefreshMs == 0 ? "手动" : _panel.RefreshMs + "ms")}");

        // 递归数出控件类型分布 —— 断言"画了几个控件"比"没抛异常"强得多
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        CountControls(_body, counts);
        lines.Add($"面板控件 {_tool.Id}.{_panel.Id}: "
                  + string.Join(", ", counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                      .Select(kv => $"{kv.Key}={kv.Value}")));

        var buttons = FindControls<Button>(_body)
            .Where(b => b.Tag is string)
            .Select(b => $"{(string)b.Tag}")
            .ToList();
        lines.Add($"面板按钮 {_tool.Id}.{_panel.Id}: {string.Join(", ", buttons)}");

        // 面板里的**提示文案**（⚠ 开头的那些）。
        //
        // 为什么要单独报出来：只数"画了几个 TextBlock"的话，断言分不清
        // 「远程地址被拒绝」和「路径恰好打不开」—— 两者都只是一个 TextBlock。
        // 而这两件事的**下一步动作完全不同**（一个是工具写错了，一个是文件没了），
        // 也是协议 §3.6.3 明确要求分开的三类降级原因。
        // 只报条数 = 断言只能证明"没崩"，报出正文才能证明"降级到了正确的那一条"。
        var notices = FindControls<TextBlock>(_body)
            .Select(t => t.Text)
            .Where(t => t.StartsWith("⚠", StringComparison.Ordinal))
            .ToList();
        lines.Add($"面板提示 {_tool.Id}.{_panel.Id}: {string.Join(" || ", notices)}");

        return lines;
    }

    // ── input 节点自检（协议 §3.7.8 的 21.2~21.6 WPF 侧）────────────────────

    /// <summary>
    /// 【自检专用】在当前渲染的输入框里模拟一次打字：直接给 <c>TextBox.Text</c> 赋值，
    /// 走**真实** <c>TextChanged</c> 路径（缓存更新 + 节流上报与生产完全同路 ——
    /// 这是自检可信度的来源：测的就是生产路径，不是另一套模拟逻辑），
    /// 然后返回当前 inputs 快照。key 不存在 ⇒ 快照原样返回（调用方断言"没进去"）。
    ///
    /// 为什么值用固定夹具串：F3 禁止把用户输入写进诊断 —— 自检值是常量，不是用户数据。
    /// </summary>
    public JsonObject SimulateInputForSelfCheck(string key, string value)
    {
        var box = _inputBoxes.FirstOrDefault(b =>
            string.Equals(b.Tag as string, key, StringComparison.Ordinal));
        if (box is not null)
        {
            box.Text = value;
        }

        return SnapshotForSelfCheck();
    }

    /// <summary>【自检专用】当前 inputs 快照（与请求实际携带的同一来源：<see cref="BuildInputsSnapshot"/>）。</summary>
    public JsonObject SnapshotForSelfCheck() => BuildInputsSnapshot();

    /// <summary>【自检专用】当前各输入框的**可见文本**（21.6 断"重绘后框里还是用户打的字"用）。</summary>
    public JsonObject BoxValuesForSelfCheck()
    {
        var o = new JsonObject();
        foreach (var box in _inputBoxes)
        {
            if (box.Tag is string key)
            {
                o[key] = box.Text;
            }
        }

        return o;
    }

    /// <summary>【自检专用】渲染区全部非空文本（回显断言用 —— 工具把收到的 inputs 画进了 text 节点）。</summary>
    public string TextContentForSelfCheck() =>
        string.Join(" || ", FindControls<TextBlock>(_body)
            .Select(t => t.Text)
            .Where(t => t.Length > 0));

    /// <summary>【自检专用】模拟窗口失焦（与真实 <c>Deactivated</c> 同一处理器）。</summary>
    public void BlurForSelfCheck() => HandleWindowBlur();

    /// <summary>【自检专用】节流器是否还有待发变更（失焦后必须为 false —— F2 守卫）。</summary>
    public bool HasPendingInputForSelfCheck => _throttle.HasPending;

    /// <summary>
    /// 统计**我构造的**控件（走逻辑树），刻意不走可视化树。
    ///
    /// 为什么不用 <see cref="VisualTreeHelper"/>：未 <c>Show()</c> 的窗口没有可视化树，
    /// 而自检恰恰必须不开窗口（协议 §9 注）；就算强行 Measure 出来，模板展开的
    /// 内部零件（Border/ContentPresenter/ScrollViewer 的子孙）也会混进计数，
    /// 于是"2 个按钮"变成"8 个 Button"—— 断言数字就失去意义了。
    /// 逻辑树数的是"我放了什么"，这正是自检想断言的。
    /// </summary>
    private static void CountControls(DependencyObject root, Dictionary<string, int> counts)
    {
        var name = root.GetType().Name;
        counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;

        foreach (var child in LogicalChildren(root))
        {
            CountControls(child, counts);
        }
    }

    private static IEnumerable<T> FindControls<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalChildren(root))
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindControls<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>取逻辑子元素。<c>Panel</c> / <c>Decorator</c> / <c>ContentControl</c> 三类覆盖本窗口用到的全部容器。</summary>
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject root)
    {
        switch (root)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    yield return child;
                }

                break;

            case Decorator { Child: { } decorated }:
                yield return decorated;
                break;

            case ContentControl { Content: DependencyObject content }:
                yield return content;
                break;
        }
    }
}
