// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Ocr;

namespace Eztools.Desktop;

/// <summary>
/// OCR 遮罩窗管理器（W4-b）：**每台显示器一扇**遮罩窗（FR-1），统一生命周期。
///
/// 完成语义（任一扇先到先得）：
///   · NotifyCopied —— 某窗成功写入剪贴板 ⇒ 收全部窗、<see cref="TextCopied"/> 一次；
///   · NotifyCancelled —— 某窗 Esc/右键/Alt+F4 ⇒ 收全部窗、<see cref="Finished"/> 一次；
///   · NotifyRetry —— 空选区/识别失败 ⇒ **不收窗**（FR-4 留在原地重试），由该窗自行提示。
/// </summary>
internal sealed class OcrOverlayManager
{
    private readonly List<OcrOverlayWindow> _windows = [];
    private readonly Action<string>? _warn;
    private WindowsOcrEngine? _engine;
    private bool _closing;

    public OcrOverlayManager(Action<string>? warn = null)
    {
        _warn = warn;
    }

    /// <summary>识别成功并已写入剪贴板的文字（完成时触发一次）。</summary>
    public event Action<string>? TextCopied;

    /// <summary>全部遮罩退出（取消路径）时触发一次。</summary>
    public event Action? Finished;

    /// <summary>共享引擎（同线程创建使用；语言包缺失时抛出 —— R1 禁静默）。</summary>
    internal WindowsOcrEngine Engine => _engine
        ?? throw new InvalidOperationException(OcrLanguages.InstallHint);

    /// <summary>
    /// 当前引擎的识别语言（BCP-47）；<c>null</c> = 引擎尚未创建。
    /// 托盘（W4-c）据此判断"配置语言已改、旧引擎语言过期" —— 过期则整个 manager 重建。
    /// </summary>
    public string? CurrentLanguageTag => _engine?.LanguageTag;

    /// <summary>
    /// 唤出全部遮罩；返回窗数。语言包缺失时抛 <see cref="InvalidOperationException"/>（含安装引导）。
    /// <paramref name="bcp47Language"/> 为空 = 引擎按默认优先级自动选（用户配置语言 → 系统首选 → 任一可用）。
    /// 引擎**懒创建且语言在创建时固定** —— 用户改了配置语言后由调用方决定何时重建（见 <see cref="CurrentLanguageTag"/>）。
    /// </summary>
    public int ShowAll(string? bcp47Language = null)
    {
        CloseAll();

        _engine ??= WindowsOcrEngine.TryCreate(string.IsNullOrWhiteSpace(bcp47Language) ? null : bcp47Language)
            ?? throw new InvalidOperationException(OcrLanguages.InstallHint);

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var window = new OcrOverlayWindow(this, screen.Bounds);
            _windows.Add(window);
        }

        foreach (var window in _windows)
        {
            window.Show();
        }

        return _windows.Count;
    }

    /// <summary>探针采样：当前存活的遮罩窗数。</summary>
    public int VisibleCount => _windows.Count(w => w.ProbeIsVisible);

    /// <summary>还有没关掉的窗（含识别中/空选区重试态）。</summary>
    public bool AnyAlive => _windows.Count > 0;

    /// <summary>存活窗快照（探针采样用；返回副本防遍历中集合被改）。</summary>
    internal IEnumerable<OcrOverlayWindow> EnumerableWindows() => _windows.ToArray();

    /// <summary>最近一次完成（复制/取消）时的逐窗诊断快照 —— --ocr-show 落盘用。</summary>
    public JsonObject? LastDiagnostics { get; private set; }

    private void SnapshotDiagnostics()
    {
        var windows = new JsonArray();
        foreach (var window in _windows)
        {
            windows.Add(new JsonObject
            {
                ["intended"] = $"{window.ProbeIntendedBounds.X},{window.ProbeIntendedBounds.Y},{window.ProbeIntendedBounds.Width}x{window.ProbeIntendedBounds.Height}",
                ["win32"] = $"{window.ProbeWin32Rect.X},{window.ProbeWin32Rect.Y},{window.ProbeWin32Rect.Width}x{window.ProbeWin32Rect.Height}",
                ["scale"] = window.ProbeScale,
                ["mouseEnter"] = window.ProbeMouseEnterCount,
                ["mouseDown"] = window.ProbeMouseDownCount,
                ["hint"] = window.ProbeHintText,
            });
        }

        LastDiagnostics = new JsonObject { ["windows"] = windows };
    }

    internal void NotifyCopied(OcrOverlayWindow source, string text)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            SnapshotDiagnostics();
            CloseAll();
            TextCopied?.Invoke(text);
        }
        finally
        {
            _closing = false;
        }
    }

    internal void NotifyCancelled(OcrOverlayWindow source)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            SnapshotDiagnostics();
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
                _warn?.Invoke($"关闭 OCR 遮罩窗失败（忽略）：{ex.Message}");
            }
        }
    }
}
