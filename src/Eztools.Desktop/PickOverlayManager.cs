// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Desktop;

/// <summary>
/// 屏幕取色遮罩窗管理器（W6-c）：**每台显示器一扇**遮罩窗（W6 FR-1），统一生命周期。
/// 与 <see cref="CaptureOverlayManager"/> 同款三态完成语义，多带一个**格式参数**
/// （color.format 生效值在唤出时读定，改配置下次唤出生效 —— 无 OCR 的引擎/语言过期问题）。
///
/// 完成语义（任一扇先到先得）：
///   · NotifyCopied —— 某窗成功写入剪贴板 ⇒ 收全部窗、<see cref="ColorCopied"/> 一次（参数 = 色值文本）；
///   · NotifyCancelled —— 某窗 Esc/右键/Alt+F4 ⇒ 收全部窗、<see cref="Finished"/> 一次；
///   · 取色失败 ⇒ 不收窗，由该窗自行提示重试（同 OCR FR-4 语义）。
/// </summary>
internal sealed class PickOverlayManager
{
    private readonly List<PickOverlayWindow> _windows = [];
    private readonly Action<string>? _warn;
    private bool _closing;

    public PickOverlayManager(Action<string>? warn = null)
    {
        _warn = warn;
    }

    /// <summary>取色成功并已写入剪贴板时触发一次（参数 = 格式化后的色值文本）。</summary>
    public event Action<string>? ColorCopied;

    /// <summary>全部遮罩退出（取消路径）时触发一次。</summary>
    public event Action? Finished;

    /// <summary>唤出全部遮罩；返回窗数。<paramref name="format"/> = color.format 生效值（hex/rgb/hsl）。</summary>
    public int ShowAll(string format)
    {
        CloseAll();

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            _windows.Add(new PickOverlayWindow(this, screen.Bounds, format));
        }

        foreach (var window in _windows)
        {
            window.Show();
        }

        return _windows.Count;
    }

    /// <summary>探针采样：当前存活的遮罩窗数。</summary>
    public int VisibleCount => _windows.Count(w => w.ProbeIsVisible);

    /// <summary>还有没关掉的窗（含取色进行中/失败重试态）。</summary>
    public bool AnyAlive => _windows.Count > 0;

    /// <summary>存活窗快照（探针采样用；返回副本防遍历中集合被改）。</summary>
    internal IEnumerable<PickOverlayWindow> EnumerableWindows() => _windows.ToArray();

    internal void NotifyCopied(PickOverlayWindow source, string text)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            CloseAll();
            ColorCopied?.Invoke(text);
        }
        finally
        {
            _closing = false;
        }
    }

    internal void NotifyCancelled(PickOverlayWindow source)
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
                _warn?.Invoke($"关闭取色遮罩窗失败（忽略）：{ex.Message}");
            }
        }
    }
}
