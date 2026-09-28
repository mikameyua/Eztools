// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Eztools.Host;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt diag</c> —— 打包诊断信息（P2 尾巴："日志与诊断包导出"）。
///
/// 收集范围（刻意最小化，见关键决策）：
/// ① <c>manifest.json</c>：宿主版本 / OS / .NET / DOTNET_ROOT / 工具清单；
/// ② <c>logs/</c> 全部（宿主日志、Core 审计、工具日志）；
/// ③ <c>config/</c> 全部（工具配置 + state.json）。
///
/// 关键决策（2026-09-20）：
/// - **不复刻 doctor 全量输出**：doctor 实时可看且会过期，诊断包专注"持久化产物"（日志/配置），
///   环境快照只取最小集合进 manifest；
/// - 输出默认落 `<安装根>\exports\`（安装根在 EnsureDirectories 里不含该目录，按需创建）；
///   `--out` 可指到任意路径。
///
/// 脱敏（2026-09-21 补，清单 C4）：
/// - **默认仍未脱敏**（本地工具、用户自选发送对象，原样打包便于排查）；
/// - 但**默认就会打印"包里有哪些敏感内容"** —— 让"手一滑发出去"这件事在发之前被看见；
/// - `--redact` 会把用户名 / 绝对路径替换成占位符后再打包。
///   为什么必须做：诊断包**天生是要发给别人看的**，未脱敏等于埋了一颗"哪天顺手一贴就漏内部路径"的雷。
/// </summary>
internal static class DiagCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        // 复用宿主清单发现（含开发形态的仓库回退）——manifest 工具清单由此而来
        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        var paths = host.Paths;

        var redact = cli.GetBool("redact");
        var redactor = redact ? Redactor.Build(paths) : null;

        var outPath = cli.Get("out");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            var exports = Path.Combine(paths.Root, "exports");
            Directory.CreateDirectory(exports);
            outPath = Path.Combine(exports, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        }
        else
        {
            outPath = Path.GetFullPath(PathInput.Normalize(outPath));
        }

        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        if (File.Exists(outPath))
        {
            ConsoleUi.Error($"目标已存在：{outPath}（换 --out 或删除后重试）");
            return 1;
        }

        var entries = 0;
        long totalBytes = 0;

        try
        {
            using var stream = new FileStream(outPath, FileMode.CreateNew);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            // ① manifest：环境快照 + 工具清单
            var manifest = new JsonObject
            {
                ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
                ["hostVersion"] = typeof(DiagCommand).Assembly.GetName().Version?.ToString(),
                ["dotnet"] = Environment.Version.ToString(),
                ["os"] = Environment.OSVersion.VersionString,
                ["machine"] = redactor is null ? Environment.MachineName : Redactor.MachinePlaceholder,
                ["dotnetRoot"] = redactor?.Text(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? ""),
                ["installRoot"] = redactor?.Text(paths.Root) ?? paths.Root,
                ["configRoot"] = redactor?.Text(paths.ConfigRoot) ?? paths.ConfigRoot,
                ["redacted"] = redact,
                ["tools"] = ToolInventory(host),
            };
            WriteJson(zip, "manifest.json", manifest);
            entries++;

            // ② logs/（含子目录：工具日志、Core 审计）
            entries += Collect(zip, sourceDir: paths.LogsDir, arcPrefix: "logs", redactor);

            // ③ config/（工具配置 + state.json）
            entries += Collect(zip, sourceDir: paths.ConfigDir, arcPrefix: "config", redactor);
            if (File.Exists(paths.StateFile))
            {
                AddFile(zip, paths.StateFile, "config/state.json", redactor);
                entries++;
            }

            totalBytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex)
        {
            ConsoleUi.Error($"诊断包生成失败：{ex.Message}");
            try { File.Delete(outPath); }
            catch
            {
                // review-guards:allow-empty-catch :: 半成品一并清理，失败忽略
            }
            return 1;
        }

        ConsoleUi.Ok($"诊断包已生成：{outPath}");
        ConsoleUi.Line($"        {entries} 个条目，{totalBytes / 1024.0:0.#} KB");

        if (redact)
        {
            ConsoleUi.Line("        ✅ 已脱敏：用户名 / 机器名 / 绝对路径已替换为占位符");
            ConsoleUi.Line("           （日志里的**业务内容**原样保留 —— 脱敏只针对身份与路径，不改变可排查性）");
        }
        else
        {
            // 未脱敏时必须把"包里有什么"摆到眼前：诊断包天生是发给别人看的，
            // 让敏感内容在发送前被看见，比事后追悔便宜得多。
            ConsoleUi.Line("        ⚠️ 未脱敏（本地原样打包）。包内含以下敏感内容，发送前请自行过目：");
            foreach (var item in SensitiveInventory(paths))
            {
                ConsoleUi.Line($"           · {item}");
            }

            ConsoleUi.Line("        想自动处理：加 --redact（用户名 / 机器名 / 绝对路径 → 占位符）");
        }

        return 0;
    }

    /// <summary>列出"这个包里有哪些敏感内容"，用于未脱敏时的前置提示。</summary>
    private static IEnumerable<string> SensitiveInventory(EztoolsPaths paths)
    {
        yield return $"当前用户名：{Redactor.CurrentUser()}（出现在日志/配置的路径中）";
        yield return $"机器名：{Environment.MachineName}（写在 manifest.machine）";
        yield return $"安装根绝对路径：{paths.Root}";
        yield return $"配置根绝对路径：{paths.ConfigRoot}";
        yield return "工具私有数据目录名（toolsdata/ 下若含业务文件名）";
    }

    /// <summary>工具清单直接取宿主清单发现结果（含开发形态的仓库回退）。</summary>
    private static JsonArray ToolInventory(EztoolsHost host)
    {
        var arr = new JsonArray();
        foreach (var m in host.LastDiscovery.Manifests)
        {
            arr.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["version"] = m.Version,
            });
        }
        return arr;
    }

    /// <summary>把 sourceDir 下全部文件收进 zip 的 arcPrefix/ 前缀下，返回条目数。</summary>
    private static int Collect(ZipArchive zip, string sourceDir, string arcPrefix, Redactor? redactor)
    {
        if (!Directory.Exists(sourceDir))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file)
                .Replace('\\', '/');
            AddFile(zip, file, $"{arcPrefix}/{rel}", redactor);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 把单个文件写进 zip。脱敏模式下走内存：文本类替换后写入，二进制类原样写入。
    /// 为什么不统一按字节处理：日志/配置都是 UTF-8 文本，按文本替换才有意义；
    /// 而 Core 的 trace 与审计也是文本，本包里没有真正的二进制产物（都是 .log / .json）。
    /// </summary>
    private static void AddFile(ZipArchive zip, string sourcePath, string entryName, Redactor? redactor)
    {
        if (redactor is null)
        {
            zip.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
            return;
        }

        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var output = entry.Open();

        try
        {
            var text = File.ReadAllText(sourcePath);
            var redacted = redactor.Text(text);
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            writer.Write(redacted);
        }
        catch (Exception)
        {
            // 读不出来（被占用 / 非文本）→ 退回原样字节，至少不丢内容
            output.Position = 0;
            using var source = File.OpenRead(sourcePath);
            source.CopyTo(output);
        }
    }

    private static void WriteJson(ZipArchive zip, string entryName, JsonObject node)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(node.ToJsonString(new() { WriteIndented = true }));
    }

    /// <summary>
    /// 诊断包脱敏器（<c>--redact</c>）。
    ///
    /// 设计取舍：**只处理"身份 + 绝对路径"，不动业务内容** ——
    /// 脱敏的目的是让包能安全地发给别人，而不是让它变得没法排查；
    /// 把业务内容也洗掉等于把诊断包变成废纸（那才是最糟的结果：看起来安全，实际没用）。
    /// </summary>
    private sealed class Redactor
    {
        internal const string MachinePlaceholder = "<machine>";

        private readonly (string Needle, string Replacement)[] _rules;

        private Redactor((string, string)[] rules) => _rules = rules;

        internal static string CurrentUser() =>
            Environment.UserName is { Length: > 0 } name ? name : "<unknown>";

        /// <summary>
        /// 构造替换规则。注意**顺序**：先换更长、更具体的路径，再换用户名 ——
        /// 反过来的话，用户名先被换成 <user>，路径里就再也匹配不到它了。
        /// </summary>
        internal static Redactor Build(EztoolsPaths paths)
        {
            var user = CurrentUser();
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var temp = Path.GetTempPath().TrimEnd('\\', '/');

            var rules = new List<(string, string)>
            {
                // 1) 已知的绝对路径（最长最具体 → 最优先）
                (paths.Root, "<installRoot>"),
                (paths.ConfigRoot, "<configRoot>"),
                (paths.LogsDir, "<installRoot>/logs"),
                (paths.ToolsDataDir, "<toolsdata>"),

                // 2) 用户主目录（把 <user> 一并吃掉，避免残留 "C:\Users\<user>"）
                (profile, "<home>"),

                // 3) 临时目录是 MSYS / WorkBuddy 的固定前缀，本身不含身份，换了也无害
                (temp, "<tmp>"),

                // 4) 环境变量里的机器名 / 用户名（路径之外的出现位置，如 host-ezt-core-<user>.log）
                (Environment.MachineName, MachinePlaceholder),
                (user, "<user>"),
            };

            // 去掉空规则（paths 里可能有空串），按 Needle 长度降序 —— 长匹配优先
            return new Redactor(rules
                .Where(r => !string.IsNullOrEmpty(r.Item1))
                .OrderByDescending(r => r.Item1.Length)
                .ToArray());
        }

        /// <summary>对文本做逐条替换（大小写不敏感，覆盖 Windows 路径大小写不一致）。</summary>
        internal string Text(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var result = text;
            foreach (var (needle, replacement) in _rules)
            {
                result = result.Replace(needle, replacement, StringComparison.OrdinalIgnoreCase);
            }

            return result;
        }
    }
}
