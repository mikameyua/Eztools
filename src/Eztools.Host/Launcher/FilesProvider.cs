// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Contracts;
using Eztools.Host.Search;

namespace Eztools.Host.Launcher;

/// <summary>
/// 文件来源（W7-a，设计方案 §10.5）—— **对既有索引管道的零行为包装**。
///
/// 它承担的正是原 <c>SearchSession</c> 的"一次查询 + 过期闸 + 错误分层"三件事：
/// <list type="bullet">
/// <item><b>查询</b>：<see cref="SearchIndexClient.QueryAsync"/>（<c>search.query</c>，协议 V1.0 冻结）。</item>
/// <item><b>过期闸（★ 必须留着）</b>：<see cref="SearchIndexClient.IsStale"/>。
///   它管的是"**在途查询期间索引侧会话状态变了**"—— <c>search.status</c> 与暂停/恢复**都会签发
///   epoch** ⇒ 结果按既有语义作废、界面不动。若用 router 的代次闸替代它，"查询在途时用户点了
///   托盘暂停"这个场景的行为就变了（新行为：照常渲染该结果；旧行为：丢弃）⇒ 违反 W7-a 零行为。
///   两道闸并存且各自成立：代次闸管"结果属于哪一次派发"，过期闸管"索引进程的会话级作废"。</item>
/// <item><b>错误分层</b>：<c>-32001 not-ready</c> 与"真的坏了"给**不同文案**
///   （只断"出了错"不够）—— 文案在此单点确定，渲染层不拼字符串。</item>
/// </list>
///
/// <para><b>映射是逐字段的</b>（设计方案 §10.5 映射表）：<c>Name→Title</c> / <c>Path→Subtitle</c> /
/// <c>Highlights→Highlights</c>，并把整个 <see cref="SearchHitDto"/> 原样塞进
/// <see cref="LauncherItem.FileHit"/> —— 渲染层凭它走**既有的** <c>HitText</c>（零改动）。</para>
/// </summary>
public sealed class FilesProvider : ILauncherProvider
{
    private readonly SearchIndexClient _client;
    private readonly Func<CoreAvailability> _coreAvailability;

    /// <summary>
    /// 未注入探测时的默认值 —— <see cref="CoreAvailability.Unknown"/> ⇒ 分流退化为 W7 的既有行为
    /// （<c>-32001</c> 一律当"正在建索引"）。
    ///
    /// <para><b>★ 生产装配点必须注入真实探测</b>（<c>TrayApplication.EnsureSearchWindow</c>），
    /// 否则 W8·B1 在该路径上不生效。之所以还留默认值而不是做成必填参数：
    /// 探针与 <c>SearchUiProbe</c> 用 <c>new FilesProvider(client)</c> 构造的是**"文件项渲染零变化"
    /// 的证据链**（W7 FR-10），那条链一行都不该为了新功能而改动。默认值在这里是**刻意沉默**，
    /// 它被 <c>LauncherUiProbe</c> 的 <c>corestatus</c> 模式以正向断言钉住（注入了才分流）。</para>
    /// </summary>
    private static readonly Func<CoreAvailability> NoProbe = static () => CoreAvailability.Unknown;

    public FilesProvider(SearchIndexClient client, Func<CoreAvailability>? coreAvailability = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _coreAvailability = coreAvailability ?? NoProbe;
    }

    /// <inheritdoc />
    public string Id => LauncherProviderRegistry.Files;

    /// <inheritdoc />
    public string DisplayName => "文件搜索";

    /// <summary>files 永远"就绪"：索引未就绪由协议错误（-32001）表达，不由 IsReady 表达
    /// （否则"准备中"会变成"静默缺席"，用户看不到"正在建索引"那句提示）。</summary>
    public bool IsReady => true;

    /// <inheritdoc />
    public async Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
    {
        try
        {
            var response = await _client
                .QueryAsync(query.Text, query.Substr, query.Limit, ct)
                .ConfigureAwait(false);

            // 过期闸：索引侧会话状态已变 ⇒ 该段作废（消费方沿用上一轮，界面不动）
            if (_client.IsStale(response.Epoch))
            {
                return new LauncherResultSet(
                    query.Generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: true, Error: null);
            }

            var items = new LauncherItem[response.Hits.Count];
            for (var i = 0; i < response.Hits.Count; i++)
            {
                items[i] = ToItem(response.Hits[i]);
            }

            return new LauncherResultSet(
                query.Generation, Id, items, response.Total, response.ElapsedMs, Dropped: false, Error: null);
        }
        catch (SearchIndexException ex)
        {
            return new LauncherResultSet(
                query.Generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false,
                Error: MapError(ex, _coreAvailability()));
        }
        catch (Exception ex)
        {
            return new LauncherResultSet(
                query.Generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false,
                Error: new LauncherError(
                    LauncherErrorKind.IndexUnavailable,
                    RpcErrorCodes.InternalError,
                    $"搜索出错（{RpcErrorCodes.InternalError}）：查询索引进程失败：{ex.Message}",
                    ex.ToString()));
        }
    }

    /// <summary>命中 → 结果项（逐字段对照设计方案 §10.5 映射表）。</summary>
    internal static LauncherItem ToItem(SearchHitDto hit) => new(
        Kind: LauncherKind.File,
        Title: hit.Name,
        Subtitle: hit.Path,
        IconHint: "",
        Score: 0,                    // files 段不参与段内重排（R14）
        Highlights: hit.Highlights,
        PrimaryAction: new LauncherAction(LauncherActionKind.Open, hit.Path),
        SecondaryAction: new LauncherAction(LauncherActionKind.Reveal, hit.Path),
        FileHit: hit);               // ★ 渲染层凭它走既有 HitText（FR-10）

    /// <summary>
    /// 结构化错误 → 段位错误。
    ///
    /// <para><b>真在建的两句话必须与今天窗口里显示的逐字相同</b>（<c>RenderError</c> 的原样迁移）——
    /// 改文案等于改行为（W7 FR-10）。</para>
    ///
    /// <para><b>W8·B1 的分流点就是这里</b>：<c>-32001</c> 的两种成因（真在建 / 核心服务缺席）
    /// 在此按 <paramref name="availability"/> 分开。非 <c>-32001</c> 的错误**不看**可达性 ——
    /// 那些是索引进程自己的故障，与核心服务无关，混进来只会造出错误的指引。</para>
    ///
    /// <para><paramref name="availability"/> 默认 <see cref="CoreAvailability.Unknown"/>：
    /// 既有调用点（selftest / 探针）因此零改动，且语义正好是"不声称核心服务有问题"。</para>
    /// </summary>
    internal static LauncherError MapError(
        SearchIndexException ex,
        CoreAvailability availability = CoreAvailability.Unknown)
    {
        if (ex.Code != RpcErrorCodes.SearchNotReady)
        {
            return new LauncherError(
                LauncherErrorKind.IndexUnavailable,
                ex.Code,
                $"搜索出错（{ex.Code}）：{ex.Message}",
                ex.Message);
        }

        return availability switch
        {
            CoreAvailability.CoreNotRunning => new LauncherError(
                LauncherErrorKind.CoreUnavailable,
                ex.Code,
                "搜索需要启动核心服务，点此启动",
                "索引未就绪，且核心服务（ezt-core）未在运行。索引自举只在进程启动时尝试一次且不重试 ⇒ "
                + "这不是等待能解决的问题。" + ex.Message,
                CanLaunch: true),

            CoreAvailability.CoreNotElevated => new LauncherError(
                LauncherErrorKind.CoreNotElevated,
                ex.Code,
                "核心服务未提权 —— 点此以管理员身份重启",
                "索引未就绪，且核心服务在运行但没有提权。读取 MFT 需要提权 ⇒ "
                + "这不是等待能解决的问题。" + ex.Message,
                CanLaunch: true),

            // CoreOk（真在建）与 Unknown（探测无结论）都落到这里 —— 既有文案逐字不变。
            _ => new LauncherError(
                LauncherErrorKind.IndexNotReady,
                ex.Code,
                "正在建索引（首次全量约 10 秒级，取决于文件数）—— 打字会自动重试",
                ex.Message),
        };
    }
}
