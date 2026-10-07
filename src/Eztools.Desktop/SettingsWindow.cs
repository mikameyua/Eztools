// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Arbitration;
using Eztools.Host.Config;
using Eztools.Host.Registry;

namespace Eztools.Desktop;

/// <summary>
/// P1b 设置窗口：schema 驱动渲染（设计方案 §7 的映射表在这里落地）。
///
/// 设计要点：
/// 1. **零工具专属代码** —— 窗口只认识 <see cref="ConfigSchema"/> / <see cref="ConfigField"/>，
///    新工具声明了 config 就自动出现在这里，本文件一行不改（与 P0 的"新增工具不改宿主"同一条标准）。
/// 2. **读写都走 ConfigStore**（P1a）—— 校验、默认值合并、原子写、损坏恢复全部复用，
///    窗口只负责"控件 ↔ 字符串"的翻译。CLI 的 <c>ezt config set</c> 与本窗口改的是同一个文件。
/// 3. **WPF 跑在 WinForms 消息循环上**（托盘是 WinForms 的）—— 用 <c>ShowDialog</c> 的模态泵，
///    这是两栈互操作里最稳的一条路（探针实测共存成立）。
/// 4. **宿主设置节**（W4-c，FR-9）—— 构造时可选传入一个 <see cref="HostSettingsSection"/>
///    （桌面宿主的热键 / OCR 语言），渲染与保存与工具配置走**同一条** ConfigStore 链路；
///    保存成功后经回调通知托盘重读/重注册热键（"改键立即生效"）。
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>
    /// 宿主设置节描述：固定 id（进 ConfigStore 的"伪工具"）+ 显示名 + schema 工厂。
    /// schema 用工厂而不是实例 —— JsonNode 挂过父不能复用，每次取新副本最稳（§四.4）。
    /// </summary>
    public sealed record HostSettingsSection(string Id, string Title, Func<JsonObject> SchemaFactory);

    private readonly EztoolsHost _host;
    private readonly HostSettingsSection? _hostSection;
    private readonly Action? _hostSettingsSaved;
    private readonly ListBox _toolList;
    private readonly StackPanel _fieldPanel;
    private ScrollViewer? _fieldScroller;
    private readonly TextBlock _header;
    private readonly TextBlock _hint;
    private readonly Button _saveButton;
    private readonly Button _resetButton;
    private RegisteredTool? _selected;
    private HostSettingsSection? _activeHostSection;

    // ── 方案 B/C（2026-09-30）：导航、dirty 与热键中心状态 ──
    /// <summary>热键中心页的虚拟组键（不是 schema 里的 x-group，是渲染层的聚合视图）。</summary>
    internal const string HotkeysGroupKey = "__hotkeys__";

    /// <summary>当前渲染页的宿主组（含热键中心）；工具页时为 null。</summary>
    private HostGroupItem? _hostGroup;
    /// <summary>渲染时记录的工具热键行（command → 工具 id / 默认组合键 / 当前生效值），保存时消费。</summary>
    private readonly Dictionary<string, (string ToolId, string Default)> _toolHotkeyRows = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A3：工具热键行的状态文本控件（command → 状态 Label），冲突重算时更新。宿主行无状态标签不进表。</summary>
    private readonly Dictionary<string, TextBlock> _hotkeyStatusLabels = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>dirty 标记：任何编辑器变更置位；保存/放弃/重渲染清除。标题随之加「未保存」。</summary>
    private bool _dirty;
    private string? _baseHeaderText;
    /// <summary>
    /// 程序化选中（恢复选中项）时挂上，防 SelectionChanged 递归。
    /// </summary>
    private bool _suppressSelectionGuard;
    /// <summary>
    /// 探针 / 自检模式：跳过"未保存确认"弹窗 —— headless 下模态确认无人点确定，
    /// ProbeSetField/ProbeSaveAsync/InventoryForSelfCheck 的程序化切节会挂死（§六-3）。
    /// </summary>
    internal bool SuppressDirtyConfirm { get; set; }

    // ── A4：配置文件路径一键打开 ──
    private TextBlock? _openPathLink;
    private string? _currentConfigPath;

    public SettingsWindow(EztoolsHost host, HostSettingsSection? hostSection = null, Action? hostSettingsSaved = null)
    {
        _host = host;
        _hostSection = hostSection;
        _hostSettingsSaved = hostSettingsSaved;

        Title = "Eztools 设置";
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        ApplyModernTheme();

        _toolList = new ListBox { Width = 190, Margin = new Thickness(8) };
        _toolList.SelectionChanged += OnListSelectionChanged;

        // C4：关窗时若有未保存修改，确认拦截（探针/自检窗口从不 Show/Close，不受影响）。
        Closing += (_, e) =>
        {
            if (!ConfirmDiscardIfDirty())
            {
                e.Cancel = true;
            }
        };

        _header = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        _hint = new TextBlock { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        _fieldPanel = new StackPanel();

        _saveButton = new Button { Content = "保存", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        _saveButton.Click += (_, _) => Save();
        _resetButton = new Button { Content = "恢复默认", MinWidth = 88 };
        _resetButton.Click += (_, _) => ResetToDefaults();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(_saveButton);
        buttons.Children.Add(_resetButton);

        var right = new DockPanel { Margin = new Thickness(10, 12, 14, 12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(buttons);
        DockPanel.SetDock(_header, Dock.Top);
        right.Children.Add(_header);
        DockPanel.SetDock(_hint, Dock.Top);
        right.Children.Add(_hint);
        // A4：配置文件路径一键打开（explorer /select）—— 仅在有配置路径的页可见。
        var openPathLink = MakeOpenPathLink();   // 先建（内部赋 _openPathLink），再 SetDock
        DockPanel.SetDock(openPathLink, Dock.Top);
        right.Children.Add(openPathLink);
        // ★ 滚动容器必须**直接**吃 DockPanel 的剩余高度（LastChildFill 默认 true）——
        //   不能包进 StackPanel：StackPanel 给子元素的测量高度是无限，ScrollViewer 拿到
        //   无限可用高度就永不裁剪、永不出现滚动条，内容超出窗口的部分直接被裁掉
        //   （用户实测：宿主节字段加到 11 个后，底部的 pick.hotkey / color.format 不可达）。
        _fieldScroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _fieldPanel };
        right.Children.Add(_fieldScroller);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_toolList, 0);
        Grid.SetColumn(right, 1);
        root.Children.Add(_toolList);
        root.Children.Add(right);

        Content = root;
        Loaded += (_, _) => PopulateToolList();
    }

    /// <summary>
    /// 挂上 iNKORE.UI.WPF.Modern 的主题资源：控件获得 Fluent 外观，
    /// 不挂的话 WPF 用 2006 年的默认皮肤。
    ///
    /// ⚠️ 三步都必要（都是实测出来的，见设计文档 §18.7）：
    /// ① Window 级合并 —— 控件外观的来源；
    /// ② App 级合并 —— 官方文档要求的标准挂法；下拉 Popup 展开时的资源解析会走 Application 链；
    /// ③ 预播 <c>OverlayCornerRadius</c> —— iNKORE 0.10.2.1 的 ComboBoxHelper.UpdateCornerRadius
    ///    在下拉展开时会 TryFindResource 这个键，**它自己的 ThemeResources 字典里居然没有**，
    ///    返回 null 后拆箱直接 NRE（症状：点开下拉整个窗口卡死/崩溃，探针实测复现+修复验证）。
    /// </summary>
    private void ApplyModernTheme()
    {
        // winforms 消息循环模式下，走到这里时 Application 可能还没被自动创建；
        // 用 ?? 合并，分析器能沿 app 局部变量推断非空（if-is-null 的写法它推不出来，会报 CS8602）
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.ThemeResources());
        Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.Controls.XamlControlsResources());
        // ThemeManager.Current 标注可空（首次访问时才初始化）；挂完 App 级资源后必然就绪，
        // 但 `!` 在这条链上压不住 CS8602（实测），用显式空检查最稳
        var themeManager = iNKORE.UI.WPF.Modern.ThemeManager.Current;
        if (themeManager is not null)
        {
            themeManager.ApplicationTheme = iNKORE.UI.WPF.Modern.ApplicationTheme.Light;
        }

        // ② App 级合并（去重：多个设置窗口实例不重复挂）
        var appRes = app.Resources;
        if (!appRes.MergedDictionaries.OfType<iNKORE.UI.WPF.Modern.ThemeResources>().Any())
        {
            appRes.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.ThemeResources());
            appRes.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.Controls.XamlControlsResources());
        }

        // ③ 预播缺失键（只在确认缺失时补默认值，不覆盖库自己的定义）
        const string overlayKey = "OverlayCornerRadius";
        if (!appRes.Contains(overlayKey))
        {
            appRes[overlayKey] = new System.Windows.CornerRadius(4);
        }

        // ④ 下拉底色换不透明浅色：iNKORE 的下拉默认是半透明亚克力，背后是深色窗口时
        //    文字对比度直接报废（她实测"根本看不清"，截图对比验证过修复前后）。
        //    只动这一把刷子，其余 Fluent 外观不变。
        appRes["ComboBoxDropDownBackground"] =
            new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF9, 0xF9, 0xF9));
    }

    // ── 数据装配 ──────────────────────────────────────────────────────────

    /// <summary>导航项：GroupKey 决定所属分组（"全局设置"/"工具"），Target 是真正的页。</summary>
    public sealed record NavItem(string GroupKey, object Target)
    {
        public override string ToString() => Target.ToString() ?? string.Empty;
    }

    /// <summary>宿主节的功能域子页（方案 B）：GroupKey=null 表示热键中心聚合页。</summary>
    private sealed record HostGroupItem(HostSettingsSection Section, string? GroupKey)
    {
        public string Display => GroupKey ?? "热键中心";

        public override string ToString() => Display;
    }

    /// <summary>左侧列表项：包一层以便显示显示名。</summary>
    private sealed record ToolItem(RegisteredTool Tool)
    {
        public override string ToString() => Tool.Manifest.Name;
    }

    private void PopulateToolList()
    {
        var items = new List<NavItem>();

        // 宿主节（W4-c → 方案 B）：固定最上，按 x-group 切功能域子页 + 热键中心聚合页。
        // schema 仍是单文件 —— 这里只是导航层锚点，ConfigStore/CLI 面不受影响。
        if (_hostSection is not null)
        {
            var schema = ConfigSchema.FromJson(_hostSection.SchemaFactory());
            items.Add(new NavItem("全局设置", new HostGroupItem(_hostSection, HotkeysGroupKey)));
            foreach (var group in schema.Fields
                         .Where(f => f.Group is not null)
                         .GroupBy(f => f.Group!)
                         .OrderBy(g => g.Min(f => f.SortOrder)))
            {
                items.Add(new NavItem("全局设置", new HostGroupItem(_hostSection, group.Key)));
            }
        }

        // C2：configHidden 的工具（验收/演示）不出现在设置面板；config 链路保留。
        foreach (var tool in _host.Registry.Tools
                     .Where(t => t.Manifest.ConfigSchema is not null && !t.Manifest.ConfigHidden)
                     .OrderBy(t => t.Manifest.Name))
        {
            items.Add(new NavItem("工具", new ToolItem(tool)));
        }

        // 分组头用 CollectionView GroupStyle 渲染 —— 分组标题不进选中流，
        // 左列表天然两级（全局设置 / 工具），且键盘导航自动跳过组头。
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.GroupKey)));
        _toolList.ItemsSource = view;

        // 组头模板：次级文字样式，与可选项视觉分层。
        _toolList.GroupStyle.Clear();
        _toolList.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = MakeGroupHeaderTemplate(),
        });

        if (items.Count == 0)
        {
            _header.Text = "没有可配置的工具";
            _hint.Text = "只有 tool.json 里声明了 config schema 且未隐藏的工具才会出现在这里。";
            return;
        }

        _suppressSelectionGuard = true;
        _toolList.SelectedItem = items[0];   // → OnListSelectionChanged → OnToolSelected（首次 dirty=false）
        _suppressSelectionGuard = false;
        _currentSelection = items[0];
        OnToolSelected();
    }

    private object? _currentSelection;

    private DataTemplate MakeGroupHeaderTemplate()
    {
        const string xaml = "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
            + "<TextBlock Text=\"{Binding Name}\" FontSize=\"11\" Opacity=\"0.55\" Margin=\"10,8,0,2\"/></DataTemplate>";
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>
    /// 选中变化的总闸：组头不可选（不进选中流，此处理论上收不到 NavItem 以外的项）、
    /// dirty 时确认拦截（C4），通过后才真正渲染。
    /// </summary>
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionGuard)
        {
            return;
        }

        if (_toolList.SelectedItem is not NavItem nav)
        {
            return;
        }

        if (ReferenceEquals(nav, _currentSelection))
        {
            return;
        }

        if (!ConfirmDiscardIfDirty())
        {
            // 用户取消：恢复原选中（挂 guard 防递归），不渲染。
            _suppressSelectionGuard = true;
            _toolList.SelectedItem = _currentSelection;
            _suppressSelectionGuard = false;
            return;
        }

        _currentSelection = nav;
        OnToolSelected();
    }

    /// <summary>
    /// C4 dirty 拦截：有未保存修改且未被抑制时确认；确认放弃则清 dirty 返回 true。
    /// 探针/自检（SuppressDirtyConfirm）恒放行 —— headless 下模态弹窗无人点确定会挂死（§六-3）。
    /// </summary>
    private bool ConfirmDiscardIfDirty()
    {
        if (!_dirty || SuppressDirtyConfirm)
        {
            return true;
        }

        var answer = MessageBox.Show(this,
            "当前页有未保存的修改，切换将丢弃。确定继续吗？",
            "未保存的修改", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        ClearDirty();
        return true;
    }

    private void MarkDirty()
    {
        if (_dirty || _baseHeaderText is null)
        {
            return;
        }

        _dirty = true;
        _header.Text = _baseHeaderText + "（有未保存修改）";
    }

    private void ClearDirty()
    {
        _dirty = false;
        if (_baseHeaderText is not null)
        {
            _header.Text = _baseHeaderText;
        }
    }

    private void OnToolSelected()
    {
        _selected = null;
        _activeHostSection = null;
        _hostGroup = null;
        _toolHotkeyRows.Clear();
        _hotkeyStatusLabels.Clear();
        ClearDirty();
        _fieldPanel.Children.Clear();

        // 宿主功能域子页（方案 B）：与工具配置走**同一条** Load/渲染/Save 链路，只是目标与过滤不同。
        if (_toolList.SelectedItem is NavItem nav)
        {
            if (nav.Target is HostGroupItem hostGroup)
            {
                _activeHostSection = hostGroup.Section;
                _hostGroup = hostGroup;
                _baseHeaderText = hostGroup.Display;
                _header.Text = _baseHeaderText;
                var hostSchemaJson = hostGroup.Section.SchemaFactory();
                var hostSnapshot = _host.Configs.Load(hostGroup.Section.Id, hostSchemaJson);
                _hint.Text = hostGroup.GroupKey is null
                    ? "全部热键的统一入口：上半是宿主热键（写入 desktop.json），下半是工具热键（写 state.json 覆盖）。保存后立即重新注册生效。"
                    : $"全局设置 · {hostGroup.Display}。配置文件：{hostSnapshot.FilePath}";
                // A4：热键中心同时写 desktop.json 与 state.json，不突出单一文件；功能域页有明确归属。
                SetOpenPathTarget(hostGroup.GroupKey is null ? null : hostSnapshot.FilePath);
                RenderSnapshot(hostSnapshot, ConfigSchema.FromJson(hostSchemaJson), hostGroup.GroupKey);
                return;
            }

            if (nav.Target is ToolItem item)
            {
                _selected = item.Tool;
                _baseHeaderText = item.Tool.Manifest.Name;
                _header.Text = _baseHeaderText;
                _hint.Text = $"ID：{item.Tool.Manifest.Id}    配置文件：{_host.Configs.ConfigPath(item.Tool.Manifest.Id)}";
                SetOpenPathTarget(_host.Configs.ConfigPath(item.Tool.Manifest.Id));

                var manifest = item.Tool.Manifest;
                var schema = ConfigSchema.FromJson(manifest.ConfigSchema);
                var snapshot = _host.Configs.Load(manifest.Id, manifest.ConfigSchema);
                RenderSnapshot(snapshot, schema, groupKey: null);
                return;
            }
        }

        SetOpenPathTarget(null);
    }

    /// <summary>A4：构造"在资源管理器中显示"链接（默认折叠，SetOpenPathTarget 控制可见性）。</summary>
    private TextBlock MakeOpenPathLink()
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("在资源管理器中显示"))
        {
            FontSize = 12,
        };
        link.Click += (_, _) => OpenConfigInExplorer();

        var text = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed,
            Inlines = { link },
        };
        _openPathLink = text;
        return text;
    }

    private void SetOpenPathTarget(string? path)
    {
        _currentConfigPath = path;
        if (_openPathLink is not null)
        {
            _openPathLink.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OpenConfigInExplorer()
    {
        if (_currentConfigPath is null)
        {
            return;
        }

        try
        {
            // ⚠️ explorer /select 只认反斜杠（既有坑）—— 正斜杠会打开"文档"文件夹而不是定位文件。
            // ⚠️ 引号只包路径、不包 /select（实测坑）：整体加引号 "/select,path" 会被 Explorer
            //    当成非法 /select 参数 ⇒ 只开默认窗口、不定位文件（本次用户实测踩到）。
            var winPath = Path.GetFullPath(_currentConfigPath).Replace('/', '\\');
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{winPath}\""));
        }
        catch (Exception ex)
        {
            _host.Log.Error($"打开配置文件位置失败：{ex.Message}", "settings");
        }
    }

    /// <summary>
    /// 快照渲染（孤键告警 / 损坏恢复告警 / 字段行）—— 工具配置与宿主设置共用（W4-c 抽出）。
    /// 两条路径共用渲染是刻意的：宿主设置若另写一套，"schema → 控件"映射就会分叉漂移。
    ///
    /// 方案 B/C（2026-09-30）扩展：
    ///  · <paramref name="groupKey"/>：宿主功能域过滤（null = 全量；自检与热键中心用）；
    ///  · <see cref="HotkeysGroupKey"/>：热键中心聚合页 —— 宿主 5 热键 + 工具热键（写 state.json）；
    ///  · x-advanced 字段渲染进默认折叠的「高级」Expander（C5），与用户决策分层。
    /// </summary>
    private void RenderSnapshot(ConfigSnapshot snapshot, ConfigSchema schema, string? groupKey)
    {
        foreach (var orphan in snapshot.OrphanKeys)
        {
            _fieldPanel.Children.Add(WarnBar(
                $"配置文件里有 schema 之外的键「{orphan}」（旧键保留但不再注入工具）。"));
        }

        if (snapshot.RecoveredFromCorrupt)
        {
            _fieldPanel.Children.Add(WarnBar(
                "配置文件此前损坏，已自动备份并回落默认值 —— 保存后会覆盖为有效配置。"));
        }

        if (groupKey == HotkeysGroupKey)
        {
            RenderHotkeysCenter(snapshot, schema);
            return;
        }

        var fields = schema.Fields
            .Where(f => groupKey is null || f.Group == groupKey)
            .ToList();

        if (fields.Count == 0)
        {
            _fieldPanel.Children.Add(WarnBar("这个分组没有任何设置字段。"));
            return;
        }

        RenderFieldList(fields, snapshot);
        AttachHotkeyCaptureToPage();   // A2：功能域页的 *.hotkey 字段同样聚焦即捕获
    }

    /// <summary>普通字段平铺 + x-advanced 字段收进默认折叠的「高级」Expander（C5）。</summary>
    private void RenderFieldList(IReadOnlyList<ConfigField> fields, ConfigSnapshot snapshot)
    {
        var normal = fields.Where(f => !f.IsAdvanced).ToList();
        var advanced = fields.Where(f => f.IsAdvanced).ToList();

        foreach (var field in normal)
        {
            _fieldPanel.Children.Add(BuildFieldRow(field, snapshot));
        }

        if (advanced.Count == 0)
        {
            return;
        }

        var inner = new StackPanel();
        foreach (var field in advanced)
        {
            inner.Children.Add(BuildFieldRow(field, snapshot));
        }

        _fieldPanel.Children.Add(new Expander
        {
            Header = new TextBlock
            {
                Text = $"高级参数（{advanced.Count} 项 · 实现细节，默认值已适用）",
                FontSize = 12,
                Opacity = 0.75,
            },
            IsExpanded = false,
            Content = inner,
            Margin = new Thickness(0, 4, 0, 4),
        });
    }

    /// <summary>
    /// 热键中心（方案 B-2）：宿主热键沿用 schema 渲染（写 desktop.json，保存回调重注册）；
    /// 工具热键来自 Registry.Hotkeys(StateStore)（生效值 = override 优先），编辑写
    /// state.json hotkeyOverrides —— **权威源仍是仲裁器**：保存走 StateStore 写入口，
    /// 再经既有保存回调触发 ReRegisterHotkeys（其内部现读 StateStore，无需重建 ToolRegistry）。
    /// A4：每行尾「↺」恢复默认 —— 宿主键清空（=Unset 语义，保存回落 schema 默认）、
    /// 工具键回填清单默认（等于默认时保存自动移除 override）。都复用 dirty/保存链。
    /// </summary>
    private void RenderHotkeysCenter(ConfigSnapshot snapshot, ConfigSchema schema)
    {
        // ① 宿主热键（5 个：*.hotkey）
        var hostHotkeys = schema.Fields.Where(f => f.Key.EndsWith(".hotkey", StringComparison.Ordinal)).ToList();
        foreach (var field in hostHotkeys)
        {
            var row = (StackPanel)BuildFieldRow(field, snapshot);   // BuildFieldRow 恒返回 StackPanel
            AppendHotkeyResetButton(row, box =>
            {
                // 宿主键"默认" = 未设置（ConfigStore 的 Unset 语义）—— 清空编辑器，
                // 保存时空值走 Unset 回落 schema 默认，不留冗余落盘项。
                box.Text = string.Empty;
            });
            _fieldPanel.Children.Add(row);
        }

        // ② 工具热键
        _fieldPanel.Children.Add(new TextBlock
        {
            Text = "工具热键（覆盖写入 state.json，留空 = 恢复清单默认）",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 14, 0, 4),
        });

        var claims = new List<HotkeyClaim>();
        foreach (var (tool, hotkey, effective) in _host.Registry.Hotkeys(_host.StateStore))
        {
            var combo = HotkeyCombo.TryParse(effective);
            if (combo is null)
            {
                continue;   // 清单阶段已有 HotkeyInvalid 诊断
            }

            claims.Add(new HotkeyClaim(tool.Id, hotkey.Command, combo, claims.Count));
            _toolHotkeyRows[hotkey.Command] = (tool.Id, hotkey.Default);
        }

        var arbitration = HotkeyArbitration.Arbitrate(claims);

        foreach (var claim in claims)
        {
            var rid = claim.Combo.ResourceId;
            var isWinner = arbitration.Winners.TryGetValue(rid, out var w) && w == claim;
            var status = isWinner ? "✓ 生效" : $"✗ 让给 {arbitration.Winners[rid].ToolId}";
            var source = string.Equals(claim.Combo.Normalized, _toolHotkeyRows[claim.Command].Default, StringComparison.OrdinalIgnoreCase)
                ? "默认"
                : "覆盖";

            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
            row.Children.Add(new TextBlock
            {
                Text = $"{claim.Command}（{claim.ToolId}）",
                FontWeight = FontWeights.Medium,
                ToolTip = $"当前生效 {claim.Combo.Normalized} · 来源：{source} · 状态：{status}",
            });
            var statusLabel = new TextBlock
            {
                Text = $"生效 {claim.Combo.Normalized} · {source} · {status}",
                FontSize = 11,
                Opacity = 0.65,
            };
            row.Children.Add(statusLabel);
            _hotkeyStatusLabels[claim.Command] = statusLabel;   // A3：冲突重算的更新目标

            var box = new TextBox { Text = claim.Combo.Normalized };
            box.SetValue(HotkeyCommandProperty, claim.Command);
            box.Margin = new Thickness(0, 4, 0, 0);
            box.MaxWidth = 420;
            box.HorizontalAlignment = HorizontalAlignment.Left;
            box.TextChanged += (_, _) => MarkDirty();
            row.Children.Add(box);

            AppendHotkeyResetButton(row, b =>
            {
                // 工具键"默认" = 清单默认组合键；回填后若等于默认，保存时自动移除 override。
                b.Text = _toolHotkeyRows[claim.Command].Default;
            });

            _fieldPanel.Children.Add(row);
        }

        AttachHotkeyCaptureToPage();   // A2：宿主 5 键 + 工具 3 键全部聚焦即捕获
        RecalcHotkeyConflicts();       // A3：初始状态即按仲裁结果渲染（与上面循环一致，重算兜底）
    }

    /// <summary>附加属性：工具热键编辑器绑定的命令 id（与 FieldKey 并列 —— 后者专指 config 键）。</summary>
    private static readonly DependencyProperty HotkeyCommandProperty = DependencyProperty.RegisterAttached(
        "HotkeyCommand", typeof(string), typeof(SettingsWindow), new PropertyMetadata(default(string)));

    private static string? GetHotkeyCommand(DependencyObject o) => o.GetValue(HotkeyCommandProperty) as string;

    // ── A2/A3（2026-10-01，C 波次轻量方案）──────────────────────────────────

    /// <summary>
    /// 给当前页全部热键编辑器挂"聚焦即捕获"（A2）—— 判定：HotkeyCommand（工具键）
    /// 或 FieldKey 以 <c>.hotkey</c> 结尾（宿主键，含热键中心与功能域页两处渲染路径）。
    /// 并给每个热键编辑器挂 TextChanged → 冲突重算（A3）。
    /// 不建新控件是刻意的：TextBox 保留 ⇒ ProbeSetField / ReadEditor / EditorKeyTypes /
    /// verify-desktop expected **零改动**（§六 教训：新控件的隐藏成本是断言面三处同批迁移）。
    /// </summary>
    private void AttachHotkeyCaptureToPage()
    {
        foreach (var fe in Editors())
        {
            if (fe is not TextBox tb)
            {
                continue;
            }

            var isHotkey = GetHotkeyCommand(fe) is not null
                || (FieldKey.Get(fe)?.EndsWith(".hotkey", StringComparison.Ordinal) ?? false);
            if (!isHotkey)
            {
                continue;
            }

            AttachHotkeyCapture(tb);
            tb.TextChanged += (_, _) => RecalcHotkeyConflicts();
        }
    }

    /// <summary>
    /// 单个热键 TextBox 的捕获行为：聚焦即录制，按下组合键立即回填规范化串。
    /// </summary>
    private void AttachHotkeyCapture(TextBox box)
    {
        string? originalOnFocus = null;
        box.GotFocus += (_, _) => originalOnFocus = box.Text;
        box.PreviewKeyDown += (_, e) =>
        {
            // 导航键放行（不做处理）—— 键盘用户不能被困死在录制框里。
            if (e.Key is Key.Tab or Key.Enter or Key.Return
                or Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Prior or Key.Next or Key.Home or Key.End)
            {
                return;
            }

            // Esc = 还原聚焦前的值（放弃本次录制）。
            if (e.Key is Key.Escape)
            {
                box.Text = originalOnFocus ?? string.Empty;
                e.Handled = true;
                return;
            }

            // Alt 按下时 WPF 把 e.Key 报成 Key.System —— 真键在 SystemKey 里（实测坑）。
            var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;

            // 主键集与 HotkeyCombo.TryParse 的主键集保持一致：A-Z / 0-9（含小键盘）/ F1~F12。
            var key = rawKey switch
            {
                >= Key.A and <= Key.Z => rawKey.ToString(),
                >= Key.D0 and <= Key.D9 => ((int)rawKey - (int)Key.D0).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => ((int)rawKey - (int)Key.NumPad0).ToString(),
                >= Key.F1 and <= Key.F12 => rawKey.ToString(),
                _ => null,
            };

            if (key is not null)
            {
                var mods = new List<string>();
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods.Add("Win");

                // 规范化唯一出口：与仲裁器 / ezt hotkeys 同一份解析（两视图同源纪律）。
                var combo = HotkeyCombo.TryParse(string.Join("+", mods.Concat(new[] { key })));
                if (combo is not null)
                {
                    box.Text = combo.Normalized;
                }
                // 规范化失败（如裸字母无修饰键）：忽略、不污染 Text —— 无"再按一个主键"
                // 之类的临时提示文案，避免临时文本被 TextChanged 当成编辑值（轻量取舍）。
            }

            // 其余按键一律吞掉（含单字符、Space、IME 直入等）：热键框没有"手打文本"合法路径。
            e.Handled = true;
        };
    }

    /// <summary>
    /// A3：热键中心页冲突重算 —— 收集当前页全部热键编辑器的**当前值**
    /// （宿主 5 键 + 工具 3 键）替换 claims 后重跑仲裁，更新工具行状态文本。
    /// 只提示不禁止保存：仲裁器"保留先注册者"的归属语义不受影响。
    /// </summary>
    private void RecalcHotkeyConflicts()
    {
        if (_hostGroup?.GroupKey is not HotkeysGroupKey)
        {
            return;   // 只在热键中心页做（功能域页没有状态标签，直接跳过）
        }

        var claims = new List<HotkeyClaim>();
        foreach (var editor in Editors())
        {
            if (editor is not TextBox tb)
            {
                continue;
            }

            var combo = HotkeyCombo.TryParse(tb.Text);
            if (combo is null)
            {
                continue;   // 无法解析的行不参与仲裁（保存时仍会被校验拦下）
            }

            var command = GetHotkeyCommand(editor);
            if (command is not null && _toolHotkeyRows.TryGetValue(command, out var toolRow))
            {
                claims.Add(new HotkeyClaim(toolRow.ToolId, command, combo, claims.Count));
            }
            else if (FieldKey.Get(editor)?.EndsWith(".hotkey", StringComparison.Ordinal) == true)
            {
                claims.Add(new HotkeyClaim(HostSettingsSchema.SectionId, FieldKey.Get(editor)!, combo, claims.Count));
            }
        }

        var arbitration = HotkeyArbitration.Arbitrate(claims);
        foreach (var claim in claims)
        {
            if (!_hotkeyStatusLabels.TryGetValue(claim.Command, out var label))
            {
                continue;   // 宿主行没有状态标签 —— 冲突体现在对手工具行的"✗ 让给 desktop"里
            }

            var rid = claim.Combo.ResourceId;
            var isWinner = arbitration.Winners.TryGetValue(rid, out var w) && w == claim;
            var source = string.Equals(claim.Combo.Normalized, _toolHotkeyRows[claim.Command].Default, StringComparison.OrdinalIgnoreCase)
                ? "默认"
                : "覆盖";
            var status = isWinner ? "✓ 生效" : $"✗ 让给 {arbitration.Winners[rid].ToolId}";
            label.Text = $"生效 {claim.Combo.Normalized} · {source} · {status}";
        }
    }

    /// <summary>
    /// A4：给热键行的编辑器旁追加「↺」恢复默认按钮。
    /// 行结构是纵向 StackPanel（标题 / 编辑器），把编辑器从原位取出与按钮横排后再插回，
    /// 其余子元素（标题、状态标签）顺序不动；编辑器引用不变 ⇒ Editors()/探针/保存链零影响。
    /// </summary>
    private void AppendHotkeyResetButton(StackPanel row, Action<TextBox> restore)
    {
        if (row.Children.Count == 0 || row.Children[^1] is not TextBox editor)
        {
            return;   // 结构不符（防御）：不装饰也不破坏原行
        }

        row.Children.RemoveAt(row.Children.Count - 1);

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        editor.Margin = new Thickness(0, 4, 6, 0);
        line.Children.Add(editor);
        var resetButton = new Button
        {
            Content = "↺",
            ToolTip = "恢复默认（清空/回填后需点「保存」）",
            MinWidth = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        resetButton.Click += (_, _) => restore(editor);
        line.Children.Add(resetButton);

        row.Children.Add(line);
    }

    private static Border WarnBar(string message) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 6, 10, 6),
        Margin = new Thickness(0, 4, 0, 4),
        Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
    };

    private UIElement BuildFieldRow(ConfigField field, ConfigSnapshot snapshot)
    {
        var row = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };

        var label = new TextBlock
        {
            Text = field.Title ?? field.Key,
            FontWeight = FontWeights.Medium,
            ToolTip = field.Description ?? field.Key,
        };
        row.Children.Add(label);

        object editor = field.Type switch
        {
            ConfigFieldType.Boolean => MakeBoolean(field, snapshot),
            _ when field.Enum is { Count: > 0 } => MakeEnum(field, snapshot),
            ConfigFieldType.String when field.IsSecret => MakeSecret(snapshot, field),
            ConfigFieldType.Integer or ConfigFieldType.Number => MakeText(snapshot, field, RangeHint(field)),
            ConfigFieldType.String => MakeText(snapshot, field, null),
            _ => MakeText(snapshot, field, "（JSON 数组/对象，按 JSON 文本编辑）"),
        };

        if (editor is FrameworkElement fe)
        {
            fe.Margin = new Thickness(0, 4, 0, 0);
            fe.MaxWidth = 420;
            // ★ 2026-10-07 手工 B3 发现：TextBox 按内容自适应宽度 ⇒ **空字符串字段只有
            //   ~16px 宽、几乎无可见边框**（index.exclude / search.pathFilter / ocr.language
            //   / clip.blacklist 均如此）—— "完整可见"在空值态不成立。设最小宽度。
            fe.MinWidth = 260;
            fe.HorizontalAlignment = HorizontalAlignment.Left;
        }

        row.Children.Add((UIElement)editor);
        return row;
    }

    private static string? RangeHint(ConfigField field)
    {
        if (field.Minimum is null && field.Maximum is null)
        {
            return null;
        }

        // Minimum/Maximum 是 double?，不能跟字符串直接 ?? —— 先各自格式化
        var min = field.Minimum?.ToString() ?? "−∞";
        var max = field.Maximum?.ToString() ?? "+∞";
        return $"范围：{min} ~ {max}";
    }

    private static string? RawOf(JsonNode? node) => node?.ToString();

    private CheckBox MakeBoolean(ConfigField field, ConfigSnapshot snapshot)
    {
        var current = RawOf(snapshot.Effective[field.Key]);
        var box = new CheckBox
        {
            // A1（评审 P6-3）：Content = 字段标题，label 与勾选框合一；
            // 整段 description 回归 ToolTip —— 原来把长文挂 Content 是语义倒挂。
            Content = field.Title ?? "启用",
            ToolTip = field.Description,
            IsChecked = bool.TryParse(current, out var b) && b,
        };
        box.Checked += (_, _) => MarkDirty();
        box.Unchecked += (_, _) => MarkDirty();
        box.SetValue(FieldKey.Property, field.Key);
        return box;
    }

    private ComboBox MakeEnum(ConfigField field, ConfigSnapshot snapshot)
    {
        var options = field.Enum!.Select(n => n?.ToString() ?? string.Empty).ToList();
        var box = new ComboBox { ItemsSource = options };
        var current = RawOf(snapshot.Effective[field.Key]) ?? RawOf(field.Default);
        if (current is not null && options.Contains(current))
        {
            box.SelectedItem = current;
        }

        box.SelectionChanged += (_, _) => MarkDirty();
        box.SetValue(FieldKey.Property, field.Key);
        return box;
    }

    private TextBox MakeText(ConfigSnapshot snapshot, ConfigField field, string? hint)
    {
        var box = new TextBox { Text = RawOf(snapshot.Effective[field.Key]) ?? string.Empty };
        if (hint is not null)
        {
            box.ToolTip = hint;
        }

        box.TextChanged += (_, _) => MarkDirty();
        box.SetValue(FieldKey.Property, field.Key);
        return box;
    }

    private FrameworkElement MakeSecret(ConfigSnapshot snapshot, ConfigField field)
    {
        // 密码框不回显已存值（屏幕共享/旁观场景）；A1（评审 P6-5）：落盘状态直接显示
        // —— configured 原来只塞在 Tag 里（保存逻辑消费），用户看不到。
        var box = new PasswordBox();
        var configured = !string.IsNullOrEmpty(RawOf(snapshot.Effective[field.Key]));
        box.Tag = configured; // 保存逻辑据此判断"用户没动"与"清空"的区别
        box.PasswordChanged += (_, _) => MarkDirty();
        box.SetValue(FieldKey.Property, field.Key);

        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = configured ? "已配置（当前输入不回显）" : "未配置",
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return panel;
    }

    /// <summary>附加属性：把控件和它的配置键绑在一起，保存时按属性找键。</summary>
    private static class FieldKey
    {
        public static readonly DependencyProperty Property = DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(SettingsWindow), new PropertyMetadata(default(string)));

        public static string? Get(DependencyObject o) => o.GetValue(Property) as string;
        public static void Set(DependencyObject o, string? value) => o.SetValue(Property, value);
    }

    /// <summary>
    /// 当前页全部编辑器（递归）：普通行是 StackPanel 直接子，C5 之后高级字段嵌在 Expander.Content 里
    /// —— 保存/探针/自检共用这一个枚举，别再只看直接子（那是"字段不可达"类回归的温床）。
    /// ⚠️ 必须同时认两种附加属性：FieldKey（config 键）与 HotkeyCommand（工具热键）——
    /// 只认前者会把热键中心的工具热键行整体漏掉（保存写不进 StateStore、自检少 3 行，实测踩过）。
    /// </summary>
    private IEnumerable<FrameworkElement> Editors()
    {
        foreach (var fe in WalkLogical(_fieldPanel))
        {
            if (IsEditor(fe) || GetHotkeyCommand(fe) is not null)
            {
                yield return fe;
            }
        }
    }

    private static IEnumerable<FrameworkElement> WalkLogical(System.Windows.DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is not FrameworkElement fe)
            {
                continue;
            }

            yield return fe;
            foreach (var sub in WalkLogical(fe))
            {
                yield return sub;
            }
        }
    }

    // ── 保存 / 重置 ───────────────────────────────────────────────────────

    // ⚠️ async void（事件处理器）的两个纪律都体现在这里：
    //    ① 顶层 try/catch 兜底 —— await 之后的未处理异常没有调用方可接，会直接崩掉整个托盘进程；
    //    ② 保存期间禁用按钮 —— await 让出 UI 线程期间窗口仍可交互，不挡的话双击"保存"会并发两次。
    private async void Save()
    {
        if (_selected is null && _activeHostSection is null)
        {
            return;
        }

        _saveButton.IsEnabled = false;
        _resetButton.IsEnabled = false;
        try
        {
            await SaveCoreAsync();
        }
        catch (Exception ex)
        {
            _host.Log.Error($"保存配置时发生未预期异常：{ex}", "settings");
            _ = MessageBox.Show(this, $"保存失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _saveButton.IsEnabled = true;
            _resetButton.IsEnabled = true;
        }
    }

    private async Task SaveCoreAsync()
    {
        // 当前配置目标：宿主设置节（W4-c）或选中的工具。两者共用同一条 ConfigStore 写入链。
        var configId = _activeHostSection?.Id ?? _selected?.Manifest.Id;
        var schemaJson = _activeHostSection?.SchemaFactory() ?? _selected?.Manifest.ConfigSchema;
        if (configId is null)
        {
            return;
        }

        var errors = new List<string>();
        var changed = 0;

        foreach (var editor in Editors())
        {
            // ① config 字段（FieldKey）：宿主/工具配置走 ConfigStore（原链路不变）
            if (FieldKey.Get(editor) is { } key)
            {
                var (raw, isSecretUntouched) = ReadEditor(editor);
                if (isSecretUntouched)
                {
                    continue; // 密码框留空 = 没动，别把已存的值当成"清空"
                }

                try
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        // 空值 = 恢复该字段的默认值（从文件里拿掉，让 Effective 回落）
                        if (_host.Configs.Unset(configId, key))
                        {
                            changed++;
                        }
                    }
                    else
                    {
                        var result = _host.Configs.Set(configId, schemaJson, key, raw);
                        if (result.Ok)
                        {
                            changed++;
                        }
                        else
                        {
                            errors.Add($"{key}: {result.Error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{key}: {ex.Message}");
                }

                continue;
            }

            // ② 工具热键（HotkeyCommand）：写 state.json hotkeyOverrides（方案 B-2）。
            //    权威源仍是仲裁器 —— 写完由保存回调触发 ReRegisterHotkeys（现读 StateStore）。
            if (GetHotkeyCommand(editor) is { } command)
            {
                if (editor is not TextBox tb || _toolHotkeyRows.TryGetValue(command, out var rowInfo) is false)
                {
                    continue;
                }

                var raw = tb.Text.Trim();
                var combo = HotkeyCombo.TryParse(raw);
                if (combo is null)
                {
                    errors.Add($"{command}: 组合键 '{raw}' 无法解析（支持 Ctrl/Alt/Shift/Win + 字母/数字/F1~F12）");
                    continue;
                }

                try
                {
                    if (string.Equals(combo.Normalized, rowInfo.Default, StringComparison.OrdinalIgnoreCase))
                    {
                        // 与清单默认相同 → 移除覆盖（回默认语义），无覆盖时幂等静默
                        if (_host.StateStore.Get(rowInfo.ToolId).HotkeyOverrides.ContainsKey(command))
                        {
                            _host.StateStore.RemoveHotkeyOverride(rowInfo.ToolId, command);
                            changed++;
                        }
                    }
                    else if (!string.Equals(combo.Normalized, _host.StateStore.Get(rowInfo.ToolId).HotkeyOverrides.GetValueOrDefault(command), StringComparison.OrdinalIgnoreCase))
                    {
                        _host.StateStore.SetHotkeyOverride(rowInfo.ToolId, command, combo.Normalized);
                        changed++;
                    }
                    // 与当前覆盖相同 → 无变化，不写不计数
                }
                catch (Exception ex)
                {
                    errors.Add($"{command}: {ex.Message}");
                }
            }
        }

        if (errors.Count > 0)
        {
            _ = MessageBox.Show(this, "以下字段没有通过校验，其余已保存：\n\n" + string.Join("\n", errors),
                "部分字段未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 变更推送给常驻工具。当前全是 transient（每次调用重起、天然拿新值），
        // 推送会立即完成并返回 false —— 这是通路验证，不报错即可。
        // 宿主设置节（desktop）不是工具、没有进程可推 —— 跳过（推了也只是噪音）。
        if (_activeHostSection is null)
        {
            try
            {
                await _host.Processes.PushConfigAsync(configId);
            }
            catch (Exception ex)
            {
                _host.Log.Error($"配置变更推送失败（{configId}）：{ex.Message}", "settings");
            }
        }

        // 顺序有讲究：先重载快照（OnToolSelected 会把 _hint 重置成配置路径），
        // 再写保存提示 —— 反过来的话提示会立刻被覆盖，用户永远看不到（实测踩过）。
        OnToolSelected();
        ClearDirty();
        if (changed > 0)
        {
            _hint.Text = _activeHostSection is not null
                ? $"已保存 {changed} 项（{DateTime.Now:HH:mm:ss}）。热键已重新注册，OCR 语言下次唤出生效。"
                : $"已保存 {changed} 项（{DateTime.Now:HH:mm:ss}）。transient 工具下次调用即生效。";

            // 保存成功 → 通知托盘重读生效值并重注册全部热键（W4-c FR-9"保存即生效"）。
            // 方案 B-2 起这条回调同时覆盖工具热键：ReRegisterHotkeys 现读 StateStore，
            // SetHotkeyOverride 写完内存即生效 —— 不需要重建 ToolRegistry（§六-4 的担忧实测不需要）。
            // 放在 changed>0 分支里：什么都没改时不必重注册（也避免一次多余的气泡）。
            if (_activeHostSection is not null)
            {
                _hostSettingsSaved?.Invoke();
            }
        }
    }

    private void ResetToDefaults()
    {
        if (CurrentConfigId is null)
        {
            return;
        }

        if (!HasSavedConfig)
        {
            return; // 没有落盘配置时没有东西可重置，点了也不该有动静
        }

        // 配置被 Reset 改名备份（不裸删），但确认框仍然要问：
        // "恢复默认"是破坏性操作，多一次点击的成本远低于误点的损失。
        var targetName = _baseHeaderText ?? _selected?.Manifest.Name ?? CurrentConfigId!;
        var answer = MessageBox.Show(this,
            $"确定要恢复 {targetName} 的全部默认配置吗？\n\n（当前配置会备份为 .reset-backup-* 文件，不会丢失）",
            "恢复默认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _host.Configs.Reset(CurrentConfigId!);
        OnToolSelected();
    }

    /// <summary>当前配置目标（宿主设置节或选中工具）的 id；两个都不是时为 null。</summary>
    private string? CurrentConfigId => _activeHostSection?.Id ?? _selected?.Manifest.Id;

    private bool HasSavedConfig => CurrentConfigId is not null && _host.Configs.HasSavedConfig(CurrentConfigId);

    private static bool IsEditor(FrameworkElement e) =>
        FieldKey.Get(e) is not null;

    private (string? Raw, bool IsSecretUntouched) ReadEditor(FrameworkElement editor) => editor switch
    {
        CheckBox c => (c.IsChecked == true ? "true" : "false", false),
        ComboBox cmb => (cmb.SelectedItem?.ToString(), false),
        PasswordBox pb => (pb.Password, pb.Password.Length == 0 && pb.Tag is true),
        TextBox tb => (tb.Text, false),
        _ => (null, false),
    };

    /// <summary>
    /// 自检清单：为每个有 config schema 的工具列出「字段=控件类型」。
    ///
    /// 供 <c>--selfcheck</c> 自动化断言（§7 的 schema → 控件映射是否真的落地）。
    /// 不 Show 窗口 —— WPF 控件允许在未显示的窗口上构建（Main 已是 STA 线程）。
    /// 宿主设置节（W4-c）以 <c>设置清单 desktop: …</c> 全量格式输出（字段寻址不变），
    /// 另出 <c>设置清单 desktop.热键中心: …</c> 聚合页一行（方案 B-2）。
    /// </summary>
    internal IReadOnlyList<string> InventoryForSelfCheck()
    {
        SuppressDirtyConfirm = true;   // 程序化切页不得触发确认弹窗（headless 挂死防线，§六-3）
        var lines = new List<string>();
        PopulateToolList();

        foreach (var nav in _toolList.Items.OfType<NavItem>().Where(n => n.Target is ToolItem))
        {
            _suppressSelectionGuard = true;
            _toolList.SelectedItem = nav;
            _suppressSelectionGuard = false;
            OnToolSelected();
            if (_selected is null)
            {
                continue;
            }

            lines.Add($"设置清单 {_selected.Manifest.Id}: {EditorKeyTypes()}");
        }

        // 宿主设置节（W4-c → 方案 B）：全量渲染 11 字段 —— 键名与控件类型与拆分前完全一致，
        // 断言消费方（verify-desktop 1b）无感。
        if (_hostSection is not null)
        {
            var hostSchemaJson = _hostSection.SchemaFactory();
            var hostSnapshot = _host.Configs.Load(_hostSection.Id, hostSchemaJson);
            _toolHotkeyRows.Clear();
            _hotkeyStatusLabels.Clear();
            _fieldPanel.Children.Clear();
            RenderSnapshot(hostSnapshot, ConfigSchema.FromJson(hostSchemaJson), groupKey: null);
            lines.Add($"设置清单 {_hostSection.Id}: {EditorKeyTypes()}");

            // 右栏滚动诊断（W6-d 用户实测回归）：ScrollViewer 必须直接吃 DockPanel 剩余
            // 高度才有真实 Viewport —— 包 StackPanel 时 Viewport=∞、ScrollableHeight=0，
            // 内容溢出窗口被裁且不可滚（底部字段不可达）。未显示窗口可 Measure/Arrange
            //（selfcheck 先例），布局后读数才有意义。
            if (_fieldScroller is not null)
            {
                // ⚠️ 对未显示的 Window 直接 Measure/Arrange 是 no-op（Window 布局绑定
                // hwnd，实测 IsMeasureValid=False）—— 要测 **Content 根**（排布出真实视口
                // 高度）；ScrollViewer 的 Extent/Viewport 依赖模板 presenter 时序拿不到，
                // ⇒ 字段面板**单独**用无限高测量拿自然内容高度，与视口比较：
                //   内容高于视口 = 滚动条必出现 = 底部字段可达。StackPanel 包裹回归时
                //   scroller 高度=内容高度（无约束），可滚=False，此处即红。
                if (Content is System.Windows.Controls.Grid rootGrid)
                {
                    rootGrid.Measure(new Size(Width, Height));
                    rootGrid.Arrange(new Rect(0, 0, Width, Height));
                }

                _fieldPanel.Measure(new Size(
                    _fieldScroller.ActualWidth > 0 ? _fieldScroller.ActualWidth : 530,
                    double.PositiveInfinity));
                var contentHeight = _fieldPanel.DesiredSize.Height;
                var viewportHeight = Math.Max(_fieldScroller.ViewportHeight, _fieldScroller.ActualHeight);
                lines.Add($"设置窗右栏滚动: 内容高度={contentHeight:F0} 视口高度={viewportHeight:F0} "
                    + $"可滚={(contentHeight > viewportHeight && viewportHeight > 0 ? "True" : "False")} 字段数={_fieldPanel.Children.Count}");
            }

            // 热键中心聚合页（方案 B-2）：宿主 5 热键 + 工具热键（HotkeyCommand 键）。
            _fieldPanel.Children.Clear();
            _toolHotkeyRows.Clear();
            _hotkeyStatusLabels.Clear();
            RenderSnapshot(hostSnapshot, ConfigSchema.FromJson(hostSchemaJson), HotkeysGroupKey);
            lines.Add($"设置清单 {_hostSection.Id}.热键中心: {EditorKeyTypes()}");
        }

        return lines;
    }

    /// <summary>当前 _fieldPanel 里全部编辑器的「键=控件类型」清单（FieldKey 优先，其次 HotkeyCommand）。</summary>
    private string EditorKeyTypes() =>
        string.Join(", ", Editors()
            .Select(e => $"{FieldKey.Get(e) ?? GetHotkeyCommand(e)}={e.GetType().Name}"));

    // ── 探针入口（W4-c 真机项自动化）────────────────────────────────────────
    // 先例与纪律：InventoryForSelfCheck 已证明"WPF 控件允许在未显示的窗口上构建"
    // （Main 是 STA 线程），探针据此可以不 Show 对话框而走完整的数据链路。

    /// <summary>
    /// 探针：装配列表并选中宿主设置节（不 Show 窗口）。
    /// <paramref name="groupKey"/> 指定功能域子页（方案 B）：null = 第一页（热键中心）；
    /// 传 HotkeysGroupKey / x-group 名可直达对应页。宿主节不存在时返回 false。
    /// </summary>
    internal bool ProbeSelectHostSection(string? groupKey = null)
    {
        SuppressDirtyConfirm = true;
        PopulateToolList();
        var hostItem = _toolList.Items.OfType<NavItem>()
            .FirstOrDefault(n => n.Target is HostGroupItem hg && (groupKey is null || hg.GroupKey == groupKey));
        if (hostItem is null)
        {
            return false;
        }

        _suppressSelectionGuard = true;
        _toolList.SelectedItem = hostItem;
        _suppressSelectionGuard = false;
        _currentSelection = hostItem;
        OnToolSelected();
        return _activeHostSection is not null;
    }

    /// <summary>探针：按配置键找编辑器并设值（只支持 string 类型的 TextBox —— 宿主节全是）。</summary>
    internal bool ProbeSetField(string key, string value)
    {
        var editor = Editors().FirstOrDefault(e => FieldKey.Get(e) == key);
        if (editor is not TextBox textBox)
        {
            return false;
        }

        textBox.Text = value;
        return true;
    }

    /// <summary>探针：走真实保存链（校验 → ConfigStore.Set → 回调）。同步等待（保存路径无真 await）。</summary>
    internal Task ProbeSaveAsync() => SaveCoreAsync();
}
