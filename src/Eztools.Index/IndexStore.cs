// ============================================================================
// Eztools.Index / IndexStore —— W3-a-2 存储结构与索引模型（内存侧）
//
// 结构定义（权威版本：docs/W3-极速文件搜索-设计方案.md §3.6）：
//   ① 名字堆   byte[]  _heap      —— 全部名字 UTF-8 连续拼接，只增不改（删除留洞）
//   ② 并行数组（struct-of-arrays，按 FRN 严格升序维护，供二分反查）
//        ulong[]  _frn        8 B   FRN（含序号高位，全局唯一）
//        ulong[]  _parentFrn  8 B   父目录 FRN → 全路径是"查出来的"（§3.5）
//        int[]    _nameOff    4 B   名字在堆内的字节偏移
//        ushort[] _nameLen    2 B   名字字节数；0xFFFF = 墓碑（删除留洞）
//        byte[]   _flags      1 B   bit0=目录 bit1=隐藏 bit2=系统
//   ③ 首字符位图 ulong[2]（128 bit）—— 某首字符是否存在，仅前缀预筛用。
//        映射 = char.ToLowerInvariant(c) & 0x7F；**允许假阳性、绝不允许假阴性**
//        （删除后位图不清位——假阳性只是多扫，假阳性不会漏结果）。
//
// 与设计方案 §3.3 预算的两处有意偏差（都在 60 MB 上限内，§3.6 有登记）：
//   - _nameLen 用 ushort（实施计划 W3-a-2 风险行：byte 会撞 255 溢出），
//     哨兵取 0xFFFF，真实名字最长 0xFFFE 字节（NTFS 255 UTF-16 字符 ≤ 765 B，余量充足）；
//   - _flags 独立成数组（不与 nameLen 打包）⇒ 单条 23 B + 名字堆 ≈ 13 B ≈ 36 B。
//
// 并发：单线程。卷内单任务是 W3-a-3 的明文约束（卷间才并行）。
// ============================================================================

using System.Text;

namespace Eztools.Index;

/// <summary>条目 flags 位（设计方案 §3.2 ③）。</summary>
public static class EntryFlags
{
    public const byte Directory = 0b0001;
    public const byte Hidden = 0b0010;
    public const byte System = 0b0100;
}

/// <summary>反查命中后的条目视图（名字已按 UTF-8 解码）。</summary>
public readonly struct IndexEntry
{
    public required ulong Frn { get; init; }
    public required ulong ParentFrn { get; init; }
    public required byte Flags { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// 扁平数组 + 紧凑名字堆 + 首字符位图的内存索引（设计方案 §3）。
/// 删除不搬数据只标洞（<see cref="TombstoneNameLen"/>），碎片率超阈值时调用
/// <see cref="Compact"/> 在空闲时一次性重排。
/// </summary>
public sealed class IndexStore
{
    /// <summary>墓碑哨兵：nameLen == 0xFFFF 表示该槽位已删除（洞）。</summary>
    public const ushort TombstoneNameLen = 0xFFFF;

    /// <summary>压缩触发阈值（设计方案 §3.4 决策 2：碎片率 &gt; 25% 才重排）。</summary>
    public const double CompactThreshold = 0.25;

    private const int InitialSlotCapacity = 256;
    private const int InitialHeapCapacity = 64 * 1024;

    private ulong[] _frn;
    private ulong[] _parentFrn;
    private int[] _nameOff;
    private ushort[] _nameLen;
    private byte[] _flags;

    private byte[] _heap;
    private int _heapUsed;

    private int _slotCount;   // 含洞
    private int _entryCount;  // 活条目
    private int _holeCount;

    // 首字符位图：128 bit。[0] 管映射值 0~63，[1] 管 64~127。
    private readonly ulong[] _firstCharBits = new ulong[2];

    public IndexStore()
    {
        _frn = new ulong[InitialSlotCapacity];
        _parentFrn = new ulong[InitialSlotCapacity];
        _nameOff = new int[InitialSlotCapacity];
        _nameLen = new ushort[InitialSlotCapacity];
        _flags = new byte[InitialSlotCapacity];
        _heap = new byte[InitialHeapCapacity];
    }

    // ── 观测量 ──

    /// <summary>
    /// 变更代数：每次成功写操作（插入/删除）+1。下游缓存（PathResolver frn→路径）
    /// 以此做失效判据 —— USN 增量改了目录名后，旧代缓存绝不复用（W3-c-2）。
    /// Compact 不动此计数（重排不改任何 frn→路径映射）。
    /// </summary>
    public int Version { get; private set; }

    /// <summary>活条目数（验收口径：entryCount）。</summary>
    public int EntryCount => _entryCount;

    /// <summary>槽位数（含洞）。</summary>
    public int SlotCount => _slotCount;

    /// <summary>洞数（墓碑槽位）。</summary>
    public int HoleCount => _holeCount;

    /// <summary>名字堆已用字节（含洞占用的死字节）。</summary>
    public int HeapUsedBytes => _heapUsed;

    /// <summary>碎片率 = 洞 / 槽位。</summary>
    public double Fragmentation => _slotCount == 0 ? 0.0 : (double)_holeCount / _slotCount;

    /// <summary>是否需要压缩（碎片率超阈值）。</summary>
    public bool NeedsCompact => Fragmentation > CompactThreshold;

    // ── 写入 ──

    /// <summary>
    /// 插入一条。FRN 已存在且为活条目 ⇒ 拒绝（返回 false）；
    /// FRN 已存在但为墓碑 ⇒ **复用该槽位**（升序不被破坏，堆上新开字节，旧字节留作洞的一部分）。
    /// </summary>
    public bool TryAdd(ulong frn, ulong parentFrn, ReadOnlySpan<char> name, byte flags)
    {
        if (name.IsEmpty)
        {
            throw new ArgumentException("名字不能为空", nameof(name));
        }

        int byteLen = Encoding.UTF8.GetByteCount(name);
        if (byteLen >= TombstoneNameLen)
        {
            throw new ArgumentException($"名字 UTF-8 字节数 {byteLen} 超过上限 {TombstoneNameLen - 1}", nameof(name));
        }

        int pos = LowerBound(frn);

        // 同 FRN 槽位已存在：活条目拒绝，墓碑复用
        if (pos < _slotCount && _frn[pos] == frn)
        {
            if (_nameLen[pos] != TombstoneNameLen)
            {
                return false;
            }

            int off = AppendToHeap(name, byteLen);
            _parentFrn[pos] = parentFrn;
            _nameOff[pos] = off;
            _nameLen[pos] = (ushort)byteLen;
            _flags[pos] = flags;
            _holeCount--;
            _entryCount++;
            SetFirstCharBit(name);
            Version++;
            return true;
        }

        EnsureSlotCapacity(_slotCount + 1);

        // 尾插快路径（全量灌入 = FRN 基本升序，零搬移）
        if (pos < _slotCount)
        {
            Array.Copy(_frn, pos, _frn, pos + 1, _slotCount - pos);
            Array.Copy(_parentFrn, pos, _parentFrn, pos + 1, _slotCount - pos);
            Array.Copy(_nameOff, pos, _nameOff, pos + 1, _slotCount - pos);
            Array.Copy(_nameLen, pos, _nameLen, pos + 1, _slotCount - pos);
            Array.Copy(_flags, pos, _flags, pos + 1, _slotCount - pos);
        }

        int offset = AppendToHeap(name, byteLen);
        _frn[pos] = frn;
        _parentFrn[pos] = parentFrn;
        _nameOff[pos] = offset;
        _nameLen[pos] = (ushort)byteLen;
        _flags[pos] = flags;
        _slotCount++;
        _entryCount++;
        SetFirstCharBit(name);
        Version++;
        return true;
    }

    /// <summary>删除：只标洞不搬数据。FRN 不存在或已是洞 ⇒ false。</summary>
    public bool Remove(ulong frn)
    {
        int pos = LowerBound(frn);
        if (pos >= _slotCount || _frn[pos] != frn || _nameLen[pos] == TombstoneNameLen)
        {
            return false;
        }

        // 位图不清位：假阳性只多扫，绝不漏结果（§3.6 不变量 I4）
        _nameLen[pos] = TombstoneNameLen;
        _entryCount--;
        _holeCount++;
        Version++;
        return true;
    }

    // ── 反查（二分，设计方案 §3.4 决策 1）──

    public bool Contains(ulong frn) => TryGet(frn, out _);

    public bool TryGet(ulong frn, out IndexEntry entry)
    {
        int pos = LowerBound(frn);
        // 墓碑必须显式排除：否则删除后反查"静默返回旧条目"（W3-a-2 验收③的反面）
        if (pos >= _slotCount || _frn[pos] != frn || _nameLen[pos] == TombstoneNameLen)
        {
            entry = default;
            return false;
        }

        int len = _nameLen[pos];
        entry = new IndexEntry
        {
            Frn = frn,
            ParentFrn = _parentFrn[pos],
            Flags = _flags[pos],
            Name = Encoding.UTF8.GetString(_heap, _nameOff[pos], len),
        };
        return true;
    }

    // ── 首字符位图（前缀预筛，W3-b 查询管道第一级）──

    /// <summary>该首字符是否可能存在。true = 可能（需实扫确认），false = 必不存在。</summary>
    public bool IsFirstCharPossible(char c)
    {
        int bit = BitIndex(c);
        return (_firstCharBits[bit >> 6] & (1ul << (bit & 63))) != 0;
    }

    // ── 压缩重排（设计方案 §3.4 决策 2：碎片率 &gt; 25% 空闲时做一次）──

    /// <summary>重排并行数组与名字堆，清除全部洞并重建位图。返回重排前的堆字节数（供日志）。</summary>
    public int Compact()
    {
        int oldHeapBytes = _heapUsed;

        var frn = new ulong[_entryCount];
        var parentFrn = new ulong[_entryCount];
        var nameOff = new int[_entryCount];
        var nameLen = new ushort[_entryCount];
        var flags = new byte[_entryCount];
        var heap = new byte[Math.Max(_heapUsed, InitialHeapCapacity)]; // 精确打包，不留洞

        int heapW = 0;
        int w = 0;
        Array.Clear(_firstCharBits);
        for (int i = 0; i < _slotCount; i++)
        {
            if (_nameLen[i] == TombstoneNameLen)
            {
                continue;
            }

            int len = _nameLen[i];
            Buffer.BlockCopy(_heap, _nameOff[i], heap, heapW, len);
            frn[w] = _frn[i];
            parentFrn[w] = _parentFrn[i];
            nameOff[w] = heapW;
            nameLen[w] = (ushort)len;
            flags[w] = _flags[i];
            heapW += len;
            w++;
        }

        _frn = frn;
        _parentFrn = parentFrn;
        _nameOff = nameOff;
        _nameLen = nameLen;
        _flags = flags;
        _heap = heap;
        _heapUsed = heapW;
        _slotCount = _entryCount;
        _holeCount = 0;

        // 重建位图（Compact 后假阳性归零）。
        // 注意：首字符必须按 UTF-8 前导字节确定序列长度后整段解码 ——
        // 只解 1 字节会把多字节首字符（中文 3 B）变成 U+FFFD ⇒ 位图设错位 ⇒ 假阴性（违反 I4）。
        RebuildBitmap();

        return oldHeapBytes;
    }

    /// <summary>
    /// 升序校验（Debug 期全量 / 测试用）：FRN 槽位序必须严格升序 —— 含洞槽位
    /// （洞保留 FRN，不破坏排序；这也是"墓碑复用"能走二分的前提）。
    /// 升序被破坏的症状是**反查静默返回错误结果**（实施计划 W3-a-2 风险行 🔴），
    /// 所以除了测试断言，批量灌入方（W3-a-3）应在灌入完成后调用一次。
    /// </summary>
    public bool ValidateSorted()
    {
        for (int i = 1; i < _slotCount; i++)
        {
            if (_frn[i] <= _frn[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    // ── 持久化访问面（internal：仅供同程序集 Persister 使用，W3-a-4）──
    //
    // 暴露的是**槽位切片**（0..SlotCount），不是底层数组 —— 数组容量富余部分
    // 绝不外泄（Save 用 MemoryMarshal.AsBytes 零拷贝写段，切片错=把垃圾写进文件）。

    internal ReadOnlySpan<ulong> FrnSlots => _frn.AsSpan(0, _slotCount);

    internal ReadOnlySpan<ulong> ParentSlots => _parentFrn.AsSpan(0, _slotCount);

    internal ReadOnlySpan<int> NameOffSlots => _nameOff.AsSpan(0, _slotCount);

    internal ReadOnlySpan<ushort> NameLenSlots => _nameLen.AsSpan(0, _slotCount);

    internal ReadOnlySpan<byte> FlagsSlots => _flags.AsSpan(0, _slotCount);

    internal ReadOnlySpan<byte> HeapBytes => _heap.AsSpan(0, _heapUsed);

    /// <summary>
    /// 从持久化段数据重建（<see cref="Persister.TryLoad"/> 专用，W3-a-4）。
    /// 五个数组与堆必须是**精确长度**（调用方负责切片），洞（墓碑）原样保留；
    /// 位图在此重建 —— 位图是派生结构，不落盘（落了也会因堆变化失效）。
    /// </summary>
    internal static IndexStore FromRaw(
        ulong[] frn, ulong[] parentFrn, int[] nameOff, ushort[] nameLen, byte[] flags, byte[] heap)
    {
        if (frn.Length != parentFrn.Length || frn.Length != nameOff.Length
            || frn.Length != nameLen.Length || frn.Length != flags.Length)
        {
            throw new ArgumentException("五个段数组长度必须一致");
        }

        var store = new IndexStore();
        store._frn = frn;
        store._parentFrn = parentFrn;
        store._nameOff = nameOff;
        store._nameLen = nameLen;
        store._flags = flags;
        store._heap = heap;
        store._heapUsed = heap.Length;
        store._slotCount = frn.Length;

        int entryCount = 0;
        foreach (ushort len in nameLen)
        {
            if (len != TombstoneNameLen)
            {
                entryCount++;
            }
        }

        store._entryCount = entryCount;
        store._holeCount = frn.Length - entryCount;
        store.RebuildBitmap();
        return store;
    }

    /// <summary>从堆重建首字符位图（Compact 与 FromRaw 共用；多字节首字符按前导字节整段解码）。</summary>
    private void RebuildBitmap()
    {
        Array.Clear(_firstCharBits);
        for (int i = 0; i < _slotCount; i++)
        {
            if (_nameLen[i] == TombstoneNameLen)
            {
                continue;
            }

            char first = FirstCharFromUtf8(_heap, _nameOff[i], _nameLen[i]);
            int bit = BitIndex(char.ToLowerInvariant(first));
            _firstCharBits[bit >> 6] |= 1ul << (bit & 63);
        }
    }

    // ── 内部 ──

    /// <summary>lower-bound 二分：第一个 FRN &gt;= frn 的槽位。</summary>
    private int LowerBound(ulong frn)
    {
        int lo = 0, hi = _slotCount;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_frn[mid] < frn)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// 活条目槽位查找（internal：同程序集 QueryEngine 的扫描/缓存复用面，W3-b）。
    /// 返回槽位下标；FRN 不存在或为墓碑 ⇒ -1。
    /// </summary>
    internal int FindSlot(ulong frn)
    {
        int pos = LowerBound(frn);
        return pos < _slotCount && _frn[pos] == frn && _nameLen[pos] != TombstoneNameLen ? pos : -1;
    }

    private int AppendToHeap(ReadOnlySpan<char> name, int byteLen)
    {
        EnsureHeapCapacity(_heapUsed + byteLen);
        Encoding.UTF8.GetBytes(name, _heap.AsSpan(_heapUsed));
        int offset = _heapUsed;
        _heapUsed += byteLen;
        return offset;
    }

    private void EnsureSlotCapacity(int required)
    {
        if (required <= _frn.Length)
        {
            return;
        }

        int newSize = _frn.Length;
        while (newSize < required)
        {
            newSize *= 2;
        }

        Array.Resize(ref _frn, newSize);
        Array.Resize(ref _parentFrn, newSize);
        Array.Resize(ref _nameOff, newSize);
        Array.Resize(ref _nameLen, newSize);
        Array.Resize(ref _flags, newSize);
    }

    private void EnsureHeapCapacity(int required)
    {
        if (required <= _heap.Length)
        {
            return;
        }

        int newSize = _heap.Length;
        while (newSize < required)
        {
            newSize *= 2;
        }

        Array.Resize(ref _heap, newSize);
    }

    private void SetFirstCharBit(ReadOnlySpan<char> name)
    {
        int bit = BitIndex(char.ToLowerInvariant(name[0]));
        _firstCharBits[bit >> 6] |= 1ul << (bit & 63);
    }

    private static int BitIndex(char c) => char.ToLowerInvariant(c) & 0x7F;

    /// <summary>从堆字节解出首字符：按 UTF-8 前导字节确定序列长度（1~4），再整段解码。</summary>
    private static char FirstCharFromUtf8(byte[] heap, int offset, int nameLen)
    {
        int lead = heap[offset];
        int seq = (lead & 0xF8) == 0xF0 ? 4
            : (lead & 0xF0) == 0xE0 ? 3
            : (lead & 0xE0) == 0xC0 ? 2
            : 1;
        if (seq > nameLen)
        {
            seq = nameLen;
        }

        return Encoding.UTF8.GetString(heap, offset, seq)[0];
    }
}
