// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Core.Primitives;

/// <summary>
/// 句柄枚举原语（<c>handles.enumerate</c>）。
///
/// 算法整体移植自 PowerToys FileLocksmith 的 <c>NtdllExtensions.cpp</c>（MIT）：
/// <list type="number">
/// <item><c>NtQuerySystemInformation(SystemExtendedHandleInformation)</c> 取全系统句柄表；</item>
/// <item>逐条 <c>OpenProcess(PROCESS_DUP_HANDLE)</c> + <c>DuplicateHandle</c> 把别的进程的句柄复制进本进程；</item>
/// <item><c>NtQueryObject</c> 查类型与名称，只保留磁盘文件句柄；</item>
/// <item>内核路径（<c>\Device\HarddiskVolume3\...</c>）经 <c>QueryDosDevice</c> 的反向映射转回盘符路径。</item>
/// </list>
///
/// ⚠️ <c>NtQueryObject</c> 对个别句柄（命名管道/等待中的句柄）会<b>永久挂起</b>，且没有带超时的替代 API。
/// PowerToys 的对策是工作线程 + 超时后 <c>TerminateThread</c>（它自己也承认 unsafe）。
/// 这里的对策等价但温和：工作线程用<b>后台线程</b>，超时只放弃（不杀）；
/// 泄漏上界 = 挂起句柄数，进程退出时自动回收。这是已知取舍（P3 方案 §9）。
///
/// <b>并行化（2026-09-21，清单 C2）</b>：原实现是单线程遍历全表，10 万+ 条目逐个
/// <c>OpenProcess</c>/<c>DuplicateHandle</c>，实测 1~2 分钟。现改为<b>按 pid 分桶 + 多 worker</b>：
/// 句柄表天然按 pid 聚集，分桶后 <c>OpenProcess</c> 由"每句柄一次"降为"每 pid 一次"，
/// 不同 pid 的桶并行处理（同 pid 必须串行 —— 它们共用一个进程句柄）。
/// 顺带把挂起隔离粒度从"整轮扫描"细化到"单个 pid 桶"。
/// </summary>
public static unsafe class HandlePrimitives
{
    public sealed record HandleEntry(uint Pid, string Type, string Path);

    private const int DefaultBufferSize = 0x0010_0000;   // 1 MB 起步
    private const int MaxBufferSize = 0x1000_0000;       // 256 MB 封顶

    /// <summary>枚举文件句柄。pidFilter 为空 = 全系统；返回条目按遍历顺序。</summary>
    public static List<HandleEntry> Enumerate(ulong? pidFilter, CancellationToken ct)
    {
        var buffer = QueryHandleTable();
        if (buffer is null)
        {
            throw new PrimitiveException(
                RpcErrorCodes.InternalError, "NtQuerySystemInformation 无法返回句柄表");
        }

        var info = (NativeInterop.SystemHandleInformationEx*)buffer;
        var count = (long)info->NumberOfHandles;
        var first = (byte*)&info->FirstEntry;   // 条目统一按 40 字节步进，字段按布局偏移读取

        // ── 并行化（2026-09-21，清单 C2）──
        //
        // 做法：把句柄表按**下标区间**切成 N 段，每段跑一个**与单线程版完全相同**的 worker
        // （各自独立的游标 + 独立看门狗）。为什么不按 pid 分桶：分桶要先扫全表建字典，
        // 而且一旦某个 pid 的桶挂住，"整桶放弃"会丢掉该进程的**全部**句柄；
        // 按下标切段则天然共享原实现的"跳过挂起的那一个句柄、从下一条继续"语义，
        // 挂起的影响面只有 1 个句柄 —— 这是原实现已经验证过的行为，不改它最稳。
        //
        // 并发度：CPU 核数 clamp 到 [2, 8]。这些工作绝大部分时间在内核里等（OpenProcess /
        // NtQueryObject），不是 CPU 密集；开太多线程只会互相争抢内核锁，边际收益递减。
        var workerCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
        if (workerCount > count)
        {
            workerCount = (int)Math.Max(1, count);
        }

        var results = new List<HandleEntry>();
        var segment = (count + workerCount - 1) / workerCount;   // 向上取整，保证覆盖全部
        var supervisors = new List<Thread>(workerCount);

        for (var w = 0; w < workerCount; w++)
        {
            var start = w * (long)segment;
            var end = Math.Min(start + segment, count);
            if (start >= end)
            {
                break;
            }

            supervisors.Add(StartRangeSupervisor(start, end, first, pidFilter, results, ct));
        }

        foreach (var supervisor in supervisors)
        {
            // 每个区间由一个"监督线程"负责跑到区间末尾（内部含看门狗重开逻辑），
            // 所以正常情况一定会自己结束；它们是后台线程，即使异常退出也不会拖住 Core。
            supervisor.Join();
        }

        return results;
    }

    /// <summary>
    /// 启动一个区间的监督线程：它反复启动/重启区间 worker，直到区间被扫完。
    ///
    /// <b>为什么必须是"监督线程 + 可重启 worker"这套结构</b>（而不是"一个 worker 自己跳过错过的句柄"）：
    /// <c>NtQueryObject</c> 挂起时，**挂住的是执行那个调用的线程，且它无法自救** ——
    /// 没有任何 API 能给它自己的调用加超时。唯一可行的办法是让**另一个线程**看到"游标不动了"，
    /// 把游标推过挂起点，再开一个新线程从新位置继续（旧线程就让它永远挂着，反正不让它阻止进程退出）。
    /// 这正是原单线程实现的做法，这里逐行保留，只是把它复制到 N 个互不重叠的下标区间上。
    /// </summary>
    private static Thread StartRangeSupervisor(
        long start,
        long end,
        byte* first,
        ulong? pidFilter,
        List<HandleEntry> results,
        CancellationToken ct)
    {
        var supervisor = new Thread(() =>
        {
            var state = new ScanState(start, end);
            var worker = StartRangeWorker(state, first, pidFilter, results);

            try
            {
                while (state.Cursor < end && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(WatchdogIntervalMs);

                    if (!state.MadeProgress())
                    {
                        state.SkipOne();   // 跳过挂起的那一个句柄
                        worker = StartRangeWorker(state, first, pidFilter, results);
                    }
                }
            }
            catch
            {
                // 监督线程绝不外抛
            }
        })
        {
            IsBackground = true,   // 挂起的遗留线程不阻止进程退出
            Name = "ezt-handle-watch",
        };

        supervisor.Start();
        return supervisor;
    }

    /// <summary>
    /// 区间游标（一个区间一个实例，不与其它区间共享）。
    /// 工作线程递增 / 监督线程读取 —— 用 <see cref="Interlocked"/> 保证跨线程可见性与原子性
    /// （原因与原实现相同：看门狗放弃旧线程与旧线程"其实还活着"之间存在窗口，两个工作线程可能短暂并发）。
    /// </summary>
    private sealed class ScanState
    {
        private readonly long _end;
        private long _cursor;
        private long _lastProgress;

        internal ScanState(long start, long end)
        {
            _cursor = start;
            _lastProgress = start;
            _end = end;
        }

        internal long Cursor => Interlocked.Read(ref _cursor);

        /// <summary>本区间的上界（不含）。</summary>
        internal long End => _end;

        /// <summary>工作线程主循环用：取下一个下标（越界即表示本区间完成）。</summary>
        internal long Next() => Interlocked.Increment(ref _cursor) - 1;

        internal bool MadeProgress() =>
            Interlocked.Read(ref _cursor) != Interlocked.Exchange(ref _lastProgress, Cursor);

        internal void SkipOne() => Interlocked.Increment(ref _cursor);
    }

    /// <summary>
    /// 区间工作线程：从游标取下标逐个处理，直到越界。
    /// 算法与并行化之前的单线程版本逐行等价；区别仅是游标有上界、且每个区间独占一个实例。
    /// </summary>
    private static Thread StartRangeWorker(
        ScanState state,
        byte* first,
        ulong? pidFilter,
        List<HandleEntry> results)
    {
        var thread = new Thread(() =>
        {
            var processHandles = new Dictionary<ulong, IntPtr>();

            try
            {
                for (var i = state.Next(); i < state.End; i = state.Next())
                {
                    var entry = first + i * 40;   // 条目步进恒为 40 字节
                    var pid = ReadPid(entry);

                    if (pidFilter is { } filter && pid != filter)
                    {
                        continue;
                    }

                    if (!processHandles.TryGetValue(pid, out var process))
                    {
                        process = NativeInterop.OpenProcess(NativeInterop.ProcessDupHandle, false, (uint)pid);
                        if (process == IntPtr.Zero)
                        {
                            processHandles[pid] = IntPtr.Zero;   // 记住"打不开"，别反复试
                            continue;
                        }

                        processHandles[pid] = process;
                    }

                    if (process == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (!NativeInterop.DuplicateHandle(
                            process, (IntPtr)ReadHandle(entry), NativeInterop.GetCurrentProcess(),
                            out var copy, 0, false, NativeInterop.DuplicateSameAccess))
                    {
                        continue;   // 打不开的句柄（退出中的进程等），跳过
                    }

                    try
                    {
                        InspectHandle(copy, (uint)pid, results);
                    }
                    finally
                    {
                        NativeInterop.CloseHandle(copy);
                    }
                }
            }
            catch
            {
                // 扫描线程绝不外抛：结果以已收集到的为准
            }
            finally
            {
                foreach (var handle in processHandles.Values)
                {
                    if (handle != IntPtr.Zero)
                    {
                        NativeInterop.CloseHandle(handle);
                    }
                }
            }
        })
        {
            IsBackground = true,   // 挂起的遗留线程不阻止进程退出
            Name = "ezt-handle-scan",
        };

        thread.Start();
        return thread;
    }

    private const int WatchdogIntervalMs = 200;

    /// <summary>
    /// 一个桶扫描 worker：独占一个后台线程，从共享队列取 pid 桶处理。
    ///
    /// 进度语义：<see cref="_progress"/> 只在**成功处理完一个句柄**或**取到一个新桶**时递增。
    /// 因此"长时间不前进"精确对应"NtQueryObject 挂在某个句柄上"。
    /// </summary>
    private sealed class BucketWorker
    {
        private readonly ConcurrentQueue<KeyValuePair<ulong, List<uint>>> _queue;
        private readonly List<HandleEntry> _results;
        private readonly CancellationToken _ct;
        private Thread? _thread;

        private long _progress;
        private long _lastSeen;
        private volatile bool _finished;

        // volatile：主线程写、工作线程读 —— 必须保证可见性，否则"放弃"标志可能迟迟不生效
        private volatile bool _abandonCurrent;

        internal BucketWorker(
            ConcurrentQueue<KeyValuePair<ulong, List<uint>>> queue,
            List<HandleEntry> results,
            CancellationToken ct)
        {
            _queue = queue;
            _results = results;
            _ct = ct;
        }

        internal bool IsFinished => _finished;

        internal void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,   // 挂起的遗留线程不阻止进程退出
                Name = "ezt-handle-scan",
            };
            _thread.Start();
        }

        /// <summary>主线程调用：距上次进度超过阈值即视为挂起。</summary>
        internal bool Stalled()
        {
            var seen = Interlocked.Read(ref _lastSeen);
            var now = Interlocked.Read(ref _progress);
            return now == seen && now > 0;
        }

        /// <summary>主线程调用：让工作线程放弃当前 pid 桶。</summary>
        internal void AbandonCurrent() => _abandonCurrent = true;

        private void Run()
        {
            try
            {
                while (!_ct.IsCancellationRequested
                       && _queue.TryDequeue(out var bucket))
                {
                    // 取到新桶 = 前进了一次（保证刚取到桶还没处理时不误判为挂起）
                    Tick();

                    var (pid, handles) = bucket;
                    ScanOneProcess(pid, handles);
                }
            }
            catch
            {
                // 扫描线程绝不外抛：结果以已收集到的为准
            }
            finally
            {
                _finished = true;
            }
        }

        private void ScanOneProcess(ulong pid, List<uint> handles)
        {
            var process = NativeInterop.OpenProcess(
                NativeInterop.ProcessDupHandle, false, (uint)pid);
            if (process == IntPtr.Zero)
            {
                return;   // 打不开的进程（权限/已退出）—— 整桶跳过，不逐句柄重试
            }

            try
            {
                foreach (var raw in handles)
                {
                    if (_abandonCurrent)
                    {
                        _abandonCurrent = false;
                        return;   // 放弃本桶剩余句柄（挂起对策），交给下一个 pid
                    }

                    _ct.ThrowIfCancellationRequested();

                    if (!NativeInterop.DuplicateHandle(
                            process, (IntPtr)raw, NativeInterop.GetCurrentProcess(),
                            out var copy, 0, false, NativeInterop.DuplicateSameAccess))
                    {
                        // 打不开的句柄（退出中的进程等）也算"前进"：
                        // 否则一个打不开句柄密集的 pid 会被误判成挂起而整桶丢弃。
                        Tick();
                        continue;
                    }

                    try
                    {
                        InspectHandle(copy, (uint)pid, _results);
                    }
                    finally
                    {
                        NativeInterop.CloseHandle(copy);
                    }

                    Tick();
                }
            }
            finally
            {
                NativeInterop.CloseHandle(process);
            }
        }

        private void Tick()
        {
            var current = Interlocked.Increment(ref _progress);
            Interlocked.Exchange(ref _lastSeen, current);
        }
    }

    // ── 条目布局（两条布局的步进都是 40 字节，字段位置不同！）──
    //
    // 经典布局（≤ Win11 23H2，PowerToys FileLocksmith 的 NtdllExtensions.h）：
    //   +0  Object(8) +8 ProcessId(8) +16 Handle(8) +24 GrantedAccess(4) ...
    //
    // Windows 11 24H2（build 26100）起内核换了新布局（实测 25H2 确认）：
    //   +0  Object(8) +8 保留(8) +16 ProcessId(4) +20 保留(4)
    //   +24 Handle(4) +28 保留(4) +32 GrantedAccess(4) +36 Attributes(4)
    // 老布局读新表会把 ProcessId 读成 0 —— 症状是"枚举永远返回空"，极难排查。
    // 本机实测（2026-09-20，Win11 25H2 build 279xx）：新布局 226 个不同 pid，与任务管理器一致。
    private static readonly bool NewLayout = Environment.OSVersion.Version.Build >= 26100;

    private static ulong ReadPid(byte* entry) =>
        NewLayout ? *(uint*)(entry + 16) : (ulong)*(IntPtr*)(entry + 8);

    private static ulong ReadHandle(byte* entry) =>
        NewLayout ? *(uint*)(entry + 24) : (ulong)*(IntPtr*)(entry + 16);


    /// <summary>
    /// 检查单个已复制的句柄，命中磁盘文件则追加到 results。
    /// <b>多线程调用</b>（每桶一个 worker）—— results 必须加锁。
    /// </summary>
    private static void InspectHandle(IntPtr handle, uint pid, List<HandleEntry> results)
    {
        if (NativeInterop.GetFileType(handle) != NativeInterop.FileTypeDisk)
        {
            return;   // 快速排除绝大多数非文件句柄
        }

        var typeName = QueryObjectNameString(handle, NativeInterop.ObjectTypeInformation);
        if (typeName is null || !typeName.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var kernelName = QueryObjectNameString(handle, NativeInterop.ObjectNameInformation);
        if (string.IsNullOrEmpty(kernelName))
        {
            return;   // 匿名文件句柄（内存节/临时句柄）没名字，跳过
        }

        lock (results)
        {
            results.Add(new HandleEntry(pid, "File", DosPath.FromKernelPath(kernelName)));
        }
    }

    private static string? QueryObjectNameString(IntPtr handle, int infoClass)
    {
        const int bufferSize = 0x4000;   // 16 KB，与 PowerToys 的 DefaultResultBufferSize 同量级
        var buffer = stackalloc byte[bufferSize];

        var status = NativeInterop.NtQueryObject(handle, infoClass, buffer, bufferSize, out _);
        if (status != 0)
        {
            return null;
        }

        var str = &((NativeInterop.ObjectNameOrTypeHeader*)buffer)->Name;
        return str->AsString();
    }

    private static byte* QueryHandleTable()
    {
        var size = DefaultBufferSize;
        while (size <= MaxBufferSize)
        {
            var buffer = (byte*)Marshal.AllocHGlobal(size);
            var status = NativeInterop.NtQuerySystemInformation(
                NativeInterop.SystemExtendedHandleInformation, buffer, size, out _);

            if (status == 0)
            {
                return buffer;
            }

            Marshal.FreeHGlobal((IntPtr)buffer);
            if (status != NativeInterop.StatusInfoLengthMismatch)
            {
                return null;
            }

            size *= 2;   // 表在增长，翻倍重试
        }

        return null;
    }
}

/// <summary>内核路径 → DOS 盘符路径（<c>\Device\HarddiskVolume3\...</c> → <c>C:\...</c>）。</summary>
public static class DosPath
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _deviceToLetter;
    private static DateTimeOffset _builtAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>盘符映射在 Core 进程内缓存（盘符挂载极少变化；换盘等 5 分钟内生效即可）。</summary>
    public static string FromKernelPath(string kernelPath)
    {
        var map = GetMap();
        if (map is not null)
        {
            // 最长前缀匹配（\Device\HarddiskVolume12 不能被 Volume1 抢走前缀）
            string? bestKey = null;
            foreach (var key in map.Keys)
            {
                if (kernelPath.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                    && (bestKey is null || key.Length > bestKey.Length))
                {
                    bestKey = key;
                }
            }

            if (bestKey is not null)
            {
                return map[bestKey] + kernelPath[bestKey.Length..];
            }
        }

        return kernelPath;   // 映射不上就原样返回内核名（仍是有用的信息）
    }

    private static Dictionary<string, string>? GetMap()
    {
        lock (Gate)
        {
            if (_deviceToLetter is not null && DateTimeOffset.Now - _builtAt < CacheLifetime)
            {
                return _deviceToLetter;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var letter in Environment.GetLogicalDrives())
            {
                var name = letter.TrimEnd('\\', '/');
                var target = new char[1024];
                uint len;
                unsafe
                {
                    fixed (char* pName = name)
                    fixed (char* pTarget = target)
                    {
                        len = NativeInterop.QueryDosDevice(name, pTarget, target.Length);
                    }
                }

                if (len > 0)
                {
                    var device = new string(target, 0, (int)Math.Min(len, target.Length)).TrimEnd('\0');
                    map[device] = name + @"\";
                }
            }

            _deviceToLetter = map;
            _builtAt = DateTimeOffset.Now;
            return map;
        }
    }
}
