// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Eztools.Ocr;

namespace Eztools.Desktop;

/// <summary>
/// 区域截图遮罩窗（W6-b）：每台显示器一扇，拖拽框选 → 位图进剪贴板（W6 FR-1/FR-2）。
/// 公共骨架（四件套 UI / 拖拽状态机 / 坐标纪律 / Esc·右键·Alt+F4 / 探针采样面）全部
/// 继承自 <see cref="OverlayWindowBase"/>（W6-a），本类只实现截图语义。
///
/// 交互契约（W6 设计方案 §4）：
///   · 拖拽松开即截图 → 位图进剪贴板 → 收全部窗（FR-2，零预览条 —— D3=A 剪贴板即终点）；
///   · 空拖拽（&lt;6 物理像素）= 直接**取消出窗**，不重试（§4：避免与 pick 的单击取色语义串味）；
///   · Esc / 右键 → 全部遮罩退出；
///   · 截图写剪贴板瞬间极短（_busy），期间忽略新拖拽。
///
/// 坐标纪律：物理像素换算（"窗口内部坐标 + 窗口 Win32 原点 = 虚拟桌面坐标"）由
/// <see cref="OverlayWindowBase.ToVirtualRect"/> 单点提供 —— W4-b 双屏血案修复直接复用。
///
/// ⚠️ 双栈类型歧义：GDI 类型一律走 <c>Drawing.</c> 别名（csproj 全局别名约定同源）。
/// </summary>
internal sealed class CaptureOverlayWindow : OverlayWindowBase
{
    private readonly CaptureOverlayManager _owner;

    public CaptureOverlayWindow(CaptureOverlayManager owner, Drawing.Rectangle physicalBounds)
        : base(physicalBounds, "拖拽框选要复制的屏幕区域 · Esc 取消")
    {
        _owner = owner;
        Title = "Eztools 截图";
    }

    // ── Base 扩展点实现 ──

    protected override void HandleClick(Drawing.Point localPhysicalPoint)
    {
        // W6 §4 拍板：capture 空拖拽 = 取消出窗（不是重试，也不是把单击当 1px 截图）。
        Cancel();
    }

    protected override void HandleRegion(Drawing.Rectangle localPhysicalRect)
    {
        _ = CopyRegionAsync(localPhysicalRect);
    }

    protected override void OnCopied(string text) => _owner.NotifyCopied(this, text);

    protected override void OnCancelled() => _owner.NotifyCancelled(this);

    // ── 截图链路 ──

    private async Task CopyRegionAsync(Drawing.Rectangle localPhysicalRect)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            using var bitmap = ScreenCapture.GrabRegion(ToVirtualRect(localPhysicalRect));
            var source = ToBitmapSource(bitmap);
            if (_resultTaken)
            {
                return; // 截图期间窗被收走（Esc 等）—— 结果作废，不写剪贴板
            }

            var description = $"{bitmap.Width}×{bitmap.Height}";
            if (TrySetClipboard(() => Clipboard.SetImage(source)))
            {
                Finish(description);
            }
            else
            {
                ShowRetryHint("剪贴板写入失败");
            }
        }
        catch (Exception ex)
        {
            // 错误必须有可见出口（S 系红线）：截图失败提示在遮罩上，用户可重试或退出。
            ShowRetryHint($"截图失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// GDI Bitmap → BitmapSource（WPF 剪贴板 SetImage 的入参类型）。
    /// ⚠️ <c>CreateBitmapSourceFromHBitmap</c> 每次调用产出一个 HBitmap，**必须
    /// <c>DeleteObject</c>（try/finally）** —— 漏一次漏一个 GDI 句柄（W6 设计方案 R9，
    /// 审查规范 §3.4·4.8 原生资源生命周期红线；断言面 = 连续 N 次截图看句柄斜率）。
    /// Freeze 后免 WPF 线程亲和，剪贴板写入与探针读取更稳。
    /// </summary>
    private static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            _ = DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);
}
