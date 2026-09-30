// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Eztools.Ocr;

namespace Eztools.Desktop;

/// <summary>
/// 屏幕取色遮罩窗（W6-c，FR-3）：每台显示器一扇，移动即放大镜预览，左键单击复制色值并退出。
/// 公共骨架继承 <see cref="OverlayWindowBase"/>（W6-a）。
///
/// 交互契约（W6 设计方案 §4）：
///   · MouseMove → 采样光标周围 15×15 物理像素小块（D7 拍板）→ loupe 放大（10×）+ 中心色块 + 色值文本；
///   · 左键单击（含拖拽释放，见下）→ 取释放点颜色 → 格式化 → Clipboard.SetText → 收全部窗（FR-3）；
///   · Esc / 右键 → 全部遮罩退出；
///   · 拖拽在 pick 模式没有框选语义（<c>ShowDragSelection=false</c>），释放 = 对释放点取色
///     —— §4 未定义拖拽，实施拍板：释放点即光标意图点，与单击同义（防"拖了反而没反应"）。
///
/// 技术纪律（W6 设计方案 §7/§10）：
///   · 🔴 禁 GDI GetPixel（R7）：一律 GrabRegion 小块 + LockBits 内存读；
///   · loupe 渲染 = WriteableBitmap.WritePixels 局部更新 + NearestNeighbor（R6：禁整屏重绘）；
///   · loupe 状态完全归窗内（R11）：MouseMove 驱动、MouseLeave 隐藏、随窗销毁，
///     跨屏由另一扇窗自己的 MouseMove 接管，无跨窗协调。
///
/// ⚠️ 双栈类型歧义：GDI 类型一律走 <c>Drawing.</c> 别名（csproj 全局别名约定同源）。
/// </summary>
internal sealed class PickOverlayWindow : OverlayWindowBase
{
    private const int SampleSize = 15;      // D7：采样块 15×15 物理像素
    private const double LoupeZoom = 10.0;  // D7：放大 10×（loupe ≈150×150 物理像素）

    private readonly PickOverlayManager _owner;
    private readonly string _format;
    private readonly Image _loupe;
    private readonly WriteableBitmap _loupeBitmap;
    private readonly Rectangle _swatch;
    private readonly TextBlock _colorLabel;

    public PickOverlayWindow(PickOverlayManager owner, Drawing.Rectangle physicalBounds, string format)
        : base(physicalBounds, "移动选取颜色，单击复制 · Esc 取消")
    {
        _owner = owner;
        _format = format;
        Title = "Eztools 取色";

        // W6-d 修正：CopyFromScreen 会把本遮罩（layered window）一起抓进取色采样 ——
        // 全屏 dim（alpha 0x60）不重排的话，取到的颜色全部被压暗（实测 255 → 159 =
        // ×0.6235 精确吻合）。pick 无框选语义 ⇒ dim 换成 **alpha 0x01**：
        //   · 视觉不可见（0.4% 黑，PowerToys ColorPicker 同款代价）；
        //   · 但不能全透（alpha 0）—— layered window 的 alpha=0 像素在 Win32 层
        //     **点击穿透**，单击会落到下面的窗口上，取色直接失效（首跑实测）。
        // 代价：采样色 = 真实色 × 254/255（每通道 ≤1 的理论偏差）。
        // OCR 保持全屏 dim 不动（W4 范畴，行为零变化红线）。
        _dim.Fill = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));

        // D7：采样块 → WriteableBitmap（Bgra32）→ NearestNeighbor 放大出像素网格效果。
        _loupeBitmap = new WriteableBitmap(SampleSize, SampleSize, 96, 96, PixelFormats.Bgra32, null);
        _loupe = new Image
        {
            Source = _loupeBitmap,
            Width = SampleSize * LoupeZoom,
            Height = SampleSize * LoupeZoom,
            Stretch = Stretch.Fill,
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(_loupe, BitmapScalingMode.NearestNeighbor);

        _swatch = new Rectangle
        {
            Width = 84,
            Height = 24,
            Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)),
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
        };
        _colorLabel = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 13,
            Visibility = Visibility.Collapsed,
        };

        // loupe 元素挂进 Base 的画布（dim 之下、hint 之上），坐标由 MouseMove 驱动。
        var root = (Canvas)Content;
        root.Children.Add(_loupe);
        root.Children.Add(_swatch);
        root.Children.Add(_colorLabel);

        // R11：鼠标离开本窗（跨屏/进 hint 标签）就隐藏预览，不留残影。
        MouseLeave += (_, _) => HideLoupe();

        MouseMove += OnMove;
    }

    protected override void HandleClick(Drawing.Point localPhysicalPoint) => _ = PickAsync(localPhysicalPoint);

    protected override void HandleRegion(Drawing.Rectangle localPhysicalRect, Drawing.Point releasePhysicalPoint)
    {
        // pick 无框选语义：拖拽释放 = 对释放点取色（见类注释"实施拍板"）。
        _ = PickAsync(releasePhysicalPoint);
    }

    protected override void OnCopied(string text) => _owner.NotifyCopied(this, text);

    protected override void OnCancelled() => _owner.NotifyCancelled(this);

    // ── 采样与 loupe ──

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var local = ToPhysicalPoint(e.GetPosition(this));
        var sample = GrabSample(local);
        if (sample is null)
        {
            return;
        }

        UpdateLoupe(e.GetPosition(this), sample.Value.Pixels, sample.Value.Width, sample.Value.Height, sample.Value.Color);
    }

    private void HideLoupe()
    {
        _loupe.Visibility = Visibility.Collapsed;
        _swatch.Visibility = Visibility.Collapsed;
        _colorLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>采样记录：块像素（Bgra32 行优先）、实际块尺寸、中心（光标）像素颜色。</summary>
    private readonly record struct Sample(byte[] Pixels, int Width, int Height, Drawing.Color Color);

    /// <summary>
    /// 抓光标周围 SampleSize×SampleSize 物理像素块（虚拟桌面坐标）并读出光标像素颜色。
    /// 光标贴虚拟桌面边缘时块被钳进屏内，采样索引同步钳制 —— 色=光标所在像素，不漂。
    /// </summary>
    private Sample? GrabSample(Drawing.Point localPhysicalPoint)
    {
        var virtualPoint = ToVirtualPoint(localPhysicalPoint);
        var virtualScreen = ScreenCapture.GetVirtualScreenBounds();
        var half = SampleSize / 2;
        var rect = new Drawing.Rectangle(virtualPoint.X - half, virtualPoint.Y - half, SampleSize, SampleSize);
        rect.Intersect(virtualScreen);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        using var bitmap = ScreenCapture.GrabRegion(rect);
        var bytes = new byte[rect.Width * rect.Height * 4];
        var data = bitmap.LockBits(
            new Drawing.Rectangle(0, 0, rect.Width, rect.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // 光标在本块内的索引（钳进边界）—— 这才是用户指的那颗像素。
        var sx = Math.Clamp(virtualPoint.X - rect.X, 0, rect.Width - 1);
        var sy = Math.Clamp(virtualPoint.Y - rect.Y, 0, rect.Height - 1);
        var offset = (sy * rect.Width + sx) * 4; // Bgra32：B,G,R,A
        var color = Drawing.Color.FromArgb(bytes[offset + 3], bytes[offset + 2], bytes[offset + 1], bytes[offset]);
        return new Sample(bytes, rect.Width, rect.Height, color);
    }

    private void UpdateLoupe(Point dipCursor, byte[] pixels, int width, int height, Drawing.Color color)
    {
        _loupeBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        _loupe.Visibility = Visibility.Visible;

        // D7 的"放大 10×"是**视觉**倍率：Image.Width 是 DIP，缩放屏上 150 DIP ≠ 150 物理像素
        // （实测 175% 屏 150 DIP 显示 262 物理像素 ≈17×）。按窗实测 DPI 换算，任意缩放下
        // 显示尺寸恒为 150 物理像素（= 15 采样 × 10）。
        _loupe.Width = SampleSize * LoupeZoom / _scaleX;
        _loupe.Height = SampleSize * LoupeZoom / _scaleY;

        _swatch.Fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        _swatch.Visibility = Visibility.Visible;

        // 格式已在托盘侧校验（R10），此处 null 不可能；防御性回落 hex 不静默崩。
        _colorLabel.Text = ColorFormatter.TryFormat(color, _format) ?? ColorFormatter.Hex(color);
        _colorLabel.Visibility = Visibility.Visible;

        // loupe 跟随光标（DIP 域），贴右/下边缘翻转方向，避免被窗边裁掉。
        var lw = _loupe.Width;
        var lh = _loupe.Height + _swatch.Height + _colorLabel.ActualHeight + 12;
        var x = dipCursor.X + 24;
        var y = dipCursor.Y + 24;
        if (x + lw > ActualWidth)
        {
            x = dipCursor.X - 24 - lw;
        }

        if (y + lh > ActualHeight)
        {
            y = dipCursor.Y - 24 - lh;
        }

        x = Math.Max(0, x);
        y = Math.Max(0, y);
        Canvas.SetLeft(_loupe, x);
        Canvas.SetTop(_loupe, y);
        Canvas.SetLeft(_swatch, x);
        Canvas.SetTop(_swatch, y + _loupe.Height + 6);
        Canvas.SetLeft(_colorLabel, x + _swatch.Width + 6);
        Canvas.SetTop(_colorLabel, y + _loupe.Height + 6);
    }

    // ── 取色链路 ──

    private async Task PickAsync(Drawing.Point localPhysicalPoint)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            // 单击点 1×1 采样（同一条 GrabRegion+LockBits 链，与 loupe 预览所见一致 —— FR-7 前提）。
            var virtualPoint = ToVirtualPoint(localPhysicalPoint);
            var virtualScreen = ScreenCapture.GetVirtualScreenBounds();
            var rect = new Drawing.Rectangle(virtualPoint.X, virtualPoint.Y, 1, 1);
            rect.Intersect(virtualScreen);
            using var bitmap = ScreenCapture.GrabRegion(rect);
            var color = SampleCenter(bitmap);
            if (_resultTaken)
            {
                return;
            }

            // R10：格式非法时托盘侧已回落并告警；此处防御性回落 hex（不可达路径）。
            var text = ColorFormatter.TryFormat(color, _format) ?? ColorFormatter.Hex(color);
            if (TrySetClipboard(() => Clipboard.SetText(text)))
            {
                Finish(text);
            }
            else
            {
                ShowRetryHint("剪贴板写入失败");
            }
        }
        catch (Exception ex)
        {
            // 错误必须有可见出口（S 系红线）：失败提示在遮罩上，用户可重试或退出。
            ShowRetryHint($"取色失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>读块中心像素（Bgra32）。internal static 供探针确定性面复用（喂已知纯色图断言链路）。</summary>
    internal static Drawing.Color SampleCenter(Drawing.Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = data.Scan0 + (data.Height / 2) * data.Stride;
            var b = Marshal.ReadByte(row, (data.Width / 2) * 4);
            var g = Marshal.ReadByte(row, (data.Width / 2) * 4 + 1);
            var r = Marshal.ReadByte(row, (data.Width / 2) * 4 + 2);
            return Drawing.Color.FromArgb(r, g, b);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
