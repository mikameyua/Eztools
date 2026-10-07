// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json.Nodes;
using Eztools.Index;

// 编码纪律（设计方案 §5.2，索引进程侧）：
// stdout 必须 UTF-8 且**不带 BOM** —— 分帧是"一行一个 JSON"，BOM 会变成首帧的前三个字节，
// 宿主侧首帧解析直接失败，表现为"调用超时"（症状与原因距离极远；本项目两侧都吃过这个亏）。
// stderr 同 UTF-8。stdin 按 UTF-8 解码；首帧的 BOM **容忍**在 IndexRpcServer 内做（utf-8-sig 语义）。

// ── 启动参数（W3-b-4 宿主接线 / W3-c 同步层）──
//   --no-bootstrap        跳过自动建索引（acceptance 真进程冒烟 / 注入式测试用；ping/stop 照常可用）
//   --data-root <path>    安装根：Core 端点（core.json）与 .ezidx（index/ 子目录）的根。
//                         缺省 = 进程当前目录（宿主拉起时必须显式传 paths.Root）。
//   --volumes C:,D:       限定建索引的卷（逗号分隔）；缺省 = 本地全部固定卷（设计方案 q6）。
//   --exclude <rules>     排除规则（W11-a）：分号分隔的目录名，原样转交自举（IndexBootstrap
//                         用 Contracts 的同一份解析器解析；解析失败记入 lastError 并按空集继续）。
//   --path-filter <dir>   初始限定根（W11-b）：自举完成后应用一次 ApplyPathFilter（锚定失败
//                         fail-open + 诊断可见）；运行期变更走 search.start，不走重启。
//   --probe-usn C:        同步层实证探针（W3-c-1）：连提权 Core 走 queryJournal/readUsn/
//                         writeUsnClose 全链路，结果打 JSON 进 stdout 后退出（rc=0/1）。
//                         需要提权 Core 在跑（`ezt core start --elevate`）；acceptance 断言
//                         ok=true && journalId>0 && records>0。
//
// 自举**在后台线程**跑：ping/status 必须立即可用（宿主拉起后先 ping 探活，协议 §3.1 ready=false
// 就是给这个窗口用的）；自举失败不拖垮服务 —— lastError 如实记录，search.query 回 -32001。
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (Exception)
{
    // review-guards:allow-empty-catch :: 个别宿主环境（句柄重定向异常 / 权限）下设置控制台编码会抛。；编码退回默认值不该阻止服务启动：若默认值不是 UTF-8，首帧会以非 JSON 形态出现 ——；那是**可见的失败**，好过进程起不来（可见失败优于静…
    // 个别宿主环境（句柄重定向异常 / 权限）下设置控制台编码会抛。
    // 编码退回默认值不该阻止服务启动：若默认值不是 UTF-8，首帧会以非 JSON 形态出现 ——
    // 那是**可见的失败**，好过进程起不来（可见失败优于静默失败，本项目 S1~S16 一贯口径）。
}

var noBootstrap = false;
var noIndexSync = false;
string? dataRoot = null;
string? probeUsn = null;
string? excludeRules = null;
string? pathFilter = null;
IReadOnlyList<string>? volumes = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--no-bootstrap":
            noBootstrap = true;
            break;
        case "--no-index-sync":
            noIndexSync = true;
            break;
        case "--data-root" when i + 1 < args.Length:
            dataRoot = args[++i];
            break;
        case "--probe-usn" when i + 1 < args.Length:
            probeUsn = args[++i];
            break;
        case "--exclude" when i + 1 < args.Length:
            excludeRules = args[++i];
            break;
        case "--path-filter" when i + 1 < args.Length:
            pathFilter = args[++i];
            break;
        case "--volumes" when i + 1 < args.Length:
            volumes = args[++i]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            break;
        default:
            await Console.Error.WriteLineAsync($"[ezt-index] 未知参数 '{args[i]}'（支持 --no-bootstrap / --no-index-sync / --data-root <path> / --volumes C:,D: / --exclude <rules> / --path-filter <dir> / --probe-usn C:）")
                .ConfigureAwait(false);
            return 64;   // rc=64 用法错误（与 uninstall 缺 --yes 的 rc=64 同口径）
    }
}

// 探针模式：不走 RPC server（一次性运行，结果即退出）
if (probeUsn is not null)
{
    return ProbeUsn(probeUsn, dataRoot ?? Environment.CurrentDirectory);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C 走优雅退出而不是被强杀：给在途应答一个写完的机会（与宿主侧 tool.stop 同精神）。
    e.Cancel = true;
    cts.Cancel();
};

var search = new SearchService();
var server = new IndexRpcServer(Console.In, Console.Out, Console.Error, search);

if (!noBootstrap)
{
    // 后台自举：跑在**专用后台线程**上（W3-e-1）—— Priority=BelowNormal +
    // THREAD_MODE_BACKGROUND_BEGIN（磁盘空闲才抢 IO），避免首扫的一万多批 readMft 卡用户。
    // 不用 Task.Run：线程池线程是共享的，改优先级会污染池里其它任务（本进程的 RPC 服务核
    // 就跑在池上）。未观察 Task 的异常会被吞（.NET 默认）—— 这里再兜一层 Log 把意外异常
    // 写到 stderr（可见失败优于静默失败）。
    var bootstrapRoot = dataRoot ?? Environment.CurrentDirectory;
    IndexThreads.Start("ezt-index-bootstrap", () =>
    {
        try
        {
            var report = IndexBootstrap.RunAsync(search, new IndexBootstrap.Options
            {
                DataRoot = bootstrapRoot,
                Volumes = volumes,
                Diagnostics = Console.Error,
                UsnSourceFactory = vol => new CoreUsnClient(bootstrapRoot, vol),
                ExcludeRules = excludeRules,
                PathFilter = pathFilter,
            }, cts.Token).GetAwaiter().GetResult();
            Console.Error.WriteLine(
                $"[ezt-index] 自举完成：卷 {report.VolumesTotal}（热启动 {report.VolumesLoaded} / 重建 {report.VolumesBuilt} / "
                + $"失败 {report.VolumesFailed}）· {report.TotalEntries} 条 · {report.ElapsedMs} ms"
                + (report.ExcludeRuleCount > 0
                    ? $" · 排除规则 {report.ExcludeRuleCount} 条 / 摘除 {report.PrunedEntries} 条"
                    : string.Empty));

            // ── 实时 tail（W3-c-1/c-2/c-3 闭环）：对账为 Incremental 的卷起常驻泵 ──
            //（游标落在 outcome.NextUsn；applier 按卷持有——pending 配对状态是卷内概念）
            // --no-index-sync：**索引照建、变更流不消费**（Everything 的 -monitor-pause 同款语义）。
            // 先建一次快照让搜索可用，再挂上暂停闸 —— 泵照常起但立刻空转（NotePausedPoll 计数可观测），
            // 这样"暂停"是**运行时状态**，能被 status / UI / 协议看到并恢复，而不是"根本不启动"的黑盒。
            if (noIndexSync)
            {
                search.Pause.Pause();
                Console.Error.WriteLine(
                    "[ezt-index] --no-index-sync：索引已建但变更流暂停（search.status.indexing.paused=true）");
            }

            int tailsStarted = 0;
            foreach (var outcome in report.SyncOutcomes)
            {
                if (outcome.Plan != SyncPlanKind.Incremental)
                {
                    Console.Error.WriteLine(
                        $"[ezt-index] volume {outcome.Volume}: 不做实时同步（{outcome.Plan}）——{outcome.Reason}");
                    continue;
                }

                var applier = new UsnApplier();
                var tail = new JournalTail(
                    outcome.Volume, new CoreUsnClient(bootstrapRoot, outcome.Volume),
                    recs => search.ApplyUsn(outcome.Volume, recs, applier),
                    Console.Error);

                // 每卷一个专用后台线程（同步泵）：优先级 + 后台 IO 模式在整个泵生命周期内有效
                //（async 续体跳线程池会丢掉它们 —— 见 JournalTail.Run 的说明）。
                IndexThreads.Start($"ezt-index-tail-{outcome.Volume.TrimEnd(':')}", () =>
                {
                    try
                    {
                        tail.Run(outcome.NextUsn, search.Pause, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        // 泵的意外异常 = 该卷 tail 停止（索引保持快照，可见失败优于静默）
                        Console.Error.WriteLine(
                            $"[ezt-index] volume {outcome.Volume}: tail 意外终止: {ex.Message}");
                    }
                });
                tailsStarted++;
            }

            if (tailsStarted > 0)
            {
                Console.Error.WriteLine(
                    $"[ezt-index] 实时 tail 已启动 {tailsStarted} 卷"
                    + "（BelowNormal + 后台 IO 模式 / 空闲 500ms 轮询 / 120s 心跳尝试）");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ezt-index] 自举意外终止: {ex.Message}");
        }
    });
}

await server.RunAsync(cts.Token);
return 0;

// ── --probe-usn 实现（W3-c-1 同步层通路实证：经提权 Core 的 queryJournal/readUsn/心跳）──
// 输出一行 JSON 进 stdout：{ok, volume, journalId, firstUsn, nextUsn, records, heartbeatOk}
// 任何一步失败 → {ok:false, error} + rc=1（结构化失败，不静默）。
static int ProbeUsn(string volume, string dataRoot)
{
    try
    {
        using var src = new CoreUsnClient(dataRoot, volume);
        var info = src.QueryJournal();
        if (!info.IsOk)
        {
            Console.Out.WriteLine(new JsonObject
            {
                ["ok"] = false,
                ["volume"] = volume,
                ["status"] = info.Status.ToString(),
                ["win32"] = info.Win32Error,
                ["error"] = "journal 不可用（未开启/删除中）或 Core 未提权/未运行",
            }.ToJsonString());
            return 1;
        }

        // 从 FirstUsn 读一批（journal 内现存最早记录起——证明"能读到数据"，非空卷必有记录）
        var batch = src.ReadUsn(info.FirstUsn, 64 * 1024);
        bool heartbeatOk = src.TryWriteCloseRecord();

        var outJson = new JsonObject
        {
            ["ok"] = true,
            ["volume"] = volume,
            ["journalId"] = info.JournalId,
            ["firstUsn"] = info.FirstUsn,
            ["nextUsn"] = info.NextUsn,
            ["records"] = batch.Records.Count,
            ["heartbeatOk"] = heartbeatOk,
        };
        if (!heartbeatOk && src.LastHeartbeatError is { } hbErr)
        {
            outJson["heartbeatError"] = hbErr;   // S2：心跳失败原因可见，不吞
        }

        Console.Out.WriteLine(outJson.ToJsonString());
        return 0;
    }
    catch (Exception ex)
    {
        Console.Out.WriteLine(new JsonObject
        {
            ["ok"] = false,
            ["volume"] = volume,
            ["error"] = ex.Message,
        }.ToJsonString());
        return 1;
    }
}
