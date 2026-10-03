// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Eztools.Host.Launcher;
using Eztools.Host.Search;

namespace Eztools.Desktop;

/// <summary>
/// 搜索窗<b>唤出生命周期</b>探针（--probe-search-summon，C2/G1 手工三 bug 的自动化面）。
///
/// <b>Phase A（假传输，确定性）—— 唤出循环</b>：
/// 真按键不可自动化（键盘钩子 → WM_HOTKEY 那一环，G1 明文），但热键到达后的
/// <c>Toggle → Summon → Show/ForceForeground/Activate/Focus</c> 全链是**本线程同步代码**，
/// 可以直接驱动。每轮采样三个时点：
///   t0 = Summon 返回后即刻 · t1 = 消息泵跑 ~700ms 后 · t2 = Toggle（二次热键=收起）后。
/// 抓两类会静默坏掉的性质：
///   ① <b>唤出即被藏</b>：ForceForeground 抖出的瞬态 Deactivated 若是**排队**送达的
///     （Summon 返回、消息泵恢复后才处理），"失焦自动隐藏"会在 <c>_summoning</c> 复位后
///     把刚唤出的窗口藏掉 —— 表现正是用户实测的"第一次能开，以后热键唤不出"。
///     判据：t0.visible=True 而 t1.visible=False（无人点击却消失）。
///   ② <b>窗口在但打不进字</b>（前台锁定的残余形态）：判据 t1.queryFocused=False。
///
/// <b>Phase B（真链路，--probe-search-live 选通）—— 大小写折叠 + "lei 无结果"诊断</b>：
/// 真 ezt-index（与托盘同一 <see cref="SearchIndexProcess"/> 形态）→ 真 <c>QueryRouter</c>
/// （节流/单在途/过期闸全真）→ SubmitForProbe 输入 "lei" 与 "LEI"（CapsLock 态的等价物）
/// → 各自等到渲染落地，落盘 total/状态行/卷清单行。判据：两查询 total 相等且 &gt; 0。
/// 状态行/卷清单行原文一并落盘 —— "lei 无结果"时它就是诊断证据（-32001 准备中？
/// 过期闸吞掉 ⇒ 状态行停在"输入以搜索"？）。
/// </summary>
internal static class SearchSummonProbe
{
    /// <summary>唤出循环轮数（覆盖"第一次"与"以后"两种形态——问题恰恰只在后者出现）。</summary>
    private const int CycleCount = 4;

    public static JsonObject Run(bool liveChain, SearchIndexClient? liveClient, int waitReadyMs)
    {
        var json = new JsonObject { ["ok"] = true };
        json["cycles"] = RunLifecycleCycles();
        if (liveChain)
        {
            json["live"] = liveClient is null
                ? new JsonObject { ["ok"] = false, ["error"] = "liveClient 未创建（宿主路径缺失）" }
                : RunLiveQuery(liveClient, waitReadyMs);
        }

        return json;
    }

    // ── Phase A：唤出循环（假传输）─────────────────────────────────────────

    private static JsonObject RunLifecycleCycles()
    {
        var transport = new ProbeSearchUiTransport();
        // ★ W7-a 探针装配纪律：显式传"仅 files"集合（真机上 apps 命中会打破 itemsCount 之类断言）
        var probeClient = new SearchIndexClient(transport);
        var window = new SearchWindow(probeClient, LauncherProviderSet.FilesOnly(probeClient));
        try
        {
            var cycles = new JsonArray();
            for (var i = 1; i <= CycleCount; i++)
            {
                // 从隐藏态热键唤出（Toggle 与托盘入口同一条代码路径）
                var summonError = TryRun(() => window.Toggle());
                PumpFor(120);
                var t0 = Sample(window);

                // 消息泵跑一段：排队的 WM_ACTIVATE / Deactivated 在这里落地 ——
                // "唤出即被藏"的竞态就发生在 t0 与 t1 之间。
                PumpFor(700);
                var t1 = Sample(window);

                // 窗口开着再唤一次（热键二段语义外的一条真实路径：用户没关窗又按了热键）
                var resummonError = TryRun(() => window.Summon());
                PumpFor(200);
                var t3 = Sample(window);

                // 再按热键（可见且激活 ⇒ 收起）
                var closeError = TryRun(() => window.Toggle());
                PumpFor(120);
                var t2 = Sample(window);

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

            // ── X 按钮 / Alt+F4 模拟（2026-09-25 实测主坑）──────────────────────
            // 用户点标题栏 X = 真 Close ⇒ 窗口销毁 ⇒ 之后每次热键 Show 抛异常被托盘吞掉
            // ⇒ "第一次能唤出，以后热键全灭"。修复契约：Close() 被 Closing 拦截成 Hide，
            //    窗口仍可再唤出。直接对窗口 Close() 再 Toggle，两个采样钉死该契约。
            var closeViaXError = TryRun(() => window.Close());
            PumpFor(150);
            var afterX = Sample(window);
            var resummonAfterXError = TryRun(() => window.Toggle());
            PumpFor(200);
            var afterXResummon = Sample(window);
            json["closeViaX"] = new JsonObject
            {
                ["closeError"] = closeViaXError?.Message,
                ["afterX"] = afterX,                    // visible=False（已隐藏）但窗口未销毁
                ["resummonError"] = resummonAfterXError?.Message,
                ["afterXResummon"] = afterXResummon,    // 必须 visible=True + queryFocused=True
            };

            // ── Enter 真键注入（hwnd 钩子的端到端验证）────────────────────────
            // Summon 抢真实焦点（与用户热键唤出同路径）→ keybd_event 注入真实 VK_RETURN
            // → hwnd 钩子应命中（ProbeEnterFired）。列表先清空（提交无匹配词），
            // 使"打开"动作无副作用（无结果可开）。注入失败/焦点未抢到都会在此显形。
            var enterPrepared = TryRun(() =>
            {
                window.Summon();
                window.SubmitForProbe("zzzz-no-match-zzzz");
            });
            PumpFor(400);
            var focusAtInject = Sample(window);   // 注入瞬间的焦点采样：False = Enter 发去了别处
            var isForeground = window.ProbeIsForeground;   // WPF 激活 ≠ 系统前台 —— 诚实读数

            // ── 注入对照矩阵：字母 vs Enter（scan=0 / scan=0x1C）—— 系统专吃谁一测便知 ──
            // ProbeLastHookVk 记录钩子收到的最后一个 VK：每步注入后读数即知该键到没到 hwnd。
            keybd_event(VK_A, 0, 0, 0);
            keybd_event(VK_A, 0, KEYEVENTF_KEYUP, 0);
            PumpFor(300);
            var vkAfterLetter = window.ProbeLastHookVk;      // 期望 0x41（字母到 hwnd）

            keybd_event(VK_RETURN, 0, 0, 0);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, 0);
            PumpFor(300);
            var vkAfterEnterScan0 = window.ProbeLastHookVk;  // 期望 0x0D

            keybd_event(VK_RETURN, 0x1C, 0, 0);              // 带真实扫描码再试
            keybd_event(VK_RETURN, 0x1C, KEYEVENTF_KEYUP, 0);
            PumpFor(300);
            var vkAfterEnterScan1C = window.ProbeLastHookVk; // 期望 0x0D

            // 直投对照：PostMessage 直发 WM_KEYDOWN 到窗口 hwnd（跳过系统注入层）——
            // 收到 = 钩子工作正常、keybd_event 注入的系统投递层有问题；收不到 = 钩子没挂上。
            _ = PostMessage(window.ProbeHwnd, 0x0100, (nint)0x0D, (nint)0);
            PumpFor(300);
            var vkAfterPost = window.ProbeLastHookVk;        // 期望 0x0D

            // Enter 端到端：真注入（有匹配结果）→ Enter → 状态行应走到"打开失败"
            //（假传输首条是假文件，Process.Start 失败 = **链路走到了打开动作**）。
            window.SubmitForProbe("报");
            PumpFor(600);
            keybd_event(VK_RETURN, 0x1C, 0, 0);
            keybd_event(VK_RETURN, 0x1C, KEYEVENTF_KEYUP, 0);
            PumpFor(600);
            var statusAfterEnter = window.ProbeStatusText;

            json["enterKey"] = new JsonObject
            {
                ["prepareError"] = enterPrepared?.Message,
                ["focusAtInject"] = focusAtInject,
                ["isForegroundAtInject"] = isForeground,   // false = 抢前台失败，注入键发去了真前台
                ["vkAfterLetter"] = vkAfterLetter,           // 0x41 = 字母链路通
                ["vkAfterEnterScan0"] = vkAfterEnterScan0,
                ["vkAfterEnterScan1C"] = vkAfterEnterScan1C,
                ["vkAfterPost"] = vkAfterPost,
                ["statusAfterEnter"] = statusAfterEnter,
                ["walkedToOpen"] = (statusAfterEnter ?? "").Contains("打开失败", StringComparison.Ordinal),
                ["fired"] = window.ProbeEnterFired,
            };

            return json;
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    private static JsonObject Sample(SearchWindow window) => new()
    {
        ["visible"] = window.ProbeIsVisible,
        ["active"] = window.ProbeIsActive,
        ["queryFocused"] = window.ProbeQueryFocused,
    };

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

    // ── Phase B：真链路查询（lei / LEI）────────────────────────────────────

    private static JsonObject RunLiveQuery(SearchIndexClient client, int waitReadyMs)
    {
        var json = new JsonObject();

        // 等自举（与 --probe-search 同款有界等待；探针专用根没建过索引时这里能看到真实耗时）
        if (waitReadyMs <= 0)
        {
            waitReadyMs = 30_000;
        }

        var sw = Stopwatch.StartNew();
        var ready = false;
        while (true)
        {
            try
            {
                ready = client.StatusAsync().GetAwaiter().GetResult().Ready;
                if (ready)
                {
                    break;
                }
            }
            catch (SearchIndexException)
            {
                // review-guards:allow-empty-catch :: 自举中端点可能未就绪 —— 继续等，落最终态即可
            }

            if (sw.ElapsedMilliseconds >= waitReadyMs)
            {
                break;
            }

            Thread.Sleep(100);
        }

        json["ready"] = ready;
        json["readyWaitMs"] = (int)sw.ElapsedMilliseconds;

        var window = new SearchWindow(client, LauncherProviderSet.FilesOnly(client));
        try
        {
            window.ShowForProbe();   // 屏幕外 + 不激活（不抢焦点，查询路径不受影响）

            json["lei"] = SubmitAndSettle(window, "lei");
            json["leiUpper"] = SubmitAndSettle(window, "LEI");

            // 判据：引擎大小写折叠（查询侧与名字侧同入 Fold）⇒ 两个查询的计数必须完全一致。
            // 只在两次查询都真出计数时才判（statusRight = "显示 X / 共 Y 条"）；
            // 未就绪/出错时如实落盘状态行原文 —— 那本身就是要诊断的证据。
            static string? S(JsonObject? o, string key) => o?[key]?.GetValue<string>();
            var lei = json["lei"] as JsonObject;
            var leiUpper = json["leiUpper"] as JsonObject;
            var a = S(lei, "statusRight");
            var b = S(leiUpper, "statusRight");
            if (a?.StartsWith("显示", StringComparison.Ordinal) == true
                && b?.StartsWith("显示", StringComparison.Ordinal) == true)
            {
                json["caseFoldEqual"] = a == b
                    && (lei?["items"]?.GetValue<int>() ?? -1) == (leiUpper?["items"]?.GetValue<int>() ?? -1);
                json["caseFoldNote"] = $"lei vs LEI ⇒ {a}";
            }
        }
        finally
        {
            window.CloseForProbe();
        }

        return json;
    }

    /// <summary>提交一次输入并等渲染落地（状态行出现终态：计数 / 没有匹配 / 准备中 / 出错）。</summary>
    private static JsonObject SubmitAndSettle(SearchWindow window, string text)
    {
        window.SubmitForProbe(text);

        var sw = Stopwatch.StartNew();
        var last = SampleLive(window);
        while (sw.ElapsedMilliseconds < 15_000)
        {
            PumpFor(250);
            var now = SampleLive(window);
            var settled = now == last
                && (now.StatusRight.Length > 0
                    || now.StatusText.Contains("没有匹配", StringComparison.Ordinal)
                    || now.StatusText.Contains("准备", StringComparison.Ordinal)
                    || now.StatusText.Contains("出错", StringComparison.Ordinal));
            last = now;
            if (settled)
            {
                break;
            }
        }

        return new JsonObject
        {
            ["text"] = text,
            ["items"] = last.Items,
            ["statusRight"] = last.StatusRight,
            ["statusText"] = last.StatusText,
            ["volumesLine"] = window.ProbeVolumesLine,
        };
    }

    private static (int Items, string StatusRight, string StatusText) SampleLive(SearchWindow window) =>
        (window.ProbeItemCount, window.ProbeStatusRight, window.ProbeStatusText);

    // ── 泵（与 SearchUiProbe 同款：ApplicationIdle 排干再睡，让 DispatcherTimer 到点）──

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

    private const byte VK_RETURN = 0x0D;
    private const byte VK_A = 0x41;
    private const uint KEYEVENTF_KEYUP = 0x0002;
}
