using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Eztools.Contracts;
using Eztools.Host;
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
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly EztoolsHost _host;
    private readonly ListBox _toolList;
    private readonly StackPanel _fieldPanel;
    private readonly TextBlock _header;
    private readonly TextBlock _hint;
    private readonly Button _saveButton;
    private readonly Button _resetButton;
    private RegisteredTool? _selected;

    public SettingsWindow(EztoolsHost host)
    {
        _host = host;

        Title = "Eztools 设置";
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        ApplyModernTheme();

        _toolList = new ListBox { Width = 190, Margin = new Thickness(8) };
        _toolList.SelectionChanged += (_, _) => OnToolSelected();

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
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _fieldPanel };
        var rightTop = new StackPanel();
        rightTop.Children.Add(_header);
        rightTop.Children.Add(_hint);
        rightTop.Children.Add(scroller);
        right.Children.Add(rightTop);

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

    private void PopulateToolList()
    {
        _toolList.Items.Clear();
        foreach (var tool in _host.Registry.Tools.Where(t => t.Manifest.ConfigSchema is not null).OrderBy(t => t.Manifest.Name))
        {
            _toolList.Items.Add(new ToolItem(tool));
        }

        if (_toolList.Items.Count == 0)
        {
            _header.Text = "没有可配置的工具";
            _hint.Text = "只有 tool.json 里声明了 config schema 的工具才会出现在这里。";
            return;
        }

        _toolList.SelectedIndex = 0;
    }

    private void OnToolSelected()
    {
        if (_toolList.SelectedItem is not ToolItem item)
        {
            return;
        }

        _selected = item.Tool;
        _fieldPanel.Children.Clear();
        _header.Text = item.Tool.Manifest.Name;
        _hint.Text = $"ID：{item.Tool.Manifest.Id}    配置文件：{_host.Configs.ConfigPath(item.Tool.Manifest.Id)}";

        var manifest = item.Tool.Manifest;
        var schema = ConfigSchema.FromJson(manifest.ConfigSchema);
        var snapshot = _host.Configs.Load(manifest.Id, manifest.ConfigSchema);

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

        if (schema.Fields.Count == 0)
        {
            _fieldPanel.Children.Add(WarnBar("这个工具声明了 config 但没有任何字段。"));
            return;
        }

        foreach (var field in schema.Fields.OrderBy(f => f.SortOrder))
        {
            _fieldPanel.Children.Add(BuildFieldRow(field, snapshot));
        }
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
            Content = field.Description ?? "启用",
            IsChecked = bool.TryParse(current, out var b) && b,
        };
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

        box.SetValue(FieldKey.Property, field.Key);
        return box;
    }

    private PasswordBox MakeSecret(ConfigSnapshot snapshot, ConfigField field)
    {
        // 密码框不回显已存值（屏幕共享/旁观场景）；占位符提示"已设置/未设置"
        var box = new PasswordBox();
        var configured = !string.IsNullOrEmpty(RawOf(snapshot.Effective[field.Key]));
        box.Tag = configured; // 保存逻辑据此判断"用户没动"与"清空"的区别
        box.SetValue(FieldKey.Property, field.Key);
        return box;
    }

    /// <summary>附加属性：把控件和它的配置键绑在一起，保存时按属性找键。</summary>
    private static class FieldKey
    {
        public static readonly DependencyProperty Property = DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(SettingsWindow), new PropertyMetadata(default(string)));

        public static string? Get(DependencyObject o) => o.GetValue(Property) as string;
        public static void Set(DependencyObject o, string? value) => o.SetValue(Property, value);
    }

    // ── 保存 / 重置 ───────────────────────────────────────────────────────

    // ⚠️ async void（事件处理器）的两个纪律都体现在这里：
    //    ① 顶层 try/catch 兜底 —— await 之后的未处理异常没有调用方可接，会直接崩掉整个托盘进程；
    //    ② 保存期间禁用按钮 —— await 让出 UI 线程期间窗口仍可交互，不挡的话双击"保存"会并发两次。
    private async void Save()
    {
        if (_selected is null)
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
        if (_selected is null)
        {
            return;
        }

        var toolId = _selected.Manifest.Id;
        var schemaJson = _selected.Manifest.ConfigSchema;
        var errors = new List<string>();
        var changed = 0;

        foreach (var child in _fieldPanel.Children)
        {
            var editor = FindEditor((UIElement)child);
            if (editor is null || FieldKey.Get(editor) is not { } key)
            {
                continue;
            }

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
                    if (_host.Configs.Unset(toolId, key))
                    {
                        changed++;
                    }
                }
                else
                {
                    var result = _host.Configs.Set(toolId, schemaJson, key, raw);
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
        }

        if (errors.Count > 0)
        {
            _ = MessageBox.Show(this, "以下字段没有通过校验，其余已保存：\n\n" + string.Join("\n", errors),
                "部分字段未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 变更推送给常驻工具。当前全是 transient（每次调用重起、天然拿新值），
        // 推送会立即完成并返回 false —— 这是通路验证，不报错即可。
        try
        {
            await _host.Processes.PushConfigAsync(toolId);
        }
        catch (Exception ex)
        {
            _host.Log.Error($"配置变更推送失败（{toolId}）：{ex.Message}", "settings");
        }

        // 顺序有讲究：先重载快照（OnToolSelected 会把 _hint 重置成配置路径），
        // 再写保存提示 —— 反过来的话提示会立刻被覆盖，用户永远看不到（实测踩过）。
        OnToolSelected();
        if (changed > 0)
        {
            _hint.Text = $"已保存 {changed} 项（{DateTime.Now:HH:mm:ss}）。transient 工具下次调用即生效。";
        }
    }

    private void ResetToDefaults()
    {
        if (_selected is null)
        {
            return;
        }

        if (!HasSavedConfig)
        {
            return; // 没有落盘配置时没有东西可重置，点了也不该有动静
        }

        // 配置被 Reset 改名备份（不裸删），但确认框仍然要问：
        // "恢复默认"是破坏性操作，多一次点击的成本远低于误点的损失。
        var answer = MessageBox.Show(this,
            $"确定要恢复 {_selected.Manifest.Name} 的全部默认配置吗？\n\n（当前配置会备份为 .reset-backup-* 文件，不会丢失）",
            "恢复默认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _host.Configs.Reset(_selected.Manifest.Id);
        OnToolSelected();
    }

    private bool HasSavedConfig => _selected is not null && _host.Configs.HasSavedConfig(_selected.Manifest.Id);

    /// <summary>字段行里唯一可编辑的控件（warn 条没有 editor，返回 null 跳过）。</summary>
    private static FrameworkElement? FindEditor(UIElement row) => row switch
    {
        StackPanel p => p.Children.OfType<FrameworkElement>().FirstOrDefault(IsEditor),
        _ => row as FrameworkElement,
    };

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
    /// </summary>
    internal IReadOnlyList<string> InventoryForSelfCheck()
    {
        var lines = new List<string>();
        PopulateToolList();
        for (var i = 0; i < _toolList.Items.Count; i++)
        {
            _toolList.SelectedIndex = i; // SelectionChanged → OnToolSelected → 同步渲染
            if (_toolList.SelectedItem is not ToolItem item)
            {
                continue;
            }

            var editors = _fieldPanel.Children.Cast<UIElement>()
                .Select(FindEditor)
                .OfType<FrameworkElement>()
                .Where(e => FieldKey.Get(e) is not null)
                .Select(e => $"{FieldKey.Get(e)}={e.GetType().Name}");
            lines.Add($"设置清单 {item.Tool.Manifest.Id}: {string.Join(", ", editors)}");
        }

        return lines;
    }

    /// <summary>左侧列表项：包一层以便显示显示名。</summary>
    private sealed record ToolItem(RegisteredTool Tool)
    {
        public override string ToString() => Tool.Manifest.Name;
    }
}
