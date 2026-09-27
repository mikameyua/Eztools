// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Config;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt config</c> —— 配置中心（P1a）。
///
/// 子命令：
/// <code>
/// ezt config list [toolId]             列出配置（带"默认值 / 已设置"来源标注）
/// ezt config get  &lt;toolId&gt; [key]     取**有效值**（默认值 ⊕ 已存值）
/// ezt config set  &lt;toolId&gt; &lt;key&gt; &lt;value&gt;
/// ezt config unset &lt;toolId&gt; &lt;key&gt;     删除该键 → 回落默认值
/// ezt config reset &lt;toolId&gt;           重置该工具全部配置
/// ezt config path &lt;toolId&gt;            打印配置文件路径（手工编辑用）
/// ezt config schema &lt;toolId&gt;          打印 schema —— **这就是将来设置页的输入**
/// </code>
///
/// 退出码沿用项目约定：0 成功 · 2 校验/操作失败 · 4 未找到工具 · 64 用法错误。
/// </summary>
internal static class ConfigCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");

        var sub = cli.Rest.FirstOrDefault();
        var toolId = cli.Rest.Skip(1).FirstOrDefault();

        return sub switch
        {
            null or "list" => await ListAsync(cli, toolId),
            "get" => await GetAsync(cli, toolId, cli.Rest.Skip(2).FirstOrDefault()),
            "set" => await SetAsync(cli, toolId, cli.Rest.Skip(2).FirstOrDefault(), cli.Rest.Skip(3).ToList()),
            "unset" => await UnsetAsync(cli, toolId, cli.Rest.Skip(2).FirstOrDefault()),
            "reset" => await ResetAsync(cli, toolId),
            "path" => await PathAsync(cli, toolId),
            "schema" => await SchemaAsync(cli, toolId),
            _ => Usage($"未知子命令 '{sub}'"),
        };
    }

    // ── list ────────────────────────────────────────────────────────────────

    private static async Task<int> ListAsync(CliArgs cli, string? toolId)
    {
        await using var host = await Program.CreateHostAsync(cli, echoLog: false);

        if (string.IsNullOrWhiteSpace(toolId))
        {
            var rows = new List<string[]>();
            foreach (var tool in host.Registry.Tools)
            {
                var snap = host.Configs.Load(tool.Id, tool.Manifest.ConfigSchema);
                rows.Add(new[]
                {
                    tool.Id,
                    snap.Schema.Fields.Count.ToString(),
                    host.Configs.HasSavedConfig(tool.Id) ? "是" : "—",
                    snap.OrphanKeys.Count == 0 ? "" : $"{snap.OrphanKeys.Count} 个键不在 schema 中",
                });
            }

            // 宿主设置节（W4-c）：desktop 保留节与工具配置一样"用户改的、有 schema、该被看见"，
            // list 里必须有它 —— 否则用户从 CLI 视角根本发现不了宿主设置的存在。
            var hostSnap = host.Configs.Load(HostSettingsSchema.SectionId, HostSettingsSchema.SchemaJson());
            rows.Add(new[]
            {
                HostSettingsSchema.SectionId,
                hostSnap.Schema.Fields.Count.ToString(),
                host.Configs.HasSavedConfig(HostSettingsSchema.SectionId) ? "是" : "—",
                "（宿主设置：热键 / OCR 语言）",
            });

            if (cli.GetBool("json"))
            {
                var arr = new JsonArray();
                foreach (var tool in host.Registry.Tools)
                {
                    var snap = host.Configs.Load(tool.Id, tool.Manifest.ConfigSchema);
                    arr.Add(new JsonObject
                    {
                        ["toolId"] = tool.Id,
                        ["fieldCount"] = snap.Schema.Fields.Count,
                        ["hasSaved"] = host.Configs.HasSavedConfig(tool.Id),
                        ["effective"] = snap.Effective.DeepClone(),
                        ["orphanKeys"] = new JsonArray(snap.OrphanKeys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                    });
                }

                var hostSnapJson = host.Configs.Load(HostSettingsSchema.SectionId, HostSettingsSchema.SchemaJson());
                arr.Add(new JsonObject
                {
                    ["toolId"] = HostSettingsSchema.SectionId,
                    ["fieldCount"] = hostSnapJson.Schema.Fields.Count,
                    ["hasSaved"] = host.Configs.HasSavedConfig(HostSettingsSchema.SectionId),
                    ["effective"] = hostSnapJson.Effective.DeepClone(),
                    ["orphanKeys"] = new JsonArray(hostSnapJson.OrphanKeys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                    ["isHostSection"] = true,
                });

                ConsoleUi.PrintJson(arr);
                return 0;
            }

            if (rows.Count == 0)
            {
                ConsoleUi.Warn("没有发现任何工具");
                return 0;
            }

            ConsoleUi.Table(new[] { "工具", "配置项", "已落盘", "备注" }, rows);
            ConsoleUi.Info("看某个工具的实际值：ezt config list <工具ID>");
            return 0;
        }

        // 单个工具的明细（desktop 保留节同样可查 —— W4-c 宿主设置）
        if (!TryResolveSchema(host, toolId, out var detailSchema))
        {
            return NotFound(toolId);
        }

        var detail = host.Configs.Load(toolId, detailSchema);

        if (!detail.Schema.HasFields)
        {
            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject { ["toolId"] = toolId, ["fields"] = new JsonArray() });
                return 0;
            }

            ConsoleUi.Info($"工具 {toolId} 没有声明任何配置项。");
            ConsoleUi.Info("（工具自己的数据应该用 host.storage，不走配置中心 —— 见设计方案 §6.2）");
            return 0;
        }

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(BuildDetailJson(detail));
            return 0;
        }

        ConsoleUi.Header($"配置 {toolId}");
        ConsoleUi.Field("配置文件", detail.FilePath);
        ConsoleUi.Field("落盘状态", host.Configs.HasSavedConfig(toolId) ? "有" : "无（全部用默认值）");
        if (detail.RecoveredFromCorrupt)
        {
            ConsoleUi.Warn("原配置文件损坏，已备份为 .corrupt-<时间戳> 并改用默认值");
        }

        ConsoleUi.Line();
        var table = new List<string[]>();
        foreach (var field in detail.Schema.Fields)
        {
            var isSet = detail.Saved.ContainsKey(field.Key);
            detail.Effective.TryGetPropertyValue(field.Key, out var effective);
            table.Add(new[]
            {
                field.Key,
                effective is null ? "（未设置）" : JsonText.Write(effective, indented: false),
                isSet ? "已设置" : "默认值",
                field.IsSecret ? "敏感" : "",
            });
        }

        ConsoleUi.Table(new[] { "键", "有效值", "来源", "标记" }, table);

        if (detail.OrphanKeys.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Warn(
                $"文件里有 {detail.OrphanKeys.Count} 个键不在当前 schema 中：{string.Join(", ", detail.OrphanKeys)}");
            ConsoleUi.Info("它们不会被注入工具，但也没有被删除（不销毁用户数据）。");
        }

        ConsoleUi.Line();
        ConsoleUi.Info($"改一个值：ezt config set {toolId} <键> <值>");
        return 0;
    }

    // ── get ─────────────────────────────────────────────────────────────────

    private static async Task<int> GetAsync(CliArgs cli, string? toolId, string? key)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return Usage("用法: ezt config get <工具ID> [键]");
        }

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        if (!TryResolveSchema(host, toolId, out var getSchema))
        {
            return NotFound(toolId);
        }

        var snap = host.Configs.Load(toolId, getSchema);

        if (string.IsNullOrWhiteSpace(key))
        {
            ConsoleUi.PrintJson(snap.Effective);
            return 0;
        }

        if (!snap.Effective.TryGetPropertyValue(key, out var value) || value is null)
        {
            if (snap.Schema.Field(key) is null)
            {
                ConsoleUi.Error($"工具 {toolId} 没有声明配置项 '{key}'");
                return 2;
            }

            ConsoleUi.Error($"'{key}' 当前既没有默认值、也没有设置过");
            return 2;
        }

        // 单值输出：JSON 模式下给机器读，否则给原始文本（方便管道）
        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(value);
        }
        else if (value is JsonValue v && v.TryGetValue(out string? s))
        {
            ConsoleUi.Line(s);
        }
        else
        {
            ConsoleUi.Line(JsonText.Write(value, indented: false));
        }

        return 0;
    }

    // ── set ─────────────────────────────────────────────────────────────────

    private static async Task<int> SetAsync(CliArgs cli, string? toolId, string? key, IReadOnlyList<string> valueParts)
    {
        if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(key) || valueParts.Count == 0)
        {
            return Usage("用法: ezt config set <工具ID> <键> <值>\n（值含空格时用引号；数组用逗号分隔；复杂结构用 JSON 写法）");
        }

        // 值可能被拆成多个位置参数（用户忘了加引号）——拼回去比报错友好
        var rawValue = string.Join(" ", valueParts);

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        if (!TryResolveSchema(host, toolId, out var setSchema))
        {
            return NotFound(toolId);
        }

        var isHostSection = HostSettingsSchema.IsSectionId(toolId);
        var result = host.Configs.Set(toolId, setSchema, key, rawValue);
        if (!result.Ok)
        {
            ConsoleUi.Error(result.Error!);
            return 2;
        }

        var snapshot = JsonText.Write(result.Value, indented: false);
        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["ok"] = true,
                ["toolId"] = toolId,
                ["key"] = key,
                ["value"] = result.Value!.DeepClone(),
            });
            return 0;
        }

        ConsoleUi.Ok($"已写入 {toolId}.{key} = {snapshot}");

        // 主动说清"什么时候生效"——否则会出现"我改了配置怎么没生效"的困惑。
        // desktop 保留节不是工具，生效语义也不同：热键由托盘在保存/刷新时重注册，
        // OCR 语言在下次唤出屏幕取字时读取。
        if (isHostSection)
        {
            ConsoleUi.Info("宿主设置：热键在托盘下次「刷新菜单」或设置窗口保存后重新注册；OCR 语言下次唤出屏幕取字生效");
        }
        else
        {
            // 走到这里时 toolId 必在 Registry（TryResolveSchema 已把非工具非 desktop 的 id 挡在 NotFound）
            _ = host.Registry.TryGetTool(toolId, out var tool);
            ConsoleUi.Info(tool!.Manifest.Lifecycle == ToolLifecycle.Resident
                ? "该工具是 resident：宿主会推送变更（属 P2，P1a 只交付通路）"
                : $"该工具是 {tool.Manifest.Lifecycle.ToWire()}：下次调用即生效（每次调用都会重起进程）");
        }

        // 若已落盘值与 schema 默认值相同，提示一下（文件里会留一条冗余项，但这是刻意的）
        var field = ConfigSchema.FromJson(setSchema).Field(key);
        if (field?.Default is not null && ConfigValues.SameValue(field.Default, result.Value))
        {
            ConsoleUi.Info($"该值等于 schema 里的默认值；它仍会被保留在文件中（这样你能看出是自己设过的）");
        }

        return 0;
    }

    // ── unset / reset ───────────────────────────────────────────────────────

    private static async Task<int> UnsetAsync(CliArgs cli, string? toolId, string? key)
    {
        if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(key))
        {
            return Usage("用法: ezt config unset <工具ID> <键>");
        }

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        if (!TryResolveSchema(host, toolId, out _))
        {
            return NotFound(toolId);
        }

        var removed = host.Configs.Unset(toolId, key);
        if (!removed)
        {
            ConsoleUi.Info($"{toolId}.{key} 本来就没设置过，配置未改动");
            return 0;
        }

        ConsoleUi.Ok($"已删除 {toolId}.{key}，回落到 schema 默认值");
        return 0;
    }

    private static async Task<int> ResetAsync(CliArgs cli, string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return Usage("用法: ezt config reset <工具ID>");
        }

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        if (!TryResolveSchema(host, toolId, out _))
        {
            return NotFound(toolId);
        }

        if (!host.Configs.Reset(toolId))
        {
            ConsoleUi.Info($"{toolId} 本来就没有配置文件，无需重置");
            return 0;
        }

        ConsoleUi.Ok($"已重置 {toolId} 的全部配置");
        ConsoleUi.Info("（.corrupt-* 备份不会被删除——那是出问题时的唯一线索）");
        return 0;
    }

    // ── path / schema ───────────────────────────────────────────────────────

    private static async Task<int> PathAsync(CliArgs cli, string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return Usage("用法: ezt config path <工具ID>");
        }

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        if (!TryResolveSchema(host, toolId, out _))
        {
            return NotFound(toolId);
        }

        ConsoleUi.Line(host.Configs.ConfigPath(toolId));
        return 0;
    }

    private static async Task<int> SchemaAsync(CliArgs cli, string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return Usage("用法: ezt config schema <工具ID>");
        }

        await using var host = await Program.CreateHostAsync(cli, echoLog: false);
        if (!TryResolveSchema(host, toolId, out var resolvedSchema))
        {
            return NotFound(toolId);
        }

        var schema = resolvedSchema;
        if (schema is null)
        {
            ConsoleUi.Error($"工具 {toolId} 没有声明 config schema");
            return 2;
        }

        ConsoleUi.PrintJson(schema.DeepClone());
        return 0;
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析配置目标的 schema 来源：工具清单优先；<b>desktop 保留节</b>（W4-c）回落到
    /// <see cref="HostSettingsSchema.SchemaJson"/>。后者让宿主设置（热键 / OCR 语言）
    /// 在 CLI 与设置窗口两个入口改的是同一个文件 —— 缺了 CLI 这一半，闭环就名存实亡。
    /// </summary>
    private static bool TryResolveSchema(EztoolsHost host, string toolId, out JsonObject? schemaJson)
    {
        if (host.Registry.TryGetTool(toolId, out var tool))
        {
            schemaJson = tool.Manifest.ConfigSchema;
            return true;
        }

        if (HostSettingsSchema.IsSectionId(toolId))
        {
            schemaJson = HostSettingsSchema.SchemaJson();
            return true;
        }

        schemaJson = null;
        return false;
    }

    private static JsonObject BuildDetailJson(ConfigSnapshot snap)
    {
        var fields = new JsonArray();
        foreach (var field in snap.Schema.Fields)
        {
            snap.Effective.TryGetPropertyValue(field.Key, out var effective);
            fields.Add(new JsonObject
            {
                ["key"] = field.Key,
                ["type"] = field.TypeName,
                ["title"] = field.Title,
                ["description"] = field.Description,
                ["value"] = effective?.DeepClone(),
                ["source"] = snap.Saved.ContainsKey(field.Key) ? "saved" : "default",
                ["isSecret"] = field.IsSecret,
                ["format"] = field.Format,
                ["minimum"] = field.Minimum,
                ["maximum"] = field.Maximum,
            });
        }

        return new JsonObject
        {
            ["toolId"] = snap.ToolId,
            ["path"] = snap.FilePath,
            ["recoveredFromCorrupt"] = snap.RecoveredFromCorrupt,
            ["orphanKeys"] = new JsonArray(snap.OrphanKeys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
            ["fields"] = fields,
        };
    }

    private static int NotFound(string toolId)
    {
        ConsoleUi.Error($"未找到工具 '{toolId}'。用 `ezt list` 看有哪些。");
        return 4;
    }

    private static int Usage(string message)
    {
        ConsoleUi.Error(message);
        ConsoleUi.Line();
        ConsoleUi.Line("  ezt config list [工具ID]           列出配置（标注 默认值 / 已设置）");
        ConsoleUi.Line("  ezt config get  <工具ID> [键]      取有效值");
        ConsoleUi.Line("  ezt config set  <工具ID> <键> <值> 设置一项");
        ConsoleUi.Line("  ezt config unset <工具ID> <键>     删除一项（回落默认值）");
        ConsoleUi.Line("  ezt config reset <工具ID>          重置该工具全部配置");
        ConsoleUi.Line("  ezt config path <工具ID>           打印配置文件路径");
        ConsoleUi.Line("  ezt config schema <工具ID>         打印 schema（设置页的输入）");
        return 64;
    }
}
