// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;

namespace Eztools.Desktop;

/// <summary>
/// 宿主化原生 EDIT 输入框（2026-09-27「换实现」）。搜索窗与剪贴板面板**共用这一个**。
///
/// <para><b>为什么换掉 WPF TextBox</b>：WPF 的文本输入走 TSF（TextStore）——
/// 本机实测 <c>ImmGetContext</c> 对 WPF 窗口**恒返回 NULL**（WPF 不是 IMM32 窗口），
/// 于是 IME 关联、转换模式判据、WM_CHAR 生成全部在系统层打滑，逼出一整套
/// 「合成层 + 轮询层 + LL 钩子」的补丁；而补丁之间又互相双打（§2.29）。
/// 原生 EDIT 走的是 IMM32 老路（与记事本同代），有真实 HIMC、真实 WM_CHAR ——
/// <b>把输入路径从 TSF 挪回 IMM32，是这次换实现的全部意义</b>。</para>
///
/// <para><b>本组件刻意"什么都不做"</b>：不合成字符、不轮询键盘、不挂钩子、不拦 TextInput。
/// 字符从哪来、怎么上屏，全权交给系统输入法与原生 EDIT ——
/// <b>供字者唯一</b>。这是 §2.29 那条最贵教训的制度化：「键盘出问题第一步是拆干预，不是加干预」。</para>
///
/// <para><b>只做三件宿主必须做的事</b>：① 把原生控件的窗口嵌进 WPF 布局；
/// ② 把"命令键"（Enter/Esc/↑↓/Del/Ctrl+P/Ctrl+C）转成宿主能懂的事件并吞掉，
/// 其余按键一律放行给控件自己处理；③ 暴露焦点/句柄给宿主的唤出与诊断逻辑。</para>
///
/// <para><b>已知限制（airspace）</b>：WindowsFormsHost 的子窗口恒在 WPF 内容之上，
/// 无法被 WPF 元素覆盖或做圆角裁切。所以输入框区域做成"平底 + 下边线"，不做悬浮层。</para>
/// </summary>
public sealed class NativeInputBox : Grid
{
    /// <summary>命令键（宿主关心的键；其余按键由原生 EDIT 自己处理，不会走到这里）。</summary>
    public enum InputCommand
    {
        /// <summary>Enter：搜索窗=打开首条/选中项；面板=直贴。</summary>
        Enter,

        /// <summary>Ctrl+Enter：搜索窗=资源管理器定位；面板=仅复制不直贴。</summary>
        CtrlEnter,

        /// <summary>Esc：收起窗口。</summary>
        Escape,

        /// <summary>↑：列表选中上移（焦点始终留在输入框，纯逻辑移动）。</summary>
        Up,

        /// <summary>↓：列表选中下移。</summary>
        Down,

        /// <summary>Delete（仅输入框为空时上报）：面板=删除选中条目。</summary>
        Delete,

        /// <summary>Ctrl+P：面板=置顶/取消置顶。</summary>
        CtrlP,

        /// <summary>Ctrl+C（仅无选区时上报）：搜索窗=复制选中项路径；面板=复制选中条目。</summary>
        CtrlC,
    }

    /// <summary>
    /// 命令键处理回调。<b>返回 true = 已处理（键被吞）</b>；返回 false = 未处理（键放行给控件默认行为）。
    /// 这个返回值契约很重要：宿主要能"拒绝"某个命令（例如搜索窗不管 Ctrl+P），
    /// 否则会静默吞掉一个用户以为有用的按键。
    /// </summary>
    public Func<InputCommand, bool>? Command { get; set; }

    /// <summary>文本变化（宿主据此触发查询/过滤）。</summary>
    public event Action? TextChanged;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int EM_SETSEL = 0x00B1;
    private const int EM_GETSEL = 0x00B0;

    private readonly Edit _edit;
    private readonly WindowsFormsHost _host;

    /// <summary>最近一次到达原生控件的虚拟键码（诊断面，-1 = 从未收到）。</summary>
    public int LastVk { get; private set; } = -1;

    public NativeInputBox()
    {
        _edit = new Edit
        {
            BorderStyle = WinForms.BorderStyle.None,
            BackColor = System.Drawing.Color.White,
            ForeColor = System.Drawing.Color.Black,
            Font = new System.Drawing.Font("Segoe UI", 14F),
            Multiline = false,
            WordWrap = false,
            TabStop = true,
        };
        _edit.KeyInterceptor = OnCommandKey;
        _edit.RawKeySeen = vk => LastVk = vk;
        _edit.TextChanged += (_, _) => TextChanged?.Invoke();

        // 强制建句柄：焦点/句柄读数在窗口 Show 之前也可能被问到（探针会问）。
        _ = _edit.Handle;

        _host = new WindowsFormsHost
        {
            Child = _edit,
            Focusable = true,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // 外层 WPF 容器负责"框的样子"（原生控件做不出圆角）；原生控件只负责文字。
        // 上下各留 4px 内边距，让下边线不与文字贴死（WindowsFormsHost 不能被 WPF 裁切，
        // 所以圆角交给这个 Border，内层是直角白底 —— 视觉上仍是"一个输入框"）。
        var frame = new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC8, 0xC8, 0xC8)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 4, 2, 4),
            Margin = new Thickness(12, 12, 12, 6),
            Child = _host,
        };

        Children.Add(frame);
    }

    /// <summary>当前文本。设值会触发 <see cref="TextChanged"/>（与 WPF TextBox 语义一致，探针依赖）。</summary>
    public string Text
    {
        get => _edit.Text;
        set => _edit.Text = value;
    }

    /// <summary>
    /// 把键盘焦点交给输入框。**两层都要落位**：WPF 侧先聚焦 HwndHost（把 WPF 焦点交出去），
    /// 再由原生控件调 Win32 <c>SetFocus</c>。只做后者会出现"WPF 以为焦点还在别处、
    /// 系统焦点已在 EDIT"的分裂态（WindowsFormsHost 的经典坑）。
    /// </summary>
    public void FocusInput()
    {
        _host.Focus();
        _edit.Focus();
    }

    /// <summary>全选（唤出即替换旧查询 —— Everything 同款）。</summary>
    public void SelectAll()
    {
        _edit.SelectAll();
    }

    /// <summary>插入符移到末尾（清空/程序化设值后调用，避免光标停在头部）。</summary>
    public void CaretToEnd()
    {
        _edit.SelectionStart = _edit.TextLength;
        _edit.SelectionLength = 0;
    }

    /// <summary>
    /// 键盘焦点是否真落在**原生控件**上（Win32 语义的 <c>GetFocus</c>，不是 WPF 的
    /// <c>IsKeyboardFocusWithin</c>）。"窗口在但打不进字"这类 bug 的断言面就是它：
    /// WPF 层说焦点在，系统层说不在 —— 只有后者能打进字。
    /// </summary>
    public bool IsInputFocused => GetFocus() == _edit.Handle;

    /// <summary>原生 EDIT 的窗口句柄（诊断/直投实验用）。</summary>
    public nint InputHwnd => _edit.Handle;

    /// <summary>输入框当前是否有文字（宿主据此决定 Del/Ctrl+C 是"编辑文本"还是"操作列表"）。</summary>
    public bool IsEmpty => _edit.TextLength == 0;

    /// <summary>当前选区长度（宿主判断 Ctrl+C 该复制哪个东西）。</summary>
    public int SelectionLength => _edit.SelectionLength;

    /// <summary>
    /// 命令键分流。**这里不产生任何字符**，只做两件事：判断这个键宿主管不管；
    /// 管就上报并吞掉，不管就返回 false 让原生控件按自己的语义处理。
    ///
    /// <para>判据全部基于"键 + 修饰键 + 输入框状态"，绝不基于 IME 模式 ——
    /// 这正是和旧合成层最大的区别：旧方案要问 IME "现在是中文还是英文"才能决定
    /// 自己该不该供字；新方案压根不供字，所以**不需要知道 IME 状态**，
    /// 那个恒返回 NULL 的 <c>ImmGetContext</c> 连同它的判据一起消失了。</para>
    /// </summary>
    private bool OnCommandKey(WinForms.Keys keyCode)
    {
        var mods = WinForms.Control.ModifierKeys;
        var ctrl = (mods & WinForms.Keys.Control) != 0;
        var shift = (mods & WinForms.Keys.Shift) != 0;
        var alt = (mods & WinForms.Keys.Alt) != 0;

        InputCommand? cmd = keyCode switch
        {
            WinForms.Keys.Enter when ctrl => InputCommand.CtrlEnter,
            WinForms.Keys.Enter when !alt => InputCommand.Enter,
            WinForms.Keys.Escape => InputCommand.Escape,

            // ↑/↓：单行 EDIT 本来就没有"上下移动插入符"的语义（多行才有），
            // 所以这两个键直接归宿主做列表导航，不需要任何前置条件。
            WinForms.Keys.Up when !alt => InputCommand.Up,
            WinForms.Keys.Down when !alt => InputCommand.Down,

            // Del：只有**输入框为空**时才当"删除条目"。有字时 Del 是正常的向后删除字符 ——
            // 旧方案在轮询层无条件把 Del 当删条目，等于"打字时按 Del 会删掉一条历史"，
            // 那是把快捷键凌驾于文本编辑之上，本组件明确修掉这个冒犯。
            WinForms.Keys.Delete when !ctrl && !shift && !alt && _edit.TextLength == 0
                => InputCommand.Delete,

            WinForms.Keys.P when ctrl && !alt => InputCommand.CtrlP,

            // Ctrl+C：有选区时是"复制我选中的文字"（用户意图明确），无选区时才可能是
            // "复制选中条目/路径" —— 由宿主再判一次（搜索窗还要看有没有选中项）。
            WinForms.Keys.C when ctrl && !alt && _edit.SelectionLength == 0
                => InputCommand.CtrlC,

            _ => null,
        };

        if (cmd is not { } command)
        {
            return false;   // 普通按键（含中文组合、粘贴、Ctrl+A/Ctrl+V）—— 放行给原生控件
        }

        return Command?.Invoke(command) == true;
    }

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    /// <summary>
    /// 原生化 TextBox。**全部按键拦截都收在 <see cref="WndProc"/> 一处** ——
    /// 这是刻意的：WinForms 的 <c>ProcessCmdKey</c>（命令键）与 <c>OnKeyDown</c>（输入键）
    /// 是两条互斥又可能重叠的路径，同一按键可能在两条路径上被处理两次；
    /// 在消息层拦把"正好一次"变成结构性保证，不靠调用顺序的约定。
    /// </summary>
    private sealed class Edit : WinForms.TextBox
    {
        // ★ 必须是**字段**不是属性：WinForms 分析器（WFO1000）要求 Control 派生类的可写属性
        //   配置代码序列化，字段则不受约束（HotkeyHook.Sink 同款取舍，2026-09-19 先例）。

        /// <summary>命令键拦截（返回 true = 已处理，不调 base ⇒ 键被吞）。</summary>
        public Func<WinForms.Keys, bool>? KeyInterceptor;

        /// <summary>观测到任意 KEYDOWN 的虚拟键码（诊断用）。</summary>
        public Action<int>? RawKeySeen;

        protected override void WndProc(ref WinForms.Message m)
        {
            if (m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var vk = m.WParam.ToInt32();
                RawKeySeen?.Invoke(vk);

                // VK 与 WinForms.Keys 在字母/数字/Enter/Esc/方向/Delete 这些键上同值，
                // 直接转型即可（自定义键才需要 Keys 映射表 —— 本项目不需精确值）。
                if (KeyInterceptor?.Invoke((WinForms.Keys)vk) == true)
                {
                    return;   // 吞掉：不调 base，原生控件不会处理它
                }
            }

            base.WndProc(ref m);
        }
    }
}
