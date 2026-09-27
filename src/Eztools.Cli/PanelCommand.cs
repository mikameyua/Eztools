// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

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
                toolId, panelId, reason: PanelDataArgs.Reason.Open, ct: CancellationToken.None);

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

        // ── 节点级校验（协议 §3.7.6）──
        //
        // 为什么由 CLI 的渲染路径顺手做：input 节点是**运行时产物**，清单解析期看不到它，
        // 所以"key 必需 / key 重复 / submitCommandId 引用"这几条只能在拿到载荷之后判。
        // CLI 是唯一一条"不依赖 GUI 就能跑通载荷"的路径 ⇒ 把它挂在这里，验收脚本就能断言。
        // （见 docs/P4-Wave2c-面板协议.md §12.2 第 1 步：契约层先行，20.8 / 20.10。）
        ReportNodeIssues(result);

        ConsoleUi.Info($"共 {nodes.Count} 个节点，耗时 {result.Elapsed.TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// 把 <see cref="PanelNodeValidator"/> 发现的问题打出来。
    ///
    /// <b>为什么走 <c>Warn</c> 而不是 <c>Error</c></b>：节点级问题**不阻塞渲染**
    /// （协议 §3.5：一个坏节点不该让整块面板白屏）。Error 那条流在本项目里意味着
    /// "清单被拒绝注册"，用在这里会把严重度语义搞乱。
    /// 严重度只体现在**文案**里（问题本身已经带 code 前缀，见下），
    /// 于是断言只需匹配 `code`，不必依赖"警告/错误"这两个中文字。
    ///
    /// 注意 <see cref="ConsoleUi.Warn"/> 自带 <c>[警告]</c> 前缀，这里**不再重复加**。
    /// </summary>
    private static void ReportNodeIssues(PanelDataResult result)
    {
        // 已声明命令清单：用于 submitCommandId 的引用校验（口径同 §2.4）。
        var declared = result.DeclaredCommandIds;
        var issues = PanelNodeValidator.ValidateNodes(result.Data, declared);

        foreach (var issue in issues)
        {
            ConsoleUi.Warn($"{issue.Code}: {issue.Message}");
        }
    }

    /// <summary>
    /// 节点渲染（协议 §3.4 的 CLI 版）。
    /// **简化实现**：只做缩进树，不追求与 WPF 视觉一致（协议 §11 第 6 条）。
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

            case "input":
                // 协议 §3.7.1 的 CLI 表示（20.1 的判据）。
                //
                // 为什么 `input` 必须在这里被"支持"，而 `image` 可以是"未知节点"：
                // `image` 在终端里**真的画不出来** —— 报"未知节点"是诚实的；
                // 而 `input` 能用一行文字完整表达（key + 当前值 + 占位提示），
                // 报"未知节点"就纯粹是噪音，还会让 CLI 与 WPF 的渲染能力无谓地分叉。
                //
                // 值的引号：**总是加**。空串 `""` 与空格 `" "` 在无引号时长得一样 ——
                // 而 20.6/20.3 恰恰要区分"没输入"和"输入了空白"。引号把这件事变得可见。
                var key = Text(obj["key"]);
                var inputValue = Text(obj["value"]);
                var placeholder = Text(obj["placeholder"]);
                var submitId = Text(obj["submitCommandId"]);

                var line = new System.Text.StringBuilder();
                line.Append($"{pad}[输入框 key={key} value=\"{inputValue}\"");
                if (placeholder.Length > 0)
                {
                    line.Append($" placeholder=\"{placeholder}\"");
                }

                if (submitId.Length > 0)
                {
                    line.Append($" submit={submitId}");
                }

                line.Append(']');
                sb.AppendLine(line.ToString());
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
