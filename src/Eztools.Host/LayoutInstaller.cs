// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json.Nodes;

namespace Eztools.Host;

/// <summary>一次安装（把"源码形态"铺成"已安装形态"）的结果。</summary>
public sealed class InstallResult
{
    public required string SourceRoot { get; init; }

    public required string TargetRoot { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    public int CopiedFiles { get; init; }

    public int CopiedTools { get; init; }

    public bool PayloadCopied { get; init; }

    public bool DryRun { get; init; }

    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 布局安装器：把仓库里的"源码形态"铺成机器上的"已安装形态"。
///
/// <b>为什么必须有这一步</b>（实测发现的缺口）：
/// 宿主的工具源与 SDK 来源是按下面顺序解析的——先看安装根，再向上找仓库根。
/// 于是从仓库里跑一切正常，但把 <c>ezt.exe</c> 单独放到别处（模拟装到机器上以后）时，
/// 安装根下的 <c>tools\</c> 与 <c>sdk\</c> 是空的，向上又找不到仓库根，
/// 结果<b>一个工具都发现不到</b>——这正是"开发跑得通、装起来是空的"那类问题。
///
/// 所以安装形态不是"把 exe 拷过去"就完事，必须把三样东西一起铺进安装根：
/// <list type="number">
/// <item><c>tools\</c> —— 内置工具（清单驱动的发现对象）</item>
/// <item><c>sdk\python\</c> —— Python SDK 源码，首次部署运行时会被植入其 site-packages</item>
/// <item><c>payload\</c> —— 运行时压缩包（可选，体积较大）</item>
/// </list>
///
/// 幂等：重复执行只覆盖变化的部分；已经就位的运行时不会被碰（那是 <see cref="Runtimes.RuntimeProvisioner"/> 的职责）。
/// </summary>
public sealed class LayoutInstaller
{
    public const string EnvSourceRoot = "EZTOOLS_SOURCE_ROOT";

    public const string MarkerFileName = "install.json";

    /// <summary>安装布局的版本，将来布局变了靠它识别需不需要重装。</summary>
    public const int LayoutSchema = 1;

    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;

    public LayoutInstaller(EztoolsPaths paths, HostLog log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// 定位"源码根"（含 <c>tools/</c>、<c>sdk/</c>、<c>payload/</c> 的那个目录）。
    /// 顺序：环境变量 → 从宿主程序集位置向上找 → null。
    /// </summary>
    public static string? FindSourceRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvSourceRoot);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var full = PathInput.NormalizeToFullPath(fromEnv);
            return Directory.Exists(full) ? full : null;
        }

        foreach (var candidate in AppLayout.RepoCandidates())
        {
            if (Directory.Exists(Path.Combine(candidate, "tools")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>把源码根铺到安装根。返回步骤流水，供 CLI 展示。</summary>
    public InstallResult Install(string sourceRoot, bool includePayload = true, bool dryRun = false)
    {
        var steps = new List<string>();
        var skipped = new List<string>();
        var copied = 0;

        sourceRoot = PathInput.NormalizeToFullPath(sourceRoot);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"源码根不存在: {sourceRoot}");
        }

        if (!dryRun)
        {
            _paths.EnsureDirectories();
        }

        steps.Add($"源码根 {sourceRoot} → 安装根 {_paths.Root}");

        // ── 1. tools：整目录复制（每个工具目录含 main.py / tool.json / Lib/ / 图标）──
        var sourceTools = Path.Combine(sourceRoot, "tools");
        var copiedTools = 0;
        if (Directory.Exists(sourceTools))
        {
            foreach (var toolDir in Directory.EnumerateDirectories(sourceTools))
            {
                if (!File.Exists(Path.Combine(toolDir, "tool.json")))
                {
                    skipped.Add($"{Path.GetFileName(toolDir)}（无 tool.json，不是工具目录）");
                    continue;
                }

                var target = Path.Combine(_paths.BuiltinToolsDir, Path.GetFileName(toolDir));
                copied += CopyTree(toolDir, target, dryRun);
                copiedTools++;
            }

            steps.Add($"工具：{copiedTools} 个 → {_paths.BuiltinToolsDir}");
        }
        else
        {
            skipped.Add($"源码根下没有 tools/（{sourceTools}）");
        }

        // ── 2. SDK：Python SDK 源码（部署运行时时会植入其 site-packages）──
        var sourceSdk = Path.Combine(sourceRoot, "sdk");
        if (Directory.Exists(sourceSdk))
        {
            copied += CopyTree(sourceSdk, _paths.SdkDir, dryRun);
            steps.Add($"SDK：{sourceSdk} → {_paths.SdkDir}");
        }
        else
        {
            skipped.Add($"源码根下没有 sdk/（{sourceSdk}）");
        }

        // ── 3. payload：运行时压缩包（可选，约 13~20MB）──
        var payloadCopied = false;
        var sourcePayload = Path.Combine(sourceRoot, "payload");
        if (includePayload && Directory.Exists(sourcePayload))
        {
            foreach (var archive in Directory.EnumerateFiles(sourcePayload, "*.tar.gz"))
            {
                var target = Path.Combine(_paths.PayloadDir, Path.GetFileName(archive));
                if (FilesAreSame(archive, target))
                {
                    continue;
                }

                if (!dryRun)
                {
                    Directory.CreateDirectory(_paths.PayloadDir);
                    File.Copy(archive, target, overwrite: true);
                }

                copied++;
                payloadCopied = true;
            }

            // dry-run 下不能说"已复制到"——试运行什么都不落盘，文案必须如实
            steps.Add(payloadCopied
                ? $"载荷：{(dryRun ? "将复制到" : "已复制到")} {_paths.PayloadDir}"
                : "载荷：安装根已是最新，无需复制");
        }
        else if (!includePayload)
        {
            steps.Add("载荷：按参数要求跳过（--no-payload）");
        }

        // ── 4. bin：宿主自身的程序集 ──
        var sourceBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var targetBin = _paths.BinDir.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(sourceBin, targetBin, StringComparison.OrdinalIgnoreCase))
        {
            skipped.Add("bin/（当前就是从安装根运行的，无需自拷贝）");
        }
        else
        {
            copied += CopyTree(sourceBin, _paths.BinDir, dryRun,
                // 只带程序集与配置，别把调试符号、临时产物一起搬过去
                filter: name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            steps.Add($"宿主程序：{sourceBin} → {_paths.BinDir}");
        }

        // ── 4b. Core 特权服务（P3）：ezt-core.exe 及其依赖 ──
        // Core 是独立项目（net10.0-windows，提权边界必须进程隔离，见 P3 方案 §2），
        // 它的构建输出不在宿主 bin 里；但已安装形态要求它就在 <安装根>\bin 下
        // （PrimitiveClient.FindCoreExe 的第一优先查找位）。找不到时不阻断安装，
        // 如实记录到 skipped —— 特权原语是可选能力，缺 Core 不影响其他功能。
        //
        // ★ 顺序很关键：**先看源 bin/ 里有没有，再去找构建输出**。
        //   便携包（scripts/make-portable.sh）的布局就是"bin/ 里三件套齐全"
        //   （ezt.exe + ezt-core.exe + Eztools.Desktop.exe）——那里没有 src/ 目录树。
        //   原先不查这一步，用便携包 install 时会打印
        //   "跳过 ezt-core.exe（未找到 Eztools.Core 的构建输出）"，看起来像安装缺件；
        //   实际上上面第 4 步拷整个 bin/ 时已经把它带过去了（虚惊一场，但提示误导人）。
        var coreInSourceBin = File.Exists(Path.Combine(sourceBin, "ezt-core.exe"));
        var coreBuild = coreInSourceBin ? null : FindNewestCoreBuild(sourceRoot);

        if (coreInSourceBin)
        {
            if (string.Equals(sourceBin, targetBin, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add("ezt-core.exe（源 bin/ 已含，当前就是从安装根运行）");
            }
            else
            {
                skipped.Add("ezt-core.exe（源 bin/ 已含，随第 4 步的 bin/ 整体复制一并就位）");
            }
        }
        else if (coreBuild is null)
        {
            skipped.Add("ezt-core.exe（源 bin/ 与 Eztools.Core 构建输出里都没有 —— 先构建解决方案）");
        }
        else
        {
            copied += CopyTree(coreBuild, _paths.BinDir, dryRun,
                filter: name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            steps.Add($"Core 特权服务：{coreBuild} → {_paths.BinDir}");
        }

        // ── 5. 安装标记 ──
        if (!dryRun)
        {
            WriteMarker(sourceRoot, copiedTools, payloadCopied);
        }

        _log.Info(
            $"安装完成：{copiedTools} 个工具，{copied} 个文件{(dryRun ? "（试运行，未落盘）" : "")}",
            "install");

        return new InstallResult
        {
            SourceRoot = sourceRoot,
            TargetRoot = _paths.Root,
            Steps = steps,
            CopiedFiles = copied,
            CopiedTools = copiedTools,
            PayloadCopied = payloadCopied,
            DryRun = dryRun,
            Skipped = skipped,
        };
    }

    /// <summary>读取安装标记（没有则说明安装根不是通过本安装器铺的）。</summary>
    public JsonObject? ReadMarker()
    {
        var path = Path.Combine(_paths.Root, MarkerFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private void WriteMarker(string sourceRoot, int toolCount, bool payloadCopied)
    {
        var node = new JsonObject
        {
            ["schema"] = LayoutSchema,
            ["hostVersion"] = typeof(LayoutInstaller).Assembly.GetName().Version?.ToString(3),
            ["sourceRoot"] = sourceRoot,
            ["installedAt"] = DateTimeOffset.Now.ToString("O"),
            ["toolCount"] = toolCount,
            ["payloadCopied"] = payloadCopied,
            ["machine"] = Environment.MachineName,
        };

        File.WriteAllText(
            Path.Combine(_paths.Root, MarkerFileName),
            node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    // ── 文件复制助手 ──

    /// <summary>定位 Eztools.Core 最新的构建输出目录（含 ezt-core.exe）。没有返回 null。</summary>
    private static string? FindNewestCoreBuild(string sourceRoot)
    {
        var binRoot = Path.Combine(sourceRoot, "src", "Eztools.Core", "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        return Directory.GetFiles(binRoot, "ezt-core.exe", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName!)
            .OrderByDescending(dir => File.GetLastWriteTime(Path.Combine(dir, "ezt-core.exe")))
            .FirstOrDefault();
    }

    private static int CopyTree(string source, string target, bool dryRun, Func<string, bool>? filter = null)
    {
        if (!Directory.Exists(source))
        {
            return 0;
        }

        var count = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);

            // 跳过字节码缓存与版本控制目录：它们是本机产物，搬过去只会有害
            if (relative.Contains("__pycache__", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filter is not null && !filter(Path.GetFileName(file)))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            if (FilesAreSame(file, destination))
            {
                continue;
            }

            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }

            count++;
        }

        return count;
    }

    /// <summary>同名同大小同修改时间即视为相同，避免重复搬运。</summary>
    private static bool FilesAreSame(string source, string target)
    {
        try
        {
            if (!File.Exists(target))
            {
                return false;
            }

            var a = new FileInfo(source);
            var b = new FileInfo(target);
            return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }
}
