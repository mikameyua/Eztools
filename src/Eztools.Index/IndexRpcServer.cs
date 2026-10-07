// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Index;

/// <summary>
/// 索引进程的 stdio JSON-RPC 服务核（W3-a-1 骨架）。
///
/// <b>为什么做成"可注入读写器"而不是直接吃 Console</b>：
/// 协议纪律（BOM 容忍 / 分帧 / 错误码）必须在 <c>ezt selftest</c> 里就能验 ——
/// 只有真进程才能验的话，这些断言会退化成"跑了 acceptance 才知道"
/// （断言挂在重环境分支内 = S9 同族的零覆盖）。Program.cs 负责把 Console.In/Out/Error 接进来；
/// 测试侧用 StringReader / StringWriter，零进程、零 IO。
///
/// <b>编码与分帧纪律（设计方案 §5.2 三条硬约束的索引进程侧）</b>：
/// <list type="bullet">
/// <item>输出：一行一个 JSON、只写 <c>\n</c>、逐帧 flush；编码由调用方决定（Program 用 UTF-8 无 BOM）。</item>
/// <item>输入：首帧**容忍 BOM**（宿主侧承诺"不写 BOM"，工具侧按 utf-8-sig 语义容忍 —— 两侧都要做）。</item>
/// <item>调试：只写 stderr（<c>_diagnostics</c>）。stdout 上出现非 JSON 帧即协议污染。</item>
/// </list>
/// </summary>
public sealed class IndexRpcServer
{
    /// <summary>协议方法：心跳。W3-a-1 阶段唯一必需的方法（W3-a-1 验收①）。</summary>
    public const string MethodPing = "ping";

    /// <summary>
    /// 协议方法：优雅退出。宿主进程管理层（W3-a-1 ③"接入宿主进程管理"）的停止入口 ——
    /// 与既有工具协议同名同语义（收到即应答 ok 并退出，等不满宽限期）。
    /// </summary>
    public const string MethodStop = "tool.stop";

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter? _diagnostics;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly SearchService? _search;
    private int _stopRequested;

    /// <summary>
    /// <paramref name="search"/> 为 null ⇒ search.* 方法按 MethodNotFound 拒绝
    ///（与 W3-a-1 骨架行为一致；协议层测试注入 service 后才可用 —— 方法存在性与
    /// 服务可用性是两件事，前者由常量决定，后者由装配决定）。
    /// </summary>
    public IndexRpcServer(
        TextReader input, TextWriter output, TextWriter? diagnostics = null, SearchService? search = null)
    {
        _input = input;
        _output = output;
        _diagnostics = diagnostics;
        _search = search;
    }

    /// <summary>收到 <see cref="MethodStop"/> 后为真（RunAsync 返回后保持真）。</summary>
    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    /// <summary>
    /// 服务主循环：读到 EOF（stdin 关闭 = 宿主要我们退）或 <see cref="MethodStop"/> 返回。
    /// <b>每一条带 id 的请求都必须得到应答</b> —— 本项目硬约束：不回 = 对端永久挂起（协议 §四.3）。
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var isFirstLine = true;

        while (!ct.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return; // stdin 关闭：宿主侧已收手，索引进程跟随退出
            }

            if (isFirstLine)
            {
                // BOM 容忍（utf-8-sig 语义）：宿主承诺不写 BOM，但**容忍是工具侧的义务**。
                // 真有 BOM 时不剥掉 ⇒ 首帧 JSON 解析失败 ⇒ 表现为"心跳超时"，症状与原因距离极远
                //（本项目宿主/工具两侧都吃过这个亏，两侧都要防）。
                if (line.Length > 0 && line[0] == '\uFEFF')
                {
                    line = line[1..];
                }

                isFirstLine = false;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                // stdout 上出现非 JSON 帧 = 分帧被污染（谁把调试输出写到了 stdout）。记 stderr 后继续服务。
                await WriteDiagnosticAsync(
                    $"非 JSON 帧（调试输出必须走 stderr）: {Truncate(line)}（{ex.Message}）").ConfigureAwait(false);
                continue;
            }

            var id = request?["id"]?.Clone();
            var method = request?["method"]?.GetValue<string>();

            if (method is null)
            {
                // 连 method 都没有：无法分派。有 id 的（形状像请求但缺字段）**必须回错误**，
                // 否则调用方 await 永久挂起；无 id 的（通知形状）按协议忽略。
                // Clone 是必须的：id 挂在请求树上，跨帧复用会抛 "node already has a parent"（§四.4）。
                if (id is not null)
                {
                    await WriteErrorAsync(id, RpcErrorCodes.InvalidRequest, "请求缺少 method 字段")
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (id is null)
            {
                continue; // 通知（无 id）：不需要应答
            }

            switch (method)
            {
                case MethodPing:
                    await WriteResultAsync(id, BuildPingResult()).ConfigureAwait(false);
                    break;

                case MethodStop:
                    await WriteResultAsync(id, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
                    Volatile.Write(ref _stopRequested, 1);
                    return;

                case ProtocolMethods.SearchStart:
                case ProtocolMethods.SearchQuery:
                case ProtocolMethods.SearchStatus:
                case ProtocolMethods.SearchPauseIndexing:
                case ProtocolMethods.SearchResumeIndexing:
                    if (_search is null)
                    {
                        await WriteErrorAsync(
                            id,
                            RpcErrorCodes.MethodNotFound,
                            $"方法 '{method}' 需要注入 SearchService（协议 {ProtocolMethods.SearchStart}/"
                            + $"{ProtocolMethods.SearchQuery}/{ProtocolMethods.SearchStatus}，docs/W3-搜索协议.md V1.0）")
                            .ConfigureAwait(false);
                        break;
                    }

                    if (method == ProtocolMethods.SearchStart)
                    {
                        await HandleSearchStartAsync(id, request).ConfigureAwait(false);
                    }
                    else if (method == ProtocolMethods.SearchQuery)
                    {
                        await HandleSearchQueryAsync(id, request).ConfigureAwait(false);
                    }
                    else if (method == ProtocolMethods.SearchPauseIndexing
                        || method == ProtocolMethods.SearchResumeIndexing)
                    {
                        await HandleSearchPauseAsync(id, method == ProtocolMethods.SearchPauseIndexing)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await HandleSearchStatusAsync(id).ConfigureAwait(false);
                    }

                    break;

                default:
                    // 未知方法必须显式拒绝（MethodNotFound），不能静默忽略 ——
                    // 静默 = 调用方挂起（§四.3）；本项目的 S3/S14 家族教训同源。
                    await WriteErrorAsync(
                        id,
                        RpcErrorCodes.MethodNotFound,
                        $"索引服务未实现方法 '{method}'（W3-a-1 骨架仅支持 {MethodPing} / {MethodStop}；"
                        + "search.* 随 W3-b-4 落地，协议见 docs/W3-搜索协议.md）").ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// ping 的返回。<b>每个字段都必须有具体值</b>（W3-a-1 验收①："返回具体字段，不是空对象"）——
    /// 空对象/空数组会让"通了"与"没通但没报错"无法区分（恒真断言同族）。
    /// volumes = 当前托管的实际卷清单（W3-b-4 宿主接线：自举完成后非空；自举前/未启用时为空数组）。
    /// </summary>
    internal JsonObject BuildPingResult()
    {
        return new JsonObject
        {
            ["ok"] = true,
            ["version"] = typeof(IndexRpcServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            ["protocol"] = 1,
            ["volumes"] = VolumesArray(),
            ["skippedVolumes"] = SkippedVolumesArray(),
            ["failedVolumes"] = FailedVolumesArray(),
            ["detectedVolumes"] = _search?.DetectedVolumes ?? 0,
            ["ownScopes"] = OwnScopesArray(),
            ["pid"] = Environment.ProcessId,
            ["uptimeMs"] = (long)(DateTimeOffset.Now - _startedAt).TotalMilliseconds,
        };
    }

    /// <summary>
    /// **尝试索引但失败**的卷（2026-09-25 缺口②）。与 <see cref="SkippedVolumesArray"/> 成对存在：
    /// 只回其中一半，`indexed + skipped + failed == detected` 就不成立，"0+0≠2" 会重演。
    /// 每项必带 <c>kind</c>（枚举名，机读）+ <c>code</c>（原码透传）+ <c>message</c>（人类可读）。
    /// </summary>
    private JsonArray FailedVolumesArray()
    {
        var arr = new JsonArray();
        if (_search is null)
        {
            return arr;
        }

        foreach (var fv in _search.FailedVolumes)
        {
            arr.Add(new JsonObject
            {
                ["volume"] = fv.Volume,
                ["kind"] = fv.Kind.ToString(),
                ["code"] = fv.Code,
                ["reasonText"] = fv.ReasonText,
                ["message"] = fv.Message,
            });
        }

        return arr;
    }

    /// <summary>
    /// 自有子树作用域（P4）。每卷一项：<c>anchored</c>=false 表示**本次没有排除任何条目**
    /// （<c>reason</c> 说明为什么）。与失败卷清单同一个理由要做成结构化字段：
    /// 想断言"排除真的生效"，就只能靠机读字段，不能靠正则挖日志。
    /// </summary>
    private JsonArray OwnScopesArray()
    {
        var arr = new JsonArray();
        if (_search is null)
        {
            return arr;
        }

        foreach (var s in _search.OwnScopes)
        {
            arr.Add(new JsonObject
            {
                ["volume"] = s.Volume,
                ["anchored"] = s.Anchored,
                ["count"] = s.Count,
                ["extendedByUsn"] = s.ExtendedByUsn,
                ["reason"] = s.Reason,
            });
        }

        return arr;
    }

    /// <summary>
    /// 跳过卷清单（W3-e-2）。每项**必带原因枚举与原因文案** —— 只回卷名等于让调用方
    /// 只能说"有卷被跳过"，说不出为什么（"跳过必须可见"的下半句就是"带原因"）。
    /// </summary>
    private JsonArray SkippedVolumesArray()
    {
        var arr = new JsonArray();
        if (_search is null)
        {
            return arr;
        }

        foreach (var sk in _search.SkippedVolumes)
        {
            arr.Add(new JsonObject
            {
                ["volume"] = sk.Volume,
                ["reason"] = sk.Reason.ToString(),
                ["reasonText"] = sk.ReasonText,
            });
        }

        return arr;
    }

    // ── W3-b-4：search.* 协议方法（docs/W3-搜索协议.md V1.0 冻结条文）──
    //
    // 纪律对照：
    //   - -32602 必带 data.field（断言 22.5 的断言面），且**拒绝而不钳制**（limit，22.7）；
    //   - epoch 缺失/非整数无默认值兜底（§2.1 —— 兜底 = 乱序 bug 从显式报错变偶发闪现）；
    //   - epoch 只**原样回传**，索引侧不做任何比较（§2.2：校验点在宿主侧）；
    //   - ready=false 时 query 回 -32001（不是 -32603 兜底，断言 22.6）；
    //   - total 与 hits 分离（§3.2.2）；highlights 由索引侧按 UTF-16 code unit 计算（§3.2.2）。

    private async Task HandleSearchStartAsync(JsonNode id, JsonNode? request)
    {
        var service = _search!;

        // W11-b：pathFilter 处理（协议 §3.1 冻结语义的落地；生效时机 = 本次调用，不重建不重启）。
        //   - 缺 params.pathFilter ⇒ 只做管道复位（V1.0 存量行为，26.5 幂等断言面）；
        //   - 空串 ⇒ **清除**限定（开窗即发在配置为空时用它确保无限定 —— 幂等，不是错误）；
        //   - 非空 ⇒ 校验（类型 / 绝对路径存在）后**真正锚定并挂包含闸**（v1 的"校验后丢弃"缺陷在此修复）。
        string? pathFilter = null;
        if (request?["params"] is { } p && p["pathFilter"] is { } pfNode)
        {
            if (pfNode is not JsonValue v || !v.TryGetValue<string>(out var pf))
            {
                await WriteInvalidParamAsync(id, "pathFilter", "pathFilter 必须是字符串").ConfigureAwait(false);
                return;
            }

            if (pf.Trim().Length > 0 && !Directory.Exists(pf))
            {
                // 错误路径不改变任何状态（协议 §4：保持 ready 原状）—— 锚定与管道复位只在成功路径做
                await WriteErrorAsync(
                    id, RpcErrorCodes.SearchBadPathFilter,
                    $"pathFilter 不存在或不可读: {pf}").ConfigureAwait(false);
                return;
            }

            pathFilter = pf;
        }

        // 成功路径：先应用限定（空串 = 清除），再复位递减管道（协议 §3.1 副作用）
        var pfState = pathFilter is null ? null : service.ApplyPathFilter(pathFilter);
        service.ResetPipeline();

        var result = new JsonObject
        {
            ["ready"] = service.Ready,
            ["totalFiles"] = service.TotalFiles,
        };

        if (pfState is not null)
        {
            // 加法扩展（同 paused 先例）：锚定结局随应答回传 —— fail-open 时调用方当场就知道"没生效"
            result["pathFilterAnchored"] = pfState.Anchored;
            result["pathFilterReason"] = pfState.Reason;
        }

        await WriteResultAsync(id, result).ConfigureAwait(false);
    }

    private async Task HandleSearchQueryAsync(JsonNode id, JsonNode? request)
    {
        var service = _search!;
        var p = request?["params"];
        if (p is null)
        {
            await WriteInvalidParamAsync(id, "q", "缺少 params").ConfigureAwait(false);
            return;
        }

        // q：非空、长度 ≤ 260（NTFS 单组件上限，协议 §3.2.1）
        if (p["q"] is not JsonValue qv || !qv.TryGetValue<string>(out var q)
            || q.Length == 0 || q.Length > 260)
        {
            await WriteInvalidParamAsync(id, "q", "q 必须是非空字符串且长度 ≤ 260").ConfigureAwait(false);
            return;
        }

        // substr：必填 bool（§3.2.1 —— 必填性由 -32602 守护，不做默认值兜底）
        if (p["substr"] is not JsonValue sv || !sv.TryGetValue<bool>(out var substr))
        {
            await WriteInvalidParamAsync(id, "substr", "substr 必须是 bool").ConfigureAwait(false);
            return;
        }

        // limit：1..200，超界拒绝而非静默钳制（§3.2.1 —— 钳制让"结果为什么变少"变成猜谜）
        if (p["limit"] is not JsonValue lv || !lv.TryGetValue<int>(out var limit)
            || limit < 1 || limit > 200)
        {
            await WriteInvalidParamAsync(id, "limit", "limit 必须是 1..200 的整数").ConfigureAwait(false);
            return;
        }

        // epoch：必填整数（§2.1 必填性 —— 兜底等于把乱序 bug 从显式报错变成偶发闪现）
        if (p["epoch"] is not JsonValue ev || !ev.TryGetValue<long>(out var epoch))
        {
            await WriteInvalidParamAsync(id, "epoch", "epoch 必填且必须是整数（无默认值兜底）").ConfigureAwait(false);
            return;
        }

        // ready 校验在参数校验之后（参数错误 = 宿主 bug，状态错误 = 正常时序，二者分层）
        if (!service.Ready)
        {
            await WriteErrorAsync(id, RpcErrorCodes.SearchNotReady, "索引准备中（ready=false）").ConfigureAwait(false);
            return;
        }

        var qr = service.Query(q, substr, limit);

        var hits = new JsonArray();
        foreach (var h in qr.Hits)
        {
            var hl = new JsonArray();
            foreach (var seg in h.Highlights)
            {
                hl.Add(new JsonObject { ["start"] = seg.Start, ["len"] = seg.Length });
            }

            hits.Add(new JsonObject
            {
                ["name"] = h.Name,
                ["dir"] = h.Dir,
                ["len"] = h.Name.Length, // UTF-16 code unit（协议 §3.2.2）
                ["frn"] = h.Frn,
                ["path"] = h.Path,
                ["highlights"] = hl,
            });
        }

        await WriteResultAsync(id, new JsonObject
        {
            ["epoch"] = epoch, // 原样回传（§2.1；索引侧不做任何比较）
            ["total"] = qr.Total,
            ["elapsedMs"] = (int)Math.Ceiling(qr.ElapsedMs),
            ["hits"] = hits,
        }).ConfigureAwait(false);
    }

    private async Task HandleSearchStatusAsync(JsonNode id)
    {
        var service = _search!;
        await WriteResultAsync(id, new JsonObject
        {
            ["ready"] = service.Ready,
            ["totalFiles"] = service.TotalFiles,
            // W3-c 接入增量同步前 indexing 恒为不活跃态；字段按协议 §3.3 全部必填回。
            // paused = W3-e-1 ③（协议 §3.5 加法扩展）：暂停只停增量消费，不改 ready。
            ["indexing"] = new JsonObject
            {
                ["active"] = false,
                ["phase"] = null,
                ["filesDone"] = null,
                ["filesTotal"] = null,
                ["paused"] = service.Paused,
            },
            ["volumes"] = VolumesArray(),
            ["skippedVolumes"] = SkippedVolumesArray(),
            ["failedVolumes"] = FailedVolumesArray(),
            ["detectedVolumes"] = service.DetectedVolumes,
            ["ownScopes"] = OwnScopesArray(),
            // W11-a：用户排除可见性（§4.4 三出口的协议面）。规则数 = 配置了几条；
            // 排除数 = 作用域内 FRN 总数（热启动时条目可能还在 store 里，由查询期闸挡住）。
            ["excludeRules"] = service.ExcludeRuleCount,
            ["excludedFrns"] = service.ExcludedFrns,
            // W11-b：pathFilter 限定可见性（§4.4 三出口的协议面）。
            // Anchored=false ⇒ 闸不生效（D8 fail-open），Reason 说明为什么 —— 不静默。
            ["pathFilter"] = service.PathFilterStatus.Filter,
            ["pathFilterAnchored"] = service.PathFilterStatus.Anchored,
            ["pathFilterReason"] = service.PathFilterStatus.Reason,
            ["lastError"] = service.LastError,
        }).ConfigureAwait(false);
    }

    /// <summary>已索引卷清单（卷名 + 条目数）。</summary>
    private JsonArray VolumesArray()
    {
        var arr = new JsonArray();
        if (_search is null)
        {
            return arr;
        }

        foreach (var v in _search.Volumes)
        {
            arr.Add(new JsonObject
            {
                ["volume"] = v.Volume,
                ["entries"] = v.Store.EntryCount,
            });
        }

        return arr;
    }

    /// <summary>
    /// 暂停 / 恢复索引消费（W3-e-1 ③）。**幂等**：重复暂停/恢复不是错误（UI 可能连点）。
    /// 返回 <c>{paused: bool}</c> —— 返回**当前实际状态**而非"请求已收到"，调用方不必猜。
    /// </summary>
    private async Task HandleSearchPauseAsync(JsonNode id, bool pause)
    {
        var service = _search!;
        if (pause)
        {
            service.Pause.Pause();
        }
        else
        {
            service.Pause.Resume();
        }

        await WriteResultAsync(id, new JsonObject { ["paused"] = service.Paused }).ConfigureAwait(false);
    }

    /// <summary>-32602 统一出口：必带 data.field（断言 22.5 的断言面 —— 只断"出错了"是不够的）。</summary>
    private async Task WriteInvalidParamAsync(JsonNode id, string field, string message)
    {
        await WriteFrameAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = RpcErrorCodes.InvalidParams,
                ["message"] = message,
                ["data"] = new JsonObject { ["field"] = field },
            },
        }).ConfigureAwait(false);
    }

    private async Task WriteResultAsync(JsonNode id, JsonObject result)
    {
        await WriteFrameAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }).ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(JsonNode id, int code, string message)
    {
        await WriteFrameAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }).ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(JsonObject frame)
    {
        // 分帧：一行一个 JSON、只写 \n（写 \r\n 会污染分帧）、写完立即 flush（不 flush 对端永久阻塞）
        await _output.WriteAsync(frame.ToJsonString() + "\n").ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteDiagnosticAsync(string message)
    {
        if (_diagnostics is null)
        {
            return;
        }

        await _diagnostics.WriteLineAsync("[ezt-index] " + message).ConfigureAwait(false);
        await _diagnostics.FlushAsync().ConfigureAwait(false);
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
