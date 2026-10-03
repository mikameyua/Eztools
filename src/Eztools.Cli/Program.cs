// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Processes;
using Eztools.Host.Registry;
using Eztools.Host.Runtimes;

namespace Eztools.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ConsoleUi.Prepare();
        var cli = new CliArgs(args);

        var command = cli.Command;
        try
        {
            // W4-a：`--probe-ocr` 顶层探针别名（等价 `ezt ocr probe`）。
            // 必须在 switch 之前接住 —— 纯 flag 调用时 command 为 null，会被 case null 当成 help 吞掉。
            if (cli.GetBool("probe-ocr"))
            {
                return await OcrCommand.RunAsync(cli);
            }

            switch (command)
            {
                case null:
                case "help":
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;

                case "version":
                case "--version":
                    Console.WriteLine($"ezt {HostVersion()}");
                    Console.WriteLine($".NET {Environment.Version}   RID {NativeRid.Current}");
                    return 0;

                case "dirs":
                    return await DirsAsync(cli);

                case "doctor":
                    return await DoctorAsync(cli);

                case "list":
                    return await ListAsync(cli);

                case "info":
                    return await InfoAsync(cli);

                case "install":
                    return await InstallAsync(cli);

                case "update":
                    return await UpdateAsync(cli);

                case "uninstall":
                    return await UninstallAsync(cli);

                case "runtime":
                    return await RuntimeAsync(cli);

                case "invoke":
                    return await InvokeAsync(cli);

                case "call":
                    return await CallAsync(cli);

                case "enable":
                    return await SetEnabledAsync(cli, true);

                case "disable":
                    return await SetEnabledAsync(cli, false);

                case "config":
                    return await ConfigCommand.RunAsync(cli);

                case "hotkeys":
                case "hotkey":
                    return await HotkeysCommand.RunAsync(cli);

                case "tray":
                    return await TrayCommand.RunAsync(cli);

                case "panel":
                case "panels":
                    return await PanelCommand.RunAsync(cli);

                case "core":
                    return await CoreCommand.RunAsync(cli);

                case "search":
                    return await SearchCommand.RunAsync(cli);

                case "ocr":
                    return await OcrCommand.RunAsync(cli);

                case "clip":
                    return await ClipCommand.RunAsync(cli);

                case "diag":
                    return await DiagCommand.RunAsync(cli);

                case "primitive":
                    return await PrimitiveCommand.RunAsync(cli);

                case "selftest":
                    return await SelfTestCommand.RunAsync(cli);

                default:
                    ConsoleUi.Error($"未知命令: {command}");
                    Console.WriteLine();
                    PrintHelp();
                    return 64;
            }
        }
        catch (ToolProtocolException ex)
        {
            ConsoleUi.Error(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            ConsoleUi.Error(ex.Message);
            if (cli.GetBool("verbose"))
            {
                Console.WriteLine(ex);
            }

            return 3;
        }
    }

    /// <summary>字符串 → JsonNode 的简写。JsonValue.Create 返回可空类型，这里收口一次。</summary>
    private static JsonNode J(string value) => JsonValue.Create(value)!;

    private static string HostVersion() =>
        typeof(EztoolsHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    // ──────────────────────────────────────────────────────────── 帮助

    private static void PrintHelp()
    {
        Console.WriteLine($"""
ezt —— Eztools 宿主命令行 ({HostVersion()})

用法: ezt <命令> [参数] [--选项]

安装与诊断
  install                   把源码形态铺成已安装形态（tools/ sdk/ payload/ bin/ → 安装根）
  update --from <包|zip>    用便携包覆盖更新（替换 bin/tools/sdk/payload，保留用户数据与运行时）
  uninstall [--yes]         卸载（默认保留用户数据；--purge-data 连数据一起删；--dry-run 先看）
  doctor                    环境与安装体检：目录结构、安装完整度、运行时、清单诊断、资源冲突
  dirs                      打印真实安装目录结构
  version                   版本信息

工具
  list                      列出已发现的工具（清单驱动）
  list --commands           列出全部可调用命令
  info <toolId>             工具详情：贡献点、配置 schema、路径
  enable  <toolId>          启用工具
  disable <toolId>          禁用工具（--reason <原因>）

调用
  invoke <commandId>        调用命令，例如: ezt invoke echo.echo --text 你好
  call   <toolId> <handler> 直接调用 handler（调试用）
  hotkeys                   热键列表 + 仲裁归属（--json 机器可读）
  hotkey set/unset          用户改键（写 state.json，托盘刷新后生效）

  selftest                  端到端自检：往返 / UTF-8 / 大 payload / 崩溃 / 超时回收

搜索索引 (W3)
  search status             索引到底长什么样：就绪 / 总条目 / 卷清单 / 跳过 / 失败 + 守恒自检
                            （起一个临时索引实例问清就退出；--wait-ready <ms> 等待自检完成）
  search pause | resume     不提供 —— 索引进程是宿主的 stdio 子进程，无跨进程端点，
                            CLI 只能改到自己临时起的实例。请用托盘菜单「搜索索引」或
                            搜索窗底部的「索引已暂停」徽标（点击即恢复）。

屏幕 OCR (W4)
  ocr langs                 可用 OCR 语言包清单（--json 机器可读；缺失时给安装引导，退出码 7）
  ocr file <图片路径>       本地图片 OCR（--lang <tag>；--json → chars/lines/elapsedMs/text）
  ocr probe / --probe-ocr   引擎自检：语言数 + 内置样图识别字数 + 耗时（--json 可断言；
                            --save-sample <png> 导出内置样图供 ocr file 复测）

剪贴板历史 (W5-a)
  clip capture              读一次当前剪贴板入库（调试/探针出口；常驻监听 W5-c 接线）
  clip list                 历史列表（--kind text|image|filelist；--limit N；--json）
  clip search <关键词>      搜索（≥3 字符 FTS trigram / <3 字符 LIKE 兜底；--json）
  clip pin|unpin <id>       置顶 / 取消（置顶条目不被清理删除）
  clip delete <id>          单条删除（图片条目连带删盘上文件）
  clip copy <id>            重新复制回剪贴板（W5-a 仅文本条目）
  clip clear [--keep-pinned]  清空历史
  clip status               库状态（总数 / 分类型 / 置顶 / 库与图片体积；--json 可断言）

特权层 (P3)
  core status               Core 特权服务状态（端点登记 + 管道探测）
  core start [--elevate]    启动 ezt-core（--elevate 走 UAC 以管理员运行）
  core stop / ping          优雅停止 / 存活探测
  core install-task         登记计划任务：登录自启 + 最高权限（免每次 UAC；需管理员）
  core uninstall-task       删除计划任务
  diag [--out <zip>]        打包诊断信息（manifest + logs/ + config/）用于问题上报
                            --redact 脱敏（用户名/机器名/绝对路径 → 占位符）
  primitive <name>          直调特权原语（调试用），如: ezt primitive volume.enumerate
                            参数用 --json '{"pid":123}' 传入

入口
  tray                      托盘菜单预览：由工具清单的 menus 贡献点自动合成
                            （--json 结构化输出 / --check 静态检查；真正的托盘由 Eztools.Desktop 启动）
  panel                     面板列表：由工具清单的 panels 贡献点自动合成
  panel <tool>              只看某个工具的面板
  panel <tool> <panel>      拉取面板数据（--json 默认 / --text 缩进树）
                            面板仅对 weight: full 的工具可用

运行时
  runtime list              已部署的运行时
  runtime payloads          可用的运行时载荷
  runtime install [name]    从载荷部署运行时（--force 强制重装）

选项
  --tools-dir <路径>        额外工具目录源（分号分隔），覆盖/追加内置源
  --install-root <路径>     覆盖安装根（默认 %LOCALAPPDATA%\Eztools）
  --config-root <路径>      覆盖配置根（默认 %APPDATA%\Eztools）
  --json                    机器可读输出
  --compact                 结果 JSON 单行输出
  --verbose                 详细日志（含调试级与 IPC 耗时）
  --quiet                   不把宿主日志回显到控制台
  --idle-recycle <秒>       空闲回收秒数；0 = 用完即收（默认）

install 专有选项
  --from <源码根>           源目录（默认自动定位：环境变量 EZTOOLS_SOURCE_ROOT 或向上找仓库根）
  --no-payload              不复制运行时载荷（约 13~20MB）
  --dry-run                 只报告会复制什么，不落盘

update 专有选项
  --from <包目录|zip>       新版本（zip 会自动解压到临时目录）
  --no-backup               不备份旧程序目录（默认备份到 <安装根>\backup\<时间戳>）
  --dry-run                 只报告会替换什么，不落盘

uninstall 专有选项
  --yes                     确认执行（非试运行时必填，防脚本手滑）
  --purge-data              **连用户数据与配置一起删**（默认保留）
  --dry-run                 只报告会删什么，不落盘
""");
    }

    // ──────────────────────────────────────────────────────────── 宿主装配

    /// <summary>
    /// 各子命令共用的宿主工厂。
    /// **做成 internal 就是为了只有这一份** —— 之前 <c>SelfTestCommand</c> 与 <c>ConfigCommand</c>
    /// 各自抄过一份，三份 <c>HostOptions</c> 迟早会漂移（比如新加一个路径覆盖只改了两处）。
    /// </summary>
    internal static async Task<EztoolsHost> CreateHostAsync(
        CliArgs cli,
        bool? echoLog = null,
        string hostName = "ezt")
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));

        var processOptions = ToolHostOptions.ForCli;
        if (cli.Get("idle-recycle") is { } recycle && double.TryParse(recycle, out var seconds))
        {
            processOptions = new ToolHostOptions { IdleRecycle = TimeSpan.FromSeconds(seconds) };
        }

        var options = new HostOptions
        {
            Paths = paths,
            ToolsDir = cli.Get("tools-dir"),
            Verbose = cli.GetBool("verbose") || cli.GetBool("v"),
            EchoLogToConsole = echoLog ?? !(cli.GetBool("json") || cli.GetBool("quiet")),
            HostName = hostName,
            ProcessOptions = processOptions,
        };

        return await EztoolsHost.CreateAsync(options);
    }

    // ──────────────────────────────────────────────────────────── dirs

    private static Task<int> DirsAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));

        var entries = new (string Name, string Path, string Note)[]
        {
            ("安装根", paths.Root, "程序与运行时，可整体重装而不丢数据"),
            ("宿主目录", paths.BinDir, "ezt.exe / Eztools.Host.dll / 将来的 WinUI 应用"),
            ("内置工具", paths.BuiltinToolsDir, "清单驱动发现，新增工具 = 新增目录"),
            ("SDK", paths.SdkDir, "Python SDK 源码，部署运行时时植入其 site-packages"),
            ("载荷", paths.PayloadDir, "运行时压缩包（python-<版本>-<rid>.tar.gz）"),
            ("运行时", paths.RuntimesDir, "解压后的隔离运行时（每个工具进程用它启动）"),
            ("日志", paths.LogsDir, "宿主日志 + 按工具分文件"),
            ("工具数据", paths.ToolsDataDir, "工具私有数据，与代码目录分离"),
            ("缓存", paths.CacheDir, "临时解压等"),
            ("配置根", paths.ConfigRoot, "用户数据，与程序目录分离"),
            ("工具配置", paths.ConfigDir, "P1 配置中心：<toolId>.json"),
            ("宿主状态", paths.StateFile, "启用/禁用、热键覆盖"),
        };

        if (cli.GetBool("json"))
        {
            var obj = new JsonObject();
            foreach (var (name, path, note) in entries)
            {
                obj[name] = new JsonObject
                {
                    ["path"] = path,
                    ["exists"] = Directory.Exists(path) || File.Exists(path),
                    ["note"] = note,
                };
            }

            ConsoleUi.PrintJson(obj, cli.GetBool("compact"));
            return Task.FromResult(0);
        }

        ConsoleUi.Header("Eztools 安装目录结构");
        foreach (var (name, path, note) in entries)
        {
            var exists = Directory.Exists(path) || File.Exists(path);
            ConsoleUi.Field(name, $"{(exists ? "✓" : "·")} {path}");
        }

        Console.WriteLine();
        ConsoleUi.Info("首次运行会自动创建以上目录；当前未创建的是尚未用到的部分。");
        ConsoleUi.Info("注意配置根在 %APPDATA%，与程序目录分离：重装程序不丢配置，卸载工具不丢数据。");

        return Task.FromResult(0);
    }

    // ──────────────────────────────────────────────────────────── doctor

    private static async Task<int> DoctorAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        await using var host = await CreateHostAsync(cli, echoLog: false);
        var paths = host.Paths;

        var json = new JsonObject();

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Header("Eztools 体检");
            ConsoleUi.Field("宿主版本", HostVersion());
            ConsoleUi.Field("安装根", paths.Root);
            ConsoleUi.Field("配置根", paths.ConfigRoot);
            ConsoleUi.Field("本机 RID", NativeRid.Current);
            ConsoleUi.Field(".NET", Environment.Version.ToString());
            ConsoleUi.Field("首次运行", paths.IsFirstRun ? "是" : "否");
        }

        // ── 目录 ──
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("目录结构");
            var created = host.CreatedDirectories;
            if (created.Count > 0)
            {
                ConsoleUi.Ok($"本次新建 {created.Count} 个目录");
                foreach (var dir in created)
                {
                    ConsoleUi.Info(dir);
                }
            }
            else
            {
                ConsoleUi.Info("目录结构已完整");
            }
        }

        // ── 运行时 ──
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("运行时");
        }

        var provisioner = host.Runtimes.Provisioner;
        var installed = provisioner.GetInstalled();
        var payloads = provisioner.DiscoverPayloads();
        var sdkDir = provisioner.ResolveSdkPackageDir();

        var runtimeJson = new JsonArray();
        foreach (var installation in installed)
        {
            runtimeJson.Add(new JsonObject
            {
                ["runtime"] = installation.Runtime,
                ["version"] = installation.Version,
                ["root"] = installation.Root,
                ["executable"] = installation.ExecutablePath,
                ["usable"] = installation.IsUsable,
                ["embedded"] = installation.IsEmbedded,
                ["origin"] = installation.Origin,
            });
        }

        var payloadJson = new JsonArray();
        foreach (var payload in payloads)
        {
            payloadJson.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(payload.ArchivePath),
                ["runtime"] = payload.Runtime,
                ["version"] = payload.Version,
                ["rid"] = payload.Rid,
                ["size"] = payload.SizeBytes,
            });
        }

        if (!cli.GetBool("json"))
        {
            if (installed.Count == 0)
            {
                ConsoleUi.Warn("尚无已部署的运行时");
            }
            else
            {
                ConsoleUi.Table(
                    new[] { "运行时", "版本", "隔离", "可用", "来源" },
                    installed.Select(i => new[]
                    {
                        i.Runtime,
                        i.Version,
                        i.IsEmbedded ? "是" : "否(外部覆盖)",
                        i.IsUsable ? "✓" : "✗",
                        i.Origin,
                    }).ToList());
            }

            if (payloads.Count == 0)
            {
                ConsoleUi.Warn("未发现运行时载荷（payload/ 下无 <runtime>-<version>-<rid>.tar.gz）");
                ConsoleUi.Info("用 scripts/make-payload.sh 生成；或临时设 EZTOOLS_PYTHON 指向本机 Python 调试");
            }
            else
            {
                ConsoleUi.Field("可用载荷", $"{payloads.Count} 个");
                foreach (var payload in payloads)
                {
                    ConsoleUi.Info($"{Path.GetFileName(payload.ArchivePath)}  {RuntimeProvisioner.FormatBytes(payload.SizeBytes)}");
                }
            }

            ConsoleUi.Field("SDK 源码", sdkDir ?? "✗ 未找到（工具将无法 import eztools）");
        }

        // ── 安装与来源 ──
        // 这一节回答两个问题：
        //   ① 每一样东西**有没有可用的**（而不是"在不在安装根里"）
        //   ② 它**从哪来**（安装根 / 仓库）
        // 早先的版本只检查"安装根里有没有"，于是在开发机上会连报 4 条"缺失"——
        // 而实际上工具、SDK、载荷都好好地来自仓库，功能完全正常。
        // 假警报比没有警报更糟：它会让人以为坏了，然后去修一个并不存在的问题。
        var installer = new LayoutInstaller(paths, host.Log);
        var installMarker = installer.ReadMarker();

        static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        var runningFromInstallRoot = SamePath(AppLayout.HostDirectory, paths.BinDir);
        var layoutInstalled = installMarker is not null;

        var toolSources = AppLayout.ResolveToolDirectories(paths)
            .Where(d => AppLayout.HasToolManifests(d.Path))
            .ToList();
        // 注意：sdkDir 是 *eztools 包* 目录（…/sdk/python/eztools），
        // 判定来源要跟安装根下的同名包目录比，不能跟 sdk 根比（比错对象会一直判成"仓库"）。
        var sdkPackageInInstallRoot = Path.Combine(paths.SdkDir, "python", "eztools");
        var sdkFromInstallRoot = sdkDir is not null && SamePath(sdkDir, sdkPackageInInstallRoot);

        var payloadFromInstallRoot = payloads.Any(p =>
            p.ArchivePath.StartsWith(paths.PayloadDir, StringComparison.OrdinalIgnoreCase));

        string ToolSourceText() => toolSources.Count == 0
            ? "无"
            : string.Join(" + ", toolSources.Select(s => SamePath(s.Path, paths.BuiltinToolsDir) ? "安装根" : "仓库"));

        var rows = new List<(string Name, bool Available, string Source, string Hint)>
        {
            // 宿主程序永远是"可用"的——我们此刻就在运行它。
            // 这一项要回答的是"从哪来"（安装根 / 仓库），而不是"有没有"。
            // 早先把它当"安装根里有没有"来判，开发机上就会报一条假警报。
            ("宿主程序",
                true,
                runningFromInstallRoot ? "安装根" : "仓库（当前运行位置）",
                "ezt install"),

            ("内置工具",
                host.LastDiscovery.Manifests.Count > 0,
                host.LastDiscovery.Manifests.Count == 0
                    ? "无"
                    : $"{host.LastDiscovery.Manifests.Count} 个 · 来源 {ToolSourceText()}",
                "ezt install"),

            ("Python SDK",
                sdkDir is not null,
                sdkDir is null ? "无" : (sdkFromInstallRoot ? "安装根" : "仓库"),
                "ezt install"),

            ("运行时载荷",
                payloads.Count > 0,
                payloads.Count == 0 ? "无" : (payloadFromInstallRoot ? "安装根" : "仓库"),
                "scripts/make-payload.sh"),

            ("已部署运行时",
                installed.Count > 0,
                installed.Count == 0
                    ? "无"
                    : string.Join("、", installed.Select(i => $"{i.Runtime} {i.Version}")),
                "ezt runtime install"),

            ("配置目录",
                Directory.Exists(paths.ConfigDir),
                paths.ConfigDir,
                "ezt doctor"),
        };

        var unavailable = rows.Where(r => !r.Available).ToList();

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("安装与来源");
            ConsoleUi.Field("安装根", paths.Root);
            ConsoleUi.Field("布局形态", layoutInstalled
                ? "已安装（安装根已铺设）"
                : "开发（安装根未铺设，工具与 SDK 来自仓库）");

            foreach (var (name, available, source, hint) in rows)
            {
                if (available)
                {
                    ConsoleUi.Ok($"{name}　{source}");
                }
                else
                {
                    ConsoleUi.Warn($"{name} —— 不可用，可执行: {hint}");
                }
            }

            if (installMarker is not null)
            {
                ConsoleUi.Field("安装来源", installMarker["sourceRoot"]?.GetValue<string>() ?? "(未记录)");
                ConsoleUi.Field("安装时间", installMarker["installedAt"]?.GetValue<string>() ?? "(未记录)");
            }
            else
            {
                ConsoleUi.Info("注意：安装根未铺设（没有 install.json）。");
                ConsoleUi.Info("从仓库运行时这不影响使用；把 ezt.exe 单独部署到别处之前要先跑 `ezt install`。");
            }
        }

        // ── 清单与注册表 ──
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("工具清单");
            foreach (var source in host.LastDiscovery.Sources)
            {
                ConsoleUi.Field(source.Name, $"{(source.Exists ? "✓" : "✗")} {source.Path}");
            }
        }

        var registry = host.Registry;
        var toolJson = new JsonArray();
        foreach (var tool in registry.Tools)
        {
            toolJson.Add(new JsonObject
            {
                ["id"] = tool.Id,
                ["name"] = tool.Name,
                ["version"] = tool.Manifest.Version,
                ["runtime"] = tool.Manifest.Runtime,
                ["weight"] = tool.Manifest.Weight.ToWire(),
                ["lifecycle"] = tool.Manifest.Lifecycle.ToWire(),
                ["enabled"] = tool.Enabled,
                ["loadState"] = tool.LoadState.ToString(),
                ["source"] = tool.Manifest.SourceName,
                ["directory"] = tool.Manifest.ToolDirectory,
                ["commands"] = new JsonArray(tool.Manifest.Contributes.Commands
                    .Select(c => J(c.Id)).ToArray()),
                ["hasConfigSchema"] = tool.Manifest.HasConfigSchema,
            });
        }

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Field("扫描文件数", host.LastDiscovery.ScannedFileCount.ToString());
            ConsoleUi.Field("注册工具数", $"{registry.Tools.Count}（已启用 {registry.EnabledCount}）");
            ConsoleUi.Field("可调用命令", registry.Commands.Count().ToString());

            if (registry.Tools.Count > 0)
            {
                ConsoleUi.Table(
                    new[] { "ID", "名称", "版本", "档位", "生命周期", "启用", "命令" },
                    registry.Tools.Select(t => new[]
                    {
                        t.Id,
                        t.Name,
                        t.Manifest.Version,
                        t.Manifest.Weight.ToWire(),
                        t.Manifest.Lifecycle.ToWire(),
                        t.Enabled ? "✓" : "✗",
                        t.Manifest.Contributes.Commands.Count.ToString(),
                    }).ToList());
            }
        }

        // ── 诊断 ──
        var diagnosticJson = new JsonArray();
        foreach (var diagnostic in registry.Diagnostics)
        {
            diagnosticJson.Add(new JsonObject
            {
                ["severity"] = diagnostic.Severity.ToString(),
                ["code"] = diagnostic.Code,
                ["message"] = diagnostic.Message,
                ["path"] = diagnostic.Path,
                ["toolId"] = diagnostic.ToolId,
            });
        }

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("诊断");
            if (registry.Diagnostics.Count == 0)
            {
                ConsoleUi.Ok("无诊断问题");
            }
            else
            {
                foreach (var diagnostic in registry.Diagnostics)
                {
                    switch (diagnostic.Severity)
                    {
                        case DiagnosticSeverity.Error:
                            ConsoleUi.Error(diagnostic.ToString());
                            break;
                        case DiagnosticSeverity.Warning:
                            ConsoleUi.Warn(diagnostic.ToString());
                            break;
                        default:
                            ConsoleUi.Info(diagnostic.ToString());
                            break;
                    }
                }
            }
        }

        // ── 可选：部署运行时 ──
        if (cli.GetBool("provision"))
        {
            if (!cli.GetBool("json"))
            {
                ConsoleUi.Section("部署运行时");
            }

            var result = await host.EnsureRuntimeAsync("python", null, message =>
            {
                if (!cli.GetBool("json"))
                {
                    ConsoleUi.Info(message);
                }
            });

            if (result.Installation is null)
            {
                if (!cli.GetBool("json"))
                {
                    ConsoleUi.Error(result.Error ?? "运行时部署失败");
                }

                json["provision"] = new JsonObject { ["ok"] = false, ["error"] = result.Error };
            }
            else
            {
                if (!cli.GetBool("json"))
                {
                    ConsoleUi.Ok($"运行时就绪: {result.Installation}（{(result.Provisioned ? "本次部署" : "复用已有")}）");
                }

                json["provision"] = new JsonObject
                {
                    ["ok"] = true,
                    ["provisioned"] = result.Provisioned,
                    ["root"] = result.Installation.Root,
                    ["version"] = result.Installation.Version,
                };
            }
        }

        // ── 结论 ──
        // 两个正交的判定，不要混成一个：
        //   ready   —— 现在能不能跑工具（有通过校验的清单 + 有 SDK）
        //   healthy —— 有没有 Error 级诊断（有则退出码为 1）
        // 二者可以同时是「ready=true + healthy=false」：部分清单坏了、其余工具照常可用。
        // 这恰恰是设计目标（一个坏清单绝不拖垮其它工具），所以必须在输出里讲清楚，
        // 否则会被读成自相矛盾。
        var ready = registry.Tools.Count > 0 && sdkDir is not null;
        var errorCount = registry.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warningCount = registry.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("结论");
            if (!ready)
            {
                ConsoleUi.Warn("尚不能跑工具：需要至少一个通过校验的工具清单，以及可用的 SDK 源码");
            }
            else if (installed.Count == 0 && payloads.Count == 0)
            {
                ConsoleUi.Warn("可以发现工具，但还没有可用运行时：跑 `ezt runtime install`");
            }
            else
            {
                ConsoleUi.Ok($"宿主就绪，可以调用工具（{registry.Tools.Count} 个可用）");
            }

            if (errorCount > 0)
            {
                // 文案要说清两件相反的事：坏的被拒了、好的照常。
                // （原文写「去注册的错误明细」，读起来像在叫人"去注册"，实测会让人愣一下。）
                ConsoleUi.Warn(
                    $"有 {errorCount} 条 Error 级诊断：相关清单已被拒绝注册，其余工具不受影响。"
                    + "被拒绝的清单明细见上方「诊断」段。");
            }
            else if (warningCount > 0)
            {
                ConsoleUi.Info($"有 {warningCount} 条 Warning 级诊断（不阻塞使用）");
            }
        }

        json["host"] = new JsonObject
        {
            ["version"] = HostVersion(),
            ["installRoot"] = paths.Root,
            ["configRoot"] = paths.ConfigRoot,
            ["rid"] = NativeRid.Current,
            ["firstRun"] = paths.IsFirstRun,
        };
        json["layout"] = new JsonObject
        {
            ["installed"] = layoutInstalled,   // 安装根是否被 `ezt install` 铺过
            ["runningFromInstallRoot"] = runningFromInstallRoot,
            ["root"] = paths.Root,
            ["allAvailable"] = unavailable.Count == 0,
            ["unavailable"] = new JsonArray(unavailable.Select(u => (JsonNode)J(u.Name)).ToArray()),
            ["items"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["available"] = r.Available,
                ["source"] = r.Source,
                ["hint"] = r.Hint,
            }).ToArray()),
            ["marker"] = installMarker,
        };
        json["installedRuntimes"] = runtimeJson;
        json["payloads"] = payloadJson;
        json["sdkDir"] = sdkDir;
        json["tools"] = toolJson;
        json["diagnostics"] = diagnosticJson;
        json["toolCount"] = registry.Tools.Count;
        json["enabledCount"] = registry.EnabledCount;
        json["commandCount"] = registry.Commands.Count();
        json["ready"] = ready;              // 现在能不能跑工具
        json["healthy"] = errorCount == 0;  // 清单是否全部通过校验（false 时退出码为 1）
        json["errorCount"] = errorCount;
        json["warningCount"] = warningCount;

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(json, cli.GetBool("compact"));
        }

        return errorCount == 0 ? 0 : 1;
    }

    // ──────────────────────────────────────────────────────────── list

    private static async Task<int> ListAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        await using var host = await CreateHostAsync(cli, echoLog: false);
        var registry = host.Registry;

        if (cli.GetBool("commands"))
        {
            if (cli.GetBool("json"))
            {
                var arr = new JsonArray();
                foreach (var binding in registry.Commands.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
                {
                    arr.Add(new JsonObject
                    {
                        ["commandId"] = binding.Command.Id,
                        ["title"] = binding.Command.Title,
                        ["toolId"] = binding.Tool.Id,
                        ["handler"] = binding.Command.Handler,
                        ["enabled"] = binding.Tool.Enabled,
                        ["timeoutMs"] = (int)binding.Tool.Manifest.Weight.DefaultTimeout().TotalMilliseconds,
                    });
                }

                ConsoleUi.PrintJson(arr, cli.GetBool("compact"));
                return 0;
            }

            ConsoleUi.Header($"可调用命令（{registry.Commands.Count()} 个）");
            ConsoleUi.Table(
                new[] { "命令 ID", "标题", "工具", "handler", "启用" },
                registry.Commands
                    .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new[]
                    {
                        c.Command.Id,
                        c.Command.Title,
                        c.Tool.Id,
                        c.Command.Handler,
                        c.Tool.Enabled ? "✓" : "✗",
                    }).ToList());
            return 0;
        }

        if (cli.GetBool("json"))
        {
            var arr = new JsonArray();
            foreach (var tool in registry.Tools)
            {
                arr.Add(new JsonObject
                {
                    ["id"] = tool.Id,
                    ["name"] = tool.Name,
                    ["version"] = tool.Manifest.Version,
                    ["description"] = tool.Manifest.Description,
                    ["runtime"] = tool.Manifest.Runtime,
                    ["weight"] = tool.Manifest.Weight.ToWire(),
                    ["lifecycle"] = tool.Manifest.Lifecycle.ToWire(),
                    ["enabled"] = tool.Enabled,
                    ["loadState"] = tool.LoadState.ToString(),
                    ["needs"] = new JsonArray(tool.Manifest.Needs
                        .Select(n => J(n)).ToArray()),
                    ["exclusiveResources"] = new JsonArray(tool.Manifest.ExclusiveResources
                        .Select(n => J(n)).ToArray()),
                    ["directory"] = tool.Manifest.ToolDirectory,
                    ["source"] = tool.Manifest.SourceName,
                    // ⚠️ 必须 Clone：ConfigSchema 已经挂在清单解析出的节点上，
                    //    直接挂到新对象会抛 "The node already has a parent."
                    ["configSchema"] = tool.Manifest.ConfigSchema?.Clone(),
                    // C2（2026-09-30）：configHidden 进双展示面 —— 恒输出（false 也输出），
                    // 读 JSON 的人才能区分"没隐藏"与"字段被吞"（acceptance 3 断言消费）。
                    ["configHidden"] = tool.Manifest.ConfigHidden,
                    ["commands"] = new JsonArray(tool.Manifest.Contributes.Commands.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["title"] = c.Title,
                        ["handler"] = c.Handler,
                    }).ToArray()),
                    ["actions"] = new JsonArray(tool.Manifest.Contributes.Actions.Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = a.Id,
                        ["title"] = a.Title,
                        ["when"] = a.When,
                        // 🔴 2026-09-29 补：原先这里**漏了 handler**，而上面的 commands 分支有
                        //    （`ezt info` 的表格也打印 handler）—— 同一份声明的两个展示面不一致，
                        //    读 `list --json` 的人无法判断 handler 到底解析出来没有。
                        //    是新增的 acceptance 3b 断言（actions 四字段齐全）把它抓出来的。
                        ["handler"] = a.Handler,
                    }).ToArray()),
                    ["hotkeys"] = new JsonArray(tool.Manifest.Contributes.Hotkeys.Select(h => (JsonNode)new JsonObject
                    {
                        ["command"] = h.Command,
                        ["default"] = h.Default,
                    }).ToArray()),
                    ["menus"] = new JsonArray(tool.Manifest.Contributes.Menus.Select(m => (JsonNode)new JsonObject
                    {
                        ["location"] = m.Location,
                        ["command"] = m.Command,
                        ["group"] = m.Group,
                        ["input"] = m.Input.ToWire(),
                    }).ToArray()),
                });
            }

            ConsoleUi.PrintJson(arr, cli.GetBool("compact"));
            return 0;
        }

        ConsoleUi.Header($"已发现的工具（{registry.Tools.Count} 个）");

        if (registry.Tools.Count == 0)
        {
            ConsoleUi.Warn("没有发现任何工具。检查 --tools-dir，或跑 `ezt doctor` 看诊断。");
            return 0;
        }

        ConsoleUi.Table(
            new[] { "ID", "名称", "版本", "档位", "生命周期", "启用", "命令", "来源" },
            registry.Tools.Select(t => new[]
            {
                t.Id,
                t.Name,
                t.Manifest.Version,
                t.Manifest.Weight.ToWire(),
                t.Manifest.Lifecycle.ToWire(),
                t.Enabled ? "✓" : "✗",
                t.Manifest.Contributes.Commands.Count.ToString(),
                t.Manifest.SourceName,
            }).ToList());

        Console.WriteLine();
        ConsoleUi.Info("「启用」是宿主是否允许启动；进程是否活着是「已加载」，见 `ezt info`。");
        ConsoleUi.Info("加 --commands 看全部命令，加 --json 拿机器可读输出。");

        return 0;
    }

    // ──────────────────────────────────────────────────────────── info

    private static async Task<int> InfoAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        var toolId = cli.Rest.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(toolId))
        {
            ConsoleUi.Error("用法: ezt info <toolId>");
            return 64;
        }

        await using var host = await CreateHostAsync(cli, echoLog: false);
        if (!host.Registry.TryGetTool(toolId, out var tool))
        {
            ConsoleUi.Error($"未找到工具 '{toolId}'。用 `ezt list` 看可用工具。");
            return 4;
        }

        var manifest = tool.Manifest;

        if (cli.GetBool("json"))
        {
            var obj = new JsonObject
            {
                ["id"] = manifest.Id,
                ["name"] = manifest.Name,
                ["version"] = manifest.Version,
                ["description"] = manifest.Description,
                ["author"] = manifest.Author,
                ["runtime"] = manifest.Runtime,
                ["runtimeVersion"] = manifest.RuntimeVersion,
                ["entry"] = manifest.Entry,
                ["entryPath"] = manifest.EntryPath,
                ["weight"] = manifest.Weight.ToWire(),
                ["lifecycle"] = manifest.Lifecycle.ToWire(),
                ["latency"] = manifest.Latency.ToWire(),
                ["enabled"] = tool.Enabled,
                ["loadState"] = tool.LoadState.ToString(),
                ["disabledReason"] = tool.DisabledReason,
                ["toolDirectory"] = manifest.ToolDirectory,
                ["manifestPath"] = manifest.ManifestPath,
                ["source"] = manifest.SourceName,
                ["needs"] = new JsonArray(manifest.Needs.Select(n => J(n)).ToArray()),
                ["exclusiveResources"] = new JsonArray(manifest.ExclusiveResources
                    .Select(n => J(n)).ToArray()),
                ["contributes"] = new JsonObject
                {
                    ["commands"] = new JsonArray(manifest.Contributes.Commands.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id, ["title"] = c.Title, ["handler"] = c.Handler,
                    }).ToArray()),
                    ["actions"] = new JsonArray(manifest.Contributes.Actions.Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = a.Id, ["title"] = a.Title, ["when"] = a.When, ["handler"] = a.Handler,
                    }).ToArray()),
                    ["hotkeys"] = new JsonArray(manifest.Contributes.Hotkeys.Select(h => (JsonNode)new JsonObject
                    {
                        ["command"] = h.Command, ["default"] = h.Default,
                    }).ToArray()),
                    ["menus"] = new JsonArray(manifest.Contributes.Menus.Select(m => (JsonNode)new JsonObject
                    {
                        ["location"] = m.Location, ["command"] = m.Command, ["group"] = m.Group,
                        ["input"] = m.Input.ToWire(),
                    }).ToArray()),
                },
                ["configSchema"] = manifest.ConfigSchema?.Clone(),
                ["defaultTimeoutMs"] = (int)manifest.Weight.DefaultTimeout().TotalMilliseconds,
            };

            ConsoleUi.PrintJson(obj, cli.GetBool("compact"));
            return 0;
        }

        ConsoleUi.Header($"{manifest.Name}  ({manifest.Id})");
        ConsoleUi.Field("版本", manifest.Version);
        ConsoleUi.Field("说明", manifest.Description);
        ConsoleUi.Field("作者", manifest.Author);
        ConsoleUi.Field("运行时", $"{manifest.Runtime} {(manifest.RuntimeVersion is null ? "" : manifest.RuntimeVersion)}");
        ConsoleUi.Field("入口", manifest.EntryPath);
        ConsoleUi.Field("重量档位", $"{manifest.Weight.ToWire()}（默认超时 {manifest.Weight.DefaultTimeout().TotalSeconds:0}s）");
        ConsoleUi.Field("生命周期", manifest.Lifecycle.ToWire());
        ConsoleUi.Field("状态", $"{(tool.Enabled ? "已启用" : "已禁用")} / 进程{(tool.LoadState == ToolLoadState.Running ? "运行中" : "未加载")}");
        if (!string.IsNullOrWhiteSpace(tool.DisabledReason))
        {
            ConsoleUi.Field("禁用原因", tool.DisabledReason);
        }

        ConsoleUi.Field("能力声明", manifest.Needs.Count == 0 ? "（无）" : string.Join("、", manifest.Needs));
        if (manifest.ExclusiveResources.Count > 0)
        {
            ConsoleUi.Field("独占资源", string.Join("、", manifest.ExclusiveResources));
        }

        ConsoleUi.Field("来源", $"{manifest.SourceName} → {manifest.ToolDirectory}");
        ConsoleUi.Field("清单", manifest.ManifestPath);

        ConsoleUi.Section("贡献点");
        ConsoleUi.Table(
            new[] { "类型", "ID", "标题", "handler / 条件" },
            manifest.Contributes.Commands.Select(c => new[] { "命令", c.Id, c.Title, c.Handler })
                .Concat(manifest.Contributes.Actions.Select(a => new[] { "动作", a.Id, a.Title, $"{a.Handler}  {a.When}" }))
                .Concat(manifest.Contributes.Hotkeys.Select(h => new[] { "热键", h.Command, h.Default, "（宿主统一注册）" }))
                // 菜单行的第 4 列显示 "分组 / 输入来源" —— 输入来源是"这个菜单项点了能不能跑"的关键信息
                .Concat(manifest.Contributes.Menus.Select(m =>
                    new[] { "菜单", m.Location, m.Command, $"{m.Group ?? "-"} / {m.Input.ToWire()}" }))
                .ToList());

        ConsoleUi.Section("配置 schema（P1 将据此自动渲染设置页）");
        if (!manifest.HasConfigSchema)
        {
            ConsoleUi.Info("未声明配置项");
        }
        else
        {
            var properties = manifest.ConfigSchema!["properties"]!.AsObject();
            var rows = new List<string[]>();
            foreach (var (key, value) in properties)
            {
                var prop = value?.AsObject();
                var type = prop?["type"]?.GetValue<string>() ?? "?";
                var title = prop?["title"]?.GetValue<string>() ?? "";
                var defaultText = prop?["default"]?.ToJsonString() ?? "";
                var extra = prop?["enum"] is JsonArray enums
                    ? string.Join(" | ", enums.Select(e => e?.ToString()))
                    : prop?["format"]?.GetValue<string>() ?? "";
                rows.Add(new[] { key, type, title, defaultText, extra });
            }

            ConsoleUi.Table(new[] { "键", "类型", "标题", "默认值", "约束" }, rows);
            ConsoleUi.Info("全部读取自 tool.json，宿主没有一行针对本工具 UI 的代码。");
        }

        return 0;
    }

    // ──────────────────────────────────────────────────────────── install

    /// <summary>
    /// 把"源码形态"铺成"已安装形态"：tools/ + sdk/ + payload/ + bin/ → 安装根。
    /// 这一步不做的话，把 ezt.exe 单独放到别处时一个工具都发现不到（实测）。
    /// </summary>
    private static async Task<int> InstallAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        await using var host = await CreateHostAsync(cli, echoLog: cli.GetBool("verbose"));
        var paths = host.Paths;

        var sourceRoot = cli.Get("from") ?? LayoutInstaller.FindSourceRoot();
        if (sourceRoot is null)
        {
            ConsoleUi.Error(
                "定位不到源码根（含 tools/ 与 sdk/ 的目录）。用 --from <路径> 指定，"
                + $"或设环境变量 {LayoutInstaller.EnvSourceRoot}。");
            return 4;
        }

        var dryRun = cli.GetBool("dry-run");
        var includePayload = !cli.GetBool("no-payload");

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Header(dryRun ? "安装（试运行）" : "安装");
        }

        var result = new LayoutInstaller(paths, host.Log)
            .Install(sourceRoot, includePayload, dryRun);

        foreach (var step in result.Steps)
        {
            ConsoleUi.Info(step);
        }

        foreach (var skip in result.Skipped)
        {
            ConsoleUi.Info($"跳过 {skip}");
        }

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["sourceRoot"] = result.SourceRoot,
                ["targetRoot"] = result.TargetRoot,
                ["dryRun"] = result.DryRun,
                ["copiedFiles"] = result.CopiedFiles,
                ["copiedTools"] = result.CopiedTools,
                ["payloadCopied"] = result.PayloadCopied,
                ["steps"] = new JsonArray(result.Steps.Select(s => (JsonNode)J(s)).ToArray()),
                ["skipped"] = new JsonArray(result.Skipped.Select(s => (JsonNode)J(s)).ToArray()),
            }, cli.GetBool("compact"));
            return 0;
        }

        ConsoleUi.Ok(
            $"{result.CopiedTools} 个工具，{result.CopiedFiles} 个文件"
            + (dryRun ? "（试运行，未落盘）" : $" → {result.TargetRoot}"));

        ConsoleUi.Section("下一步");
        ConsoleUi.Info($"1. 部署运行时：ezt runtime install");
        ConsoleUi.Info($"2. 自检：      ezt selftest");
        ConsoleUi.Info("从安装根的 bin/ 运行 ezt，即可验证「已安装形态」（不再依赖仓库目录）。");

        return 0;
    }

    // ──────────────────────────────────────────────────────────── update / uninstall

    /// <summary>
    /// 用便携包覆盖更新（P4 Wave 2b，决策 A3）。
    /// 替换 bin/ tools/ sdk/ payload/，**保留** toolsdata/ runtimes/ logs/ cache/。
    /// </summary>
    private static async Task<int> UpdateAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        await using var host = await CreateHostAsync(cli, echoLog: cli.GetBool("verbose"));
        var paths = host.Paths;

        // 包根：既接受"解压后的包目录"，也接受"zip 文件"（自动解压到临时目录）
        var from = cli.Get("from");
        if (string.IsNullOrWhiteSpace(from))
        {
            ConsoleUi.Error("必须用 --from <包目录|zip 文件> 指定新版本。");
            return 64;
        }

        var normalized = PathInput.NormalizeToFullPath(from);
        string packageRoot;
        string? tempExtract = null;

        if (File.Exists(normalized) && normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            tempExtract = Path.Combine(Path.GetTempPath(), "eztools-update-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(normalized, tempExtract);
            }
            catch (Exception ex)
            {
                ConsoleUi.Error($"解压失败：{ex.Message}");
                return 3;
            }

            // zip 里可能有一层顶层目录（打包时的常见形态）——向下钻一层找"含 bin/ 的目录"
            packageRoot = FindPackageRoot(tempExtract);
        }
        else if (Directory.Exists(normalized))
        {
            packageRoot = FindPackageRoot(normalized);
        }
        else
        {
            ConsoleUi.Error($"包不存在：{normalized}");
            return 4;
        }

        var dryRun = cli.GetBool("dry-run");
        var backup = !cli.GetBool("no-backup");

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Header(dryRun ? "更新（试运行）" : "更新");
            ConsoleUi.Field("安装根", paths.Root);
            ConsoleUi.Field("新版本包", packageRoot);
        }

        try
        {
            var result = new LayoutUpdater(paths, host.Log).Update(packageRoot, backup, dryRun);

            foreach (var step in result.Steps)
            {
                ConsoleUi.Info(step);
            }

            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject
                {
                    ["packageRoot"] = result.PackageRoot,
                    ["targetRoot"] = result.TargetRoot,
                    ["dryRun"] = result.DryRun,
                    ["replacedFiles"] = result.ReplacedFiles,
                    ["addedFiles"] = result.AddedFiles,
                    ["backedUpFiles"] = result.BackedUpFiles,
                    ["backupDir"] = result.BackupDir,
                    ["preserved"] = new JsonArray(result.Preserved.Select(p => (JsonNode)J(p)).ToArray()),
                    ["steps"] = new JsonArray(result.Steps.Select(s => (JsonNode)J(s)).ToArray()),
                }, cli.GetBool("compact"));
                return 0;
            }

            ConsoleUi.Ok(
                $"替换 {result.ReplacedFiles} / 新增 {result.AddedFiles} 个文件"
                + (dryRun ? "（试运行，未落盘）" : ""));

            if (result.BackupDir is not null)
            {
                ConsoleUi.Info($"回滚备份：{result.BackupDir}（确认无误后可手动删除）");
            }

            if (result.Preserved.Count > 0)
            {
                ConsoleUi.Info($"已保留用户数据与运行时：{string.Join(" / ", result.Preserved)}");
            }

            ConsoleUi.Section("下一步");
            ConsoleUi.Info("重启托盘（ezt tray / 从开始菜单）让新版本生效。");

            return 0;
        }
        finally
        {
            if (tempExtract is not null && Directory.Exists(tempExtract))
            {
                try
                {
                    Directory.Delete(tempExtract, recursive: true);
                }
                catch
                {
                    // review-guards:allow-empty-catch :: 临时目录删不掉不该让更新失败（系统清理会兜底）
                }
            }
        }
    }

    /// <summary>
    /// 定位包根：若目录本身含 <c>bin/</c> 或 <c>tools/</c> 则就是它；
    /// 否则向下找唯一的子目录（打包时常见"zip 里套一层"）。
    /// </summary>
    private static string FindPackageRoot(string dir)
    {
        if (Directory.Exists(Path.Combine(dir, "bin")) || Directory.Exists(Path.Combine(dir, "tools")))
        {
            return dir;
        }

        var subdirs = Directory.GetDirectories(dir);
        foreach (var sub in subdirs)
        {
            if (Directory.Exists(Path.Combine(sub, "bin")) || Directory.Exists(Path.Combine(sub, "tools")))
            {
                return sub;
            }
        }

        // 再钻一层（Eztools-x.y.z/bin 这种）
        foreach (var sub in subdirs)
        {
            foreach (var deeper in Directory.GetDirectories(sub))
            {
                if (Directory.Exists(Path.Combine(deeper, "bin")) || Directory.Exists(Path.Combine(deeper, "tools")))
                {
                    return deeper;
                }
            }
        }

        return dir;   // 找不到就原样返回，让 Update 的校验报错
    }

    /// <summary>
    /// 卸载（P4 Wave 2b）。**默认保留用户数据**——要连数据一起清必须显式 <c>--purge-data</c>。
    /// </summary>
    private static async Task<int> UninstallAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        await using var host = await CreateHostAsync(cli, echoLog: cli.GetBool("verbose"));
        var paths = host.Paths;

        var dryRun = cli.GetBool("dry-run");
        var purge = cli.GetBool("purge-data");

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Header(dryRun ? "卸载（试运行）" : "卸载");
            ConsoleUi.Field("安装根", paths.Root);
            ConsoleUi.Field("配置根", paths.ConfigRoot);
        }

        // 破坏性操作：非试运行时必须显式确认（--yes），避免脚本里手一滑
        if (!dryRun && !cli.GetBool("yes"))
        {
            ConsoleUi.Warn("这会删除安装根下的程序与运行时（用户数据默认保留）。");
            ConsoleUi.Info($"确认执行请加 --yes；只想看会删什么请加 --dry-run。");
            return 64;
        }

        if (purge && !cli.GetBool("json"))
        {
            ConsoleUi.Warn("--purge-data：**连用户数据与配置一起删**（不可恢复）。");
        }

        var result = new LayoutUpdater(paths, host.Log).Uninstall(purge, dryRun);

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["targetRoot"] = result.TargetRoot,
                ["dryRun"] = result.DryRun,
                ["removedFiles"] = result.RemovedFiles,
                ["purgedUserData"] = result.PurgedUserData,
                ["kept"] = new JsonArray(result.Kept.Select(k => (JsonNode)J(k)).ToArray()),
                ["failed"] = new JsonArray(result.Failed.Select(f => (JsonNode)J(f)).ToArray()),
            }, cli.GetBool("compact"));
            return result.Failed.Count == 0 ? 0 : 1;
        }

        ConsoleUi.Ok(
            $"已删除 {result.RemovedFiles} 个文件" + (dryRun ? "（试运行，未落盘）" : ""));

        if (result.Kept.Count > 0)
        {
            ConsoleUi.Section("已保留");
            foreach (var k in result.Kept)
            {
                ConsoleUi.Info(k);
            }

            if (!purge)
            {
                ConsoleUi.Info("要连用户数据一起删，重新运行并加 --purge-data。");
            }
        }

        if (result.Failed.Count > 0)
        {
            ConsoleUi.Section("未能删除（文件可能被占用）");
            foreach (var f in result.Failed)
            {
                ConsoleUi.Warn(f);
            }

            ConsoleUi.Info("关掉托盘与工具进程后重试。");
            return 1;
        }

        return 0;
    }

    // ──────────────────────────────────────────────────────────── runtime

    private static async Task<int> RuntimeAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        var sub = cli.Rest.FirstOrDefault() ?? "list";

        await using var host = await CreateHostAsync(cli, echoLog: false);
        var provisioner = host.Runtimes.Provisioner;

        switch (sub)
        {
            case "list":
            case "status":
            {
                var installed = provisioner.GetInstalled();
                if (cli.GetBool("json"))
                {
                    var arr = new JsonArray();
                    foreach (var item in installed)
                    {
                        arr.Add(new JsonObject
                        {
                            ["runtime"] = item.Runtime,
                            ["version"] = item.Version,
                            ["root"] = item.Root,
                            ["executable"] = item.ExecutablePath,
                            ["usable"] = item.IsUsable,
                            ["embedded"] = item.IsEmbedded,
                            ["origin"] = item.Origin,
                        });
                    }

                    ConsoleUi.PrintJson(arr, cli.GetBool("compact"));
                    return 0;
                }

                ConsoleUi.Header($"已部署的运行时（{installed.Count} 个）");
                if (installed.Count == 0)
                {
                    ConsoleUi.Warn("尚无运行时。跑 `ezt runtime install` 从载荷部署。");
                    return 0;
                }

                ConsoleUi.Table(
                    new[] { "运行时", "版本", "隔离", "可用", "目录" },
                    installed.Select(i => new[]
                    {
                        i.Runtime, i.Version, i.IsEmbedded ? "是" : "否", i.IsUsable ? "✓" : "✗", i.Root,
                    }).ToList());
                return 0;
            }

            case "payloads":
            {
                var payloads = provisioner.DiscoverPayloads();
                if (cli.GetBool("json"))
                {
                    var arr = new JsonArray();
                    foreach (var payload in payloads)
                    {
                        arr.Add(new JsonObject
                        {
                            ["file"] = Path.GetFileName(payload.ArchivePath),
                            ["path"] = payload.ArchivePath,
                            ["runtime"] = payload.Runtime,
                            ["version"] = payload.Version,
                            ["rid"] = payload.Rid,
                            ["size"] = payload.SizeBytes,
                        });
                    }

                    ConsoleUi.PrintJson(arr, cli.GetBool("compact"));
                    return 0;
                }

                ConsoleUi.Header($"运行时载荷（{payloads.Count} 个）");
                ConsoleUi.Field("本机 RID", NativeRid.Current);
                ConsoleUi.Field("扫描目录", string.Join(" ; ", provisioner.PayloadSearchPaths));

                if (payloads.Count == 0)
                {
                    ConsoleUi.Warn("未发现载荷。用 scripts/make-payload.sh 生成，或把 tar.gz 放到上述目录。");
                    return 0;
                }

                ConsoleUi.Table(
                    new[] { "文件", "运行时", "版本", "RID", "大小" },
                    payloads.Select(p => new[]
                    {
                        Path.GetFileName(p.ArchivePath), p.Runtime, p.Version, p.Rid,
                        RuntimeProvisioner.FormatBytes(p.SizeBytes),
                    }).ToList());
                return 0;
            }

            case "install":
            {
                var runtimeName = cli.Rest.Skip(1).FirstOrDefault() ?? ToolRuntimes.Python;
                var versionRange = cli.Get("version");

                ConsoleUi.Header($"部署运行时 {runtimeName}");

                var result = await host.EnsureRuntimeAsync(runtimeName, versionRange, message =>
                {
                    ConsoleUi.Info(message);
                });

                if (result.Installation is null)
                {
                    ConsoleUi.Error(result.Error ?? "部署失败");
                    return 1;
                }

                ConsoleUi.Ok($"{result.Installation}（{(result.Provisioned ? "本次部署" : "复用已有安装")}）");
                if (result.Installation.IsEmbedded)
                {
                    ConsoleUi.Info("该运行时随宿主隔离，不写入 PATH，用户无需自行安装 Python。");
                }
                else
                {
                    ConsoleUi.Warn("当前用的是外部运行时覆盖（EZTOOLS_PYTHON），仅适合开发调试，不可交付。");
                }

                return 0;
            }

            default:
                ConsoleUi.Error($"未知子命令: runtime {sub}（可用 list / payloads / install）");
                return 64;
        }
    }

    // ──────────────────────────────────────────────────────────── invoke / call

    private static async Task<int> InvokeAsync(CliArgs cli)
    {
        var commandId = cli.Rest.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(commandId))
        {
            ConsoleUi.Error("用法: ezt invoke <commandId> [--json '{...}' | --text <文本>]");
            return 64;
        }

        ConsoleUi.JsonMode = cli.GetBool("json");
        var args = cli.BuildParams();

        await using var host = await CreateHostAsync(cli, echoLog: cli.GetBool("verbose"));

        if (!cli.GetBool("json") && !cli.GetBool("quiet"))
        {
            ConsoleUi.Header($"调用 {commandId}");
        }

        var timeoutMs = cli.Get("timeout") is { } timeoutText && int.TryParse(timeoutText, out var parsed)
            ? parsed
            : (int?)null;

        var result = await host.Processes.InvokeCommandAsync(
            commandId,
            args,
            timeoutMs is null ? null : TimeSpan.FromMilliseconds(timeoutMs.Value));

        if (!cli.GetBool("json") && !cli.GetBool("quiet"))
        {
            ConsoleUi.Ok($"完成，用时 {result.Elapsed.TotalMilliseconds:0}ms");
        }

        ConsoleUi.PrintJson(result.Result, cli.GetBool("compact"));
        return 0;
    }

    private static async Task<int> CallAsync(CliArgs cli)
    {
        var rest = cli.Rest;
        if (rest.Count < 2)
        {
            ConsoleUi.Error("用法: ezt call <toolId> <handler> [--json '{...}']");
            return 64;
        }

        ConsoleUi.JsonMode = cli.GetBool("json");
        var toolId = rest[0];
        var handler = rest[1];
        var args = cli.BuildParams();

        await using var host = await CreateHostAsync(cli, echoLog: cli.GetBool("verbose"));

        if (!cli.GetBool("json") && !cli.GetBool("quiet"))
        {
            ConsoleUi.Header($"{toolId}.{handler}");
        }

        var result = await host.Processes.InvokeHandlerAsync(toolId, handler, args);

        if (!cli.GetBool("json") && !cli.GetBool("quiet"))
        {
            ConsoleUi.Ok($"完成，用时 {result.Elapsed.TotalMilliseconds:0}ms");
        }

        ConsoleUi.PrintJson(result.Result, cli.GetBool("compact"));
        return 0;
    }

    // ──────────────────────────────────────────────────────────── enable / disable

    private static async Task<int> SetEnabledAsync(CliArgs cli, bool enabled)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        var toolId = cli.Rest.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(toolId))
        {
            ConsoleUi.Error($"用法: ezt {(enabled ? "enable" : "disable")} <toolId>");
            return 64;
        }

        await using var host = await CreateHostAsync(cli, echoLog: false);
        var ok = host.SetEnabled(toolId, enabled, enabled ? null : cli.Get("reason") ?? "用户禁用");

        if (!ok)
        {
            ConsoleUi.Error($"未找到工具 '{toolId}'");
            return 4;
        }

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["toolId"] = toolId,
                ["enabled"] = enabled,
            }, cli.GetBool("compact"));
            return 0;
        }

        ConsoleUi.Ok($"工具 {toolId} 已{(enabled ? "启用" : "禁用")}");
        ConsoleUi.Info("注意：禁用只阻止启动，不影响清单是否注册（区别于 PowerToys 的 enabled 语义）。");
        return 0;
    }
}
