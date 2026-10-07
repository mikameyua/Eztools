// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Eztools.Index;

/// <summary>
/// 自有子树作用域（P4）：DataRoot 落在本卷时，把它的**整棵子树**的 FRN 标记出来，
/// 供**扫描期** O(1) 排除 —— 让"文件搜索工具搜到自己"这件事不发生。
///
/// <b>为什么不是"扫描期按路径排除"</b>：扫描段只持有 <c>(Key, Frn, Store, Pos)</c>，**没有路径**
/// （路径是收敛到 ≤ K 个候选之后才回溯的）。要按路径判定就得每候选走一次父链 ——
/// 那正是设计方案 §5.2 标为 🔴 不可接受的做法（百万次二分）。FRN 标记把这件事从
/// "每候选 O(深度)"降到"每候选一次哈希查表"，且**只在自举期付一次 O(n)**。
///
/// <b>为什么不需要任何 P/Invoke / 不碰"FRN 表示是否可比"</b>：锚定与传播都**只读索引本身**
/// （<see cref="IndexStore"/> 的 FRN 段与父段），与查询用的是同一份数据 ⇒ 表示一致是**构造性事实**，
/// 不是需要实测的外部假设。
///
/// <b>为什么不用持久化</b>：标记是**派生状态**（由 store + DataRoot 决定）。热启动后重算一次
/// （O(n) 廉价扫描，只跑 DataRoot 所在的那一个卷）就没有"侧表与索引不同步"这一类问题；
/// 而加侧表要让 <c>.ezidx</c> 升格式版本（全体用户重建一次索引），代价与收益不成比例。
///
/// <b>算法（两趟，都依赖 <see cref="IndexStore"/> 按 FRN 升序这一个既有不变量）</b>：
/// <list type="number">
/// <item><b>锚定</b>：按 UTF-8 字节比较找"叶子名"候选（零解码），再逐级向上核对父链名字
///   ⇒ 得到 DataRoot 自身的 FRN。<b>整条链对不上就不锚定</b>（只按名字排除会让用户同名目录
///   里的真文件**静默消失** —— 那是 W3-e-2 的 S9′ 红线）。</item>
/// <item><b>前向传播</b>：一趟扫过槽位，<c>父 ∈ 自有 ⇒ 子 ∈ 自有</c>。NTFS 下父目录 FRN 恒小于
///   子项 FRN，故升序一趟即可覆盖；移动进来的既有子树会漏标 —— 漏标 = **少排除**（用户仍能搜到），
///   落在安全方向，不产生"静默丢结果"。</item>
/// </list>
///
/// <b>未锚定（<see cref="Anchored"/>=false）时 <see cref="Contains"/> 恒 false</b>：
/// 不排除任何条目，并把原因留在 <see cref="Reason"/> 里 —— 与 <see cref="JournalTail.CollectOwnNames"/>
/// 同一条纪律：**拿不准就什么都不做，并且说得出为什么**（绝不"猜一个"）。
/// </summary>
public sealed class OwnScope
{
    /// <summary>父链核对深度上限（成环/异常超长兜底；正常 NTFS 深度远小于此）。</summary>
    public const int MaxChainDepth = 64;

    /// <summary>前向传播的最大轮数（正常情况下第 2 轮即无新增；上界只为防病态输入死循环）。</summary>
    public const int MaxSweepRounds = 8;

    private readonly HashSet<ulong> _frns = [];

    private OwnScope()
    {
    }

    /// <summary>
    /// 是否已锚定。false ⇒ <see cref="Contains"/> 恒 false（不排除任何条目）。
    /// 与 <see cref="Count"/>==0 分开表达：**"没找到"与"找到了但为空"是两件事**。
    /// </summary>
    public bool Anchored { get; private set; }

    /// <summary>人读原因。未锚定也要说得出为什么（不猜、不静默）。</summary>
    public string Reason { get; private set; } = "未标记";

    /// <summary>被标记的 FRN 个数（锚定后 ≥ 1：DataRoot 目录自身）。</summary>
    public int Count => _frns.Count;

    /// <summary>由 USN 增量补标的条数（观测面：证明增量期维护真的在跑）。</summary>
    public long ExtendedByUsn { get; private set; }

    /// <summary>扫描期判据：该 FRN 是否属于自有子树（**未锚定恒 false**）。</summary>
    public bool Contains(ulong frn) => Anchored && _frns.Contains(frn);

    /// <summary>未锚定的作用域（不排除任何东西），带上原因。</summary>
    public static OwnScope NotAnchored(string reason) => new() { Anchored = false, Reason = reason };

    /// <summary>
    /// 标记：<paramref name="dataRoot"/> 落在 <paramref name="volume"/> 上时，
    /// 找出它在 <paramref name="store"/> 里的 FRN 并标记整棵子树。
    /// 任何一步对不上 ⇒ 返回**未锚定**作用域（附原因），绝不"尽力猜一个"。
    /// </summary>
    public static OwnScope Mark(IndexStore store, string volume, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(store);

        var volRoot = VolumeRootOf(volume);
        if (volRoot is null)
        {
            return NotAnchored($"卷名无法解析（{volume}）");
        }

        if (!TryRelativeComponents(volRoot, dataRoot, out var components))
        {
            return NotAnchored($"DataRoot 不在本卷（{dataRoot}）");
        }

        if (components.Length > MaxChainDepth)
        {
            return NotAnchored($"DataRoot 层级过深（{components.Length} > {MaxChainDepth}）");
        }

        var scope = new OwnScope();
        var anchor = FindAnchor(store, components);
        if (anchor is null)
        {
            return NotAnchored($"索引中找不到 DataRoot 子树（{dataRoot}）—— 本次不排除任何条目");
        }

        scope._frns.Add(anchor.Value);
        var propagated = scope.SweepDescendants(store, anchor.Value);
        scope.Anchored = true;
        scope.Reason = $"已锚定 {dataRoot}：排除自有子树 {scope._frns.Count} 条（其中 {propagated} 条由传播补齐）";
        return scope;
    }

    /// <summary>
    /// USN 增量维护：新条目若挂在自有目录下 ⇒ 纳入作用域（快照之后新建于 DataRoot 下的文件
    /// 才不会被"漏排除"）。只看记录的 (Frn, ParentFrn)，与 store 无关 ⇒ 调用时序无要求。
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

    // ── 内部：锚定 ──

    /// <summary>找 DataRoot 自身的 FRN；找不到（或父链对不上）⇒ null。</summary>
    /// <remarks>internal static：W11-b 的 <see cref="FrScope.MarkByPath"/>（pathFilter 路径锚定）
    /// 复用同一套"字节预筛 + 逐级核链"算法 —— **只共享算法，不共享语义**
    ///（"自有"与"用户限定"是两件事，W11 §5.3）。</remarks>
    internal static ulong? FindAnchor(IndexStore store, string[] components)
    {
        string leaf = components[^1];
        byte[] leafBytes = Encoding.UTF8.GetBytes(leaf);

        var heap = store.HeapBytes;
        var offs = store.NameOffSlots;
        var lens = store.NameLenSlots;
        var frns = store.FrnSlots;

        for (int slot = 0; slot < lens.Length; slot++)
        {
            int len = lens[slot];
            if (len == IndexStore.TombstoneNameLen || len != leafBytes.Length)
            {
                continue; // 墓碑 / 字节长度不符 —— 连解码都不需要
            }

            if (!BytesMatch(heap.Slice(offs[slot], len), leafBytes))
            {
                continue;
            }

            if (ChainMatches(store, frns[slot], components))
            {
                return frns[slot];
            }
        }

        return null;
    }

    /// <summary>自叶子向上逐级核对父链名字（叶子已由字节比较命中，这里一并复核）。internal：同 <see cref="FindAnchor"/>。</summary>
    internal static bool ChainMatches(IndexStore store, ulong leafFrn, string[] components)
    {
        ulong cur = leafFrn;
        for (int k = components.Length - 1; k >= 0; k--)
        {
            if (!store.TryGet(cur, out var entry))
            {
                return false;
            }

            if (!string.Equals(entry.Name, components[k], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (k == 0)
            {
                return true; // 最上一级（卷根之下的第一段）已核对通过
            }

            ulong parent = entry.ParentFrn;
            if (parent == cur)
            {
                return false; // 自指但还没走完链 ⇒ 异常数据，不锚定
            }

            cur = parent;
        }

        return false;
    }

    // ── 内部：传播 ──

    /// <summary>
    /// 前向传播：父 ∈ 自有 ⇒ 子 ∈ 自有。升序一趟 + 轮次上限。
    /// 剪枝：自有 FRN 恒 ≥ 锚点（NTFS 父 FRN &lt; 子 FRN），故锚点之前的槽位可整段跳过。
    /// </summary>
    private int SweepDescendants(IndexStore store, ulong anchorFrn)
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
                ulong frn = frns[slot];
                if (frn < anchorFrn || lens[slot] == IndexStore.TombstoneNameLen)
                {
                    continue;
                }

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
                break; // 收敛（正常第 1 轮就够）
            }
        }

        return added;
    }

    // ── 内部：路径与字节比较 ──

    /// <summary>卷名 → 卷根（"D:" / "D:\" / "d:" ⇒ "D:\"）。非法 ⇒ null。internal：同 <see cref="FindAnchor"/>。</summary>
    internal static string? VolumeRootOf(string volume)
    {
        if (string.IsNullOrWhiteSpace(volume))
        {
            return null;
        }

        var t = volume.Trim();
        if (t.Length < 2 || t[1] != ':')
        {
            return null;
        }

        char drive = char.ToUpperInvariant(t[0]);
        if (drive is < 'A' or > 'Z')
        {
            return null;
        }

        return $"{drive}:\\";
    }

    /// <summary>把 dataRoot 拆成"相对卷根"的分量；不在本卷 / 就是卷根自身 ⇒ false。internal：同 <see cref="FindAnchor"/>。</summary>
    internal static bool TryRelativeComponents(string volRoot, string dataRoot, out string[] components)
    {
        components = [];

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(dataRoot);
        }
        catch (ArgumentException)
        {
            return false; // 非法路径（含通配符等）
        }

        string root = Path.GetPathRoot(full) ?? "";
        if (!string.Equals(root, volRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false; // DataRoot == 卷根：排除整卷没有意义，不锚定
        }

        components = parts;
        return true;
    }

    /// <summary>
    /// UTF-8 字节比较（Windows 语义：ASCII 大小写不敏感，其余严格相等）。
    /// **零解码、零分配** —— 这是"一趟扫 200 万槽位"能便宜的前提。
    /// 用不到 <c>Fold</c>：store 存的是**原名**，全角/半角在 NTFS 里并不等价。
    /// internal：同 <see cref="FindAnchor"/>。
    /// </summary>
    internal static bool BytesMatch(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
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
