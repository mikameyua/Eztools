// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Host.Config;

/// <summary>一个工具的配置快照（读一次文件算出来的全部信息）。</summary>
public sealed record ConfigSnapshot(
    string ToolId,
    ConfigSchema Schema,
    JsonObject Saved,
    JsonObject Effective,
    IReadOnlyList<string> OrphanKeys,
    string FilePath,
    bool RecoveredFromCorrupt);

/// <summary>
/// 配置中心（设计方案 §6.2 / §7，P1a）。
///
/// 职责：把 <c>%APPDATA%\Eztools\config\&lt;toolId&gt;.json</c> 这条链补全 ——
/// <b>读 → 合并默认值 → 校验 → 原子写 → 交给进程层注入工具</b>。
///
/// 三条硬要求（都不是可选项）：
/// <list type="number">
/// <item><b>原子写</b>：先写 <c>.tmp</c> 再 <c>Move(overwrite)</c>。
///   直接覆盖写会在断电/崩溃时留下半个 JSON，而配置文件坏了用户是察觉不到的。</item>
/// <item><b>损坏文件绝不静默覆盖</b>：解析失败时把原文件改名为 <c>.corrupt-&lt;时间戳&gt;</c>，
///   用默认值继续跑，并记一条 Error。**宁可留一个看不懂的文件，也不能把用户配置抹掉。**</item>
/// <item><b>注入面 == schema</b>：只有 schema 声明过的字段才会进 <see cref="ConfigSnapshot.Effective"/>。
///   schema 里已删除的旧键仍保留在文件里（不销毁数据），但不再注入工具，
///   并通过 <see cref="ConfigSnapshot.OrphanKeys"/> 让 CLI 提示用户。</item>
/// </list>
///
/// **本类刻意不做的事**：不碰 <c>host.storage</c>（工具私有 KV）。
/// 两者语义不同 —— 配置是"用户改的、有 schema、该被看见"，KV 是"工具自己写的、没 schema、用户不该看见"；
/// 合并会让"用户点重置"把工具内部状态一起清掉。
/// </summary>
public sealed class ConfigStore
{
    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;

    // 配置读写很少发生、每次都是小文件，所以一把全局锁足够；
    // 按 toolId 分锁只会增加复杂度，换不到任何实际收益。
    private readonly object _gate = new();

    public ConfigStore(EztoolsPaths paths, HostLog log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>某个工具的配置文件绝对路径（用户手工编辑时用）。</summary>
    public string ConfigPath(string toolId) => _paths.ToolConfigFile(toolId);

    /// <summary>
    /// 读一次配置，算出全部需要的信息。
    /// 文件不存在是**正常状态**（不是错误）——此时 Saved 为空对象，Effective 全是默认值。
    /// </summary>
    public ConfigSnapshot Load(string toolId, JsonObject? schemaJson)
    {
        lock (_gate)
        {
            var path = ConfigPath(toolId);
            var saved = ReadSaved(toolId, path, out var recovered);
            var schema = ConfigSchema.FromJson(schemaJson);

            return new ConfigSnapshot(
                toolId,
                schema,
                saved,
                ConfigValues.Merge(schema, saved),
                ConfigValues.OrphanKeys(schema, saved),
                path,
                recovered);
        }
    }

    /// <summary>只取有效配置 —— 进程层注入用的就是它。</summary>
    public JsonObject Effective(string toolId, JsonObject? schemaJson) =>
        Load(toolId, schemaJson).Effective;

    /// <summary>
    /// 设置一个配置项。**只接受 schema 声明过的键** ——
    /// 没声明的键既没法渲染设置页、也没法校验类型，写进去只会变成
    /// "用户看不见、也改不掉"的垃圾。这类数据应该走 <c>host.storage</c>。
    /// </summary>
    public ConfigSetResult Set(string toolId, JsonObject? schemaJson, string key, string rawValue)
    {
        var schema = ConfigSchema.FromJson(schemaJson);
        var field = schema.Field(key);
        if (field is null)
        {
            var hint = schema.HasFields
                ? "该工具声明的配置项：" + string.Join(" / ", schema.Fields.Select(f => f.Key))
                : "该工具没有声明任何配置项（要存工具自己的数据请用 host.storage）";
            return ConfigSetResult.Fail($"工具 {toolId} 没有声明配置项 '{key}'。{hint}");
        }

        var coerced = ConfigValues.Coerce(field, rawValue);
        if (!coerced.Ok)
        {
            return coerced;
        }

        lock (_gate)
        {
            var path = ConfigPath(toolId);
            var saved = ReadSaved(toolId, path, out _);
            saved[key] = coerced.Value!.DeepClone();
            WriteAtomic(path, saved);

            _log.Info(
                $"配置 {toolId}.{key} = {JsonText.Write(coerced.Value, indented: false)}",
                "config");
        }

        return coerced;
    }

    /// <summary>删除一个配置项，让它回落到 schema 的默认值。键本来就不存在时是空操作。</summary>
    public bool Unset(string toolId, string key)
    {
        lock (_gate)
        {
            var path = ConfigPath(toolId);
            var saved = ReadSaved(toolId, path, out _);
            if (!saved.Remove(key))
            {
                return false;
            }

            WriteAtomic(path, saved);
            _log.Info($"配置 {toolId}.{key} 已删除，回落默认值", "config");
            return true;
        }
    }

    /// <summary>
    /// 重置该工具的全部配置（删掉文件）。
    /// **刻意不删 <c>.corrupt-*</c> 备份** —— 那是出问题时的唯一线索。
    /// </summary>
    public bool Reset(string toolId)
    {
        lock (_gate)
        {
            var path = ConfigPath(toolId);
            if (!File.Exists(path))
            {
                return false;
            }

            // 🔴 改名备份而不是 File.Delete：损坏文件都有 .corrupt-* 备份，
            //    用户**主动**重置却把配置裸删，是同一个原则的自相矛盾 ——
            //    "宁可留一个看不懂的文件，也不能把用户配置抹掉"（本类类头注释）。
            //    误点恢复默认（或工具误调用）时，.reset-backup-* 是唯一能挽回的线索。
            var backup = $"{path}.reset-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup);
            _log.Info($"配置 {toolId} 已重置（原文件备份为 {Path.GetFileName(backup)}）", "config");
            return true;
        }
    }

    /// <summary>该工具是否有落盘的配置（不含只有默认值的情况）。</summary>
    public bool HasSavedConfig(string toolId) => File.Exists(ConfigPath(toolId));

    // ── 内部 ────────────────────────────────────────────────────────────────

    private JsonObject ReadSaved(string toolId, string path, out bool recovered)
    {
        recovered = false;

        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var text = File.ReadAllText(path, new UTF8Encoding(false));
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }

            if (JsonNode.Parse(text) is JsonObject obj)
            {
                return obj;
            }

            throw new JsonException("JSON 根节点不是对象");
        }
        catch (Exception ex)
        {
            // 🔴 绝不静默覆盖：先留档，再用默认值继续。
            //    用户手工编坏了配置是正常会发生的事，把数据抹掉才是真的伤害。
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var backup = $"{path}.corrupt-{stamp}";
            var backedUp = false;
            try
            {
                File.Move(path, backup, overwrite: true);
                backedUp = true;
            }
            catch (Exception moveEx)
            {
                _log.Error($"配置文件 {path} 损坏且无法备份：{moveEx.Message}", "config");
            }

            _log.Error(
                $"配置文件 {toolId} 解析失败，已{(backedUp ? $"备份为 {Path.GetFileName(backup)} 并" : "")}改用默认值：{ex.Message}",
                "config");

            recovered = true;
            return new JsonObject();
        }
    }

    private void WriteAtomic(string path, JsonObject content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 先写同目录下的 .tmp 再整体改名 —— 同一个卷上的 rename 是原子的，
        // 不会出现"写到一半"的中间态。
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonText.Write(content), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
