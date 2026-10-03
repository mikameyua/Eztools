// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Contracts;

namespace Eztools.Host.Launcher;

/// <summary>一次**派发**（到点且单在途已保证时）。<see cref="Generation"/> 是派发代次，从 1 起。</summary>
public readonly record struct QueryTicket(long Generation, string Text);

/// <summary>
/// 查询泵（W7-a，设计方案 §3.3 / §10.4）—— 把"用户打字"收敛成"正确的派发序列"，
/// **UI 无关**（selftest 假时钟 + 手动驱动全量可验）。代码自 <c>SearchSession</c> **平移**而来
/// （不是重写）：那一份是 W3 起就在跑的三层收敛，语义已被 6 个 selftest 用例与 W3 全波验收钉住。
///
/// 三层收敛，各管一种乱源：
/// <list type="number">
/// <item><b>节流</b>（复用 <see cref="InputThrottle"/>，防抖 <see cref="DebounceMs"/> /
///   最短间隔 <see cref="MinIntervalMs"/>）：收敛"物理按键"。每键必查是请求风暴。</item>
/// <item><b>单在途 + trailing 合并</b>：同一时刻最多一个批次在途；在途期间又到的**放行不排队**，
///   记标记，在途回来后**用最新文本补发一次**。收敛"打字快于响应"。</item>
/// <item><b>代次</b>：每次派发自增（<see cref="QueryTicket.Generation"/>），消费方据此丢弃迟到批次。
///   收敛"慢 provider 回填旧结果"（设计方案 R3）。</item>
/// </list>
///
/// <para><b>★ 代次在"派发"时递增，不是在 <c>Submit</c> 时递增</b>（设计方案 §10.4 注 1）：
/// 若在 Submit 时递增，用户在批次在途期间打字（只置 trailing、并未派发）会把**在途批次自己作废**
/// ⇒ 界面白等一轮（要等 trailing 那一轮才出结果）。这条是本类最容易写错的一行。</para>
///
/// <para><b>空文本不走管道</b>：清空输入框 = 立刻本地回调 <see cref="OnEmptyText"/>（零往返），
/// 且**不占单在途位**（与 <c>SearchSession</c> 同语义）。</para>
/// </summary>
public sealed class QueryPump : IDisposable
{
    /// <summary>
    /// 搜索链路的**防抖窗口**（2026-09-25 参数归位；取自设计方案 §6.2）。
    ///
    /// <para><b>为什么不是 <see cref="InputThrottle.DebounceMs"/>（150 ms）</b>：那组 150/80 是
    /// <b>面板 <c>input</c> 节点</b>的协议值，代价口径是"一次 <c>tool.panel.data</c> 进程往返 +
    /// 面板整体重绘"。搜索窗是**原生 WPF 直连索引进程（stdio）**，链路里没有面板那一跳 ——
    /// 复用节流类是复用**机制**，但常量取值必须跟着链路重新论证。顺手抄的代价是实打实的：
    /// 端到端下限 = 防抖 + 往返 + 渲染，150 ms 会让"停顿后出结果"白等 120 ms。</para>
    /// </summary>
    public const int DebounceMs = 30;

    /// <summary>
    /// 最短间隔：连打期间"两次真发之间的最小间隔"。
    /// **刻意保留 80 ms（与面板同值）**：连打时人类按键间隔 ~80 ms，这条让每次按键都能立刻
    /// 放行（否则纯防抖实现下连续打字会让列表**一直不更新** —— 每次按键都重置防抖）。
    /// 降到 30 只会让每次按键都发一次，被"单在途 + trailing 合并"吞掉，纯属白造请求。
    /// </summary>
    public const int MinIntervalMs = InputThrottle.MinIntervalMs;

    private readonly InputThrottle _throttle;
    private readonly object _gate = new();

    private string _latestText = "";
    private bool _inFlight;
    private bool _trailing;
    private long _generation;
    private bool _disposed;

    /// <summary>
    /// 执行一个批次的正文。**抛出的异常会被捕获并由 <see cref="OnExecuteFailed"/> 上报**
    /// （延迟到"没有后续补发"时才报 —— 打字过程中的瞬时失败不该闪一下错误）。
    /// </summary>
    public Func<QueryTicket, Task>? Execute { get; set; }

    /// <summary>空文本本地回调（零往返）。</summary>
    public Action<QueryTicket>? OnEmptyText { get; set; }

    /// <summary>批次失败上报（**只在没有 trailing 补发时**触发）。</summary>
    public Action<Exception>? OnExecuteFailed { get; set; }

    /// <param name="nowMs">可注入时钟（selftest 假时钟用；默认单调毫秒）。</param>
    /// <param name="schedule">节流的到点回调调度（WPF 用 DispatcherTimer；selftest 手动驱动）。</param>
    public QueryPump(
        Func<long>? nowMs = null,
        Action<long>? schedule = null,
        int debounceMs = DebounceMs,
        int minIntervalMs = MinIntervalMs)
    {
        _throttle = new InputThrottle(nowMs, schedule, debounceMs, minIntervalMs);
    }

    /// <summary>最近一次派发的代次（0 = 尚未派发）。</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>当前节流计数（诊断/断言面：Dispatched 必须 &lt; Requested）。</summary>
    public (int Requested, int Dispatched) ThrottleCounters => (_throttle.Requested, _throttle.Dispatched);

    /// <summary>输入变了。防抖/最短间隔由 <see cref="InputThrottle"/> 决定何时真正派发。</summary>
    public void Submit(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _latestText = text ?? "";
        if (_throttle.Report("q", _latestText) is { } delivered)
        {
            Kick(delivered.Value);
        }
    }

    /// <summary>节流器的到点回调（schedule 注入的调度器到点调它）。</summary>
    public void Fire()
    {
        if (_throttle.Fire() is { } delivered)
        {
            Kick(delivered.Value);
        }
    }

    /// <summary>
    /// 用**当前最新文本**重新派发一次（W7-b 新增）。
    ///
    /// <para><b>为什么需要它</b>：有些结果来源是"延迟就绪"的 —— 例如 apps 首次全扫要几十毫秒，
    /// 在它就绪前用户已经打完了字（那时该来源静默缺席）。等它就绪时**必须补一次派发**，
    /// 否则用户得再敲一个字符才能看见应用结果（设计方案 R10）。</para>
    ///
    /// <para>语义 = "同一个文本又来了一次"：走同一套节流/单在途/trailing，不新造路径。
    /// 文本为空（从未输入/已清空）⇒ 什么都不做（空态已是终态）。</para>
    /// </summary>
    public void Requery()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_latestText.Length == 0)
        {
            return;
        }

        if (_throttle.Report("q", _latestText) is { } delivered)
        {
            Kick(delivered.Value);
        }
    }

    /// <summary>
    /// 会话作废（窗口关闭）。清待发态 —— 否则到点 <see cref="Fire"/> 会在没有消费者的会话上派发。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _throttle.Reset();
    }

    private void Kick(string text)
    {
        // 空文本：零往返本地回调，**不进管道**（也就不占单在途位 —— 与 SearchSession 同语义）
        if (text.Length == 0)
        {
            OnEmptyText?.Invoke(new QueryTicket(NextGeneration(), ""));
            return;
        }

        lock (_gate)
        {
            if (_inFlight)
            {
                _trailing = true;   // 在途回来后用最新文本补发（trailing 合并）
                return;
            }

            _inFlight = true;
        }

        _ = RunCoreAsync(text);
    }

    private async Task RunCoreAsync(string text)
    {
        while (true)
        {
            var ticket = new QueryTicket(NextGeneration(), text);
            Exception? error = null;
            try
            {
                if (Execute is { } execute)
                {
                    await execute(ticket).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }

            // trailing：在途期间有新放行 ⇒ 用最新文本补发一次（只补最末次 —— 中间值没有观众）
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
                // 失败延迟到这里才报：有 trailing 说明用户还在打字，"打字中的瞬时失败"不该弹错
                if (error is not null)
                {
                    OnExecuteFailed?.Invoke(error);
                }

                return;
            }

            text = nextText;
            if (text.Length == 0)
            {
                // trailing 期间文本被清空：直接本地回调空结果，不再进管道
                OnEmptyText?.Invoke(new QueryTicket(NextGeneration(), ""));
                lock (_gate)
                {
                    _inFlight = false;
                }

                return;
            }
        }
    }

    private long NextGeneration() => Interlocked.Increment(ref _generation);
}
