// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Eztools.Index;

/// <summary>
/// USN Journal Reason 位常量（winioctl.h 权威值，设计方案 §2.3 —— 2026-09-24 修正版）。
///
/// ⚠️ 判断一律用位与（<see cref="UsnRecord.Has"/>），**禁止 ==**：
/// 一条记录常带多个 reason 位（尤其 CLOSE=0x80000000 与业务 reason 同现——
/// 文件创建后句柄关闭就产生 FILE_CREATE|CLOSE 复合记录，== 会把整条丢掉 = 增量全丢）。
/// </summary>
public static class UsnReason
{
    public const uint DataOverwrite = 0x00000001;
    public const uint DataExtend = 0x00000002;
    public const uint DataTruncation = 0x00000004;
    public const uint FileCreate = 0x00000100;
    public const uint FileDelete = 0x00000200;
    public const uint RenameOldName = 0x00001000;
    public const uint RenameNewName = 0x00002000;
    public const uint Close = 0x80000000;
}

/// <summary>
/// 一条 USN 变更记录（USN_RECORD_V2 解析结果）。
/// <see cref="FileAttributes"/> 保留原始属性位（bit 侧），落索引时经 <see cref="MapFlags"/> 映射。
/// </summary>
public readonly record struct UsnRecord(
    long Usn,
    ulong Frn,
    ulong ParentFrn,
    uint Reason,
    uint FileAttributes,
    string Name)
{
    /// <summary>位与判断（本类型唯一的 reason 判定入口 —— 设计方案 §2.3 红字）。</summary>
    public bool Has(uint reason) => (Reason & reason) != 0;
}

/// <summary>
/// USN_RECORD_V2 解析器（纯函数，selftest 用手工字节夹具可验）。
/// 布局（winioctl.h，小端）：0 u32 RecordLength · 4 u16 Major · 6 u16 Minor ·
/// 8 u64 Frn · 16 u64 ParentFrn · 24 i64 Usn · 32 u64 TimeStamp · 40 u32 Reason ·
/// 44 u32 SourceInfo · 48 u32 SecurityId · 52 u32 FileAttributes ·
/// 56 u16 FileNameLength（字节）· 58 u16 FileNameOffset（自记录头）· 60 WCHAR[] 名字。
/// </summary>
public static class UsnRecordParser
{
    public const int MinRecordBytes = 60;
    public const ushort SupportedMajorVersion = 2;

    /// <summary>
    /// 解析单条记录。缓冲区不足以容纳完整记录（跨块截断）/ 版本不符 ⇒ false（调用方按契约处理，
    /// **绝不静默吞掉半条记录**——截断须整条重新读取）。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> record, out UsnRecord parsed)
    {
        parsed = default;
        if (record.Length < MinRecordBytes)
        {
            return false;
        }

        uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (recordLength < MinRecordBytes || recordLength > (uint)record.Length)
        {
            return false;
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4));
        if (major != SupportedMajorVersion)
        {
            return false;
        }

        ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(8));
        ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(16));
        long usn = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(24));
        uint reason = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(40));
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(52));
        ushort nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(56));
        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(58));

        if (nameOffset + nameBytes > record.Length)
        {
            return false;
        }

        string name = Encoding.Unicode.GetString(record.Slice(nameOffset, nameBytes));
        parsed = new UsnRecord(usn, frn, parent, reason, attributes, name);
        return true;
    }

    /// <summary>
    /// FILE_ATTRIBUTE_* → <see cref="EntryFlags"/> 位映射（只取索引关心的三位；
    /// 其余属性位丢弃 —— flags 契约只有 bit0 目录 / bit1 隐藏 / bit2 系统）。
    /// </summary>
    public static byte MapFlags(uint fileAttributes)
    {
        byte flags = 0;
        if ((fileAttributes & 0x10) != 0)
        {
            flags |= EntryFlags.Directory;   // FILE_ATTRIBUTE_DIRECTORY
        }

        if ((fileAttributes & 0x02) != 0)
        {
            flags |= EntryFlags.Hidden;      // FILE_ATTRIBUTE_HIDDEN
        }

        if ((fileAttributes & 0x04) != 0)
        {
            flags |= EntryFlags.System;      // FILE_ATTRIBUTE_SYSTEM
        }

        return flags;
    }
}
