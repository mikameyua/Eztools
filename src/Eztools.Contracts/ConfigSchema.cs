// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;

namespace Eztools.Contracts;

/// <summary>配置项类型。刻意只覆盖设置页渲染需要的子集（设计方案 §7）。</summary>
public enum ConfigFieldType
{
    Unknown,
    Boolean,
    String,
    Integer,
    Number,
    Array,
    Object,
}

/// <summary>
/// 一个配置字段的描述。
///
/// 它同时服务两个消费者，所以放在契约层：
/// <list type="bullet">
/// <item>P1a：校验用户输入、生成默认值</item>
/// <item>P1b：渲染设置页（`title` 是标签、`description` 是帮助文本、`x-order` 是排序）</item>
/// </list>
/// </summary>
public sealed class ConfigField
{
    public required string Key { get; init; }

    public ConfigFieldType Type { get; init; } = ConfigFieldType.Unknown;

    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>schema 里声明的默认值。null 表示"没有默认值"（字段可缺省）。</summary>
    public JsonNode? Default { get; init; }

    /// <summary>枚举允许值（原样保留 JSON 值，比较用 DeepEquals）。</summary>
    public JsonArray? Enum { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    /// <summary>`format`：directory / file / textarea（P1b 用来换控件类型）。</summary>
    public string? Format { get; init; }

    /// <summary>`x-order`：设置页里的排序权重，越小越靠前。</summary>
    public int SortOrder { get; init; }

    /// <summary>
    /// `x-group`：设置页里的功能域组名（方案 B，2026-09-30）。
    /// null = 不分组（渲染在无组区）。设置窗口按它把宿主节 11 字段切成功能域子页；
    /// 它只是**展示层元数据**，ConfigStore 校验与 CLI 完全不消费。
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// `x-advanced`：技术参数标记（方案 C5）——渲染为默认折叠的「高级」Expander，
    /// 与用户决策类选项分层。同样是展示层元数据，校验/CLI 不消费。
    /// </summary>
    public bool IsAdvanced { get; init; }

    /// <summary>`x-secret`：标记敏感值。**P1a 只识别不处理**（加密留给 P1b，那时才有 UI）。</summary>
    public bool IsSecret { get; init; }

    /// <summary>数组元素的类型描述（`items`）。只支持基本类型。</summary>
    public ConfigFieldType ItemType { get; init; } = ConfigFieldType.String;

    /// <summary>原始 schema 片段，供 P1b 或诊断使用。</summary>
    public JsonObject? Raw { get; init; }

    /// <summary>可读的类型名，用于报错。</summary>
    public string TypeName => Type switch
    {
        ConfigFieldType.Boolean => "boolean",
        ConfigFieldType.String => "string",
        ConfigFieldType.Integer => "integer",
        ConfigFieldType.Number => "number",
        ConfigFieldType.Array => "array",
        ConfigFieldType.Object => "object",
        _ => "（未声明）",
    };
}

/// <summary>
/// 把 `tool.json` 里的 `config` 对象解析成字段表。
///
/// **刻意只支持 JSON Schema 的子集**（§7 的渲染映射表需要什么就支持什么）：
/// 不支持 `$ref` / `oneOf` / `anyOf` / `allOf` / `patternProperties` / `if-then-else` /
/// 远程 `$schema` / 正则 `pattern`。顺手实现它们会让校验器变成大工程，
/// 而设置页一个都用不上。
///
/// **未知的 `x-` 扩展键原样保留、不报错** —— 这样将来加 `x-secret` 之类的新标记
/// 不需要改校验器，也不会让旧版本把新清单判成非法。
/// </summary>
public sealed class ConfigSchema
{
    public static readonly ConfigSchema Empty = new();

    private readonly Dictionary<string, ConfigField> _byKey = new(StringComparer.Ordinal);

    public IReadOnlyList<ConfigField> Fields { get; private init; } = Array.Empty<ConfigField>();

    public JsonObject? Raw { get; private init; }

    /// <summary>是否至少有字段可言（没有的话不该给这个工具显示设置页）。</summary>
    public bool HasFields => Fields.Count > 0;

    public ConfigField? Field(string key) => _byKey.TryGetValue(key, out var f) ? f : null;

    public static ConfigSchema FromJson(JsonObject? schema)
    {
        if (schema is null || schema["properties"] is not JsonObject props || props.Count == 0)
        {
            return Empty;
        }

        var fields = new List<ConfigField>(props.Count);
        var index = 0;

        foreach (var (key, node) in props)
        {
            if (node is not JsonObject spec)
            {
                // 字段描述必须是对象；不是就跳过（诊断由 ManifestParser 负责，这里不重复报）
                continue;
            }

            var field = ParseField(key, spec, index++);
            fields.Add(field);
        }

        // 按 x-order 排（没声明的按声明顺序排在后面），让设置页与 list 输出稳定
        var ordered = fields
            .OrderBy(f => f.SortOrder < 0 ? int.MaxValue : f.SortOrder)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();

        var result = new ConfigSchema { Fields = ordered, Raw = schema };
        foreach (var f in ordered)
        {
            result._byKey[f.Key] = f;
        }

        return result;
    }

    private static ConfigField ParseField(string key, JsonObject spec, int declarationIndex)
    {
        var sortOrder = declarationIndex;

        // x-order 优先，其次用声明顺序（保证输出顺序稳定）
        if (spec["x-order"] is JsonValue orderNode &&
            orderNode.TryGetValue(out int order))
        {
            sortOrder = order;
        }

        return new ConfigField
        {
            Key = key,
            Type = ParseType(spec["type"]?.GetValue<string>()),
            Title = spec["title"]?.GetValue<string>(),
            Description = spec["description"]?.GetValue<string>(),
            Default = spec["default"]?.DeepClone(),
            Enum = spec["enum"] as JsonArray,
            Minimum = AsDouble(spec["minimum"]),
            Maximum = AsDouble(spec["maximum"]),
            Format = spec["format"]?.GetValue<string>(),
            SortOrder = sortOrder,
            Group = spec["x-group"] is JsonValue gv && gv.TryGetValue(out string? groupName)
                ? groupName
                : null,
            IsAdvanced = spec["x-advanced"] is JsonValue adv && adv.TryGetValue(out bool isAdv) && isAdv,
            IsSecret = spec["x-secret"] is JsonValue sv && sv.TryGetValue(out bool secret) && secret,
            ItemType = spec["items"] is JsonObject items
                ? ParseType(items["type"]?.GetValue<string>())
                : ConfigFieldType.String,
            Raw = (JsonObject)spec.DeepClone(),
        };
    }

    private static ConfigFieldType ParseType(string? type) => type switch
    {
        "boolean" => ConfigFieldType.Boolean,
        "string" => ConfigFieldType.String,
        "integer" => ConfigFieldType.Integer,
        "number" => ConfigFieldType.Number,
        "array" => ConfigFieldType.Array,
        "object" => ConfigFieldType.Object,
        _ => ConfigFieldType.Unknown,
    };

    /// <summary>
    /// 读 schema 里的数值约束。
    /// 走 <see cref="ConfigValues.TryGetNumber"/> 而不是直接 <c>TryGetValue&lt;double&gt;</c>
    /// —— 后者对"底层类型不是 double"的值返回 false，会让约束**静默失效**（详见该方法的注释）。
    /// </summary>
    private static double? AsDouble(JsonNode? node) =>
        ConfigValues.TryGetNumber(node, out var d) ? d : null;
}
