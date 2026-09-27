// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Eztools.Host.Runtimes;

/// <summary>运行时请求。</summary>
public sealed record RuntimeRequest(string Runtime, string? VersionRange = null, string? PreferredVersion = null);

/// <summary>部署结果。</summary>
public sealed class RuntimeProvisionResult
{
    public RuntimeInstallation? Installation { get; init; }

    /// <summary>true 表示本次真正执行了解压部署；false 表示复用已有安装。</summary>
    public bool Provisioned { get; init; }

    public string? Error { get; init; }

    /// <summary>步骤流水，供 <c>ezt doctor</c> / <c>ezt runtime install</c> 展示。</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    public bool Ok => Installation is not null && Error is null;
}

/// <summary>
/// 嵌入式运行时部署（P0 的核心交付之一）。
///
/// 事实基础（spike 第七节已实测）：CPython 完整安装布局<b>可整体重定位</b>——
/// 换个路径后 <c>sys.executable</c> / <c>sys.prefix</c> 自动指向新位置，隔离性与性能零损失。
/// 所以"嵌入式运行时分发"退化为"把一份运行时解压到自己的目录"：
/// 无需下载、无需注册表、无需写 PATH、用户机器上无需预装 Python。
///
/// 部署策略：<b>压缩包分发 + 首次运行解压</b>（53MB 直接拷贝需 42 秒，不可接受；
/// tar.gz 约 15~20MB，解压远快于拷贝）。
/// </summary>
public sealed class RuntimeProvisioner
{
    /// <summary>开发期覆盖：直接用本机 Python（不可交付，仅供调试）。</summary>
    public const string EnvPythonOverride = "EZTOOLS_PYTHON";

    /// <summary>额外载荷扫描目录（分号分隔）。</summary>
    public const string EnvPayloadDirs = "EZTOOLS_PAYLOAD_DIRS";

    /// <summary>SDK 源码目录覆盖。</summary>
    public const string EnvSdkDir = "EZTOOLS_SDK_DIR";

    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;

    public RuntimeProvisioner(EztoolsPaths paths, HostLog log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>载荷可能所在的位置（部署位置 + 仓库位置 + 环境变量追加）。</summary>
    public IReadOnlyList<string> PayloadSearchPaths
    {
        get
        {
            var dirs = new List<string> { _paths.PayloadDir };
            AppLayout.AddRepoRelative(dirs, "payload");

            dirs.AddRange(PathInput.NormalizeList(Environment.GetEnvironmentVariable(EnvPayloadDirs)));

            return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>扫描所有可用载荷并按版本降序排序。</summary>
    public IReadOnlyList<RuntimePayload> DiscoverPayloads(string? runtime = null)
    {
        var found = new List<RuntimePayload>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in PayloadSearchPaths)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.tar.gz", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!seen.Add(Path.GetFullPath(file)))
                {
                    continue;
                }

                if (!RuntimePayload.TryParse(file, out var payload, out var error) || payload is null)
                {
                    _log.Warn($"忽略载荷 {Path.GetFileName(file)}：{error}", "runtime");
                    continue;
                }

                if (!string.Equals(payload.Rid, NativeRid.Current, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Debug($"忽略载荷 {Path.GetFileName(file)}：RID {payload.Rid} 与本机 {NativeRid.Current} 不匹配", "runtime");
                    continue;
                }

                if (runtime is not null && !string.Equals(payload.Runtime, runtime, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(payload);
            }
        }

        return found
            .OrderByDescending(p => Contracts.SemVer.TryParse(p.Version, out var v) ? v : new Contracts.SemVer(0))
            .ToList();
    }

    /// <summary>已安装的运行时（按目录扫描，不依赖任何索引文件）。</summary>
    public IReadOnlyList<RuntimeInstallation> GetInstalled(string? runtime = null)
    {
        var result = new List<RuntimeInstallation>();
        if (!Directory.Exists(_paths.RuntimesDir))
        {
            return result;
        }

        foreach (var runtimeDir in Directory.EnumerateDirectories(_paths.RuntimesDir))
        {
            var runtimeName = Path.GetFileName(runtimeDir).ToLowerInvariant();
            if (runtime is not null && !string.Equals(runtimeName, runtime, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var versionDir in Directory.EnumerateDirectories(runtimeDir))
            {
                var version = Path.GetFileName(versionDir);
                var installation = new RuntimeInstallation
                {
                    Runtime = runtimeName,
                    Version = version,
                    Root = versionDir,
                    ExecutablePath = Path.Combine(versionDir, InterpreterRelativePath(runtimeName)),
                    IsEmbedded = true,
                    Origin = RuntimeMarker.Read(versionDir)?.PayloadFile is { } p
                        ? $"payload:{p}"
                        : "runtimes-dir",
                };
                result.Add(installation);
            }
        }

        return result
            .OrderByDescending(i => Contracts.SemVer.TryParse(i.Version, out var v) ? v : new Contracts.SemVer(0))
            .ToList();
    }

    /// <summary>
    /// 确保指定运行时可用。流程：复用已有安装 → 开发覆盖 → 从载荷解压部署。
    /// 幂等：重复调用不会重复解压。
    /// </summary>
    public async Task<RuntimeProvisionResult> EnsureAsync(
        RuntimeRequest request,
        Action<string>? report = null,
        CancellationToken ct = default)
    {
        var steps = new List<string>();
        void Step(string message)
        {
            steps.Add(message);
            report?.Invoke(message);
            _log.Info(message, "runtime");
        }

        // ── 1. 复用已有安装（快路径）──
        var installed = FindInstalled(request);
        if (installed is not null)
        {
            Step($"复用已安装运行时: {installed.Runtime} {installed.Version}");
            await EnsureSdkAsync(installed, Step, ct).ConfigureAwait(false);

            return new RuntimeProvisionResult
            {
                Installation = installed,
                Provisioned = false,
                Steps = steps,
            };
        }

        // ── 2. 开发期覆盖 ──
        var overridePath = Environment.GetEnvironmentVariable(EnvPythonOverride);
        if (string.Equals(request.Runtime, Contracts.ToolRuntimes.Python, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(overridePath)
            && File.Exists(overridePath))
        {
            var version = await ProbePythonVersionAsync(overridePath, ct).ConfigureAwait(false);
            Step($"使用开发期外部 Python 覆盖（不可交付）: {overridePath}");
            return new RuntimeProvisionResult
            {
                Installation = new RuntimeInstallation
                {
                    Runtime = Contracts.ToolRuntimes.Python,
                    Version = version ?? "0.0.0",
                    Root = Path.GetDirectoryName(overridePath)!,
                    ExecutablePath = overridePath,
                    IsEmbedded = false,
                    Origin = $"external:{EnvPythonOverride}",
                },
                Provisioned = false,
                Steps = steps,
            };
        }

        // ── 3. 从载荷部署 ──
        var candidates = DiscoverPayloads(request.Runtime);
        var picked = SelectPayload(candidates, request);
        if (picked is null)
        {
            return new RuntimeProvisionResult
            {
                Error = BuildMissingPayloadMessage(request, candidates),
                Steps = steps,
            };
        }

        Step($"选中载荷: {Path.GetFileName(picked.ArchivePath)}（{FormatBytes(picked.SizeBytes)}）");

        try
        {
            var installation = await ExtractAsync(picked, Step, ct).ConfigureAwait(false);
            await EnsureSdkAsync(installation, Step, ct).ConfigureAwait(false);

            return new RuntimeProvisionResult
            {
                Installation = installation,
                Provisioned = true,
                Steps = steps,
            };
        }
        catch (Exception ex)
        {
            _log.Error($"运行时部署失败: {ex.Message}", "runtime");
            return new RuntimeProvisionResult
            {
                Error = $"运行时部署失败: {ex.Message}",
                Steps = steps,
            };
        }
    }

    private RuntimeInstallation? FindInstalled(RuntimeRequest request)
    {
        Contracts.VersionRange range = Contracts.VersionRange.Any;
        if (!string.IsNullOrWhiteSpace(request.VersionRange))
        {
            range = Contracts.VersionRange.Parse(request.VersionRange, out var rangeError);
            if (rangeError is not null)
            {
                _log.Warn($"runtimeVersion 无法解析，按任意版本处理：{rangeError}", "runtime");
            }
        }

        var installed = GetInstalled(request.Runtime);

        // 若声明了首选版本，优先精确匹配
        if (!string.IsNullOrWhiteSpace(request.PreferredVersion))
        {
            var exact = installed.FirstOrDefault(i =>
                string.Equals(i.Version, request.PreferredVersion, StringComparison.OrdinalIgnoreCase));
            if (exact is not null && exact.IsUsable)
            {
                return exact;
            }
        }

        return installed.FirstOrDefault(i =>
            i.IsUsable &&
            Contracts.SemVer.TryParse(i.Version, out var v) &&
            range.IsSatisfied(v) &&
            RuntimeMarker.Read(i.Root)?.Schema == RuntimeMarker.CurrentSchema);
    }

    private static RuntimePayload? SelectPayload(IReadOnlyList<RuntimePayload> candidates, RuntimeRequest request)
    {
        var range = Contracts.VersionRange.Any;
        if (!string.IsNullOrWhiteSpace(request.VersionRange))
        {
            range = Contracts.VersionRange.Parse(request.VersionRange, out _);
        }

        return candidates.FirstOrDefault(p =>
            Contracts.SemVer.TryParse(p.Version, out var v) && range.IsSatisfied(v))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>解压载荷并落位到 <c>runtimes\&lt;runtime&gt;\&lt;version&gt;\</c>。</summary>
    private async Task<RuntimeInstallation> ExtractAsync(RuntimePayload payload, Action<string> step, CancellationToken ct)
    {
        var target = _paths.RuntimeDir(payload.Runtime, payload.Version);
        GuardUnderRoot(target, _paths.RuntimesDir);

        var tempRoot = Path.Combine(_paths.CacheDir, "extract-" + Guid.NewGuid().ToString("N")[..8]);
        GuardUnderRoot(tempRoot, _paths.CacheDir);

        try
        {
            Directory.CreateDirectory(tempRoot);
            step($"解压到临时目录 {tempRoot}");

            var archiveSize = payload.SizeBytes;
            var sha = await Task.Run(() => ComputeSha256(payload.ArchivePath), ct).ConfigureAwait(false);

            await Task.Run(() =>
            {
                using var file = File.OpenRead(payload.ArchivePath);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, tempRoot, overwriteFiles: true);
            }, ct).ConfigureAwait(false);

            var extractRoot = NormalizeExtractRoot(tempRoot, payload.Runtime);
            var expectedExe = InterpreterRelativePath(payload.Runtime);
            if (!File.Exists(Path.Combine(extractRoot, expectedExe)))
            {
                throw new InvalidOperationException(
                    $"载荷结构异常：解压后未找到 {expectedExe}（载荷根应为运行时根目录）");
            }

            var fileCount = CountFiles(extractRoot);
            step($"解压完成：{fileCount} 个文件");

            // 落位：先移走旧目录（仅限 runtimes 根之下），再整体 Move（同卷内为原子重命名）
            if (Directory.Exists(target))
            {
                step($"移除旧安装 {target}");
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MoveDirectory(extractRoot, target);
            step($"落位到 {target}");

            var installMarkerAttempts = new RuntimeMarker
            {
                Runtime = payload.Runtime,
                Version = payload.Version,
                Rid = payload.Rid,
                PayloadFile = Path.GetFileName(payload.ArchivePath),
                PayloadSha256 = sha,
                InstalledAt = DateTimeOffset.Now.ToString("O"),
                FileCount = fileCount,
            }.Write(target);

            // 重试过就说出来：静默重试会让"这台机器上文件被占用"这件事永远不可见，
            // 而它正是"首次部署失败"的成因（见 RuntimeMarker.Write 的注释）。
            if (installMarkerAttempts > 1)
            {
                _log.Warn(
                    $"安装标记写入遇到占用，重试 {installMarkerAttempts - 1} 次后成功（共 {installMarkerAttempts} 次尝试）",
                    "runtime");
            }

            _log.Info(
                $"运行时部署完成: {payload.Runtime} {payload.Version} " +
                $"({FormatBytes(archiveSize)} 载荷 → {fileCount} 文件)，sha256={sha[..12]}…",
                "runtime");

            return new RuntimeInstallation
            {
                Runtime = payload.Runtime,
                Version = payload.Version,
                Root = target,
                ExecutablePath = Path.Combine(target, InterpreterRelativePath(payload.Runtime)),
                IsEmbedded = true,
                Origin = $"payload:{Path.GetFileName(payload.ArchivePath)}",
            };
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// 把 Python SDK 植入运行时的 <c>Lib\site-packages</c>。
    ///
    /// 为什么这样做（已实测）：<c>-I</c> 会忽略父进程 <c>PYTHONPATH</c>，但**运行时自身的
    /// site-packages 仍在 sys.path 上**。因此把 SDK 放进运行时，工具侧写
    /// <c>from eztools import Tool</c> 即可，不需要 <c>-c</c> 预置代码，也不需要每个工具目录各放一份 SDK。
    /// </summary>
    private async Task EnsureSdkAsync(RuntimeInstallation installation, Action<string> step, CancellationToken ct)
    {
        if (!string.Equals(installation.Runtime, Contracts.ToolRuntimes.Python, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sdkSource = ResolveSdkPackageDir();
        if (sdkSource is null)
        {
            _log.Warn("未找到 Python SDK 源码目录，跳过 SDK 植入（工具将无法 import eztools）", "runtime");
            step("警告：未找到 SDK 源码，跳过植入");
            return;
        }

        var sitePackages = Path.Combine(installation.Root, "Lib", "site-packages");
        GuardUnderRoot(sitePackages, installation.Root);

        var destination = Path.Combine(sitePackages, "eztools");
        var fingerprint = ComputeSdkFingerprint(sdkSource);
        var marker = RuntimeMarker.Read(installation.Root);

        if (marker?.SdkVersion == fingerprint && Directory.Exists(destination))
        {
            _log.Debug($"SDK 已是最新（{fingerprint}）", "runtime");
            return;
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(sitePackages);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            CopyDirectory(sdkSource, destination);
        }, ct).ConfigureAwait(false);

        var refreshed = RuntimeMarker.Read(installation.Root) ?? new RuntimeMarker
        {
            Runtime = installation.Runtime,
            Version = installation.Version,
            Rid = NativeRid.Current,
        };
        refreshed.SdkVersion = fingerprint;
        refreshed.InstalledAt ??= DateTimeOffset.Now.ToString("O");
        // ★ 这一次写入是"首次部署必然失败"实际发生的位置（实测）：它在 SDK 植入结束时执行，
        //   而那时刚解压的上千个文件很可能正被第三方扫描。详见 RuntimeMarker.Write 的注释。
        var sdkMarkerAttempts = refreshed.Write(installation.Root);
        if (sdkMarkerAttempts > 1)
        {
            _log.Warn(
                $"安装标记写入遇到占用，重试 {sdkMarkerAttempts - 1} 次后成功（共 {sdkMarkerAttempts} 次尝试）",
                "runtime");
        }

        step($"SDK 已植入 {destination}（{fingerprint}）");
        _log.Info($"Python SDK 植入完成: {destination}（{fingerprint}）", "runtime");
    }

    /// <summary>定位 sdk/python/eztools 包目录。</summary>
    public string? ResolveSdkPackageDir()
    {
        var envDir = PathInput.Normalize(Environment.GetEnvironmentVariable(EnvSdkDir));
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            // 允许指向 sdk/ 或 sdk/python/ 或直接指向 eztools 包
            candidates.Add(Path.Combine(envDir, "python", "eztools"));
            candidates.Add(Path.Combine(envDir, "eztools"));
            candidates.Add(envDir);
        }

        candidates.Add(Path.Combine(_paths.SdkDir, "python", "eztools"));

        // 以"有内容"为判据：安装根下的空 sdk/ 目录不该遮蔽仓库里的 SDK
        candidates.Add(Path.Combine(AppLayout.ResolveSdkDir(_paths), "python", "eztools"));

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "__init__.py")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>SDK 指纹：文件名 + 大小 + 修改时间的哈希。SDK 变更时自动重新植入。</summary>
    private static string ComputeSdkFingerprint(string sdkDir)
    {
        var entries = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sdkDir, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(file);
            entries.Add($"{Path.GetRelativePath(sdkDir, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }

        var joined = string.Join("\n", entries);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return "0.1.0+" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private async Task<string?> ProbePythonVersionAsync(string pythonPath, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add("-X");
            psi.ArgumentList.Add("utf8");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys;print('.'.join(map(str,sys.version_info[:3])))");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var text = output.Trim();
            return Contracts.SemVer.TryParse(text, out _) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildMissingPayloadMessage(RuntimeRequest request, IReadOnlyList<RuntimePayload> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"未找到可用的 {request.Runtime} 运行时载荷" +
                      (string.IsNullOrWhiteSpace(request.VersionRange) ? "" : $"（要求 {request.VersionRange}）") + "。");
        sb.AppendLine($"载荷应为 {request.Runtime}-<version>-{NativeRid.Current}.tar.gz，放置于以下任一目录：");
        foreach (var dir in PayloadSearchPaths)
        {
            sb.AppendLine($"    {dir}");
        }

        if (candidates.Count > 0)
        {
            sb.AppendLine("已发现但不满足版本要求的载荷：");
            foreach (var c in candidates)
            {
                sb.AppendLine($"    {Path.GetFileName(c.ArchivePath)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("获取方式（推荐 python-build-standalone 的 install_only 产物，专为再分发设计）：");
        sb.AppendLine("    1) 解压 install_only 产物，确认根目录下有 python.exe（不要用 python.org 的 embeddable zip，它带 ._pth 且没有 pip）");
        sb.AppendLine("    2) 用 scripts/make-payload.sh 打成 tar.gz（会剔除 test/idlelib/__pycache__ 等）");
        sb.AppendLine($"    3) 产物放到 {_paths.PayloadDir}");
        sb.AppendLine();
        sb.AppendLine($"开发调试可临时设置 {EnvPythonOverride}=<本机 python.exe> 跳过部署（不可交付）。");

        return sb.ToString();
    }

    // ── 文件系统助手 ──

    /// <summary>运行时解释器在运行时根目录下的相对路径。</summary>
    public static string InterpreterRelativePath(string runtime) => runtime.ToLowerInvariant() switch
    {
        Contracts.ToolRuntimes.Python => OperatingSystem.IsWindows() ? "python.exe" : "bin/python3",
        Contracts.ToolRuntimes.Node => OperatingSystem.IsWindows() ? "node.exe" : "bin/node",
        Contracts.ToolRuntimes.DotNet => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
        _ => OperatingSystem.IsWindows() ? "python.exe" : "bin/python3",
    };

    private static string NormalizeExtractRoot(string dir, string runtime)
    {
        var exe = InterpreterRelativePath(runtime);
        if (File.Exists(Path.Combine(dir, exe)))
        {
            return dir;
        }

        // 容忍"压缩包里套了一层同名目录"的常见打包方式
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length == 1 && Directory.Exists(entries[0]) &&
            File.Exists(Path.Combine(entries[0], exe)))
        {
            return entries[0];
        }

        return dir;
    }

    private void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            // 跨卷或目标占用时退化为复制
            CopyDirectory(source, destination);
            TryDeleteDirectory(source);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static int CountFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            GuardUnderRoot(dir, _paths.Root);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Debug($"清理临时目录失败（可忽略）: {dir} → {ex.Message}", "runtime");
        }
    }

    /// <summary>安全带：任何递归删除/落位都必须发生在安装根之下，防止路径拼接错误误删用户数据。</summary>
    private static void GuardUnderRoot(string path, string allowedRoot)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(allowedRoot);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (full == root || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"拒绝在安装根之外操作路径: {full}（允许根: {root}）");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }
}
