// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eztools.Host.Processes;

/// <summary>
/// 工具进程的 Job Object 归属（P2 尾巴："Job Object 资源限制"）。
///
/// 当前唯一启用的约束 = <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>：
/// 宿主进程退出（无论优雅与否）时句柄关闭，所有仍归属本 Job 的工具进程被内核一并终止 ——
/// 消灭"宿主崩溃后 python 孤儿进程"这一类残留。
///
/// ⚠️ 关键决策（2026-09-20）：
/// 1. **只做 kill-on-close，不做内存/CPU 上限**——现有工具无资源约束需求（YAGNI），
///    且内存上限需要动 `tool.json` schema + Contracts 校验器，扩大改动面；
///    将来加限制时在 <see cref="SetKillOnClose"/> 同处扩展 JobObjectExtendedLimitInformation。
/// 2. **失败不阻断**：Assign 失败只记警告（Job 是增益而非关键路径，不能因为它让调用失败）。
/// 3. Windows 8+ 支持嵌套 Job——宿主自身已在某个 Job 里（如 CI/调试器）不受影响。
/// 4. **`resident` 生命周期落地时需重新评估**：常驻工具不应随宿主退出而死，
///    届时改为按生命周期分 Job（transient 归宿主 Job，resident 独立 Job 或 BREAKAWAY）。
/// </summary>
internal static class ToolJobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>归还 Job 句柄（宿主死亡时的收割由 OS 关闭句柄触发，不依赖此调用）。</summary>
    public static void Release(IntPtr job)
    {
        if (job != IntPtr.Zero)
        {
            CloseHandle(job);
        }
    }

    /// <summary>
    /// 创建 kill-on-close 的 Job 并把 <paramref name="process"/> 挂进去。
    /// <paramref name="attach"/> = false（<c>resident</c> 工具）时直接返回 Zero：
    /// 常驻进程不能随宿主死亡而被收割（宿主崩溃/强杀场景下 resident 必须存活）。
    /// 返回 Job 句柄（宿主进程生命周期内持有，进程退出由 OS 关闭触发收割）；
    /// 失败返回 <see cref="IntPtr.Zero"/> 并已记日志（调用方继续，不阻断）。
    /// </summary>
    public static IntPtr Assign(Process process, HostLog log, bool attach = true)
    {
        if (!attach)
        {
            log.Info($"进程 {process.Id} 为 resident 生命周期，不纳入 kill-on-close Job（宿主退出后需存活）");
            return IntPtr.Zero;
        }

        var job = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                log.Warn($"Job Object 创建失败（Win32 错误 {Marshal.GetLastWin32Error()}），工具进程不纳入 Job 归属");
                return IntPtr.Zero;
            }

            var limit = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limit.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(
                    job, JobObjectExtendedLimitInformation, ref limit,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                log.Warn($"Job Object 限制设置失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                CloseHandle(job);
                return IntPtr.Zero;
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                log.Warn($"进程 {process.Id} 挂入 Job 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                CloseHandle(job);
                return IntPtr.Zero;
            }

            log.Info($"进程 {process.Id} 已纳入 kill-on-close Job（宿主退出时一并终止）");
            return job;
        }
        catch (Exception ex)
        {
            // Job 归属是增益：任何异常都不应影响工具调用本身
            log.Warn($"Job Object 归属异常（不阻断）：{ex.Message}");
            if (job != IntPtr.Zero)
            {
                CloseHandle(job);
            }
            return IntPtr.Zero;
        }
    }
}
