// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Eztools.Desktop;

/// <summary>
/// 全屏遮罩窗公共骨架（W6-a，从 OcrOverlayWindow 抽出；设计方案 §3.1 决策 D1=A）。
/// 每台显示器一扇，全屏置顶半透明，承载 dim + selection + sizeLabel + hint 四件套、
/// 拖拽框选状态机、Esc/右键/Alt+F4 取消、busy 忽略新拖拽、探针采样面。
/// capture（区域截图）与 pick（取色）两扇新窗与 OCR 窗共用本骨架（W6-b/c）。
///
/// 坐标纪律（W4 设计方案 §3.2，**单点维护** —— 本类是纪律唯一出处）：
///   · 拖拽在 WPF DIP 域里采集，<see cref="ToPhysicalRect"/>/<see cref="ToPhysicalPoint"/>
///     在**离开窗口前**换算成物理像素；
///   · DPI 缩放比在 <c>SourceInitialized</c> 后用 <c>GetDpiForWindow</c> 按窗实测
///     （本窗自身 per-monitor 生效后的读数），不走 Screen 的估算值；
///   · 窗口定位用 Win32 <c>SetWindowPos</c> 直接吃物理像素（WPF Window.Left/Top 的 DIP
///     语义在跨 DPI 多屏下不明确，W4-b 双屏实测抓出）；
///   · ★ 截图坐标系换算：<see cref="ToVirtualRect"/>/<see cref="ToVirtualPoint"/> ——
///     "窗口内部物理坐标 + 窗口 Win32 原点 = 虚拟桌面坐标"。主屏在 (0,0) 时侥幸相等，
///     窗口原点非零（副屏）时不加原点 = 截图区域整体偏移到别的屏（W4-b 双屏血案，
///     用户实测"外接屏截不到"的根因）。
///
/// 完成语义（挂 <see cref="Closing"/> 的系统关闭路径同样按"取消"上报）：
///   · <see cref="Finish"/> —— 成功写入剪贴板，派生类经 <see cref="OnCopied"/> 上报；
///   · <see cref="Cancel"/> —— Esc/右键/Alt+F4，派生类经 <see cref="OnCancelled"/> 上报；
///   · 两者先置 <c>_resultTaken</c>，收窗路径与主动路径互斥（防重复上报）。
///
/// ⚠️ 双栈类型歧义：本文件同时可见 WPF 的 Shapes.Rectangle 与 GDI 的 Drawing.Rectangle，
/// GDI 类型一律走 <c>Drawing.</c> 别名（csproj 全局别名约定）。
/// </summary>
internal abstract class OverlayWindowBase : Window
{
    private readonly Drawing.Rectangle _physicalBounds; // 本显示器物理像素矩形（虚拟桌面坐标）
    private readonly Canvas _root;
    protected readonly Rectangle _dim; // 全屏 dim 层（派生类可按自身语义重排/隐藏，见三个拖拽钩子）
    private readonly Rectangle _selection;
    private readonly TextBlock _sizeLabel;
    private readonly TextBlock _hint;
    protected double _scaleX = 1.0;
    protected double _scaleY = 1.0;
    private bool _dragging;
    protected bool _busy;
    protected bool _resultTaken; // 完成令牌：派生类异步链用它识别"窗已被收走，结果作废"
    private Point _dragStart; // DIP

    protected OverlayWindowBase(Drawing.Rectangle physicalBounds, string hintText)
    {
        _physicalBounds = physicalBounds;

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
            Fill = SelectionFill,
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
            Text = hintText,
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
            // Alt+F4 等系统关闭路径同样按"取消"上报；主动收窗时 _resultTaken 已置位。
            if (!_resultTaken)
            {
                _resultTaken = true;
                OnCancelled();
            }
        };
    }

    // ── 派生类扩展点 ──

    /// <summary>
    /// 拖拽过程中是否显示选区高亮与尺寸标签。
    /// OCR/capture 有框选语义（true）；pick 无框选概念（false）—— 按下不画选区，
    /// 拖拽只是"点起来又放下"，语义仍归取色（W6 设计方案 §4）。
    /// </summary>
    protected virtual bool ShowDragSelection => true;

    /// <summary>拖拽距离过小（&lt;6 物理像素）视为单击：命中域由派生类决定语义（OCR=取词，pick/capture=取色/取消）。</summary>
    protected abstract void HandleClick(Drawing.Point localPhysicalPoint);

    /// <summary>
    /// 有效框选完成：选区与**释放点**均为窗口内部物理像素坐标，语义由派生类决定
    /// （OCR=识别选区，capture=截选区，pick=对释放点取色 —— 拖拽释放也是一次取色意图）。
    /// </summary>
    protected abstract void HandleRegion(Drawing.Rectangle localPhysicalRect, Drawing.Point releasePhysicalPoint);

    /// <summary>成功写入剪贴板后的上报钩子（收全部窗 + 气泡由 manager 层负责）。</summary>
    protected abstract void OnCopied(string text);

    /// <summary>取消路径的上报钩子（Esc/右键/Alt+F4/系统关闭）。</summary>
    protected abstract void OnCancelled();

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

    /// <summary>
    /// 窗口实际的 Win32 位置（物理像素）—— 探针用它对账"摆没摆对"（M1 自动化判据）。
    /// ⚠️ GetWindowRect 出参是 RECT{L,T,R,B}，不能直接 marshal 成 Rectangle{X,Y,W,H} ——
    /// 首版 Width 字段读到的是 Right（多屏下 = 2560+1920 = 4480），主屏 X=0 时侥幸相等掩盖了 bug。
    /// </summary>
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

    // ── 坐标换算（纪律单点） ──

    /// <summary>DIP 矩形选区 → 窗口内部物理像素矩形（在离开窗口前换算）。</summary>
    protected Drawing.Rectangle ToPhysicalRect(Point a, Point b)
    {
        var x1 = (int)Math.Round(Math.Min(a.X, b.X) * _scaleX);
        var y1 = (int)Math.Round(Math.Min(a.Y, b.Y) * _scaleY);
        var x2 = (int)Math.Round(Math.Max(a.X, b.X) * _scaleX);
        var y2 = (int)Math.Round(Math.Max(a.Y, b.Y) * _scaleY);
        return new Drawing.Rectangle(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>DIP 点 → 窗口内部物理像素点。</summary>
    protected Drawing.Point ToPhysicalPoint(Point dip) => new(
        (int)Math.Round(dip.X * _scaleX),
        (int)Math.Round(dip.Y * _scaleY));

    /// <summary>
    /// 窗口内部物理矩形 → 虚拟桌面物理矩形（GDI <c>CopyFromScreen</c> 吃虚拟桌面坐标）。
    /// ★ 窗口原点非零（副屏/双屏）时差一个原点，不加 = 截图区域整体偏移到别的屏（W4-b 血案）。
    /// </summary>
    protected Drawing.Rectangle ToVirtualRect(Drawing.Rectangle localPhysicalRect)
    {
        var origin = ProbeWin32Rect.Location;
        return new Drawing.Rectangle(
            origin.X + localPhysicalRect.X,
            origin.Y + localPhysicalRect.Y,
            localPhysicalRect.Width,
            localPhysicalRect.Height);
    }

    /// <summary>窗口内部物理点 → 虚拟桌面物理点（与 <see cref="ToVirtualRect"/> 同一换算）。</summary>
    protected Drawing.Point ToVirtualPoint(Drawing.Point localPhysicalPoint)
    {
        var origin = ProbeWin32Rect.Location;
        return new Drawing.Point(origin.X + localPhysicalPoint.X, origin.Y + localPhysicalPoint.Y);
    }

    // ── 拖拽状态机 ──

    /// <summary>选区高亮填充。默认半透明蓝（OCR 观感不变）；capture 覆写为全透 ——
    /// 半透明填充会被 CopyFromScreen 抓进截图给内容加色罩（W6-d 实测 #123456 → #1a456b，
    /// 混合比与 0x20 填充精确吻合），只留描边。</summary>
    protected virtual Brush SelectionFill => new SolidColorBrush(Color.FromArgb(0x20, 0x4C, 0xC2, 0xFF));

    /// <summary>
    /// 拖拽选区更新时的 dim 重排钩子。默认**无操作**（OCR 全屏 dim 行为不变 —— W6-d 发现
    /// dim 会被 CopyFromScreen 抓进截图/取色，capture/pick 派生类按自身语义重排，见各自实现）。
    /// <paramref name="selectionDip"/> = 当前选区（DIP，Canvas 坐标）。
    /// </summary>
    protected virtual void OnSelectionDragging(Rect selectionDip) { }

    /// <summary>拖拽开始钩子（CaptureMouse 之后）。默认无操作。</summary>
    protected virtual void OnDragStarted() { }

    /// <summary>拖拽结束钩子（释放鼠标之后、派生类 HandleClick/HandleRegion **之后** ——
    /// capture 的同步截图依赖"拖拽中的视觉状态"（dim 亮洞）仍在）。默认无操作。</summary>
    protected virtual void OnDragFinished() { }

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ProbeMouseDownCount++;
        if (_busy)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        if (ShowDragSelection)
        {
            _selection.Visibility = Visibility.Visible;
            _sizeLabel.Visibility = Visibility.Visible;
        }
        UpdateSelection(e.GetPosition(this));
        CaptureMouse();
        OnDragStarted();
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
        OnSelectionDragging(new Rect(x, y, w, h));
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
        if (ShowDragSelection)
        {
            _selection.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
        }
        OnDragFinished();

        var physRect = ToPhysicalRect(_dragStart, current);
        var releasePoint = ToPhysicalPoint(current);
        // 顺序纪律（W6-d）：先派生类语义（HandleRegion 的同步段就完成截图/取色 ——
        // 依赖"拖拽中的视觉状态"如 capture 的 dim 亮洞仍在），**后** OnDragFinished 恢复视觉。
        if (physRect.Width < 6 || physRect.Height < 6)
        {
            HandleClick(releasePoint);
        }
        else
        {
            HandleRegion(physRect, releasePoint);
        }
        OnDragFinished();
    }

    // ── 提示与完成语义 ──

    protected void ShowRetryHint(string message)
    {
        _hint.Text = $"{message} —— 重新框选，或 Esc 退出";
        Canvas.SetLeft(_hint, Math.Max(12, (_root.ActualWidth - _hint.ActualWidth) / 2));
    }

    /// <summary>成功路径上报：置结果令牌 → 派生类上报 manager。</summary>
    protected void Finish(string text)
    {
        _resultTaken = true;
        OnCopied(text);
    }

    /// <summary>取消路径上报：防重入（已上报则忽略）。</summary>
    protected void Cancel()
    {
        if (_resultTaken)
        {
            return;
        }

        _resultTaken = true;
        OnCancelled();
    }

    // ── 定位与 DPI（W4-b 实测纪律） ──

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

    // ── 剪贴板输出（W6 设计方案 §11：capture 的 SetImage 与 pick 的 SetText 共用同一重试壳） ──

    protected static bool TrySetClipboard(Action write)
    {
        // 剪贴板是全局竞争资源：外部程序正在打开时会抛 COMException —— 重试 ≤3 次（W4 R7）。
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return true;
            }
            catch (COMException) when (attempt < 3)
            {
                Thread.Sleep(60);
            }
        }
    }

    // ── Win32 ──

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Drawing.Rectangle rect);

    private const uint SwpNoZorder = 0x0004;
}
