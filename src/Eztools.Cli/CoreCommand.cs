// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using Eztools.Host;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host.Primitives;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt core</c> 子命令：Core 特权服务的生命周期管理（P3）。
///
/// <list type="bullet">
/// <item><c>ezt core status</c> —— 读 core.json + 管道探测，如实报告（没在跑不是错误）</item>
/// <item><c>ezt core start</c> —— 启动 ezt-core（<c>--elevate</c> 走 UAC）</item>
/// <item><c>ezt core stop</c> —— 经管道请求优雅退出；僵死则按 pid 结束</item>
/// <item><c>ezt core ping</c> —— 存活与往返</item>
/// <item><c>ezt core install-task / uninstall-task</c> —— 计划任务形态（开机自启 + 最高权限，免每次 UAC）</item>
/// </list>
/// </summary>
internal static class CoreCommand
{
    private const string TaskName = "EztoolsCore";

    public static async Task<int> RunAsync(CliArgs cli)
    {
        var sub = cli.Rest.Count > 0 ? cli.Rest[0] : "status";
        ConsoleUi.JsonMode = cli.GetBool("json");

        return sub switch
        {
            "status" => await StatusAsync(cli).ConfigureAwait(false),
            "start" => await StartAsync(cli).ConfigureAwait(false),
            "stop" => await StopAsync(cli).ConfigureAwait(false),
            "ping" => await PingAsync(cli).ConfigureAwait(false),
            "install-task" => InstallTask(cli),
            "uninstall-task" => UninstallTask(cli),
            _ => UnknownSub(sub),
        };
    }

    private static int UnknownSub(string sub)
    {
        ConsoleUi.Error($"未知子命令: core {sub}（可用：status | start | stop | ping | install-task | uninstall-task）");
        return 64;
    }

    // ── status ──

    private static async Task<int> StatusAsync(CliArgs cli)
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        var endpoint = CoreEndpoint.TryRead(paths.Root);

        var alive = false;
        JsonNode? ping = null;
        if (endpoint is not null)
        {
            (alive, ping) = await ProbeAsync(endpoint.PipeName).ConfigureAwait(false);
        }

        var report = new JsonObject
        {
            ["running"] = alive,
            ["registered"] = endpoint is not null,
            ["pid"] = endpoint?.Pid,
            ["elevated"] = endpoint?.Elevated,
            ["version"] = endpoint?.Version,
            ["startedAt"] = endpoint?.StartedAt.ToString("O"),
            ["pipeName"] = endpoint?.PipeName,
            ["pong"] = ping?["pong"]?.GetValue<bool>(),
        };

        if (cli.GetBool("json"))
        {
            Console.WriteLine(report.ToJsonString(new() { WriteIndented = true }));
        }
        else
        {
            if (endpoint is null)
            {
                ConsoleUi.Line("Core 未运行（无端点登记）。启动：ezt core start");
                return 0;
            }

            ConsoleUi.Line($"Core 端点：pid={endpoint.Pid}  提权={endpoint.Elevated}  版本={endpoint.Version}");
            ConsoleUi.Line($"        管道={endpoint.PipeName}");
            ConsoleUi.Line(alive
                ? "        状态：运行中 ✓"
                : "        状态：✗ 已登记但无响应（可能僵死）—— ezt core stop && ezt core start");
        }

        return 0;
    }

    // ── start ──

    private static async Task<int> StartAsync(CliArgs cli)
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        // Core 的 stdout/stderr 落到独立日志文件（host-ezt-core-*.log），崩了至少留现场
        var coreLog = new HostLog(paths, cli.GetBool("verbose"), echoToConsole: false, hostName: "ezt-core");

        // 幂等：已在跑就直说（不当作错误，脚本友好）
        if (CoreEndpoint.TryRead(paths.Root) is { } existing
            && (await ProbeAsync(existing.PipeName).ConfigureAwait(false)).Alive)
        {
            ConsoleUi.Line($"Core 已在运行（pid={existing.Pid}，提权={existing.Elevated}），无需重复启动。");
            return 0;
        }

        var exe = PrimitiveClient.FindCoreExe(paths)
                  ?? throw new ToolProtocolException(
                      "找不到 ezt-core.exe。开发形态先构建 Eztools.Core 项目；"
                      + "已安装形态应有 <安装根>\\bin\\ezt-core.exe（可用 EZTOOLS_CORE_EXE 显式指定）。");

        var elevate = cli.GetBool("elevate");
        // 提权只能走 ShellExecute（UAC 限制）；同令牌启动走普通管道重定向——
        // 子进程的 stderr 能进本进程日志，崩溃时至少留下现场（ShellExecute 会把它吞掉）。
        var info = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--root \"{paths.Root}\"" + (cli.GetBool("echo") ? " --echo" : ""),
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : null,
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (!elevate)
        {
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
        }

        using var process = Process.Start(info)
                             ?? throw new ToolProtocolException("ezt-core 启动失败（Process.Start 返回空）");

        // 消费重定向流（不读会在缓冲区满时阻塞子进程），同时作为诊断现场
        if (!elevate)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    coreLog.Info(e.Data, "core-stderr");
                }
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    coreLog.Info(e.Data, "core-stdout");
                }
            };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }

        // 等端点登记 + 管道可连（最多 ~8 秒；提权弹 UAC 由用户决定，用户取消 = 进程直接退出）
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(200).ConfigureAwait(false);
            if (process.HasExited)
            {
                // 给异步 stderr 事件一点时间落地，让崩溃现场进 host-ezt-core-*.log
                await Task.Delay(600).ConfigureAwait(false);
                ConsoleUi.Error($"ezt-core 提前退出（exit={process.ExitCode}）。"
                                + (process.ExitCode == 3 ? "（已有实例在运行）" : "")
                                + " 崩溃现场见安装根 logs\\host-ezt-core-*.log");
                return process.ExitCode == 3 ? 0 : 2;
            }

            if (CoreEndpoint.TryRead(paths.Root) is { } ep
                && (await ProbeAsync(ep.PipeName).ConfigureAwait(false)).Alive)
            {
                ConsoleUi.Line($"Core 已启动：pid={ep.Pid}  提权={ep.Elevated}  版本={ep.Version}");
                ConsoleUi.Line($"        管道={ep.PipeName}");
                return 0;
            }
        }

        ConsoleUi.Error("Core 已启动但 8 秒内未就绪（端点未登记或管道不可连）。用 ezt core status 排查。");
        return 2;
    }

    // ── stop ──

    private static async Task<int> StopAsync(CliArgs cli)
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        var endpoint = CoreEndpoint.TryRead(paths.Root);
        if (endpoint is null)
        {
            ConsoleUi.Line("Core 未运行。");
            return 0;
        }

        // 优雅优先：core.shutdown 走管道；拒绝连接/超时则按登记的 pid 结束（僵死兜底）
        var (alive, _) = await ProbeAsync(endpoint.PipeName, CoreProtocolMethods.Shutdown).ConfigureAwait(false);

        for (var i = 0; i < 20 && !alive; i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (!IsPidAlive(endpoint.Pid))
            {
                break;
            }
        }

        if (!alive && IsPidAlive(endpoint.Pid))
        {
            try
            {
                using var proc = Process.GetProcessById(endpoint.Pid);
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // review-guards:allow-empty-catch :: 进程刚好退出了
            }
        }

        ConsoleUi.Line($"Core 已停止（pid={endpoint.Pid}）。");
        return 0;
    }

    // ── ping ──

    private static async Task<int> PingAsync(CliArgs cli)
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        var endpoint = CoreEndpoint.TryRead(paths.Root)
                       ?? throw new ToolRpcException(
                           PrimitiveErrorCodes.CoreUnavailable, "Core 未运行。启动：ezt core start");

        var (alive, pong) = await ProbeAsync(endpoint.PipeName).ConfigureAwait(false);
        if (!alive)
        {
            ConsoleUi.Error("Core 无响应（端点已登记但 ping 失败）。");
            return 2;
        }

        var result = new JsonObject
        {
            ["pong"] = true,
            ["pid"] = pong?["pid"]?.GetValue<int>(),
            ["elevated"] = pong?["elevated"]?.GetValue<bool>(),
            ["version"] = pong?["version"]?.GetValue<string>(),
        };

        Console.WriteLine(cli.GetBool("json")
            ? result.ToJsonString()
            : $"pong ✓ pid={result["pid"]} 提权={result["elevated"]} 版本={result["version"]}");
        return 0;
    }

    // ── 计划任务（提权形态，P3 方案 §4）──

    private static int InstallTask(CliArgs cli)
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        var exe = PrimitiveClient.FindCoreExe(paths)
                  ?? throw new ToolProtocolException("找不到 ezt-core.exe，无法登记计划任务。");

        // /RL HIGHEST 需要管理员令牌 —— 非提权执行时 schtasks 会失败，这里先把话说清
        var args = $"/Create /F /TN {TaskName} /SC ONLOGON /RL HIGHEST "
                   + $"/TR \"\\\"{exe}\\\" --root \\\"{paths.Root}\\\" --from-task\"";

        var (code, output) = RunSchtasks(args);
        if (code != 0)
        {
            ConsoleUi.Error($"计划任务登记失败（schtasks 退出 {code}）：{output.Trim()}");
            ConsoleUi.Error("提示：/RL HIGHEST 需要管理员令牌 —— 以管理员身份重跑，"
                            + "或用 `ezt core start --elevate`（每次 UAC 确认的替代形态）。");
            return 2;
        }

        ConsoleUi.Line($"计划任务 {TaskName} 已登记：用户登录时以最高权限自动启动 ezt-core。");
        ConsoleUi.Line($"  任务命令：{exe} --root \"{paths.Root}\" --from-task");
        ConsoleUi.Line("  取消自启：ezt core uninstall-task");
        return 0;
    }

    private static int UninstallTask(CliArgs cli)
    {
        var (code, output) = RunSchtasks($"/Delete /F /TN {TaskName}");
        if (code != 0)
        {
            ConsoleUi.Error($"计划任务删除失败（schtasks 退出 {code}）：{output.Trim()}");
            return 2;
        }

        ConsoleUi.Line($"计划任务 {TaskName} 已删除。");
        return 0;
    }

    private static (int Code, string Output) RunSchtasks(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (proc is null)
        {
            return (-1, "无法启动 schtasks.exe");
        }

        var text = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, text);
    }

    // ── 探测帮手 ──

    private static async Task<(bool Alive, JsonNode? Pong)> ProbeAsync(
        string pipeName, string method = CoreProtocolMethods.Ping)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await client.ConnectAsync(1500).ConfigureAwait(false);

            var writer = new StreamWriter(client, new System.Text.UTF8Encoding(false), 1024, true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            var reader = new StreamReader(
                client, new System.Text.UTF8Encoding(false), false, 1024, leaveOpen: true);

            await writer.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "probe",
                ["method"] = method,
                ["params"] = new JsonObject(),
            }.ToJsonString()).ConfigureAwait(false);

            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return (false, null);
                }

                if (JsonNode.Parse(line) is not { } frame || frame["id"]?.GetValue<string>() != "probe")
                {
                    continue;
                }

                return (frame["error"] is null, frame["result"]);
            }
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
