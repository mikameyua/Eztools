// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Contracts;

namespace Eztools.Host.Discovery;

/// <summary>工具目录源类型。</summary>
public enum ToolSourceKind
{
    /// <summary>随产品发布的内置工具目录。</summary>
    Builtin,

    /// <summary>用户目录源（保留，P0 未启用）。</summary>
    User,

    /// <summary>开发挂载（<c>EZTOOLS_TOOLS_DIR</c> / <c>--tools-dir</c>）。</summary>
    Dev,
}

/// <summary>一个工具目录源。</summary>
public sealed class ToolSource
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public ToolSourceKind Kind { get; init; } = ToolSourceKind.Builtin;

    /// <summary>同一 id 出现多次时，序号小者优先（内置源优先于开发源之外的冲突，按加入顺序）。</summary>
    public int Order { get; init; }

    public bool Exists => Directory.Exists(Path);

    public override string ToString() => $"{Name}({Kind}) → {Path}";
}

/// <summary>发现结果。</summary>
public sealed class ToolDiscoveryResult
{
    public IReadOnlyList<ToolManifest> Manifests { get; init; } = Array.Empty<ToolManifest>();

    public IReadOnlyList<ToolDiagnostic> Diagnostics { get; init; } = Array.Empty<ToolDiagnostic>();

    public IReadOnlyList<ToolSource> Sources { get; init; } = Array.Empty<ToolSource>();

    public int ScannedFileCount { get; init; }

    public int ErrorCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    public int WarningCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
}

/// <summary>
/// 清单驱动的工具发现（设计方案 §6.1）。
///
/// 这是对 PowerToys 最重要的修正：PowerToys 把 36 个模块名硬编码在
/// <c>src/runner/main.cpp:256-293</c> 的数组里，新增模块必须改宿主源码重编译；
/// 这里改为扫描目录下的 <c>tool.json</c>，<b>新增工具 = 新增一个目录</b>。
/// </summary>
public sealed class ToolDiscovery
{
    private readonly HostLog _log;

    public ToolDiscovery(HostLog log)
    {
        _log = log;
    }

    /// <summary>按源列表解析目录源（内置 + 开发挂载）。</summary>
    public static IReadOnlyList<ToolSource> ResolveSources(EztoolsPaths paths, string? explicitToolsDir = null)
    {
        var sources = new List<ToolSource>();
        var order = 0;

        if (!string.IsNullOrWhiteSpace(explicitToolsDir))
        {
            foreach (var dir in Split(explicitToolsDir))
            {
                sources.Add(new ToolSource
                {
                    Name = $"dev:{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}",
                    Path = Path.GetFullPath(dir),
                    Kind = ToolSourceKind.Dev,
                    Order = order++,
                });
            }
        }

        var envDirs = Environment.GetEnvironmentVariable(AppLayout.EnvToolsDirs);
        if (!string.IsNullOrWhiteSpace(envDirs))
        {
            foreach (var dir in Split(envDirs))
            {
                if (sources.Any(s => string.Equals(s.Path, Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                sources.Add(new ToolSource
                {
                    Name = $"dev:{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}",
                    Path = Path.GetFullPath(dir),
                    Kind = ToolSourceKind.Dev,
                    Order = order++,
                });
            }
        }

        foreach (var (name, path) in AppLayout.ResolveToolDirectories(paths))
        {
            if (sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            sources.Add(new ToolSource
            {
                Name = name,
                Path = path,
                Kind = ToolSourceKind.Builtin,
                Order = order++,
            });
        }

        return sources;
    }

    private static IEnumerable<string> Split(string value) =>
        PathInput.NormalizeList(value);

    /// <summary>
    /// 扫描全部源，解析并校验清单，同 id 去重（保留版本更高者，版本相同则源顺序靠前者）。
    /// 任何单个清单的问题都只进诊断，不影响其他工具与本方法返回。
    /// </summary>
    public ToolDiscoveryResult Scan(IReadOnlyList<ToolSource> sources)
    {
        var diagnostics = new List<ToolDiagnostic>();
        var accepted = new List<ToolManifest>();
        var byId = new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        foreach (var source in sources)
        {
            if (!source.Exists)
            {
                diagnostics.Add(new ToolDiagnostic(
                    DiagnosticSeverity.Info,
                    DiagnosticCodes.ToolDirectoryMissing,
                    $"工具目录源不存在，已跳过: {source.Path}"));
                continue;
            }

            IEnumerable<string> manifestPaths;
            try
            {
                manifestPaths = Directory.EnumerateFiles(source.Path, ManifestParser.FileName, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ToolDiagnostic(
                    DiagnosticSeverity.Warning,
                    DiagnosticCodes.ManifestUnreadable,
                    $"枚举工具目录失败: {ex.Message}",
                    source.Path));
                continue;
            }

            foreach (var manifestPath in manifestPaths)
            {
                // 跳过 SDK 模板与隐藏目录，避免把样例当成真工具注册
                if (IsIgnored(manifestPath))
                {
                    continue;
                }

                scanned++;
                var result = ManifestParser.Load(manifestPath, source.Name);
                diagnostics.AddRange(result.Diagnostics);

                if (result.Manifest is null)
                {
                    continue;
                }

                var manifest = result.Manifest;
                if (!result.Ok)
                {
                    // 校验失败：静默降级 + 日志，绝不弹窗（PowerToys 的 MessageBoxW 是明确的反面教材）
                    _log.Warn($"工具 {manifest.Id} 清单校验未通过，已跳过注册（{manifestPath}）", "discovery");
                    continue;
                }

                if (byId.TryGetValue(manifest.Id, out var existing))
                {
                    var incoming = ParseVersion(manifest.Version);
                    var current = ParseVersion(existing.Version);
                    // 版本更高者胜出；版本相同则"先出现的源"胜出（源顺序由 ResolveSources 决定）
                    var replace = incoming.CompareTo(current) > 0;

                    diagnostics.Add(new ToolDiagnostic(
                        DiagnosticSeverity.Warning,
                        DiagnosticCodes.DuplicateId,
                        $"工具 id '{manifest.Id}' 重复出现，保留 {manifest.SourceName} 的 {manifest.Version}" +
                        $"（另一处为 {existing.SourceName} 的 {existing.Version}）",
                        manifestPath,
                        manifest.Id));

                    if (!replace)
                    {
                        continue;
                    }

                    accepted.Remove(existing);
                }

                byId[manifest.Id] = manifest;
                accepted.Add(manifest);
            }
        }

        return new ToolDiscoveryResult
        {
            Manifests = accepted,
            Diagnostics = diagnostics,
            Sources = sources,
            ScannedFileCount = scanned,
        };
    }

    private static Contracts.SemVer ParseVersion(string version) =>
        Contracts.SemVer.TryParse(version, out var parsed) ? parsed : new Contracts.SemVer(0);

    /// <summary>忽略 SDK 模板目录、隐藏目录与构建产物目录。</summary>
    private static bool IsIgnored(string manifestPath)
    {
        var relative = manifestPath.Replace('\\', '/');

        return relative.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)
               || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || relative.Contains("/_template/", StringComparison.OrdinalIgnoreCase)
               || relative.Contains("/template/", StringComparison.OrdinalIgnoreCase)
               || IsUnderHiddenDirectory(manifestPath);
    }

    private static bool IsUnderHiddenDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (dir is not null)
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.'))
            {
                return true;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }
}
