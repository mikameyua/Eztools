// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Eztools.Ocr;

namespace Eztools.Desktop;

/// <summary>
/// OCR 框选遮罩窗（W4-b）：每台显示器一扇，全屏置顶半透明，拖拽框选 → 截图 → OCR → 剪贴板。
///
/// 交互契约（Text-Grab 同款，设计方案 §1）：
///   · 拖拽松开即识别复制；选区内无文字 → **窗口保持活动**提示重试（FR-4），不闪退；
///   · 拖拽距离过小 = 单击 → 按 word 包围盒取词（FR-5）；
///   · Esc / 右键 → 全部遮罩退出（FR-1）；
///   · 识别中（_busy）忽略新的拖拽，退出请求仍受理。
///
/// 坐标纪律（设计方案 §3.2）：拖拽在 WPF DIP 域里采集，**离开窗口前**就换算成物理像素，
/// 截图/取词全程物理像素。DPI 缩放比在 <c>SourceInitialized</c> 后用 <c>GetDpiForWindow</c>
/// 按窗实测（本窗自身 per-monitor 生效后的读数），不走 Screen 的估算值。
///
/// ⚠️ 双栈类型歧义：本文件同时可见 WPF 的 Shapes.Rectangle 与 GDI 的 Drawing.Rectangle，
/// GDI 类型一律走 <c>Drawing.</c> 别名（Desktop 项目级别名约定同源）。
/// </summary>
internal sealed class OcrOverlayWindow : Window
{
    private readonly OcrOverlayManager _owner;
    private readonly Drawing.Rectangle _physicalBounds; // 本显示器物理像素矩形（虚拟桌面坐标）
    private readonly Canvas _root;
    private readonly Rectangle _dim;
    private readonly Rectangle _selection;
    private readonly TextBlock _sizeLabel;
    private readonly TextBlock _hint;
    private double _scaleX = 1.0;
    private double _scaleY = 1.0;
    private bool _dragging;
    private bool _busy;
    private bool _resultTaken;
    private Point _dragStart; // DIP

    public OcrOverlayWindow(OcrOverlayManager owner, Drawing.Rectangle physicalBounds)
    {
        _owner = owner;
        _physicalBounds = physicalBounds;

        Title = "Eztools OCR";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;

        _dim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)) };
        _selection = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(0x20, 0x4C, 0xC2, 0xFF)),
            Visibility = Visibility.Collapsed,
        };
        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };
        _hint = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 13,
            Text = "拖拽框选要提取的文字（单击取词）· Esc 取消",
        };

        _root = new Canvas { Background = Brushes.Transparent };
        _root.Children.Add(_dim);
        _root.Children.Add(_selection);
        _root.Children.Add(_sizeLabel);
        _root.Children.Add(_hint);
        Content = _root;

        // 起始定位按 1:1 物理像素（绝大多数场景主屏缩放正确）；SourceInitialized 后按窗实测 DPI 修正。
        Left = physicalBounds.Left;
        Top = physicalBounds.Top;
        Width = physicalBounds.Width;
        Height = physicalBounds.Height;

        SizeChanged += (_, e) =>
        {
            _dim.Width = e.NewSize.Width;
            _dim.Height = e.NewSize.Height;
            Canvas.SetLeft(_hint, Math.Max(12, (e.NewSize.Width - _hint.ActualWidth) / 2));
            Canvas.SetTop(_hint, 18);
        };

        SourceInitialized += (_, _) => ApplyMonitorDpi();
        Loaded += (_, _) => SetWindowPosExact();
        MouseEnter += (_, _) => ProbeMouseEnterCount++;
        MouseLeftButtonDown += OnLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftButtonUp;
        MouseRightButtonDown += (_, _) => Cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)))
            {
                Cancel();
                e.Handled = true;
            }
        };
        Closing += (_, _) =>
        {
            // Alt+F4 等系统关闭路径同样按"取消"上报；manager 主动收窗时 _resultTaken 已置位。
            if (!_resultTaken)
            {
                _resultTaken = true;
                _owner.NotifyCancelled(this);
            }
        };
    }

    // ── 探针采样面 ──

    public bool ProbeIsVisible => IsVisible;

    public string ProbeHintText => _hint.Text;

    /// <summary>按窗实测的 DPI 缩放比（探针采样用；R2 机制证据）。</summary>
    public double ProbeScale => _scaleX;

    /// <summary>鼠标进入本窗的次数（诊断：外接屏"无法截图"时区分"鼠标从没进过窗"与"进了但拖拽没生效"）。</summary>
    public int ProbeMouseEnterCount { get; private set; }

    /// <summary>本窗收到的按下次数（同上）。</summary>
    public int ProbeMouseDownCount { get; private set; }

    /// <summary>预期物理矩形（探针对账用：ProbeWin32Rect 应与它一致）。</summary>
    public Drawing.Rectangle ProbeIntendedBounds => _physicalBounds;

    /// <summary>最近一次取词尝试的诊断（区域 + 区域内识别出的字符数）—— miss 时区分"区域内没字"与"命中测试没打中"。</summary>
    public string ProbeLastWordDebug => _lastWordDebug;
    private string _lastWordDebug = string.Empty;

    /// <summary>
    /// 探针专用：喂一张**纯色空白位图**走 CopyRegionAsync 的同一条后处理链（识别→空结果→FR-4 提示）。
    /// M2/M4 的确定性断言入口 —— 屏幕上找不到可控的空白区，就自己造一张。
    /// </summary>
    internal async Task ProbeFeedBlankAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            using var blank = new Drawing.Bitmap(200, 100);
            using var graphics = Drawing.Graphics.FromImage(blank);
            graphics.Clear(Drawing.Color.FromArgb(32, 32, 32));
            await HandleCapturedAsync(blank).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ApplyMonitorDpi()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            return;
        }

        _scaleX = dpi / 96.0;
        _scaleY = dpi / 96.0;

        // ★ R2 根治（2026-09-26 双屏实测抓出）：WPF Window.Left/Top 的 DIP 语义在**跨 DPI 多屏**
        //   下不明确（相对主屏换算还是相对本屏，版本行为不一）—— 用 DIP 数学定位 175%/100% 混搭时
        //   窗口实际落点会偏移。改为 Win32 SetWindowPos **直接吃物理像素**，绕开整套 DIP 歧义；
        //   内容布局（Canvas 尺寸、鼠标 DIP 坐标）仍由 WPF 按本窗 DPI 自动处理，互不干扰。
        //   Loaded 后再执行一次：WPF Show() 时会按自己的 Left/Top 摆一次，必须在其后覆盖。
        SetWindowPosExact();
    }

    private void SetWindowPosExact()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, nint.Zero, _physicalBounds.X, _physicalBounds.Y,
            _physicalBounds.Width, _physicalBounds.Height, SwpNoZorder);
    }

    /// <summary>窗口实际的 Win32 位置（物理像素）—— 探针用它对账"摆没摆对"（M1 自动化判据）。
    /// ⚠️ GetWindowRect 出参是 RECT{L,T,R,B}，不能直接 marshal 成 Rectangle{X,Y,W,H} ——
    /// 首版 Width 字段读到的是 Right（多屏下 = 2560+1920 = 4480），主屏 X=0 时侥幸相等掩盖了 bug。</summary>
    public Drawing.Rectangle ProbeWin32Rect
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var rect))
            {
                return default;
            }

            return Drawing.Rectangle.FromLTRB(rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ProbeMouseDownCount++;
        if (_busy)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _selection.Visibility = Visibility.Visible;
        _sizeLabel.Visibility = Visibility.Visible;
        UpdateSelection(e.GetPosition(this));
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        UpdateSelection(e.GetPosition(this));
    }

    private void UpdateSelection(Point current)
    {
        var x = Math.Min(_dragStart.X, current.X);
        var y = Math.Min(_dragStart.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.X);
        var h = Math.Abs(current.Y - _dragStart.Y);

        Canvas.SetLeft(_selection, x);
        Canvas.SetTop(_selection, y);
        _selection.Width = w;
        _selection.Height = h;

        _sizeLabel.Text = $"{(int)(w * _scaleX)} × {(int)(h * _scaleY)} px";
        Canvas.SetLeft(_sizeLabel, x);
        Canvas.SetTop(_sizeLabel, y - 26 > 0 ? y - 26 : y + h + 6);
    }

    private void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        var current = e.GetPosition(this);
        _selection.Visibility = Visibility.Collapsed;
        _sizeLabel.Visibility = Visibility.Collapsed;

        var physRect = ToPhysicalRect(_dragStart, current);
        if (physRect.Width < 6 || physRect.Height < 6)
        {
            // 单击：按 word 包围盒取词（FR-5）。命中域过小就抓以点为中心的一块上下文。
            _ = CopyWordAtAsync(ToPhysicalPoint(current));
        }
        else
        {
            _ = CopyRegionAsync(physRect);
        }
    }

    private Drawing.Rectangle ToPhysicalRect(Point a, Point b)
    {
        var x1 = (int)Math.Round(Math.Min(a.X, b.X) * _scaleX);
        var y1 = (int)Math.Round(Math.Min(a.Y, b.Y) * _scaleY);
        var x2 = (int)Math.Round(Math.Max(a.X, b.X) * _scaleX);
        var y2 = (int)Math.Round(Math.Max(a.Y, b.Y) * _scaleY);
        return new Drawing.Rectangle(x1, y1, x2 - x1, y2 - y1);
    }

    private Drawing.Point ToPhysicalPoint(Point dip) => new(
        (int)Math.Round(dip.X * _scaleX),
        (int)Math.Round(dip.Y * _scaleY));

    private void ShowRetryHint(string message)
    {
        _hint.Text = $"{message} —— 重新框选，或 Esc 退出";
        Canvas.SetLeft(_hint, Math.Max(12, (_root.ActualWidth - _hint.ActualWidth) / 2));
    }

    private async Task CopyRegionAsync(Drawing.Rectangle localPhysicalRect)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            // ★ 坐标系修正（2026-09-26 双屏实测抓出）：CopyFromScreen 吃**虚拟桌面**坐标，
            //   而 physicalRect 是**窗口内部**物理坐标 —— 窗口原点非零（副屏/双屏）时两者差一个
            //   窗口原点，截图区域整体偏移到别的屏上（用户实测"外接屏截不到"的根因；
            //   主屏在 (0,0) 时侥幸相等，单屏测试一直没暴露）。
            var origin = ProbeWin32Rect.Location;
            var virtualRect = new Drawing.Rectangle(
                origin.X + localPhysicalRect.X,
                origin.Y + localPhysicalRect.Y,
                localPhysicalRect.Width,
                localPhysicalRect.Height);
            using var bitmap = ScreenCapture.GrabRegion(virtualRect);
            await HandleCapturedAsync(bitmap).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 错误必须有可见出口（S 系红线）：识别失败提示在遮罩上，用户可重试或退出。
            ShowRetryHint($"识别失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 截图之后的公共后处理链：识别 → 空结果提示（FR-4 保持活动）/ 写剪贴板（FR-3）→ Finish。
    /// M2/M4 的手工断言点就在这条链上 —— 探针经 <see cref="ProbeFeedBlankAsync"/> 直接喂图走同一条链，
    /// 不再依赖"屏幕上恰好有空白区"这种不可控前提。
    /// </summary>
    private async Task HandleCapturedAsync(Drawing.Bitmap bitmap)
    {
        var result = await _owner.Engine.RecognizeAsync(bitmap).ConfigureAwait(true);
        if (_resultTaken)
        {
            return; // 识别期间窗被收走（Esc 等）—— 结果作废，不写剪贴板
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            ShowRetryHint("未识别到文字");
            return; // FR-4：窗口保持活动
        }

        if (TrySetClipboard(result.Text))
        {
            Finish(result.Text);
        }
        else
        {
            ShowRetryHint("剪贴板写入失败");
        }
    }

    private async Task CopyWordAtAsync(Drawing.Point localPhysicalPoint)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            // 以点击点为中心抓一块上下文（word 包围盒来自整块识别结果，孤点没得认）。
            // ★ 与 CopyRegionAsync 同款坐标系修正：窗口内部坐标 + 窗口原点 = 虚拟桌面坐标。
            var origin = ProbeWin32Rect.Location;
            var virtualPoint = new Drawing.Point(
                origin.X + localPhysicalPoint.X,
                origin.Y + localPhysicalPoint.Y);
            var virtualScreen = ScreenCapture.GetVirtualScreenBounds();
            const int size = 480;
            var rect = new Drawing.Rectangle(
                virtualPoint.X - (size / 2),
                virtualPoint.Y - (size / 4),
                size,
                size / 2);
            rect.Intersect(virtualScreen);

            using var bitmap = ScreenCapture.GrabRegion(rect);
            var result = await _owner.Engine.RecognizeAsync(bitmap).ConfigureAwait(true);
            if (_resultTaken)
            {
                return;
            }

            var local = new Drawing.Point(virtualPoint.X - rect.X, virtualPoint.Y - rect.Y);
            var word = WindowsOcrEngine.FindWordAt(result, local);
            _lastWordDebug = $"rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height}) chars={result.Text.Length} words={result.Lines.Sum(l => l.Words.Count)} local=({local.X},{local.Y})";
            if (string.IsNullOrEmpty(word))
            {
                ShowRetryHint("未点到文字");
                return;
            }

            if (TrySetClipboard(word))
            {
                Finish(word);
            }
            else
            {
                ShowRetryHint("剪贴板写入失败");
            }
        }
        catch (Exception ex)
        {
            ShowRetryHint($"识别失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static bool TrySetClipboard(string text)
    {
        // 剪贴板是全局竞争资源：外部程序正在打开时会抛 COMException —— 重试 ≤3 次（设计方案 R7）。
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 3)
            {
                Thread.Sleep(60);
            }
        }
    }

    private void Finish(string text)
    {
        _resultTaken = true;
        _owner.NotifyCopied(this, text);
    }

    private void Cancel()
    {
        if (_resultTaken)
        {
            return;
        }

        _resultTaken = true;
        _owner.NotifyCancelled(this);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Drawing.Rectangle rect);

    private const uint SwpNoZorder = 0x0004;
}
