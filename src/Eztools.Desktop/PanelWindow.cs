using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        Closed += (_, _) =>
        {
            _closed = true;
            _timer?.Stop();
            _timer = null;
            _eventSubscription?.Dispose();
            _eventSubscription = null;
        };
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
    private async Task ReloadAsync(string reason)
    {
        if (_busy || _closed)
        {
            return;
        }

        _busy = true;
        _refreshButton.IsEnabled = false;
        _status.Text = "正在拉取…";

        try
        {
            var result = await _host.Processes.PanelDataAsync(_tool.Id, _panel.Id, reason);

            if (_closed)
            {
                return;
            }

            _body.Children.Clear();
            RenderPayload(result.Data);

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
        }
    }

    /// <summary>
    /// 把工具返回的载荷渲染进 <see cref="_body"/>。
    ///
    /// 容错分层（协议 §3.5）：**载荷级**错误显示整块提示；**节点级**错误跳过该节点。
    /// 两级分开的意义：一个坏节点不该让整块面板白屏，但工具返回了非对象时也确实无内容可画。
    /// </summary>
    private void RenderPayload(JsonNode? data)
    {
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

        foreach (var node in nodes)
        {
            try
            {
                RenderNode(node);
            }
            catch (Exception ex)
            {
                // 节点级容错：单个节点渲染异常 → 跳过它（其余照画），并留下可追查的痕迹
                _host.Log.Warn(
                    $"面板 {_tool.Id}.{_panel.Id} 的节点渲染失败（已跳过）：{ex.Message}", "panel");
                AddNotice($"⚠ 一个节点渲染失败（{ex.GetType().Name}），已跳过", isError: false);
            }
        }
    }

    /// <summary>协议 §3.4 的 6 种节点类型。未知 type 跳过并提示（**不报错、不白屏**）。</summary>
    private void RenderNode(JsonNode? node)
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

            default:
                AddNotice($"⚠ 未知节点类型 {type ?? "(无 type)"}（宿主版本过旧？）", isError: false);
                break;
        }
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
    public IReadOnlyList<string> InventoryForSelfCheck(JsonNode? payload)
    {
        _body.Children.Clear();
        RenderPayload(payload);

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

        return lines;
    }

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
