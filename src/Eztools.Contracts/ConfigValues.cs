using System.Globalization;
using System.Text.Json.Nodes;

namespace Eztools.Contracts;

/// <summary>设置一个配置项的结果。</summary>
public sealed record ConfigSetResult(bool Ok, JsonNode? Value, string? Error)
{
    public static ConfigSetResult Success(JsonNode value) => new(true, value, null);
    public static ConfigSetResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 配置值的类型强制、校验与默认值合并。
///
/// 这一层是"schema 驱动"的第一次落地：命令行拿到的是**字符串**，
/// 而工具最终要拿到的是**带类型的 JSON 值**。P1b 的设置页会复用同一套逻辑
/// （文本框 → 校验 → 落盘），所以它放在契约层而不是 CLI 里。
/// </summary>
public static class ConfigValues
{
    /// <summary>
    /// 把命令行给的一串文本按字段类型转成 JSON 值。
    ///
    /// 约定（要在 `ezt config set --help` 与文档里写明，否则用户猜不到）：
    /// <list type="bullet">
    /// <item>以 <c>{</c> 或 <c>[</c> 开头 → 按 **JSON 原文**解析（适合 object / 复杂 array）</item>
    /// <item><c>array</c> → 按**逗号**切分，逐项按 <c>items.type</c> 转（值里含逗号时请改用 JSON 写法）</item>
    /// <item><c>boolean</c> → <c>true/false</c>、<c>1/0</c>、<c>yes/no</c>、<c>on/off</c> 都认</item>
    /// </list>
    /// </summary>
    public static ConfigSetResult Coerce(ConfigField field, string raw)
    {
        var text = raw ?? string.Empty;

        // 显式 JSON 写法优先：能表达 object 与含逗号的数组
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (Exception ex)
            {
                return ConfigSetResult.Fail($"不是合法的 JSON：{ex.Message}");
            }

            var typeError = Validate(field, parsed);
            return typeError is null ? ConfigSetResult.Success(parsed!) : ConfigSetResult.Fail(typeError);
        }

        switch (field.Type)
        {
            case ConfigFieldType.Boolean:
            {
                var v = text.Trim().ToLowerInvariant();
                bool ok = v switch
                {
                    "true" or "1" or "yes" or "on" or "y" => true,
                    "false" or "0" or "no" or "off" or "n" => false,
                    _ => false,
                };
                if (v is not ("true" or "1" or "yes" or "on" or "y" or "false" or "0" or "no" or "off" or "n"))
                {
                    return ConfigSetResult.Fail($"'{text}' 不是布尔值（可用 true/false、1/0、yes/no、on/off）");
                }

                return Finish(field, JsonValue.Create(ok));
            }

            case ConfigFieldType.Integer:
            {
                if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    return ConfigSetResult.Fail($"'{text}' 不是整数");
                }

                return Finish(field, JsonValue.Create(n));
            }

            case ConfigFieldType.Number:
            {
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return ConfigSetResult.Fail($"'{text}' 不是数字");
                }

                return Finish(field, JsonValue.Create(d));
            }

            case ConfigFieldType.Array:
            {
                var arr = new JsonArray();
                foreach (var part in text.Split(','))
                {
                    var item = part.Trim();
                    JsonNode node;
                    switch (field.ItemType)
                    {
                        case ConfigFieldType.Integer when long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var li):
                            node = JsonValue.Create(li);
                            break;
                        case ConfigFieldType.Number when double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var di):
                            node = JsonValue.Create(di);
                            break;
                        case ConfigFieldType.Boolean when item.Equals("true", StringComparison.OrdinalIgnoreCase) || item == "1":
                            node = JsonValue.Create(true);
                            break;
                        case ConfigFieldType.Boolean when item.Equals("false", StringComparison.OrdinalIgnoreCase) || item == "0":
                            node = JsonValue.Create(false);
                            break;
                        default:
                            node = JsonValue.Create(item);
                            break;
                    }

                    arr.Add(node);
                }

                return Finish(field, arr);
            }

            case ConfigFieldType.Object:
                return ConfigSetResult.Fail("object 类型必须用 JSON 写法，例如：'{\"a\":1}'");

            default:
                // 没声明 type 的字段按字符串收 —— 宽松，但不至于把值弄丢
                return Finish(field, JsonValue.Create(text));
        }
    }

    private static ConfigSetResult Finish(ConfigField field, JsonNode value)
    {
        var error = Validate(field, value);
        return error is null ? ConfigSetResult.Success(value) : ConfigSetResult.Fail(error);
    }

    /// <summary>
    /// 校验一个值是否符合字段约束。返回 null 表示通过，否则是**可读的**原因。
    /// 文案要能被直接贴给用户看，所以带上字段名与允许范围。
    /// </summary>
    public static string? Validate(ConfigField field, JsonNode? value)
    {
        if (value is null)
        {
            return null; // null 由调用方解释为"删除该键"
        }

        switch (field.Type)
        {
            case ConfigFieldType.Boolean when value is not JsonValue bv || !bv.TryGetValue(out bool _):
                return $"{field.Key} 需要 boolean，收到 {Describe(value)}";

            case ConfigFieldType.String or ConfigFieldType.Unknown
                when value is not JsonValue sv || !sv.TryGetValue(out string? _):
                return $"{field.Key} 需要 string，收到 {Describe(value)}";

            case ConfigFieldType.Integer:
            {
                if (value is not JsonValue iv || !iv.TryGetValue(out long _))
                {
                    return $"{field.Key} 需要 integer，收到 {Describe(value)}";
                }

                break;
            }

            case ConfigFieldType.Number:
            {
                if (value is not JsonValue nv || !nv.TryGetValue(out double _))
                {
                    return $"{field.Key} 需要 number，收到 {Describe(value)}";
                }

                break;
            }

            case ConfigFieldType.Array when value is not JsonArray:
                return $"{field.Key} 需要 array，收到 {Describe(value)}";

            case ConfigFieldType.Object when value is not JsonObject:
                return $"{field.Key} 需要 object，收到 {Describe(value)}";
        }

        // enum
        if (field.Enum is { Count: > 0 })
        {
            var hit = field.Enum.Any(candidate => SameValue(candidate, value));
            if (!hit)
            {
                var allowed = string.Join(" / ", field.Enum.Select(c => c?.ToJsonString() ?? "null"));
                return $"{field.Key} 的值 {value.ToJsonString()} 不在允许范围内（可选：{allowed}）";
            }
        }

        // minimum / maximum 只对数值有意义
        if ((field.Minimum is not null || field.Maximum is not null) &&
            TryGetNumber(value, out var num))
        {
            if (field.Minimum is not null && num < field.Minimum)
            {
                return $"{field.Key} 的值 {num} 小于下限 {field.Minimum}";
            }

            if (field.Maximum is not null && num > field.Maximum)
            {
                return $"{field.Key} 的值 {num} 超过上限 {field.Maximum}";
            }
        }

        return null;
    }

    /// <summary>
    /// 取出一个 JSON 数值。
    ///
    /// 🔴 **不能只写 `TryGetValue&lt;double&gt;`** —— 这是实测踩到的坑：
    /// <c>JsonValue.Create(99999L)</c> 背后是 <c>long</c>，而 <c>JsonValue.TryGetValue&lt;double&gt;</c>
    /// **只在底层类型正好是 double 时才返回 true，不做数值转换**。于是
    /// "设置了超出 maximum 的值"这条校验**静默失效**：命令成功、文件写进去了、没有任何报错。
    ///
    /// 更阴的地方在于**两种来源行为不一致**：<c>JsonNode.Parse</c> 出来的数字背后是 <c>JsonElement</c>
    /// （转换是通的），而程序自己 <c>JsonValue.Create</c> 出来的不是。所以 schema 里写的
    /// <c>maximum: 1024</c> 能正确读出，命令行传进来的 <c>99999</c> 却读不出。
    ///
    /// ⇒ 一律用这个 helper，按 double → long → decimal 依次尝试（double 优先，
    ///   免得 <c>1.5</c> 被 long 截成 <c>1</c>）。
    /// </summary>
    public static bool TryGetNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue(out double d)) { value = d; return true; }
        if (v.TryGetValue(out long l)) { value = l; return true; }
        if (v.TryGetValue(out decimal m)) { value = (double)m; return true; }
        return false;
    }

    /// <summary>
    /// 计算**有效配置** = 默认值 ⊕ 已存值（已存值优先）。
    ///
    /// 注入给工具进程的就是这个结果。两条刻意的取舍：
    /// <list type="bullet">
    /// <item><b>既没默认值、也没设过的字段不进结果</b> —— 让工具侧 `.get()` 拿到 None，
    ///   而不是一个来路不明的 null</item>
    /// <item><b>不在当前 schema 里的已存键也不进结果</b> —— 注入面严格等于 schema，
    ///   这样"schema 改了"不会让工具收到它不认识的字段。
    ///   但文件里**保留**那些键（不销毁用户数据），`ezt config list` 会提示它们的存在</item>
    /// </list>
    /// </summary>
    public static JsonObject Merge(ConfigSchema? schema, JsonObject? saved)
    {
        var result = new JsonObject();
        if (schema is null)
        {
            return result;
        }

        foreach (var field in schema.Fields)
        {
            if (saved is not null &&
                saved.TryGetPropertyValue(field.Key, out var stored) && stored is not null)
            {
                result[field.Key] = stored.DeepClone();
            }
            else if (field.Default is not null)
            {
                result[field.Key] = field.Default.DeepClone();
            }
        }

        return result;
    }

    /// <summary>文件里有、但当前 schema 没声明的键（用于提示，不用于注入）。</summary>
    public static IReadOnlyList<string> OrphanKeys(ConfigSchema? schema, JsonObject? saved)
    {
        if (saved is null || saved.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (schema is null)
        {
            return saved.Select(kv => kv.Key).ToList();
        }

        return saved
            .Where(kv => schema.Field(kv.Key) is null)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 值等价判断。
    /// **不能直接用 <c>JsonNode.DeepEquals</c>**：schema 里写 <c>3</c>、命令行转出来是 <c>3L</c>，
    /// 两者数值相同但装箱类型不同，DeepEquals 可能判否。这里先按规范文本比较，必要时再退回 DeepEquals。
    /// </summary>
    public static bool SameValue(JsonNode? a, JsonNode? b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    private static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonValue v:
                if (v.TryGetValue(out bool b)) return "bool:" + (b ? "1" : "0");
                if (v.TryGetValue(out string? s)) return "str:" + s;
                if (TryGetNumber(v, out var n)) return "num:" + n.ToString("R", CultureInfo.InvariantCulture);
                return "json:" + v.ToJsonString();
            default:
                return "json:" + node.ToJsonString();
        }
    }

    private static string Describe(JsonNode value) => value.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
        System.Text.Json.JsonValueKind.String => "string",
        System.Text.Json.JsonValueKind.Number => "number",
        System.Text.Json.JsonValueKind.Array => "array",
        System.Text.Json.JsonValueKind.Object => "object",
        System.Text.Json.JsonValueKind.Null => "null",
        _ => "未知类型",
    };
}
