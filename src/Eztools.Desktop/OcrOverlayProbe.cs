// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Eztools.Ocr;

namespace Eztools.Desktop;

/// <summary>
/// OCR 遮罩<b>唤出生命周期</b>探针（--probe-ocr-overlay，W4-b 自动化面）。
///
/// 遮罩窗全屏置顶 ⇒ **真鼠标注入必然落在遮罩上** —— 这与搜索窗"热键不可自动化"不同，
/// 鼠标链路可以端到端真跑。为了让拖拽/取词目标**不依赖屏幕上恰好有字**，探针先做
/// <b>自校准</b>（截主屏 → OCR → 拿到当前真实文字的位置），后续所有注入都瞄准校准点：
///   ① 唤出：每显示器一扇、全部可见（FR-1）+ 逐窗摆放对账（placement，R2）；
///   ② 真拖拽 → copied（剪贴板 = 屏幕真实文字）或 emptyRetry（合法分支）；
///   ③ Esc 真键 → 全部退出；复现性：再唤出 → Esc；
///   ④ 单击取词：确定性面（FindWordAt 变换链自洽）+ 真点击校准词中心 → copied；
///   ⑤ 连续模式复现：复制后立刻（Dispatcher 延迟）重唤 → 第二次拖拽仍能复制；
///   ⑥ 手工项替身：M2 空结果提示 / M5 右键·Alt+F4 / M6 剪贴板持锁竞争。
///
/// ⚠️ 副作用声明：本探针会真实移动鼠标、改写剪贴板 —— 只能跑在无人交互的会话里。
/// </summary>
internal static class OcrOverlayProbe
{
    public static JsonObject Run()
    {
        var warnings = new JsonArray();
        var manager = new OcrOverlayManager(msg => warnings.Add(msg));
        string? copiedText = null;
        manager.TextCopied += text => copiedText = text;

        var json = new JsonObject { ["ok"] = false, ["warnings"] = warnings };
        try
        {
            // ⓪ 自校准：每块屏分别截图 OCR，拿到"此刻哪里真的有字"（物理像素词框，含屏归属）
            var hits = CalibrateTextRegions(warnings);
            json["calibratedWords"] = hits.Count;
            var primaryIndex = Array.FindIndex(
                System.Windows.Forms.Screen.AllScreens,
                s => s.Primary);
            var primaryHits = hits.Where(h => h.ScreenIndex == primaryIndex).ToList();
            var externalHits = hits.Where(h => h.ScreenIndex != primaryIndex).ToList();
            json["calibratedPrimaryWords"] = primaryHits.Count;
            json["calibratedExternalWords"] = externalHits.Count;

            // ① 唤出
            var monitors = manager.ShowAll();
            PumpFor(500);
            var t0Visible = manager.VisibleCount;
            json["monitors"] = monitors;
            json["t0Visible"] = t0Visible;
            json["engineReady"] = true;

            // R2 机制证据：每扇窗按 GetDpiForWindow 实测的缩放比 + 摆放对账
            var scales = new JsonArray();
            var placement = new JsonArray();
            var placementAllMatch = true;
            foreach (var window in manager.EnumerableWindows())
            {
                scales.Add(window.ProbeScale);
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

            json["dpiScales"] = scales;
            json["placement"] = placement;
            json["placementAllMatch"] = placementAllMatch;

            // ② 真拖拽（瞄准主屏校准到的文字区）
            var drag = DoRealDrag(manager, PickDragRect(primaryHits, 0));
            json["drag"] = new JsonObject
            {
                ["branch"] = drag.Branch,
                ["hintAfterDrag"] = drag.HintAfterDrag,
            };

            // ③ 空选区残留 → Esc 收
            var escClosed = true;
            if (manager.AnyAlive)
            {
                InjectEscape();
                PumpFor(600);
                escClosed = manager.VisibleCount == 0;
                json["hintBeforeEsc"] = drag.HintAfterDrag;
            }

            json["escClosed"] = escClosed;

            // ④ 复现性：再唤出 → Esc
            var reshown = 0;
            if (escClosed)
            {
                reshown = manager.ShowAll();
                PumpFor(400);
                InjectEscape();
                PumpFor(600);
            }

            json["reshown"] = reshown;
            json["reshowEscClosed"] = manager.VisibleCount == 0 && reshown > 0;

            // ⑤ 单击取词（FR-5）：确定性面 + 真点击主屏校准词中心
            var clickWord = new JsonObject();
            clickWord["deterministic"] = RunFindWordAtSelfTest(warnings);
            var realClick = DoRealClick(manager, primaryHits);
            clickWord["realClick"] = new JsonObject
            {
                ["branch"] = realClick.Branch,
                ["clipboardText"] = realClick.ClipboardText,
                ["hintAfterClick"] = realClick.HintAfterClick,
                ["attempts"] = realClick.Attempts,
            };
            json["clickWord"] = clickWord;

            // ⑥ 连续模式复现（--ocr-show 的时序）：复制（窗全收）→ Dispatcher 延迟重唤 → 再拖一次
            var cont = new JsonObject();
            manager.ShowAll();
            PumpFor(500);
            var d1 = PickDragRect(hits, 0);
            InjectDrag(d1.X1, d1.Y1, d1.X2, d1.Y2);
            SettleForHint(manager);
            cont["firstCopy"] = manager.VisibleCount == 0;
            // ★ 复制后重唤必须走 Dispatcher 延迟（与 --ocr-show 修复同款）：同步 ShowAll 在
            //   旧窗 Close 事件栈里会撞 WPF 关闭期限制 → 消息泵卡死（实测整进程挂起，rc=3 前科）。
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => manager.ShowAll()));
            var waitForReshow = Stopwatch.StartNew();
            while (manager.VisibleCount == 0 && waitForReshow.ElapsedMilliseconds < 5_000)
            {
                PumpFor(100);
            }

            cont["reshowVisible"] = manager.VisibleCount;
            var d2 = PickDragRect(primaryHits, 1);
            InjectDrag(d2.X1, d2.Y1, d2.X2, d2.Y2);
            SettleForHint(manager);
            cont["secondCopy"] = manager.VisibleCount == 0;
            json["continuous"] = cont;

            // ⑥′ 外接屏端到端（M1 的机器可读判据，monitors>1 才跑）：
            // 在**外接屏**校准词上真拖拽 + 真点击 —— 生产"副屏截图坐标"链路的直接证据。
            // 若剪贴板抄出的是外接屏上的真实文字 ⇒ 坐标链路正确；抄错/抄空 ⇒ 坐标 bug。
            var externalPhase = new JsonObject { ["skipped"] = true };
            if (monitors > 1 && externalHits.Count > 0)
            {
                externalPhase = new JsonObject { ["skipped"] = false };
                manager.ShowAll();
                PumpFor(500);
                var ed = PickDragRect(externalHits, 0);
                InjectDrag(ed.X1, ed.Y1, ed.X2, ed.Y2);
                SettleForHint(manager);
                var externalDragCopied = manager.VisibleCount == 0;
                var externalText = ReadClipboardSafe();

                externalPhase["dragCopied"] = externalDragCopied;
                externalPhase["dragText"] = externalText;
                externalPhase["hint"] = SettleForHint(manager).Hint;

                var externalClickCopied = false;
                string? externalClickText = null;
                if (!externalDragCopied)
                {
                    // 拖拽没成（比如选到空白）→ Esc 收掉再试点击取词
                    InjectEscape();
                    PumpFor(500);
                }

                if (externalHits.Count > 1)
                {
                    manager.ShowAll();
                    PumpFor(500);
                    var click = DoRealClick(manager, externalHits.Skip(1).ToList());
                    externalClickCopied = click.Branch == "copied";
                    externalClickText = click.ClipboardText;
                    externalPhase["clickCopied"] = externalClickCopied;
                    externalPhase["clickText"] = externalClickText;
                }
                else
                {
                    externalPhase["clickSkipped"] = true;
                }

                externalPhase["copied"] = externalDragCopied || externalClickCopied;
            }

            json["externalScreen"] = externalPhase;

            // ⑦ 手工清单项的自动化替身（M2/M4/M5/M6 —— 能注入的全注入，M1 真多屏仍需人工）
            var sim = RunManualSimulations(manager, hits);
            json["manualSimulation"] = sim;

            // 剪贴板采样（copied 分支的判据）
            string? clipboard = null;
            try
            {
                clipboard = System.Windows.Clipboard.GetText();
            }
            catch (Exception ex)
            {
                warnings.Add($"剪贴板读取失败：{ex.Message}");
            }

            json["clipboardChars"] = clipboard?.Length ?? 0;
            json["copiedText"] = copiedText;

            var dragBranch = drag.Branch;
            var dragOk = dragBranch switch
            {
                "copied" => escClosed && (clipboard?.Length ?? 0) > 0,
                "emptyRetry" => escClosed,
                _ => false,
            };

            var clickOk = realClick.Branch switch
            {
                "copied" => realClick.ClipboardText.Length > 0
                    && !realClick.ClipboardText.Contains('\r')
                    && !realClick.ClipboardText.Contains('\n'), // 单词不该带换行
                "miss" => true, // 未点到文字是合法分支（点击处恰好无文字）
                _ => false,
            };

            json["ok"] = monitors > 0
                && t0Visible == monitors
                && placementAllMatch
                && dragOk
                && escClosed
                && reshown > 0
                && manager.VisibleCount == 0
                && clickOk
                && clickWord["deterministic"]?["matched"]?.GetValue<bool>() == true
                && cont["firstCopy"]?.GetValue<bool>() == true
                && cont["reshowVisible"]?.GetValue<int>() > 0
                && cont["secondCopy"]?.GetValue<bool>() == true
                && sim["m2EmptyResultHint"]?.GetValue<bool>() == true
                && sim["m5RightClickClosed"]?.GetValue<bool>() == true
                && sim["m5AltF4Closed"]?.GetValue<bool>() == true
                && sim["m6ClipboardContention"]?.GetValue<bool>() == true;
        }
        catch (Exception ex)
        {
            json["ok"] = false;
            json["error"] = ex.Message;
        }
        finally
        {
            manager.CloseAll();
        }

        return json;
    }

    // ── 自校准 ──

    private sealed record TextHit(Drawing.Rectangle Rect, string Text, int ScreenIndex, bool IsPrimary);

    /// <summary>
    /// 对**每一块屏**分别截图 → OCR → 返回当前真实存在的**词级**物理像素矩形（虚拟桌面坐标系）。
    /// 后续所有注入都瞄准这些命中点 —— 探针不再依赖"屏幕上恰好有字"的脆弱假设，
    /// 也让外接屏的截图链路可以被端到端自动化（M1 的机器可读判据）。
    /// </summary>
    private static List<TextHit> CalibrateTextRegions(JsonArray warnings)
    {
        var engine = WindowsOcrEngine.TryCreate()
            ?? throw new InvalidOperationException(OcrLanguages.InstallHint);

        var hits = new List<TextHit>();
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (var si = 0; si < screens.Length; si++)
        {
            var bounds = screens[si].Bounds;
            using var bitmap = ScreenCapture.GrabRegion(new Drawing.Rectangle(
                bounds.X, bounds.Y, bounds.Width, bounds.Height));
            var result = engine.RecognizeAsync(bitmap).GetAwaiter().GetResult();

            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    if (word.Text.Trim().Length < 3)
                    {
                        continue; // 短词多为图标字形/噪声，注入命中率低（首版曾让 hits[0] 踩雷）
                    }

                    // 词框在预处理坐标系（×Scale + Offset）→ 换回源位图（物理像素）→ 加本屏原点偏移
                    int SrcX(int prepared) => (int)Math.Round((prepared - result.OffsetX) / result.ScaleX);
                    int SrcY(int prepared) => (int)Math.Round((prepared - result.OffsetY) / result.ScaleY);
                    var x1 = bounds.X + SrcX(word.Bounds.X);
                    var y1 = bounds.Y + SrcY(word.Bounds.Y);
                    var x2 = bounds.X + SrcX(word.Bounds.X + word.Bounds.Width);
                    var y2 = bounds.Y + SrcY(word.Bounds.Y + word.Bounds.Height);
                    hits.Add(new TextHit(
                        Drawing.Rectangle.FromLTRB(x1, y1, x2, y2),
                        word.Text, si, screens[si].Primary));
                }
            }
        }

        // 长词优先：它们 OCR 最稳、区域最有把握，作为注入目标的第一梯队
        hits.Sort((a, b) => b.Text.Length.CompareTo(a.Text.Length));

        if (hits.Count == 0)
        {
            warnings.Add("自校准没有找到任何文字（全部屏幕 OCR 为空）—— 注入目标退化为固定坐标");
        }

        return hits;
    }

    /// <summary>选第 index 个校准命中（循环取用），以词中心外扩到**最小 300×80**的拖拽矩形
    /// （词框本身只有几十像素，引擎对这么小的图会返回空 —— 首版 ±4px 外扩的教训）；
    /// 无校准数据时退回主屏中央横带。</summary>
    private static (int X1, int Y1, int X2, int Y2) PickDragRect(List<TextHit> hits, int index)
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("无主显示器");

        if (hits.Count > 0)
        {
            var hit = hits[index % hits.Count];
            var cx = hit.Rect.X + (hit.Rect.Width / 2);
            var cy = hit.Rect.Y + (hit.Rect.Height / 2);
            var w = Math.Max(300, hit.Rect.Width + 8);
            var h = Math.Max(80, hit.Rect.Height + 8);
            var x1 = Math.Max(primary.X, cx - (w / 2));
            var y1 = Math.Max(primary.Y, cy - (h / 2));
            var x2 = Math.Min(primary.X + primary.Width, x1 + w);
            var y2 = Math.Min(primary.Y + primary.Height, y1 + h);
            return (x1, y1, x2, y2);
        }

        return (
            primary.X + (int)(primary.Width * 0.25),
            primary.Y + (int)(primary.Height * 0.45),
            primary.X + (int)(primary.Width * 0.60),
            primary.Y + (int)(primary.Height * 0.58));
    }

    // ── 拖拽 ──

    private sealed record DragResult(string Branch, string HintAfterDrag);

    private static DragResult DoRealDrag(OcrOverlayManager manager, (int X1, int Y1, int X2, int Y2) rect)
    {
        InjectDrag(rect.X1, rect.Y1, rect.X2, rect.Y2);
        var settle = SettleForHint(manager);
        var branch = manager.VisibleCount == 0 ? "copied" : "emptyRetry";
        return new DragResult(branch, settle.Hint);
    }

    // ── 单击取词 ──

    private sealed record ClickResult(string Branch, string ClipboardText, string HintAfterClick, JsonArray Attempts);

    private static ClickResult DoRealClick(OcrOverlayManager manager, List<TextHit> hits)
    {
        var before = ReadClipboardSafe();

        var monitors = manager.ShowAll();
        if (monitors == 0)
        {
            return new ClickResult("show-failed", string.Empty, string.Empty, []);
        }

        PumpFor(500);

        // 候选点 = 校准词中心（真正有字的地方，命中确定性高）；无校准数据退回主屏中央。
        var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("无主显示器");
        var candidates = new List<(int X, int Y)>();
        foreach (var hit in hits.Take(3))
        {
            candidates.Add((hit.Rect.X + (hit.Rect.Width / 2), hit.Rect.Y + (hit.Rect.Height / 2)));
        }

        if (candidates.Count == 0)
        {
            candidates.Add((primary.X + (primary.Width / 2), primary.Y + (primary.Height / 2)));
        }

        var attempts = new JsonArray();
        var hint = string.Empty;
        string after = string.Empty;
        var copied = false;
        foreach (var (cx, cy) in candidates)
        {
            if (manager.VisibleCount == 0)
            {
                break; // 上一击已成功收窗
            }

            InjectMove(cx, cy);
            PumpFor(150);
            InjectButton(MOUSEEVENTF_LEFTDOWN);
            PumpFor(80);
            InjectButton(MOUSEEVENTF_LEFTUP);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < 6_000)
            {
                PumpFor(200);
                hint = manager.VisibleCount > 0 ? ProbeHintOfAny(manager) : string.Empty;
                var settled = manager.VisibleCount == 0
                    || hint.Contains("未点到文字", StringComparison.Ordinal)
                    || hint.Contains("识别失败", StringComparison.Ordinal)
                    || hint.Contains("剪贴板写入失败", StringComparison.Ordinal);
                if (settled)
                {
                    break;
                }
            }

            after = ReadClipboardSafe();
            // 判据用"窗自动关闭"而非"剪贴板非空"：拖拽阶段已经改写过剪贴板，"非空"不是新信号的证据。
            // 单击路径只有 Finish（已写剪贴板）会收窗，没有取消出口 ⇒ 窗关了 = 取词成功。
            copied = manager.VisibleCount == 0;
            attempts.Add(new JsonObject
            {
                ["point"] = $"{cx},{cy}",
                ["copied"] = copied,
                ["hint"] = hint,
                ["wordDebug"] = manager.EnumerableWindows().FirstOrDefault()?.ProbeLastWordDebug ?? string.Empty,
            });
            if (copied)
            {
                break;
            }
        }

        return new ClickResult(
            copied ? "copied" : "miss",
            copied ? after : string.Empty,
            hint,
            attempts);
    }

    private static string ReadClipboardSafe()
    {
        try
        {
            return System.Windows.Clipboard.GetText();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// FindWordAt 自洽测试：样图识别 → 取含 "OCR" 的词 → 其包围盒中心换回**源位图**坐标反查
    /// ⇒ 必须命中同一个词。钉死"预处理坐标 ↔ 源坐标"这条变换链（首版曾在这里双重变换，测试的错）。
    /// </summary>
    private static JsonObject RunFindWordAtSelfTest(JsonArray warnings)
    {
        using var sample = SampleImage.RenderText("EZTOOLS OCR 2026");
        var engine = WindowsOcrEngine.TryCreate();
        if (engine is null)
        {
            warnings.Add("自洽测试跳过：引擎创建失败");
            return new JsonObject { ["matched"] = false, ["reason"] = "engine-create-failed" };
        }

        var result = engine.RecognizeAsync(sample).GetAwaiter().GetResult();
        OcrWord? target = null;
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                if (word.Text.Contains("OCR", StringComparison.OrdinalIgnoreCase))
                {
                    target = word;
                    break;
                }
            }
        }

        if (target is null)
        {
            return new JsonObject { ["matched"] = false, ["reason"] = "sample-word-not-recognized", ["sampleText"] = result.Text };
        }

        var preparedCenterX = target.Bounds.X + (target.Bounds.Width / 2.0);
        var preparedCenterY = target.Bounds.Y + (target.Bounds.Height / 2.0);
        var sourceCenter = new Drawing.Point(
            (int)Math.Round((preparedCenterX - result.OffsetX) / result.ScaleX),
            (int)Math.Round((preparedCenterY - result.OffsetY) / result.ScaleY));
        var hit = WindowsOcrEngine.FindWordAt(result, sourceCenter);
        return new JsonObject
        {
            ["matched"] = hit == target.Text,
            ["expected"] = target.Text,
            ["hit"] = hit,
            ["sampleChars"] = result.Text.Length,
        };
    }

    // ── 手工清单替身（M2/M4/M5/M6）──

    /// <summary>
    /// 把 <c>W4-手工验收清单.md</c> 里能注入的项跑一遍（M1 真多屏坐标除外——物理条件无法自动化）：
    /// <list type="bullet">
    /// <item>m2EmptyResultHint —— 喂**纯色空白位图**走后处理链：期望提示「未识别到文字」且窗保持活动（FR-4）。
    ///       首版拖屏幕边角找空白，实测屏幕上没有可控空白区（窗口铺满）⇒ 改为确定性喂图。</item>
    /// <item>m5RightClickClosed —— 右键真注入 ⇒ 全部遮罩退出（FR-1）。</item>
    /// <item>m5AltF4Closed —— Alt+F4 真键注入 ⇒ 全部遮罩退出（FR-1）。</item>
    /// <item>m6ClipboardContention —— 后台线程**持锁剪贴板 4s** 期间做拖拽：重试后成功或提示
    ///     「剪贴板写入失败」都合法（R7），唯一红线是崩溃/静默。</item>
    /// </list>
    /// </summary>
    private static JsonObject RunManualSimulations(OcrOverlayManager manager, List<TextHit> hits)
    {
        var json = new JsonObject();

        // ── M2/M4：空结果 → 提示 + 保持活动（确定性：喂纯色空白位图）──
        var m2 = false;
        string m2Hint = string.Empty;
        manager.ShowAll();
        PumpFor(500);
        var firstWindow = manager.EnumerableWindows().FirstOrDefault();
        if (firstWindow is not null)
        {
            _ = firstWindow.ProbeFeedBlankAsync();
            var settle2 = SettleForHint(manager);
            m2Hint = settle2.Hint;
            m2 = m2Hint.Contains("未识别到文字", StringComparison.Ordinal) && manager.VisibleCount > 0;
        }

        json["m2EmptyResultHint"] = m2;
        json["m2Hint"] = m2Hint;

        // M2 收尾：若窗还开着（空结果保持活动），Esc 收掉
        if (manager.AnyAlive)
        {
            InjectEscape();
            PumpFor(500);
        }

        // ── M5a：右键 ⇒ 全部退出 ──
        manager.ShowAll();
        PumpFor(500);
        InjectButton(MOUSEEVENTF_RIGHTDOWN);
        PumpFor(60);
        InjectButton(MOUSEEVENTF_RIGHTUP);
        PumpFor(600);
        var rightClickClosed = manager.VisibleCount == 0;
        json["m5RightClickClosed"] = rightClickClosed;

        // ── M5b：Alt+F4 ⇒ 全部退出 ──
        manager.ShowAll();
        PumpFor(500);
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_F4, 0, 0, 0);
        keybd_event(VK_F4, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        PumpFor(600);
        var altF4Closed = manager.VisibleCount == 0;
        json["m5AltF4Closed"] = altF4Closed;

        // ── M6：剪贴板竞争 ──
        // 后台线程持锁 4s；期间拖拽文字带 → CopyRegionAsync 的 3 次重试（60ms 间隔）全落在锁窗内
        // ⇒ 期望「剪贴板写入失败」提示 + 窗保持活动；若锁释放时机赶上重试间隙 ⇒ 成功复制也是合法分支。
        // 红线只有一条：崩溃或静默（窗消失却没写字、或既不提示也不退出）。
        manager.ShowAll();
        PumpFor(500);
        var lockThread = new Thread(() =>
        {
            if (OpenClipboard(nint.Zero))
            {
                Thread.Sleep(4_000);
                CloseClipboard();
            }
        });
        lockThread.SetApartmentState(ApartmentState.STA);
        lockThread.Start();
        Thread.Sleep(200); // 确保锁已就位再拖

        var dragRect = PickDragRect(hits, 0);
        InjectDrag(dragRect.X1, dragRect.Y1, dragRect.X2, dragRect.Y2);
        lockThread.Join(6_000);

        var settle6 = SettleForHint(manager);
        var m6Branch = manager.VisibleCount == 0
            ? "copied-after-retry"
            : settle6.Hint.Contains("剪贴板写入失败", StringComparison.Ordinal) ? "blocked-with-hint" : "unexpected";
        if (manager.AnyAlive)
        {
            InjectEscape();
            PumpFor(500);
        }

        var m6Closed = manager.VisibleCount == 0;
        json["m6ClipboardContention"] = m6Branch != "unexpected" && m6Closed;
        json["m6Branch"] = m6Branch;
        json["m6Hint"] = settle6.Hint;
        json["m6ClosedAfterEsc"] = m6Closed;

        return json;
    }

    // ── 注入原语 ──

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

    private static void InjectMove(int x, int y)
    {
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var nx = (x - virtualScreen.X) * 65536 / Math.Max(1, virtualScreen.Width);
        var ny = (y - virtualScreen.Y) * 65536 / Math.Max(1, virtualScreen.Height);
        // ★ MOUSEEVENTF_VIRTUALDESK 必须带上：没有它，绝对坐标被 OS 按**主屏**映射
        //   （实测数据：注入 (996,347) 落到 (569,346) = X÷主屏缩放 1.75，Y 因主屏高度
        //   恰好等于虚拟桌面高度而侥幸正确）—— 带上后才按整个虚拟桌面物理坐标映射。
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0, 0);
    }

    private static void InjectButton(uint flags) => mouse_event(flags, 0, 0, 0, 0);

    private static void InjectEscape()
    {
        keybd_event(VK_ESCAPE, 0, 0, 0);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);
    }

    private static string ProbeHintOfAny(OcrOverlayManager manager)
    {
        foreach (var window in manager.EnumerableWindows())
        {
            return window.ProbeHintText;
        }

        return string.Empty;
    }

    /// <summary>等识别落地（窗收起或提示进入终态），返回最后的提示文案。</summary>
    private static (bool Settled, string Hint) SettleForHint(OcrOverlayManager manager)
    {
        var stopwatch = Stopwatch.StartNew();
        var hint = string.Empty;
        while (stopwatch.ElapsedMilliseconds < 8_000)
        {
            PumpFor(200);
            hint = manager.VisibleCount > 0 ? ProbeHintOfAny(manager) : string.Empty;
            var settled = manager.VisibleCount == 0
                || hint.Contains("未识别到文字", StringComparison.Ordinal)
                || hint.Contains("未点到文字", StringComparison.Ordinal)
                || hint.Contains("识别失败", StringComparison.Ordinal)
                || hint.Contains("剪贴板写入失败", StringComparison.Ordinal);
            if (settled)
            {
                return (true, hint);
            }
        }

        return (false, hint);
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

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const byte VK_ESCAPE = 0x1B;
    private const byte VK_MENU = 0x12;
    private const byte VK_F4 = 0x73;
    private const uint KEYEVENTF_KEYUP = 0x0002;
}
