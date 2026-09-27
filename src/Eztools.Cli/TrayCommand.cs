// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Tray;

namespace Eztools.Cli;

/// <summary>
/// `ezt tray` —— 查看托盘菜单的**合成结果**。
///
/// 为什么由 CLI 提供：托盘本体是 WinExe（没有控制台），而"菜单会排成什么样"是
/// **框架无关**的合成产物，命令行看它最方便 —— 同时它也是验收脚本的入口
/// （`--json` 可断言、`--check` 做静态检查）。
///
/// 它**不启动托盘**，那由 `Eztools.Desktop` 负责；这里只回答"菜单长什么样"。
/// 这条分工让"托盘"在没有 UI 框架的情况下也能被验证（见 docs/P2-托盘-实施方案.md §8）。
/// </summary>
internal static class TrayCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        var model = TrayMenuBuilder.Build(host.Registry);
        var problems = Check(host, model);

        if (cli.GetBool("json"))
        {
            PrintJson(model, problems);
        }
        else
        {
            PrintHuman(model);
            if (cli.GetBool("check"))
            {
                PrintCheck(model, problems);
            }
        }

        // 只有显式要求 --check 时才让退出码反映问题 —— 否则"看一眼菜单"也会莫名其妙失败
        return cli.GetBool("check") && problems.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// 静态检查：托盘项引用的命令是否真的注册上了。
    ///
    /// **它检查不了"点了能不能跑通"** —— 那取决于工具 handler 要不要参数，是运行期才知道的。
    /// 所以"每个托盘项零参可跑"这条规则由 `scripts/acceptance.sh` 用具体输入真调一次来守
    /// （见 docs/P2-托盘-实施方案.md §1.2）。这里只兜住"引用了不存在的命令"这类静态错。
    /// </summary>
    private static List<string> Check(EztoolsHost host, TrayMenuModel model)
    {
        var problems = new List<string>();

        foreach (var item in model.Items)
        {
            if (!host.Registry.TryResolveCommand(item.CommandId, out _))
            {
                problems.Add($"托盘项「{item.Title}」引用的命令 {item.CommandId} 未注册（工具 {item.ToolId}）");
            }
        }

        return problems;
    }

    private static void PrintHuman(TrayMenuModel model)
    {
        if (model.Count == 0)
        {
            ConsoleUi.Info("没有工具声明托盘菜单（需要在 tool.json 的 contributes.menus 里写 location: \"tray\"）");
            return;
        }

        ConsoleUi.Section($"托盘菜单（{model.Count} 项 / {model.Groups.Count} 组）");
        ConsoleUi.Table(
            new[] { "分组", "条目", "命令", "输入来源" },
            model.Items.Select(i => new[]
            {
                string.IsNullOrEmpty(i.GroupKey) ? "（顶层）" : TrayMenuBuilder.GroupDisplayName(i.GroupKey),
                i.Title,
                i.CommandId,
                i.Input.ToWire(),
            }).ToList());

        ConsoleUi.Info("输入来源 none = 命令自足；clipboard = 宿主把剪贴板文本注入 args[\"input\"]");
    }

    private static void PrintCheck(TrayMenuModel model, IReadOnlyList<string> problems)
    {
        ConsoleUi.Section("静态检查");

        if (problems.Count == 0)
        {
            ConsoleUi.Pass($"全部 {model.Count} 个托盘项引用的命令都能解析到工具");
            ConsoleUi.Info("「点了能不能跑通」是运行期问题，由 acceptance.sh 真调一次来守，不在这里判");
        }
        else
        {
            foreach (var problem in problems)
            {
                ConsoleUi.Fail(problem);
            }
        }
    }

    private static void PrintJson(TrayMenuModel model, IReadOnlyList<string> problems)
    {
        var obj = new JsonObject
        {
            ["count"] = model.Count,
            ["groupCount"] = model.Groups.Count,
            ["groups"] = new JsonArray(model.Groups.Select(g => (JsonNode)new JsonObject
            {
                ["key"] = g.Items.Count > 0 ? g.Items[0].GroupKey : null,
                ["title"] = g.Title,
                ["count"] = g.Items.Count,
            }).ToArray()),
            ["items"] = new JsonArray(model.Items.Select(i => (JsonNode)new JsonObject
            {
                ["toolId"] = i.ToolId,
                ["commandId"] = i.CommandId,
                ["title"] = i.Title,
                ["group"] = i.GroupKey,
                ["groupTitle"] = TrayMenuBuilder.GroupDisplayName(i.GroupKey),
                ["input"] = i.Input.ToWire(),
            }).ToArray()),
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        };

        ConsoleUi.PrintJson(obj);
    }
}
