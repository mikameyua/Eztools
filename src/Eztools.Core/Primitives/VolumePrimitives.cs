// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Core.Primitives;

/// <summary>
/// 卷原语（<c>volume.enumerate</c> / <c>volume.readMft</c>）。
///
/// readMft 用 <c>DeviceIoControl(FSCTL_ENUM_USN_DATA)</c> 遍历 USN 记录
/// （即按 MFT 记录枚举文件名，Everything/WizTree 的同款入口）。
/// 需要**管理员令牌**打开卷设备；未提权时返回结构化 <c>ElevationRequired</c>，
/// 而不是让调用方等到 Win32 错误 —— 这条是验收脚本的可断言负向路径。
///
/// 批量上限（≤5000 条/次）+ 游标续传是刻意设计：不提供"一次读全盘"的通道，
/// 防单次调用长时间占用 Core、防审计日志被灌爆（P3 方案 §5）。
/// </summary>
public static unsafe class VolumePrimitives
{
    private const uint OutputBufferSize = 0x1_0000;   // 64 KB / 次
    private const int DefaultMaxRecords = 1000;
    private const int HardMaxRecords = 5000;
    private const ulong FrnMask = 0x0000_FFFF_FFFF_FFFF;   // FRN 高 16 位是序号，剥掉

    public static JsonNode Enumerate()
    {
        var volumes = new JsonArray();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.NoRootDirectory)
            {
                continue;
            }

            var kernelDevice = QueryKernelDevice(drive.Name);
            volumes.Add(new JsonObject
            {
                ["letter"] = drive.Name,
                ["type"] = drive.DriveType.ToString(),
                ["label"] = SafeLabel(drive),
                ["fs"] = SafeFileSystem(drive),
                ["kernelDevice"] = kernelDevice,
                ["ready"] = drive.IsReady,
            });
        }

        return new JsonObject
        {
            ["count"] = volumes.Count,
            ["elevated"] = IsElevated(),
            ["volumes"] = volumes,
        };
    }

    public static JsonNode ReadMft(string volume, long? cursor, long? maxRecords)
    {
        if (!IsElevated())
        {
            throw new PrimitiveException(
                PrimitiveErrorCodes.ElevationRequired,
                "volume.readMft 需要提权的 Core（管理员令牌）。"
                + "用 `ezt core start --elevate` 或计划任务形态启动后重试。");
        }

        var limit = Math.Clamp(maxRecords ?? DefaultMaxRecords, 1, HardMaxRecords);
        var volPath = NormalizeVolume(volume);

        var handle = NativeInterop.CreateFileW(
            volPath,
            NativeInterop.GenericRead,
            NativeInterop.FileShareReadWriteDelete,
            IntPtr.Zero,
            NativeInterop.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == (IntPtr)(-1))
        {
            var error = Marshal.GetLastWin32Error();
            throw error switch
            {
                5 => new PrimitiveException(
                    PrimitiveErrorCodes.ElevationRequired,
                    $"打开 {volPath} 被拒绝（访问被拒）—— 需要 Core 以管理员运行"),
                _ => new PrimitiveException(
                    RpcErrorCodes.InternalError, $"打开 {volPath} 失败（Win32 错误 {error}）"),
            };
        }

        try
        {
            var records = new JsonArray();
            var inCursor = (ulong)(cursor ?? 0);
            var nextCursor = inCursor;
            var done = false;

            var output = new byte[OutputBufferSize];

            // 输出缓冲区必须在整个解析期间 fixed（ParseRecords 用裸指针读它）
            fixed (byte* outPtr = output)
            {
                while (records.Count < limit)
                {
                    var prevCursor = nextCursor;

                    var mft = new NativeInterop.MftEnumDataV0
                    {
                        StartFileReferenceNumber = nextCursor,
                        LowUsn = 0,
                        // 🔴 必须是 long.MaxValue —— USN 是有符号 LONGLONG：
                        // ulong.MaxValue = 有符号 -1 → 过滤区间 [0,-1] 空集 → EOF(38)，恒空结果。
                        HighUsn = (ulong)long.MaxValue,
                    };

                    uint returned;
                    bool ok;
                    // mft 是栈上局部变量，直接取址即可（fixed 只用于托管可移动对象）
                    ok = NativeInterop.DeviceIoControl(
                        handle, NativeInterop.FsctlEnumUsnData,
                        &mft, (uint)sizeof(NativeInterop.MftEnumDataV0),
                        outPtr, (uint)output.Length, out returned, IntPtr.Zero);

                    if (!ok)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == NativeInterop.ErrorHandleEof)
                        {
                            done = true;   // MFT 遍历到尾
                            break;
                        }

                        throw new PrimitiveException(
                            RpcErrorCodes.InternalError,
                            $"FSCTL_ENUM_USN_DATA 失败（Win32 错误 {error}，cursor={nextCursor}）");
                    }

                    if (returned < sizeof(ulong))
                    {
                        done = true;   // 成功但连游标都没有 —— 无更多记录
                        break;
                    }

                    // 防御：NEITHER 下驱动不该写出超过缓冲区的长度，钳制后再交给指针解析
                    var valid = Math.Min(returned, (uint)output.Length);
                    ParseRecords(outPtr, valid, records, inCursor, ref nextCursor, (int)limit);

                    // 防死循环守卫：游标零推进 = 系统无法给出下一位置（MFT 尾/quirk），按结束处理。
                    // 正常路径游标必然推进：缓冲区全消费时 = 系统 buffer 头；截断时 = 最后一条记录的 FRN，
                    // 下一轮 FSCTL 从它重启并消化重放首条，系统 buffer 头必然不同。
                    if (nextCursor == prevCursor)
                    {
                        done = true;
                        break;
                    }
                }
            }

            return new JsonObject
            {
                ["volume"] = volPath,
                ["cursor"] = (JsonNode)(long)Math.Min(nextCursor, long.MaxValue),
                ["done"] = done,
                ["count"] = records.Count,
                ["records"] = records,
            };
        }
        finally
        {
            NativeInterop.CloseHandle(handle);
        }
    }

    // USN_RECORD_V2 头部大小（不含文件名）：字段到 FileNameOffset 止 = 60 字节。
    // C# struct 因对齐补到 64，做边界检查会漏掉缓冲区末尾恰好只差 2~4 字节的记录。
    private const int UsnRecordV2HeaderSize = 60;

    private static void ParseRecords(
        byte* basePtr, uint returned, JsonArray records,
        ulong inCursor, ref ulong nextCursor, int limit)
    {
        // 前 8 字节 = 系统给出的续传游标（指向整块缓冲区的下一位置）
        var bufferCursor = *(ulong*)basePtr;

        // 🔴 解析起点 = 8（跳过游标），不是 60 —— 60 只是"单条记录头"的边界检查大小。
        //    起点写 60 会从第一条记录体内开始瞎读 → 名字错位、frn 重复、链漂移（2026-09-20 手测抓到）。
        var offset = sizeof(ulong);
        var lastFrn = 0UL;          // 本批最后一条**已返回**记录的完整 FRN
        var truncByLimit = false;   // 是否因 maxRecords 在缓冲区中途停下
        var consumedAny = false;

        while (offset + UsnRecordV2HeaderSize <= returned)
        {
            var record = (NativeInterop.UsnRecordV2*)(basePtr + offset);
            // 合法 USN_RECORD_V2 至少 60 字节头；0 或过短 = 布局异常/垃圾区，停止（防漂移+死循环）
            if (record->RecordLength < UsnRecordV2HeaderSize)
            {
                break;
            }

            // 重放消化：上一批因 maxRecords 截断时，游标 = 该批最后一条已返回记录的 FRN，
            // 本批从该记录重新开始。首条 FRN 与入参 cursor 相同的记录上一批已返回过，跳过。
            // （对"系统游标是否已 +1"两种语义都成立：有重放则消化，无重放则首条 FRN 不相等、不触发。）
            var isReplay = !consumedAny && inCursor != 0 && record->FileReferenceNumber == inCursor;

            if (!isReplay)
            {
                var nameLength = record->FileNameLength / sizeof(char);
                if (records.Count >= limit)
                {
                    truncByLimit = true;
                    break;   // 不消费这条 —— 游标必须停在它之前
                }

                if (nameLength > 0)
                {
                    var name = new string(
                        (char*)(basePtr + offset + record->FileNameOffset), 0, nameLength);

                    records.Add(new JsonObject
                    {
                        ["frn"] = (JsonNode)(long)(record->FileReferenceNumber & FrnMask),
                        ["parent"] = (JsonNode)(long)(record->ParentFileReferenceNumber & FrnMask),
                        ["name"] = name,
                    });
                    lastFrn = record->FileReferenceNumber;
                }

                consumedAny = true;
            }

            offset += (int)record->RecordLength;
        }

        // 因 maxRecords 截断：游标 = 最后一条已返回记录的 FRN（下一批从它开始，重放首条被消化）。
        // 缓冲区全消费：游标 = 系统给出的缓冲区边界（语义无歧义，无漏无重）。
        nextCursor = truncByLimit ? lastFrn : bufferCursor;
    }

    // ── 帮手 ──

    /// <summary>提权校验 + 开卷句柄（GENERIC_READ；readMft/queryJournal/readUsn 共用；写原语传 writeAccess 加 GENERIC_WRITE）。</summary>
    private static IntPtr OpenVolumeElevated(string volume, string primitiveName, bool writeAccess = false)
    {
        if (!IsElevated())
        {
            throw new PrimitiveException(
                PrimitiveErrorCodes.ElevationRequired,
                $"{primitiveName} 需要提权的 Core（管理员令牌）。"
                + "用 `ezt core start --elevate` 或计划任务形态启动后重试。");
        }

        var volPath = NormalizeVolume(volume);
        var handle = NativeInterop.CreateFileW(
            volPath,
            NativeInterop.GenericRead | (writeAccess ? NativeInterop.GenericWrite : 0),
            NativeInterop.FileShareReadWriteDelete,
            IntPtr.Zero,
            NativeInterop.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == (IntPtr)(-1))
        {
            var error = Marshal.GetLastWin32Error();
            throw error switch
            {
                5 => new PrimitiveException(
                    PrimitiveErrorCodes.ElevationRequired,
                    $"打开 {volPath} 被拒绝（访问被拒）—— 需要 Core 以管理员运行"),
                _ => new PrimitiveException(
                    RpcErrorCodes.InternalError, $"打开 {volPath} 失败（Win32 错误 {error}）"),
            };
        }

        return handle;
    }

    /// <summary>journal 专用 Win32 错误 → 结构化 JournalUnavailable（1178/1179/1181）；其余原样 InternalError。</summary>
    private static PrimitiveException JournalError(string op, int error)
    {
        if (error is NativeInterop.ErrorJournalDeleteInProgress
            or NativeInterop.ErrorJournalNotActive
            or NativeInterop.ErrorJournalEntryDeleted)
        {
            return new PrimitiveException(
                PrimitiveErrorCodes.JournalUnavailable,
                $"{op} 失败：journal 不可用（Win32 {error}）");
        }

        return new PrimitiveException(RpcErrorCodes.InternalError, $"{op} 失败（Win32 错误 {error}）");
    }

    /// <summary>
    /// 查 USN journal 元信息（W3-c-3 启动对账第一步，设计方案 §4.2）。
    /// journal 未开启/删除中 ⇒ 结构化 JournalUnavailable（-32021），调用方按"静态快照"处理。
    /// </summary>
    public static JsonNode QueryJournal(string volume)
    {
        var volPath = NormalizeVolume(volume);
        var handle = OpenVolumeElevated(volPath, "volume.queryJournal");
        try
        {
            var data = new NativeInterop.UsnJournalDataV0();
            uint returned;
            bool ok;
            unsafe
            {
                ok = NativeInterop.DeviceIoControl(
                    handle, NativeInterop.FsctlQueryUsnJournal,
                    null, 0,
                    &data, (uint)sizeof(NativeInterop.UsnJournalDataV0),
                    out returned, IntPtr.Zero);
            }

            if (!ok)
            {
                throw JournalError("FSCTL_QUERY_USN_JOURNAL", Marshal.GetLastWin32Error());
            }

            return new JsonObject
            {
                ["volume"] = volPath,
                ["journalId"] = (JsonNode)(long)data.UsnJournalId,
                ["firstUsn"] = (JsonNode)(long)data.FirstUsn,
                ["nextUsn"] = (JsonNode)(long)data.NextUsn,
                ["lowestValidUsn"] = (JsonNode)(long)data.LowestValidUsn,
            };
        }
        finally
        {
            NativeInterop.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 读 USN 变更流一批（W3-c-1）。BytesToWaitFor=0 ⇒ 立即返回（不阻塞不忙等）；
    /// maxBytes 钳制 [4KB, 1MB]（同 readMft 的"防单次调用灌爆"口径）。reason/attributes
    /// 原样透传（位掩码，由调用方做位与判断）；frn/parent 与 readMft 同款剥高 16 位序号。
    /// </summary>
    public static JsonNode ReadUsn(string volume, long journalId, long fromUsn, long? maxBytes)
    {
        if (journalId <= 0)
        {
            throw new PrimitiveException(
                RpcErrorCodes.InvalidParams, $"journalId 必须为正整数（收到 {journalId}）");
        }

        var volPath = NormalizeVolume(volume);
        var handle = OpenVolumeElevated(volPath, "volume.readUsn");
        try
        {
            var req = new NativeInterop.ReadUsnJournalDataV1
            {
                StartUsn = (ulong)fromUsn,
                ReasonMask = 0xFFFF_FFFF,   // 全收；过滤是调用方的事
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,         // 0 = 读到当前尾立即返回
                UsnJournalId = (ulong)journalId,
                MinMajorVersion = 2,
                MaxMajorVersion = 2,
            };

            var output = new byte[(int)Math.Clamp(maxBytes ?? 0x1_0000, 0x1000, 0x10_0000)];
            var records = new JsonArray();
            long nextUsn;

            fixed (byte* outPtr = output)
            {
                uint returned;
                bool ok;
                unsafe
                {
                    ok = NativeInterop.DeviceIoControl(
                        handle, NativeInterop.FsctlReadUsnJournal,
                        &req, (uint)sizeof(NativeInterop.ReadUsnJournalDataV1),
                        outPtr, (uint)output.Length,
                        out returned, IntPtr.Zero);
                }

                if (!ok)
                {
                    throw JournalError("FSCTL_READ_USN_JOURNAL", Marshal.GetLastWin32Error());
                }

                // 输出 = 8 B NextUsn + USN_RECORD_V2 逐条紧排
                if (returned < sizeof(ulong))
                {
                    throw new PrimitiveException(
                        RpcErrorCodes.InternalError,
                        $"FSCTL_READ_USN_JOURNAL 返回 {returned} 字节（不足 8 字节游标）");
                }

                nextUsn = *(long*)outPtr;
                var offset = sizeof(ulong);
                while (offset + UsnRecordV2HeaderSize <= returned)
                {
                    var record = (NativeInterop.UsnRecordV2*)(outPtr + offset);
                    if (record->RecordLength < UsnRecordV2HeaderSize
                        || offset + record->RecordLength > returned)
                    {
                        break;   // 过短/越界 = 布局异常，停止（防漂移，同 ParseRecords）
                    }

                    var nameLength = record->FileNameLength / sizeof(char);
                    if (nameLength > 0)
                    {
                        var name = new string(
                            (char*)(outPtr + offset + record->FileNameOffset), 0, nameLength);
                        records.Add(new JsonObject
                        {
                            ["usn"] = (JsonNode)(long)record->Usn,
                            ["frn"] = (JsonNode)(long)(record->FileReferenceNumber & FrnMask),
                            ["parent"] = (JsonNode)(long)(record->ParentFileReferenceNumber & FrnMask),
                            ["reason"] = (JsonNode)(long)record->Reason,
                            ["attributes"] = (JsonNode)(long)record->FileAttributes,
                            ["name"] = name,
                        });
                    }

                    offset += (int)record->RecordLength;
                }
            }

            return new JsonObject
            {
                ["volume"] = volPath,
                ["nextUsn"] = (JsonNode)nextUsn,
                ["count"] = records.Count,
                ["records"] = records,
            };
        }
        finally
        {
            NativeInterop.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 心跳：对句柄强制产生一条 CLOSE 记录（W3-c-1）。
    /// ⚠️ API 契约（winioctl.h 实证修正，2026-09-25）：
    /// ① FSCTL_WRITE_USN_CLOSE_RECORD **不接收输入缓冲区**（lpInBuffer=NULL, nInBufferSize=0）
    ///   ——初版把 journalId 塞进输入缓冲是凭空发明（err=87）；
    /// ② 该 FSCTL **只支持文件/目录句柄**（OSR + 旧版 MSDN "handle to the file or directory"；
    ///   现代 learn 文档参数表 "handle to volume" 与实测矛盾——卷句柄上 err=1 INVALID_FUNCTION）。
    /// 本原语按卷句柄形态实现 = 恒失败（结构化 err=1 经 JournalError 透传），
    /// 心跳按 S9 偏差显式降级（journal 删除风险由 ReadUsn NotActive 分支覆盖，不引入每卷标记文件）。
    /// </summary>
    public static JsonNode WriteUsnClose(string volume)
    {
        var volPath = NormalizeVolume(volume);
        var handle = OpenVolumeElevated(volPath, "volume.writeUsnClose", writeAccess: true);
        try
        {
            ulong usnWritten;
            uint returned;
            bool ok;
            unsafe
            {
                ok = NativeInterop.DeviceIoControl(
                    handle, NativeInterop.FsctlWriteUsnCloseRecord,
                    null, 0,
                    &usnWritten, sizeof(ulong),
                    out returned, IntPtr.Zero);
            }

            if (!ok)
            {
                throw JournalError("FSCTL_WRITE_USN_CLOSE_RECORD", Marshal.GetLastWin32Error());
            }

            return new JsonObject
            {
                ["volume"] = volPath,
                ["ok"] = true,
                ["usn"] = (JsonNode)(long)usnWritten,
            };
        }
        finally
        {
            NativeInterop.CloseHandle(handle);
        }
    }

    /// <summary>"C" / "C:" / "C:\" / "\\.\C:" → "\\.\C:"。</summary>
    internal static string NormalizeVolume(string volume)
    {
        var value = volume.Trim().TrimEnd('\\', '/');

        // 接受 `\\.\X:` 形式（调用方与自检用例都在用），但**只接受单盘符**。
        // ⚠️ 2026-09-29 安全面专项审 RI-7：原先这里对 `\\.\` 前缀**直接 `return value`**，
        //    等于放行 `\\.\PhysicalDrive0` 等**任意设备路径** —— 而卷级 IOCTL 只需要盘符句柄，
        //    放宽没有任何收益，只是把参数面扩大（`writeUsnClose` 还带 `GenericWrite`）。
        //    已核实全仓无消费方依赖更宽的形式（`volume.enumerate` 的 `kernelDevice` 没有回传调用点，
        //    唯一的 `\\.\F:` 用例来自 `SelfTestCommand` 的 `OrderVolumes`，仍是单盘符）。
        if (value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..].TrimEnd(':');
            if (value.Length != 1 || !char.IsAsciiLetter(value[0]))
            {
                throw new PrimitiveException(
                    RpcErrorCodes.InvalidParams,
                    $"volume 参数只接受盘符或 \\\\.\\<盘符>: 形式（收到 '{volume}'）");
            }

            return @"\\.\" + char.ToUpperInvariant(value[0]) + ":";
        }

        value = value.TrimEnd(':');
        if (value.Length != 1 || !char.IsAsciiLetter(value[0]))
        {
            throw new PrimitiveException(
                RpcErrorCodes.InvalidParams,
                $"volume 参数必须是单个盘符（收到 '{volume}'）");
        }

        return @"\\.\" + char.ToUpperInvariant(value[0]) + ":";
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch
        {
            return "";
        }
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string QueryKernelDevice(string driveName)
    {
        var name = driveName.TrimEnd('\\', '/');
        var target = new char[1024];
        uint len;
        fixed (char* pName = name)
        fixed (char* pTarget = target)
        {
            len = NativeInterop.QueryDosDevice(name, pTarget, target.Length);
        }

        return len > 0 ? new string(target, 0, (int)Math.Min(len, target.Length)).TrimEnd('\0') : "";
    }

    private static string SafeFileSystem(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch
        {
            return "";
        }
    }
}
