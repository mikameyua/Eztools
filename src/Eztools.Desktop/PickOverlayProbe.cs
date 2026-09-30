// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Eztools.Ocr;

namespace Eztools.Desktop;

/// <summary>
/// 取色遮罩端到端探针（--probe-pick-overlay，W6-c 验收面）。两层断言：
///
/// ① 确定性面（FR-7 的机器判据）：喂一张已知纯色位图（RGB 255,0,0）走
///    <see cref="PickOverlayWindow.SampleCenter"/> + <see cref="ColorFormatter"/> 同一条链，
///    断言 hex/rgb/hsl 三格式输出 = 手算精确值（±0，W6 FR-7"取色准确性"）；
/// ② 真链路面：真鼠标移动 + 单击 → 剪贴板文本 == 探针自采样同一点格式化值
///    （CopyFromScreen→LockBits→格式化→SetClipboardText 全链；屏幕静态假设与 W4-b
///    "剪贴板=屏幕真实文字"先例同口径）。
///
/// 注入原语与 <see cref="CaptureOverlayProbe"/> 同款纪律（VIRTUALDESK 必带 / WPF Dispatcher 泵）。
/// ⚠️ 副作用：真实移动鼠标、**改写系统剪贴板** —— 只能跑在无人交互的会话。
/// </summary>
internal static class PickOverlayProbe
{
    public static JsonObject Run(Action<string>? warn = null)
    {
        var manager = new PickOverlayManager(warn);
        string? copied = null;
        manager.ColorCopied += text => copied = text;

        var json = new JsonObject { ["ok"] = false };
        try
        {
            // ① 确定性面：已知纯色 → 三格式手算精确断言（FR-7，±0）
            using (var solid = new System.Drawing.Bitmap(15, 15))
            {
                using (var g = System.Drawing.Graphics.FromImage(solid))
                {
                    g.Clear(System.Drawing.Color.FromArgb(255, 0, 0));
                }

                var color = PickOverlayWindow.SampleCenter(solid);
                json["deterministic"] = new JsonObject
                {
                    ["hex"] = ColorFormatter.TryFormat(color, "hex") == "#ff0000",
                    ["rgb"] = ColorFormatter.TryFormat(color, "rgb") == "rgb(255, 0, 0)",
                    ["hsl"] = ColorFormatter.TryFormat(color, "hsl") == "hsl(0, 100%, 50%)",
                };
            }

            // ② 真链路面
            var monitors = manager.ShowAll("hex");
            PumpFor(500);
            json["monitors"] = monitors;
            json["t0Visible"] = manager.VisibleCount;

            var first = manager.EnumerableWindows().FirstOrDefault();
            if (first is null)
            {
                json["error"] = "唤出后没有任何遮罩窗";
                return json;
            }

            var bounds = first.ProbeIntendedBounds;
            var px = bounds.X + bounds.Width / 2;
            var py = bounds.Y + bounds.Height / 2;
            json["pickPoint"] = $"{px},{py}";

            InjectMove(px, py);
            PumpFor(300);
            InjectClick(px, py);

            // 单击 → 取色 → SetText → 收窗；轮询剪贴板文本落地。
            // ⚠️ 断言口径（2026-09-30 实测修正）：真链路面只断言"剪贴板 = 合法 hex 色值"。
            //   不要断言"剪贴板 == 探针自采样同点颜色"——两次采样间隔数百毫秒，采样点若是
            //   活动终端/动画区域，内容必然变化（实测 #003a71→#9f9f9f 假红）；颜色**精确性**
            //   已由确定性面（喂已知位图 ±0）钉住，这里验的是链路活性与格式正确。
            var clipboardText = string.Empty;
            var deadline = Environment.TickCount64 + 8000;
            while (Environment.TickCount64 < deadline)
            {
                PumpFor(200);
                if (manager.VisibleCount == 0 && ClipboardContainsText(out var text))
                {
                    clipboardText = text;
                    break;
                }
            }

            json["copiedClosed"] = manager.VisibleCount == 0;
            json["clipboardText"] = clipboardText;
            var clipboardFormat = System.Text.RegularExpressions.Regex.IsMatch(
                clipboardText, "^#[0-9a-f]{6}$");
            json["clipboardFormat"] = clipboardFormat;

            // Esc 防御性收尾（失败态才走到）
            var escClosed = true;
            if (manager.AnyAlive)
            {
                InjectEscape();
                PumpFor(600);
                escClosed = manager.VisibleCount == 0;
            }

            json["escClosed"] = escClosed;

            json["ok"] = monitors > 0
                && json["deterministic"]!["hex"]!.GetValue<bool>()
                && json["deterministic"]!["rgb"]!.GetValue<bool>()
                && json["deterministic"]!["hsl"]!.GetValue<bool>()
                && manager.VisibleCount == 0
                && clipboardFormat
                && escClosed;
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
            // 遮罩绝不过夜（与 CaptureOverlayProbe 同款兜底）
            try
            {
                manager.CloseAll();
            }
            catch
            {
                // 探针即将退进程，无出口可去
            }
        }
    }

    private static bool ClipboardContainsText(out string text)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                text = System.Windows.Clipboard.GetText();
                return !string.IsNullOrEmpty(text);
            }
        }
        catch (COMException)
        {
            // 剪贴板被外部程序短暂持锁：下轮轮询再试（≤3s deadline 内自愈）
        }

        text = string.Empty;
        return false;
    }

    // ── 注入原语（与 CaptureOverlayProbe 同款）──

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
