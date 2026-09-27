// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Contracts;

/// <summary>
/// 面板 <c>input</c> 节点的**输入节流器**（协议 §3.7.3，V1.3 第 4 步）。
///
/// <b>它解决什么问题</b>：用户每次按键都会产生一次"值变了"。若每键都发一次
/// <c>tool.panel.data</c>，打字快的人一秒能打出 8~10 次进程往返 —— 工具还没回完
/// 上一次，下一次就到了（协议 §3.7.5 的"请求堆积"）。节流把物理按键收敛成
/// "用户停下来了"与"连打时的有节奏更新"两类时机。
///
/// <b>为什么放在契约层而不是窗口里</b>：协议 §12.2 第 4 步原话 ——
/// "纯逻辑，可用假时钟测；**不涉及窗口**"。放窗口里就只能开 GUI 才测得到 21.7，
/// 而 21.7 恰恰是这一节唯一的自动化判据。放这里还能被 selftest 直接单测。
///
/// <b>两个动作与两种收敛，别混淆</b>：
/// <list type="number">
/// <item><b>最短间隔（min interval，80 ms）</b>：距上次真发不足 80 ms 的新变更**排队**；
///   已满 80 ms 的变更**立即放行**。这是连打时的"更新节奏"来源 —— 没有它，
///   纯防抖实现下连续打字会让面板**一直不更新**（每次按键都重置防抖）。</item>
/// <item><b>防抖（debounce，150 ms）</b>：排队的变更要等"最后一次变更后静置 150 ms"
///   才由 <see cref="Fire"/> 放行。这是"停顿后发最终值"的来源。</item>
/// </list>
/// 合起来 = 协议 §3.7.3 的"防抖窗口 150 ms / 最短间隔 80 ms / 请求合并"。
///
/// <b>API 形状（为什么返回元组而不是 bool + Take）</b>：
/// 第一版是 <c>bool Report()/Fire()</c> + <c>Take()</c>，结果 selftest 当场抓到一个
/// 设计缺陷：<c>Fire()</c> 返回 true 时内部已把待发值清掉，随后的 <c>Take()</c> 拿到
/// null —— "放行"与"取值"分成两步，所有权就不清楚（忘调 Take / 双重派发都是
/// 这个形状的衍生 bug）。改成**放行即交付**：返回非 null = 值已经给你了、待发态已清，
/// 调用方没有任何"记得做"的事。这正是本项目"把契约做成结构性事实，而不是纪律"的惯例。
///
/// <b>返回的元组是"触发"，不是"载荷"</b>：WPF 侧拿到放行信号后，应把**窗口上全部
/// 输入框的当前值**（值缓存）作为 <c>inputs</c> 快照发出 —— 而不是只发这一个元组。
/// 原因：面板上可能有多个 <c>input</c>，交替打字时待发位只有一个（最后的变更），
/// 早先 key 的触发被覆盖是**预期行为**（合并），它的当前值由快照照常带上。
///
/// <b>为什么不用依赖注入框架</b>：只有一个可变量（时钟）。抽成 <c>Func&lt;long&gt;</c>
/// 就够 —— 引入容器是为"尚未存在的多个实现"设计。
///
/// <b>线程模型</b>：本类**不加锁**，调用方负责串行化。
/// WPF 侧的调用点全在 UI 线程（<c>TextBox.TextChanged</c> / <c>DispatcherTimer</c>），
/// 天然串行；CLI/selftest 是单线程。加锁会给人"可以随便并发调"的错觉 ——
/// 而它内部有"重置计时 + 记最后值"这种**非原子**的复合状态。
/// </summary>
public sealed class InputThrottle
{
    // ── 协议硬约束（§3.7.3 表格）──────────────────────────────────────────────
    //
    // 声明成 public const 而不是私有魔数：selftest 要引用它们（21.7 的判据里
    // 含"有下限"这条）。若测试里再写一遍 150/80，改协议时就只改了一处 ⇒
    // 测试与实现**各自正确但不再匹配**（本项目在 21.1 已踩过一次"断言工具
    // 与实现的格式必须同时对"的坑）。

    /// <summary>防抖窗口：待发变更要静置这么久才由 <see cref="Fire"/> 放行（协议 §3.7.3）。</summary>
    public const int DebounceMs = 150;

    /// <summary>最短间隔：两次真发之间至少隔这么久（协议 §3.7.3）。</summary>
    public const int MinIntervalMs = 80;

    private readonly Func<long> _nowMs;        // 可注入时钟（毫秒，单调）。测试用假时钟。
    private readonly Action<long>? _schedule;  // 可选的"到点回调"调度（WPF 用 DispatcherTimer 实现）
    private readonly int _debounceMs;          // 见构造参数的说明（**按链路可覆盖**）
    private readonly int _minIntervalMs;

    private string? _pendingKey;          // 待发值属于哪个 key（null = 无待发）
    private string _pendingValue = "";    // 最后一次变更的**值**（合并：中途值被覆盖）
    private long _pendingAtMs;            // 该待发值**最后一次**变更的时刻（防抖计时基准）

    // ⚠️ 用 `long?` 而不是 `long.MinValue` 当"从未发过"的哨兵 —— 后者会让
    // `now - _lastDispatchMs` **整数回绕**（`0 - long.MinValue` 溢出成负数），
    // 于是时钟起点为 0 时"距上次真发已满 80ms"反向成立，首发被误判成"间隔不够"。
    // 实测：selftest 的合并用例当场红（详见本日日志）。`null` 表达"从未发过"
    // 无歧义且无算术陷阱。
    private long? _lastDispatchMs;

    // 是否已把"到点回调"交给调度器（去重用 —— Report 与 Fire 都可能想 Arm，
    // 不去重会让调度器堆积一串同一时刻的回调，数量随打字量线性增长）。
    private bool _timerArmed;

    /// <summary>调用方**想要**发送的次数（每次值变更 +1）。21.7 的分母。</summary>
    public int Requested { get; private set; }

    /// <summary>真正**放行**的次数。21.7 的分子 —— 快速连打时必须 &lt; <see cref="Requested"/>。</summary>
    public int Dispatched { get; private set; }

    /// <summary>是否有待发的变更（调用方可用于显示"正在输入…"之类的状态）。</summary>
    public bool HasPending => _pendingKey is not null;

    /// <param name="nowMs">
    /// 取当前单调毫秒的委托。默认 <see cref="Environment.TickCount64"/>（进程启动起算，
    /// 不受系统时间被改影响 —— 用 <c>DateTime.Now</c> 会在用户改表时倒退，节流失效）。
    /// 测试传假时钟即可**不睡觉**地推进时间。
    /// </param>
    /// <param name="schedule">
    /// 可选的"延迟回调"调度器：<c>schedule(delayMs)</c> 表示"delayMs 之后来调一次
    /// <see cref="Fire"/>"。传 null = 纯被动模式（调用方自己按节奏轮询 <see cref="Fire"/>）。
    /// <b>为什么留着这个钩子而不直接依赖 DispatcherTimer</b>：契约层不能引用 WPF。
    /// </param>
    /// <param name="debounceMs">
    /// 防抖窗口覆盖值。默认 = <see cref="DebounceMs"/>（150 ms，**面板 <c>input</c> 节点的协议值**）。
    /// <b>为什么可覆盖</b>：150/80 这组数是按"一次 <c>tool.panel.data</c> 进程往返 + 面板重绘"的
    /// 代价定的。**搜索窗走的是另一条链路**（原生 WPF 直连索引进程 stdio，没有面板那一跳），
    /// 设计方案 §6.2 给同一条链路写的就是 30 ms —— 复用这个类是对的，
    /// 但**常量取值必须跟着链路重新论证**，不能顺手抄（详见 <c>SearchSession.DebounceMs</c> 的注释）。
    /// </param>
    /// <param name="minIntervalMs">最短间隔覆盖值。默认 = <see cref="MinIntervalMs"/>（80 ms）。</param>
    public InputThrottle(
        Func<long>? nowMs = null,
        Action<long>? schedule = null,
        int debounceMs = DebounceMs,
        int minIntervalMs = MinIntervalMs)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _schedule = schedule;
        _debounceMs = Math.Max(debounceMs, 0);
        _minIntervalMs = Math.Max(minIntervalMs, 0);
    }

    /// <summary>
    /// 上报一次"某个 <c>key</c> 的值变了"。
    ///
    /// 返回非 null = **立刻把这个值发出去**（最短间隔已满足 —— 通常是"距上次真发
    /// 已满 80 ms"，或者这是本会话第一次）；返回 null = 已排入节流窗口，
    /// 到点后 <see cref="Fire"/> 会交付它。
    ///
    /// 无论返回什么，**待发位总是记录最后一次的值**（合并语义：
    /// 窗口内再变 4 次，最终只有最后一个值会被交付 —— 协议 §3.7.3）。
    /// </summary>
    public (string Key, string Value)? Report(string key, string value)
    {
        Requested++;

        var now = _nowMs();
        _pendingKey = key;
        _pendingValue = value ?? "";
        _pendingAtMs = now;

        // 最短间隔满足 ⇒ 立即放行，不必再等防抖（防抖的目的是"等停顿后的最终值"，
        // 而此刻距上次真发已满 80 ms —— 对连打场景这就是协议要的更新节奏；
        // 停顿后的最终值由下一次变更或 Fire 兜底）。
        if (_lastDispatchMs is null || now - _lastDispatchMs.Value >= _minIntervalMs)
        {
            return Dispatch(now);
        }

        ArmTimer(delayOverrideMs: null, force: false);
        return null;
    }

    /// <summary>
    /// 试着放行一次待发变更。由调度器（<paramref name="schedule"/> 注册的到点回调）
    /// 或被动轮询的调用方调用。
    ///
    /// 返回非 null = 现在就发这个值；null = 防抖还没走完 / 最短间隔未到 / 无待发。
    /// </summary>
    public (string Key, string Value)? Fire()
    {
        if (_pendingKey is null)
        {
            return null;
        }

        var now = _nowMs();

        // 防抖窗口还没走完 ⇒ 不发。这一条是 21.7 的关键：若不比 _pendingAtMs
        // 而直接发，"防抖"就退化成"立刻发"，且**看起来还能用**（值最终是对的）——
        // 只有"变更后马上轮询"的用例才抓得到。典型静默失效。
        if (now - _pendingAtMs < _debounceMs)
        {
            // force：调度器若因计时精度提前触发，必须允许重新登记，
            // 否则待发值会卡到下一次 Report 才有机会放行。
            ArmTimer(_pendingAtMs + _debounceMs - now, force: true);
            return null;
        }

        // 最短间隔未到 ⇒ 推迟到"上次真发 + minInterval"。
        //
        // ⚠️ 2026-09-25（G2 参数归位）后这条**不再是纯防御性**：
        //   面板 input 节点是 Debounce 150 > MinInterval 80，此时 pendingAt ≥ lastDispatch
        //   ⇒ 防抖过闸必然也过了最短间隔。
        //   但**搜索窗**用 Debounce 30 < MinInterval 80（设计 §6.2）⇒ 大小关系反转，
        //   防抖过闸**不**保证最短间隔已满足，这条闸真的会拦住放行
        //  （典型：变更落在"上次真发 + 50~80 ms"这个窗口内 ⇒ 防抖先过、最短间隔后过）。
        //   谁再改这两个值，先看这里 —— 它是两个配置共用的同一条路径。
        if (_lastDispatchMs is long last && now - last < _minIntervalMs)
        {
            ArmTimer(last + _minIntervalMs - now, force: true);
            return null;
        }

        return Dispatch(now);
    }

    /// <summary>
    /// 清空全部状态（窗口失焦 / 关闭时调用，协议 §3.7.4 的 F2）。
    ///
    /// <b>为什么失焦要连"待发"一起清</b>：否则用户打了字、切走窗口、节流器到点
    /// <see cref="Fire"/> ⇒ 会在窗口**未聚焦**时交付一次用户输入 ——
    /// 正是 F1 禁止的"后台面板持续上报"。清空后 Fire 因无待发直接返回 null。
    ///
    /// <b>计数不清</b>：它们是本次会话的累计观测值（21.7 的判据），清了数字会漂移。
    /// <b>lastDispatch 一并清</b>：失焦重开后视为全新会话，下一次输入立即放行。
    /// </summary>
    public void Reset()
    {
        _pendingKey = null;
        _pendingValue = "";
        _timerArmed = false;
        _lastDispatchMs = null;
    }

    /// <summary>放行：记时刻、计数、**交付并清空待发态**（返回值即交付物）。</summary>
    private (string Key, string Value) Dispatch(long now)
    {
        _lastDispatchMs = now;
        _timerArmed = false;
        Dispatched++;

        var delivered = (_pendingKey!, _pendingValue);
        _pendingKey = null;
        _pendingValue = "";
        return delivered;
    }

    /// <summary>
    /// 把"下次该什么时候来 <see cref="Fire"/>"交给调度器。
    /// <paramref name="force"/> 供 <see cref="Fire"/> 的重试路径用
    /// （调度器提前触发时必须允许重新登记，否则待发值会卡住）。
    /// </summary>
    private void ArmTimer(long? delayOverrideMs, bool force)
    {
        if (_schedule is null || (_timerArmed && !force))
        {
            return;
        }

        _timerArmed = true;
        _schedule(delayOverrideMs ?? _debounceMs);
    }
}
