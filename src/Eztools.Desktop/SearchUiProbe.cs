// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Eztools.Host.Launcher;
using Eztools.Host.Search;

namespace Eztools.Desktop;

/// <summary>
/// UI 探针的假传输（W3-d-2）：**确定性**返回 N 条合成命中，零进程。为什么是假传输而不是真
/// ezt-index —— 真索引在无提权 Core 的环境里 query 回 -32001（不 ready），拿不到"200 条
/// 结果"这个要被断言的状态；而"列表拿到 200 条后怎么表现"恰好是纯 UI 性质，与数据来源无关。
///
/// 合成数据刻意覆盖三个易错点：
///   ① <b>中文多段高亮</b>（不连续区间 ⇒ 分段着色必须逐段应用，不能只取第一段）；
///   ② <b>total ≠ hits</b>（计数显示必须两个数都显示且都对）；
///   ③ <b>全路径</b>（打开/定位动作必须拿到索引回传的全路径，不是文件名）。
/// </summary>
internal sealed class ProbeSearchUiTransport : ISearchIndexTransport
{
    /// <summary>本轮返回的命中条数（虚拟化断言的分母）。</summary>
    public const int HitCount = 200;

    /// <summary>全库命中总数（≠ HitCount ⇒ 计数显示断言"显示 200 / 共 12345 条"）。</summary>
    public const int TotalCount = 12345;

    /// <summary>首条命中的名字：两段不连续高亮（"季报" + "年度"）。</summary>
    public const string FirstHitName = "季报2026年度.pdf";

    /// <summary>首条命中的全路径。</summary>
    public const string FirstHitPath = @"C:\Users\ishe\Documents\季报2026年度.pdf";

    /// <summary>首条命中的高亮区间（UTF-16 code unit）：[0,2) = "季报"，[6,2) = "年度"（不连续）。</summary>
    public static readonly (int Start, int Len)[] FirstHitHighlights = [(0, 2), (6, 2)];

    /// <summary>status 探针夹具：2 个已索引卷 + 1 个被跳过的 exFAT 卷（W3-e-2 的 UI 可见性）。</summary>
    public static readonly string[] IndexedVolumes = ["C:", "D:"];

    /// <summary>被跳过的卷（含原因枚举 + 文案）—— UI 必须把它们显示出来并带上原因。</summary>
    public static readonly (string Volume, string Reason, string ReasonText)[] SkippedVolumes =
        [("E:", "UnsupportedFileSystem", "文件系统不支持（仅 NTFS 有 MFT/USN：exFAT）")];

    /// <summary>
    /// **索引失败**的卷夹具（缺口②）。与 <see cref="SkippedVolumes"/> 成对 ——
    /// "没试过"与"试了没成"必须在状态行里**分开显示**，合并成"有 2 个卷没索引"就退回了缺口本身。
    /// 码 5 = 无提权 Core 时最真实的形态（E1a 实测就落在这里）。
    /// </summary>
    public static readonly (string Volume, string Kind, int Code, string ReasonText, string Message)[]
        FailedVolumes =
        [
            ("F:", "AccessDenied", 5,
                "访问被拒绝（需要管理员权限）",
                "访问被拒绝（需要管理员权限）：读不到卷序列号（未就绪或非本地卷）"),
        ];

    /// <summary>检测到的卷总数夹具 = 已索引 + 跳过 + 失败（三档守恒的 UI 侧口径）。</summary>
    public static int DetectedVolumes =>
        IndexedVolumes.Length + SkippedVolumes.Length + FailedVolumes.Length;

    /// <summary>status 回传的总条目数（卷清单行文案里的"N 项"）。</summary>
    public const int StatusTotalFiles = 2468;

    /// <summary>status 被请求过几次（断言"卷清单行真的走了协议"而不是硬编码）。</summary>
    public int StatusCalls { get; private set; }

    /// <summary>当前暂停态（夹具可写：翻成 true 后 `RefreshVolumeSummary` 应把徽标刷出来）。</summary>
    public bool Paused { get; set; }

    /// <summary>`search.pauseIndexing` 收到几次（断言 UI 真的走了协议，而不是只改本地变量）。</summary>
    public int PauseCalls { get; private set; }

    /// <summary>`search.resumeIndexing` 收到几次。</summary>
    public int ResumeCalls { get; private set; }

    public Task<JsonObject> RoundTripAsync(JsonObject request, CancellationToken ct = default)
    {
        var method = request["method"]?.GetValue<string>();
        if (method == "search.status")
        {
            StatusCalls++;
            return Task.FromResult(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]!.DeepClone(),
                ["result"] = StatusResult(),
            });
        }

        // 暂停 / 恢复（缺口①）：**返回实际状态**而不是"请求已收到" —— 契约与真实现一致。
        if (method is "search.pauseIndexing" or "search.resumeIndexing")
        {
            Paused = method == "search.pauseIndexing";
            if (Paused)
            {
                PauseCalls++;
            }
            else
            {
                ResumeCalls++;
            }

            return Task.FromResult(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]!.DeepClone(),
                ["result"] = new JsonObject { ["paused"] = Paused },
            });
        }

        // 协议契约：epoch 必填且原样回传（索引侧不做任何比较，协议 §2.2）。
        // 这里不编造 epoch —— 从请求里取，回什么等于请求什么，配对闸才过。
        var epoch = request["params"]!["epoch"]!.GetValue<long>();

        var hits = new JsonArray();
        for (var i = 0; i < HitCount; i++)
        {
            hits.Add(i == 0 ? FirstHit() : SyntheticHit(i));
        }

        return Task.FromResult(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = new JsonObject
            {
                ["epoch"] = epoch,
                ["total"] = TotalCount,
                ["elapsedMs"] = 7,
                ["hits"] = hits,
            },
        });
    }

    private JsonObject StatusResult()
    {
        var volumes = new JsonArray();
        foreach (var v in IndexedVolumes)
        {
            volumes.Add(new JsonObject { ["volume"] = v, ["entries"] = 1234 });
        }

        var skipped = SkippedArray();
        var failed = FailedArray();

        return new JsonObject
        {
            ["ready"] = true,
            ["totalFiles"] = StatusTotalFiles,
            ["indexing"] = new JsonObject { ["active"] = false, ["paused"] = Paused },
            ["volumes"] = volumes,
            ["skippedVolumes"] = skipped,
            ["failedVolumes"] = failed,
            ["detectedVolumes"] = DetectedVolumes,
            ["lastError"] = null,
        };
    }

    /// <summary>跳过夹具 → status JSON 形状（探针与 status 结果**同一份**，避免两处漂移）。</summary>
    public static JsonArray SkippedArray()
    {
        var arr = new JsonArray();
        foreach (var (volume, reason, reasonText) in SkippedVolumes)
        {
            arr.Add(new JsonObject
            {
                ["volume"] = volume,
                ["reason"] = reason,
                ["reasonText"] = reasonText,
            });
        }

        return arr;
    }

    /// <summary>失败夹具 → status JSON 形状（缺口②，与 <see cref="SkippedArray"/> 同款）。</summary>
    public static JsonArray FailedArray()
    {
        var arr = new JsonArray();
        foreach (var (volume, kind, code, reasonText, message) in FailedVolumes)
        {
            arr.Add(new JsonObject
            {
                ["volume"] = volume,
                ["kind"] = kind,
                ["code"] = code,
                ["reasonText"] = reasonText,
                ["message"] = message,
            });
        }

        return arr;
    }

    private static JsonObject FirstHit() => new()
    {
        ["name"] = FirstHitName,
        ["dir"] = false,
        ["len"] = FirstHitName.Length,
        ["frn"] = 100L,
        ["path"] = FirstHitPath,
        ["highlights"] = Highlights(FirstHitHighlights),
    };

    private static JsonObject SyntheticHit(int i)
    {
        var name = $"report-{i:D4}.txt";
        var dir = i % 17 == 0;
        return new JsonObject
        {
            ["name"] = name,
            ["dir"] = dir,
            ["len"] = name.Length,
            ["frn"] = (long)(1000 + i),
            ["path"] = $@"C:\Users\ishe\Desktop\{name}",
            ["highlights"] = Highlights([(0, 6)]),   // 前缀 "report"
        };
    }

    private static JsonArray Highlights((int Start, int Len)[] ranges)
    {
        var arr = new JsonArray();
        foreach (var (start, len) in ranges)
        {
            arr.Add(new JsonObject { ["start"] = start, ["len"] = len });
        }

        return arr;
    }
}

/// <summary>
/// UI 探针运行器（W3-d-2/3 验收面）：在**不弹窗、不抢焦点**的前提下，把"200 条数据进列表后
/// 的表现"与"三个动作的进程参数"落成结构化 JSON。
///
/// 为什么 <c>Measure/Arrange</c> 而不是 <c>Show()</c>：布局/虚拟化只要求控件有**有限尺寸**，
/// 不要求窗口可见。Show() 会抢焦点（自动化环境里的人正干活时被弹窗打断），且需要消息循环；
/// Measure/Arrange 零副作用且确定性更好。真按键/真滚动/真失焦仍在自动化之外（与 W3-d-1 同口径）。
/// </summary>
internal static class SearchUiProbe
{
    /// <summary>渲染落地条件轮询上限（节流 150ms + 渲染，1.5s 足够；超时即如实报当前态）。</summary>
    private const int PumpTimeoutMs = 1500;

    public static JsonObject Run()
    {
        var transport = new ProbeSearchUiTransport();
        // ★ W7-a 探针装配纪律（设计方案 §5）：显式传"仅 files"集合 —— 探针断言**一行不改**，
        //   只换装配参数。真机上开始菜单可能含中文名应用，放进 apps provider 会让
        //   `itemsCount == 200` 这类断言被环境命中打破（假红，与"墙钟断言配置感知"同族）。
        var client = new SearchIndexClient(transport);
        var window = new SearchWindow(client, LauncherProviderSet.FilesOnly(client))
        {
            ProbeSuppressDrift = true,   // 假传输卷清单 vs 真实盘符 → 环境依赖提示；drift 由纯函数直测断言
        };

        try
        {
            // 真窗口、屏幕外、不抢焦点（虚拟化需要呈现源建立的真实视口）
            window.ShowForProbe();

            // 真链路：TextChanged → QueryRouter（节流 + 单在途 + 代次闸）→ OnRender → 渲染。
            // 同时等卷清单行落地（走真 search.status 协议，见 ProbeSearchUiTransport）。
            window.SubmitForProbe("报");
            PumpUntil(
                () => window.ProbeItemCount == ProbeSearchUiTransport.HitCount
                    && window.ProbeVolumesLine.Length > 0,
                PumpTimeoutMs);

            var snap = window.SnapshotUi();
            var json = Snapshot(snap, transport.StatusCalls);

            // ── 暂停徽标（缺口①「暂停必须可见」）───────────────────────────────
            // 快照一：未暂停（上面那份）—— 断言**不含**"已暂停"（反向夹具，防恒真）。
            json["volumesLineUnpaused"] = window.ProbeVolumesLine;
            json["clickableUnpaused"] = window.ProbeVolumesLineClickable;

            // 快照二：status 回 paused=true ⇒ 徽标必须出现在**行首**（行尾会被省略号截断）
            transport.Paused = true;
            window.RefreshVolumeSummary();
            PumpUntil(() => window.ProbeVolumesLine.StartsWith("索引已暂停", StringComparison.Ordinal),
                PumpTimeoutMs);
            json["volumesLinePaused"] = window.ProbeVolumesLine;
            json["clickablePaused"] = window.ProbeVolumesLineClickable;

            // 往返：走真协议拿**服务端实际状态**（不是本地变量），再把回显落 statusText
            _ = window.SetPauseAsync(true);
            PumpUntil(() => transport.PauseCalls == 1 && window.ProbeStatusText.Contains("暂停"),
                PumpTimeoutMs);
            _ = window.SetPauseAsync(false);
            PumpUntil(() => transport.ResumeCalls == 1 && window.ProbeStatusText.Contains("恢复"),
                PumpTimeoutMs);

            json["pauseCalls"] = transport.PauseCalls;
            json["resumeCalls"] = transport.ResumeCalls;
            json["pausedAfterResume"] = transport.Paused;
            json["pauseText"] = window.ProbeStatusText;
            return json;
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    private static JsonObject Snapshot(SearchUiSnapshot snap, int statusCalls)
    {
        var segments = new JsonArray();
        foreach (var (text, bold) in snap.FirstSegments)
        {
            segments.Add(new JsonObject { ["text"] = text, ["bold"] = bold });
        }

        // ── 卷清单（W3-e-2 "跳过必须可见"）────────────────────────────────────
        // 桌面读的是 status 回传的 volumes/skippedVolumes，**不重算**；这里把 UI 实际显示
        // 的文本与纯函数算子（DescribeVolumes）的渲染各落一份 —— 前者证明"真显示出来了"，
        // 后者证明文案规则（有跳过必带原因）本身正确。
        var skipped = ProbeSearchUiTransport.SkippedArray();
        var failed = ProbeSearchUiTransport.FailedArray();

        // 反向夹具：全 NTFS（无跳过、无失败）时文案**不含**"跳过"/"失败"字样
        //（防把空集合渲染成噪声 —— 恒真断言同族）。
        var noSkipText = SearchWindow.DescribeVolumes(
            ProbeSearchUiTransport.IndexedVolumes.Length,
            ProbeSearchUiTransport.StatusTotalFiles,
            Array.Empty<SkippedVolumeDto>(),
            Array.Empty<FailedVolumeDto>());

        // ── 动作构造器断言面（W3-d-3）：用**正斜杠**样例证明 open 原样透传、reveal 归一为反斜杠 ──
        var sample = new SearchHitDto("x.txt", false, "C:/Users/ishe/Desktop/x.txt", 1, []);
        var open = SearchWindow.BuildOpenStartInfo(sample);
        var reveal = SearchWindow.BuildRevealStartInfo(sample);
        var gone = new SearchHitDto("gone.txt", false, @"C:\no\such\gone.txt", 2, []);

        // 只看**路径部分**是否残留正斜杠 —— `/select` 开关本身带正斜杠，不能整体 Contains('/')。
        var quoted = reveal.Arguments.Split('"');
        var revealPath = quoted.Length > 1 ? quoted[1] : reveal.Arguments;

        return new JsonObject
        {
            // 卷漂移提示纯函数直测（热插拔已知限制的可见性补丁）：新增 / 移除 / 一致 / 未就绪 四例
            ["driftProbe"] = new JsonObject
            {
                ["added"] = SearchWindow.DetectVolumeDrift(true, ["C:", "D:"], ["C:", "D:", "E:"]),
                ["removed"] = SearchWindow.DetectVolumeDrift(true, ["C:", "D:", "Z:"], ["C:", "D:"]),
                ["none"] = SearchWindow.DetectVolumeDrift(true, ["C:", "D:"], ["C:", "D:"]),
                ["notReady"] = SearchWindow.DetectVolumeDrift(false, [], ["C:"]),
            },
            ["itemsCount"] = snap.ItemsCount,            ["realizedContainers"] = snap.RealizedContainers,
            ["layoutMs"] = snap.LayoutMs,
            ["buildMs"] = snap.BuildMs,
            ["renderMs"] = snap.RenderMs,
            ["statusRight"] = snap.StatusRight,
            ["statusText"] = snap.StatusText,
            ["firstRendered"] = snap.FirstRendered,
            ["firstName"] = snap.FirstName,
            ["firstPath"] = snap.FirstPath,
            ["firstSegments"] = segments,
            ["itemsPanelType"] = snap.ItemsPanelType,
            ["usedVirtualizingPanel"] = snap.UsedVirtualizingPanel,
            ["canContentScroll"] = snap.CanContentScroll,
            ["isVirtualizing"] = snap.IsVirtualizing,
            ["viewportHeight"] = snap.ViewportHeight,
            ["extentHeight"] = snap.ExtentHeight,
            ["volumesLine"] = snap.VolumesLine,                  // UI 里真显示的那行文本
            ["statusCalls"] = statusCalls,                       // 卷清单真的走了 status 协议
            ["skippedVolumes"] = skipped,                        // 夹具原样回显（断言面自证）
            ["failedVolumes"] = failed,                          // 同上（缺口②）
            ["detectedVolumes"] = ProbeSearchUiTransport.DetectedVolumes,
            ["noSkipText"] = noSkipText,                         // 无跳过/失败 ⇒ 不含这两个词
            ["expectVolumesLine"] = SearchWindow.DescribeVolumes(
                ProbeSearchUiTransport.IndexedVolumes.Length,
                ProbeSearchUiTransport.StatusTotalFiles,
                [.. ProbeSearchUiTransport.SkippedVolumes.Select(s =>
                    new SkippedVolumeDto(s.Volume, s.Reason, s.ReasonText))],
                [.. ProbeSearchUiTransport.FailedVolumes.Select(f =>
                    new FailedVolumeDto(f.Volume, f.Kind, f.Code, f.ReasonText, f.Message))]),
            ["actions"] = new JsonObject
            {
                ["openTarget"] = open.FileName,
                ["openShellExecute"] = open.UseShellExecute,
                ["revealExe"] = reveal.FileName,
                ["revealArgs"] = reveal.Arguments,
                ["revealPath"] = revealPath,
                ["revealPathHasForwardSlash"] = revealPath.Contains('/'),
                ["deletedMessage"] = SearchWindow.DescribeOpenFailure(
                    gone, new FileNotFoundException("模拟：文件已被删除")),
            },
        };
    }

    /// <summary>
    /// 泵 Dispatcher 直到条件成立或超时。每轮 <c>Invoke(ApplicationIdle)</c> 会先处理所有更高
    /// 优先级的工作（含 DataBind/Render/Loaded ⇒ 布局与渲染），再 Sleep 让 DispatcherTimer 到点。
    /// </summary>
    private static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var dispatcher = Dispatcher.CurrentDispatcher;
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(15);
        }

        // 最后一次满泵：确保渲染/布局彻底落地（快照才读到终态）
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
