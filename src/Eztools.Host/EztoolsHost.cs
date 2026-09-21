using Eztools.Host.Config;
using Eztools.Host.Discovery;
using Eztools.Host.Processes;
using Eztools.Host.Registry;
using Eztools.Host.Runtimes;

namespace Eztools.Host;

/// <summary>宿主创建参数。</summary>
public sealed class HostOptions
{
    public EztoolsPaths? Paths { get; init; }

    /// <summary>额外的工具目录源（分号分隔）。</summary>
    public string? ToolsDir { get; init; }

    public bool Verbose { get; init; }

    /// <summary>日志是否回显到控制台（GUI 宿主关闭它，日志只落文件）。</summary>
    public bool EchoLogToConsole { get; init; } = true;

    /// <summary>宿主名，用于日志与诊断（ezt / eztools-ui / selftest）。</summary>
    public string HostName { get; init; } = "ezt";

    public ToolHostOptions ProcessOptions { get; init; } = ToolHostOptions.ForCli;

    public static HostOptions Default { get; } = new();
}

/// <summary>
/// 宿主门面：把路径、日志、发现、注册表、运行时、进程管理装配成一个可直接使用的对象。
///
/// 这是 P1 的 WinUI 设置应用与 P0 的 <c>ezt</c> CLI 共用的唯一入口——
/// 两者都只是"这个门面的另一种前端"，不存在两套逻辑。
/// </summary>
public sealed class EztoolsHost : IAsyncDisposable
{
    private readonly List<string> _startupSteps = new();
    private readonly ToolDiscovery _discovery;
    private int _disposed;

    private EztoolsHost(
        HostOptions options,
        EztoolsPaths paths,
        HostLog log,
        ToolDiscovery discovery)
    {
        Options = options;
        Paths = paths;
        Log = log;
        _discovery = discovery;
    }

    public HostOptions Options { get; }

    public EztoolsPaths Paths { get; }

    public HostLog Log { get; }

    public ToolRegistry Registry { get; private set; } = null!;

    public RuntimeRegistry Runtimes { get; private set; } = null!;

    public ToolStateStore StateStore { get; private set; } = null!;

    /// <summary>独占资源运行时注册表（§8.3）。热键注册成功即在此 claim；resident 工具（将来）启动时 claim 其声明。</summary>
    public Arbitration.ExclusiveResourceManager Exclusive { get; private set; } = null!;

    /// <summary>配置中心（P1a）：读写 <c>%APPDATA%\Eztools\config\&lt;toolId&gt;.json</c>。</summary>
    public ConfigStore Configs { get; private set; } = null!;

    public ToolHostManager Processes { get; private set; } = null!;

    /// <summary>
    /// 宿主内部事件总线（P4 Wave 2a，决策 D2）：**宿主 → 宿主 UI 的单向通知，工具不可订阅**。
    /// 解决"UI 要刷新但只能手动点刷新"这个已存在的痛点（托盘菜单里的「刷新菜单」项）。
    /// 详见 <see cref="HostEventBus"/> 与 <c>docs/Eztools-设计方案.md</c> §23。
    /// </summary>
    public HostEventBus Events { get; } = new();

    public ToolDiscoveryResult LastDiscovery { get; private set; } = new();

    /// <summary>启动过程中的步骤流水（<c>ezt doctor</c> 会展示）。</summary>
    public IReadOnlyList<string> StartupSteps => _startupSteps;

    /// <summary>首次运行时真实新建的目录。</summary>
    public IReadOnlyList<string> CreatedDirectories { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// 创建宿主：确保目录结构 → 扫描清单 → 建注册表 → 装配运行时与进程管理。
    /// <b>刻意不在此处部署运行时</b>：只做发现时不应该触发几十 MB 的解压，
    /// 部署由调用方按需触发（<see cref="EnsureRuntimeAsync"/>）或首次调用工具时惰性触发。
    /// </summary>
    public static Task<EztoolsHost> CreateAsync(HostOptions? options = null, CancellationToken ct = default)
    {
        var opts = options ?? HostOptions.Default;
        var paths = opts.Paths ?? EztoolsPaths.Create();
        var log = new HostLog(paths, opts.Verbose, opts.EchoLogToConsole, opts.HostName);

        var host = new EztoolsHost(opts, paths, log, new ToolDiscovery(log));
        host.Events.AttachLog(log);

        host.CreatedDirectories = paths.EnsureDirectories();
        if (host.CreatedDirectories.Count > 0)
        {
            host._startupSteps.Add($"初始化目录结构，新建 {host.CreatedDirectories.Count} 个目录");
            log.Info($"首次运行：已创建 {host.CreatedDirectories.Count} 个目录于 {paths.Root}", "startup");
        }

        host.LoadRegistry();

        host.Runtimes = new RuntimeRegistry(paths, log);
        host.Configs = new ConfigStore(paths, log);

        // 先于 ToolHostManager 创建：进程层启动工具时要用它算"有效配置"
        host.Processes = CreateProcessManager(host);

        return Task.FromResult(host);
    }

    /// <summary>
    /// 构造进程层。抽成方法是因为 <see cref="ReloadAsync"/> 也要用它 ——
    /// 两处各写一遍迟早会漂移（其中一处忘了订阅熔断事件）。
    /// </summary>
    private static ToolHostManager CreateProcessManager(EztoolsHost host)
    {
        var processes = new ToolHostManager(
            host.Paths, host.Log, host.Registry, host.Runtimes,
            host.StateStore, host.Configs, host.Options.ProcessOptions, host.Events);

        processes.ToolQuarantined += e =>
            host.Log.Error($"工具 {e.ToolId} 被熔断隔离：{e.Reason}", "circuit-breaker");

        return processes;
    }

    /// <summary>
    /// 重新扫描清单并重建注册表（开发热重载、新增工具后调用）。
    ///
    /// ⚠️ **它只换 <see cref="Registry"/> 引用，不换进程层** —— <see cref="Processes"/>
    /// 仍持有旧注册表，于是"新扫出来的工具"在列表里可见、点下去却报找不到（半迁移）。
    /// 要连进程层一起换，用 <see cref="ReloadAsync"/>。
    /// </summary>
    public ToolRegistry LoadRegistry()
    {
        var sources = ToolDiscovery.ResolveSources(Paths, Options.ToolsDir);
        LastDiscovery = _discovery.Scan(sources);

        StateStore = new ToolStateStore(Paths, Log);
        Exclusive = new Arbitration.ExclusiveResourceManager();
        Registry = ToolRegistry.Build(LastDiscovery, StateStore, Log);

        _startupSteps.Add(
            $"扫描 {sources.Count} 个目录源，注册 {Registry.Tools.Count} 个工具" +
            $"（错误 {LastDiscovery.ErrorCount} / 警告 {LastDiscovery.WarningCount}）");

        Log.Info(
            $"清单发现完成：{Registry.Tools.Count} 个工具（已启用 {Registry.EnabledCount}），" +
            $"命令 {Registry.Commands.Count()} 个",
            "discovery");

        return Registry;
    }

    /// <summary>
    /// 完整重载：重扫清单 + 重读启停状态 + **连同进程层一起重建**。
    ///
    /// 托盘的「刷新菜单」走这条 —— 只在外部（命令行改了启停、或往 tools/ 放了新工具）后需要。
    /// 注意它会 dispose 旧的进程层，**正在执行的工具调用会被中止**；所以不要在
    /// 菜单弹出时无脑调用它（那是 <see cref="LoadRegistry"/> 的半迁移陷阱的另一半：
    /// 图省事在每次弹菜单时重载，代价是随机的调用中断）。
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (Processes is not null)
        {
            await Processes.DisposeAsync().ConfigureAwait(false);
        }

        LoadRegistry();
        Processes = CreateProcessManager(this);
        Events.Publish(HostEventKind.RegistryReloaded, null,
            $"{Registry.Tools.Count} 个工具（已启用 {Registry.EnabledCount}）");
    }

    /// <summary>确保某个工具所需运行时可用（必要时部署）。</summary>
    public async Task<RuntimeProvisionResult> EnsureRuntimeAsync(
        string toolId,
        Action<string>? report = null,
        CancellationToken ct = default)
    {
        if (!Registry.TryGetTool(toolId, out var tool))
        {
            return new RuntimeProvisionResult { Error = $"未找到工具 '{toolId}'" };
        }

        return await Runtimes.EnsureForAsync(tool.Manifest, report, ct).ConfigureAwait(false);
    }

    /// <summary>确保指定运行时可部署可用（与具体工具无关）。</summary>
    public async Task<RuntimeProvisionResult> EnsureRuntimeAsync(
        string runtime,
        string? versionRange,
        Action<string>? report = null,
        CancellationToken ct = default)
    {
        var result = await Runtimes.Provisioner
            .EnsureAsync(new RuntimeRequest(runtime, versionRange), report, ct)
            .ConfigureAwait(false);
        Runtimes.Invalidate();
        return result;
    }

    /// <summary>启用/禁用一个工具（"已启用"与"已加载"是两件事：禁用不会卸载已注册的清单）。</summary>
    public bool SetEnabled(string toolId, bool enabled, string? reason = null)
    {
        if (!Registry.TryGetTool(toolId, out var tool))
        {
            return false;
        }

        tool.Enabled = enabled;
        tool.DisabledReason = enabled ? null : reason;
        if (enabled && tool.LoadState == ToolLoadState.Quarantined)
        {
            tool.LoadState = ToolLoadState.NotLoaded;
            tool.CrashCount = 0;
        }

        StateStore.SetEnabled(toolId, enabled, reason);
        Log.Info($"工具 {toolId} 已{(enabled ? "启用" : "禁用")}" + (reason is null ? "" : $"（{reason}）"), "registry");
        Events.Publish(HostEventKind.ToolEnabledChanged, toolId, enabled ? "启用" : $"禁用（{reason ?? "用户操作"}）");
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Processes is not null)
        {
            await Processes.DisposeAsync().ConfigureAwait(false);
        }

        Log.Dispose();
    }
}
