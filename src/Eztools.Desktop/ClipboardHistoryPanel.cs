// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Eztools.ClipboardLib;
using Eztools.Ocr;
using InputCmd = Eztools.Desktop.NativeInputBox.InputCommand;

namespace Eztools.Desktop;

/// <summary>
/// 剪贴板历史面板（W5-b）：热键唤出、打字过滤、Enter 直贴回原窗口。
///
/// <b>与 SearchWindow 同构同纪律</b>：虚拟化三重前提、焦点常驻输入框、失焦隐藏、X=隐藏、
/// <c>_summoning</c> 屏蔽唤出抖动。
///
/// <b>★ 2026-09-27 换实现（本文件的关键变更）</b>：输入框从 WPF <c>TextBox</c>（TSF 文本栈）
/// 换成宿主化**原生 EDIT**（<see cref="NativeInputBox"/>，IMM32 路径）。配套地，本面板原有的
/// 「直通字符合成层 / LL 钩子 / GetAsyncKeyState 轮询 / hwnd 消息钩子 / PreviewKeyDown 拦截」
/// 五层干预**全部删除** —— 它们是"上一轮补丁的副作用"的解药，叠加即互相双打（§2.29）。
/// 现在的分工只有一句：<b>文本编辑归原生控件，命令键由它上报</b>。
///
/// <b>与搜索窗的模型差异</b>：
/// ① 数据是本地 SQLite（同步查询 &lt;10ms，直接在 UI 线程调 —— 与常驻监听竞争同一
///    <see cref="HistoryStore"/> 实例时锁粒度是毫秒级，不构成"UI 等待"问题）；
/// ② Enter 的动作是<b>直贴</b>：内容上剪贴板 → Hide → 还原唤出前前台 → 注入 Ctrl+V
///    （<see cref="PasteBack"/>；探针模式抑制真注入 —— 真注入会贴进运行探针的终端，
///    端到端注入归手工验收清单 M 项）；
/// ③ 唤出前必须记住前台窗口（<see cref="_lastForeground"/>），这是直贴的还原目标。
/// </summary>
public sealed class ClipboardHistoryPanel : Window
{
    /// <summary>与 SearchWindow 同款虚拟化模板（结构前提，缺一即静默退化 —— W3-d-2 实测）。</summary>
    private const string VirtualizingListTemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type ListBox}">
          <Border Background="Transparent" SnapsToDevicePixels="True">
            <ScrollViewer Focusable="False" Padding="0" CanContentScroll="True"
                          HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
              <ItemsPresenter/>
            </ScrollViewer>
          </Border>
        </ControlTemplate>
        """;

    /// <summary>单次加载条数上限（历史库再大，面板一次也只看这么多）。</summary>
    private const int LoadLimit = 200;

    private readonly HistoryStore _store;
    private readonly Action<string>? _notify;   // 直贴降级/错误提示走托盘气泡（面板隐藏后没有别的嘴）
    private readonly string? _ocrLanguage;      // OCR 提字用的语言标签（W5-d FR-15；null = 引擎自动选）

    private readonly NativeInputBox _input;
    private readonly ListBox _results;
    private readonly ComboBox _kindFilter;
    private readonly TextBlock _status;
    private readonly TextBlock _statusRight;
    private readonly MenuItem _ocrMenuItem;

    private bool _realClose;            // 仅托盘退出/探针收尾置位（X=隐藏）
    private bool _summoning;            // 唤出期屏蔽瞬态 Deactivated
    private bool _suppressReload;       // 程序化设文本不得反过来触发过滤（21.6 同族）
    private bool _ocrBusy;              // OCR 进行中（防连点重复识别）
    private nint _lastForeground;       // 唤出前的前台窗口（直贴还原目标；0 = 未知）
    private List<ClipEntry> _current = new();

    public ClipboardHistoryPanel(HistoryStore store, Action<string>? notify = null, string? ocrLanguage = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notify = notify;
        _ocrLanguage = string.IsNullOrWhiteSpace(ocrLanguage) ? null : ocrLanguage.Trim();

        Title = "Eztools 剪贴板历史";
        Width = 680;
        Height = 520;
        MinWidth = 440;
        MinHeight = 280;
        // ★ 位置 = 跟随光标（W5-d「视线不跳」）：不做 CenterScreen 自动居中 ——
        //   居中会让用户的视线从光标跳走；改为每次唤出按光标所在显示器定位（见 PositionNearCursor）。
        //   Manual 是必须的：非 Manual 时每次 Show() 都会按策略重新定位，覆盖我们的 SetWindowPos。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;                    // 热键召唤的窗必须在最前
        ShowInTaskbar = false;             // 临时浮层不占任务栏
        UseLayoutRounding = true;

        ApplyModernTheme();

        // ── 输入行：原生 EDIT（占满）+ 类型过滤下拉（右侧固定宽）────────────────
        _input = new NativeInputBox();

        _kindFilter = new ComboBox
        {
            Width = 104,
            Margin = new Thickness(0, 12, 12, 6),
            VerticalAlignment = VerticalAlignment.Stretch,
            DisplayMemberPath = nameof(KindOption.Label),
            ItemsSource = new[]
            {
                new KindOption(null, "全部"),
                new KindOption(ClipKind.Text, "文本"),
                new KindOption(ClipKind.FileList, "文件"),
                new KindOption(ClipKind.Image, "图片"),
            },
            SelectedIndex = 0,
        };

        var inputRow = new DockPanel();
        DockPanel.SetDock(_kindFilter, Dock.Right);
        inputRow.Children.Add(_kindFilter);
        inputRow.Children.Add(_input);     // LastChildFill = 输入框吃掉剩余宽度

        // ── 结果列表（虚拟化三重前提，见 SearchWindow 同款注释）────────────────
        _results = new ListBox
        {
            Margin = new Thickness(12, 0, 12, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = MakeClipTemplate(),
        };

        // ★ 焦点常驻输入框 ⇒ 列表必然失焦，默认"失焦选中色"太淡看不见
        //   —— 双保险：① 覆盖系统选中刷；② ItemContainerStyle 的 IsSelected 触发器上不透明浅蓝底。
        var selBrush = new SolidColorBrush(Color.FromRgb(0xBD, 0xE3, 0xFF));
        _results.Resources[SystemColors.HighlightBrushKey] = selBrush;
        _results.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = selBrush;

        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(BackgroundProperty, selBrush));
        selectedTrigger.Setters.Add(new Setter(ForegroundProperty, Brushes.Black));
        itemStyle.Triggers.Add(selectedTrigger);
        _results.ItemContainerStyle = itemStyle;

        _results.Template = (ControlTemplate)XamlReader.Parse(VirtualizingListTemplateXaml);
        _results.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(
            new FrameworkElementFactory(typeof(VirtualizingStackPanel))));
        _results.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _results.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _results.SetValue(ScrollViewer.CanContentScrollProperty, true);

        // ── 右键菜单（W5-d · FR-15：图片条目 OCR 提字）──────────────────────────
        // 非图片条目**置灰**而不是隐藏菜单项：隐藏会让用户以为"这功能不存在"，
        // 置灰表达的是"有这功能，但这条用不上"（两者对用户的信息量完全不同）。
        _ocrMenuItem = new MenuItem { Header = "提取文字（OCR）" };
        _ocrMenuItem.Click += (_, _) => ExtractTextFromSelectedImage();

        var menu = new ContextMenu();
        menu.Items.Add(_ocrMenuItem);
        menu.Opened += (_, _) =>
        {
            _ocrMenuItem.IsEnabled = OcrMenuEnabled;
            _ocrMenuItem.ToolTip = OcrMenuEnabled ? null : "只有图片条目可以提取文字";
        };
        _results.ContextMenu = menu;

        // 右键先选中：ListBox 默认不因右键改变选中项 —— 不处理就会"右键 A、操作了上次选的 B"。
        // 命中目标通常是模板里的 TextBlock/Run（不是 ListBoxItem 本身），所以要向上找祖先。
        _results.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
            {
                item.IsSelected = true;
            }
        };

        // ── 状态行 ─────────────────────────────────────────────────────────────
        _status = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _statusRight = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var statusGrid = new Grid { Margin = new Thickness(12, 0, 12, 10) };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(_statusRight, 1);
        statusGrid.Children.Add(_status);
        statusGrid.Children.Add(_statusRight);

        var root = new DockPanel();
        DockPanel.SetDock(inputRow, Dock.Top);
        DockPanel.SetDock(statusGrid, Dock.Bottom);
        root.Children.Add(inputRow);
        root.Children.Add(statusGrid);
        root.Children.Add(_results);
        Content = root;

        // ── 事件接线（控件全部就位后再接，避免构造期触发 Reload 撞空引用）────────
        _suppressReload = true;
        _input.TextChanged += () =>
        {
            if (!_suppressReload)
            {
                Reload();
            }
        };
        _suppressReload = false;

        _input.Command = OnInputCommand;
        _kindFilter.SelectionChanged += (_, _) => Reload();

        // 失焦自动隐藏。★ Summon() 强制前台期间会抖出瞬态 Deactivated，由 _summoning 屏蔽。
        Deactivated += (_, _) => { if (!_summoning) Hide(); };

        // ★ 标题栏 X / Alt+F4 = 收起，不是关闭（"热键全灭"主坑，与搜索窗同款契约）。
        Closing += (_, e) =>
        {
            if (_realClose)
            {
                return;
            }

            e.Cancel = true;
            Hide();
        };

        Reload();
    }

    // ────────────────────────────────────────────── 唤出 / 收起

    /// <summary>唤出（可见则仅激活）。TrayApplication 热键/托盘入口（W5-c）与探针共用。</summary>
    public void Summon()
    {
        _lastForeground = GetForegroundWindow();
        _summoning = true;
        try
        {
            if (Visibility != Visibility.Visible)
            {
                Show();
            }
            else
            {
                Activate();
            }

            ForceForeground();
            PositionNearCursor();   // ★ 每次唤出都按光标定位（W5-d「视线不跳」）
            Reload();
            _input.FocusInput();
            _input.SelectAll();
            RestoreImeAssociation();   // 抢前台副作用修复（与搜索窗同款，见方法注释）
            if (_results.Items.Count > 0)
            {
                _results.SelectedIndex = 0;
            }

            // ★ 前台完全建立后再钉一次焦点（搜索窗同款的"双保险"，2026-09-27 探针实测补上）：
            //   从隐藏态重新唤出时，ForceForeground 的激活消息可能在 Summon 返回、消息泵恢复后
            //   才落地 —— 只做同步那一次 FocusInput，系统会把焦点留给"当时还是前台的那个窗口"，
            //   于是窗口可见却打不进字（探针第 2~4 轮 queryFocused=false 就是这个形态）。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (IsLoaded && Visibility == Visibility.Visible)
                {
                    _input.FocusInput();
                }
            });
        }
        finally
        {
            _summoning = false;
        }
    }

    /// <summary>热键两段语义：可见 ⇔ 隐藏。</summary>
    public void Toggle()
    {
        if (Visibility == Visibility.Visible)
        {
            Hide();
        }
        else
        {
            Summon();
        }
    }

    private new void Hide()
    {
        Visibility = Visibility.Hidden;
    }

    // ────────────────────────────────────────────── 数据装载

    /// <summary>
    /// 按「类型过滤 + 输入文本」装载列表。空文本 = 最近历史全量（List），
    /// 有文本 = FTS/LIKE 搜索（<see cref="HistoryStore.Search"/>），再按类型二次过滤
    ///（搜索接口不带 kind 参数 —— 契约如此，过滤放在这里做，代价是内存里筛一遍）。
    /// </summary>
    private void Reload()
    {
        var kind = SelectedKind();
        var text = _input.Text;

        _current = string.IsNullOrWhiteSpace(text)
            ? _store.List(kind, LoadLimit).ToList()
            : _store.Search(text, LoadLimit).Where(e => kind is null || e.Kind == kind).ToList();

        _results.Items.Clear();
        foreach (var entry in _current)
        {
            _results.Items.Add(entry);
        }

        _statusRight.Text = _current.Count == 0 ? "" : $"共 {_current.Count} 条";

        if (_current.Count == 0)
        {
            _status.Text = string.IsNullOrWhiteSpace(text)
                ? "暂无历史 —— 复制任意内容后这里就会出现（后台监听自动捕获）"
                : $"没有匹配“{text}”的条目";
        }
        else
        {
            _status.Text = HintText;
        }
    }

    /// <summary>常态提示行（操作键一览 —— 键位是面板唯一的可发现性来源，必须写出来）。</summary>
    private static string HintText =>
        "Enter 直贴 · Ctrl+Enter 仅复制 · Ctrl+C 复制 · Del 删除(输入框空时) · Ctrl+P 置顶 · Esc 收起";

    private ClipKind? SelectedKind() => (_kindFilter.SelectedItem as KindOption)?.Kind;

    private ClipEntry? SelectedEntry() => _results.SelectedItem as ClipEntry;

    private ClipEntry? FirstOrSelected() => SelectedEntry() ?? (_results.Items.Count > 0 ? _current[0] : null);

    // ────────────────────────────────────────────── 动作

    /// <summary>把条目内容写上系统剪贴板（Text/FileList/Image 三通道）。失败返回错误文案。</summary>
    private string? WriteEntryToClipboard(ClipEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case ClipKind.Text:
                    System.Windows.Clipboard.SetText(entry.Content ?? "");
                    return null;

                case ClipKind.FileList:
                    var list = new System.Collections.Specialized.StringCollection();
                    list.AddRange((entry.Content ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    if (list.Count == 0)
                    {
                        return "文件列表为空";
                    }

                    System.Windows.Clipboard.SetFileDropList(list);
                    return null;

                case ClipKind.Image:
                    var source = LoadBitmap(entry);
                    if (source is null)
                    {
                        return "图片文件缺失或不可解码";
                    }

                    System.Windows.Clipboard.SetImage(source);
                    return null;

                default:
                    return "未知条目类型";
            }
        }
        catch (Exception ex)
        {
            // 剪贴板被其它进程占用是真实场景（W4 竞争先例）——明示而非静默
            return ex.Message;
        }
    }

    private BitmapSource? LoadBitmap(ClipEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ImagePath) || _store.ImagesDirectory is null)
        {
            return null;
        }

        var full = Path.Combine(_store.ImagesDirectory, entry.ImagePath);
        if (!File.Exists(full))
        {
            return null;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 释放文件句柄
            bmp.DecodePixelWidth = 256;                    // 剪贴板不需要原始分辨率
            bmp.UriSource = new Uri(full);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Enter：直贴闭环（FR-10）。写剪贴板 → Hide → 还原唤出前前台 → 注入 Ctrl+V。
    /// 任一环失败都降级为"仅复制"+ 气泡提示（内容已在剪贴板上，用户手动 Ctrl+V 兜底）。
    /// </summary>
    private void PasteFirstOrSelected()
    {
        var entry = FirstOrSelected();
        if (entry is null)
        {
            return;
        }

        var writeError = WriteEntryToClipboard(entry);
        if (writeError is not null)
        {
            _status.Text = $"写剪贴板失败：{writeError}";
            _notify?.Invoke($"剪贴板历史：写入失败（{writeError}）");
            return;
        }

        Hide();

        if (ProbeSuppressInject)
        {
            // 探针模式：真 Ctrl+V 会贴进运行探针的终端（不可接受的副作用）。
            // 剪贴板写入与面板收起已发生 —— 断言面在此，端到端注入归手工 M 项。
            return;
        }

        // 还原前台（目标可能已销毁——用户关掉了复制来源）→ 等激活落地 → 注入
        var restored = PasteBack.RestoreForeground(_lastForeground);
        if (!restored)
        {
            _notify?.Invoke("剪贴板历史：已复制，但未能还原原窗口（可能已关闭），请手动粘贴");
            return;
        }

        Thread.Sleep(120);   // 目标窗口处理激活消息（可见窗已隐藏，UI 线程短睡无感）
        PasteBack.InjectCtrlV();
    }

    /// <summary>Ctrl+Enter：仅复制，不收窗不粘贴（写完给一行回显）。</summary>
    private void CopyOnly()
    {
        var entry = FirstOrSelected();
        if (entry is null)
        {
            return;
        }

        var error = WriteEntryToClipboard(entry);
        _status.Text = error is null
            ? $"已复制（未直贴）：{_truncate(entry.Preview)}"
            : $"复制失败：{error}";
    }

    private static string _truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private void DeleteFirstOrSelected()
    {
        var entry = FirstOrSelected();
        if (entry is null)
        {
            return;
        }

        _store.Delete(entry.Id);
        _status.Text = $"已删除：{_truncate(entry.Preview)}";
        Reload();
    }

    /// <summary>置顶/取消（Ctrl+P）。无选中 = 第一条。**必须回显**：置顶效果不明显，无声 = 用户感知"没实现"。</summary>
    private void TogglePinFirstOrSelected()
    {
        var entry = FirstOrSelected();
        if (entry is null)
        {
            return;
        }

        _store.SetPinned(entry.Id, !entry.Pinned);
        _status.Text = entry.Pinned
            ? $"已取消置顶：{_truncate(entry.Preview)}"
            : $"已置顶：{_truncate(entry.Preview)}";
        Reload();
    }

    /// <summary>复制（Ctrl+C）。无选中 = 第一条。</summary>
    private void CopyFirstOrSelected()
    {
        var entry = FirstOrSelected();
        if (entry is null)
        {
            return;
        }

        var err = WriteEntryToClipboard(entry);
        _status.Text = err is null ? $"已复制：{_truncate(entry.Preview)}" : $"复制失败：{err}";
    }

    // ────────────────────────────────────────────── OCR 提字（W5-d · FR-15）

    /// <summary>
    /// OCR 菜单项此刻是否应可用（**单一实现点**：菜单 <c>Opened</c> 处理器与探针断言都读它）。
    /// 判据 = 当前选中项是图片 且 没有正在进行的识别。
    /// </summary>
    private bool OcrMenuEnabled => SelectedEntry()?.Kind == ClipKind.Image && !_ocrBusy;

    /// <summary>
    /// 图片条目的"提取文字"：复用 W4 引擎把图里的文字提出来放进系统剪贴板。
    ///
    /// <para><b>R1 纪律照搬</b>（这是本节唯一不能打折的地方）：语言包缺失 / 引擎建不起来时
    /// <b>必须显式引导，禁止静默</b>。出口给两个：状态行（用户还看得见面板）+ 托盘气泡
    /// （用户可能已经按 Esc 收窗，那时状态行没人看得到 —— 面板隐藏后气泡是唯一的嘴）。</para>
    ///
    /// <para>线程姿势与 W4 遮罩窗一致：UI 线程上 <c>await RecognizeAsync</c>（引擎自带
    /// 预处理与缩放，WinRT OCR 是异步的，不阻塞消息泵）。</para>
    /// </summary>
    private async void ExtractTextFromSelectedImage()
    {
        if (_ocrBusy || SelectedEntry() is not { Kind: ClipKind.Image } entry)
        {
            return;
        }

        _ocrBusy = true;
        _status.Text = "正在识别文字…";
        try
        {
            if (!OcrLanguages.IsAvailable)
            {
                // R1：语言包缺失 ⇒ 显式引导。绝不谎报"图片里没有文字"（那是把"我没能力"
                // 说成"你没内容"，S9 家族）。
                _status.Text = "OCR 语言包缺失 —— 需要装 Windows OCR 语言包（见气泡 / `ezt ocr langs`）";
                _notify?.Invoke($"剪贴板历史：本机没有可用的 OCR 语言包，无法提取文字。\n{OcrLanguages.InstallHint}");
                return;
            }

            var full = ResolveImagePath(entry);
            if (full is null)
            {
                _status.Text = "图片文件缺失，无法提取文字";
                return;
            }

            var engine = WindowsOcrEngine.TryCreate(_ocrLanguage);
            if (engine is null)
            {
                _status.Text = $"OCR 引擎创建失败（语言 {_ocrLanguage ?? "自动"} 在本机不可用）";
                _notify?.Invoke("剪贴板历史：OCR 引擎创建失败 —— 先 `ezt ocr langs` 看可用语言包");
                return;
            }

            using var bmp = new Drawing.Bitmap(full);
            var result = await engine.RecognizeAsync(bmp).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                _status.Text = "未识别出文字（这张图里可能确实没有文本）";
                return;
            }

            System.Windows.Clipboard.SetText(result.Text);
            _status.Text = $"已提取文字并复制到剪贴板（{result.Text.Length} 字 · 语言 {engine.LanguageTag}）";
        }
        catch (Exception ex)
        {
            // 图片损坏 / 剪贴板被别的进程占住 / 引擎异常 —— 全部明示，不静默
            _status.Text = $"提取文字失败：{ex.Message}";
        }
        finally
        {
            _ocrBusy = false;
        }
    }

    /// <summary>条目图片在磁盘上的绝对路径；文件不存在返回 null。</summary>
    private string? ResolveImagePath(ClipEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ImagePath) || _store.ImagesDirectory is null)
        {
            return null;
        }

        var full = Path.Combine(_store.ImagesDirectory, entry.ImagePath);
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// 沿可视/逻辑树向上找祖先。两个树都要走：右键命中的可能是模板里的
    /// <c>Run</c>（逻辑节点，<see cref="VisualTreeHelper"/> 对它直接抛异常），
    /// 而中间的容器是可视节点 —— 只用其中一个都会在某一层断掉。
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            try
            {
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            catch (InvalidOperationException)
            {
                return null;   // 非可视也非逻辑节点（极少数）——放弃，返回 null 由调用方降级
            }
        }

        return null;
    }

    // ────────────────────────────────────────────── 键位（命令键由原生输入框上报）

    /// <summary>
    /// 命令键分流。<b>返回 false = 本面板不管这个命令</b>，键放行给原生控件按默认语义处理。
    ///
    /// <para>注意两个条件的归属已下沉到 <see cref="NativeInputBox"/>：Del 只在**输入框为空**时上报
    /// （有字时 Del 是正常的向后删除），Ctrl+C 只在**无选区**时上报（有选区时用户要复制的是文字）。
    /// 这样"文本编辑优先于快捷键"这条规则只有一个实现点，不会在两个窗口里各写一份、各有偏差。</para>
    /// </summary>
    private bool OnInputCommand(InputCmd cmd)
    {
        switch (cmd)
        {
            case InputCmd.Escape:
                Hide();
                return true;

            case InputCmd.Enter when _results.Items.Count > 0:
                ProbeEnterFired = true;
                PasteFirstOrSelected();
                return true;

            case InputCmd.CtrlEnter when _results.Items.Count > 0:
                CopyOnly();
                return true;

            case InputCmd.Down when _results.Items.Count > 0:
                MoveSelection(+1);
                return true;

            case InputCmd.Up when _results.Items.Count > 0:
                MoveSelection(-1);
                return true;

            case InputCmd.Delete:
                DeleteFirstOrSelected();
                return true;

            case InputCmd.CtrlP:
                TogglePinFirstOrSelected();
                return true;

            case InputCmd.CtrlC when _results.Items.Count > 0:
                CopyFirstOrSelected();
                return true;

            default:
                return false;   // 无结果时的 Enter/Ctrl+Enter/Del/Ctrl+P：不吞，交给控件
        }
    }

    /// <summary>列表选中移动（焦点不动）。</summary>
    private void MoveSelection(int delta)
    {
        var target = Math.Clamp(_results.SelectedIndex + delta, 0, _results.Items.Count - 1);
        _results.SelectedIndex = target;
        _results.ScrollIntoView(_results.SelectedItem);
        _input.FocusInput();   // 焦点钉回输入框（WPF 焦点与 Win32 焦点是两套）
    }

    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();

        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        var foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var curThread = GetCurrentThreadId();
        var attached = foreThread != 0 && foreThread != curThread
            && AttachThreadInput(curThread, foreThread, fAttach: true);
        try
        {
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(curThread, foreThread, fAttach: false);
            }
        }
    }

    /// <summary>
    /// 把窗口摆到光标附近（W5-d「视线不跳」）。
    ///
    /// <para><b>为什么用 SetWindowPos 物理像素，而不是设 WPF 的 Left/Top</b>：WPF 的 Left/Top 是
    /// <b>DIP</b>，而"光标在哪台显示器、那台显示器缩放多少"必须先知道位置才能算 —— 鸡生蛋问题。
    /// 走 Win32 这条路径直接用物理像素，跨显示器/混合 DPI 都不需要换算（W4-b 遮罩窗同一教训：
    /// <b>物理像素定位，不做 DIP 估算</b>）。</para>
    ///
    /// <para>越界策略：默认摆在光标右下 12px；右侧/下侧放不下就翻到光标另一侧；仍越界则夹进工作区。
    /// 用 <c>Screen.FromPoint</c> 取<b>光标所在那台</b>显示器的工作区 —— 副屏坐标原点不是 (0,0)，
    /// 那是 W4-b 实测踩过的坑。</para>
    /// </summary>
    private void PositionNearCursor()
    {
        if (!GetCursorPos(out var pt))
        {
            return;   // 拿不到光标就保持上次位置，不猜
        }

        var screen = WinForms.Screen.FromPoint(new Drawing.Point(pt.X, pt.Y));
        var wa = screen.WorkingArea;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!GetWindowRect(hwnd, out var wr))
        {
            return;
        }

        const int gap = 12;
        var w = wr.Right - wr.Left;
        var h = wr.Bottom - wr.Top;

        var x = pt.X + gap;
        var y = pt.Y + gap;
        if (x + w > wa.Right)
        {
            x = pt.X - w - gap;      // 右侧放不下 ⇒ 翻到光标左侧
        }

        if (y + h > wa.Bottom)
        {
            y = pt.Y - h - gap;      // 下侧放不下 ⇒ 翻到光标上方
        }

        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));

        _ = SetWindowPos(hwnd, nint.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 恢复输入框的**默认 IME 上下文关联**（与 SearchWindow 同款修复）。
    /// ★ 目标是**原生 EDIT 的 hwnd** —— IME 上下文属于真正接键的那个窗口；
    /// 挂在 WPF 窗口上等于空转（WPF 是 TSF 窗口，ImmGetContext 恒 NULL）。
    /// </summary>
    private void RestoreImeAssociation()
    {
        _ = ImmAssociateContextEx(_input.InputHwnd, nint.Zero, IACE_DEFAULT);
    }

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint IACE_DEFAULT = 0x00000001;

    private DataTemplate MakeClipTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(ClipItemView));
        factory.SetBinding(ClipItemView.EntryProperty, new Binding());
        factory.SetValue(ClipItemView.ImagesDirectoryProperty, _store.ImagesDirectory);
        return new DataTemplate { VisualTree = factory };
    }

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

    // ── 输入诊断（SearchWindow.LogInputDiag 同款，独立文件）──────────────
    // 用途：换实现后的行为取证 —— CMD 行 = 命令键上报；TEXTCH/FILTER = 过滤结果。
    // 判据："按键没反应"时先看 CMD 行有没有：没有 = 键没到原生控件（系统层），
    // 有 = 键到了但动作没生效（本面板逻辑）。

    /// <summary>诊断日志路径（%TEMP%；写入失败静默——诊断不得拖垮功能）。</summary>
    internal static readonly string DiagPath =
        Path.Combine(Path.GetTempPath(), "eztools-clip-input-diag.log");

    private static void LogInputDiag(string kind, string detail)
    {
        try
        {
            File.AppendAllText(
                DiagPath,
                $"{DateTime.Now:HH:mm:ss.fff} [{kind}] {detail}{Environment.NewLine}");
        }
        catch
        {
            // 诊断写入失败不影响功能
        }
    }

    // ────────────────────────────────────────────── 探针面

    /// <summary>探针模式：抑制直贴的 Ctrl+V 真注入（会贴进运行探针的终端）。剪贴板写入/收窗照常。</summary>
    internal bool ProbeSuppressInject { get; set; }

    internal int ProbeItemCount => _results.Items.Count;
    internal string ProbeStatusText => _status.Text;
    internal string ProbeStatusRight => _statusRight.Text;
    internal bool ProbeIsVisible => Visibility == Visibility.Visible;
    internal bool ProbeIsActive => IsActive;
    internal bool ProbeEnterFired { get; private set; }
    internal nint ProbeHwnd => _input.InputHwnd;
    internal nint ProbeLastForeground => _lastForeground;

    /// <summary>
    /// 键盘焦点是否真落在输入框上（Win32 <c>GetFocus</c> 语义 —— 与搜索窗同款判据升级：
    /// WPF 的"焦点在这"与系统层的焦点可能不一致，只有后者能真的打进字）。
    /// </summary>
    internal bool ProbeQueryFocused => _input.IsInputFocused;

    /// <summary>
    /// 探针：OCR 菜单项此刻**应**是否可用。读的是 <see cref="OcrMenuEnabled"/> ——
    /// 与菜单 <c>Opened</c> 处理器**同一个实现点**，不是复写一遍判据
    ///（复写就会出现"测试通过但菜单行为不同步"的假绿）。
    /// </summary>
    internal bool ProbeOcrMenuEnabled => OcrMenuEnabled;

    /// <summary>探针：选中第 n 条（造"当前选中项是图片 / 不是图片"两种上下文）。</summary>
    internal void ProbeSelectIndex(int index)
    {
        if (index >= 0 && index < _results.Items.Count)
        {
            _results.SelectedIndex = index;
        }
    }

    /// <summary>
    /// 探针：第 n 条的类型（越界 = null）。有了它，探针不必假设"列表按什么顺序排"——
    /// 靠序号的断言在排序规则变动时会静默错位（测的就不是它以为的那条了）。
    /// </summary>
    internal ClipKind? ProbeKindAt(int index) =>
        index >= 0 && index < _current.Count ? _current[index].Kind : null;

    /// <summary>
    /// 探针：走**真实菜单点击链**触发 OCR 提字。
    /// 刻意不直调 <see cref="ExtractTextFromSelectedImage"/> —— 那会绕过"菜单项此刻该不该可用"
    /// 这一环，而那一环恰恰最容易静默失效（恒可用/恒不可用都测不出来）。
    /// </summary>
    internal void ProbeClickExtractText() =>
        _ocrMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    internal bool ProbeIsForeground =>
        GetForegroundWindow() == new WindowInteropHelper(this).Handle;

    /// <summary>探针入口：设输入文本（走真 TextChanged → 真过滤）。</summary>
    internal void SubmitForProbe(string text) => _input.Text = text;

    /// <summary>
    /// 外部数据变更（监听层捕获了新条目）。可见时才刷新 —— 隐藏面板本就"每次唤出重拉"，
    /// 不需要为此唤醒 Dispatcher（W5-c 监听接线）。
    /// </summary>
    internal void OnExternalChange()
    {
        if (Visibility == Visibility.Visible && IsLoaded)
        {
            _ = Dispatcher.BeginInvoke(Reload);
        }
    }

    /// <summary>真关闭（绕过 X=隐藏拦截）。仅托盘退出与探针收尾可调。</summary>
    internal void RealClose()
    {
        _realClose = true;
        Close();
    }

    internal void CloseForProbe()
    {
        try
        {
            RealClose();
        }
        catch (InvalidOperationException)
        {
            // 未 Show 过的窗口 Close 抛异常——窗口本就没起来，无需清理
        }
    }

    // ────────────────────────────────────────────── Win32

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmAssociateContextEx(nint hWnd, nint hIMC, uint dwFlags);

    // ── 跟随光标定位（W5-d）─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    // ────────────────────────────────────────────── 条目视图

    /// <summary>过滤下拉的数据项。</summary>
    private sealed record KindOption(ClipKind? Kind, string Label);

    /// <summary>单条历史的渲染控件（预览 + 元信息行；图片条目带缩略图）。FEF 要求公开无参构造。</summary>
    public sealed class ClipItemView : StackPanel
    {
        public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
            nameof(Entry), typeof(ClipEntry), typeof(ClipItemView),
            new PropertyMetadata(null, OnEntryChanged));

        /// <summary>图片相对路径的解析基准（由面板在装载模板时注入；测试可为 null）。</summary>
        public static readonly DependencyProperty ImagesDirectoryProperty = DependencyProperty.Register(
            nameof(ImagesDirectory), typeof(string), typeof(ClipItemView),
            new PropertyMetadata(null));

        public ClipEntry? Entry
        {
            get => (ClipEntry?)GetValue(EntryProperty);
            set => SetValue(EntryProperty, value);
        }

        public string? ImagesDirectory
        {
            get => (string?)GetValue(ImagesDirectoryProperty);
            set => SetValue(ImagesDirectoryProperty, value);
        }

        public ClipItemView()
        {
            Orientation = Orientation.Horizontal;
            Margin = new Thickness(0, 2, 0, 2);
        }

        private static void OnEntryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = (ClipItemView)d;
            // ⚠️ Recycling 复用容器：不清 Children 会导致条目叠显（SearchWindow.HitText 同坑）
            panel.Children.Clear();

            if (e.NewValue is not ClipEntry entry)
            {
                return;
            }

            if (entry.Kind == ClipKind.Image)
            {
                var thumb = panel.MakeThumbnail(entry);
                if (thumb is not null)
                {
                    panel.Children.Add(new Border
                    {
                        Width = 48,
                        Height = 48,
                        Margin = new Thickness(0, 0, 8, 0),
                        Child = new Image { Source = thumb, Stretch = Stretch.Uniform },
                    });
                }
            }

            var preview = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
            if (entry.Pinned)
            {
                preview.Inlines.Add(new Run("📌 ") { Foreground = Brushes.Goldenrod });
            }

            preview.Inlines.Add(new Run(entry.Preview.Length == 0 ? "（无预览）" : entry.Preview));
            if (entry.CopyCount > 1)
            {
                preview.Inlines.Add(new Run($"  ×{entry.CopyCount}") { Foreground = Brushes.DodgerBlue, FontSize = 11 });
            }

            var meta = new TextBlock
            {
                Text = MetaText(entry),
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 560,
            };

            var lines = new StackPanel { Orientation = Orientation.Vertical };
            lines.Children.Add(preview);
            lines.Children.Add(meta);
            panel.Children.Add(lines);
        }

        private static string MetaText(ClipEntry entry)
        {
            var kind = entry.Kind switch
            {
                ClipKind.Image => "图片",
                ClipKind.FileList => "文件",
                _ => "文本",
            };
            var source = string.IsNullOrEmpty(entry.SourceApp) ? "" : $" · {entry.SourceApp}";
            return $"{kind}{source} · {entry.LastUsedAt:MM-dd HH:mm}";
        }

        private BitmapSource? MakeThumbnail(ClipEntry entry)
        {
            var dir = ImagesDirectory;
            if (string.IsNullOrEmpty(entry.ImagePath) || string.IsNullOrEmpty(dir))
            {
                return null;
            }

            var full = Path.Combine(dir, entry.ImagePath);
            if (!File.Exists(full))
            {
                return null;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;   // 缩略图按 96px 解码（懒生成、省内存）
                bmp.UriSource = new Uri(full);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception)
            {
                return null;   // 文件坏/被删：占位 = 没有缩略图，不拖垮整行渲染
            }
        }
    }
}
