// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Eztools.Desktop;

/// <summary>
/// 截图遮罩端到端探针（--probe-capture-overlay，W6-b 验收面）。
/// 真鼠标拖拽（遮罩全屏置顶 ⇒ 注入必然落在遮罩上）→ 断言剪贴板真拿到位图且
/// **尺寸与拖拽矩形一致**（±1 圆整容差 —— DIP↔物理像素 Round 的理论漂移）→
/// 再验"空拖拽（单击）= 取消出窗"契约（W6 设计方案 §4）。
///
/// 注入原语（InjectDrag/InjectMove/PumpFor）与 <see cref="OcrOverlayProbe"/> 同款纪律：
/// · MOUSEEVENTF_VIRTUALDESK 必带（否则绝对坐标被 OS 按主屏映射，双 DPI 落点偏移）；
/// · 泵必须走 WPF Dispatcher（ApplicationIdle）—— DoEvents 在探针上下文会吞键盘消息
///   （踩坑全集 §2.27；本探针只注鼠标，但泵口径保持一致）。
///
/// ⚠️ 副作用：真实移动鼠标、**改写系统剪贴板**（复制屏幕内容进去）—— 只能跑在无人交互的会话。
/// </summary>
internal static class CaptureOverlayProbe
{
    public static JsonObject Run(Action<string>? warn = null)
    {
        var manager = new CaptureOverlayManager(warn);
        string? copiedLabel = null;
        manager.ImageCopied += text => copiedLabel = text;

        var json = new JsonObject { ["ok"] = false };
        try
        {
            // ① 唤出
            var monitors = manager.ShowAll();
            PumpFor(500);
            var t0Visible = manager.VisibleCount;
            json["monitors"] = monitors;
            json["t0Visible"] = t0Visible;

            // R2 机制证据：摆放对账（同 OCR 探针口径，容差 8px）
            var placementAllMatch = true;
            var placement = new JsonArray();
            foreach (var window in manager.EnumerableWindows())
            {
                var intended = window.ProbeIntendedBounds;
                var actual = window.ProbeWin32Rect;
                var match = Math.Abs(actual.X - intended.X) <= 8
                    && Math.Abs(actual.Y - intended.Y) <= 8
                    && Math.Abs(actual.Width - intended.Width) <= 8
                    && Math.Abs(actual.Height - intended.Height) <= 8;
                placementAllMatch &= match;
                placement.Add(new JsonObject
                {
                    ["intended"] = $"{intended.X},{intended.Y},{intended.Width}x{intended.Height}",
                    ["actual"] = $"{actual.X},{actual.Y},{actual.Width}x{actual.Height}",
                    ["match"] = match,
                    ["scale"] = window.ProbeScale,
                });
            }

            json["placementAllMatch"] = placementAllMatch;
            json["placement"] = placement;

            // ② 真拖拽：第一扇窗 intended bounds 中心 320×240 物理像素
            //    （自校准 —— 不假设屏幕分辨率与缩放；320/240 随小屏钳到半屏内）
            var first = manager.EnumerableWindows().FirstOrDefault();
            if (first is null)
            {
                json["error"] = "唤出后没有任何遮罩窗";
                return json;
            }

            var bounds = first.ProbeIntendedBounds;
            var w = Math.Min(320, bounds.Width / 2);
            var h = Math.Min(240, bounds.Height / 2);
            var x1 = bounds.X + (bounds.Width - w) / 2;
            var y1 = bounds.Y + (bounds.Height - h) / 2;
            json["dragRect"] = $"{x1},{y1},{w}x{h}";

            // 内容核验准备（FR-2 深化）：把拖拽矩形用**不透明纯色窗**填满 —— 非 layered
            // 窗进 GDI 抓屏、layered 遮罩不进（W4 实测口径）⇒ 截出的位图应整块都是
            // 定义色 #123456，中心/四角抽样可精确断言（活动屏幕内容变化不再影响判据）。
            using var patch = new System.Windows.Forms.Form
            {
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                ShowInTaskbar = false,
                BackColor = System.Drawing.Color.FromArgb(18, 52, 86),
                Bounds = new System.Drawing.Rectangle(x1, y1, w, h),
            };
            patch.Show();
            PumpFor(400);

            InjectDrag(x1, y1, x1 + w, y1 + h);
            PumpFor(800); // 截图 + GDI→BitmapSource + 剪贴板写落地

            var copiedClosed = manager.VisibleCount == 0;
            json["copiedClosed"] = copiedClosed;
            json["copiedLabel"] = copiedLabel;

            // ③ 剪贴板断言：真位图 + 尺寸与拖拽矩形一致（±1 圆整容差）
            BitmapSource? image = null;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    image = Clipboard.GetImage();
                }
            }
            catch (COMException ex)
            {
                json["clipboardError"] = ex.Message;
            }

            json["clipboardHasImage"] = image is not null;
            var sizeMatch = false;
            var contentMatch = false;
            if (image is not null)
            {
                json["clipW"] = image.PixelWidth;
                json["clipH"] = image.PixelHeight;
                sizeMatch = Math.Abs(image.PixelWidth - w) <= 1
                    && Math.Abs(image.PixelHeight - h) <= 1;

                // 内容核验：整图应为纯色 #123456 —— 中心 + 四角（内缩 3px）五点抽样，
                // 每通道 ±1 容差（DIB 往返的理论抖动）。样本实际值进 JSON 供失败诊断。
                try
                {
                    var stride = image.PixelWidth * 4;
                    var buffer = new byte[stride * image.PixelHeight];
                    image.CopyPixels(buffer, stride, 0);
                    contentMatch = SampleIsPatchColor(buffer, image.PixelWidth, image.PixelHeight);
                    var samples = new JsonArray();
                    foreach (var (sx, sy) in new[]
                             {
                                 (image.PixelWidth / 2, image.PixelHeight / 2),
                                 (3, 3), (image.PixelWidth - 4, 3),
                                 (3, image.PixelHeight - 4), (image.PixelWidth - 4, image.PixelHeight - 4),
                             })
                    {
                        var o = (sy * image.PixelWidth + sx) * 4;
                        samples.Add($"#{buffer[o + 2]:x2}{buffer[o + 1]:x2}{buffer[o]:x2}");
                    }

                    json["samples"] = samples;
                }
                catch (Exception ex)
                {
                    json["contentCheckError"] = ex.Message;
                }
            }

            json["sizeMatch"] = sizeMatch;
            json["contentMatch"] = contentMatch;

            // ④ Esc 防御性收尾（正常复制路径窗已被收；仅失败态才会走到）
            var escClosed = true;
            if (manager.AnyAlive)
            {
                InjectEscape();
                PumpFor(600);
                escClosed = manager.VisibleCount == 0;
            }

            json["escClosed"] = escClosed;

            // ⑤ 空拖拽（单击）= 取消出窗（W6 §4 契约：与 pick 的单击取色语义不串味）
            var clickCancelled = false;
            if (escClosed)
            {
                var reshown = manager.ShowAll();
                PumpFor(400);
                if (reshown > 0)
                {
                    InjectClick(x1, y1);
                    PumpFor(600);
                    clickCancelled = manager.VisibleCount == 0;
                    json["clickCancelled"] = clickCancelled;
                }

                // 兜底收窗：契约破了也别把全屏遮罩留给验收环境
                if (manager.AnyAlive)
                {
                    InjectEscape();
                    PumpFor(500);
                }
            }

            json["ok"] = t0Visible == monitors && monitors > 0
                && placementAllMatch
                && copiedClosed && image is not null && sizeMatch && contentMatch
                && escClosed
                && (json["clickCancelled"] is null || clickCancelled);
            return json;
        }
        catch (Exception ex)
        {
            json["ok"] = false;
            json["error"] = ex.Message;
            return json;
        }
        finally
        {
            // 遮罩绝不过夜：探针任何退出路径都收干净（全屏置顶窗留着 = "屏幕坏了"）
            try
            {
                manager.CloseAll();
            }
            catch
            {
                // review-guards:allow-empty-catch :: 探针收尾路径（即将退进程），连日志宿主都可能没就绪 ——
                // 有出口可去的收窗失败见 TrayApplication.Dispose（那里是 try/catch + Warn）
            }
        }
    }

    // ── 注入原语（与 OcrOverlayProbe 同款纪律；刻意自含 —— 不为复用去动 OCR 稳定面）──

    /// <summary>Bgra32 缓冲五点抽样（中心 + 四角内缩 3px），每通道与 #123456 差 ≤1 即通过。</summary>
    private static bool SampleIsPatchColor(byte[] pixels, int width, int height)
    {
        bool Match(int x, int y)
        {
            var o = (y * width + x) * 4;
            return Math.Abs(pixels[o] - 0x56) <= 1      // B
                && Math.Abs(pixels[o + 1] - 0x34) <= 1  // G
                && Math.Abs(pixels[o + 2] - 0x12) <= 1; // R
        }

        var cx = width / 2;
        var cy = height / 2;
        return Match(cx, cy) && Match(3, 3) && Match(width - 4, 3)
            && Match(3, height - 4) && Match(width - 4, height - 4);
    }

    private static void InjectDrag(int x1, int y1, int x2, int y2)
    {
        InjectMove(x1, y1);
        PumpFor(120);
        InjectButton(MOUSEEVENTF_LEFTDOWN);
        PumpFor(80);

        const int steps = 14;
        for (var i = 1; i <= steps; i++)
        {
            InjectMove(x1 + ((x2 - x1) * i / steps), y1 + ((y2 - y1) * i / steps));
            PumpFor(25);
        }

        InjectButton(MOUSEEVENTF_LEFTUP);
    }

    private static void InjectClick(int x, int y)
    {
        InjectMove(x, y);
        PumpFor(120);
        InjectButton(MOUSEEVENTF_LEFTDOWN);
        PumpFor(80);
        InjectButton(MOUSEEVENTF_LEFTUP);
    }

    private static void InjectMove(int x, int y)
    {
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var nx = (x - virtualScreen.X) * 65536 / Math.Max(1, virtualScreen.Width);
        var ny = (y - virtualScreen.Y) * 65536 / Math.Max(1, virtualScreen.Height);
        // ★ MOUSEEVENTF_VIRTUALDESK 必须带上（W4-b 实测）：绝对坐标按整个虚拟桌面物理像素映射。
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0, 0);
    }

    private static void InjectButton(uint flags) => mouse_event(flags, 0, 0, 0, 0);

    private static void InjectEscape()
    {
        keybd_event(VK_ESCAPE, 0, 0, 0);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);
    }

    private static void PumpFor(int ms)
    {
        var stopwatch = Stopwatch.StartNew();
        var dispatcher = Dispatcher.CurrentDispatcher;
        while (stopwatch.ElapsedMilliseconds < ms)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(15);
        }

        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);
}
