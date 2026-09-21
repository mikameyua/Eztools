using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Eztools.Core;

/// <summary>
/// Win32 互操作（ntdll / kernel32 / advapi32）。
///
/// 只放 Core 会用到的最小集合。句柄枚举的结构体布局照抄
/// PowerToys FileLocksmith 的 NtdllExtensions（MIT，已登记《参考来源.md》）——
/// 这类结构体错一个字段就是全盘错位，不要凭记忆重写。
/// </summary>
internal static unsafe class NativeInterop
{
    // ── kernel32 ──

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateHandle(
        IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint QueryDosDevice(string deviceName, char* targetPath, int max);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileW(
        string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr device, uint code, void* inBuffer, uint inSize,
        void* outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    // OpenProcess 权限位
    internal const uint ProcessDupHandle = 0x0040;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint GenericRead = 0x8000_0000;

    // CreateFileW
    internal const uint FileShareReadWriteDelete = 0x0000_0007;
    internal const uint OpenExisting = 3;

    // GetFileType 返回值：磁盘文件
    internal const uint FileTypeDisk = 3;

    // DuplicateHandle
    internal const uint DuplicateSameAccess = 0x0000_0002;

    // DeviceIoControl 的 FSCTL_ENUM_USN_DATA
    // = CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, 44, METHOD_NEITHER, FILE_ANY_ACCESS)
    // = (9 << 16) | (44 << 2) | 3 = 0x0009_00B3 —— ⚠️ METHOD 是 **NEITHER**，官方定义如此
    //   （MSYS2 winioctl.h L1504 + MS Learn，勿凭印象改回 BUFFERED）。
    // 📌 2026-09-20 三轮排查终审（她手测 M3 + python 提权矩阵实验）：
    //   readMft 恒空的控制码嫌疑全部排除——0x902CC / 0x900B0 实测 err=1（无效），
    //   0x900B3 恒空的真凶在 VolumePrimitives：HighUsn 传了 ulong.MaxValue
    //   （= 有符号 LONGLONG 的 **-1**）→ 过滤区间 [0,-1] 空集 → EOF(38)。
    //   HighUsn 改 long.MaxValue(0x7FFF_FFFF_FFFF_FFFF) 后单次返回 65400 字节真实记录。
    internal const uint FsctlEnumUsnData = 0x0009_00B3;

    internal const uint ErrorHandleEof = 38;

    // ── ntdll：句柄表枚举（布局来源 = PowerToys FileLocksmith NtdllExtensions.h）──

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int infoClass, void* info, int length, out int returnedLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryObject(
        IntPtr handle, int infoClass, void* info, int length, out int returnedLength);

    internal const int SystemExtendedHandleInformation = 64;
    internal const int ObjectNameInformation = 1;
    internal const int ObjectTypeInformation = 2;
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;

        public readonly string AsString() =>
            Buffer is null || Length == 0 ? string.Empty : new string(Buffer, 0, Length / sizeof(char));
    }

    /// <summary>SYSTEM_HANDLE_INFORMATION_EX：8 字节计数 + 变长条目数组（x64）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemHandleInformationEx
    {
        public IntPtr NumberOfHandles;
        public SystemHandleTableEntryInfoEx FirstEntry;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemHandleTableEntryInfoEx
    {
        public IntPtr Object;
        public IntPtr ProcessId;          // ULONG_PTR
        public IntPtr Handle;             // ULONG_PTR
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    /// <summary>OBJECT_TYPE_INFORMATION / OBJECT_NAME_INFORMATION 的头部都是 UNICODE_STRING，后续字段我们不用。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjectNameOrTypeHeader
    {
        public UnicodeString Name;
    }

    // ── ntdll：USN / MFT 枚举 ──

    /// <summary>MFT_ENUM_DATA_V0（FSCTL_ENUM_USN_DATA 的输入）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;
        public ulong LowUsn;
        public ulong HighUsn;
    }

    /// <summary>USN_RECORD_V2 头部（记录体从偏移 0 起，文件名按 FileNameOffset 定位）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UsnRecordV2
    {
        public uint RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public ulong Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public ushort FileNameLength;   // 字节数
        public ushort FileNameOffset;   // 相对记录起始的字节偏移
    }

    // ── advapi32：完整性级别校验 ──

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr token, int infoClass, void* info, uint length, out uint returnedLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint* GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern byte* GetSidSubAuthorityCount(IntPtr sid);

    /// <summary>经典 Win32 客户端模拟：在管道连接上调用后，当前线程以调用方令牌运行，配对 RevertToSelf。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImpersonateNamedPipeClient(IntPtr namedPipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RevertToSelf();

    /// <summary>取管道对端客户端的真实 PID —— 比客户端自报可信（进审计与授权都可用）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientPid);

    internal const int TokenIntegrityLevel = 25;
    internal const uint TokenQuery = 0x0008;

    // 安全强制性级别（SECURITY_MANDATORY_*_RID）
    internal const uint IntegrityLow = 0x1000;
    internal const uint IntegrityMedium = 0x2000;
    internal const uint IntegrityHigh = 0x3000;
    internal const uint IntegritySystem = 0x4000;

    /// <summary>读取令牌完整性级别 RID；读不到返回 0（调用方按"最低"处理）。</summary>
    internal static uint GetTokenIntegrityLevel(IntPtr token)
    {
        if (!GetTokenInformation(token, TokenIntegrityLevel, null, 0, out var needed) && needed == 0)
        {
            return 0;
        }

        var buffer = stackalloc byte[(int)needed];
        if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
        {
            return 0;
        }

        // TOKEN_MANDATORY_LABEL.Label = SID_AND_ATTRIBUTES { PSID Sid; ULONG Attributes; }
        var sid = *(IntPtr*)buffer;
        var count = *GetSidSubAuthorityCount(sid);
        var rid = GetSidSubAuthority(sid, (uint)(count - 1));   // 最后一个子授权才是 RID
        return *rid;
    }

    /// <summary>Win32LastError → 异常（消息带 Win32 语义，便于排障）。</summary>
    internal static Win32Exception LastError(string what, int error) =>
        new($"{what} 失败（Win32 错误 {error}）");
}

/// <summary>原语执行失败（带 JSON-RPC 错误码，Core 与宿主两侧共用同一组码）。</summary>
public sealed class PrimitiveException : Exception
{
    public PrimitiveException(int code, string message) : base(message) => Code = code;

    public int Code { get; }
}

/// <summary>Win32 错误异常。</summary>
public sealed class Win32Exception : Exception
{
    public Win32Exception(string message) : base(message) { }
}
