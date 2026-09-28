// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Eztools.Desktop;

/// <summary>
/// 剪贴板常驻监听（W5-c，FR-1）：不可见消息窗口 + <c>AddClipboardFormatListener</c> +
/// <c>WM_CLIPBOARDUPDATE</c> 分发。
///
/// <para>与 <see cref="HotkeyHook"/> 同构（同款 WinForms 隐形 Sink —— 那边的注释是权威），
/// 消息驱动零轮询（W5 设计 NFR-5）。必须在有消息循环的线程上 Start（托盘主线程）；
/// <c>ClipboardChanged</c> 在该线程触发，消费者可以直接碰 UI。</para>
///
/// <para>权限边界（设计 §8 R1/UIPI）：非提权进程读不到提权进程写入的内容 —— 监听器只负责
/// "有变化了"这个信号，读取与降级（占位条目）归 <see cref="ClipboardReader"/> 与消费方。</para>
/// </summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint pChangedInfo);

    // MSGFLT_ALLOW：允许目标窗口接收来自任意完整性级别的该消息（放通跨 IL 剪贴板通知）。
    private const uint MSGFLT_ALLOW = 1;

    /// <summary>消息 sink：只负责把 WM_CLIPBOARDUPDATE 转成事件（HotkeyHook.Sink 同款）。</summary>
    private sealed class Sink : Control
    {
        // WinForms 分析器对 Control 派生类可写属性要求序列化标注（WFO1000）——用字段
        public Action? OnUpdate;

        public Sink() => SetStyle(ControlStyles.AllPaintingInWmPaint, true);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                OnUpdate?.Invoke();
            }

            base.WndProc(ref m);
        }
    }

    private readonly Sink _sink = new();

    /// <summary>剪贴板有变化（内容是什么、能不能读，由消费方决定）。</summary>
    public event Action? ClipboardChanged;

    public bool IsRunning { get; private set; }

    /// <summary>开始监听。失败（AddClipboardFormatListener 拿不到）返回 false —— 调用方显式日志+气泡，禁静默。</summary>
    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        if (!_sink.IsHandleCreated)
        {
            _ = _sink.Handle;   // 强制创建句柄（构造线程 = 托盘主线程，泵归它）
        }

        if (!AddClipboardFormatListener(_sink.Handle))
        {
            return false;
        }

        // UIPI（设计 §8 R1 / FR-11③，09-28 修正）：默认 Windows UIPI 不向 low IL 监听窗口
        // 投递 high IL 进程写的剪贴板变化通知，导致托盘收不到"提权进程动了剪贴板"的信号，
        // 占位条目逻辑永远没机会执行。显式放通 WM_CLIPBOARDUPDATE 的跨 IL 接收。
        _ = ChangeWindowMessageFilterEx(_sink.Handle, WM_CLIPBOARDUPDATE, MSGFLT_ALLOW, nint.Zero);

        IsRunning = true;
        _sink.OnUpdate = () => ClipboardChanged?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (IsRunning)
        {
            _ = RemoveClipboardFormatListener(_sink.Handle);
            IsRunning = false;
        }

        _sink.Dispose();
    }
}
