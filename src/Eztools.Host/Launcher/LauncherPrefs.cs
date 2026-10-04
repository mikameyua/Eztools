// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Host.Config;

namespace Eztools.Host.Launcher;

/// <summary>
/// 启动器配置（W7-b，设计方案 §5 解析规格表 / §10.10）。
///
/// <para><b>为什么解析是纯函数 + 白名单</b>：这个键的值直接决定"哪些 provider 参与"，写错的后果是
/// <b>静默少一个结果来源</b>（用户以为没装那个应用）。所以未知值**必须报错**，而不是"忽略认不出的那项"
/// （静默失败登记册 S2 家族）。<see cref="LauncherProviderRegistry.KnownIds"/> 是白名单的唯一出处。</para>
///
/// <para><b>回落语义</b>：解析失败 ⇒ 返回<a>默认集合</a> + 一条可读错误；错误文案由调用方决定出口
/// （设置窗口保存时拒绝 / 窗口状态行告警一次，见设计方案 §5）。</para>
/// </summary>
public sealed record LauncherPrefs(IReadOnlyList<string> Providers, bool UsageEnabled = true)
{
    /// <summary>默认启用集合 = **全部已实现的 provider**（与白名单同源，杜绝两处漂移）。</summary>
    public static IReadOnlyList<string> DefaultProviders => LauncherProviderRegistry.DefaultEnabled;

    /// <summary>该 provider 是否启用（大小写不敏感）。</summary>
    public bool Includes(string providerId) =>
        Providers.Contains(providerId, StringComparer.OrdinalIgnoreCase);

    /// <summary>默认配置（键缺失时的形态）。</summary>
    public static LauncherPrefs Default { get; } = new(DefaultProviders.ToArray());

    /// <summary>
    /// 配置**有效值**摘要（W8·B2）。托盘在窗口创建时记一份，唤出时比对 —— 变了就重建窗口
    /// （旧窗口的 providers / alias / usage 冻结在创建那一刻，这是"改了配置不生效"的根因）。
    ///
    /// <para>只含**决定窗口行为**的三项；此串不落盘、不进任何协议，格式可自由更改。</para>
    ///
    /// <para><b>为什么放在这一层而不是托盘私有</b>：它必须有机器断言 ——
    /// <b>漏掉一个键 = 那一项配置静默不生效</b>，而"静默不生效"正是 B2 的原始症状。</para>
    /// </summary>
    public static string Fingerprint(LauncherPrefs prefs, LauncherAliases aliases)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(aliases);

        return string.Join('\u001f', [
            string.Join(',', prefs.Providers),
            prefs.UsageEnabled ? "1" : "0",
            string.Join(';', aliases.Entries.Select(e => $"{e.Alias}={e.Target}")),
        ]);
    }

    /// <summary>
    /// **搜索窗装配指纹**（W10-a）：<see cref="Fingerprint"/> 之外再纳入"来源开关"
    /// <paramref name="clipEnabled"/>（<c>clip.enabled</c>）—— 它决定 clip provider 装不装进集合。
    ///
    /// <para><b>为什么必须进来</b>：<see cref="Fingerprint"/> 的注释写着"漏掉一个键 = 那一项配置
    /// 静默不生效"（B2 的原始症状）。<c>clip.enabled</c> 是**装配输入**的一份子：它变了而窗口不重建，
    /// 用户看到的将是"关了剪贴板来源，结果里却还有剪贴板条目"（或反之）—— 与 B2 同族，
    /// 且同样**不报错**。</para>
    /// </summary>
    public static string AssemblyFingerprint(LauncherPrefs prefs, LauncherAliases aliases, bool clipEnabled)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(aliases);
        return Fingerprint(prefs, aliases) + "\u001fclip=" + (clipEnabled ? "1" : "0");
    }

    /// <summary>
    /// 解析配置值（纯函数，selftest 直测；规则即设计方案 §5 的规格表逐行）。
    /// <paramref name="raw"/> 为 <c>null</c> = 键缺失 ⇒ 默认集合、无错误。
    /// </summary>
    public static (LauncherPrefs Out, string? Error) Parse(string? raw)
    {
        if (raw is null)
        {
            return (Default, null);
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return (Default, "至少启用一个 provider（launcher.providers 为空）");
        }

        var list = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var id = part.Trim().ToLowerInvariant();
            if (!LauncherProviderRegistry.IsKnown(id))
            {
                return (Default, $"未知 provider \"{part.Trim()}\"（已知：{LauncherProviderRegistry.KnownListText()}）");
            }

            if (!list.Contains(id, StringComparer.Ordinal))
            {
                list.Add(id);   // 去重保留首次出现位置（顺序即段序优先级）
            }
        }

        return (new LauncherPrefs(list), null);
    }

    /// <summary>
    /// 解析 <c>launcher.usage</c>（W7-e，纯函数）：键缺失 ⇒ true；显式布尔 ⇒ 原值；
    /// **类型不是布尔 ⇒ 报错**（与 providers 的 ParseNode 同一条纪律 —— 不能拿"解析不出"当 false）。
    /// </summary>
    public static (bool Enabled, string? Error) ParseUsage(JsonNode? node)
    {
        if (node is null)
        {
            return (true, null);
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var enabled)
            ? (enabled, null)
            : (true, "launcher.usage 类型应为布尔（true/false）");
    }

    /// <summary>
    /// 从 JSON 节点解析（**必须与"键缺失"区分**）：键存在但类型不是字符串 ⇒ 报错。
    /// 少了这一层，手改配置写成 <c>"launcher.providers": []</c> 会被当成"没配"静默走默认
    /// （设计方案 §11.6 G-2；<c>TryGetString</c> 对两种情形都返回 null，不能拿它判）。
    /// </summary>
    public static (LauncherPrefs Out, string? Error) ParseNode(JsonNode? node)
    {
        if (node is null)
        {
            return (Default, null);
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            return Parse(raw);
        }

        return (Default, "launcher.providers 类型应为字符串（分号/逗号分隔的 provider 名）");
    }

    /// <summary>
    /// 从配置中心读（<c>desktop</c> 保留节的 <c>launcher.providers</c> + <c>launcher.usage</c>）。
    /// 两键各自解析；错误只报**第一条**（状态行一次告警只说一件事），回落语义各自独立
    /// （providers ⇒ 默认集合；usage ⇒ true）。
    /// </summary>
    public static (LauncherPrefs Out, string? Error) FromConfig(ConfigStore configs)
    {
        var effective = configs.Effective(HostSettingsSchema.SectionId, HostSettingsSchema.SchemaJson());
        var (prefs, providersError) = ParseNode(effective[HostSettingsSchema.KeyLauncherProviders]);
        var (usageEnabled, usageError) = ParseUsage(effective[HostSettingsSchema.KeyLauncherUsage]);
        var error = providersError ?? usageError;
        if (usageError is not null && providersError is null)
        {
            prefs = prefs with { UsageEnabled = true };   // 回落语义显式化
        }
        else
        {
            prefs = prefs with { UsageEnabled = usageEnabled };
        }

        return (prefs, error);
    }
}

/// <summary>
/// 启动器别名表（W7-e，设计方案 §10.6 C：别名命中 ⇒ 加 300 分）。
///
/// <para><b>配置格式（<c>launcher.alias</c>，字符串）</b>：<c>别名=目标;别名2=目标2</c>
/// （<c>;</c> 或换行分隔条目，<c>=</c> 分隔别名与目标，两侧空白忽略）。**目标 = 应用的标题或
/// 目标全路径**（不区分大小写全等）。格式写错 ⇒ 报错（与 providers 白名单同一条"未知值报错不静默"
/// 纪律），回落 = 空表（等于没配别名）。</para>
///
/// <para><b>目标匹配是"子串包含"（W7-e 落地改判，原稿全等）</b>：目标的自然写法是俗称
/// （<c>leigod</c>），而条目是文件名（<c>leigod.exe</c>）—— 全等实测必失配。误伤面靠
/// "目标尽量写长一点"约束（手工 curated 小清单，最坏 = 多一条加成结果）。</para>
/// </summary>
public sealed record LauncherAliases(IReadOnlyList<(string Alias, string Target)> Entries)
{
    public static readonly LauncherAliases Empty = new(Array.Empty<(string, string)>());

    public static (LauncherAliases Out, string? Error) Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (Empty, null);
        }

        var entries = new List<(string, string)>();
        foreach (var part in raw.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            var eq = item.IndexOf('=');
            if (eq <= 0 || eq == item.Length - 1)
            {
                return (Empty, $"launcher.alias 条目格式应为「别名=目标」：\"{item}\"");
            }

            entries.Add((item[..eq].Trim(), item[(eq + 1)..].Trim()));
        }

        return (new LauncherAliases(entries), null);
    }

    /// <summary>从 JSON 节点解析（类型不是字符串 ⇒ 报错，同 <see cref="LauncherPrefs.ParseNode"/> 纪律）。</summary>
    public static (LauncherAliases Out, string? Error) ParseNode(JsonNode? node)
    {
        if (node is null)
        {
            return (Empty, null);
        }

        return node is JsonValue value && value.TryGetValue<string>(out var raw)
            ? Parse(raw)
            : (Empty, "launcher.alias 类型应为字符串（别名=目标，分号分隔）");
    }

    public static (LauncherAliases Out, string? Error) FromConfig(ConfigStore configs)
    {
        var effective = configs.Effective(HostSettingsSchema.SectionId, HostSettingsSchema.SchemaJson());
        return ParseNode(effective[HostSettingsSchema.KeyLauncherAlias]);
    }

    /// <summary>
    /// <summary>
    /// 取能命中某应用（标题 + 全路径一起看）的别名清单。没配 ⇒ 空数组。
    ///
    /// <para><b>匹配语义（W7-e 落地改判：全等 → 子串）</b>：目标与应用标题/路径做**不区分大小写的
    /// 子串包含**（<c>title.Contains(target) || path.Contains(target)</c>）。改判理由：别名目标的
    /// 自然写法是"俗称"（如 <c>lei=leigod</c>），而系统条目是文件名（<c>leigod.exe</c>）——
    /// 全等要求用户逐字抄整串，门槛太高且实测必失配（雷声加速器/记事本两条全没命中）。
    /// 误伤面：目标是用户手工 curated 的小清单，且命中只是"多一条结果 + 排序加成"，
    /// 最坏情况是别名同时加成两个应用 —— 文档统一口径"**目标尽量写长一点**"。</para>
    /// </summary>
    public IReadOnlyList<string> AliasesOf(string title, string targetPath) =>
        Entries.Where(e =>
            {
                var t = e.Target;
                var inTitle = title is not null && title.Contains(t, StringComparison.OrdinalIgnoreCase);
                var inPath = targetPath is not null && targetPath.Contains(t, StringComparison.OrdinalIgnoreCase);
                return inTitle || inPath;
            })
               .Select(e => e.Alias)
               .ToArray();
}
