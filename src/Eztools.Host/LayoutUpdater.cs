// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json.Nodes;

namespace Eztools.Host;

/// <summary>一次「覆盖更新」的结果。</summary>
public sealed class UpdateResult
{
    public required string PackageRoot { get; init; }

    public required string TargetRoot { get; init; }

    public int ReplacedFiles { get; init; }

    public int AddedFiles { get; init; }

    /// <summary>被覆盖前备份的旧文件数（回滚用；<c>--no-backup</c> 时为 0）。</summary>
    public int BackedUpFiles { get; init; }

    /// <summary>备份目录（无备份时为 null）。</summary>
    public string? BackupDir { get; init; }

    public bool DryRun { get; init; }

    /// <summary>被跳过的路径（工具私有数据等受保护目录）。</summary>
    public IReadOnlyList<string> Preserved { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();
}

/// <summary>一次卸载的结果。</summary>
public sealed class UninstallResult
{
    public required string TargetRoot { get; init; }

    public int RemovedFiles { get; init; }

    /// <summary>被保留的用户数据（工具私有数据 / 配置），以及为什么保留。</summary>
    public IReadOnlyList<string> Kept { get; init; } = Array.Empty<string>();

    /// <summary>未能删除的路径（文件被占用等）—— 如实列出，不假装成功。</summary>
    public IReadOnlyList<string> Failed { get; init; } = Array.Empty<string>();

    public bool DryRun { get; init; }

    public bool PurgedUserData { get; init; }
}

/// <summary>
/// 便携分发的更新器与卸载器（P4 Wave 2b；决策 D1 = 自包含·非单文件，A3 = 便携 zip + 首次运行铺开）。
///
/// <b>为什么"更新"不能简单 rm -rf 再铺一遍</b>：安装根里混着三类东西，寿命完全不同：
/// <list type="bullet">
/// <item><b>程序</b>（<c>bin/ tools/ sdk/ payload/</c>）—— 更新时**整体替换**；</item>
/// <item><b>用户数据</b>（<c>toolsdata/</c> 工具私有数据、配置根下的 config/state）—— **绝不碰**，
///   这是 §13 反模式清单里"配置存在工具目录里 → 卸载即丢用户数据"的正面做法；</item>
/// <item><b>运行时</b>（<c>runtimes/</c>）—— **保留**，除非包内带了新的 payload 且显式要求重建
///   （几十 MB 的解压不该每次更新都重做）。</item>
/// </list>
///
/// 所以更新 = <b>备份程序部分 → 替换 → 保留用户数据与运行时</b>，而不是清空重铺。
/// </summary>
public sealed class LayoutUpdater
{
    /// <summary>
    /// 更新时**整体替换**的目录（相对安装根）。
    /// 刻意列成白名单而非"删除除 X 外的所有"——后者漏一个目录就会误删用户数据。
    /// </summary>
    private static readonly string[] ProgramDirs = { "bin", "tools", "sdk", "payload" };

    /// <summary>
    /// 更新时**绝不替换**的目录（相对安装根）。用户数据与运行时。
    /// </summary>
    private static readonly string[] PreservedDirs = { "toolsdata", "runtimes", "logs", "cache" };

    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;

    public LayoutUpdater(EztoolsPaths paths, HostLog log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>跨盘符移动用复制+删（<c>Directory.Move</c> 跨卷会抛）。</summary>
    private static void MoveOrCopy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // 跨卷：退化成复制
            CopyDirectory(source, target);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// 用一个新的便携包（<paramref name="packageRoot"/> = 解压后的包目录，其布局与安装根同构）
    /// 覆盖更新当前安装根。
    /// </summary>
    /// <param name="packageRoot">解压后的包根（含 bin/ tools/ sdk/ payload/）。</param>
    /// <param name="backup">是否在替换前把旧的程序目录备份到 <c>backup/&lt;时间戳&gt;</c>。</param>
    /// <param name="dryRun">只报告将做什么，不落盘。</param>
    public UpdateResult Update(string packageRoot, bool backup = true, bool dryRun = false)
    {
        packageRoot = PathInput.NormalizeToFullPath(packageRoot);
        if (!Directory.Exists(packageRoot))
        {
            throw new DirectoryNotFoundException($"包目录不存在: {packageRoot}");
        }

        var steps = new List<string>();
        var preserved = new List<string>();
        var replaced = 0;
        var added = 0;
        var backedUp = 0;
        string? backupDir = null;

        steps.Add($"包目录 {packageRoot} → 安装根 {_paths.Root}");

        // ── 1. 备份旧的程序目录（回滚用）──
        if (backup && !dryRun && Directory.Exists(_paths.Root))
        {
            backupDir = Path.Combine(_paths.Root, "backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (var name in ProgramDirs)
            {
                var src = Path.Combine(_paths.Root, name);
                if (!Directory.Exists(src))
                {
                    continue;
                }

                var dst = Path.Combine(backupDir, name);
                CopyDirectory(src, dst);
                backedUp += Directory.GetFiles(dst, "*", SearchOption.AllDirectories).Length;
            }

            if (backedUp > 0)
            {
                steps.Add($"已备份旧程序 {backedUp} 个文件 → {backupDir}");
            }
            else
            {
                Directory.Delete(backupDir, recursive: true);
                backupDir = null;
                steps.Add("旧的程序目录为空，无需备份");
            }
        }
        else if (!backup)
        {
            steps.Add("按参数要求跳过备份（--no-backup）");
        }

        // ── 2. 替换程序目录 ──
        foreach (var name in ProgramDirs)
        {
            var src = Path.Combine(packageRoot, name);
            var dst = Path.Combine(_paths.Root, name);

            // 记录"保留的目录"——它们不在替换名单里，明确列出来给用户看（不是漏掉）
            if (!Directory.Exists(src))
            {
                continue;
            }

            if (dryRun)
            {
                steps.Add($"[试运行] 将替换 {name}/（{Directory.GetFiles(src, "*", SearchOption.AllDirectories).Length} 个文件）");
                continue;
            }

            var existed = Directory.Exists(dst);
            var before = existed ? Directory.GetFiles(dst, "*", SearchOption.AllDirectories).Length : 0;

            // 先删后拷：避免旧版本残留的、新版已删除的文件继续存在（"降级残留"）
            if (existed)
            {
                Directory.Delete(dst, recursive: true);
            }

            CopyDirectory(src, dst);

            var after = Directory.GetFiles(dst, "*", SearchOption.AllDirectories).Length;
            if (existed)
            {
                replaced += Math.Min(before, after);
                added += Math.Max(0, after - before);
                steps.Add($"已替换 {name}/（旧 {before} → 新 {after} 个文件）");
            }
            else
            {
                added += after;
                steps.Add($"已新增 {name}/（{after} 个文件）");
            }
        }

        // ── 3. 明确报告保留的目录 ──
        foreach (var name in PreservedDirs)
        {
            var dir = Path.Combine(_paths.Root, name);
            if (Directory.Exists(dir))
            {
                preserved.Add(name);
            }
        }

        if (preserved.Count > 0)
        {
            steps.Add($"保留未动：{string.Join(" / ", preserved)}（用户数据与运行时，更新不该清掉）");
        }

        // ── 4. 更新安装标记（记下来源是便携包）──
        if (!dryRun)
        {
            WriteUpdateMarker(packageRoot);
        }

        _log.Info(
            $"更新完成：替换 {replaced} / 新增 {added} 个文件（备份 {backedUp}）{(dryRun ? "（试运行）" : "")}",
            "update");

        return new UpdateResult
        {
            PackageRoot = packageRoot,
            TargetRoot = _paths.Root,
            ReplacedFiles = replaced,
            AddedFiles = added,
            BackedUpFiles = backedUp,
            BackupDir = backupDir,
            DryRun = dryRun,
            Preserved = preserved,
            Steps = steps,
        };
    }

    /// <summary>
    /// 卸载：删除程序目录，**按策略决定**是否连用户数据一起删。
    ///
    /// <b>默认保留用户数据</b>——这与 §13「配置存在工具目录里 → 卸载即丢用户数据」是同一件事的两面：
    /// 卸载时把用户数据一起删，用户下次装回来就什么都没了。要连数据一起清必须显式 <c>--purge-data</c>。
    /// </summary>
    /// <param name="purgeUserData">true = 连 toolsdata/ 与配置根一起删（不可恢复，需显式指定）。</param>
    public UninstallResult Uninstall(bool purgeUserData = false, bool dryRun = false)
    {
        var removed = 0;
        var kept = new List<string>();
        var failed = new List<string>();

        // ── 1. 程序目录（含运行时？不 —— 运行时单独决定）──
        //    运行时默认删（它属于"程序"而非"用户数据"，且能重新部署），但如果要保留用户数据，
        //    运行时其实也无所谓；这里选"删"，理由：卸载就是卸载，留着几十 MB 无主运行时是垃圾。
        //    backup/ 也一起删：那是"更新前备份"，卸载时留着毫无意义（用户要回滚就不会卸载）。
        string[] programDirs = { "bin", "tools", "sdk", "payload", "runtimes", "cache", "logs", "backup" };

        foreach (var name in programDirs)
        {
            var dir = Path.Combine(_paths.Root, name);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            if (dryRun)
            {
                removed += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                continue;
            }

            try
            {
                removed += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                // 文件被占用是常见情况（托盘还在跑、工具进程没退）——如实列出而不是假装成功
                failed.Add($"{name}/（{ex.GetType().Name}：{ex.Message}）");
            }
        }

        // ── 2. 用户数据 ──
        var toolData = _paths.ToolsDataDir;
        if (purgeUserData)
        {
            if (Directory.Exists(toolData))
            {
                if (dryRun)
                {
                    removed += Directory.GetFiles(toolData, "*", SearchOption.AllDirectories).Length;
                }
                else
                {
                    try
                    {
                        removed += Directory.GetFiles(toolData, "*", SearchOption.AllDirectories).Length;
                        Directory.Delete(toolData, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"toolsdata/（{ex.GetType().Name}：{ex.Message}）");
                    }
                }
            }

            // 配置根（含 config/ 与 state.json）
            if (Directory.Exists(_paths.ConfigRoot))
            {
                if (dryRun)
                {
                    removed += Directory.GetFiles(_paths.ConfigRoot, "*", SearchOption.AllDirectories).Length;
                }
                else
                {
                    try
                    {
                        removed += Directory.GetFiles(_paths.ConfigRoot, "*", SearchOption.AllDirectories).Length;
                        Directory.Delete(_paths.ConfigRoot, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"配置根（{ex.GetType().Name}：{ex.Message}）");
                    }
                }
            }
        }
        else
        {
            // 默认：保留，并**说清在哪、怎么删** —— 用户要知道数据还在
            if (Directory.Exists(toolData))
            {
                kept.Add($"工具私有数据 {toolData}");
            }

            if (Directory.Exists(_paths.ConfigRoot))
            {
                kept.Add($"配置与状态 {_paths.ConfigRoot}");
            }
        }

        // ── 3. 收尾：删掉安装根与 install.json（只有当安装根已空）──
        if (!dryRun)
        {
            foreach (var marker in new[] { LayoutInstaller.MarkerFileName, "update.json" })
            {
                var path = Path.Combine(_paths.Root, marker);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{marker}（{ex.GetType().Name}：{ex.Message}）");
                    }
                }
            }

            // 安装根若已空则一并删掉（有残留就留着，别硬删 —— 硬删可能删掉用户手工放的东西）
            if (Directory.Exists(_paths.Root))
            {
                var leftovers = Directory.EnumerateFileSystemEntries(_paths.Root)
                    .Select(Path.GetFileName)
                    .ToArray();

                if (leftovers.Length == 0)
                {
                    try
                    {
                        Directory.Delete(_paths.Root);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"安装根（{ex.GetType().Name}：{ex.Message}）");
                    }
                }
                else
                {
                    // 不带 --purge-data 时，残留的就是被刻意保留的用户数据 —— 说清是哪些，
                    // 而不是含糊的"含未删除项"（后者看起来像卸载失败）。
                    kept.Add($"安装根残留 {_paths.Root}（{string.Join(" / ", leftovers)}）");
                }
            }
        }

        _log.Info(
            $"卸载完成：删除 {removed} 个文件，保留 {kept.Count} 项，失败 {failed.Count} 项"
            + $"{(dryRun ? "（试运行）" : "")}",
            "uninstall");

        return new UninstallResult
        {
            TargetRoot = _paths.Root,
            RemovedFiles = removed,
            Kept = kept,
            Failed = failed,
            DryRun = dryRun,
            PurgedUserData = purgeUserData,
        };
    }

    private void WriteUpdateMarker(string packageRoot)
    {
        var path = Path.Combine(_paths.Root, "update.json");
        var node = new JsonObject
        {
            ["schema"] = LayoutInstaller.LayoutSchema,
            ["hostVersion"] = typeof(LayoutUpdater).Assembly.GetName().Version?.ToString(3),
            ["packageRoot"] = packageRoot,
            ["updatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["machine"] = Environment.MachineName,
        };

        File.WriteAllText(
            path,
            node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }
}
