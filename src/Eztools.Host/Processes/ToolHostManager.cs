using System.Text.Json;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host.Config;
using Eztools.Host.Primitives;
using Eztools.Host.Registry;
using Eztools.Host.Runtimes;

namespace Eztools.Host.Processes;

/// <summary>进程治理策略。</summary>
public sealed class ToolHostOptions
{
    /// <summary>
    /// 空闲回收时间。0 表示"用完即收"（transient 语义，CLI 用）；
    /// 设成正值则进程在空闲该时长后被回收（将来的 GUI 常驻宿主用，避免每次调用都付冷启动）。
    /// </summary>
    public TimeSpan IdleRecycle { get; init; } = TimeSpan.Zero;

    /// <summary>进程启动 + initialize 预算。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>是否启用崩溃熔断（自动禁用反复崩溃的工具）。</summary>
    public bool EnableCircuitBreaker { get; init; } = true;

    public static ToolHostOptions Default { get; } = new();

    public static ToolHostOptions ForCli { get; } = new() { IdleRecycle = TimeSpan.Zero };
}

/// <summary>resident 工具的运行状态快照（托盘状态展示用，只读）。</summary>
public sealed record ResidentSnapshot(string ToolId, bool Running, int? Pid, string State, int CrashCount);

/// <summary>工具被熔断隔离时的事件载荷。</summary>
public sealed record ToolQuarantinedEvent(string ToolId, int CrashCount, TimeSpan Window, string Reason);

/// <summary>
/// 工具请求弹通知（<c>host.notify</c>）的事件载荷。
/// <c>ToolName</c> 是清单里的显示名，<c>Title</c> 是工具自己给的气泡标题（两者可不同）。
/// </summary>
public sealed record ToolNotificationEvent(string ToolId, string ToolName, string Title, string Body);

/// <summary>
/// 一次面板数据拉取的结果（P4 Wave 2c）。
/// <see cref="Data"/> 是工具原样返回的载荷（约定为 <c>{nodes:[...]}</c>），
/// **格式校验在渲染层做**：宿主刻意不在这里把非法载荷转成异常 ——
/// 面板出问题只该显示一条提示，不该让宿主调用链崩掉（协议 §3.5）。
/// </summary>
/// <param name="ToolId">工具 id。</param>
/// <param name="PanelId">面板 id（规范化后）。</param>
/// <param name="Panel">清单里声明的面板（含尺寸等渲染参数）。</param>
/// <param name="Data">工具返回的原始载荷（可能为 null —— 工具返回了 null）。</param>
/// <param name="Elapsed">耗时（展示在面板状态栏）。</param>
public sealed record PanelDataResult(
    string ToolId,
    string PanelId,
    ToolPanel Panel,
    JsonNode? Data,
    TimeSpan Elapsed);

/// <summary>
/// 工具进程管理（P0 的第三件核心交付）。
///
/// 语义要点（对齐设计方案 §6.1 / §6.5 / §8.1）：
/// <list type="bullet">
/// <item><b>"已启用" ≠ "已加载"</b>：禁用只阻止启动，不影响清单是否注册。</item>
/// <item><b>崩溃熔断</b>：默认 10 分钟 3 次；<c>resident</c> 收紧为 5 分钟 2 次。触发后自动禁用该工具并记录原因，
///   而不是让用户每次调用都撞一次崩溃。</item>
/// <item><b>进程回收</b>：取消/超时/崩溃后不留僵尸（kill 整个进程树）。</item>
/// </list>
/// </summary>
public sealed class ToolHostManager : IAsyncDisposable
{
    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;
    private readonly ToolRegistry _registry;
    private readonly RuntimeRegistry _runtimes;
    private readonly ToolHostOptions _options;
    private readonly ToolStateStore _stateStore;
    private readonly ConfigStore _configs;

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _crashHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _perToolGate = new(1, 1);

    /// <summary>
    /// 工具间嵌套调用的闸门（P4）。**必须与 <see cref="_perToolGate"/> 分开**：
    /// 顶层调用持有 <c>_perToolGate</c> 直到工具返回，而嵌套调用正是在那之前发起的；
    /// 共用一个信号量就是三方死锁（宿主等工具 → 工具等宿主 → 宿主等闸门）。
    /// 见 docs/P4-实施方案.md §2.1。
    /// </summary>
    private readonly SemaphoreSlim _nestedGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _sweeper;
    private readonly PrimitiveClient _primitives;
    private int _disposed;

    public ToolHostManager(
        EztoolsPaths paths,
        HostLog log,
        ToolRegistry registry,
        RuntimeRegistry runtimes,
        ToolStateStore stateStore,
        ConfigStore configs,
        ToolHostOptions? options = null,
        HostEventBus? events = null)
    {
        _paths = paths;
        _log = log;
        _registry = registry;
        _runtimes = runtimes;
        _stateStore = stateStore;
        _configs = configs;
        _options = options ?? ToolHostOptions.Default;
        _events = events;
        _primitives = new PrimitiveClient(paths, log);
        _sweeper = Task.Run(SweepLoopAsync);
    }

    /// <summary>
    /// 宿主内部事件总线（P4 Wave 2a）。**可空** —— 单元测试与旧调用点不传时静默不发事件，
    /// 而不是强制所有构造点都改（那会让"事件总线"变成装配负担）。
    /// </summary>
    private readonly HostEventBus? _events;

    /// <summary>工具被熔断隔离时触发（宿主可据此弹通知；P0 只记录日志）。</summary>
    /// <remarks>
    /// 与 <see cref="HostEventBus"/> 并存而非取代：这是**强类型单事件**，订阅方（宿主自身）需要
    /// <see cref="ToolQuarantinedEvent"/> 的窗口/次数等细节；事件总线是**给 UI 的统一刷新信号**。
    /// 两者面向的消费方不同，合并会让任一方被迫解析不需要的 schema。
    /// </remarks>
    public event Action<ToolQuarantinedEvent>? ToolQuarantined;

    /// <summary>
    /// 工具请求弹出通知（<c>host.notify</c>）时触发。返回值 = **气泡是否真的显示出来了**，
    /// 会原样回给工具（<c>delivered</c> 字段）。
    ///
    /// <b>为什么用返回值而不是"订阅了就当作送达"</b>：托盘可能因为系统通知设置而静默丢弃气泡
    /// （她实测踩到），所以"有订阅者"和"用户看到了"是两件事。只有 UI 宿主自己知道弹没弹成。
    ///
    /// 没有订阅者 = 无 UI 宿主（CLI）→ <c>delivered:false</c>，文案说明是"没有 UI"而不是"没实现"。
    /// </summary>
    public event Func<ToolNotificationEvent, bool>? NotifyRequested;

    /// <summary>当前活着的工具进程数。</summary>
    public int LiveProcessCount
    {
        get
        {
            lock (_sessions)
            {
                return _sessions.Count(s => !s.Value.Process.HasExited);
            }
        }
    }

    /// <summary>
    /// 按命令 id 调用工具。命令 id 由注册表解析到具体工具 —— 调用方不需要知道工具是谁，
    /// 这正是"新增工具零宿主改动"的另一半保证。
    /// </summary>
    public async Task<ToolCallResult> InvokeCommandAsync(
        string commandId,
        JsonNode? args,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!_registry.TryResolveCommand(commandId, out var binding))
        {
            throw new ToolProtocolException(
                $"未找到命令 '{commandId}'。可用命令见 `ezt list --commands`。",
                RpcErrorCodes.UnknownCommand);
        }

        var effective = timeout
                        ?? (binding.Command.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null)
                        ?? binding.Tool.Manifest.Weight.DefaultTimeout();

        return await InvokeAsync(binding.Tool, commandId, binding.Command.Handler, args, effective, ct)
            .ConfigureAwait(false);
    }

    /// <summary>按 (工具, handler) 直接调用（调试路径：<c>ezt call</c>）。</summary>
    public async Task<ToolCallResult> InvokeHandlerAsync(
        string toolId,
        string handler,
        JsonNode? args,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!_registry.TryGetTool(toolId, out var tool))
        {
            throw new ToolProtocolException($"未找到工具 '{toolId}'", RpcErrorCodes.UnknownCommand);
        }

        var effective = timeout ?? tool.Manifest.Weight.DefaultTimeout();
        return await InvokeAsync(tool, null, handler, args, effective, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 拉取某工具某个面板的数据（P4 Wave 2c，<c>tool.panel.data</c>）。
    ///
    /// <b>为什么走 <see cref="InvokeAsync"/> 而不是像 <c>invokeTool</c> 那样另开一条路</b>：
    /// 面板拉取的发起方是**宿主 UI**（托盘窗口 / CLI），不是另一个工具 ——
    /// 所以它在调用链的**顶端**，天然可以取 <c>_perToolGate</c>（不存在"链上的人回头取闸"的死锁形状）。
    /// 复用主路径还白拿了崩溃熔断、超时回收、启用/禁用校验。
    ///
    /// <b>两个档位/能力闸门放在这里（发起侧）</b>，而不是在 <c>HandleToolRequestAsync</c>：
    /// <c>EnsureApiAllowed</c> 管的是"工具 → 宿主"方向的白名单；本方法方向相反（宿主 → 工具），
    /// 它进不了那条闸门，所以必须自己检查。
    /// </summary>
    /// <param name="toolId">目标工具。</param>
    /// <param name="panelId">面板 id（对应清单 <c>contributes.panels[].id</c>）。</param>
    /// <param name="reason">拉取原因：<c>open</c> / <c>refresh</c> / <c>host-event</c>。</param>
    public async Task<PanelDataResult> PanelDataAsync(
        string toolId,
        string panelId,
        string reason = "open",
        CancellationToken ct = default)
    {
        if (!_registry.TryGetTool(toolId, out var tool))
        {
            throw new ToolProtocolException(
                $"未找到工具 '{toolId}'。可用工具见 `ezt list`。", RpcErrorCodes.UnknownCommand);
        }

        // ── 闸门 ①：档位必须是 full（与清单解析期的 Error 同一条规则，这里是运行期的第二道）──
        // 解析期拒绝的清单不该走到这里；但运行时对象可能来自旧注册表/手动构造，所以双保险。
        if (tool.Manifest.Weight != ToolWeight.Full)
        {
            throw new ToolRpcException(
                RpcErrorCodes.WeightNotPermitted,
                $"面板仅对 weight: full 的工具可用 —— 当前 {toolId} 是 {tool.Manifest.Weight.ToWire()} 档。"
                + "把 tool.json 的 weight 改成 \"full\" 即可。");
        }

        // ── 闸门 ②：该面板必须真的声明过（防止拉一个不存在的面板 id 得到空结果而误以为工具坏了）──
        var panel = tool.Manifest.Contributes.Panels
            .FirstOrDefault(p => string.Equals(p.Id, panelId, StringComparison.OrdinalIgnoreCase));

        if (panel is null)
        {
            var known = tool.Manifest.Contributes.Panels.Select(p => p.Id).ToArray();
            throw new ToolRpcException(
                RpcErrorCodes.InvalidParams,
                known.Length == 0
                    ? $"工具 {toolId} 没有声明任何面板（contributes.panels）"
                    : $"工具 {toolId} 没有声明面板 '{panelId}'。已声明的面板：{string.Join(" / ", known)}");
        }

        var args = new JsonObject
        {
            ["panelId"] = panel.Id,
            ["context"] = new JsonObject { ["reason"] = reason },
        };

        // 超时：沿用 full 档默认（120s），除非该工具进程层另配。
        var call = await InvokeAsync(
                tool, null, PanelDataHandler, args, tool.Manifest.Weight.DefaultTimeout(), ct)
            .ConfigureAwait(false);

        return new PanelDataResult(
            ToolId: tool.Id,
            PanelId: panel.Id,
            Panel: panel,
            Data: call.Result,
            Elapsed: call.Elapsed);
    }

    /// <summary>
    /// 面板数据 handler 的固定名（协议 §8.1：一个 handler 用 <c>args.panelId</c> 分派，
    /// 不是每个面板一个 handler）。
    ///
    /// 走 <c>tool.invoke</c> 的 <c>handler</c> 形态（而不是新加一个协议方法的方向）——
    /// 这样 SDK 侧不需要为"面板拉取"再加一套派发，<c>_dispatch</c> 里的 <c>tool.panel.data</c>
    /// 分支处理的正是宿主发来的这个 handler 调用。
    /// </summary>
    public const string PanelDataHandler = "panel_data";

    private async Task<ToolCallResult> InvokeAsync(
        RegisteredTool tool,
        string? commandId,
        string handler,
        JsonNode? args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        EnsureCallable(tool);

        await _perToolGate.WaitAsync(ct).ConfigureAwait(false);
        ToolProcess? process = null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            process = await AcquireAsync(tool, ct).ConfigureAwait(false);

            JsonNode? result;
            try
            {
                result = commandId is not null
                    ? await process.InvokeCommandAsync(commandId, args, timeout, ct).ConfigureAwait(false)
                    : await process.InvokeHandlerAsync(handler, args, timeout, ct).ConfigureAwait(false);
            }
            catch (ToolCrashedException ex)
            {
                await HandleCrashAsync(tool, ex).ConfigureAwait(false);
                throw;
            }
            catch (ToolTimeoutException)
            {
                await HandleTimeoutAsync(tool).ConfigureAwait(false);
                throw;
            }

            return new ToolCallResult
            {
                Result = result,
                Raw = result,
                Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started),
            };
        }
        finally
        {
            await ReleaseAsync(tool.Id).ConfigureAwait(false);
            _perToolGate.Release();
        }
    }

    private void EnsureCallable(RegisteredTool tool)
    {
        if (tool.LoadState == ToolLoadState.Quarantined)
        {
            throw new ToolDisabledException(
                $"工具 {tool.Id} 因反复崩溃已被宿主自动禁用（{tool.DisabledReason ?? "达到崩溃阈值"}）。"
                + $"用 `ezt enable {tool.Id}` 重新启用。");
        }

        if (!tool.Enabled)
        {
            throw new ToolDisabledException(
                $"工具 {tool.Id} 处于禁用状态（{tool.DisabledReason ?? "用户禁用"}）。用 `ezt enable {tool.Id}` 启用。");
        }
    }

    /// <summary>
    /// 把最新配置推给**正在运行**的工具进程。
    ///
    /// 生效规则（**必须写进文档**，否则会出现"我改了配置怎么不生效"的困惑）：
    /// <list type="bullet">
    /// <item><c>transient</c> 工具**不需要推** —— 每次调用都重起进程，天然拿到新值</item>
    /// <item><c>resident</c> 工具**必须推** —— 进程一直活着，不推就永远用旧值</item>
    /// </list>
    ///
    /// 进程没在跑时返回 <c>false</c>（**不是错误**）：下次启动自然会读到新配置。
    /// P1a 只交付这条通路；真正跑到它要有 <c>resident</c> 工具，那是 P2 的事。
    /// </summary>
    public async Task<bool> PushConfigAsync(string toolId, CancellationToken ct = default)
    {
        if (!_registry.TryGetTool(toolId, out var tool))
        {
            return false;
        }

        ToolProcess process;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(toolId, out var session) || session.Process.HasExited)
            {
                return false;
            }

            process = session.Process;
        }

        var config = _configs.Effective(toolId, tool.Manifest.ConfigSchema);
        try
        {
            await process.CallAsync(
                ProtocolMethods.ToolOnConfigChanged,
                new JsonObject { ["config"] = config },
                TimeSpan.FromSeconds(5),
                ct).ConfigureAwait(false);

            _log.Info($"已向 {toolId} 推送配置变更", "config");
            return true;
        }
        catch (Exception ex)
        {
            // 推送失败不该让调用方失败 —— 工具可能正好在退出，下次启动会拿到新值
            _log.Warn($"向 {toolId} 推送配置失败（进程可能已退出）：{ex.Message}", "config");
            return false;
        }
    }

    /// <summary>
    /// 取得（必要时启动）工具进程。
    /// </summary>
    /// <param name="chain">
    /// 本次调用所属的工具间调用链；<c>null</c> = 顶层调用，链即 <c>[该工具]</c>。
    /// 嵌套调用传入"父链 + 目标工具"，供环检测使用（见 <see cref="Session.Chain"/>）。
    /// </param>
    private async Task<ToolProcess> AcquireAsync(RegisteredTool tool, CancellationToken ct, string[]? chain = null)
    {
        var effectiveChain = chain ?? new[] { tool.Id };

        lock (_sessions)
        {
            if (_sessions.TryGetValue(tool.Id, out var existing) && !existing.Process.HasExited)
            {
                existing.Leases++;
                existing.LastUsed = DateTimeOffset.Now;
                existing.Chain = effectiveChain;
                return existing.Process;
            }
        }

        var runtime = await _runtimes.GetAsync(tool.Manifest, null, ct).ConfigureAwait(false);
        if (runtime is null && ToolRuntimes.IsInterpreted(tool.Manifest.Runtime))
        {
            throw new ToolProtocolException(
                $"工具 {tool.Id} 所需运行时 {tool.Manifest.Runtime} 不可用，无法启动。"
                + "运行 `ezt runtime install` 查看部署指引。",
                RpcErrorCodes.ToolCrashed);
        }

        var options = new ToolProcessOptions
        {
            StartupTimeout = _options.StartupTimeout,
            CallTimeout = tool.Manifest.Weight.DefaultTimeout(),
            // 🔴 配置中心注入 —— 这是 P1a 补上的那一段。
            //    在此之前 options.Config 从来没人填，工具拿到的 tool.config **恒为 {}**，
            //    即使 tool.json 里写了 default 也拿不到。
            //    值 = 默认值 ⊕ 用户已存值（见 ConfigStore.Effective / ConfigValues.Merge）。
            Config = _configs.Effective(tool.Id, tool.Manifest.ConfigSchema),
        };

        var process = await ToolProcess.StartAsync(
            tool.Manifest,
            runtime,
            options,
            _log,
            (method, prms, _) => HandleToolRequestAsync(tool, method, prms),
            ct,
            // Job 分流（P2 生命周期）：transient/task 挂 kill-on-close Job（宿主死工具死）；
            // resident 不挂——宿主崩溃/强杀时常驻进程必须存活。
            attachKillOnCloseJob: tool.Manifest.Lifecycle != ToolLifecycle.Resident).ConfigureAwait(false);

        lock (_sessions)
        {
            _sessions[tool.Id] = new Session(process) { Chain = effectiveChain };
        }

        tool.LoadState = ToolLoadState.Running;
        tool.ProcessId = process.ProcessId;
        tool.StartedAt = process.StartedAt;
        _log.Info($"已启动工具 {tool.Id}（pid={process.ProcessId}，lifecycle={tool.Manifest.Lifecycle.ToWire()}）", "lifecycle");
        Publish(HostEventKind.ToolStarted, tool.Id, $"pid={process.ProcessId}");

        return process;
    }

    private async Task ReleaseAsync(string toolId)
    {
        // resident 工具**跳过即用即走回收**：进程保活供后续调用复用（pid 不变），
        // 只在宿主 Dispose（优雅退出）或崩溃/超时清理时终止。
        var isResident = _registry.TryGetTool(toolId, out var reg)
                         && reg!.Manifest.Lifecycle == ToolLifecycle.Resident;

        ToolProcess? toDispose = null;

        lock (_sessions)
        {
            if (!_sessions.TryGetValue(toolId, out var session))
            {
                return;
            }

            session.Leases = Math.Max(0, session.Leases - 1);
            session.LastUsed = DateTimeOffset.Now;

            // transient/task + IdleRecycle=0 → 即用即走；resident 常驻
            if (!isResident && session.Leases == 0 && _options.IdleRecycle <= TimeSpan.Zero)
            {
                _sessions.Remove(toolId);
                toDispose = session.Process;
            }
        }

        if (toDispose is not null)
        {
            var registered = _registry.TryGetTool(toolId, out var tool) ? tool : null;
            if (registered is not null && registered.LoadState == ToolLoadState.Running)
            {
                registered.LoadState = ToolLoadState.NotLoaded;
                registered.ProcessId = null;
            }

            await toDispose.DisposeAsync().ConfigureAwait(false);
            _log.Debug($"已回收工具 {toolId} 的进程", "lifecycle");
            Publish(HostEventKind.ToolStopped, toolId, "空闲回收");
        }
    }

    private async Task SweepLoopAsync()
    {
        if (_options.IdleRecycle <= TimeSpan.Zero)
        {
            return; // 即用即走模式无需扫描
        }

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var threshold = DateTimeOffset.Now - _options.IdleRecycle;
            List<string> expired = new();

            lock (_sessions)
            {
                foreach (var (id, session) in _sessions)
                {
                    if (session.Leases == 0 && session.LastUsed < threshold)
                    {
                        expired.Add(id);
                    }
                }
            }

            foreach (var id in expired)
            {
                ToolProcess? process = null;
                lock (_sessions)
                {
                    if (_sessions.TryGetValue(id, out var session) && session.Leases == 0)
                    {
                        _sessions.Remove(id);
                        process = session.Process;
                    }
                }

                if (process is not null)
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                    _log.Debug($"空闲回收工具 {id} 的进程", "lifecycle");
                }
            }
        }
    }

    // ── 崩溃熔断 ──

    private async Task HandleCrashAsync(RegisteredTool tool, ToolCrashedException ex)
    {
        RemoveSession(tool.Id);
        tool.CrashCount++;

        var (window, maxCrashes) = tool.Manifest.Lifecycle.CrashBudget();
        var now = DateTimeOffset.Now;

        Queue<DateTimeOffset> history;
        lock (_crashHistory)
        {
            if (!_crashHistory.TryGetValue(tool.Id, out history!))
            {
                history = new Queue<DateTimeOffset>();
                _crashHistory[tool.Id] = history;
            }

            history.Enqueue(now);
            while (history.Count > 0 && now - history.Peek() > window)
            {
                history.Dequeue();
            }
        }

        _log.Warn(
            $"工具 {tool.Id} 崩溃（exit={ex.ExitCode}），窗口内第 {history.Count}/{maxCrashes} 次", "lifecycle");
        Publish(HostEventKind.ToolStopped, tool.Id, $"崩溃退出 exit={ex.ExitCode}");

        if (!_options.EnableCircuitBreaker || history.Count < maxCrashes)
        {
            tool.LoadState = ToolLoadState.NotLoaded;

            // resident 自动重启（P2 决策 2）：崩溃不中断服务，熔断计数兜底防重启风暴。
            // 异步执行——此刻调用方还持有 _perToolGate，同步 Acquire 会死锁。
            if (tool.Manifest.Lifecycle == ToolLifecycle.Resident)
            {
                _log.Info($"resident 工具 {tool.Id} 安排自动重启", "lifecycle");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _perToolGate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (tool.Enabled && tool.LoadState != ToolLoadState.Quarantined)
                            {
                                // 用宿主关停令牌而不是 CancellationToken.None：
                                // 宿主退出时不该为了重启一个 resident 工具再等满一次启动超时。
                                await AcquireAsync(tool, _shutdown.Token).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _perToolGate.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"resident 工具 {tool.Id} 自动重启失败：{ex.Message}", "lifecycle");
                    }
                });
            }

            return;
        }

        var reason = $"{window.TotalMinutes:0} 分钟内崩溃 {history.Count} 次（阈值 {maxCrashes}）";
        tool.Enabled = false;
        tool.DisabledReason = reason;
        tool.LoadState = ToolLoadState.Quarantined;

        _stateStore.SetEnabled(tool.Id, false, "崩溃熔断：" + reason);
        _log.Error($"工具 {tool.Id} 已被熔断隔离：{reason}", "lifecycle");

        ToolQuarantined?.Invoke(new ToolQuarantinedEvent(tool.Id, history.Count, window, reason));
        Publish(HostEventKind.ToolQuarantined, tool.Id, reason);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Task HandleTimeoutAsync(RegisteredTool tool)
    {
        RemoveSession(tool.Id);
        if (tool.LoadState == ToolLoadState.Running)
        {
            tool.LoadState = ToolLoadState.NotLoaded;
        }

        tool.ProcessId = null;
        Publish(HostEventKind.ToolStopped, tool.Id, "调用超时");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 发一条宿主内部事件（P4 Wave 2a）。**只有 <see cref="_events"/> 存在时才发**——
    /// 单元测试与旧构造点不传总线时是空操作。
    /// </summary>
    private void Publish(HostEventKind kind, string? toolId, string? detail) =>
        _events?.Publish(kind, toolId, detail);

    private void RemoveSession(string toolId)
    {
        lock (_sessions)
        {
            _sessions.Remove(toolId);
        }
    }

    /// <summary>
    /// resident 工具的运行状态快照（托盘"常驻服务"菜单的数据源）。
    /// 线程安全：快照在锁内采集，调用方拿到的是不可变副本。
    /// </summary>
    public IReadOnlyList<ResidentSnapshot> GetResidentSnapshot()
    {
        lock (_sessions)
        {
            return _registry.Tools
                .Where(t => t.Manifest.Lifecycle == ToolLifecycle.Resident)
                .Select(t =>
                {
                    _sessions.TryGetValue(t.Id, out var session);
                    var running = session is not null && !session.Process.HasExited;
                    var state = t.LoadState == ToolLoadState.Quarantined ? "已熔断"
                        : !t.Enabled ? "已禁用"
                        : running ? "运行中"
                        : "已停止";
                    return new ResidentSnapshot(
                        t.Id,
                        running,
                        running ? session!.Process.ProcessId : null,
                        state,
                        t.CrashCount);
                })
                .ToList();
        }
    }

    // ── 生命周期：task / resident（P2 尾巴，方案 docs/P2-生命周期-实施方案.md）──

    /// <summary>
    /// 宿主启动钩子（P2 决策 1/3）：常驻宿主（托盘）启动时调用一次——
    /// <list type="bullet">
    /// <item><c>lifecycle: "task"</c>：执行清单 commands 的第一条命令，跑完即回收；</item>
    /// <item><c>lifecycle: "resident"</c>：拉起并保活（同宿主内跨调用复用同一进程）。</item>
    /// </list>
    /// CLI 一次性宿主<b>不应</b>调用本方法（task 语义属于常驻宿主；失败逐工具吞掉，不阻断其余）。
    /// </summary>
    public async Task RunStartupLifecycleAsync(CancellationToken ct = default)
    {
        foreach (var tool in _registry.Tools)
        {
            if (!tool.Enabled || tool.LoadState == ToolLoadState.Quarantined)
            {
                continue;
            }

            try
            {
                switch (tool.Manifest.Lifecycle)
                {
                    case ToolLifecycle.Task:
                    {
                        // 决策 1（简单版）：取 commands 第一条；schedule 触发留 P4
                        var first = tool.Manifest.Contributes.Commands.FirstOrDefault();
                        if (first is null)
                        {
                            _log.Warn($"task 工具 {tool.Id} 没有任何 commands，跳过", "lifecycle");
                            continue;
                        }

                        _log.Info($"task 工具 {tool.Id} 启动执行：{first.Id}", "lifecycle");
                        await InvokeCommandAsync(first.Id, null, ct: ct).ConfigureAwait(false);
                        _log.Info($"task 工具 {tool.Id} 执行完成", "lifecycle");
                        break;
                    }

                    case ToolLifecycle.Resident:
                        await _perToolGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await AcquireAsync(tool, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _perToolGate.Release();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                // 单个工具的启动失败不阻断其余（与清单发现"坏清单不拖垮别人"同一原则）
                _log.Warn($"生命周期启动钩子 {tool.Id} 失败：{ex.Message}", "lifecycle");
            }
        }
    }

    /// <summary>
    /// 向<b>正在运行</b>的 resident 实例发送 <c>tool.recover</c>（P2 决策 3 的宿主发送侧；
    /// 托盘 recover 入口即调用此方法）。进程没在跑返回 false（不是错误）。
    /// </summary>
    public async Task<bool> PushRecoverAsync(string toolId, CancellationToken ct = default)
    {
        if (!_registry.TryGetTool(toolId, out var tool))
        {
            return false;
        }

        ToolProcess process;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(toolId, out var session) || session.Process.HasExited)
            {
                return false;
            }

            process = session.Process;
        }

        try
        {
            await process.CallAsync(
                ProtocolMethods.ToolRecover,
                new JsonObject(),
                TimeSpan.FromSeconds(5),
                ct).ConfigureAwait(false);

            _log.Info($"已向 {toolId} 发送 tool.recover", "lifecycle");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"向 {toolId} 发送 tool.recover 失败：{ex.Message}", "lifecycle");
            return false;
        }
    }

    // ── 工具间调用（P4）──

    /// <summary>
    /// 调用**其他工具**的命令（<c>host.invokeTool</c> 的实现体）。
    ///
    /// <b>刻意不取 <c>_perToolGate</c></b>：顶层调用持有它直到工具返回，而本方法恰恰是在
    /// 那之前被调用的 —— 取它就等于三方死锁（宿主等工具、工具等宿主、宿主等闸门）。
    /// 死锁安全的依据是「调用链无环」：顶层调用被全局串行 ⇒ 链外工具必然空闲 ⇒ 环检测即充分，
    /// 因此**不做深度上限**（那是个永不可达的分支）。见 docs/P4-实施方案.md §2.2。
    /// </summary>
    /// <param name="callerToolId">发起调用的工具（由 <c>ToolProcess</c> 的回调带进来，不可由工具自报）。</param>
    public async Task<JsonObject> InvokeToolAsync(
        string callerToolId,
        string targetToolId,
        string commandId,
        JsonNode? args,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetToolId))
        {
            throw new ToolRpcException(RpcErrorCodes.InvalidParams, "host.invokeTool 需要非空 toolId");
        }

        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ToolRpcException(RpcErrorCodes.InvalidParams, "host.invokeTool 需要非空 commandId");
        }

        var chain = ChainOf(callerToolId);
        if (chain.Contains(targetToolId, StringComparer.OrdinalIgnoreCase))
        {
            var trace = string.Join(" → ", chain.Append(targetToolId));
            throw new ToolRpcException(
                RpcErrorCodes.InvokeCycle,
                $"工具间调用成环：{trace}。同一个工具不允许在一条调用链上出现两次 —— "
                + "宿主直接拒绝而不是等待，等待会死锁（见 docs/P4-实施方案.md §2）。");
        }

        if (!_registry.TryResolveCommand(commandId, out var binding))
        {
            throw new ToolRpcException(
                RpcErrorCodes.UnknownCommand, $"未找到命令 '{commandId}'。可用命令见 `ezt list`。");
        }

        // 归属校验：命令 id 的前缀只是命名习惯，不是约束（清单里可以写任意 id）。
        // 不校验的话，工具 A 能拿工具 B 的命令 id 调用 C，报错信息也会指错工具。
        if (!string.Equals(binding.Tool.Id, targetToolId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolRpcException(
                RpcErrorCodes.UnknownCommand,
                $"命令 '{commandId}' 属于工具 {binding.Tool.Id}，不是 {targetToolId}。");
        }

        EnsureCallable(binding.Tool);

        var nestedChain = chain.Append(binding.Tool.Id).ToArray();
        var timeout = binding.Command.TimeoutMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : binding.Tool.Manifest.Weight.DefaultTimeout();

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        await _nestedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var process = await AcquireAsync(binding.Tool, ct, nestedChain).ConfigureAwait(false);

            JsonNode? result;
            try
            {
                _log.Info(
                    $"工具间调用 {callerToolId} → {binding.Tool.Id}:{commandId}"
                    + $"（链路 {string.Join(" → ", nestedChain)}）",
                    "invoke");

                result = await process.InvokeCommandAsync(commandId, args, timeout, ct).ConfigureAwait(false);
            }
            catch (ToolCrashedException ex)
            {
                await HandleCrashAsync(binding.Tool, ex).ConfigureAwait(false);
                throw;
            }
            catch (ToolTimeoutException)
            {
                await HandleTimeoutAsync(binding.Tool).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await ReleaseAsync(binding.Tool.Id).ConfigureAwait(false);
            }

            return new JsonObject
            {
                // Clone 是必须的：从对方响应里取出的节点**挂在它的响应树上**，
                // 直接塞进新对象会抛 "The node already has a parent."（本项目反复踩的坑）。
                ["result"] = result?.Clone(),
                ["toolId"] = binding.Tool.Id,
                ["commandId"] = commandId,
                ["elapsedMs"] = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            };
        }
        finally
        {
            _nestedGate.Release();
        }
    }

    /// <summary>取某工具当前正在执行的调用链；它不在链上（自己有空时发起的调用）则返回空数组。</summary>
    private string[] ChainOf(string toolId)
    {
        lock (_sessions)
        {
            return _sessions.TryGetValue(toolId, out var session) ? session.Chain : Array.Empty<string>();
        }
    }

    /// <summary>
    /// <c>weight</c> 档位的 API 面闸门（设计方案 §5.1：<c>script</c> = "仅基础 API，无依赖，无设置界面"）。
    ///
    /// **白名单而非黑名单**：将来新增的 API 默认对 <c>script</c> 不可见（fail-closed），
    /// 避免"新 API 悄悄泄漏给最受限档位"。允许的基础 API = 日志 / 通知。
    /// </summary>
    private static void EnsureApiAllowed(RegisteredTool tool, string api)
    {
        if (tool.Manifest.Weight != ToolWeight.Script)
        {
            return;
        }

        if (api is ProtocolMethods.HostLog or ProtocolMethods.HostNotify)
        {
            return;
        }

        throw new ToolRpcException(
            RpcErrorCodes.WeightNotPermitted,
            $"{api} 对 weight: script 档不可用 —— 该档位按设计只提供基础 API（日志 / 通知），无依赖、无设置界面。"
            + "把 tool.json 的 weight 改成 \"lite\"（或 \"full\"）即可使用完整 API。");
    }

    /// <summary>
    /// <c>host.progress</c> 的档位闸门：设计方案 §5.3 明文限定"**仅 task**"。
    /// 该约束与 <c>weight</c> 正交（由生命周期决定，不是重量档位）。
    /// </summary>
    private static void EnsureProgressAllowed(RegisteredTool tool)
    {
        if (tool.Manifest.Lifecycle == ToolLifecycle.Task)
        {
            return;
        }

        throw new ToolRpcException(
            RpcErrorCodes.WeightNotPermitted,
            "host.progress 仅对 lifecycle: task 的工具开放（设计方案 §5.3）—— "
            + $"当前工具 {tool.Id} 的 lifecycle 是 {tool.Manifest.Lifecycle.ToWire()}。"
            + "进度只对宿主启动时执行一次的批处理任务有意义。");
    }

    // ── 工具 → 宿主 的方法实现 ──

    /// <summary>
    /// 处理 工具 → 宿主 的请求。
    /// <b>必须回复每一条</b>：工具侧是在 await 响应的，不回就等于让工具永久挂起。
    /// 未实现的方法返回 <c>MethodNotFound</c> 或 <c>NotSupported</c>，而不是静默忽略。
    /// </summary>
    private async Task<JsonNode?> HandleToolRequestAsync(RegisteredTool tool, string method, JsonNode? prms)
    {
        // host.ready 是"我已就绪"的**通知**（无 id），不是请求，先放行：
        // 否则它会被下面的档位闸门当成"未白名单的 API"而误伤。
        if (method == ProtocolMethods.HostReady)
        {
            return null;
        }

        // 档位闸门放在 switch 之前，而不是每个 case 里各补一行：
        // 将来新增的 API 默认对 script 档不可见（fail-closed），不会因为"忘了加"而泄漏。
        EnsureApiAllowed(tool, method);

        switch (method)
        {
            case ProtocolMethods.HostLog:
            {
                var level = prms?["level"]?.GetValue<string>() ?? "info";
                var message = prms?["message"]?.GetValue<string>() ?? string.Empty;
                var scoped = _log.Scope("tool:" + tool.Id, tool.Id);
                switch (level.ToLowerInvariant())
                {
                    case "debug": scoped.Debug(message); break;
                    case "warn":
                    case "warning": scoped.Warn(message); break;
                    case "error": scoped.Error(message); break;
                    default: scoped.Info(message); break;
                }

                return new JsonObject { ["ok"] = true };
            }

            case ProtocolMethods.HostNotify:
            {
                var title = prms?["title"]?.GetValue<string>() ?? tool.Name;
                var body = prms?["body"]?.GetValue<string>() ?? string.Empty;

                // 日志始终写：通知气泡会被 Win11 的勿扰/通知设置静默丢弃，日志是不依赖系统设置的唯一出口。
                _log.Info($"通知[{title}] {body}", "notify");

                // delivered 必须**如实反映**：
                //   · 有 UI 宿主（托盘）且气泡已送达 → true
                //   · 无 UI 宿主（ezt CLI）→ false + 说明原因
                // 绝不能恒定 false（那是"承诺了做不到"），也不能恒定 true（那会让工具以为弹出来了）。
                if (NotifyRequested is null)
                {
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["delivered"] = false,
                        ["reason"] = "当前宿主没有 UI（命令行宿主），通知只落日志",
                    };
                }

                var delivered = NotifyRequested(new ToolNotificationEvent(tool.Id, tool.Name, title, body));
                return new JsonObject
                {
                    ["ok"] = true,
                    ["delivered"] = delivered,
                    ["reason"] = delivered ? null : "宿主有 UI 但未能显示（详见宿主日志 notify 分类）",
                };
            }

            case ProtocolMethods.HostProgress:
            {
                EnsureProgressAllowed(tool);
                var percent = prms?["percent"]?.GetValue<double?>();
                var message = prms?["message"]?.GetValue<string>();
                _log.Info($"进度 {percent:0.#}% {message}", "tool:" + tool.Id);
                return new JsonObject { ["ok"] = true };
            }

            case ProtocolMethods.HostStorageGet:
            {
                var key = prms?["key"]?.GetValue<string>()
                          ?? throw new ToolRpcException(RpcErrorCodes.InvalidParams, "host.storage.get 需要 key");
                var storage = ReadStorage(tool.Id);
                return new JsonObject
                {
                    ["value"] = storage.TryGetPropertyValue(key, out var value) ? value?.Clone() : null,
                };
            }

            case ProtocolMethods.HostStorageSet:
            {
                var key = prms?["key"]?.GetValue<string>()
                          ?? throw new ToolRpcException(RpcErrorCodes.InvalidParams, "host.storage.set 需要 key");
                var storage = ReadStorage(tool.Id);
                // 值为 null 时显式写入 JSON null（而不是留下旧值）
                storage[key] = prms?["value"] is { } incoming ? incoming.Clone() : null;
                WriteStorage(tool.Id, storage);
                return new JsonObject { ["ok"] = true };
            }

            case ProtocolMethods.HostStorageRemove:
            {
                var key = prms?["key"]?.GetValue<string>()
                          ?? throw new ToolRpcException(RpcErrorCodes.InvalidParams, "host.storage.remove 需要 key");
                var storage = ReadStorage(tool.Id);
                storage.Remove(key);
                WriteStorage(tool.Id, storage);
                return new JsonObject { ["ok"] = true };
            }

            case ProtocolMethods.HostPrimitiveCall:
            {
                // P3：工具 → 宿主 → Core 的特权原语通路（见 PrimitiveClient 与 P3 实施方案）。
                var primitiveName = prms?.GetString("name")
                                    ?? throw new ToolRpcException(
                                        RpcErrorCodes.InvalidParams, "host.primitive.call 缺少原语名 name");

                // 声明门槛：清单 elevatedPrimitives 是工具的**唯一授权面**
                // （与 needs 一样不做第二套运行时仲裁 —— 那是伪安全，见设计文档 §4.4 / 附录 B）。
                // 未声明 = 清单与行为漂移，直接拒绝并告诉工具怎么改清单。
                if (!tool.Manifest.ElevatedPrimitives.Contains(primitiveName, StringComparer.Ordinal))
                {
                    throw new ToolRpcException(
                        PrimitiveErrorCodes.PrimitiveNotDeclared,
                        $"工具 {tool.Id} 未在 tool.json 的 elevatedPrimitives 中声明原语 '{primitiveName}'。" +
                        $"已声明：[{string.Join(", ", tool.Manifest.ElevatedPrimitives)}]。声明后重试。");
                }

                var primitiveResult = await _primitives.CallAsync(
                    primitiveName,
                    prms?["args"] as JsonObject,
                    tool.Id,
                    TimeSpan.FromSeconds(30),
                    _shutdown.Token).ConfigureAwait(false);

                return primitiveResult ?? new JsonObject { ["ok"] = true };
            }

            case ProtocolMethods.HostInvokeTool:
                // P4：工具间调用真实通路。callerToolId 由 ToolProcess 的回调带进来（工具无法自报），
                // 因为调用链的起点必须是宿主认定的那个工具。
                //
                // 🔴 args 必须 Clone：它挂在工具请求的 params 树上，直接塞进新对象会抛
                //    "The node already has a parent."（本项目反复踩的坑；P3 的 primitive 通路
                //    在 PrimitiveClient:96 已经用 args?.Clone() 处理过同一问题）。
                return await InvokeToolAsync(
                    tool.Id,
                    prms.GetString("toolId") ?? string.Empty,
                    prms.GetString("commandId") ?? string.Empty,
                    prms?["args"]?.Clone(),
                    _shutdown.Token).ConfigureAwait(false);

            default:
                throw new ToolRpcException(RpcErrorCodes.MethodNotFound, $"宿主未实现方法 {method}");
        }
    }

    private JsonObject ReadStorage(string toolId)
    {
        var path = _paths.ToolStorageFile(toolId);
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _log.Warn($"工具 {toolId} 私有存储解析失败，已重置: {ex.Message}", "storage");
            return new JsonObject();
        }
    }

    private void WriteStorage(string toolId, JsonObject storage)
    {
        var path = _paths.ToolStorageFile(toolId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            storage.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();

        List<ToolProcess> processes;
        lock (_sessions)
        {
            processes = _sessions.Values.Select(s => s.Process).ToList();
            _sessions.Clear();
        }

        foreach (var process in processes)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _sweeper.ConfigureAwait(false);
        }
        catch
        {
            // 忽略
        }

        _shutdown.Dispose();
        _gate.Dispose();
        _perToolGate.Dispose();
        _nestedGate.Dispose();
    }

    private sealed class Session
    {
        public Session(ToolProcess process)
        {
            Process = process;
        }

        public ToolProcess Process { get; }

        public int Leases { get; set; } = 1;

        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.Now;

        /// <summary>
        /// 本次调用所属的**工具间调用链**，如 <c>[probe, wordcount]</c>。
        /// 由顶层调用置为 <c>[自身]</c>，嵌套调用逐级追加。用途只有一个：
        /// 在工具发来 <c>host.invokeTool</c> 时判断目标是否已在链上（成环 = 死锁，必须拒绝）。
        /// 见 docs/P4-实施方案.md §2.2。
        /// </summary>
        public string[] Chain { get; set; } = Array.Empty<string>();
    }
}
