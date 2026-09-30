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

            // ②′ 真屏纯色块（FR-7 全链自动化 + 真链路精确断言的稳定化）：
            //     探针自己显示一块**不透明**纯色 WinForms 窗（非 layered ⇒ GDI 抓屏含它，
            //     而 layered 的取色遮罩不进抓屏 —— W4 实测口径），把真实单击目标从
            //     "屏幕上恰好那一点"（活动终端，内容必变 ⇒ 首版断言假红）换成
            //     "已知定义色的窗中心" ⇒ 剪贴板可精确断言 == 定义值 #123456（±1/通道）。
            using var patch = new System.Windows.Forms.Form
            {
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                ShowInTaskbar = false,
                BackColor = System.Drawing.Color.FromArgb(18, 52, 86),
                Bounds = new System.Drawing.Rectangle(px - 80, py - 60, 160, 120),
            };
            patch.Show();
            PumpFor(400);
            // 读回**实际**物理矩形。⚠️ GetWindowRect 出参是 RECT{L,T,R,B}，直接 marshal 成
            // Rectangle{X,Y,Width,Height} 时 Width 字段装的是 Right —— 必须 FromLTRB
            // （W4-b 踩过并写进 ProbeWin32Rect 注释的同一个坑，首跑又踩了一次）。
            if (!GetWindowRect(patch.Handle, out var patchRectRaw))
            {
                json["error"] = "纯色窗 GetWindowRect 失败";
                return json;
            }

            var patchRect = System.Drawing.Rectangle.FromLTRB(
                patchRectRaw.X, patchRectRaw.Y, patchRectRaw.Width, patchRectRaw.Height);
            var patchCx = patchRect.X + patchRect.Width / 2;
            var patchCy = patchRect.Y + patchRect.Height / 2;
            json["patchRect"] = $"{patchRect.X},{patchRect.Y},{patchRect.Width}x{patchRect.Height}";

            InjectMove(patchCx, patchCy);
            PumpFor(300);
            InjectClick(patchCx, patchCy);

            // 单击 → 取色 → SetText → 收窗；轮询剪贴板文本落地。
            // 断言（2026-09-30 二次修正后定案）：真链路目标 = 自绘纯色窗 ⇒ 可精确断言
            // 剪贴板 == "#123456"；同时保留 hex 格式匹配兜底诊断。
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
            // 失败诊断：提示文案区分"取色失败/剪贴板失败"与"根本没点进窗"
            foreach (var window in manager.EnumerableWindows())
            {
                json["hintAfterWait"] = window.ProbeHintText;
                break;
            }
            var clipboardFormat = System.Text.RegularExpressions.Regex.IsMatch(
                clipboardText, "^#[0-9a-f]{6}$");
            json["clipboardFormat"] = clipboardFormat;
            // 真屏断言 ±1/通道：遮罩抗点击穿透层（alpha 1/255）的理论偏差
            // （c × 254/255，round/truncate 都 ≤1）。
            var realScreenMatch = false;
            if (System.Text.RegularExpressions.Regex.Match(clipboardText, "^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$")
                is { Success: true } m)
            {
                var pr = Convert.ToInt32(m.Groups[1].Value, 16);
                var pg = Convert.ToInt32(m.Groups[2].Value, 16);
                var pb = Convert.ToInt32(m.Groups[3].Value, 16);
                realScreenMatch = Math.Abs(pr - 0x12) <= 1 && Math.Abs(pg - 0x34) <= 1 && Math.Abs(pb - 0x56) <= 1;
            }

            json["realScreenMatch"] = realScreenMatch;

            // Esc 防御性收尾（失败态才走到）：探针后台启动 ⇒ 抢前台被拒（§2.24①）——
            // 注入 Esc 前必须 AttachThreadInput 让前台 + WPF Focus 补位，否则 Esc 永远
            // 到不了 PreviewKeyDown（首跑实测：失败态遮罩关不掉 = 兜底名存实亡）。
            var escClosed = true;
            if (manager.AnyAlive)
            {
                foreach (var window in manager.EnumerableWindows())
                {
                    TrayApplication.ForceForeground(
                        new System.Windows.Interop.WindowInteropHelper(window).Handle);
                    window.Focus();
                    break;
                }

                Thread.Sleep(150);
                PumpFor(100);
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
                && realScreenMatch
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out System.Drawing.Rectangle rect);
}
