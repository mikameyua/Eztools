// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Eztools.ClipboardLib;

/// <summary>
/// 直贴的后半程（W5-剪贴板-设计方案.md §3.3 / FR-10）：
/// 面板 Hide 之后 → 把唤出前的前台窗口还原回来 → 注入 Ctrl+V。
///
/// <para>拆成静态纯函数的原因与 <c>BuildOpenStartInfo</c> 同款：
/// "传给系统的到底是什么"可以被探针断言，而"真的贴进了用户的编辑器"
/// 依赖桌面环境，属手工验收项。</para>
///
/// <para>已知代价（接受并登记）：AttachThreadInput 组合拳还原前台时会扰动目标进程的
/// IME 关联（SearchWindow 抢前台同款副作用，方向相反）；Ctrl+V 注入走真实键盘流，
/// 目标窗口若处于密码框等场景由用户自行掌控（面板本就是把内容放上了剪贴板）。</para>
/// </summary>
public static class PasteBack
{
    /// <summary>
    /// 把 <paramref name="targetHwnd"/> 还原回前台。返回是否成功（GetForegroundWindow == target）。
    /// 与 SearchWindow.ForceForeground 同款三级策略，但这里是"还给别人的窗口"：
    /// 目标窗口可能已销毁（用户 Alt+F4 关掉了复制来源）—— 所有失败路径都如实返回 false，
    /// 由面板降级为"仅复制"并提示。
    /// </summary>
    public static bool RestoreForeground(nint targetHwnd)
    {
        if (targetHwnd == nint.Zero || !IsWindow(targetHwnd))
        {
            return false;
        }

        if (GetForegroundWindow() == targetHwnd)
        {
            return true;
        }

        _ = SetForegroundWindow(targetHwnd);
        if (GetForegroundWindow() == targetHwnd)
        {
            return true;
        }

        // ALT-trick（Everything 同款）：把本进程放进前台白名单，不碰输入队列。
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        _ = SetForegroundWindow(targetHwnd);
        if (GetForegroundWindow() == targetHwnd)
        {
            return true;
        }

        // AttachThreadInput 兜底（最后手段：会扰动目标进程 IME 关联）
        var foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var curThread = GetCurrentThreadId();
        var attached = foreThread != 0 && foreThread != curThread
            && AttachThreadInput(curThread, foreThread, fAttach: true);
        try
        {
            _ = BringWindowToTop(targetHwnd);
            _ = SetForegroundWindow(targetHwnd);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(curThread, foreThread, fAttach: false);
            }
        }

        return GetForegroundWindow() == targetHwnd;
    }

    /// <summary>
    /// 注入 Ctrl+V（真实键盘流：Ctrl↓ V↓ V↑ Ctrl↑）。调用方需保证注入时
    /// 目标窗口已在前台并处理完激活（建议先等 ~120ms —— 激活消息排队落地）。
    /// </summary>
    public static void InjectCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_V, 0x2F, 0, 0);            // 0x2F = V 的 Set 1 扫描码（真实感更强）
        keybd_event(VK_V, 0x2F, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

    private const byte VK_MENU = 0x12;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);
}
