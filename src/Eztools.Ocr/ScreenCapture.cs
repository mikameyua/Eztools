// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Drawing;

namespace Eztools.Ocr;

/// <summary>
/// 屏幕区域截图原语（W4-a：仅原语本体；多屏遮罩/坐标换算的完整消费在 W4-b）。
/// <para>
/// 坐标纪律（设计方案 §3.2）：入参一律**物理像素**。进程级 per-monitor DPI 感知
/// 由 <see cref="EnsurePerMonitorDpiAware"/> 一次性声明（非 v2 感知下 GDI 坐标会被
/// DWM 虚拟化，缩放 ≠100% 的屏上框哪儿截哪儿错）。
/// </para>
/// <para>
/// 光标处理：GDI <c>CopyFromScreen</c> 抓的是屏幕合成前的像素，不含鼠标光标；
/// 若 W4-b 实测发现个别环境光标被叠印，再引入 PowerOCR <c>CursorClipper.cs</c> 的擦除逻辑，
/// 现在不过早引入。
/// </para>
/// </summary>
public static class ScreenCapture
{
    private static bool _dpiAwarenessEnsured;

    /// <summary>
    /// 声明进程级 per-monitor DPI v2 感知（幂等、尽力而为：失败不抛——
    /// 最坏结果是旧式虚拟化坐标，由 W4-b 手工清单的混合缩放条目兜底发现）。
    /// 必须在任何窗口/截图发生**之前**调用。
    /// </summary>
    public static void EnsurePerMonitorDpiAware()
    {
        if (_dpiAwarenessEnsured)
        {
            return;
        }

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4));
        _dpiAwarenessEnsured = true;
    }

    /// <summary>截取屏幕上一个**物理像素**矩形区域（多屏虚拟桌面坐标）。</summary>
    public static Bitmap GrabRegion(Rectangle physicalRect)
    {
        EnsurePerMonitorDpiAware();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(physicalRect.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(physicalRect.Height, 0);

        var bitmap = new Bitmap(physicalRect.Width, physicalRect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(physicalRect.X, physicalRect.Y, 0, 0, physicalRect.Size);
        return bitmap;
    }

    /// <summary>
    /// 虚拟桌面（全部显示器拼合）的物理像素边界。
    /// PMv2 感知下 <c>GetSystemMetrics</c> 返回的就是物理像素 —— 与 <see cref="GrabRegion"/> 同一坐标系。
    /// </summary>
    public static Rectangle GetVirtualScreenBounds()
    {
        EnsurePerMonitorDpiAware();
        return Rectangle.FromLTRB(
            GetSystemMetrics(SmXvirtualScreen),
            GetSystemMetrics(SmYvirtualScreen),
            GetSystemMetrics(SmXvirtualScreen) + GetSystemMetrics(SmCxvirtualScreen),
            GetSystemMetrics(SmYvirtualScreen) + GetSystemMetrics(SmCyvirtualScreen));
    }

    private const int SmXvirtualScreen = 76;
    private const int SmYvirtualScreen = 77;
    private const int SmCxvirtualScreen = 78;
    private const int SmCyvirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
}
