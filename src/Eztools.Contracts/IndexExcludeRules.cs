// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Eztools.Contracts;

/// <summary>
/// 规范化后的排除规则集（W11，设计方案 §4.1/§4.2）。
///
/// <para><b>空集 = 不排除任何目录</b> —— 它与"排除一切"是**相反**的东西，两者必须可区分
/// （selftest 11.4 钉这条）。</para>
///
/// <para><b>为什么要字节预筛</b>：锚定要在**百万级槽位**上跑一趟（`IndexStore` 的升序 FRN 段），
/// 每个槽位都解码成 string 再比较会直接把自举拖慢一个量级。按"UTF-8 字节长度"分组后，
/// 绝大多数槽位**连比较都不需要**（长度不符即跳过），与 <see cref="Eztools.Index"/> 侧
/// `OwnScope` 的做法同款。</para>
/// </summary>
public sealed class IndexExcludeRuleSet
{
    private readonly Dictionary<int, byte[][]> _byByteLength;

    internal IndexExcludeRuleSet(IReadOnlyList<string> rules, Dictionary<int, byte[][]> byByteLength)
    {
        Rules = rules;
        _byByteLength = byByteLength;
    }

    /// <summary>空集（不排除任何目录）。</summary>
    public static IndexExcludeRuleSet Empty { get; } = new([], []);

    /// <summary>规则原文（已 trim / 去重 / 保序）—— 供状态行与配置回显。</summary>
    public IReadOnlyList<string> Rules { get; }

    public int Count => Rules.Count;

    public bool IsEmpty => Rules.Count == 0;

    /// <summary>
    /// 热路径判据：该 **UTF-8 名字**是否命中任一规则（等值，ASCII 大小写折叠）。
    ///
    /// <para>与 <see cref="IndexExcludeRules.MatchName"/> 是**同一语义的两个视图**（一个吃字节、
    /// 一个吃字符）。两者对非 ASCII 名字的处理有已知差异（见 <see cref="IndexExcludeRules.BytesEqualAsciiFold"/>
    /// 的注释）⇒ selftest 用一张名字表**交叉断言两者一致**，防止漂移。</para>
    /// </summary>
    public bool ByteMatches(ReadOnlySpan<byte> name)
    {
        if (!_byByteLength.TryGetValue(name.Length, out var candidates))
        {
            return false;
        }

        // 不用 LINQ：ReadOnlySpan 是 ref struct，不能被 lambda 捕获。
        foreach (var rule in candidates)
        {
            if (IndexExcludeRules.BytesEqualAsciiFold(name, rule))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 排除规则的**解析与匹配**（W11，设计方案 §4.2 / D2 = 只支持目录名，不支持 glob；D7 = 落 Contracts）。
///
/// <para><b>为什么落 <c>Contracts</c> 而不是 <c>Host</c> 或 <c>Index</c></b>：配置侧（Host 校验/回显）
/// 与锚定侧（Index 匹配）**需要同一份语义**；Host 与 Index 都引用 Contracts ⇒ 解析逻辑只有一个出口。
/// 先例：<c>HotkeyCombo.TryParse</c>（仲裁器 / CLI / 设置窗共用一份）。</para>
///
/// <para><b>为什么不做 glob</b>：锚定是"按字节长度预筛 + 一次字节比较"，支持通配就得对**每个候选**
/// 跑匹配器 ⇒ 从 O(1) 退化成百万次匹配。而本场景用户要的恰好是等值语义（"凡是叫这个名字的都排掉"）。</para>
/// </summary>
public static class IndexExcludeRules
{
    /// <summary>规则条数上限（D5 = 32）。上限的意义是兜住"规则越多越可能命中意外目录"。</summary>
    public const int MaxRules = 32;

    /// <summary>规则分隔符（配置面单键承载多条）。</summary>
    public const char Separator = ';';

    /// <summary>
    /// 解析配置串。**超上限 / 含路径分隔符都是显式错误**（绝不静默截断 —— 静默截断 = S2 家族）。
    /// </summary>
    /// <param name="raw">原始配置值（可空；空 = 空集）。</param>
    /// <param name="set">解析结果（失败时为 <see cref="IndexExcludeRuleSet.Empty"/>）。</param>
    /// <param name="error">失败原因（成功时 null）。</param>
    public static bool TryParse(string? raw, out IndexExcludeRuleSet set, out string? error)
    {
        set = IndexExcludeRuleSet.Empty;
        error = null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<string>();

        foreach (var part in (raw ?? "").Split(Separator, StringSplitOptions.TrimEntries))
        {
            var name = part.Trim().Trim('"');
            if (name.Length == 0)
            {
                continue;   // 空项（含结尾分号 / 连续分号）⇒ 跳过，不算错
            }

            if (name.IndexOfAny(['\\', '/']) >= 0)
            {
                error = $"排除规则只能是目录名（不含路径）：{name}";
                return false;
            }

            if (name is "." or "..")
            {
                error = $"排除规则不能是相对目录名：{name}";
                return false;
            }

            // 大小写不敏感去重（NTFS 本身大小写不敏感 ⇒ Node_Modules 与 node_modules 是同一个规则）
            if (seen.Add(name))
            {
                rules.Add(name);
            }
        }

        if (rules.Count > MaxRules)
        {
            error = $"排除规则最多 {MaxRules} 条，当前 {rules.Count} 条（请合并或删减）";
            return false;
        }

        var byLength = new Dictionary<int, List<byte[]>>();
        foreach (var rule in rules)
        {
            var bytes = Encoding.UTF8.GetBytes(rule);
            if (!byLength.TryGetValue(bytes.Length, out var bucket))
            {
                byLength[bytes.Length] = bucket = [];
            }

            bucket.Add(bytes);
        }

        set = new IndexExcludeRuleSet(
            rules,
            byLength.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        return true;
    }

    /// <summary>
    /// 规则原文 → 进程边界传参用的规范串（**分号分隔、去重、保序**）。
    /// Host 侧把配置值经它归一后再交给索引进程 ⇒ 两侧看到的规则集逐字相同。
    /// </summary>
    public static string ToConfigValue(IndexExcludeRuleSet set) => string.Join(Separator, set.Rules);

    /// <summary>
    /// 语义判据（**字符级**，大小写不敏感等值）。这是"规则命中意味着什么"的权威口径；
    /// 热路径用的是 <see cref="IndexExcludeRuleSet.ByteMatches"/>（同一语义的字节视图）。
    ///
    /// <para>★ **不做子串匹配**：`node` 不得命中 `node_modules`（selftest 11.2 是**负向**断言，
    /// 防的就是有人图省事改成 `Contains` —— 那会把用户一大堆无关目录卷进来）。</para>
    /// </summary>
    public static bool MatchName(ReadOnlySpan<char> name, IndexExcludeRuleSet set)
    {
        foreach (var rule in set.Rules)
        {
            if (name.Equals(rule.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 字节等值 + **ASCII 大小写折叠**（与 <c>OwnScope.BytesMatch</c> 逐字同款）。
    ///
    /// <para><b>已知差异（必须知道，不是 bug）</b>：非 ASCII 名字这里要求**逐字节相等**，
    /// 而 <see cref="MatchName"/> 走 .NET 的 OrdinalIgnoreCase（含完整 Unicode 简单折叠）。
    /// 二者只在"非 ASCII 且大小写形态不同"的名字上分叉（如土耳其语 <c>İ</c>）。
    /// NTFS 的大小写表与 .NET 也不完全一致 —— 这是**既有实现**（<c>OwnScope</c>）已经做过一次的取舍，
    /// 本波沿用而不是另起一套。selftest 用交叉表把两者的**已知一致域**钉住。</para>
    /// </summary>
    internal static bool BytesEqualAsciiFold(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i];
            byte y = b[i];
            if (x == y)
            {
                continue;
            }

            if (x is >= (byte)'A' and <= (byte)'Z')
            {
                x += 32;
            }

            if (y is >= (byte)'A' and <= (byte)'Z')
            {
                y += 32;
            }

            if (x != y)
            {
                return false;
            }
        }

        return true;
    }
}
