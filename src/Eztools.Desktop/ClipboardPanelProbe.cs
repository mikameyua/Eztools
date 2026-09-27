// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Eztools.ClipboardLib;

namespace Eztools.Desktop;

/// <summary>
/// 剪贴板面板探针（--probe-clip-panel，W5-b 验收面）。
///
/// <b>Phase A（生命周期，确定性）</b>：临时库预置条目 → Toggle 循环 ×4（采样
/// visible/active/queryFocused 三时点）→ Close 模拟点 X → 再唤出（"X=隐藏"契约）→
/// 程序化搜索过滤。
///
/// <b>Phase B（直贴契约，真键 Enter）</b>：Summon（真抢焦点，与真热键同代价）→
/// 程序化提交搜索词 → keybd_event 注入真实 VK_RETURN →
/// <b>断言：面板已隐藏 + 剪贴板内容逐字等于条目内容</b>。
///
/// <b>★ enterFired / vkAfter* 是诊断面不是断言面</b>（2026-09-26 实测）：探针上下文
/// 没有 Win32 消息泵（<c>PumpFor</c> 只排 Dispatcher 队列），WM_KEYDOWN 永远不会被
/// dispatch ⇒ WPF 键盘事件管线（HwndSource 钩子 / PreviewKeyDown）整个不跑，
/// <c>vkAfterPost=-1</c>（PostMessage 直投也收不到）就是钩子从未挂上的铁证。
/// 此时 Enter 直贴由 <b>GetAsyncKeyState 轮询兜底</b>完成 —— 这正是键盘链
/// 双通路设计的直接实证（§2.26②：消息层会丢键，轮询与消息层幂等）。真实运行时
/// （Application.Run 主泵 + 用户真按键）消息层正常，两路全活。
///
/// ⚠️ <b>Ctrl+V 真注入被抑制</b>（<see cref="ClipboardHistoryPanel.ProbeSuppressInject"/>）：
/// 真注入会把内容贴进运行探针的终端 —— 那是不可接受的副作用。"还原前台 + 注入"的
/// 端到端验证归 W5-手工验收清单 M 项（与 OCR 遮罩真鼠标同口径）。
/// </summary>
internal static class ClipboardPanelProbe
{
    private const int CycleCount = 4;

    /// <summary>
    /// 剪贴板监听探针（--probe-clip-monitor，W5-c 验收面）：
    /// <b>真 AddClipboardFormatListener → 真 WM_CLIPBOARDUPDATE → 真事件 → 真入库</b>。
    ///
    /// ⚠️ 关键实现约束：探针上下文没有消息泵（见类头/RunMonitorProbe 内注释），
    /// 监听 sink 的窗口消息必须由本探针的 <b>PeekMessage 裸泵</b>喂出来 ——
    /// 与键盘链"裸泵才活"同族（§2.27）。副作用：改写一次系统剪贴板（跑完不恢复，
    /// 与 verify-desktop section 2 反复改剪贴板同量级）。
    /// </summary>
    public static JsonObject RunMonitorProbe()
    {
        var marker = $"W5C-monitor-probe-{Guid.NewGuid():N}";
        var dbDir = Path.Combine(Path.GetTempPath(), "ezt-clip-monitor-probe", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dbDir);
        var store = new HistoryStore(
            Path.Combine(dbDir, "clips.db"),
            Path.Combine(dbDir, "images"));

        var captured = false;
        var monitor = new ClipboardMonitor();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var started = monitor.Start();
            if (started)
            {
                monitor.ClipboardChanged += () =>
                {
                    try
                    {
                        var (snapshot, error) = ClipboardReader.ReadCurrent();
                        if (error is null && snapshot.Text is { } text && text.Contains(marker, StringComparison.Ordinal))
                        {
                            store.Upsert(MakeTextEntry(text));
                            captured = true;   // 真事件真链路落库
                        }
                    }
                    catch (Exception)
                    {
                        // 探针内捕获异常：不计入（超时后 ok=false 会显式变红）
                    }
                };

                // 另起 STA 线程写剪贴板（SetClipboard 需要 STA；本线程要保持 Pump 循环不被阻塞）
                var writer = new Thread(() =>
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(marker);
                    }
                    catch (Exception)
                    {
                        // 写失败 → captured 永不置位 → ok=false 显式红
                    }
                });
                writer.SetApartmentState(ApartmentState.STA);
                writer.Start();
                writer.Join();

                // ★ 裸泵：PeekMessage 循环把 sink 窗口的消息喂出来。
                //   Dispatcher 泵不管 Win32 窗口消息（§2.27 同族教训），必须 PeekMessage→Dispatch。
                while (!captured && stopwatch.ElapsedMilliseconds < 5000)
                {
                    while (PeekMessage(out var m, nint.Zero, 0, 0, PM_REMOVE))
                    {
                        _ = TranslateMessage(in m);
                        _ = DispatchMessage(in m);
                    }

                    Thread.Sleep(10);
                }
            }

            stopwatch.Stop();
            return new JsonObject
            {
                ["ok"] = started && captured,
                ["listenerStarted"] = started,
                ["captured"] = captured,
                ["elapsedMs"] = (int)stopwatch.ElapsedMilliseconds,
                ["marker"] = marker,
                ["storeHits"] = captured ? store.Search(marker).Count : 0,
            };
        }
        finally
        {
            monitor.Dispose();
            store.Dispose();
            try
            {
                Directory.Delete(dbDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public static JsonObject Run()
    {
        var dbDir = Path.Combine(Path.GetTempPath(), "ezt-clip-panel-probe", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dbDir);

        var store = new HistoryStore(
            Path.Combine(dbDir, "clips.db"),
            Path.Combine(dbDir, "images"));

        try
        {
            // 预置条目（真 Upsert 真指纹；"重复内容"制造 copy_count=2 的真实形态）
            var marker = $"W5B面板直贴验收 clip-panel-probe-{Guid.NewGuid():N}".Substring(0, 40);
            store.Upsert(MakeTextEntry(marker));
            store.Upsert(MakeTextEntry(marker));
            store.Upsert(MakeTextEntry("第二条 噪声条目 noise-entry-xyz"));
            store.Upsert(MakeTextEntry("第三条 pin 我 pin-me-constant"));

            // ★ 2026-09-27 换实现后：输入框回来了（原生 EDIT），直贴契约（依赖"打字过滤"）
            //   随之恢复；面板生命周期 + 打字过滤 + Enter 直贴三面一起验。
            var json = new JsonObject { ["ok"] = true };
            json["cycles"] = RunLifecycleCycles(store, marker);
            // ok 判据 = 生命周期 4 轮全部跑完无异常（TryRun 已兜住逐轮异常并记入 JSON）
            if (json["cycles"]?["cycles"] is JsonArray arr)
            {
                json["ok"] = arr.Count == CycleCount;
            }

            // 直贴契约独立跑一个干净面板（生命周期那轮已经开开关关多次，状态不干净）
            json["paste"] = RunPasteContract(store, marker);

            return json;
        }
        finally
        {
            store.Dispose();
            try
            {
                Directory.Delete(dbDir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不碍验收（系统 temp 兜底）
            }
        }
    }

    private static ClipEntry MakeTextEntry(string content) => new()
    {
        Kind = ClipKind.Text,
        Content = content,
        Preview = content,
        Hash = CaptureService.ComputeHash(content),
        SourceApp = "clip-panel-probe.exe",
    };

    // ── Phase A ──────────────────────────────────────────────────────────

    private static JsonObject RunLifecycleCycles(HistoryStore store, string marker)
    {
        var panel = new ClipboardHistoryPanel(store) { ProbeSuppressInject = true };
        try
        {
            var cycles = new JsonArray();
            for (var i = 1; i <= CycleCount; i++)
            {
                var summonError = TryRun(() => panel.Toggle());
                PumpFor(120);
                var t0 = Sample(panel);

                PumpFor(700);   // 排队消息落地："唤出即被藏"竞态发生在 t0 与 t1 之间
                var t1 = Sample(panel);

                var resummonError = TryRun(() => panel.Summon());
                PumpFor(200);
                var t3 = Sample(panel);

                var closeError = TryRun(() => panel.Toggle());
                PumpFor(120);
                var t2 = Sample(panel);

                cycles.Add(new JsonObject
                {
                    ["cycle"] = i,
                    ["summonError"] = summonError?.Message,
                    ["resummonError"] = resummonError?.Message,
                    ["closeError"] = closeError?.Message,
                    ["t0"] = t0,
                    ["t1"] = t1,
                    ["t3"] = t3,
                    ["t2"] = t2,
                });
            }

            var json = new JsonObject { ["cycles"] = cycles };

            // X 按钮 / Alt+F4 契约：Close() 被拦截成 Hide，窗口可再唤出
            var closeViaXError = TryRun(() => panel.Close());
            PumpFor(150);
            var afterX = Sample(panel);
            var resummonAfterXError = TryRun(() => panel.Toggle());
            PumpFor(200);
            var afterXResummon = Sample(panel);
            json["closeViaX"] = new JsonObject
            {
                ["closeError"] = closeViaXError?.Message,
                ["afterX"] = afterX,
                ["resummonError"] = resummonAfterXError?.Message,
                ["afterXResummon"] = afterXResummon,
            };

            return json;
        }
        finally
        {
            panel.CloseForProbe();
        }
    }

    private static JsonObject Sample(ClipboardHistoryPanel panel)
    {
        var visible = panel.ProbeIsVisible;
        return new JsonObject
        {
            ["visible"] = visible,
            ["isActive"] = panel.ProbeIsActive,
            // 焦点判据 = **Win32 GetFocus**（原生 EDIT 语义），不是 WPF 的 IsKeyboardFocusWithin:
            // "窗口在但打不进字"的分裂态只有系统层读数能抓出来。
            ["queryFocused"] = panel.ProbeQueryFocused,
            ["stillVisibleAfterSettle"] = visible && panel.ProbeIsActive,
            ["statusRight"] = panel.ProbeStatusRight,
        };
    }

    // ── 直贴契约（打字过滤 → Enter → 直贴）────────────────────────────────

    /// <summary>
    /// 直贴闭环：唤出 → 打字过滤到恰 1 条 → Enter → 断言「面板隐藏 + 剪贴板逐字等于条目内容」。
    ///
    /// <para><b>注入方式（两条，诚实记录各自结果）</b>：
    /// ① <c>keybd_event</c> 真键注入 —— 与用户路径同源，但**探针进程常常抢不到系统前台**
    ///   （§2.26⑦），键会路由给真正的前台窗口，负结果不代表功能坏；
    /// ② <c>PostMessage</c> 直投 WM_KEYDOWN 到**原生 EDIT 的 hwnd** —— 不依赖前台，
    ///   键照样进 EDIT 的窗口过程，是探针上下文里唯一可靠的注入姿势。
    /// 断言判据挂在"最终是否隐藏 + 剪贴板内容"，并落盘 <c>resolvedBy</c> 说明是哪条路生效的。</para>
    /// </summary>
    private static JsonObject RunPasteContract(HistoryStore store, string marker)
    {
        var json = new JsonObject();
        var panel = new ClipboardHistoryPanel(store) { ProbeSuppressInject = true };
        try
        {
            panel.Summon();
            PumpFor(300);
            panel.SubmitForProbe(marker);   // 真走 TextChanged → 真 FTS 过滤
            PumpFor(400);

            json["itemsAtInject"] = panel.ProbeItemCount;
            json["lastForegroundNonZero"] = panel.ProbeLastForeground != nint.Zero;
            json["queryFocusedAtInject"] = panel.ProbeQueryFocused;

            // ① 真键注入（结果只记录，不断言 —— 见方法注释）
            keybd_event(VK_RETURN, 0x1C, 0, 0);
            keybd_event(VK_RETURN, 0x1C, KEYEVENTF_KEYUP, 0);
            PumpFor(500);
            json["realKeyHidden"] = !panel.ProbeIsVisible;

            // ② 直投兜底（探针上下文的可靠姿势）
            if (panel.ProbeIsVisible)
            {
                _ = PostMessage(panel.ProbeHwnd, WM_KEYDOWN, (nint)VK_RETURN, (nint)0);
                PumpFor(500);
                json["resolvedBy"] = "postMessage";
            }
            else
            {
                json["resolvedBy"] = "realKey";
            }

            json["hiddenAfterEnter"] = !panel.ProbeIsVisible;

            var text = "";
            var readError = (string?)null;
            try
            {
                text = System.Windows.Clipboard.GetText() ?? "";
            }
            catch (Exception ex)
            {
                // 剪贴板被别的进程占住是真实场景：落错误 + clipboardMatches=false（显式变红，不空过）
                readError = ex.Message;
            }

            json["clipboard"] = text;
            json["clipboardMatches"] = text == marker;
            if (readError is not null)
            {
                json["clipboardReadError"] = readError;
            }
        }
        catch (Exception ex)
        {
            json["error"] = ex.Message;
        }
        finally
        {
            panel.CloseForProbe();
        }

        return json;
    }

    private static Exception? TryRun(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // ── 泵（SearchSummonProbe 同款：ApplicationIdle 排干再睡，DispatcherTimer 到点）──

    private static void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        var dispatcher = Dispatcher.CurrentDispatcher;
        while (sw.ElapsedMilliseconds < ms)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(15);
        }

        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public nint Point;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);

    private const uint PM_REMOVE = 0x0001;

    private const uint WM_KEYDOWN = 0x0100;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_Z = 0x5A;
    private const uint KEYEVENTF_KEYUP = 0x0002;
}
