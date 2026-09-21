using Eztools.Contracts;

namespace Eztools.Host.Runtimes;

/// <summary>
/// 运行时注册表：按工具清单声明解析"这个工具该用哪个运行时"，并缓存结果。
///
/// 规则：<c>runtimeVersion</c> 是范围（如 <c>&gt;=3.11 &lt;4.0</c>），在已部署的运行时里挑满足条件的最新版；
/// 没有满足条件的则触发按需部署。<c>runtime: exe</c> 不需要运行时（entry 本身即可执行文件）。
/// </summary>
public sealed class RuntimeRegistry
{
    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;
    private readonly Dictionary<string, RuntimeInstallation?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RuntimeRegistry(EztoolsPaths paths, HostLog log)
    {
        _paths = paths;
        _log = log;
        Provisioner = new RuntimeProvisioner(paths, log);
    }

    public RuntimeProvisioner Provisioner { get; }

    /// <summary>确保工具所需运行时可用（必要时部署），并返回它。</summary>
    public async Task<RuntimeProvisionResult> EnsureForAsync(
        ToolManifest manifest,
        Action<string>? report = null,
        CancellationToken ct = default)
    {
        if (string.Equals(manifest.Runtime, ToolRuntimes.Exe, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeProvisionResult { Provisioned = false, Steps = new[] { "runtime=exe，无需运行时" } };
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(manifest.Id, out var cached) && cached is not null && cached.IsUsable)
            {
                return new RuntimeProvisionResult { Installation = cached, Provisioned = false };
            }

            var request = new RuntimeRequest(manifest.Runtime, manifest.RuntimeVersion);
            var result = await Provisioner.EnsureAsync(request, report, ct).ConfigureAwait(false);
            _cache[manifest.Id] = result.Installation;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>确保并返回运行时；失败返回 null 并记录原因。</summary>
    public async Task<RuntimeInstallation?> GetAsync(
        ToolManifest manifest,
        Action<string>? report = null,
        CancellationToken ct = default)
    {
        var result = await EnsureForAsync(manifest, report, ct).ConfigureAwait(false);
        if (result.Installation is null)
        {
            _log.Error($"工具 {manifest.Id} 的运行时不可用：{result.Error}", "runtime");
        }

        return result.Installation;
    }

    /// <summary>清空缓存（部署新运行时后调用）。</summary>
    public void Invalidate()
    {
        _cache.Clear();
    }
}
