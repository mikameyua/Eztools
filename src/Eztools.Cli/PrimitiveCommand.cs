using System.IO.Pipes;
using Eztools.Host;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host.Primitives;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt primitive &lt;name&gt;</c> —— 直调一个特权原语（调试与验收用）。
///
/// 走的是**与工具完全相同的链路**（PrimitiveClient → 管道 → Core），只是不经过工具进程，
/// 所以它能独立验证 Core 与宿主通路；工具侧全链路（工具→宿主→Core）由 pinfo 工具覆盖。
///
/// 用法：
///   ezt primitive process.enumerate
///   ezt primitive process.terminate --json '{"pid":123}'
///   ezt primitive volume.readMft --json '{"volume":"C:","maxRecords":10}'
///   ezt primitive handles.enumerate --json '{}' --timeout 300   # 全量扫描 1~2 分钟，需放宽超时
/// </summary>
internal static class PrimitiveCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");

        var name = cli.Rest.Count > 0 ? cli.Rest[0] : null;
        if (string.IsNullOrEmpty(name))
        {
            ConsoleUi.Error("用法: ezt primitive <原语名> [--json '{...参数JSON...}'] [--timeout <秒>]");
            ConsoleUi.Line($"已知原语：{string.Join(" · ", PrimitiveNames.Known)}");
            return 64;
        }

        JsonObject? args = null;
        var rawArgs = cli.Get("json", "args");
        if (!string.IsNullOrWhiteSpace(rawArgs))
        {
            args = JsonNode.Parse(rawArgs) as JsonObject
                   ?? throw new ToolProtocolException("--json 参数必须是 JSON 对象");
        }

        // --timeout <秒>：默认 30；下限 5 防 0/负数，上限 600 防"永远挂着"。
        // 全量 handles.enumerate 约 1~2 分钟（P3 方案 §20.4 已知限制），需要显式放宽。
        var timeoutSeconds = 30;
        if (int.TryParse(cli.Get("timeout"), out var requested))
        {
            timeoutSeconds = Math.Clamp(requested, 5, 600);
        }

        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        var client = new PrimitiveClient(paths, new HostLog(paths, verbose: false, echoToConsole: false, "ezt"));

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await client.CallAsync(
                name, args, toolId: null,
                timeout: TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (cli.GetBool("json"))
            {
                Console.WriteLine((result ?? new JsonObject()).ToJsonString(
                    new() { WriteIndented = !cli.GetBool("compact") }));
            }
            else
            {
                ConsoleUi.Ok($"{name} 成功（{elapsed.TotalMilliseconds:0} ms）");
                ConsoleUi.PrintJson(result);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            // 超时是"调用类失败"（退出 2），不是宿主内部异常（3）——如实映射
            if (cli.GetBool("json"))
            {
                Console.WriteLine(new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["code"] = RpcErrorCodes.ToolTimeout,
                        ["message"] = $"原语执行超时（上限 {timeoutSeconds} 秒）",
                    },
                }.ToJsonString());
            }
            else
            {
                ConsoleUi.Error($"[-32011] 原语执行超时（上限 {timeoutSeconds} 秒）——用 --timeout <秒> 放宽（全量句柄扫描建议 300）");
            }

            return 2;
        }
        catch (ToolRpcException ex)
        {
            if (cli.GetBool("json"))
            {
                Console.WriteLine(new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = ex.RpcCode, ["message"] = ex.Message },
                }.ToJsonString());
            }
            else
            {
                ConsoleUi.Error($"[{ex.RpcCode}] {ex.Message}");
            }

            // 结构化错误码透传为退出码偏移：脚本可按码断言（如 ElevationRequired = -32015）
            return 2;
        }
    }
}
