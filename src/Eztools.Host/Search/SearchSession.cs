// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Contracts;

namespace Eztools.Host.Search;

/// <summary>
/// 搜索会话编排（W3-d-1）—— 把"用户打字"收敛成"对索引进程的正确查询流"，
/// **UI 无关**（selftest 假传输 + 假时钟全量可验；与 <see cref="InputThrottle"/>
/// 放契约层是同一条纪律：纯逻辑不进窗口）。
///
/// 三层收敛，各管一种乱源：
/// <list type="number">
/// <item><b>节流</b>（复用协议 §3.7.3 的 <see cref="InputThrottle"/>，防抖 150 ms / 最短间隔 80 ms）：
///   收敛"物理按键"。每键必查是请求风暴（实施计划 R20）。</item>
/// <item><b>单在途 + trailing 合并</b>：同一时刻最多一个在途查询；查询期间又到的放行不排队，
///   记标记，在途回来后**用最新文本补发一次**。收敛"打字快于响应"。</item>
/// <item><b>过期闸</b>（<see cref="SearchIndexClient.IsStale"/>，W3-b-4 就位的第二道闸在 UI 消费层落地）：
///   响应回来时若已有更新的请求签发（epoch ≠ LatestEpoch），**整体丢弃**，回调根本不触发。
///   收敛"响应乱序" —— "打完 report 却显示 rep 的结果"防的就是它。</item>
/// </list>
///
/// 错误分层：<see cref="SearchIndexException"/>（含 -32001 not-ready）走 <see cref="OnError"/>，
/// **不吞**——"索引准备中"与"真的坏了"是两种状态，UI 的提示文案靠这个区分（§30.6：只断"出错"不够）。
/// 空查询（清空输入框）不发请求，直接回调 <see cref="OnResults"/>(空)。
/// </summary>
public sealed class SearchSession : IDisposable
{
    public const int DefaultLimit = 50;

    /// <summary>
    /// 搜索窗的**防抖窗口**（2026-09-25 参数归位）。取自设计方案 §6.2 ——
    /// 那里给**同一条 stdio 链路**写的就是 30 ms（"立刻发起 + 连续输入时 debounce 30 ms"）。
    ///
    /// <b>为什么不是 <see cref="InputThrottle.DebounceMs"/>（150 ms）</b>：那组 150/80 是
    /// **面板 <c>input</c> 节点**的协议值，代价口径是"一次 <c>tool.panel.data</c> 进程往返 +
    /// 面板整体重绘"（协议 §3.7.3）。搜索窗是**原生 WPF 直连索引进程（stdio）**，
    /// 链路里没有面板那一跳 —— 复用节流类是复用**机制**，但常量取值必须跟着链路重新论证。
    /// 顺手抄的代价是实打实的：端到端下限 = 防抖 + 往返 + 渲染，150 ms 会让"停顿后出结果"
    /// 白等 120 ms。这条偏差已登记（设计方案 §4.6 G6）。
    /// </summary>
    public const int DebounceMs = 30;

    /// <summary>
    /// 最短间隔：连打期间"两次真发之间的最小间隔"。
    /// **刻意保留 80 ms（与面板同值）**：连打时人类按键间隔 ~80 ms，这条让每次按键都能立刻
    /// 放行（否则纯防抖实现下连续打字会让列表**一直不更新** —— 每次按键都重置防抖）。
    /// 降到 30 只会让每次按键都发一次，被"单在途 + trailing 合并"吞掉，纯属白造请求。
    /// </summary>
    public const int MinIntervalMs = InputThrottle.MinIntervalMs;

    private readonly SearchIndexClient _client;
    private readonly InputThrottle _throttle;
    private readonly object _gate = new();

    private string _latestText = "";
    private bool _inFlight;
    private bool _trailing;
    private bool _disposed;

    /// <summary>结果到达（已过过期闸 —— 只可能是最新请求的响应）。UI 侧自行 marshal 到线程。</summary>
    public Action<SearchQueryResponse>? OnResults { get; set; }

    /// <summary>查询失败（结构化错误；-32001 = 索引未就绪，UI 显示"准备中"而非报错弹窗）。</summary>
    public Action<SearchIndexException>? OnError { get; set; }

    /// <param name="client">索引客户端（epoch 签发与配对闸都在它里面）。</param>
    /// <param name="nowMs">可注入时钟（selftest 假时钟用；默认单调毫秒）。</param>
    /// <param name="schedule">节流的到点回调调度（WPF 用 DispatcherTimer；selftest 手动驱动）。</param>
    public SearchSession(
        SearchIndexClient client,
        Func<long>? nowMs = null,
        Action<long>? schedule = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _throttle = new InputThrottle(nowMs, schedule, DebounceMs, MinIntervalMs);
    }

    /// <summary>当前节流计数（诊断/断言面：Dispatched 必须 &lt; Requested，21.7 同判据）。</summary>
    public (int Requested, int Dispatched) ThrottleCounters => (_throttle.Requested, _throttle.Dispatched);

    /// <summary>输入变了。防抖/最短间隔由 <see cref="InputThrottle"/> 决定何时真正发查询。</summary>
    public void Submit(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _latestText = text ?? "";

        if (_throttle.Report("q", _latestText) is { } delivered)
        {
            _ = RunQueryAsync(delivered.Value);
        }
    }

    /// <summary>节流器的到点回调（schedule 注入的调度器到点调它）。</summary>
    public void Fire()
    {
        if (_throttle.Fire() is { } delivered)
        {
            _ = RunQueryAsync(delivered.Value);
        }
    }

    /// <summary>
    /// 会话作废（窗口关闭）。清待发态 —— 否则到点 <see cref="Fire"/> 会在没有消费者的会话上发查询。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _throttle.Reset();
    }

    private async Task RunQueryAsync(string text)
    {
        // 空查询不进管道：清空输入框 = 显示空结果（一次本地回调，零往返）
        if (text.Length == 0)
        {
            OnResults?.Invoke(new SearchQueryResponse(0, 0, 0, Array.Empty<SearchHitDto>()));
            return;
        }

        lock (_gate)
        {
            if (_inFlight)
            {
                _trailing = true;   // 在途查询回来后用 _latestText 补发（trailing 合并）
                return;
            }

            _inFlight = true;
        }

        await RunCoreAsync(text).ConfigureAwait(false);
    }

    private async Task RunCoreAsync(string text)
    {
        while (true)
        {
            SearchIndexException? error = null;
            try
            {
                var response = await _client
                    .QueryAsync(text, substr: true, DefaultLimit)
                    .ConfigureAwait(false);

                // 过期闸：响应回来时已有更新的请求签发 ⇒ 整体丢弃（不回调）。
                if (_client.IsStale(response.Epoch))
                {
                    // 丢弃后仍可能有 trailing —— 由下一轮循环处理（见下）。
                }
                else
                {
                    OnResults?.Invoke(response);
                }
            }
            catch (SearchIndexException ex)
            {
                error = ex;
            }
            catch (Exception ex)
            {
                error = new SearchIndexException(
                    RpcErrorCodes.InternalError, $"查询索引进程失败：{ex.Message}");
            }

            // trailing：在途期间有新放行 ⇒ 用最新文本补发一次（只补最末次 —— 中间值没有观众）。
            string? nextText;
            lock (_gate)
            {
                if (_trailing)
                {
                    _trailing = false;
                    nextText = _latestText;
                }
                else
                {
                    _inFlight = false;
                    nextText = null;
                }
            }

            if (nextText is null)
            {
                if (error is not null)
                {
                    OnError?.Invoke(error);
                }

                return;
            }

            text = nextText;
            if (text.Length == 0)
            {
                // trailing 期间文本被清空：直接回调空结果，不再进管道
                OnResults?.Invoke(new SearchQueryResponse(0, 0, 0, Array.Empty<SearchHitDto>()));
                lock (_gate)
                {
                    _inFlight = false;
                }

                return;
            }
        }
    }
}
