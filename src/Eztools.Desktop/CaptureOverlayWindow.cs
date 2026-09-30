// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private readonly System.Windows.Shapes.Rectangle[] _dimBands;

    public CaptureOverlayWindow(CaptureOverlayManager owner, Drawing.Rectangle physicalBounds)
        : base(physicalBounds, "拖拽框选要复制的屏幕区域 · Esc 取消")
    {
        _owner = owner;
        Title = "Eztools 截图";

        // W6-d 修正：CopyFromScreen 会把本遮罩（layered window）一起抓进截图 ——
        // 全屏 dim 不重排的话，用户截到的是**被 dim 压暗**的图。拖拽开始后把 dim
        // 重排成选区外的四条边带（选区内亮，标准截图 UX），松开后恢复全屏 dim。
        var dimBrush = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0));
        _dimBands =
        [
            new System.Windows.Shapes.Rectangle { Fill = dimBrush, Visibility = Visibility.Collapsed },
            new System.Windows.Shapes.Rectangle { Fill = dimBrush, Visibility = Visibility.Collapsed },
            new System.Windows.Shapes.Rectangle { Fill = dimBrush, Visibility = Visibility.Collapsed },
            new System.Windows.Shapes.Rectangle { Fill = dimBrush, Visibility = Visibility.Collapsed },
        ];
        var root = (Canvas)Content;
        foreach (var band in _dimBands)
        {
            root.Children.Add(band);
        }
    }

    /// <summary>W6-d：选区高亮填充改全透 —— 半透明填充会被截图抓进去给内容加色罩（实测），只留描边。</summary>
    protected override Brush SelectionFill => Brushes.Transparent;

    /// <summary>W6-d：拖拽开始 ⇒ 全屏 dim 换四条边带（选区内亮）。</summary>
    protected override void OnDragStarted()
    {
        _dim.Visibility = Visibility.Collapsed;
        foreach (var band in _dimBands)
        {
            band.Visibility = Visibility.Visible;
        }
    }

    /// <summary>W6-d：选区变化 ⇒ 四条边带围绕选区排布（挖出亮洞）。</summary>
    protected override void OnSelectionDragging(Rect selectionDip)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var top = _dimBands[0];
        var bottom = _dimBands[1];
        var left = _dimBands[2];
        var right = _dimBands[3];

        top.Width = width;
        Canvas.SetTop(top, 0);
        Canvas.SetLeft(top, 0);
        top.Height = Math.Max(0, selectionDip.Y);

        bottom.Width = width;
        Canvas.SetLeft(bottom, 0);
        Canvas.SetTop(bottom, selectionDip.Bottom);
        bottom.Height = Math.Max(0, height - selectionDip.Bottom);

        left.Height = selectionDip.Height;
        Canvas.SetLeft(left, 0);
        Canvas.SetTop(left, selectionDip.Y);
        left.Width = Math.Max(0, selectionDip.X);

        right.Height = selectionDip.Height;
        Canvas.SetLeft(right, selectionDip.Right);
        Canvas.SetTop(right, selectionDip.Y);
        right.Width = Math.Max(0, width - selectionDip.Right);
    }

    /// <summary>W6-d：拖拽结束 ⇒ 恢复全屏 dim（窗多半已收；失败重试态回到与拖拽前一致的观感）。</summary>
    protected override void OnDragFinished()
    {
        _dim.Visibility = Visibility.Visible;
        foreach (var band in _dimBands)
        {
            band.Visibility = Visibility.Collapsed;
        }
    }

    // ── Base 扩展点实现 ──

    protected override void HandleClick(Drawing.Point localPhysicalPoint)
    {
        // W6 §4 拍板：capture 空拖拽 = 取消出窗（不是重试，也不是把单击当 1px 截图）。
        Cancel();
    }

    protected override void HandleRegion(Drawing.Rectangle localPhysicalRect, Drawing.Point releasePhysicalPoint)
    {
        _ = CopyRegionAsync(localPhysicalRect); // capture 只认选区；释放点语义与截图无关（W6-c 扩展点保持兼容）
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
