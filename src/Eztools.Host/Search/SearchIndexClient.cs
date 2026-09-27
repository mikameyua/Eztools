// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Host.Search;

/// <summary>
/// 宿主 → ezt-index 的传输抽象（W3-b-4）。协议帧一行一个 JSON（stdio 分帧），
/// 抽象成"请求帧进、响应帧出"，让 <see cref="SearchIndexClient"/> 的**校验逻辑**
/// （epoch 配对 / 错误分层 / 字段契约）在 selftest 里用内存假传输全量可验 ——
/// 真进程 stdio 传输随 W3-d（搜索 UI 接入）落地（进程托管与懒启动同批）。
/// </summary>
public interface ISearchIndexTransport
{
    /// <summary>发一帧请求，等到匹配 id 的响应帧（error 帧也原样返回，由客户端分层翻译）。</summary>
    Task<JsonObject> RoundTripAsync(JsonObject request, CancellationToken ct = default);
}

/// <summary>
/// 宿主侧搜索客户端（W3-b-4）：**epoch 单调性校验点**（设计方案 §5.4 / 协议 §2.2 ——
/// "epoch 索引侧只原样回传，校验在宿主"）。
///
/// 为什么 epoch 校验必须在宿主侧做（不做成索引侧的拒绝）：
/// 乱序的根源是**宿主并发请求**（UI 打字快），索引进程视角里每个请求都是合法的新请求 ——
/// "过期"只有发请求的一方知道。所以索引侧原样回传（V1.0 冻结条文），宿主侧两道闸：
///   ① <b>配对闸</b>（本类 <see cref="QueryAsync"/> 内）：响应 epoch 必须 == 请求 epoch，
///      不匹配 = 传输层串帧/服务端 bug ⇒ <see cref="SearchEpochMismatchException"/>（结构化，带两侧 epoch）；
///   ② <b>过期闸</b>（UI 消费层，随 W3-d）：响应回来时 epoch 可能已不是
///      <see cref="LatestEpoch"/>（更新的请求已发出）⇒ 调用方用 <see cref="IsStale"/> 丢弃，
///      不更新界面（"打完 report 却显示 rep 的结果"防的就是它）。
/// </summary>
public sealed class SearchIndexClient
{
    private readonly ISearchIndexTransport _transport;
    private long _latestEpoch;

    public SearchIndexClient(ISearchIndexTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>最近一次签发的 epoch（单调递增；过期闸的比较基准）。</summary>
    public long LatestEpoch => Interlocked.Read(ref _latestEpoch);

    /// <summary>签发下一个 epoch（原子递增；0 保留给"未签发"，从 1 起）。</summary>
    public long IssueEpoch() => Interlocked.Increment(ref _latestEpoch);

    /// <summary>过期闸：该 epoch 是否已不是最新（新请求签发后，旧响应即过期）。</summary>
    public bool IsStale(long epoch) => epoch != Interlocked.Read(ref _latestEpoch);

    /// <summary>
    /// 发起一次 search.query。响应 epoch 与请求 epoch 不匹配 ⇒ <see cref="SearchEpochMismatchException"/>
    ///（**不返回部分结果** —— 乱序结果必须整体丢弃，调用方没有"拿一半"的口子）。
    /// 服务端结构化错误（-32001 not-ready / -32602 参数）原样透传为 <see cref="SearchIndexException"/>。
    /// </summary>
    public async Task<SearchQueryResponse> QueryAsync(
        string q, bool substr, int limit, CancellationToken ct = default)
    {
        var epoch = IssueEpoch();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = $"q{epoch}",
            ["method"] = ProtocolMethods.SearchQuery,
            ["params"] = new JsonObject
            {
                ["q"] = q,
                ["substr"] = substr,
                ["limit"] = limit,
                ["epoch"] = epoch,
            },
        };

        var response = await _transport.RoundTripAsync(request, ct).ConfigureAwait(false);

        if (response["error"] is { } error)
        {
            throw new SearchIndexException(
                error["code"]?.GetValue<int>() ?? RpcErrorCodes.InternalError,
                error["message"]?.GetValue<string>() ?? "ezt-index 返回未知错误");
        }

        var result = response["result"] as JsonObject
            ?? throw new SearchIndexException(
                RpcErrorCodes.InternalError, "ezt-index 响应缺 result（协议破坏）");

        // 配对闸：响应 epoch 必须 == 请求 epoch（索引侧原样回传被破坏 = 串帧或服务端 bug）
        var echoed = result["epoch"]?.GetValue<long>()
            ?? throw new SearchIndexException(
                RpcErrorCodes.InternalError, "ezt-index 响应缺 epoch 字段（协议 §2.1）");
        if (echoed != epoch)
        {
            throw new SearchEpochMismatchException(epoch, echoed);
        }

        var hits = new List<SearchHitDto>();
        if (result["hits"] is JsonArray arr)
        {
            foreach (var h in arr)
            {
                if (h is not JsonObject hit)
                {
                    continue;
                }

                var highlights = new List<(int Start, int Len)>();
                if (hit["highlights"] is JsonArray hl)
                {
                    foreach (var seg in hl)
                    {
                        if (seg is JsonObject o
                            && o["start"] is { } s && o["len"] is { } l)
                        {
                            highlights.Add((s.GetValue<int>(), l.GetValue<int>()));
                        }
                    }
                }

                hits.Add(new SearchHitDto(
                    hit["name"]?.GetValue<string>() ?? "",
                    hit["dir"]?.GetValue<bool>() ?? false,
                    hit["path"]?.GetValue<string>() ?? "",
                    hit["frn"]?.GetValue<long>() ?? 0,
                    highlights));
            }
        }

        return new SearchQueryResponse(
            echoed,
            result["total"]?.GetValue<int>() ?? 0,
            result["elapsedMs"]?.GetValue<int>() ?? 0,
            hits);
    }

    /// <summary>
    /// 暂停索引增量消费（协议 <c>search.pauseIndexing</c>，W3-e-1 ③ / 2026-09-25 缺口①）。
    ///
    /// <b>语义边界（写死，避免 UI 猜）</b>：暂停**只停"把 journal 变更应用进索引"**，
    /// 已建索引照常可查、<c>ready</c> 不变 —— 所以暂停期的搜索结果**会静默变旧**。
    /// 正因如此，"暂停必须可见"（S9′ 家族）是调用方的**义务**：任何触发暂停的入口，
    /// 都必须在用户能看到的地方显示"已暂停"，否则用户拿着过时结果却以为是最新的。
    ///
    /// **幂等**：重复暂停不是错误（UI 可能连点）。返回值 = **当前实际状态**，不是"请求已收到"。
    /// </summary>
    public Task<bool> PauseIndexingAsync(CancellationToken ct = default) =>
        SetPauseAsync(ProtocolMethods.SearchPauseIndexing, ct);

    /// <summary>恢复索引增量消费（协议 <c>search.resumeIndexing</c>）。幂等；返回当前实际状态。</summary>
    public Task<bool> ResumeIndexingAsync(CancellationToken ct = default) =>
        SetPauseAsync(ProtocolMethods.SearchResumeIndexing, ct);

    private async Task<bool> SetPauseAsync(string method, CancellationToken ct)
    {
        var epoch = IssueEpoch();   // 暂停态变了 ⇒ 在途查询结果一并作废
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = $"p{epoch}",
            ["method"] = method,
        };

        var response = await _transport.RoundTripAsync(request, ct).ConfigureAwait(false);
        if (response["error"] is { } error)
        {
            throw new SearchIndexException(
                error["code"]?.GetValue<int>() ?? RpcErrorCodes.InternalError,
                error["message"]?.GetValue<string>() ?? "ezt-index 返回未知错误");
        }

        var result = response["result"] as JsonObject
            ?? throw new SearchIndexException(
                RpcErrorCodes.InternalError, $"ezt-index {method} 响应缺 result（协议破坏）");

        // 缺 paused 字段 = 协议破坏：不能默默当 false（那会把"服务端没答应"显示成"已恢复"）
        if (result["paused"] is null)
        {
            throw new SearchIndexException(
                RpcErrorCodes.InternalError, $"{method} 响应缺 paused 字段（协议破坏）");
        }

        return result["paused"]!.GetValue<bool>();
    }

    /// <summary>
    /// 查询索引状态（协议 <c>search.status</c>，W3-e-2 起用于取卷清单与**跳过清单**）。
    /// status 无 epoch（不是查询，不参与乱序配对）。
    /// </summary>
    public async Task<SearchStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var epoch = IssueEpoch();   // 仍签发：让任何在途查询结果作废（状态刷新后旧结果已无意义）
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = $"s{epoch}",
            ["method"] = ProtocolMethods.SearchStatus,
        };

        var response = await _transport.RoundTripAsync(request, ct).ConfigureAwait(false);
        if (response["error"] is { } error)
        {
            throw new SearchIndexException(
                error["code"]?.GetValue<int>() ?? RpcErrorCodes.InternalError,
                error["message"]?.GetValue<string>() ?? "ezt-index 返回未知错误");
        }

        var result = response["result"] as JsonObject
            ?? throw new SearchIndexException(
                RpcErrorCodes.InternalError, "ezt-index status 响应缺 result（协议破坏）");

        var volumes = new List<string>();
        if (result["volumes"] is JsonArray va)
        {
            foreach (var v in va)
            {
                if (v?["volume"]?.GetValue<string>() is { } name)
                {
                    volumes.Add(name);
                }
            }
        }

        // 跳过清单：reason 与 reasonText 缺任何一个都不算"可见"（只回卷名 = 说不出为什么）
        var skipped = new List<SkippedVolumeDto>();
        if (result["skippedVolumes"] is JsonArray sa)
        {
            foreach (var s in sa)
            {
                if (s is not JsonObject o
                    || o["volume"]?.GetValue<string>() is not { } vol
                    || o["reason"]?.GetValue<string>() is not { } reason
                    || o["reasonText"]?.GetValue<string>() is not { } reasonText)
                {
                    continue;
                }

                skipped.Add(new SkippedVolumeDto(vol, reason, reasonText));
            }
        }

        // 失败清单（缺口②）：kind / code / message 缺任何一个都不算"可机读"。
        // 与 skipped 并列 —— 少了它，"volumes + skipped" 会静默漏掉失败卷（`0+0≠2` 的成因）。
        var failed = new List<FailedVolumeDto>();
        if (result["failedVolumes"] is JsonArray fa)
        {
            foreach (var f in fa)
            {
                if (f is not JsonObject o
                    || o["volume"]?.GetValue<string>() is not { } vol
                    || o["kind"]?.GetValue<string>() is not { } kind
                    || o["reasonText"]?.GetValue<string>() is not { } reasonText)
                {
                    continue;
                }

                failed.Add(new FailedVolumeDto(
                    vol, kind, o["code"]?.GetValue<int>() ?? 0,
                    reasonText, o["message"]?.GetValue<string>() ?? reasonText));
            }
        }

        // 自有子树作用域（P4）：anchored=false 也要收进来 —— "没生效"是必须能被看见的状态。
        var ownScopes = new List<OwnScopeDto>();
        if (result["ownScopes"] is JsonArray oa)
        {
            foreach (var s in oa)
            {
                if (s is not JsonObject o || o["volume"]?.GetValue<string>() is not { } vol)
                {
                    continue;
                }

                ownScopes.Add(new OwnScopeDto(
                    vol,
                    o["anchored"]?.GetValue<bool>() ?? false,
                    o["count"]?.GetValue<int>() ?? 0,
                    o["extendedByUsn"]?.GetValue<long>() ?? 0,
                    o["reason"]?.GetValue<string>() ?? "未标记"));
            }
        }

        return new SearchStatusDto(
            result["ready"]?.GetValue<bool>() ?? false,
            result["totalFiles"]?.GetValue<int>() ?? 0,
            result["indexing"]?["paused"]?.GetValue<bool>() ?? false,
            volumes,
            skipped,
            failed,
            result["detectedVolumes"]?.GetValue<int>() ?? 0,
            ownScopes);
    }
}

/// <summary>被跳过的卷（宿主消费面；<paramref name="Reason"/> = 枚举名，<paramref name="ReasonText"/> = 用户可读文案）。</summary>
public sealed record SkippedVolumeDto(string Volume, string Reason, string ReasonText);

/// <summary>
/// **尝试索引但失败**的卷（宿主消费面，2026-09-25 缺口②）。
/// <paramref name="Code"/> 为原码透传（Win32 / Core 结构化码），不归一 —— UI 要能说"D: 访问被拒绝"。
/// </summary>
public sealed record FailedVolumeDto(
    string Volume, string Kind, int Code, string ReasonText, string Message);

/// <summary>索引状态 DTO（协议 search.status 的宿主消费形态，W3-e-2 扩展）。</summary>
public sealed record SearchStatusDto(
    bool Ready,
    int TotalFiles,
    bool Paused,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<SkippedVolumeDto> Skipped,
    IReadOnlyList<FailedVolumeDto> Failed,
    int DetectedVolumes,
    IReadOnlyList<OwnScopeDto> OwnScopes);

/// <summary>
/// 自有子树作用域状态（P4）。<see cref="Anchored"/>=false ⇒ 该卷本次**没有排除任何条目**，
/// <see cref="Reason"/> 说明为什么 —— "没生效"必须是可读的，不能与"生效了但没声音"同形。
/// </summary>
public sealed record OwnScopeDto(
    string Volume,
    bool Anchored,
    int Count,
    long ExtendedByUsn,
    string Reason);

/// <summary>查询响应 DTO（宿主消费面；字段名与协议 §3.2.2 对齐）。</summary>
public sealed record SearchQueryResponse(
    long Epoch,
    int Total,
    int ElapsedMs,
    IReadOnlyList<SearchHitDto> Hits);

/// <summary>单条命中 DTO。</summary>
public sealed record SearchHitDto(
    string Name,
    bool Dir,
    string Path,
    long Frn,
    IReadOnlyList<(int Start, int Len)> Highlights);

/// <summary>ezt-index 返回的结构化错误（-32001 not-ready / -32602 参数 / -32603 内部）。</summary>
public sealed class SearchIndexException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// epoch 配对失败（响应 epoch ≠ 请求 epoch）。**设计为独立类型**：调用方对"乱序/串帧"
/// 的处置（整体丢弃、静默重试）与普通错误不同 —— 类型区分是可观测性的一部分。
/// </summary>
public sealed class SearchEpochMismatchException(long expected, long actual)
    : Exception($"epoch 配对失败：请求 {expected}，响应 {actual}（结果已整体丢弃，协议 §2.2）")
{
    public long ExpectedEpoch { get; } = expected;
    public long ActualEpoch { get; } = actual;
}
