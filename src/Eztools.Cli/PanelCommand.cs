using System.Text;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Processes;
using Eztools.Host.Registry;

namespace Eztools.Cli;

/// <summary>
/// `ezt panel` —— 面板数据通道（P4 Wave 2c）。
///
/// **为什么这个命令必须存在**：面板有两个消费方（WPF 窗口 / 命令行），
/// 二者读的是**同一条** <c>tool.panel.data</c> 通道。把这条路开在 CLI 上，
/// 验收脚本就能**不依赖 GUI** 断言整条数据链路 —— 只有"WPF 真的画出来了"才需要窗口。
/// 见 `docs/P4-Wave2c-面板协议.md` §7.2 / §9。
///
/// 三个子形态（协议 §7.2）：
/// <code>
/// ezt panel                      # 列出所有工具的面板
/// ezt panel mytool               # 列出该工具的面板
/// ezt panel mytool main          # 拉取并打印面板数据（默认 JSON）
/// ezt panel mytool main --text   # 人类可读渲染（缩进树）
/// </code>
/// </summary>
internal static class PanelCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        var toolId = cli.Rest.ElementAtOrDefault(0);
        var panelId = cli.Rest.ElementAtOrDefault(1);

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);

        // ── 无参数：列出全部面板 ──
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return ListPanels(host, cli, filterToolId: null);
        }

        // ── 只有工具 id：列该工具的面板 ──
        if (string.IsNullOrWhiteSpace(panelId))
        {
            if (!host.Registry.TryGetTool(toolId, out _))
            {
                ConsoleUi.Error($"未找到工具 '{toolId}'。用 `ezt list` 看可用工具。");
                return 2;
            }

            return ListPanels(host, cli, toolId);
        }

        // ── 工具 + 面板：拉数据 ──
        try
        {
            var result = await host.Processes.PanelDataAsync(
                toolId, panelId, reason: "open", ct: CancellationToken.None);

            if (cli.GetBool("json") || !cli.GetBool("text"))
            {
                PrintDataJson(result);
            }
            else
            {
                PrintDataText(result);
            }

            return 0;
        }
        catch (ToolProtocolException ex)
        {
            // 错误码进 JSON（脚本可断言），人读则走 stderr
            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject
                {
                    ["ok"] = false,
                    ["toolId"] = toolId,
                    ["panelId"] = panelId,
                    ["code"] = ex.Code,
                    ["error"] = ex.Message,
                });
            }
            else
            {
                ConsoleUi.Error(ex.Message);
            }

            return 2;
        }
    }

    private static int ListPanels(EztoolsHost host, CliArgs cli, string? filterToolId)
    {
        var rows = new List<string[]>();
        foreach (var tool in host.Registry.Tools)
        {
            if (filterToolId is not null &&
                !string.Equals(tool.Id, filterToolId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var p in tool.Manifest.Contributes.Panels)
            {
                rows.Add(new[]
                {
                    $"{tool.Id}.{p.Id}",
                    p.Title,
                    p.Width + "×" + p.Height,
                    p.RefreshMs == 0 ? "手动" : $"{p.RefreshMs}ms",
                    tool.Manifest.Weight.ToWire(),
                });
            }
        }

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["count"] = rows.Count,
                ["panels"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["toolId"] = r[0].Split('.')[0],
                    ["panelId"] = r[0].Contains('.') ? r[0][(r[0].IndexOf('.') + 1)..] : r[0],
                    ["qualified"] = r[0],
                    ["title"] = r[1],
                    ["width"] = int.Parse(r[2].Split('×')[0]),
                    ["height"] = int.Parse(r[2].Split('×')[1]),
                    ["refreshMs"] = r[3] == "手动" ? 0 : int.Parse(r[3].TrimEnd('m', 's')),
                    ["weight"] = r[4],
                }).ToArray()),
            });

            return 0;
        }

        if (rows.Count == 0)
        {
            ConsoleUi.Info(filterToolId is null
                ? "没有任何工具声明面板（需要在 tool.json 的 contributes.panels 里声明，且 weight 必须是 full）"
                : $"工具 {filterToolId} 没有声明面板");
            return 0;
        }

        ConsoleUi.Section($"面板（{rows.Count} 个）");
        ConsoleUi.Table(new[] { "标识", "标题", "尺寸", "自动刷新", "weight" }, rows);
        ConsoleUi.Info("用 `ezt panel <toolId> <panelId>` 拉取数据（--text 看人类可读版）");
        return 0;
    }

    private static void PrintDataJson(PanelDataResult result)
    {
        // 刻意**不解析 / 不规范化 nodes**：把工具返回原样透出 —— 验收要能看到"工具到底说了什么"，
        // 宿主若在这里做规范化，脚本断言的就成了宿主加工后的东西（假阳性来源）。
        var obj = new JsonObject
        {
            ["ok"] = true,
            ["toolId"] = result.ToolId,
            ["panelId"] = result.PanelId,
            ["title"] = result.Panel.Title,
            ["width"] = result.Panel.Width,
            ["height"] = result.Panel.Height,
            ["refreshMs"] = result.Panel.RefreshMs,
            ["elapsedMs"] = (int)result.Elapsed.TotalMilliseconds,
            // Clone 必须：Data 挂在其原树上，直接塞进来会抛 "The node already has a parent."
            ["data"] = result.Data?.Clone(),
        };

        // nodeCount 是给脚本用的便捷计数，**容忍非对象载荷**：
        // 工具返回数组/字符串（格式错误）是必须能观测到的负向用例，
        // 若这里直接索引 ["nodes"] 会抛 InvalidOperationException —— 那正是本人第一版踩的坑：
        // 负向用例把排查入口自己弄崩了，脚本只看到"命令失败"而看不到"工具返回了什么"。
        obj["nodeCount"] =
            result.Data is JsonObject o && o["nodes"] is JsonArray nodes ? nodes.Count : 0;
        obj["dataShape"] = Shape(result.Data);

        ConsoleUi.PrintJson(obj);
    }

    /// <summary>给"工具返回了什么形状"一个可断言的字符串（脚本用它判负向用例）。</summary>
    private static string Shape(JsonNode? data) => data switch
    {
        null => "null",
        JsonObject o => o["nodes"] is JsonArray ? "object-with-nodes" : "object-without-nodes",
        JsonArray => "array",
        JsonValue => "scalar",
        _ => "unknown",
    };

    private static void PrintDataText(PanelDataResult result)
    {
        ConsoleUi.Section($"{result.Panel.Title}  [{result.ToolId}.{result.PanelId}]");

        // 先判形状再取 nodes —— 工具返回非对象时给一条明确提示（而不是抛异常 / 显示空白）
        if (result.Data is not JsonObject obj)
        {
            ConsoleUi.Warn($"工具返回的载荷不是对象（形状：{Shape(result.Data)}），面板无法渲染");
            ConsoleUi.Info("按协议 §3.5，宿主应显示「工具返回格式不正确」而不是崩溃");
            return;
        }

        if (obj["nodes"] is not JsonArray nodes || nodes.Count == 0)
        {
            ConsoleUi.Info("（此面板当前无内容）");
            return;
        }

        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            RenderNode(sb, node, indent: 0);
        }

        Console.WriteLine(sb.ToString().TrimEnd());

        ConsoleUi.Info($"共 {nodes.Count} 个节点，耗时 {result.Elapsed.TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// 节点渲染（协议 §3.4 的 CLI 版）。
    /// **简化实现**：只做缩进树，不追求与 WPF 视觉一致（协议 §11.6）。
    /// 未知 type 也渲染出来而不是跳过 —— 命令行是排查工具，看见"工具发了个我不认识的节点"比看不见有用。
    /// </summary>
    private static void RenderNode(StringBuilder sb, JsonNode? node, int indent)
    {
        var pad = new string(' ', indent * 2);
        if (node is not JsonObject obj)
        {
            sb.AppendLine($"{pad}（非法节点：不是对象）");
            return;
        }

        var type = obj["type"]?.GetValue<string>() ?? "(无 type)";
        switch (type)
        {
            case "heading":
                sb.AppendLine($"{pad}# {Text(obj["text"])}");
                break;

            case "text":
                sb.AppendLine($"{pad}{Text(obj["text"])}");
                break;

            case "kv":
                if (obj["items"] is JsonArray kv)
                {
                    foreach (var pair in kv)
                    {
                        if (pair is JsonArray p && p.Count >= 2)
                        {
                            sb.AppendLine($"{pad}{Text(p[0])}: {Text(p[1])}");
                        }
                    }
                }

                break;

            case "list":
                if (obj["items"] is JsonArray list)
                {
                    foreach (var item in list)
                    {
                        sb.AppendLine($"{pad}· {Text(item)}");
                    }
                }

                break;

            case "buttons":
                if (obj["items"] is JsonArray buttons)
                {
                    var labels = buttons
                        .OfType<JsonObject>()
                        .Select(b => $"[{Text(b["label"])} → {Text(b["commandId"])}]");
                    sb.AppendLine($"{pad}{string.Join(" ", labels)}");
                }

                break;

            case "separator":
                sb.AppendLine($"{pad}{new string('─', 24)}");
                break;

            default:
                // 命令行保留未知节点（与 WPF 的"跳过并提示"不同：这里没有渲染负担，信息越多越好）
                sb.AppendLine($"{pad}⚠ 未知节点类型 '{type}'");
                break;
        }
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
}
