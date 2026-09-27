// ============================================================================
// Eztools.Index / Persister —— W3-a-4 持久化与热启动（.ezidx 落盘 / mmap 加载）
//
// 文件格式（权威版本：docs/W3-极速文件搜索-设计方案.md §3.4，精确布局以本文件为准）：
//   <安装根>/index/vol-<serial:X16>.ezidx
//   ┌ Header (64 B, little-endian) ──────────────────────────────────────
//   │  0   8   magic  "EZIDX\0\0\0"
//   │  8   4   formatVersion u32（= 1；结构变更必须递增，旧版本显式拒绝）
//   │ 12   4   flags u32（= 0，预留）
//   │ 16   8   volumeSerial u64（换盘 ⇒ VolumeMismatch ⇒ 重建）
//   │ 24   8   usnJournalId u64（W3-c 起用于增量对账，§4.2）
//   │ 32   8   nextUsn i64（本阶段恒 0：readMft 不含 journal 查询，§2.4 已登记）
//   │ 40   8   entryCount u64（活条目）
//   │ 48   4   slotCount u32（含洞 —— 洞是语义的一部分：墓碑复用依赖 FRN 槽位保留）
//   │ 52   8   nameHeapSize u64
//   │ 60   4   crc32c u32 —— 覆盖 header[0..60) + 全部段 + 名字堆
//   └ 段（slotCount × 各自宽度，FRN 段严格升序）：
//       FRN(8B) · Parent(8B) · NameOff(4B) · NameLen(2B) · Flags(1B)
//     名字堆（UTF-8 原样，删除留洞不改动）
//
// 与 §3.4 草图的两处偏差（§3.4 已登记）：
//   - createdUtcTicks 不落盘（诊断价值低，文件 mtime 即可），让出 8 B 给 slotCount；
//   - slotCount u32 为新增字段（草图只写 entryCount，无法推出段长与洞）。
//
// 🔴 加载铁律（§3.4）：magic / 版本 / 形状 / CRC / 卷序列号 任何一项不符 ⇒
//   返回结构化 EzidxLoadStatus 让调用方**显式重建**，禁止"尽力解析"
//   （本项目 S1 教训的持久化等价物：静默读到垃圾 = 跑旧解析器）。
//
// 并发：Save/TryLoad 均为单线程一次性操作；调用方保证 IndexStore 无并发写。
// ============================================================================

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Eztools.Index;

/// <summary>TryLoad 的结构化结果。非 <see cref="EzidxLoadStatus.Ok"/> 时 Store/Header 均为 null。</summary>
public enum EzidxLoadStatus
{
    Ok,
    FileMissing,
    BadMagic,
    BadVersion,
    BadShape,
    CrcMismatch,
    VolumeMismatch,
}

/// <summary>header 中调用方关心的字段（W3-c 增量对账消费 usnJournalId / nextUsn）。</summary>
public sealed record EzidxFileInfo(
    ulong VolumeSerial,
    ulong UsnJournalId,
    long NextUsn,
    ulong EntryCount);

/// <summary>加载结果：状态 + 索引 + header 摘要。</summary>
public sealed record EzidxLoadResult(EzidxLoadStatus Status, IndexStore? Store, EzidxFileInfo? Header)
{
    public bool IsOk => Status == EzidxLoadStatus.Ok;

    public static EzidxLoadResult Fail(EzidxLoadStatus status) => new(status, null, null);
}

/// <summary>.ezidx 落盘 / 加载（W3-a-4）。格式定义见文件头注释。</summary>
public static class Persister
{
    public const uint CurrentFormatVersion = 1;
    public const int HeaderSize = 64;

    /// <summary>字节序魔数 "EZIDX\0\0\0"（u64 形式，little-endian 读出）。</summary>
    private const ulong MagicEzidx = 0x00_00_00_00_58_44_49_45; // 'E','I','D','X',0,0,0,0

    private const int SlotBytes = 8 + 8 + 4 + 2 + 1; // 23 B/条（五段合计）

    // ── 路径 ──

    /// <summary>索引文件路径：&lt;installRoot&gt;/index/vol-&lt;serial:X16&gt;.ezidx。</summary>
    public static string GetIndexPath(string installRoot, ulong volumeSerial) =>
        Path.Combine(installRoot, "index", $"vol-{volumeSerial:X16}.ezidx");

    // ── 落盘 ──

    /// <summary>
    /// 落盘。FRN 段不是严格升序 ⇒ 直接抛（fail-fast：升序被破坏的索引落盘 = 生成方 bug，
    /// 绝不写出一个"加载后反查错乱"的文件）。段与堆用切片零拷贝直写，不复制。
    /// </summary>
    public static void Save(IndexStore store, string path, ulong volumeSerial, ulong usnJournalId, long nextUsn)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.ValidateSorted())
        {
            throw new InvalidOperationException(
                $"FRN 段非严格升序（slotCount={store.SlotCount}），拒绝落盘 —— 请先排查生成方");
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        WriteHeader(header, store, volumeSerial, usnJournalId, nextUsn, crcPlaceholder: 0);

        using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);

        // CRC 覆盖 header[0..60) + 段 + 堆；先以 crc=0 写 header 占位，边写边算，最后回填。
        uint raw = Crc32C.Update(0, header[..60]);
        fs.Write(header);

        raw = WriteSegment(fs, MemoryMarshal.AsBytes(store.FrnSlots), raw);
        raw = WriteSegment(fs, MemoryMarshal.AsBytes(store.ParentSlots), raw);
        raw = WriteSegment(fs, MemoryMarshal.AsBytes(store.NameOffSlots), raw);
        raw = WriteSegment(fs, MemoryMarshal.AsBytes(store.NameLenSlots), raw);
        raw = WriteSegment(fs, store.FlagsSlots, raw);
        raw = WriteSegment(fs, store.HeapBytes, raw);

        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(60, 4), Crc32C.Final(raw));
        fs.Seek(60, SeekOrigin.Begin);
        fs.Write(header.Slice(60, 4));
    }

    // ── 加载（mmap）──

    /// <summary>
    /// mmap 加载。任何一项结构校验不符 ⇒ 结构化状态返回（调用方显式重建），
    /// 绝不返回部分解析的索引。
    /// </summary>
    public static EzidxLoadResult TryLoad(string path, ulong expectedVolumeSerial)
    {
        if (!File.Exists(path))
        {
            return EzidxLoadResult.Fail(EzidxLoadStatus.FileMissing);
        }

        long length = new FileInfo(path).Length;
        if (length < HeaderSize)
        {
            return EzidxLoadResult.Fail(EzidxLoadStatus.BadShape);
        }

        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

        var header = new byte[HeaderSize];
        _ = view.ReadArray(0, header, 0, HeaderSize);

        if (BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0, 8)) != MagicEzidx)
        {
            return EzidxLoadResult.Fail(EzidxLoadStatus.BadMagic);
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        if (version != CurrentFormatVersion)
        {
            return EzidxLoadResult.Fail(EzidxLoadStatus.BadVersion);
        }

        ulong volumeSerial = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16, 8));
        if (volumeSerial != expectedVolumeSerial)
        {
            return EzidxLoadResult.Fail(EzidxLoadStatus.VolumeMismatch);
        }

        ulong entryCount = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40, 8));
        uint slotCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48, 4));
        ulong nameHeapSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(52, 8));

        // 形状：文件长必须精确等于 header + 五段 + 堆；slot/entry 数值关系自洽。
        if (entryCount > slotCount
            || length != HeaderSize + (long)slotCount * SlotBytes + (long)nameHeapSize)
        {
            return EzidxLoadResult.Fail(EzidxLoadStatus.BadShape);
        }

        // CRC：header[0..60) + 五段 + 堆 全量重算。
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(60, 4));
        uint raw = Crc32C.Update(0, header.AsSpan(0, 60));

        var frn = new ulong[slotCount];
        var parent = new ulong[slotCount];
        var nameOff = new int[slotCount];
        var nameLen = new ushort[slotCount];
        var flags = new byte[slotCount];
        var heap = new byte[nameHeapSize];

        raw = ReadSegmentAndCrc(view, HeaderSize, frn, raw);
        raw = ReadSegmentAndCrc(view, HeaderSize + 8L * slotCount, parent, raw);
        raw = ReadSegmentAndCrc(view, HeaderSize + 16L * slotCount, nameOff, raw);
        raw = ReadSegmentAndCrc(view, HeaderSize + 20L * slotCount, nameLen, raw);
        raw = ReadSegmentAndCrc(view, HeaderSize + 22L * slotCount, flags, raw);
        raw = ReadSegmentAndCrc(view, HeaderSize + 23L * slotCount, heap, raw);

        if (Crc32C.Final(raw) != storedCrc)
        {
            return EzidxLoadResult.Fail(EzidxLoadStatus.CrcMismatch);
        }

        var store = IndexStore.FromRaw(frn, parent, nameOff, nameLen, flags, heap);
        if (!store.ValidateSorted())
        {
            // CRC 通过但排序坏 = 生成方 bug 写出的"内容一致但结构非法"的文件，同样拒绝。
            return EzidxLoadResult.Fail(EzidxLoadStatus.BadShape);
        }

        var info = new EzidxFileInfo(
            volumeSerial,
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32, 8)),
            entryCount);
        return new EzidxLoadResult(EzidxLoadStatus.Ok, store, info);
    }

    // ── 内部 ──

    private static void WriteHeader(
        Span<byte> header, IndexStore store, ulong volumeSerial, ulong usnJournalId, long nextUsn, uint crcPlaceholder)
    {
        header.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(header[..8], MagicEzidx);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), CurrentFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), 0); // flags 预留
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(16, 8), volumeSerial);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24, 8), usnJournalId);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(32, 8), nextUsn);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40, 8), (ulong)store.EntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(48, 4), (uint)store.SlotCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(52, 8), (ulong)store.HeapUsedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(60, 4), crcPlaceholder);
    }

    private static uint WriteSegment(FileStream fs, ReadOnlySpan<byte> data, uint rawCrc)
    {
        rawCrc = Crc32C.Update(rawCrc, data);
        fs.Write(data);
        return rawCrc;
    }

    private static uint ReadSegmentAndCrc<T>(MemoryMappedViewAccessor view, long position, T[] target, uint rawCrc)
        where T : unmanaged
    {
        if (target.Length == 0)
        {
            return rawCrc; // 空段（如空库 heap=0）：position 可能 == capacity，ReadArray 会抛，直接跳过
        }

        _ = view.ReadArray(position, target, 0, target.Length);
        return Crc32C.Update(rawCrc, MemoryMarshal.AsBytes(target.AsSpan()));
    }
}

/// <summary>
/// CRC32C（Castagnoli，反射多项式 0x82F63B78）查表实现 —— 零依赖（避免引入
/// System.IO.Hashing 包）；流式接口（Update 保持原始寄存器，Final 取反收尾），
/// 分段 Update 与一次性 Compute 结果一致。
/// </summary>
internal static class Crc32C
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc >> 1) ^ (0x82F63B78u & (uint)-(int)(crc & 1));
            }

            table[i] = crc;
        }

        return table;
    }

    /// <summary>流式推进。首次传 0，后续传上一次返回值。</summary>
    public static uint Update(uint raw, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            raw = Table[(raw ^ b) & 0xFF] ^ (raw >> 8);
        }

        return raw;
    }

    /// <summary>收尾取反，得到最终 CRC32C 值。</summary>
    public static uint Final(uint raw) => raw ^ 0xFFFFFFFFu;

    /// <summary>一次性计算（测试与短数据用）。</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Final(Update(0, data));
}
