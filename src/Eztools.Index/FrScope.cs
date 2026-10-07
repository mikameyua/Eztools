// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Eztools.Contracts;

namespace Eztools.Index;

/// <summary>
/// FRN 子树作用域（W11，设计方案 §5.1）—— <b>同一类型两种用法</b>：
/// <list type="bullet">
/// <item><b>排除</b>（<see cref="Role.Exclude"/>，W11-a）：按**目录名规则**锚定，命中目录的整棵子树被摘除；</item>
/// <item><b>限定</b>（<see cref="Role.Include"/>，W11-b）：按 `pathFilter` 的**路径**锚定，只留该子树。</item>
/// </list>
///
/// <para><b>★ 两个实例两个语义，禁止混用同一个实例</b>：排除是"命中的踢出去"、限定是"没命中的踢出去"，
/// 方向相反。混用会让结果在"全有"与"全无"之间跳。</para>
///
/// <para>算法与 <see cref="OwnScope"/> 同源（锚定 + **升序前向传播** + USN 增量 `Extend`），
/// 差别只在锚定判据：`OwnScope` 认"一条完整路径"，本类认"名字命中规则的目录"。</para>
///
/// <para><b>★ 未锚定（<see cref="Anchored"/>=false）时 <see cref="Contains"/> 恒 false</b>：
/// 不排除（也不限定）任何条目，并把原因留在 <see cref="Reason"/> —— 与 `OwnScope` 同一条纪律：
/// **拿不准就什么都不做，并且说得出为什么**（绝不"猜一个"；也绝不静默）。</para>
/// </summary>
public sealed class FrScope
{
    /// <summary>前向传播的最大轮数（正常第 1 轮即收敛；上界只防病态输入死循环）。</summary>
    public const int MaxSweepRounds = 8;

    /// <summary>单次锚定的候选上限（防御：病态规则集把整卷一半目录卷进来时，不去做无意义的传播）。
    /// 超限 ⇒ 不锚定并给原因（**不猜**）。128 万远超任何合理的排除规模。</summary>
    public const int MaxAnchors = 1_000_000;

    private readonly HashSet<ulong> _frns = [];

    private FrScope(string role)
    {
        Role = role;
    }

    /// <summary>作用域角色（文案用）："排除" / "限定"。**只影响措辞，不影响判据**。</summary>
    public string Role { get; }

    /// <summary>是否已锚定。false ⇒ <see cref="Contains"/> 恒 false（什么都不做）。</summary>
    public bool Anchored { get; private set; }

    /// <summary>人读原因。未锚定也要说得出为什么（不猜、不静默）。</summary>
    public string Reason { get; private set; } = "未标记";

    /// <summary>作用域内的 FRN 个数。</summary>
    public int Count => _frns.Count;

    /// <summary>作用域内的 FRN（排空用；**顺序不保证**）。</summary>
    public IReadOnlyCollection<ulong> Frns => _frns;

    /// <summary>由 USN 增量补标的条数（观测面：证明增量期维护真的在跑）。</summary>
    public long ExtendedByUsn { get; private set; }

    /// <summary>查询期判据：该 FRN 是否属于本作用域（**未锚定恒 false**）。</summary>
    public bool Contains(ulong frn) => Anchored && _frns.Contains(frn);

    /// <summary>未锚定的作用域（什么都不排除/限定），带上原因。</summary>
    public static FrScope NotAnchored(string role, string reason) =>
        new(role) { Anchored = false, Reason = reason };

    /// <summary>
    /// **已锚定的空作用域**（W11-b 专用语义）：`Anchored=true` 且不含任何 FRN ⇒
    /// `Contains` 恒 false。**只用于"包含"方向** —— 表示"限定生效，但本卷不在限定范围内，
    /// 整卷不产出结果"（W11 §4.5 跨卷语义：限定 `D:\...` ⇒ C: 等其他卷一条都不出，
    /// 否则"限定"名存实亡）。**禁止**把它当排除作用域用（排除方向 Anchored=true+空 =
    /// "什么都不排除"，语义相反 —— 混用即事故）。
    /// </summary>
    public static FrScope EmptyAnchored(string role, string reason) =>
        new(role) { Anchored = true, Reason = reason };

    /// <summary>
    /// 按**目录名规则**锚定（W11-a）：单趟扫过 store，命中规则的目录连同其整棵子树纳入作用域。
    /// </summary>
    /// <param name="store">已建好的索引（第 1 趟的输入）。</param>
    /// <param name="rules">规则集（空集 ⇒ 未锚定，原因写明"未配置"）。</param>
    /// <param name="role">角色文案（"排除" / "限定"）。</param>
    public static FrScope Mark(IndexStore store, IndexExcludeRuleSet rules, string role)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.IsEmpty)
        {
            return NotAnchored(role, "未配置规则 —— 本次不排除任何条目");
        }

        var scope = new FrScope(role);
        var candidates = scope.CollectNameMatches(store, rules);
        if (candidates.Count == 0)
        {
            // ★ 与"配置为空"分开说：这是"配了规则但索引里一条都没命中"（索引可能还没建好 /
            //   规则名写错）。两者在状态行上是不同的话（§4.4 的 Reason 纪律）。
            return NotAnchored(role, $"规则未匹配到任何目录（{string.Join(",", rules.Rules)}）");
        }

        int dirs = scope.KeepDirectoriesOnly(store, candidates);
        if (dirs == 0)
        {
            return NotAnchored(role, $"命中的名字都不是目录（{string.Join(",", rules.Rules)}）");
        }

        var propagated = scope.SweepDescendants(store);
        scope.Anchored = true;
        scope.Reason =
            $"已锚定 {dirs} 个目录，作用域 {scope._frns.Count} 条（其中 {propagated} 条由子树传播补齐）";
        return scope;
    }

    /// <summary>
    /// 按**完整路径**锚定（W11-b，`pathFilter`）：从 store 里找到该目录自身的 FRN，
    /// 连同整棵子树纳入**包含**作用域。算法与 <see cref="OwnScope.Mark"/> 同源
    /// （字节预筛找叶子 + 逐级向上核父链 —— 路径锚定**天然核链**：每一级都对上才算找到），
    /// 内部实现复用 <see cref="OwnScope"/> 的 internal 静态助手（只共享算法，不共享语义）。
    /// 任何一步对不上 ⇒ 未锚定（附原因），绝不"尽力猜一个"（D8 fail-open 的上游）。
    /// </summary>
    /// <remarks>
    /// **已知边界（如实登记）**：<paramref name="pathFilter"/> 是**卷根**（如 <c>D:\</c>）时
    /// 返回未锚定 —— "限定整卷"没有信息量（卷根本来就全可见），且全卷 FRN 灌进集合的代价
    /// 与收益不成比例。调用方（<see cref="SearchService.ApplyPathFilter"/>）按 D8 处理：
    /// 闸不生效 + 原因可见。
    /// </remarks>
    public static FrScope MarkByPath(IndexStore store, string volume, string pathFilter)
    {
        ArgumentNullException.ThrowIfNull(store);

        var volRoot = OwnScope.VolumeRootOf(volume);
        if (volRoot is null)
        {
            return NotAnchored("限定", $"卷名无法解析（{volume}）");
        }

        if (!OwnScope.TryRelativeComponents(volRoot, pathFilter, out var components))
        {
            // 两种可能：不在本卷 / 就是卷根 —— TryRelativeComponents 不区分，这里给出对两种都成立的措辞
            return NotAnchored("限定",
                $"pathFilter 不是本卷下的子目录路径（{pathFilter}；卷根本身不支持限定）");
        }

        if (components.Length > OwnScope.MaxChainDepth)
        {
            return NotAnchored("限定", $"pathFilter 层级过深（{components.Length} > {OwnScope.MaxChainDepth}）");
        }

        var scope = new FrScope("限定");
        var anchor = OwnScope.FindAnchor(store, components);
        if (anchor is null)
        {
            // ★ M5 的判据来源：磁盘存在但索引里没有（或写错）⇒ 限定不生效，原因说得出
            return NotAnchored("限定", $"索引中找不到该目录（{pathFilter}）—— 限定不生效，本次不隐藏任何结果");
        }

        scope._frns.Add(anchor.Value);
        var propagated = scope.SweepDescendants(store);
        scope.Anchored = true;
        scope.Reason =
            $"已锚定 {pathFilter}：限定子树 {scope._frns.Count} 条（其中 {propagated} 条由传播补齐）";
        return scope;
    }

    /// <summary>
    /// USN 增量维护（与 `OwnScope.Extend` 同款）：新条目若挂在本作用域内的目录下 ⇒ 纳入。
    /// 只看记录的 (Frn, ParentFrn)，与 store 无关 ⇒ 调用时序无要求。
    /// </summary>
    public int Extend(IReadOnlyList<UsnRecord> records)
    {
        if (!Anchored || records.Count == 0)
        {
            return 0;
        }

        int added = 0;
        foreach (var rec in records)
        {
            if (_frns.Contains(rec.Frn) || !_frns.Contains(rec.ParentFrn))
            {
                continue;
            }

            _frns.Add(rec.Frn);
            added++;
        }

        ExtendedByUsn += added;
        return added;
    }

    // ── 内部：第 1 趟（名字命中） ──

    /// <summary>按"UTF-8 字节长度预筛 + 一次字节比较"收集名字命中规则的条目 FRN（零解码）。</summary>
    private List<ulong> CollectNameMatches(IndexStore store, IndexExcludeRuleSet rules)
    {
        var heap = store.HeapBytes;
        var offs = store.NameOffSlots;
        var lens = store.NameLenSlots;
        var frns = store.FrnSlots;

        var hits = new List<ulong>();
        for (int slot = 0; slot < frns.Length; slot++)
        {
            int len = lens[slot];
            if (len == IndexStore.TombstoneNameLen)
            {
                continue;   // 墓碑：已删除
            }

            if (rules.ByteMatches(heap.Slice(offs[slot], len)))
            {
                hits.Add(frns[slot]);
                if (hits.Count > MaxAnchors)
                {
                    break;
                }
            }
        }

        return hits;
    }

    // ── 内部：目录判定（★ 自举期 flags 恒 0，只能推） ──

    /// <summary>
    /// 从候选里挑出**目录**（★ 这是本波对设计方案的一处必要调整，见 §2.6）。
    ///
    /// <para><b>为什么不能读 <c>EntryFlags.Directory</c></b>：自举是 `TryAdd(..., flags: 0)`
    /// （`VolumeWorker.cs:163`）⇒ 存量索引里所有条目的 flags 都是 0，Directory 位**恒为 0**。
    /// 而按 §4.2"规则命中的是目录本身"，我们必须把同名的**文件**排除在外。</para>
    ///
    /// <para><b>怎么推</b>：一条记录是目录 ⟺ 它出现在某个条目的父字段里（含卷根自指）。
    /// 实现上**只对候选做判定**（候选通常几十~几千个），所以只需一趟 O(n) 的父字段扫描 + 两个小集合，
    /// 不做"给全部目录建索引"那笔大分配。</para>
    ///
    /// <para><b>已知代价（如实登记）</b>：**空目录**（尚无任何子项）不会出现在父字段里 ⇒ 判不成目录
    /// ⇒ 它自己不会被摘除。方向是**漏排除**（用户仍可能搜到那个空目录壳），落在安全侧；
    /// 一旦它有子项，规则立即对它生效。设计方案 §2.6 已登记。</para>
    /// </summary>
    private int KeepDirectoriesOnly(IndexStore store, List<ulong> candidates)
    {
        var candSet = new HashSet<ulong>(candidates);
        var parents = store.ParentSlots;
        var frns = store.FrnSlots;

        var dirs = new HashSet<ulong>();
        for (int slot = 0; slot < parents.Length; slot++)
        {
            var parent = parents[slot];
            if (candSet.Contains(parent))
            {
                dirs.Add(parent);
            }
        }

        // 卷根自指（parent == self）也算目录：它可能恰好命中规则名（防御，正常不会）
        for (int slot = 0; slot < frns.Length; slot++)
        {
            if (frns[slot] == parents[slot] && candSet.Contains(frns[slot]))
            {
                dirs.Add(frns[slot]);
            }
        }

        _frns.UnionWith(dirs);
        return dirs.Count;
    }

    // ── 内部：传播 ──

    /// <summary>
    /// 前向传播：父 ∈ 作用域 ⇒ 子 ∈ 作用域。升序一趟 + 轮次上限。
    /// NTFS 下父目录 FRN 恒小于子项 FRN ⇒ 升序单趟即可覆盖（与 `OwnScope` 同一不变量）。
    /// </summary>
    private int SweepDescendants(IndexStore store)
    {
        var frns = store.FrnSlots;
        var parents = store.ParentSlots;
        var lens = store.NameLenSlots;
        int added = 0;

        for (int round = 0; round < MaxSweepRounds; round++)
        {
            bool grew = false;
            for (int slot = 0; slot < frns.Length; slot++)
            {
                if (lens[slot] == IndexStore.TombstoneNameLen)
                {
                    continue;
                }

                ulong frn = frns[slot];
                if (_frns.Contains(frn) || !_frns.Contains(parents[slot]))
                {
                    continue;
                }

                _frns.Add(frn);
                added++;
                grew = true;
            }

            if (!grew)
            {
                break;   // 收敛（正常第 1 轮就够）
            }
        }

        return added;
    }
}
