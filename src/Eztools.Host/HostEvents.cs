namespace Eztools.Host;

/// <summary>
/// 宿主事件的种类（P4 Wave 2a，决策 D2：**仅内部事件，不开放给工具订阅**）。
///
/// <b>为什么不做成开放总线</b>：设计方案 §12 的 P4 清单只写了"宿主事件总线"五个字——
/// 无方法名、无订阅语义、无载荷 schema。当前**没有任何工具**需要订阅宿主事件，
/// 造一套 `host.events.subscribe` + 弱类型载荷总线 = 造一个人人都可能用错的抽象（YAGNI）。
/// 而"UI 要刷新但只能手动点刷新"是**已存在**的痛点（托盘菜单里有"刷新菜单"项），
/// 所以本层只解决后者：宿主状态变了 → 通知宿主自己的 UI。
/// </summary>
public enum HostEventKind
{
    /// <summary>工具被启用/禁用（含熔断自动禁用）。</summary>
    ToolEnabledChanged,

    /// <summary>工具被熔断隔离（崩溃超阈值自动禁用）。</summary>
    ToolQuarantined,

    /// <summary>工具进程已启动（resident 预热完成、或某个调用刚拉起它）。</summary>
    ToolStarted,

    /// <summary>工具进程已退出（正常回收、崩溃、超时、或宿主关停）。</summary>
    ToolStopped,

    /// <summary>工具清单被重扫（新增/移除工具目录、或状态文件被外部改动）。</summary>
    RegistryReloaded,

    /// <summary>运行时（Python 等）部署/失效状态变化。</summary>
    RuntimeChanged,
}

/// <summary>
/// 一条宿主事件。设计成一个**统一载荷**而不是每种事件一个 record：
/// 订阅方（托盘/设置窗口）关心的是"有东西变了，该刷新哪一块"，而不是解析每种事件的细节字段；
/// 细节一律走 <see cref="Detail"/>（可空字符串），既保住可读性又不用为每种事件定义 schema ——
/// 这正是"不做弱类型总线"与"又要能带信息"之间的中间点。
/// </summary>
/// <param name="Kind">事件种类。</param>
/// <param name="ToolId">相关工具 id（清单级事件如 <c>RegistryReloaded</c> 时为 null）。</param>
/// <param name="Detail">人类可读的补充说明（日志与调试用，不承诺机器可解析）。</param>
/// <param name="At">事件发生时刻（本地时间，与日志口径一致）。</param>
public sealed record HostEvent(
    HostEventKind Kind,
    string? ToolId = null,
    string? Detail = null,
    DateTimeOffset? At = null)
{
    public DateTimeOffset Timestamp => At ?? DateTimeOffset.Now;

    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff} {Kind}{(ToolId is null ? "" : $" [{ToolId}]")}" +
        (string.IsNullOrEmpty(Detail) ? "" : $" — {Detail}");
}

/// <summary>
/// 宿主内部事件总线（P4 Wave 2a，决策 D2）。
///
/// <b>语义</b>：单进程内、宿主 → 宿主 UI 的单向通知。**工具不能订阅**（无 IPC 面、无契约字段）。
///
/// <b>投递模型</b>：**同步、按订阅顺序、逐个调用**。理由：
/// <list type="bullet">
/// <item>事件源（进程管理、状态存储）都在**后台线程**上，同步投递不会阻塞 UI 线程；</item>
/// <item>订阅方（托盘）自己决定要不要 marshal —— 强制异步投递会引入"事件乱序"这种新问题；</item>
/// <item>本层不处理订阅者抛异常：**一个订阅者坏了不该让事件源跟着坏**，所以逐订阅者 try/catch，
///   异常只写日志（<see cref="HostLog"/> 可空 —— 早期构造阶段还没有日志对象时静默吞掉）。</item>
/// </list>
///
/// <b>订阅/退订必须成对</b>：提供 <see cref="Subscribe"/> 返回 <see cref="IDisposable"/>，
/// 而不是裸 <c>+=</c>/<c>-=</c> —— 后者在 UI 重建时最容易漏退订（托盘 Dispose 时若漏了，
/// 下次宿主的同一个总线会重复通知已死的菜单对象）。见 §23 的踩坑记录。
/// </summary>
public sealed class HostEventBus
{
    private readonly List<Subscription> _subscriptions = new();
    private readonly Lock _gate = new();
    private HostLog? _log;

    /// <summary>当前订阅者数量（诊断/自检用）。</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>
    /// 绑定日志（宿主装配完成后调用）。允许为空：早期构造阶段还没日志对象。
    /// 订阅者抛异常时若日志为空则静默吞掉 —— **绝不向上抛**，否则事件源会被一个坏订阅者拖垮。
    /// </summary>
    internal void AttachLog(HostLog log) => _log = log;

    /// <summary>订阅：返回的 <see cref="IDisposable"/> 释放即退订（用 <c>using</c> 保证成对）。</summary>
    public IDisposable Subscribe(Action<HostEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var sub = new Subscription(this, handler);
        lock (_gate)
        {
            _subscriptions.Add(sub);
        }

        return sub;
    }

    /// <summary>发布一条事件。任何订阅者抛异常都会被隔离（记日志后继续下一个）。</summary>
    public void Publish(HostEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // 快照后再投递：订阅者在回调里退订/新订阅都不该让本次投递出现
        // "集合被修改"（那样事件源就得替订阅方的行为买单）。
        Subscription[] snapshot;
        lock (_gate)
        {
            snapshot = _subscriptions.ToArray();
        }

        foreach (var sub in snapshot)
        {
            try
            {
                sub.Handler(evt);
            }
            catch (Exception ex)
            {
                // 一个订阅者坏了不该让事件源跟着坏 —— 这是本层最重要的稳妥性承诺。
                _log?.Warn($"宿主事件订阅者抛异常（已隔离，事件 {evt.Kind}）：{ex.Message}", "events");
            }
        }
    }

    /// <summary>便捷重载。</summary>
    public void Publish(HostEventKind kind, string? toolId = null, string? detail = null) =>
        Publish(new HostEvent(kind, toolId, detail));

    private void Unsubscribe(Subscription sub)
    {
        lock (_gate)
        {
            _subscriptions.Remove(sub);
        }
    }

    private sealed class Subscription(HostEventBus bus, Action<HostEvent> handler) : IDisposable
    {
        private int _disposed;

        internal Action<HostEvent> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                bus.Unsubscribe(this);
            }
        }
    }
}
