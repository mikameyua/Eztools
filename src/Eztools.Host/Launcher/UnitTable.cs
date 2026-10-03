// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Eztools.Host.Launcher;

/// <summary>单位类别。**温度单独一路**（仿射变换，不是乘系数）—— 见 <see cref="UnitTable.Convert"/>。</summary>
public enum UnitCategory
{
    Length,
    Mass,
    Data,
    Temperature,
}

/// <summary>
/// 一个单位。<paramref name="Factor"/> = 相对**基准单位**的倍数（长度=米 / 重量=千克 / 数据量=字节）；
/// 温度不用它（仿射）。
/// <para><paramref name="Binary"/>=true 表示 1024 进制（`KiB`/`MiB`/…）—— 显示时必须带制式说明，
/// 否则 `1GB → 953.674MiB` 会被当成 bug（设计方案 §10.8 A 明确点名的"最常被误解的一处"）。</para>
/// </summary>
public sealed record UnitDef(string Key, string Display, UnitCategory Category, double Factor, bool Binary = false);

/// <summary>
/// 一次单位换算请求。<paramref name="To"/>=null 表示**输入里没有触发词** ⇒ 列出该类别常用单位
/// （设计方案 D10=A：`10km` 单独输入时用户十有八九想看别的单位）。
/// </summary>
public sealed record UnitRequest(double Value, UnitDef From, UnitDef? To, string Raw);

/// <summary>
/// 单位表与换算（W7-d，设计方案 §10.8 A）—— **纯函数、零 IO、零 UI**。
///
/// <para><b>解析是"全消费"的</b>：数字 + 单位 + 可选触发词 + 可选单位，**整串必须被吃完**，
/// 否则返回 false（静默）。这条比"尽力匹配前缀"重要得多 —— 少了它，`10km + 5mi` 会被解析成
/// "10km 后面跟了点看不懂的东西"，然后当成"无触发词"去列常用单位（**错的输入给出看似合理的结果**，
/// 比静默更糟）。设计方案 §10.8 A 明确"不做单位混算"，这里就是它的落地。</para>
///
/// <para><b>触发词三态等价</b>（D10）：`to` / `->` / `转`。三者都只在"单位之后"才被识别，
/// 所以 `10m` 里的 `m` 不会被当作词。</para>
/// </summary>
public static class UnitTable
{
    /// <summary>输入长度上限（与 <see cref="CalcExpression.MaxInputLength"/> 同一理由：防超长输入卡 UI）。</summary>
    public const int MaxInputLength = 512;

    /// <summary>长度的基准 = 米；英制系数取国际码定义（精确值，可手算核对）。</summary>
    private static readonly UnitDef[] LengthUnits =
    [
        new("mm", "mm", UnitCategory.Length, 0.001),
        new("cm", "cm", UnitCategory.Length, 0.01),
        new("m", "m", UnitCategory.Length, 1),
        new("km", "km", UnitCategory.Length, 1000),
        new("in", "in", UnitCategory.Length, 0.0254),
        new("ft", "ft", UnitCategory.Length, 0.3048),
        new("yd", "yd", UnitCategory.Length, 0.9144),
        new("mi", "mi", UnitCategory.Length, 1609.344),
    ];

    private static readonly UnitDef[] MassUnits =
    [
        new("mg", "mg", UnitCategory.Mass, 0.000001),
        new("g", "g", UnitCategory.Mass, 0.001),
        new("kg", "kg", UnitCategory.Mass, 1),
        new("t", "t", UnitCategory.Mass, 1000),
        new("oz", "oz", UnitCategory.Mass, 0.028349523125),
        new("lb", "lb", UnitCategory.Mass, 0.45359237),
    ];

    /// <summary>数据量：**两制式并存**（`KB`=1000 / `KiB`=1024），基准 = 字节。</summary>
    private static readonly UnitDef[] DataUnits =
    [
        new("b", "B", UnitCategory.Data, 1),
        new("kb", "KB", UnitCategory.Data, 1000),
        new("mb", "MB", UnitCategory.Data, 1000L * 1000),
        new("gb", "GB", UnitCategory.Data, 1000L * 1000 * 1000),
        new("tb", "TB", UnitCategory.Data, 1000L * 1000 * 1000 * 1000),
        new("kib", "KiB", UnitCategory.Data, 1024, Binary: true),
        new("mib", "MiB", UnitCategory.Data, 1024L * 1024, Binary: true),
        new("gib", "GiB", UnitCategory.Data, 1024L * 1024 * 1024, Binary: true),
        new("tib", "TiB", UnitCategory.Data, 1024L * 1024 * 1024 * 1024, Binary: true),
    ];

    /// <summary>温度：<paramref name="Factor"/> 不参与（仿射）—— 只有 <see cref="Key"/> 有用。</summary>
    private static readonly UnitDef[] TemperatureUnits =
    [
        new("c", "°C", UnitCategory.Temperature, 0),
        new("f", "°F", UnitCategory.Temperature, 0),
        new("k", "K", UnitCategory.Temperature, 0),
    ];

    private static readonly Dictionary<string, UnitDef> ByKey =
        LengthUnits.Concat(MassUnits).Concat(DataUnits).Concat(TemperatureUnits)
            .ToDictionary(u => u.Key, StringComparer.Ordinal);

    /// <summary>全部单位（selftest 遍历用）。</summary>
    public static IReadOnlyList<UnitDef> All { get; } = [.. LengthUnits, .. MassUnits, .. DataUnits, .. TemperatureUnits];

    /// <summary>
    /// 无触发词时列出的常用单位（设计方案 D10=A）。★ **不含源单位** ——
    /// 源单位那一行是恒等变换（`10 km = 10 km`），对用户零信息，纯噪声。
    /// </summary>
    private static readonly Dictionary<UnitCategory, string[]> CommonKeys = new()
    {
        [UnitCategory.Length] = ["km", "mi", "m", "ft", "in"],
        [UnitCategory.Mass] = ["kg", "lb", "oz", "g"],
        [UnitCategory.Data] = ["mb", "mib", "gb", "gib"],
        [UnitCategory.Temperature] = ["c", "f", "k"],
    };

    /// <summary>该类别里"源单位之外"的常用单位（顺序即 Score 递减顺序）。</summary>
    public static IReadOnlyList<UnitDef> CommonOf(UnitCategory category, string excludeKey)
    {
        var keys = CommonKeys[category];
        var list = new List<UnitDef>(keys.Length);
        foreach (var key in keys)
        {
            if (!string.Equals(key, excludeKey, StringComparison.Ordinal))
            {
                list.Add(ByKey[key]);
            }
        }

        return list;
    }

    /// <summary>
    /// 解析 `<数值><单位>[ <触发词> <单位>]`。**整串必须被吃完**，否则 false。
    /// </summary>
    public static bool TryParse(string? text, out UnitRequest request)
    {
        request = null!;
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s.Length > MaxInputLength)
        {
            return false;
        }

        var i = 0;

        // ① 数值（可带符号与小数；**不支持科学记数** —— 单位换算里它是噪声，见 §10.8 A 的"不做"清单）
        var start = i;
        if (i < s.Length && s[i] is '+' or '-')
        {
            i++;
        }

        var digits = 0;
        while (i < s.Length && s[i] is >= '0' and <= '9')
        {
            i++;
            digits++;
        }

        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && s[i] is >= '0' and <= '9')
            {
                i++;
                digits++;
            }
        }

        if (digits == 0
            || !double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        // ② 源单位（紧跟数字，空格可选 ⇒ `10 m` 与 `10m` 等价）
        SkipSpaces(s, ref i);
        var unit1 = ReadUnit(s, ref i);
        if (unit1 is null)
        {
            return false;
        }

        // ③ 可选触发词 + 目标单位
        SkipSpaces(s, ref i);
        var triggerAt = i;
        if (TryReadTrigger(s, ref i))
        {
            SkipSpaces(s, ref i);
            var unit2 = ReadUnit(s, ref i);
            if (unit2 is null)
            {
                return false;
            }

            SkipSpaces(s, ref i);
            if (i != s.Length)
            {
                return false;   // 目标单位之后还有东西 ⇒ 不是本 provider 的输入
            }

            return Finish(value, unit1, unit2, s, out request);
        }

        // ④ 无触发词：**必须整串吃完**（`10km + 5mi` 在这里被挡掉 —— 不做单位混算）
        i = triggerAt;
        SkipSpaces(s, ref i);
        if (i != s.Length)
        {
            return false;
        }

        return Finish(value, unit1, null, s, out request);
    }

    /// <summary>换算。<b>温度走仿射路径</b>（不是乘系数）—— 这是本类里唯一的分支。</summary>
    public static double Convert(UnitDef from, UnitDef to, double value)
    {
        if (from.Category == UnitCategory.Temperature || to.Category == UnitCategory.Temperature)
        {
            if (from.Category != to.Category)
            {
                return double.NaN;   // 跨类别（如 `10km to C`）由调用方挡在解析层，这里防御
            }

            return FromCelsius(ToCelsius(from.Key, value), to.Key);
        }

        return value * from.Factor / to.Factor;
    }

    private static double ToCelsius(string key, double v) => key switch
    {
        "c" => v,
        "f" => (v - 32) * 5 / 9,
        _ => v - 273.15,          // k
    };

    private static double FromCelsius(double c, string key) => key switch
    {
        "c" => c,
        "f" => (c * 9 / 5) + 32,
        _ => c + 273.15,          // k
    };

    private static bool Finish(double value, UnitDef from, UnitDef? to, string raw, out UnitRequest request)
    {
        // 跨类别换算（`10km to kg`）不是"算不出来"，是**用户没这个意思** ⇒ 静默（返回 false）
        if (to is not null && to.Category != from.Category)
        {
            request = null!;
            return false;
        }

        request = new UnitRequest(value, from, to, raw);
        return true;
    }

    /// <summary>
    /// 读一段单位记号（ASCII 字母 + `°`/`℃`/`℉`）。
    ///
    /// ★ **为什么不用 <c>char.IsLetter</c>**：`转` 与 `℃` 都是"字母"类字符，
    /// 用 <c>IsLetter</c> 会把它们吃进单位记号里 —— `10km转mi` 的单位记号会变成 `km转mi`。
    /// 显式列字符集比"用通用判定再打补丁"更难写错。
    /// </summary>
    private static UnitDef? ReadUnit(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && IsUnitChar(s[i]))
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        var run = s[start..i];
        if (TryFind(run, out var whole))
        {
            return whole;
        }

        // 触发词会与单位记号连写（`kmto`）：退化为"最长可识别前缀 + 余下交给触发词判定"
        for (var len = run.Length - 1; len >= 1; len--)
        {
            if (TryFind(run[..len], out var prefix))
            {
                i = start + len;
                return prefix;
            }
        }

        return null;
    }

    private static bool IsUnitChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '°' or '℃' or '℉';

    /// <summary>单位记号归一：大小写不敏感（`KM`/`km` 等价）+ 显式接受 `°`/`℃`/`℉`。</summary>
    private static bool TryFind(string token, out UnitDef unit)
    {
        var key = token.ToLowerInvariant().Replace("℃", "c").Replace("℉", "f").Replace("°", "");
        if (key.Length > 0 && ByKey.TryGetValue(key, out var found))
        {
            unit = found;
            return true;
        }

        unit = null!;
        return false;
    }

    private static bool TryReadTrigger(string s, ref int i)
    {
        if (i < s.Length && s[i] == '-' && i + 1 < s.Length && s[i + 1] == '>')
        {
            i += 2;
            return true;
        }

        if (i < s.Length && s[i] == '转')
        {
            i++;
            return true;
        }

        if (i + 1 < s.Length && (s[i] is 't' or 'T') && (s[i + 1] is 'o' or 'O'))
        {
            i += 2;
            return true;
        }

        return false;
    }

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
    }
}
