// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>
/// 查询路由（W7-a，设计方案 §10.4）—— 输入文本 → **扇出各 provider → 归并 → 代次闸 → 渲染模型**。
///
/// <para><b>职责边界（别把东西塞进来）</b>：节流/单在途/trailing/代次递增都在 <see cref="QueryPump"/>；
/// 各 provider 的**段内**排序与错误分层在各自 provider（见 <see cref="ILauncherProvider"/> 契约 C6）。
/// 本类只做三件别处做不了的事：<b>扇出与隔离</b>、<b>段间归并排序</b>、<b>代次闸</b>。</para>
///
/// <para><b>不变量（设计方案 §10.4，改代码前必须全部成立）</b></para>
/// <list type="table">
/// <item><term>I1</term><description>同一时刻最多一个批次在途（由 <see cref="QueryPump"/> 保证）。</description></item>
/// <item><term>I2</term><description>只发布 <c>ticket.Generation == 当前代次</c> 的批次；迟到批次整体丢弃（不渲染 = 什么都没发生）。</description></item>
/// <item><term>I3/I4</term><description>trailing 补发与"空文本零往返"由 pump 保证。</description></item>
/// <item><term>I5</term><description>任一 provider 抛异常 ⇒ 该段置空 + 进 <see cref="LauncherRenderModel.Errors"/>；其余段照常渲染。</description></item>
/// <item><term>I6</term><description><c>IsReady=false</c> 的 provider 不调用、不占段位、**不进错误面**（"还没准备好"不是故障）。</description></item>
/// <item><term>I7</term><description>段序 <c>pin → apps → files</c>；段内按 <c>Score</c> 降序、同分按 Title 序号（确定性可断言）。</description></item>
/// <item><term>I8</term><description><b>files 段原序透传、绝不重排</b>（重排即破坏"与今天逐字节一致"，R14）。</description></item>
/// <item><term>I9</term><description>各 provider 已按 <see cref="DefaultLimit"/> 自限条数，归并不再二次截断。</description></item>
/// <item><term>I10</term><description>非接受态（错误/过期）的段 ⇒ 沿用上一轮该段内容，不清空不覆盖。</description></item>
/// </list>
/// </summary>
public sealed class QueryRouter : IDisposable
{
    /// <summary>单次查询的条数上限（与既有 <c>SearchSession.DefaultLimit</c> 同值 —— 改动它等于改行为）。</summary>
    public const int DefaultLimit = 50;

    private readonly QueryPump _pump;
    private readonly IReadOnlyList<ILauncherProvider> _providers;
    private readonly object _gate = new();

    /// <summary>各 provider 上一轮被**接受**的结果（段位保持用；文本变化时清空）。</summary>
    private readonly Dictionary<string, LauncherResultSet> _lastSegments = new(StringComparer.Ordinal);

    private long _generation;
    private string _lastQueryText = "";
    private bool _disposed;

    /// <summary>渲染模型到达（**可能在线程池线程** —— 消费方自行 marshal 到 UI 线程）。</summary>
    public Action<LauncherRenderModel>? OnRender { get; set; }

    /// <summary>批次级失败上报（provider 级失败走模型里的 <see cref="LauncherRenderModel.Errors"/>，不走这里）。</summary>
    public Action<LauncherError>? OnError { get; set; }

    /// <param name="providers">**已按配置筛选过的** provider 集合（未启用者不在此列 —— I6 的"禁用"侧）。</param>
    /// <param name="nowMs">可注入时钟（selftest 假时钟）。</param>
    /// <param name="schedule">节流到点调度（WPF 用 DispatcherTimer；selftest 手动驱动）。</param>
    public QueryRouter(
        IReadOnlyList<ILauncherProvider> providers,
        Func<long>? nowMs = null,
        Action<long>? schedule = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _pump = new QueryPump(nowMs, schedule);
        _pump.Execute = RunBatchAsync;
        _pump.OnEmptyText = OnEmptyTicket;
        _pump.OnExecuteFailed = ex => OnError?.Invoke(new LauncherError(
            LauncherErrorKind.ProviderFailed, 0, $"启动器查询失败：{ex.Message}", ex.ToString()));
    }

    /// <summary>最近一次派发的代次（探针/断言面）。</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>节流计数（诊断/断言面）。</summary>
    public (int Requested, int Dispatched) ThrottleCounters => _pump.ThrottleCounters;

    /// <summary>输入变了（窗口的 TextChanged 入口）。</summary>
    public void Submit(string text)
    {
        if (_disposed)
        {
            return;
        }

        _pump.Submit(text);
    }

    /// <summary>节流到点回调（窗口的 DispatcherTimer 入口）。</summary>
    public void Fire() => _pump.Fire();

    /// <summary>
    /// 用当前文本重新派发一次（延迟就绪的来源补发用，见 <see cref="QueryPump.Requery"/>）。
    /// </summary>
    public void Requery()
    {
        if (_disposed)
        {
            return;
        }

        _pump.Requery();
    }

    /// <summary>会话作废（窗口关闭）。</summary>
    public void Dispose()
    {
        _disposed = true;
        _pump.Dispose();
    }

    /// <summary>空文本：零 provider 调用 + 清段位（否则"清空输入框后仍残留上一轮的应用列表"）。</summary>
    private void OnEmptyTicket(QueryTicket ticket)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _generation = ticket.Generation;   // ★ 也推进代次：让在途批次的结果失效（见类注）
            _lastQueryText = "";
            _lastSegments.Clear();
        }

        Publish(new LauncherRenderModel(
            Generation: ticket.Generation,
            QueryText: "",
            IsEmptyQuery: true,
            AnyAccepted: true,
            FilesAccepted: true,
            Items: Array.Empty<LauncherItem>(),
            Errors: EmptyErrors,
            FilesHitCount: 0,
            FilesTotal: 0,
            FilesElapsedMs: 0));
    }

    private async Task RunBatchAsync(QueryTicket ticket)
    {
        var text = ticket.Text;

        lock (_gate)
        {
            _generation = ticket.Generation;
            if (!string.Equals(text, _lastQueryText, StringComparison.Ordinal))
            {
                _lastQueryText = text;
                _lastSegments.Clear();   // 文本变了 ⇒ 段位保持失效（都不清了 ⇒ 新旧文本结果混搭）
            }
        }

        // I6：未就绪的 provider 静默缺席（不调用、不占段位、不进错误面）
        var active = new List<ILauncherProvider>(_providers.Count);
        foreach (var provider in _providers)
        {
            if (provider.IsReady)
            {
                active.Add(provider);
            }
        }

        var sets = await Task.WhenAll(active.Select(p => SafeQueryAsync(p, ticket))).ConfigureAwait(false);

        // I2 代次闸：迟到批次整体丢弃（连渲染都不发生 —— 等价于"什么都没发生"）
        // ★ 突变验证（2026-10-01 实测）：摘掉本闸 ⇒ `--probe-launcher --router` 的渲染日志多出
        //   `1|201|陈旧结果` ⇒ verify-desktop 那条"代次闸上屏级证据"立刻变红（不是恒真断言）。
        if (ticket.Generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        var model = Merge(ticket, sets);

        // 全部段未被接受且无错误 ⇒ 不发布（今天"过期丢弃 ⇒ 回调不触发 ⇒ 界面与选择态全不动"的等价物）
        if (!model.AnyAccepted && model.Errors.Count == 0)
        {
            return;
        }

        Publish(model);
    }

    /// <summary>
    /// 单 provider 的隔离壳（I5）。**致命异常不捕**：`OutOfMemoryException` / `StackOverflowException`
    /// 属于进程级不可恢复状态，把它们伪装成"某个 provider 坏了"只会掩盖真问题。
    /// </summary>
    private static async Task<LauncherResultSet> SafeQueryAsync(ILauncherProvider provider, QueryTicket ticket)
    {
        var query = new LauncherQuery(ticket.Text, ticket.Generation, DefaultLimit, Substr: true);
        try
        {
            return await provider.QueryAsync(query, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new LauncherResultSet(
                ticket.Generation,
                provider.Id,
                Array.Empty<LauncherItem>(),
                0,
                0,
                Dropped: false,
                Error: new LauncherError(
                    LauncherErrorKind.ProviderFailed,
                    0,
                    $"{provider.DisplayName}暂不可用：{ex.Message}",
                    ex.ToString()));
        }
    }

    private static readonly IReadOnlyDictionary<string, LauncherError> EmptyErrors =
        new Dictionary<string, LauncherError>(0);

    private LauncherRenderModel Merge(QueryTicket ticket, IReadOnlyList<LauncherResultSet> sets)
    {
        var errors = new Dictionary<string, LauncherError>(StringComparer.Ordinal);
        var pin = new List<LauncherItem>();
        var apps = new List<LauncherItem>();
        var clip = new List<LauncherItem>();
        var files = new List<LauncherItem>();
        var anyAccepted = false;
        var filesAccepted = false;
        var filesTotal = 0;
        var filesElapsed = 0;

        lock (_gate)
        {
            foreach (var set in sets)
            {
                if (set.Error is { } error)
                {
                    errors[set.ProviderId] = error;
                }

                // I10：非接受态 ⇒ 沿用上一轮该段（不清空不覆盖）
                if (!set.Accepted)
                {
                    if (_lastSegments.TryGetValue(set.ProviderId, out var previous))
                    {
                        Accumulate(previous, fromPrevious: true);
                    }

                    continue;
                }

                _lastSegments[set.ProviderId] = set;
                Accumulate(set, fromPrevious: false);
            }

            void Accumulate(LauncherResultSet set, bool fromPrevious)
            {
                // 只有"本轮真接受"的段才算 AnyAccepted；沿用上一轮的段不产生重绘理由
                anyAccepted |= !fromPrevious;

                switch (LauncherProviderRegistry.SegmentOf(set.ProviderId))
                {
                    case LauncherSegment.Pin:
                        pin.AddRange(set.Items);
                        break;

                    case LauncherSegment.Apps:
                        apps.AddRange(set.Items);
                        break;

                    case LauncherSegment.Clip:
                        // W10-a：剪贴板段原序透传（D5，与 files 同款"不重排"），
                        // 且**不参与** files 专属计数 —— 混进去会让状态行的数字失真（S9）。
                        clip.AddRange(set.Items);
                        break;

                    default:
                        files.AddRange(set.Items);
                        if (!fromPrevious)
                        {
                            filesAccepted = true;
                            filesTotal = set.Total;
                            filesElapsed = set.ElapsedMs;
                        }

                        break;
                }
            }
        }

        // I7：段内排序（files / clip 段跳过 —— I8 原序透传，那顺序是各自来源给定的既有契约）
        var merged = new List<LauncherItem>(pin.Count + apps.Count + clip.Count + files.Count);
        merged.AddRange(OrderSegment(pin));
        merged.AddRange(OrderSegment(apps));
        merged.AddRange(clip);
        merged.AddRange(files);

        return new LauncherRenderModel(
            Generation: ticket.Generation,
            QueryText: ticket.Text,
            IsEmptyQuery: false,
            AnyAccepted: anyAccepted,
            FilesAccepted: filesAccepted,
            Items: merged,
            Errors: errors,
            FilesHitCount: files.Count,
            FilesTotal: filesTotal,
            FilesElapsedMs: filesElapsed);
    }

    /// <summary>段内排序：Score 降序，同分按 Title 序号（确定性 —— 否则同一输入两次渲染顺序可能不同）。</summary>
    private static List<LauncherItem> OrderSegment(List<LauncherItem> segment) =>
        segment.Count < 2
            ? segment
            : segment
                .OrderByDescending(i => i.Score)
                .ThenBy(i => i.Title, StringComparer.Ordinal)
                .ToList();

    private void Publish(LauncherRenderModel model) => OnRender?.Invoke(model);
}
