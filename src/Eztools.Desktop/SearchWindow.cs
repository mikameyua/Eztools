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
using System.Windows.Threading;
using Eztools.Contracts;
using Eztools.Host.Search;
using InputCmd = Eztools.Desktop.NativeInputBox.InputCommand;

namespace Eztools.Desktop;

/// <summary>
/// 原生搜索窗（W3-d-1）：全局热键唤出、打字出结果、失焦隐藏。
///
/// 与 <see cref="PanelWindow"/> 同构但不复用：面板是"声明式节点 + 拉取"模型，
/// 搜索窗是"输入驱动 + 实时查询"模型 —— 共享的只有主题挂载与"绝不在 UI 线程同步等"两条纪律。
///
/// <b>模型与职责边界（实施计划 W3-d-2 的"绝不在 UI 侧重算"）</b>：
/// 匹配、打分、高亮区间**全部由索引进程回传**（<see cref="SearchHitDto.Highlights"/>，
/// UTF-16 code unit 口径），本窗口只做渲染 —— Run 分段着色按回传区间切，不重算匹配位置。
///
/// <b>线程模型</b>：查询回调在线程池线程上到（<see cref="SearchSession"/> 不 marshal），
/// 必须 <see cref="System.Windows.Threading.Dispatcher"/> 回 UI 线程再碰控件；BeginInvoke 是
/// 非阻塞投递，不会形成"UI 等 UI"死锁环（托盘气泡的教训只适用于"同步等待"，两处条件不同）。
///
/// <b>隐藏而非关闭</b>：失焦 / Esc = <see cref="Hide"/>（窗口与查询状态保留，再唤出即上次结果 ——
/// 搜索的复用场景是"反复找不同的文件"，清空上次结果反而是惩罚）。真关闭才释放会话。
/// 索引进程不归本窗口所有 —— 由 TrayApplication 持有 <see cref="SearchIndexProcess"/>。
/// </summary>
public sealed class SearchWindow : Window
{
    /// <summary>
    /// 结果列表的虚拟化模板（W3-d-2）。写成 XAML 而不是 FEF 构造：结构就是"可评审的规格"——
    /// <c>ScrollViewer</c>（虚拟化的滚动宿主）必须**直接**包住 <c>ItemsPresenter</c>，
    /// 中间不能夹任何会给出无限高度约束的容器（夹了 = 虚拟化静默失效）。
    /// 这是实测（--probe-search-ui）逼出来的：默认/主题模板不保证这个结构。
    /// </summary>
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

    private readonly SearchSession _session;
    private readonly SearchIndexClient _client;

    private readonly NativeInputBox _input;
    private readonly ListBox _results;
    private readonly TextBlock _status;
    private readonly TextBlock _statusRight;
    private readonly TextBlock _volumesLine;

    private bool _suppressQueryEvents;   // 程序化设值不得反过来触发查询（21.6 同族）
    private bool _sessionDisposed;
    private bool _pauseBusy;             // 暂停/恢复往返中（防连点发出并发请求）
    private bool _realClose;             // 仅托盘退出/探针收尾置位：绕过"X=隐藏"拦截真关闭

    // ── 渲染观测（W3-d-2 断言面：--probe-search-ui 读这些值落盘）──
    private SearchQueryResponse? _lastResponse;
    private double _lastBuildMs;
    private double _lastRenderMs;

    public SearchWindow(SearchIndexClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _session = new SearchSession(client, schedule: ScheduleThrottleFire);

        Title = "Eztools 搜索";
        Width = 660;
        Height = 480;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;                    // 热键召唤的窗必须在最前 —— 用户的意图就是"立刻搜"
        ShowInTaskbar = false;             // 临时浮层不占任务栏（Everything 同款）
        UseLayoutRounding = true;

        ApplyModernTheme();

        // ★ 2026-09-27 换实现：原生 EDIT（WindowsFormsHost）替代 WPF TextBox。
        //   理由与边界见 NativeInputBox 类头。要点：本窗**不再向输入框供字** ——
        //   合成层/轮询层/LL 钩子/PreviewTextInput 拦截全部删除（§2.29 教训制度化）。
        _input = new NativeInputBox();
        _input.TextChanged += () =>
        {
            if (!_suppressQueryEvents)
            {
                _session.Submit(_input.Text);
            }
        };
        _input.Command = OnInputCommand;

        _results = new ListBox
        {
            Margin = new Thickness(12, 0, 12, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = MakeHitTemplate(),
        };

        // ★ 焦点常驻输入框（Everything 模式）⇒ 列表必然失焦，默认"失焦选中色"太淡看不见
        //   —— 双保险：① 覆盖此 ListBox 范围内的系统选中刷；② ItemContainerStyle 的
        //   IsSelected 触发器直接上不透明浅蓝底（不依赖主题模板是否引用 SystemColors）。
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

        // 虚拟化（W3-d-2 硬检查项）。**必须显式三件套 + 自带模板** —— 实测（--probe-search-ui）：
        // iNKORE 主题的 ListBox 模板不给 ItemsPresenter 配 ScrollContentPresenter 宿主，
        // VirtualizingStackPanel 的 ScrollOwner 为空 ⇒ 退化成普通 StackPanel，200 条生成 200 个容器
        //（"虚拟化名义已开、实际没发生"）。只设 IsVirtualizing 完全不够，必须自己保证结构。
        //   ① 显式 ControlTemplate：Border > ScrollViewer(CanContentScroll=True) > ItemsPresenter
        //      —— 这是 WPF 虚拟化的**结构前提**，不依赖任何主题；
        //   ② ItemsPanel 显式挂 VirtualizingStackPanel；
        //   ③ IsVirtualizing + Recycling（附加属性只能 SetValue，无静态 C# 访问器 —— CS0117 的由来）；
        //   ④ CanContentScroll=true（滚动按"项"而非"像素"）。
        _results.Template = (ControlTemplate)XamlReader.Parse(VirtualizingListTemplateXaml);
        _results.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(
            new FrameworkElementFactory(typeof(VirtualizingStackPanel))));
        _results.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _results.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _results.SetValue(ScrollViewer.CanContentScrollProperty, true);

        _status = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "输入以搜索（Enter 打开 · Ctrl+Enter 定位 · Ctrl+C 复制路径）",
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

        // 卷清单行（W3-e-2 "跳过必须可见"）：哪些卷进了索引、哪些被跳过、**为什么**。
        // 不显示的话，用户看到搜索不到某个盘的文件，只能猜（"是没索引还是没匹配？"）。
        // 2026-09-25 起这一行还兼任**暂停徽标**（缺口①）：暂停后结果会静默变旧，
        // 不显示就等于拿过时的数据骗用户（S9′ 家族）。
        _volumesLine = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.55,
            Margin = new Thickness(12, 0, 12, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "",
        };
        _volumesLine.MouseLeftButtonUp += (_, _) => OnVolumesLineClick();

        var root = new DockPanel();
        DockPanel.SetDock(_input, Dock.Top);
        DockPanel.SetDock(statusGrid, Dock.Bottom);
        DockPanel.SetDock(_volumesLine, Dock.Bottom);
        root.Children.Add(_input);
        root.Children.Add(statusGrid);
        root.Children.Add(_volumesLine);
        root.Children.Add(_results);
        Content = root;

        // 全窗口键位（设计方案 §6.4）：
        // ★ 2026-09-27 起**不再挂 PreviewKeyDown / PreviewTextInput**。原因是结构性的：
        //   输入框已是宿主的原生 EDIT（独立子窗口），它的键盘消息根本不进 WPF 输入管线 ——
        //   在这里挂 WPF 事件既收不到键，又必然与原生控件形成"两个供字者"（§2.29）。
        //   命令键改由 _input.Command 上报（见 OnInputCommand），文本编辑全权归原生控件。

        // 失焦自动隐藏（W3-d-1 验收②）。热键窗的交互契约：切走即收，唤出即搜。
        // ★ Summon() 强制前台期间会抖出瞬态 Deactivated，由 _summoning 屏蔽（否则刚唤出就被藏掉）。
        Deactivated += (_, _) => { if (!_summoning) Hide(); };

        // ★ 标题栏 X / Alt+F4 = 收起，不是关闭（2026-09-25 实测教训：用户点一次 X，窗口真关闭，
        //   之后每次热键 Show() 抛 InvalidOperationException 被 ToggleSearchWindow 吞掉 ⇒
        //   "第一次能唤出，以后热键全灭"。常驻唤出窗的 X 一律当隐藏（Everything 同款），
        //   真关闭只走 RealClose()（托盘退出/探针收尾）。
        Closing += (_, e) =>
        {
            if (_realClose)
            {
                return;
            }

            e.Cancel = true;
            Hide();
        };

        // ★ 2026-09-27：Enter/↓/↑/Esc 的 15ms GetAsyncKeyState 轮询兜底**整体删除**。
        //   它存在的前提是"消息层会按 VK 吞键"—— 那个观测是在我们自己那套干预（合成层 +
        //   WPF TSF 文本栈）污染下测出来的；换成原生 EDIT 后输入走 IMM32，WPF 的 TSF
        //   文本栈不再参与，观测前提消失。若真机上仍出现吞键（DiagPath 里 KEY 行缺失可判），
        //   再按"单入口 + 去重闸"的方式补一条定向兜底 —— 而不是先把兜底堆回去。

        _session.OnResults = response =>
        {
            // 线程池回调 → 回 UI 线程（非阻塞投递，见类头线程模型）
            _ = Dispatcher.BeginInvoke(() => RenderResults(response));
        };
        _session.OnError = ex =>
        {
            _ = Dispatcher.BeginInvoke(() => RenderError(ex));
        };

        Closed += (_, _) => DisposeSession();
    }

    /// <summary>唤出（已可见则仅激活到前台）。TrayApplication 的热键入口。</summary>
    public void Summon()
    {
        // 强制前台过程中焦点/激活事件会抖动（AttachThreadInput 可能瞬态 Deactivated），
        // 不屏蔽的话"失焦自动隐藏"会把刚唤出的窗口立刻藏掉 —— C2/G1 手工实测抓出的坑。
        _summoning = true;
        try
        {
            if (Visibility != Visibility.Visible)
            {
                Show();
            }

            ForceForeground();
            Activate();
            _input.FocusInput();
            _input.SelectAll();
            RestoreImeAssociation();  // ★ 抢焦点副作用修复：见方法注释（英文直通字符不进框）
            RefreshVolumeSummary();   // ★ 每次唤出都重拉：首帧可能撞上自举未完成（否则停在旧快照）

            // ★ 前台完全建立后再钉一次焦点：ForceForeground 的激活消息可能在 Summon 返回、
            //   消息泵恢复后才落地；Win32 焦点真正落位后再 Focus 一次，双保险。
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

    private bool _summoning;

    /// <summary>
    /// 强制本窗口到前台并拿到键盘焦点（**三级降级**，2026-09-25 手工实测迭代）。
    ///
    /// ★ Windows 前台锁定：托盘是后台进程，直接 <c>Activate()</c> 会被系统<b>静默拒绝</b>。
    ///
    /// <para><b>为什么不能每次都走 AttachThreadInput</b>（v1 实测反例）：附加/分离输入队列会
    /// 同步并搞坏本线程的<b>键盘直通链</b>——表现：Esc/Ctrl+V/IME 中文组合全正常（KeyDown 与
    /// IME 投递路径完好），但**一切无组合直通字符**（微软拼音英文模式、CapsLock、ENG 纯英文
    /// 键盘）不进框——TranslateMessage 生成的 WM_CHAR 再也到不了 TextBox。为此改成：</para>
    ///
    /// ① 快路径：已是前台直接返回（二次唤出零副作用）；
    /// ② 直接 <see cref="SetForegroundWindow"/>（热键/Show 之后系统时常放行；成功即零副作用）；
    /// ③ <b>ALT-trick</b>（Everything 同款）：模拟一次 Alt 按下/抬起——系统把"刚处理完用户
    ///    输入"的进程放进前台白名单，同样不碰输入队列；
    /// ④ AttachThreadInput 组合拳（<b>最后兜底</b>）：能硬抢但会搅乱 IME 关联与键盘直通
    ///    （参见 <see cref="RestoreImeAssociation"/>），只在前三级都失败时使用。
    /// </summary>
    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();

        // ① 已是前台（二次唤出/已激活）
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        // ② 直接尝试
        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        // ③ ALT-trick：模拟 Alt 按下/抬起（不碰输入队列；轻微代价 = 原前台程序的菜单栏
        //    可能闪一下 Alt 模式，Everything/各大启动器均接受此代价）
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        // ④ AttachThreadInput 兜底
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
                RestoreImeAssociation();   // 兜底路径的已知副作用，事后修复
            }
        }
    }

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

    /// <summary>Alt 虚拟键码（<see cref="keybd_event"/> ALT-trick 用）。</summary>
    private const byte VK_MENU = 0x12;

    /// <summary>按键抬起标志（<see cref="keybd_event"/> 用）。</summary>
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// 恢复本窗口的**默认 IME 上下文关联**（2026-09-25 手工实测抓出的修复）。
    ///
    /// 症状链：微软拼音**中文模式**打字正常（组合条→上屏都进搜索框），但 **CapsLock /
    /// Shift 切到英文模式后字符完全不进框**——输入框里一个字母都没有，且**鼠标点击
    /// 输入框也无法恢复**（点击重建 Win32 焦点但不动 IME 关联）；记事本一切正常。
    ///
    /// 根因：AttachThreadInput 附加/分离输入队列的过程会同步并打乱 IME 上下文与
    /// 焦点窗口的关联 —— IME 组合路径靠 IME 主动投递所以幸存，无组合的英文直通
    /// 路径依赖正确的 IME 关联所以全灭。修法：显式把默认 HIMC 重新关联到本窗口
    /// （IACE_DEFAULT；同类抢焦点工具的标准恢复手段）。对关联正常的窗口是无害 no-op。
    /// </summary>
    private void RestoreImeAssociation()
    {
        // ★ 2026-09-27 随换实现修正：IME 上下文属于**真正接键的那个窗口**。
        //   以前挂在本窗（WPF）上等于空转 —— WPF 是 TSF 窗口，根本没有 IMM32 上下文
        //  （实测 ImmGetContext 恒返回 NULL）。现在输入框是原生 EDIT，必须挂到它的 hwnd。
        _ = ImmAssociateContextEx(_input.InputHwnd, nint.Zero, IACE_DEFAULT);
    }

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmAssociateContextEx(nint hWnd, nint hIMC, uint dwFlags);

    /// <summary>恢复默认 IME 上下文（<see cref="ImmAssociateContextEx"/> 的标志位）。</summary>
    private const uint IACE_DEFAULT = 0x00000001;

    // ── Enter / ↓ / ↑ / Esc：由原生输入框的命令事件送达（2026-09-27 换实现）────────
    //
    // 这里原来有 15ms GetAsyncKeyState 轮询兜底 + hwnd 消息钩子 + 直通字符合成层三层干预。
    // 三层全部删除，原因见 NativeInputBox 类头与 §2.29：它们各自是"上一个补丁的副作用"
    // 的解药，叠加即互相双打。换到原生 EDIT 后命令键由控件自己在消息层上报
    // （<see cref="NativeInputBox.Command"/>），**不再需要任何并行观测面**。

    // ── 打字输入：全部交给原生 EDIT（2026-09-27 换实现）─────────────────────
    //
    // 这里原来是"轮询打字兜底"（GetAsyncKeyState 逐键合成 + TypingWatchVk 键表 +
    // MapVkToChar/_vkSymbol 布局表 + PollPressed 按下沿判据，约 120 行）。
    // 它解决的从来不是输入问题，而是**我们自己造成的问题**：AttachThreadInput 打坏直通链
    // 之后，才需要自己合成字符；合成层又与 IME 双打，于是又加互斥开关…… 换到原生 EDIT 后
    // 这条链整体不存在 —— 字符由系统输入法经 WM_CHAR 交给原生控件，本窗不碰。

    // ── 键到达观测（诊断/探针面）────────────────────────────────────────────
    //
    // 这里原来有一个 HwndSource.AddHook 的 hwnd 级消息钩子（记录原始 VK + 直接执行 Enter）。
    // 删除原因有二：① 原生 EDIT 的键盘消息进的是**它自己的子窗口**，挂在 WPF 主窗上的钩子
    // 根本看不见（挂着=死探针面，§2.27④）；② 它顺手执行 Enter 动作会与原生控件的命令事件
    // 重复触发。观测面改为读 <see cref="NativeInputBox.LastVk"/>（同一个读数，无第二套逻辑）。

    // ── 直通字符诊断（2026-09-25 手工调查用；量极小：只在窗口可见时按键才有行）──────

    /// <summary>诊断日志路径（%TEMP%，用户复现后取走分析；写入失败静默——诊断不得拖垮功能）。</summary>
    internal static readonly string DiagPath =
        Path.Combine(Path.GetTempPath(), "eztools-search-input-diag.log");

    /// <summary>落一行输入诊断（KEY/CHAR）。异常吞掉：诊断设施永不影响功能。</summary>
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
            // 诊断写入失败（文件锁/权限）不吞功能 —— 空catch即诊断自身降级
        }
    }

    // ── 直通字符合成层：已整体移除（2026-09-27 换实现）──────────────────────
    //
    // 这里原来是本文件最"重"的一段：IsNativeComposition（问 IME 现在是中文还是英文）+
    // SynthesizeDirectChar（自己按 US 键位表把按键翻成字符）+ MapKeyToChar + 数字/符号映射表。
    // 它的存在本身就是一个诊断结论：**抢前台打坏了系统直通链，所以不得不自己造字符**。
    //
    // 删除它的理由不是"简化代码"，而是它**在原生 EDIT 上无法成立也不必要**：
    //   · 不必要 —— 原生 EDIT 的字符由系统输入法经 WM_CHAR 直送，不经任何中间层；
    //   · 不成立 —— 它的分流判据 ImmGetContext 在 WPF 窗口上恒返回 NULL（WPF 是 TSF 窗口），
    //     搬过来只会把"永远走合成分支"这个错误前提带进新架构（§2.29 的"名存实亡判据"）。
    // 与它配套的 PreviewTextInput 拦截（防双打）也随之删除 —— 没有合成层就没有双打。

    /// <summary>
    /// 强制重查卷清单 + 暂停态（暂停/恢复后**必须**调用 —— 否则徽标停在旧值，
    /// 那是"显示了但显示的是错的"，比不显示更坏）。
    /// </summary>
    internal void RefreshVolumeSummary() => _ = LoadVolumeSummaryAsync();

    private async Task LoadVolumeSummaryAsync()
    {
        try
        {
            var status = await _client.StatusAsync().ConfigureAwait(false);
            var localDrives = SafeLocalDrives();   // 线程池侧收集，不碰 UI 线程
            _ = Dispatcher.BeginInvoke(() => ApplyVolumeSummary(status, localDrives));
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(() => _volumesLine.Text = $"卷清单不可用：{ex.Message}");
        }
    }

    /// <summary>
    /// 最近一次由 status 确认的暂停态（null = 还没查过）。**托盘「搜索索引」菜单用它显示当前态** ——
    /// 菜单在 UI 线程同步构建，不能为了一个勾选态去同步等管道往返（见 TrayApplication）。
    /// </summary>
    internal bool? KnownPaused { get; private set; }

    /// <summary>
    /// 探针抑制卷漂移提示：假传输的卷清单 vs 探针进程读到的真实盘符会产生**环境依赖**的
    /// 提示文本（断言不确定）—— 探针关掉，漂移逻辑由纯函数直测覆盖（见 DetectVolumeDrift）。
    /// </summary>
    internal bool ProbeSuppressDrift { get; set; }

    /// <summary>
    /// 把 status 落到 UI（暂停徽标的**样式**也跟着变 —— 只有文字变化在灰色小字里看不出来）。
    /// ★ 未就绪快照（<see cref="SearchStatusDto.Ready"/>=false）**不能**落地成"已索引 0 个卷 · 0 项"——
    /// 那是把"还没好"显示成"没东西"（S9 家族：数字可见但语义是错的）；改显示"准备中"并轮询到就绪。
    /// </summary>
    private void ApplyVolumeSummary(SearchStatusDto status, IReadOnlyList<string> localDrives)
    {
        KnownPaused = status.Paused;
        if (!status.Ready)
        {
            _volumesLine.Text = "索引准备中…（后台自举进行中，结果暂不可搜）";
            ScheduleVolumePoll();
            return;
        }

        var text = DescribeVolumes(
            status.Volumes.Count, status.TotalFiles, status.Skipped, status.Failed, status.Paused);

        // ★ 卷漂移提示（热插拔已知限制的可见性补丁，设计方案 §7.4.1）：卷清单是自举快照，
        //   运行期插拔的盘不会自动进出索引 —— 提示写在卷清单行这个已有的可见处，不做即 S9′。
        if (!ProbeSuppressDrift)
        {
            var drift = DetectVolumeDrift(status.Ready, status.Volumes, localDrives);
            if (drift is not null)
            {
                text += $" · {drift}";
            }
        }

        _volumesLine.Text = text;
        _volumesLine.Opacity = status.Paused ? 1.0 : 0.55;
        _volumesLine.FontWeight = status.Paused ? FontWeights.SemiBold : FontWeights.Normal;
        _volumesLine.Cursor = status.Paused ? Cursors.Hand : Cursors.Arrow;
        _volumesLine.ToolTip = status.Paused ? "点击恢复索引更新" : null;
    }

    /// <summary>
    /// 卷漂移提示（**纯函数**，探针与 UI 共用）：对比"索引里的卷"与"本机当前盘符"，
    /// 有差异（新插入未索引 / 已移除仍在索引）返回提示文案，一致返回 null。
    /// 差异本身**不自动处理**（热插拔是已登记的 V1 已知限制，设计方案 §7.4.1），
    /// 只做可见性 —— 重启工具生效。
    /// </summary>
    internal static string? DetectVolumeDrift(
        bool ready, IReadOnlyList<string> indexedVolumes, IReadOnlyList<string> localDrives)
    {
        if (!ready)
        {
            return null;
        }

        var indexed = indexedVolumes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = localDrives.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        var added = local.Where(d => !indexed.Contains(d)).OrderBy(d => d).ToList();
        if (added.Count > 0)
        {
            parts.Add($"检测到未索引的卷 {string.Join("、", added)}（重启工具可纳入索引）");
        }

        var removed = indexed.Where(d => !local.Contains(d)).OrderBy(d => d).ToList();
        if (removed.Count > 0)
        {
            parts.Add($"索引含已移除的卷 {string.Join("、", removed)}（重启工具可刷新）");
        }

        return parts.Count > 0 ? string.Join("；", parts) : null;
    }

    /// <summary>本机当前盘符（固定盘 + 可移动盘，归一为 "C:" 形态；IsReady 才计入）。</summary>
    private static string[] SafeLocalDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d =>
            {
                try
                {
                    return d.IsReady ? d.Name[..1] + ":" : null;
                }
                catch
                {
                    return null;   // 个别盘符查询抛异常（虚拟盘等）——跳过，不拖垮状态行
                }
            })
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d)
            .ToArray();
    }

    private bool _volumePollPending;

    /// <summary>未就绪轮询排程（500 ms 后重拉；窗口隐藏即停，防堆积用单一 pending 标志）。</summary>
    private void ScheduleVolumePoll()
    {
        if (_volumePollPending || Visibility != Visibility.Visible)
        {
            return;
        }

        _volumePollPending = true;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(true);
            _volumePollPending = false;
            if (Visibility == Visibility.Visible && IsLoaded)
            {
                _ = LoadVolumeSummaryAsync();
            }
        });
    }

    /// <summary>
    /// 点卷清单行 = 恢复索引（**只在暂停时可点**）。
    /// 刻意不做"未暂停时点击即暂停"：那会让误点变成一个**静默**的停更操作 ——
    /// 暂停是低频动作，必须走显式入口（托盘菜单 / CLI），见缺口① 的设计取舍。
    /// </summary>
    private void OnVolumesLineClick()
    {
        if (_pauseBusy || _volumesLine.ToolTip is null)
        {
            return;
        }

        _ = SetPauseAsync(false);
    }

    /// <summary>暂停/恢复索引（托盘与状态行共用）。失败**显式回显**到状态行，不静默。</summary>
    internal async Task SetPauseAsync(bool pause)
    {
        if (_pauseBusy)
        {
            return;
        }

        _pauseBusy = true;
        try
        {
            var now = pause
                ? await _client.PauseIndexingAsync().ConfigureAwait(false)
                : await _client.ResumeIndexingAsync().ConfigureAwait(false);

            await Dispatcher.BeginInvoke(() =>
            {
                _status.Text = now
                    ? "索引更新已暂停（结果可能过时；点底部状态行或托盘菜单可恢复）"
                    : "索引更新已恢复";
                RefreshVolumeSummary();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.BeginInvoke(() => _status.Text = $"{(pause ? "暂停" : "恢复")}索引失败：{ex.Message}");
        }
        finally
        {
            _pauseBusy = false;
        }
    }

    /// <summary>
    /// 卷清单摘要文案（**纯函数**，探针与 UI 共用 —— 断言文本时验的就是这一份）。
    /// 三段式：**已索引 / 跳过 / 失败**。有跳过卷时**必须**列出原因：只说"跳过了 1 个卷"
    /// 等于把问题原样丢回给用户；失败卷同理，而且**必须与跳过分开说** ——
    /// "没试过"（跳过）与"试了没成"（失败）是两个不同的下一步动作（改配置 vs 修环境）。
    ///
    /// <paramref name="paused"/> = true 时把徽标放在**最前面**：这行有
    /// <see cref="TextTrimming.CharacterEllipsis"/>，放末尾会被卷清单挤掉 ——
    /// "暂停必须可见"若被截断就等于没做。
    /// </summary>
    internal static string DescribeVolumes(
        int indexedCount,
        int totalFiles,
        IReadOnlyList<SkippedVolumeDto> skipped,
        IReadOnlyList<FailedVolumeDto> failed,
        bool paused = false)
    {
        var text = $"已索引 {indexedCount} 个卷 · {totalFiles:N0} 项";
        if (skipped.Count > 0)
        {
            var reasons = string.Join("、", skipped.Select(s => s.ReasonText).Distinct());
            text += $" · 跳过 {skipped.Count} 个卷（{reasons}）";
        }

        if (failed.Count > 0)
        {
            // 卷名必须在 —— 只说"失败 1 个卷"用户得自己去猜是哪个盘
            var detail = string.Join("、",
                failed.Select(f => $"{f.Volume} {f.ReasonText}").Distinct());
            text += $" · 索引失败 {failed.Count} 个卷（{detail}）";
        }

        return paused ? $"索引已暂停（结果可能过时，点此恢复） · {text}" : text;
    }

    /// <summary>可见则隐藏，否则唤出（热键二段语义：再按一次收起）。</summary>
    public void Toggle()
    {
        if (Visibility == Visibility.Visible && IsActive)
        {
            Hide();
        }
        else
        {
            Summon();
        }
    }

    // ── 键位（2026-09-27：命令键由原生输入框上报，本窗不再监听 WPF 键盘事件）────

    /// <summary>
    /// 输入框上报的命令键。<b>返回值即"是否处理"</b>：返回 false = 本窗不管这个命令，
    /// 键放行给原生控件按默认语义处理。**绝不用"静默吞掉"表达"我不关心"** ——
    /// 那会让用户以为快捷键坏了（M2 反馈①"Ctrl+P 没实现"就是同族形态：无选中时静默不动作）。
    /// </summary>
    private bool OnInputCommand(InputCmd cmd)
    {
        LogInputDiag("CMD", cmd.ToString());

        switch (cmd)
        {
            case InputCmd.Escape:
                Hide();
                return true;

            // Enter：有选中项打开选中项；无选中项（打完字直接回车的最高频路径）默认打开第一条。
            case InputCmd.Enter when _results.Items.Count > 0:
                ProbeEnterFired = true;
                LogInputDiag("ENTER", $"count={_results.Items.Count} sel={_results.SelectedIndex}");
                OpenFirstOrSelected(reveal: false);
                return true;
            case InputCmd.Enter:
                // 有 Enter 无结果：记录形态（竞态/查询失败一目了然），不改变行为。
                LogInputDiag("ENTER", $"no results: count={_results.Items.Count} status={_status.Text} right={_statusRight.Text}");
                return true;

            // Ctrl+Enter：资源管理器定位（同样的 FirstOrSelected 语义）。
            case InputCmd.CtrlEnter when _results.Items.Count > 0:
                ProbeEnterFired = true;
                OpenFirstOrSelected(reveal: true);
                return true;

            // ↓/↑：焦点**始终留在输入框**（Everything 模式）—— 导航只动 SelectedIndex，
            // 绝不把键盘焦点交给列表：焦点一进列表就打不了字了（旧方案的历史教训）。
            case InputCmd.Down when _results.Items.Count > 0:
                MoveSelection(+1);
                return true;
            case InputCmd.Up when _results.Items.Count > 0:
                MoveSelection(-1);
                return true;

            // Ctrl+C：只在"有选中结果"时接管（复制路径）；否则返回 false，
            // 让原生控件执行普通的"复制我选中的文字"。
            case InputCmd.CtrlC when SelectedHit() is { } copyHit:
                try
                {
                    Clipboard.SetText(copyHit.Path);
                    _status.Text = $"已复制路径：{copyHit.Path}";
                }
                catch (Exception ex)
                {
                    // 剪贴板被其它进程占用是真实场景（尤其远程桌面），明示而非静默
                    _status.Text = $"复制失败：{ex.Message}";
                }

                return true;

            // Del / Ctrl+P：搜索窗没有对应语义 —— 放行，不吞。
            default:
                return false;
        }
    }

    /// <summary>列表选中移动（焦点不动，见 <see cref="OnInputCommand"/>）。</summary>
    private void MoveSelection(int delta)
    {
        var target = Math.Clamp(_results.SelectedIndex + delta, 0, _results.Items.Count - 1);
        _results.SelectedIndex = target;
        _results.ScrollIntoView(_results.SelectedItem);
        _input.FocusInput();   // 导航后把焦点钉回输入框（原生控件与 WPF 焦点是两套，必须显式复位）
        LogInputDiag("NAV", $"sel={target}");
    }

    private SearchHitDto? SelectedHit() => _results.SelectedItem as SearchHitDto;

    /// <summary>
    /// Enter / Ctrl+Enter 的动作：选中项优先，无选中项取第一条（打完字直接回车是最高频路径）。
    /// <paramref name="reveal"/>=true 走资源管理器定位，false 走打开 —— 这个分流**由输入框
    /// 上报的命令直接给出**（Enter vs Ctrl+Enter），不再靠"读 Keyboard.Modifiers 猜"：
    /// 原生控件在子窗口里，WPF 的 Keyboard.Modifiers 未必反映真实修饰键状态。
    /// </summary>
    private void OpenFirstOrSelected(bool reveal)
    {
        if (_results.Items.Count == 0)
        {
            LogInputDiag("OPEN", "skip: count=0");
            return;
        }

        var hit = SelectedHit() ?? _results.Items[0] as SearchHitDto;
        if (hit is null)
        {
            LogInputDiag("OPEN", "skip: hit null");
            return;
        }

        LogInputDiag("OPEN", $"hit={hit.Path} reveal={reveal}");
        if (reveal)
        {
            RevealInExplorer(hit);
        }
        else
        {
            OpenFile(hit);
        }
    }

    // ── 动作（打开 / 定位 / 复制）─────────────────────────────────────────

    private void OpenFile(SearchHitDto hit)
    {
        try
        {
            // 路径由索引进程返回（设计方案 §6.4 红线：UI 不拼路径 —— 盘符映射/UNC 只有索引侧知道）
            using var _ = Process.Start(BuildOpenStartInfo(hit));
            Hide();   // 打开成功 = 搜索完成，收窗（Everything 同款）
        }
        catch (Exception ex)
        {
            _status.Text = DescribeOpenFailure(hit, ex);
        }
    }

    private void RevealInExplorer(SearchHitDto hit)
    {
        try
        {
            using var _ = Process.Start(BuildRevealStartInfo(hit));
            Hide();
        }
        catch (Exception ex)
        {
            _status.Text = $"定位失败：{ex.Message}";
        }
    }

    // ── 动作构造器（W3-d-3 断言面：抽出为纯函数，探针可断言"传给进程的到底是什么"，
    //    不真起进程 —— 验收能钉住全路径 / 反斜杠归一，而"真的 ShellExecute 成功"
    //    依赖桌面环境，属手工项）────────────────────────────────────────────────

    /// <summary>打开动作的进程参数。<b>FileName = 全路径</b>（索引回传，UI 不拼）。</summary>
    internal static ProcessStartInfo BuildOpenStartInfo(SearchHitDto hit) =>
        new(hit.Path) { UseShellExecute = true };

    /// <summary>
    /// 资源管理器定位的进程参数。`explorer /select` **只认反斜杠** —— 正斜杠是静默 no-op
    ///（托盘选中项断言三重根因之一，2026-09-24 实锤），这里必须归一。
    /// </summary>
    internal static ProcessStartInfo BuildRevealStartInfo(SearchHitDto hit) =>
        new("explorer.exe", $"/select,\"{hit.Path.Replace('/', '\\')}\"");

    /// <summary>
    /// 打开失败文案。**区分原因**（§30.6：只断"出了错"不够）——
    /// 文件不存在（索引尚未同步到删除，或用户手删）与其它异常给不同文案。
    /// </summary>
    internal static string DescribeOpenFailure(SearchHitDto hit, Exception ex) =>
        File.Exists(hit.Path)
            ? $"打开失败：{ex.Message}"
            : $"打开失败：文件不存在（索引尚未同步到删除？）—— {hit.Path}";

    // ── 渲染 ────────────────────────────────────────────────────────────────

    private void RenderResults(SearchQueryResponse response)
    {
        _suppressQueryEvents = true;
        var build = Stopwatch.StartNew();
        var render = Stopwatch.StartNew();
        try
        {
            _results.Items.Clear();
            foreach (var hit in response.Hits)
            {
                _results.Items.Add(hit);
            }
        }
        finally
        {
            _suppressQueryEvents = false;
        }

        build.Stop();
        _lastBuildMs = build.Elapsed.TotalMilliseconds;   // 数据层渲染耗时（W3-d-2 数字）
        _lastResponse = response;

        // "首屏渲染耗时"：从开始灌数据到**布局/渲染完成**。Loaded 优先级低于 Render，
        // 所以回调跑到时渲染已落地 —— 这才是用户能看见第一屏的时刻。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _lastRenderMs = render.Elapsed.TotalMilliseconds);

        // Epoch==0 = 会话的"空查询本地回调"（未签发请求）；真查询的 epoch ≥ 1 且 elapsedMs 有值。
        if (response.Epoch == 0 && response.ElapsedMs == 0)
        {
            _status.Text = "输入以搜索（Enter 打开 · Ctrl+Enter 定位 · Ctrl+C 复制路径）";
            _statusRight.Text = "";
            return;
        }

        _statusRight.Text = $"显示 {response.Hits.Count} / 共 {response.Total} 条";

        _status.Text = response.Hits.Count == 0
            ? $"没有匹配“{_input.Text}”的文件"
            : $"{response.ElapsedMs} ms";
    }

    private void RenderError(SearchIndexException ex)
    {
        if (ex.Code == RpcErrorCodes.SearchNotReady)
        {
            // W3-d-1 风险🟡对策：索引未就绪显示"正在建索引"，**不是空列表**（空列表 = "坏了吗？"）
            _status.Text = "正在建索引（首次全量约 10 秒级，取决于文件数）—— 打字会自动重试";
            _statusRight.Text = "";
            return;
        }

        _status.Text = $"搜索出错（{ex.Code}）：{ex.Message}";
        _statusRight.Text = "";
    }

    /// <summary>节流到点回调：单发 DispatcherTimer（InputThrottle 的 schedule 适配）。</summary>
    private void ScheduleThrottleFire(long delayMs)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _session.Fire();
        };
        timer.Start();
    }

    private void DisposeSession()
    {
        if (_sessionDisposed)
        {
            return;
        }

        _sessionDisposed = true;
        _session.Dispose();
    }

    /// <summary>探针收尾：释放会话（未 <c>Show()</c> 的窗口 <c>Close()</c> 不触发 <c>Closed</c>）。</summary>
    internal void DisposeForProbe() => DisposeSession();

    // ── UI 探针钩子（W3-d-2 断言面）─────────────────────────────────────────
    //
    // 窗口行为（真按键、真滚动、真失焦）在自动化之外（与真按键同口径）；但**"200 条数据
    // 进来后列表怎么表现"**是确定性的、值得钉住的：虚拟化是否真生效、计数是否两个数都对、
    // 高亮分段是否落在回传区间上。这些用"直接对内容根做 Measure/Arrange"就能验 ——
    // 不 Show()、不弹窗、不抢焦点（自动化友好），也不需要消息循环。

    /// <summary>探针入口：设输入文本 —— 走真 <c>TextChanged</c> → 真 <c>SearchSession</c> → 真渲染。</summary>
    internal void SubmitForProbe(string text) => _input.Text = text;

    /// <summary>
    /// 探针预备：把窗口挪到**屏幕外**、不激活（不抢焦点），然后 <c>Show()</c>。
    /// 为什么必须 Show()：实测 <c>Measure/Arrange</c> 单独调用时 VirtualizingStackPanel
    /// 拿不到真实视口（ExtentHeight/ViewportHeight 都不成立）⇒ 200 条全部生成容器 ——
    /// 虚拟化"名义已开、实际没发生"。真视口只能由呈现源建立。
    /// 屏幕外 + <c>ShowActivated=false</c> 保证不打扰正干活的用户（自动化友好）。
    /// </summary>
    internal void ShowForProbe()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -4000;
        Top = -4000;
        ShowActivated = false;
        Topmost = false;
        Show();
        RefreshVolumeSummary();   // 探针也要覆盖卷清单行（W3-e-2 的 UI 可见性）
    }

    /// <summary>当前卷清单行文本（探针用）。</summary>
    internal string ProbeVolumesLine => _volumesLine.Text;

    /// <summary>当前左侧状态行文本（探针用 —— 暂停/恢复的回显落在这里）。</summary>
    internal string ProbeStatusText => _status.Text;

    /// <summary>卷清单行当前是否"可点"（= 处于暂停态）。探针据此断言"只有暂停时才可点"。</summary>
    internal bool ProbeVolumesLineClickable => _volumesLine.ToolTip is not null;

    /// <summary>
    /// 真关闭（绕过"X=隐藏"拦截）。仅托盘退出与探针收尾可调 ——
    /// 用户侧的一切关闭动作（X / Alt+F4）都必须是 <see cref="Hide"/>，
    /// 否则一次关闭就让热键永久失效（窗口 Closed 后 Show 抛异常）。
    /// </summary>
    internal void RealClose()
    {
        _realClose = true;
        Close();
    }

    /// <summary>探针收尾：关窗（触发 Closed → 释放会话）。窗口未 Show 则退化为直接释放会话。</summary>
    internal void CloseForProbe()
    {
        try
        {
            RealClose();
        }
        catch (InvalidOperationException)
        {
            DisposeSession();
        }
    }

    /// <summary>当前列表项数（探针轮询渲染落地用）。</summary>
    internal int ProbeItemCount => _results.Items.Count;

    /// <summary>当前状态行右文本（探针用）。</summary>
    internal string ProbeStatusRight => _statusRight.Text;

    /// <summary>窗口当前是否可见（唤出生命周期探针用：Toggle → 采样）。</summary>
    internal bool ProbeIsVisible => Visibility == Visibility.Visible;

    /// <summary>窗口当前是否激活（WPF IsActive —— "唤出后焦点在不在窗口上"的观测面）。</summary>
    internal bool ProbeIsActive => IsActive;

    /// <summary>Enter 动作是否真被执行过（Enter 端到端链路的断言面）。</summary>
    internal bool ProbeEnterFired { get; private set; }

    /// <summary>
    /// 原生输入框收到的最后一个 KEYDOWN 虚拟键（注入对照实验读数；-1 = 从未收到）。
    /// ★ 换实现后观测点从"WPF 主窗的 hwnd 钩子"移到"原生 EDIT 自己" —— 键本来就进它那里，
    ///   挂在主窗上的旧读数在新架构下永远是 -1（死观测面，§2.27④）。
    /// </summary>
    internal int ProbeLastHookVk => _input.LastVk;

    /// <summary>本窗口是否是系统前台窗口（WPF IsActive ≠ 系统前台 —— 抢前台实验的诚实读数）。</summary>
    internal bool ProbeIsForeground =>
        GetForegroundWindow() == new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 直投实验目标句柄：**原生 EDIT 的子窗口句柄**。
    /// PostMessage 到窗口消息队列后由窗口过程处理 —— 键要进的是 EDIT，不是 WPF 主窗，
    /// 所以这里必须返回 EDIT 句柄，否则实验测的是"消息到了父窗"，与"能不能打字"无关。
    /// </summary>
    internal nint ProbeHwnd => _input.InputHwnd;

    /// <summary>
    /// 键盘焦点是否真落在输入框上（G1 手工 bug 的断言面："窗口在但打不进字" =
    /// 窗口可见而这里为 false）。★ 换实现后判据升级为 **Win32 语义的原生 <c>GetFocus</c>**
    /// （<see cref="NativeInputBox.IsInputFocused"/>）—— 旧判据 <c>IsKeyboardFocusWithin</c>
    /// 是 WPF 层的说法，在"WPF 说焦在这里、系统说不在"的分裂态下会**给出假绿**，
    /// 而只有系统层的焦点才真的能打进字。
    /// </summary>
    internal bool ProbeQueryFocused => _input.IsInputFocused;

    /// <summary>
    /// 布局并快照当前渲染结果。**不 Show()**：对内容根 <c>Measure/Arrange/UpdateLayout</c>
    /// 给 ListBox 一个有限视口 —— 虚拟化照常生效（容器按需生成），且零窗口副作用。
    /// <see cref="SearchUiSnapshot.RealizedContainers"/> = 实际生成容器的条数（虚拟化数字：
    /// 关掉虚拟化时它会等于 <see cref="SearchUiSnapshot.ItemsCount"/>）。
    /// </summary>
    internal SearchUiSnapshot SnapshotUi()
    {
        var sw = Stopwatch.StartNew();
        UpdateLayout();   // 真窗口下强制跑完本次布局（含滚动视口建立）

        // 兜底：自动化环境没有消息循环时窗口可能拿不到真实尺寸 ⇒ 显式给内容根有限约束
        //（有限约束同样能建立虚拟化视口；这一步只为"布局真的跑过"，不改变结果语义）。
        if (Content is FrameworkElement root && _results.ActualHeight <= 0)
        {
            var size = new Size(Width, Height);
            root.Measure(size);
            root.Arrange(new Rect(0, 0, Width, Height));
            root.UpdateLayout();
        }

        sw.Stop();

        // 虚拟化的三个使能条件现场读数（诊断 + 断言面）：面板实际类型 / 滚动语义 / 附加属性。
        // "设了但没生效"（主题模板覆盖）与"没设"必须能区分 —— 这是 --probe-search-ui 的价值点。
        var panel = FindDescendant<VirtualizingStackPanel>(_results);
        var scroller = FindDescendant<ScrollViewer>(_results);
        var scrollInfo = panel as System.Windows.Controls.Primitives.IScrollInfo;

        // 实生成容器 = 虚拟化面板**可视化子元素**里的 ListBoxItem 个数。
        // 不用 ItemContainerGenerator.ContainerFromIndex：它会**按需强制实现**容器
        //（遍历一遍就把 200 条全实现了，得到"虚拟化没生效"的假象 —— 首版实测踩到）。
        var realized = 0;
        HitText? first = null;
        if (panel is not null)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(panel);
            for (var i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(panel, i) is not ListBoxItem container)
                {
                    continue;
                }

                realized++;
                first ??= FindHitText(container);
            }
        }

        return new SearchUiSnapshot(
            ItemsCount: _results.Items.Count,
            RealizedContainers: realized,
            LayoutMs: sw.Elapsed.TotalMilliseconds,
            BuildMs: _lastBuildMs,
            RenderMs: _lastRenderMs,
            StatusRight: _statusRight.Text,
            StatusText: _status.Text,
            FirstName: first?.RenderedName,
            FirstPath: first?.RenderedPath,
            FirstSegments: first?.RenderedSegments() ?? Array.Empty<(string, bool)>(),
            FirstRendered: first is not null,
            ItemsPanelType: _results.ItemsPanel?.LoadContent()?.GetType().Name ?? "(none)",
            UsedVirtualizingPanel: panel is not null,
            CanContentScroll: scroller?.CanContentScroll ?? false,
            IsVirtualizing: (bool)(_results.GetValue(VirtualizingPanel.IsVirtualizingProperty) ?? false),
            ViewportHeight: scrollInfo?.ViewportHeight ?? -1,
            ExtentHeight: scrollInfo?.ExtentHeight ?? -1,
            VolumesLine: _volumesLine.Text);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var found = FindDescendant<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 找容器内的 <see cref="HitText"/>。这里用可视化树是**刻意的**：
    /// 项目约定"自检数控件走逻辑树"针对的是**未 Show() 的窗口**（没有可视化树）。
    /// 本探针的场景相反 —— 内容根已 Measure/Arrange，可视化树已建立，
    /// 而 DataTemplate 生成的内容**不在逻辑树上**（模板子元素不是逻辑子节点）。
    /// 找不到 ⇒ 返回 null（快照里 FirstRendered=false，断言显式变红，绝不空过）。
    /// </summary>
    private static HitText? FindHitText(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is HitText hit)
            {
                return hit;
            }

            var found = FindHitText(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 结果项模板：文件名（含高亮分段）+ 右侧灰色目录。高亮区间**直接按索引回传切**
    ///（UTF-16 code unit），UI 不重算匹配位置（W3-d-2 职责边界；R9 中文错位由索引侧保证）。
    /// </summary>
    private static DataTemplate MakeHitTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(HitText));
        factory.SetBinding(HitText.HitProperty, new Binding());   // 绑定数据项自身
        return new DataTemplate { VisualTree = factory };
    }

    /// <summary>单条命中的渲染控件（名字高亮 + 目录路径灰色尾注）。FEF 要求可无参构造。</summary>
    internal sealed class HitText : StackPanel
    {
        public static readonly DependencyProperty HitProperty = DependencyProperty.Register(
            nameof(Hit), typeof(SearchHitDto), typeof(HitText),
            new PropertyMetadata(null, OnHitChanged));

        private SearchHitDto? _hit;

        public SearchHitDto? Hit
        {
            get => (SearchHitDto?)GetValue(HitProperty);
            set => SetValue(HitProperty, value);
        }

        /// <summary>探针用：本条渲染的名字（DTO 原值）。</summary>
        internal string? RenderedName => _hit?.Name;

        /// <summary>探针用：本条渲染的路径（DTO 原值，全路径）。</summary>
        internal string? RenderedPath => _hit?.Path;

        /// <summary>
        /// 探针用：**实际渲染出来的**高亮分段（文本 + 是否加粗）。
        /// 断言"高亮真的落在索引回传的区间上"读这里 —— 而不是重算一遍（那等于自证）。
        /// </summary>
        internal IReadOnlyList<(string Text, bool Bold)> RenderedSegments()
        {
            if (Children.Count == 0 || Children[0] is not TextBlock name)
            {
                return Array.Empty<(string, bool)>();
            }

            var list = new List<(string, bool)>(name.Inlines.Count);
            foreach (var inline in name.Inlines)
            {
                if (inline is Run run)
                {
                    list.Add((run.Text, run.FontWeight == FontWeights.Bold));
                }
            }

            return list;
        }

        /// <summary>
        /// FEF 反射构造要求**公开无参构造器**（private 会在首次渲染时抛
        /// "No parameterless constructor defined" —— W3-d-1 的窗口渲染从未被自动化触达，
        /// 这个缺陷由 W3-d-2 的渲染探针当场抓出）。
        /// </summary>
        public HitText()
        {
            // 两行结构：名字（含高亮分段）在上，路径灰色小字省略在下。
            // 路径行用 TextBlock 才有 TextTrimming —— Run 没有该属性（初版踩到 CS0117）。
            Orientation = Orientation.Vertical;
            Margin = new Thickness(0, 2, 0, 2);
        }

        private static void OnHitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = (HitText)d;
            if (e.NewValue is not SearchHitDto hit)
            {
                return;
            }

            // ⚠️ VirtualizationMode.Recycling 会**复用容器**：不清 Children，第二个条目会叠在第一个上
            //（典型"第 3 条起显示错乱"的虚拟化+模板组合坑）。
            panel.Children.Clear();
            panel._hit = hit;

            var name = new TextBlock { FontSize = 14 };
            // 名字按回传高亮区间分段（UTF-16 code unit 口径，与 string 切片天然一致）。
            // 区间由索引进程计算 —— 可能是不连续多段（模糊匹配），这里逐段应用。
            var pos = 0;
            foreach (var (start, len) in hit.Highlights.OrderBy(s => s.Start))
            {
                if (start > pos && start <= hit.Name.Length)
                {
                    name.Inlines.Add(new Run(hit.Name[pos..start]));
                    pos = start;
                }

                // 边界钳制：渲染层不信任跨进程数据（防御深界）
                var take = Math.Min(len, Math.Max(0, hit.Name.Length - start));
                if (take > 0)
                {
                    name.Inlines.Add(new Run(hit.Name.Substring(start, take))
                    {
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.DodgerBlue,
                    });
                    pos = start + take;
                }
            }

            if (pos < hit.Name.Length)
            {
                name.Inlines.Add(new Run(hit.Name[pos..]));
            }

            if (hit.Dir)
            {
                name.Inlines.Add(new Run("  [目录]") { Foreground = Brushes.Gray, FontSize = 11 });
            }

            var path = new TextBlock
            {
                Text = hit.Path,
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            panel.Children.Add(name);
            panel.Children.Add(path);
        }
    }

    /// <summary>
    /// 挂 iNKORE 主题资源。与 PanelWindow / SettingsWindow **完全同款**三步
    /// （那里的注释解释了每一步的由来；共用 App 级字典，已挂过不重复挂）。
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
}

/// <summary>
/// UI 探针快照（W3-d-2 断言面）：**全数字/全文本**，供 <c>--probe-search-ui</c> 落盘、
/// verify-desktop 断言。选择这些观测量的理由 —— 它们各自钉住一条会静默退化的性质：
/// <list type="bullet">
/// <item><see cref="RealizedContainers"/>：虚拟化是否真生效（关掉时 = ItemsCount，200 条全生成容器）；</item>
/// <item><see cref="StatusRight"/>：total 与 hits 两个数都显示且都对（"显示 X / 共 Y 条"）；</item>
/// <item><see cref="FirstSegments"/>：高亮是否真落在索引回传区间上（含中文/多段）；</item>
/// <item><see cref="FirstPath"/>：路径来自索引进程（全路径），UI 不拼；</item>
/// <item><see cref="FirstRendered"/>：首项**真的**在可视化树上（false ⇒ 断言显式变红，不空过）。</item>
/// </list>
/// </summary>
internal sealed record SearchUiSnapshot(
    int ItemsCount,
    int RealizedContainers,
    double LayoutMs,
    double BuildMs,
    double RenderMs,
    string StatusRight,
    string StatusText,
    string? FirstName,
    string? FirstPath,
    IReadOnlyList<(string Text, bool Bold)> FirstSegments,
    bool FirstRendered,
    string ItemsPanelType,
    bool UsedVirtualizingPanel,
    bool CanContentScroll,
    bool IsVirtualizing,
    double ViewportHeight,
    double ExtentHeight,
    string VolumesLine);
