// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Input;
using Eztools.Ocr;

namespace Eztools.Desktop;

/// <summary>
/// OCR 框选遮罩窗（W4-b；W6-a 起继承 <see cref="OverlayWindowBase"/> 公共骨架，行为零变化）：
/// 每台显示器一扇，拖拽框选 → 截图 → OCR → 剪贴板。
///
/// 交互契约（Text-Grab 同款，设计方案 §1）：
///   · 拖拽松开即识别复制；选区内无文字 → **窗口保持活动**提示重试（FR-4），不闪退；
///   · 拖拽距离过小 = 单击 → 按 word 包围盒取词（FR-5）；
///   · Esc / 右键 → 全部遮罩退出（FR-1）；
///   · 识别中（_busy）忽略新的拖拽，退出请求仍受理。
///
/// 坐标纪律（DIP 采集 / GetDpiForWindow 按窗实测 / SetWindowPos 物理像素 /
/// "窗口内部坐标 + 窗口原点 = 虚拟桌面坐标"）已收进 <see cref="OverlayWindowBase"/> 单点维护，
/// 本类只消费 <see cref="OverlayWindowBase.ToVirtualRect"/> / <see cref="OverlayWindowBase.ToVirtualPoint"/>。
///
/// ⚠️ 双栈类型歧义：GDI 类型一律走 <c>Drawing.</c> 别名（csproj 全局别名约定同源）。
/// </summary>
internal sealed class OcrOverlayWindow : OverlayWindowBase
{
    private readonly OcrOverlayManager _owner;

    /// <summary>最近一次取词尝试的诊断（区域 + 区域内识别出的字符数）—— miss 时区分"区域内没字"与"命中测试没打中"。</summary>
    public string ProbeLastWordDebug => _lastWordDebug;
    private string _lastWordDebug = string.Empty;

    public OcrOverlayWindow(OcrOverlayManager owner, Drawing.Rectangle physicalBounds)
        : base(physicalBounds, "拖拽框选要提取的文字（单击取词）· Esc 取消")
    {
        _owner = owner;
        Title = "Eztools OCR";
    }

    // ── Base 扩展点实现 ──

    protected override void HandleClick(Drawing.Point localPhysicalPoint)
    {
        // 单击：按 word 包围盒取词（FR-5）。命中域过小就抓以点为中心的一块上下文。
        _ = CopyWordAtAsync(localPhysicalPoint);
    }

    protected override void HandleRegion(Drawing.Rectangle localPhysicalRect, Drawing.Point releasePhysicalPoint)
    {
        _ = CopyRegionAsync(localPhysicalRect); // OCR 只认选区；释放点语义与取词无关（W6-c 扩展点保持兼容）
    }

    protected override void OnCopied(string text) => _owner.NotifyCopied(this, text);

    protected override void OnCancelled() => _owner.NotifyCancelled(this);

    // ── 探针专用 ──

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

    // ── OCR 链路 ──

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

        if (TrySetClipboard(() => Clipboard.SetText(result.Text)))
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
            // ★ 与 CopyRegionAsync 同款坐标系修正：窗口内部坐标 + 窗口原点 = 虚拟桌面坐标
            //   （Base.ToVirtualPoint / Base.ToVirtualRect 单点换算）。
            var virtualPoint = ToVirtualPoint(localPhysicalPoint);
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

            if (TrySetClipboard(() => Clipboard.SetText(word)))
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
}
