// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Index;

/// <summary>排空与回收的观测面（全部落具体数字，供日志与断言）。</summary>
/// <param name="ExcludedFrns">作用域里的 FRN 总数（排除集规模）。</param>
/// <param name="Removed">实际摘除数（&lt; ExcludedFrns 说明集合里有已被删除的 FRN）。</param>
/// <param name="Compacted">是否在落盘前做了压缩。</param>
/// <param name="HeapBytesBefore">压缩前的名字堆字节数（0 = 没压缩）。</param>
/// <param name="HolesAfter">压缩后的洞数（压缩过就应该是 0）。</param>
public sealed record PruneReport(
    int ExcludedFrns,
    int Removed,
    bool Compacted,
    int HeapBytesBefore,
    int HolesAfter);

/// <summary>
/// 排空 + 落盘前回收（W11-a 的"第 2~3 步"，设计方案 §3.3/§3.4、D6）。
///
/// <para><b>★ 为什么"排空"之后必须"压缩"（本波最容易被漏掉的一步）</b>：
/// <c>Persister.Save</c> 会**连同墓碑槽位与死名字堆字节一起落盘**（其头注释明文"删除留洞不改动"），
/// 而 <see cref="IndexStore.Compact"/> 在本波之前**生产代码零调用**。
/// ⇒ 只排空的话结果对了、解码省了，但 <c>.ezidx</c> **一个字节都不会少** ——
/// 本波"让索引变小"的主卖点会静默落空。所以摘除数 &gt; 0 就必须压缩一次。</para>
///
/// <para><b>为什么不用 25% 阈值</b>（D6）：那个阈值是为**运行期增量删除**设计的（那些洞随时可能被
/// <c>TryAdd</c> 复用，合并要挑空闲时机）。而这里是**自举末尾、即将整体落盘**，此刻回收是纯收益、
/// 且可预测。阈值语义原样留给运行期（<see cref="IndexStore.NeedsCompact"/>）。</para>
/// </summary>
public static class IndexPrune
{
    /// <summary>
    /// 执行排空 + 压缩。<paramref name="exclude"/> 未锚定或为空 ⇒ **什么都不做**并返回零报告
    /// （绝不"尽力猜一个"）。
    /// </summary>
    public static PruneReport Run(IndexStore store, FrScope exclude)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(exclude);

        if (!exclude.Anchored || exclude.Count == 0)
        {
            return new PruneReport(0, 0, false, 0, store.HoleCount);
        }

        int removed = store.SweepRemovals(exclude.Frns);
        if (removed == 0)
        {
            // 墓碑一个都没插进去 ⇒ 没有可回收的东西，压缩纯属浪费（O(n) 重排 + ~100MB 瞬时分配）
            return new PruneReport(exclude.Count, 0, false, 0, store.HoleCount);
        }

        int heapBefore = store.Compact();
        return new PruneReport(exclude.Count, removed, true, heapBefore, store.HoleCount);
    }
}
