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

    private readonly NativeInputBox _input;
    private readonly ListBox _results;
    private readonly ComboBox _kindFilter;
    private readonly TextBlock _status;
    private readonly TextBlock _statusRight;

    private bool _realClose;            // 仅托盘退出/探针收尾置位（X=隐藏）
    private bool _summoning;            // 唤出期屏蔽瞬态 Deactivated
    private bool _suppressReload;       // 程序化设文本不得反过来触发过滤（21.6 同族）
    private nint _lastForeground;       // 唤出前的前台窗口（直贴还原目标；0 = 未知）
    private List<ClipEntry> _current = new();

    public ClipboardHistoryPanel(HistoryStore store, Action<string>? notify = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notify = notify;

        Title = "Eztools 剪贴板历史";
        Width = 680;
        Height = 520;
        MinWidth = 440;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
