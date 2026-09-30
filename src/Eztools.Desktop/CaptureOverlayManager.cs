// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;

namespace Eztools.Desktop;

/// <summary>
/// 区域截图遮罩窗管理器（W6-b）：**每台显示器一扇**遮罩窗（W6 FR-1），统一生命周期。
/// 与 <see cref="OcrOverlayManager"/> 同款三态完成语义，但没有引擎懒创建/语言过期语义
/// —— 刻意**不抽 Manager 基类**（W6 设计方案 §3.1：强行统一会把 OCR 的复杂度传染过来）。
///
/// 完成语义（任一扇先到先得）：
///   · NotifyCopied —— 某窗成功写入剪贴板 ⇒ 收全部窗、<see cref="ImageCopied"/> 一次
///     （参数 = 尺寸描述 "W×H"，气泡文案用）；
///   · NotifyCancelled —— 某窗 Esc/右键/Alt+F4/**空拖拽** ⇒ 收全部窗、<see cref="Finished"/> 一次；
///   · 截图失败 ⇒ 不收窗，由该窗自行提示重试（同 OCR FR-4 语义）。
/// </summary>
internal sealed class CaptureOverlayManager
{
    private readonly List<CaptureOverlayWindow> _windows = [];
    private readonly Action<string>? _warn;
    private bool _closing;

    public CaptureOverlayManager(Action<string>? warn = null)
    {
        _warn = warn;
    }

    /// <summary>截图成功并已写入剪贴板时触发一次（参数 = "W×H" 尺寸描述）。</summary>
    public event Action<string>? ImageCopied;

    /// <summary>全部遮罩退出（取消路径）时触发一次。</summary>
    public event Action? Finished;

    /// <summary>唤出全部遮罩；返回窗数。</summary>
    public int ShowAll()
    {
        CloseAll();

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            _windows.Add(new CaptureOverlayWindow(this, screen.Bounds));
        }

        foreach (var window in _windows)
        {
            window.Show();
        }

        return _windows.Count;
    }

    /// <summary>探针采样：当前存活的遮罩窗数。</summary>
    public int VisibleCount => _windows.Count(w => w.ProbeIsVisible);

    /// <summary>还有没关掉的窗（含截图进行中/失败重试态）。</summary>
    public bool AnyAlive => _windows.Count > 0;

    /// <summary>存活窗快照（探针采样用；返回副本防遍历中集合被改）。</summary>
    internal IEnumerable<CaptureOverlayWindow> EnumerableWindows() => _windows.ToArray();

    internal void NotifyCopied(CaptureOverlayWindow source, string description)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            CloseAll();
            ImageCopied?.Invoke(description);
        }
        finally
        {
            _closing = false;
        }
    }

    internal void NotifyCancelled(CaptureOverlayWindow source)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            CloseAll();
            Finished?.Invoke();
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>收全部窗。窗的 Closing 会回调 NotifyCancelled，用 _closing 防重入。</summary>
    public void CloseAll()
    {
        var windows = _windows.ToArray();
        _windows.Clear();
        foreach (var window in windows)
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                _warn?.Invoke($"关闭截图遮罩窗失败（忽略）：{ex.Message}");
            }
        }
    }
}
