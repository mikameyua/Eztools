// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Arbitration;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt hotkeys</c> / <c>ezt hotkey</c>：热键仲裁的可视化入口（§6.3）。
///
/// 列表 = 全部生效热键（override 优先）+ 仲裁归属（保留先注册者，工具 Id 序即注册序）；
/// set/unset = 写 <c>state.json</c> 的 <c>hotkeyOverrides</c>，写完立即重仲裁并展示新归属 ——
/// 托盘「刷新菜单」会走 <c>ReRegisterHotkeys</c>，改键后无需重启托盘。
/// </summary>
internal static class HotkeysCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        // cli.Command 是主命令（hotkeys/hotkey），子命令在 Rest[0]，参数从 Rest[1] 起
        var sub = cli.Rest.FirstOrDefault() ?? string.Empty;
        var args = cli.Rest.Skip(1).ToList();

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);

        return sub switch
        {
            "" or "list" => List(host, cli),
            "set" => Set(host, args),
            "unset" => Unset(host, args),
            _ => UnknownSub(sub),
        };
    }

    private static int UnknownSub(string sub)
    {
        ConsoleUi.Error($"未知的 hotkeys 子命令 '{sub}'（可用：list / set / unset）");
        return 64;
    }

    /// <summary>收集全部生效热键并仲裁。返回（claims, result, 来源标注, 面板归属）。</summary>
    private static (List<HotkeyClaim> Claims,
                    HotkeyArbitrationResult Result,
                    Dictionary<string, string> Sources,
                    Dictionary<string, string> OpensPanels)
        Collect(EztoolsHost host)
    {
        var claims = new List<HotkeyClaim>();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var opensPanels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tool, hotkey, effective) in host.Registry.Hotkeys(host.StateStore))
        {
            var combo = HotkeyCombo.TryParse(effective);
            if (combo is null)
            {
                // 清单阶段已有 HotkeyInvalid 诊断；这里跳过即可
                continue;
            }

            claims.Add(new HotkeyClaim(tool.Id, hotkey.Command, combo, claims.Count));
            sources[hotkey.Command] = effective == hotkey.Default ? "默认" : $"覆盖（原 {hotkey.Default}）";

            // opensPanel 是"命令成功后的附加动作"，属于**声明面**而非仲裁面 ——
            // 仲裁只裁决"键归谁"（HotkeyClaim 也没有它的位置），所以这里单独带出来给 ezt hotkeys 显示。
            if (hotkey.OpensPanel is { Length: > 0 } panelId)
            {
                opensPanels[hotkey.Command] = panelId;
            }
        }

        return (claims, HotkeyArbitration.Arbitrate(claims), sources, opensPanels);
    }

    private static int List(EztoolsHost host, CliArgs cli)
    {
        var (claims, result, sources, opensPanels) = Collect(host);

        if (cli.GetBool("json"))
        {
            var node = new JsonArray();
            foreach (var claim in claims)
            {
                var rid = claim.Combo.ResourceId;
                var isWinner = result.Winners.TryGetValue(rid, out var w) && w == claim;
                var blockedBy = result.Winners.TryGetValue(rid, out var holder) && !isWinner
                    ? holder.ToolId
                    : null;
                var entry = new JsonObject
                {
                    ["combo"] = claim.Combo.Normalized,
                    ["tool"] = claim.ToolId,
                    ["command"] = claim.Command,
                    ["source"] = sources.GetValueOrDefault(claim.Command, "默认"),
                    ["status"] = isWinner ? "wins" : $"blocked-by:{blockedBy}",
                };

                // 仅声明了才加这个键（`["k"] = null` 会序列化成 `"k": null` 而非省略）——
                // 断言用"键是否存在"判断"这条热键会不会开面板"，null 占位会让它永远为真。
                if (opensPanels.TryGetValue(claim.Command, out var opensPanel))
                {
                    entry["opensPanel"] = opensPanel;
                }

                node.Add(entry);
            }

            ConsoleUi.PrintJson(new JsonObject
            {
                ["count"] = claims.Count,
                ["conflicts"] = result.Losers.Count,
                ["hotkeys"] = node,
            }, cli.GetBool("compact"));
            return 0;
        }

        ConsoleUi.Section("热键（按仲裁归属：保留先注册者，工具 Id 序即注册序）");
        var rows = claims.Select(c =>
        {
            var rid = c.Combo.ResourceId;
            var isWinner = result.Winners.TryGetValue(rid, out var w) && w == c;
            var status = isWinner
                ? "✓ 生效"
                : $"✗ 让给 {result.Winners[rid].ToolId}";
            return new[]
            {
                c.Combo.Normalized, c.ToolId, c.Command,
                sources.GetValueOrDefault(c.Command, "默认"), status,
            };
        }).ToList();
        ConsoleUi.Table(new[] { "组合键", "工具", "命令", "来源", "状态" }, rows);

        Console.WriteLine();
        if (result.Losers.Count > 0)
        {
            ConsoleUi.Warn($"{result.Losers.Count} 个热键在仲裁中落选；用 `ezt hotkey set` 改键后可共存");
        }
        else
        {
            ConsoleUi.Ok($"共 {claims.Count} 个热键，无内部冲突");
        }

        ConsoleUi.Info("改键：ezt hotkey set <命令id> <组合键>   恢复：ezt hotkey unset <命令id>");
        return 0;
    }

    private static int Set(EztoolsHost host, List<string> args)
    {
        if (args.Count < 2)
        {
            ConsoleUi.Error("用法：ezt hotkey set <命令id> <组合键>    例：ezt hotkey set wordcount.count Ctrl+Shift+W");
            return 64;
        }

        var command = args[0];
        var combo = HotkeyCombo.TryParse(args[1]);
        if (combo is null)
        {
            ConsoleUi.Error($"组合键 '{args[1]}' 无法解析（支持 Ctrl/Alt/Shift/Win + 字母/数字/F1~F12）");
            return 2;
        }

        if (!host.Registry.TryResolveCommand(command, out var binding))
        {
            ConsoleUi.Error($"未找到命令 '{command}'（用 ezt list --commands 查看全部命令）");
            return 4;
        }

        var toolId = binding.Tool.Id;
        host.StateStore.SetHotkeyOverride(toolId, command, combo.Normalized);
        ConsoleUi.Ok($"已覆盖 {command} → {combo.Normalized}（{toolId}）");

        var (_, result, _, _) = Collect(host);
        if (result.Losers.Count > 0)
        {
            foreach (var (loser, heldBy) in result.Losers)
            {
                ConsoleUi.Warn($"{loser.Combo.Normalized}（{loser.ToolId}:{loser.Command}）被 {heldBy} 压制");
            }

            ConsoleUi.Info("托盘里点「刷新菜单」后生效");
        }
        else
        {
            ConsoleUi.Ok("改键后无冲突；托盘里点「刷新菜单」后生效");
        }

        return 0;
    }

    private static int Unset(EztoolsHost host, List<string> args)
    {
        if (args.Count < 1)
        {
            ConsoleUi.Error("用法：ezt hotkey unset <命令id>");
            return 64;
        }

        var command = args[0];
        if (!host.Registry.TryResolveCommand(command, out var binding))
        {
            ConsoleUi.Error($"未找到命令 '{command}'");
            return 4;
        }

        // 幂等：没有覆盖时也返回成功（unset 的语义就是"之后一定是默认值"）
        host.StateStore.RemoveHotkeyOverride(binding.Tool.Id, command);
        ConsoleUi.Ok($"已移除 {command} 的覆盖（回退清单默认）");
        return 0;
    }
}
