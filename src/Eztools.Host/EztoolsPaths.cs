// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host;

/// <summary>
/// 真实安装布局（P0 落地的目录结构）。
///
/// <code>
/// %LOCALAPPDATA%\Eztools\            ← Root：程序与运行时（可整体重装而不丢数据）
/// ├── bin\                            宿主可执行（ezt.exe / Eztools.Host.dll / 将来的 WinUI 应用）
/// ├── tools\                          内置工具（随产品发布，清单驱动发现）
/// ├── sdk\python\eztools\             Python SDK 源码，首次部署运行时植入其 site-packages
/// ├── payload\                        运行时压缩包（约 15~20MB）
/// ├── runtimes\python\&lt;version&gt;\      解压后的隔离运行时
/// ├── logs\                           宿主日志 + 按工具分文件
/// ├── toolsdata\&lt;toolId&gt;\data\       工具私有数据（卸载工具不丢）
/// └── cache\                          临时解压、其他缓存
///
/// %APPDATA%\Eztools\                 ← ConfigRoot：用户数据（漫游、与程序分离）
/// ├── config\&lt;toolId&gt;.json           工具配置（P1 配置中心）
/// └── state.json                      宿主状态（启用/禁用等）
/// </code>
///
/// 设计要点：**配置、私有数据与代码三处分离**（设计方案 §6.2）。禁用或移除工具不丢用户数据；
/// 重装程序不丢配置。
/// </summary>
public sealed class EztoolsPaths
{
    public const string AppFolderName = "Eztools";

    /// <summary>覆盖安装根（测试与共存安装用）。</summary>
    public const string EnvInstallRoot = "EZTOOLS_INSTALL_ROOT";

    /// <summary>覆盖配置根（测试用）。</summary>
    public const string EnvConfigRoot = "EZTOOLS_CONFIG_ROOT";

    private EztoolsPaths(string root, string configRoot)
    {
        Root = root;
        ConfigRoot = configRoot;
    }

    /// <summary>安装根（默认 <c>%LOCALAPPDATA%\Eztools</c>）。</summary>
    public string Root { get; }

    /// <summary>配置根（默认 <c>%APPDATA%\Eztools</c>）。</summary>
    public string ConfigRoot { get; }

    public string BinDir => Path.Combine(Root, "bin");

    public string BuiltinToolsDir => Path.Combine(Root, "tools");

    public string SdkDir => Path.Combine(Root, "sdk");

    public string PayloadDir => Path.Combine(Root, "payload");

    public string RuntimesDir => Path.Combine(Root, "runtimes");

    public string LogsDir => Path.Combine(Root, "logs");

    public string ToolsDataDir => Path.Combine(Root, "toolsdata");

    public string CacheDir => Path.Combine(Root, "cache");

    public string ConfigDir => Path.Combine(ConfigRoot, "config");

    /// <summary>启动器数据目录（W7-e D6=B：频次文件 <c>launcher/usage.json</c> 的父目录）。</summary>
    public string LauncherDataDir => Path.Combine(ConfigRoot, "launcher");

    public string StateFile => Path.Combine(ConfigRoot, "state.json");

    /// <summary>宿主自身的日志文件（按日期分文件）。</summary>
    public string HostLogFile => Path.Combine(LogsDir, $"host-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// 带宿主名的日志文件。**多个宿主进程共用 logs 目录时必须用它** ——
    /// 否则 CLI 与托盘会写进同一个文件，日志交错、排查困难（设计方案 §5.2 的风险项）。
    /// 注意路径每次都要重算（不缓存）：长驻宿主跨午夜时会自动换到新日期的文件。
    /// </summary>
    public string HostLogFileFor(string hostName) =>
        Path.Combine(LogsDir, $"host-{hostName}-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>某个运行时的安装目录。</summary>
    public string RuntimeDir(string runtime, string version) =>
        Path.Combine(RuntimesDir, runtime, version);

    /// <summary>某个工具日志目录。</summary>
    public string ToolLogDir(string toolId) => Path.Combine(LogsDir, toolId);

    /// <summary>某个工具日志文件。</summary>
    public string ToolLogFile(string toolId) =>
        Path.Combine(ToolLogDir(toolId), $"{toolId}-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>某个工具的私有数据目录。</summary>
    public string ToolDataDir(string toolId) => Path.Combine(ToolsDataDir, toolId, "data");

    /// <summary>某个工具的私有 KV 存储文件。</summary>
    public string ToolStorageFile(string toolId) =>
        Path.Combine(ToolsDataDir, toolId, "storage.json");

    /// <summary>某个工具的配置文件。</summary>
    public string ToolConfigFile(string toolId) => Path.Combine(ConfigDir, toolId + ".json");

    public static EztoolsPaths Create(string? installRoot = null, string? configRoot = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var root = installRoot
                   ?? Environment.GetEnvironmentVariable(EnvInstallRoot)
                   ?? Path.Combine(localAppData, AppFolderName);

        var configs = configRoot
                      ?? Environment.GetEnvironmentVariable(EnvConfigRoot)
                      ?? Path.Combine(appData, AppFolderName);

        // 归一化：Git Bash 里传 /d/... 也能用（见 PathInput 的说明）
        return new EztoolsPaths(
            Path.GetFullPath(PathInput.Normalize(root)),
            Path.GetFullPath(PathInput.Normalize(configs)));
    }

    /// <summary>
    /// 幂等创建全部目录，返回实际新建的目录列表（用于首次运行的部署日志）。
    /// 只创建目录、不删除任何东西——卸载清理是安装器的职责。
    /// </summary>
    public IReadOnlyList<string> EnsureDirectories()
    {
        string[] required =
        {
            Root,
            BinDir,
            BuiltinToolsDir,
            SdkDir,
            PayloadDir,
            RuntimesDir,
            LogsDir,
            ToolsDataDir,
            CacheDir,
            ConfigRoot,
            ConfigDir,
        };

        var created = new List<string>();
        foreach (var dir in required)
        {
            if (Directory.Exists(dir))
            {
                continue;
            }

            Directory.CreateDirectory(dir);
            created.Add(dir);
        }

        return created;
    }

    /// <summary>是否为"首次运行"（安装根尚不存在）。</summary>
    public bool IsFirstRun => !Directory.Exists(Root);

    public override string ToString() => $"安装根={Root} 配置根={ConfigRoot}";
}
