// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

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
/// 这里的对策等价但温和：工作线程用<b>后台线程</b>，超时只放弃（不杀）。
///
/// <b>★ 挂起句柄黑名单（2026-09-29 安全面专项审 RI-6 · 治本）</b>：原先此处写着
/// 「泄漏上界 = 挂起句柄数，进程退出时自动回收」—— <b>该论证已被实测推翻</b>，而且真实机理
/// 比"线程累积"更糟：挂住的线程永远走不到 <c>finally</c> ⇒ <c>DuplicateHandle</c> 出来的副本
/// **漏在 Core 里**，而该副本本身又是下一轮扫描的候选 ⇒ **自增强**。实测同一 Core 连续调用：
/// 句柄数 273→458→700→1087→1746、耗时 4.8→10.1→21.8→43.7 s，**逐次翻倍**。
/// 对策 = <see cref="StalledHandles"/>：记住卡住的 <c>(pid, handle)</c>，后续扫描直接跳过。
/// 跳过的条数经 <c>handles.enumerate</c> 的 <c>skippedStalled</c> 字段对外可见 ——
/// 它是**已知的数据缺失**，不是静默丢弃。
///
/// <para>⚠️ 已知残留：第一轮仍会为每个"新遇到的"挂起句柄泄漏 ≤ 段数个副本（看门狗发现它之前
/// 各段各挂一次），且这些副本此后**无法被识别**（对象指针列在新版 Windows 恒为 0）⇒
/// 每轮仍会漏少量句柄、线性而非指数。彻底消除需把这一步挪进**可丢弃的子进程**
/// （挂住就杀进程，代价随进程一起回收）—— 已记入 `docs/未完成项与待决清单.md`。</para>
///
/// <para><b>★ 线程句柄的"增长"不是泄漏（2026-09-29 性能专项审 RI-10 定性）</b>：每轮扫描创建
/// 2×workerCount 个线程，进程句柄数随之 ~+60/轮增长 —— 实测（连续 15 轮）这是 <b>GC 有界锯齿</b>
/// 而非泄漏：托管 <see cref="Thread"/> 对象的 OS 句柄由终结器释放，扫描自身的大分配量会自然
/// 触发 GC，句柄数随之回落（435→785→494→…→844→386→375，低点始终在基线区）。独立对照实验
/// （`_scratch/perf/threadprobe`）证明 `GC.Collect` 后句柄立即回落。**别再据此改池化/子进程** ——
/// 池化反而会被挂起线程污染（挂住的线程无法复用）。先前"等 12 秒只回落 9"的测量是静置无分配
/// 压力、GC 未触发的假象。</para>
///
/// <b>并行化（2026-09-21，清单 C2）</b>：原实现是单线程遍历全表，10 万+ 条目逐个
/// <c>OpenProcess</c>/<c>DuplicateHandle</c>，实测 1~2 分钟。现改为
/// <b>按下标区间切段 + 每段一个监督线程</b>（区间内为"可重启 worker"）。
/// ⚠️ 注意**不是**按 pid 分桶 —— 分桶要先扫全表建字典，且某个 pid 的桶挂住会丢掉该进程
/// **全部**句柄；切段则天然保留原实现"跳过挂起的那一个句柄、从下一条继续"的语义，
/// 挂起影响面只有 1 个句柄。两种方案的取舍详见 <see cref="StartRangeSupervisor"/> 的注释。
/// （2026-09-29：本段原先写的是"按 pid 分桶 + 多 worker"，那是**被否决的方案**；
/// 与之配套的死类 <c>BucketWorker</c> 一并删除，见安全面专项审 RI-5。）
/// </summary>
public static unsafe class HandlePrimitives
{
    public sealed record HandleEntry(uint Pid, string Type, string Path);

    /// <summary>
    /// 一次枚举的结果。
    /// <paramref name="SkippedStalled"/> &gt; 0 表示有条目因「其内核对象已知会让
    /// <c>NtQueryObject</c> 永久挂起」被<b>黑名单跳过</b>（RI-6）—— 这个数字必须对外可见：
    /// 它是**已知的数据缺失**，静默丢弃会让调用方把"少了几条"误读成"系统里就只有这些"。
    /// <paramref name="StalledObjectsKnown"/> 是黑名单当前规模（诊断用，见 <see cref="StalledObjects"/>）。
    /// </summary>
    public sealed record ScanOutcome(List<HandleEntry> Entries, long SkippedStalled, int StalledObjectsKnown);

    private const int DefaultBufferSize = 0x0010_0000;   // 1 MB 起步
    private const int MaxBufferSize = 0x1000_0000;       // 256 MB 封顶

    /// <summary>枚举文件句柄。pidFilter 为空 = 全系统；返回条目按遍历顺序。</summary>
    public static ScanOutcome Enumerate(ulong? pidFilter, CancellationToken ct)
    {
        var buffer = QueryHandleTable();
        if (buffer is null)
        {
            throw new PrimitiveException(
                RpcErrorCodes.InternalError, "NtQuerySystemInformation 无法返回句柄表");
        }

        var counters = new ScanCounters();
        try
        {
            var entries = Scan(buffer, pidFilter, ct, counters);
            return new ScanOutcome(entries, counters.Skipped, StalledHandles.Count);
        }
        finally
        {
            // 🔴 必须释放：QueryHandleTable 用 Marshal.AllocHGlobal 分配（1 MB 起步，表大时到 256 MB），
            //    而**成功路径原先没有释放** —— 出参 buffer 被直接丢弃。Core 是**常驻**进程，
            //    于是每次 handles.enumerate 调用泄漏一整张句柄表。
            //    2026-09-29 安全面专项审 RI-4 实测（同一 Core 进程连续调用）：
            //      基线 34620 KB → +9096 → +7280 → +8088 → +7948 KB，**线性累积、从不回收**，
            //    约 8 MB/次 ⇒ 上百次调用即泄漏到 GB 级。
            //    对照：同目录的 VolumePrimitives 对 CreateFileW 句柄一律 try/finally，做对了 ——
            //    可见这是疏忽，不是取舍。
            Marshal.FreeHGlobal((IntPtr)buffer);
        }
    }

    /// <summary>
    /// 扫描句柄表主体。buffer 的生命周期由 <see cref="Enumerate"/> 的 finally 管理 ——
    /// 本方法**不得**释放它。拆成独立方法是为了让释放点只有一个、且不改变原主体的缩进与审阅基线。
    /// </summary>
    private static List<HandleEntry> Scan(
        byte* buffer, ulong? pidFilter, CancellationToken ct, ScanCounters counters)
    {
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        CoreConsole.Trace($"[handles] scan begin: entries={count} workers={workerCount}"
                          + $" pidFilter={pidFilter?.ToString() ?? "all"} handles={OwnHandleCount()}");

        for (var w = 0; w < workerCount; w++)
        {
            var start = w * (long)segment;
            var end = Math.Min(start + segment, count);
            if (start >= end)
            {
                break;
            }

            supervisors.Add(StartRangeSupervisor(start, end, first, pidFilter, results, ct, counters));
        }

        foreach (var supervisor in supervisors)
        {
            // 每个区间由一个"监督线程"负责跑到区间末尾（内部含看门狗重开逻辑），
            // 所以正常情况一定会自己结束；它们是后台线程，即使异常退出也不会拖住 Core。
            supervisor.Join();
        }

        CoreConsole.Trace($"[handles] scan end: ms={sw.ElapsedMilliseconds} results={results.Count}"
                          + $" skipped={counters.Skipped} stalls={counters.Stalls}"
                          + $" known={StalledHandles.Count} handles={OwnHandleCount()}");

        return results;
    }

    /// <summary>本进程当前句柄数（诊断：泄漏类问题必须能自测量，否则只能靠外部工具反复试）。</summary>
    private static uint OwnHandleCount() =>
        NativeInterop.GetProcessHandleCount(NativeInterop.GetCurrentProcess(), out var n) ? n : 0;

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
        CancellationToken ct,
        ScanCounters counters)
    {
        var supervisor = new Thread(() =>
        {
            var state = new ScanState(start, end);
            var slot = new WorkerSlot();   // 本区间的"正在碰哪个对象"槽位（跨 worker 重启复用）
            var worker = StartRangeWorker(state, first, pidFilter, results, counters, slot);

            try
            {
                while (state.Cursor < end && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(WatchdogIntervalMs);

                    if (!state.MadeProgress())
                    {
                        // ★ RI-6：把**卡住的那一条**记进黑名单，否则下次调用会在同一条上再挂一次
                        //   —— 而每挂一次都会**泄漏一个 DuplicateHandle 副本**（挂住的线程永远走不到
                        //   finally），泄漏出来的副本本身又是下一轮的挂起源 ⇒ 自增强。实测：Core
                        //   句柄数 273→458→700→1087→1746、耗时 4.8→10.1→21.8→43.7 s，**逐次翻倍**。
                        //
                        //   ⚠️ 条目必须取自 **worker 主动声明的槽位**：两种反推都实测不可行 ——
                        //   ① `游标 - 1` 读到的是空闲槽；② 想读内核对象指针（表 +0 列）——
                        //   新版 Windows 该字段**恒为 0**（不再暴露内核地址）。
                        var key = slot.Key;
                        var stage = slot.Stage;
                        counters.AddStall();
                        if (counters.Stalls <= 8)
                        {
                            CoreConsole.Trace($"[handles] stall #{counters.Stalls} seg=[{start},{end})"
                                              + $" cursor={state.Cursor} key=0x{key:x}"
                                              + $" pid={key >> 32} handle={(uint)key} stage={stage}");
                        }

                        if (key != 0)
                        {
                            StalledHandles.Remember(key);
                        }

                        // ★ 替挂住的 worker 关掉它那一份副本。不关的话副本漏在 Core 里，而它本身
                        //   又会被下一轮扫描当成新候选 ⇒ 自增强（实测每轮仍漏 ~22 个、句柄数
                        //   稳定 +180/轮）。所有权握手保证与 worker 的 finally 不会双重关闭。
                        if (slot.ReleaseOwnership(out var abandoned))
                        {
                            NativeInterop.CloseHandle(abandoned);
                        }

                        state.SkipOne();   // 跳过挂起的那一个句柄
                        worker = StartRangeWorker(state, first, pidFilter, results, counters, slot);
                    }
                }
            }
            catch
            {
                // review-guards:allow-empty-catch :: 监督线程绝不外抛
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
        List<HandleEntry> results,
        ScanCounters counters,
        WorkerSlot slot)
    {
        var thread = new Thread(() =>
        {
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

                    // ★ RI-6 黑名单：这一条已知会让 NtQueryObject 永久挂起 ⇒ 直接跳过。
                    //   必须**早于** OpenProcess / DuplicateHandle：否则仍会为它建句柄、并把
                    //   挂起线程（连同它的句柄）永远留在进程里。跳过的条数计入 skippedStalled
                    //   并对调用方可见。
                    var key = ((long)pid << 32) | (long)ReadHandle(entry);
                    if (StalledHandles.Contains(key))
                    {
                        counters.AddSkipped();
                        continue;
                    }

                    // ★ 声明"本 worker 现在要碰这一条" —— 看门狗据此精确定位挂起的那一条。
                    //   两种反推都实测不可行（见 WorkerSlot 的注释），只能主动声明。
                    slot.Enter(key, 1);

                    // ★ 进程句柄**只在 DuplicateHandle 期间需要** —— 用完立刻关。
                    //   绝不能让它跨到 NtQueryObject 之后：挂住的线程永远走不到 finally，
                    //   跨过去就是"每个挂起点漏一整个 pid 缓存"（实测每轮漏 ~59 个句柄）。
                    //   代价是 OpenProcess 由"每 pid 一次"退回"每条一次"（实测总耗时仍 1.4 s 级，
                    //   见 docs/代码审查报告-安全面专项-2026-09-29.md 的 RI-6 收尾实测）。
                    var process = NativeInterop.OpenProcess(NativeInterop.ProcessDupHandle, false, (uint)pid);
                    if (process == IntPtr.Zero)
                    {
                        continue;   // 打不开的进程（权限/已退出）
                    }

                    IntPtr copy;
                    try
                    {
                        if (!NativeInterop.DuplicateHandle(
                                process, (IntPtr)ReadHandle(entry), NativeInterop.GetCurrentProcess(),
                                out copy, 0, false, NativeInterop.DuplicateSameAccess))
                        {
                            continue;   // 打不开的句柄（退出中的进程等），跳过
                        }
                    }
                    finally
                    {
                        NativeInterop.CloseHandle(process);
                    }

                    // 此后只持有 copy（唯一可能在 NtQueryObject 上挂住的资源）。
                    // 先登记所有权、再做查询：一旦挂住，看门狗会替我们关掉它
                    // （挂住的线程永远走不到下面的 finally，不登记就是永久泄漏）。
                    slot.OwnDuplicate(copy);
                    try
                    {
                        InspectHandle(copy, (uint)pid, results, slot);
                    }
                    finally
                    {
                        if (slot.ReleaseOwnership(out var owned))
                        {
                            NativeInterop.CloseHandle(owned);
                        }
                    }
                }
            }
            catch
            {
                // review-guards:allow-empty-catch :: 扫描线程绝不外抛：结果以已收集到的为准
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
    /// 区间内"worker 当前正在处理哪一条、卡在哪一步"的共享槽位（RI-6）。
    ///
    /// <para><b>为什么需要它</b>：看门狗只能知道"游标 200 ms 没动"，但答不出是**哪一条**挂住了。
    /// 实测过两种反推都不成立：① <c>游标 - 1</c> 读到的是空闲槽；② 想读内核对象指针
    /// （表里 +0 位置）——<b>新版 Windows 该字段恒为 0</b>（实测 q0=q1=0x0，只有
    /// <c>+16 pid / +24 handle / +32 access</c> 有效，显然是不再向用户态暴露内核地址）。
    /// 所以只能让 worker **主动声明**。</para>
    ///
    /// <para>键取 <c>(pid, handle)</c>：两者都在表里可读且已验证正确；在"对象指针不可得"的
    /// 前提下，这是能定位到具体一条的唯一键。</para>
    ///
    /// <para>槽位按**区间**分配、跨 worker 重启复用：旧 worker 挂住后不会重置它，所以看门狗
    /// 读到的仍是挂起那条；新 worker 一开工就会覆盖成自己的。</para>
    /// </summary>
    private sealed class WorkerSlot
    {
        private long _key;
        private int _stage;
        private long _duplicate;
        private int _owned;

        /// <summary>当前条目的 <c>(pid &lt;&lt; 32) | handle</c>。</summary>
        internal long Key => Interlocked.Read(ref _key);

        /// <summary>当前所处阶段：1=即将 DuplicateHandle，2=GetFileType，3=NtQueryObject(类型)，4=NtQueryObject(名称)。</summary>
        internal int Stage => Volatile.Read(ref _stage);

        internal void Enter(long key, int stage)
        {
            Interlocked.Exchange(ref _key, key);
            Volatile.Write(ref _stage, stage);
        }

        internal void SetStage(int stage) => Volatile.Write(ref _stage, stage);

        /// <summary>
        /// 登记"本 worker 现在持有一个待关的副本"。<b>必须先写值再置所有权位</b> ——
        /// 否则移交方可能读到 0 并关掉一个无效句柄（丢的正是本该关的那个）。
        /// </summary>
        internal void OwnDuplicate(IntPtr duplicate)
        {
            Interlocked.Exchange(ref _duplicate, duplicate.ToInt64());
            Volatile.Write(ref _owned, 1);
        }

        /// <summary>
        /// 移交副本的所有权：**只有一方能拿到**（<see cref="Interlocked.Exchange(ref int, int)"/> 的原子性
        /// 保证）。worker 的正常 <c>finally</c> 与看门狗的"挂起处置"都调它 —— 谁先谁关，绝不会双重关闭。
        ///
        /// <para>为什么需要这套握手：worker 挂住后永远走不到 <c>finally</c>，副本就漏在 Core 里，
        /// 而它本身又是下一轮的挂起源（自增强）。让看门狗替它关掉，泄漏就被切断。
        /// 关一个"别的线程正在用它做查询"的句柄是安全的：内核在系统调用入口就已持有对象引用，
        /// 关句柄不会让那次调用崩溃（最坏是它返回 <c>STATUS_INVALID_HANDLE</c>）。</para>
        /// </summary>
        internal bool ReleaseOwnership(out IntPtr duplicate)
        {
            if (Interlocked.Exchange(ref _owned, 0) == 1)
            {
                duplicate = (IntPtr)Interlocked.Read(ref _duplicate);
                return true;
            }

            duplicate = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>一次扫描的计数器（所有 worker / 监督线程共享一个实例）。</summary>
    private sealed class ScanCounters
    {
        private long _skipped;
        private long _stalls;

        internal long Skipped => Interlocked.Read(ref _skipped);

        /// <summary>看门狗判定"游标不动"的次数（每次 = 一个被放弃的挂起线程 + 200 ms 等待）。</summary>
        internal long Stalls => Interlocked.Read(ref _stalls);

        internal void AddSkipped() => Interlocked.Increment(ref _skipped);

        internal void AddStall() => Interlocked.Increment(ref _stalls);
    }

    /// <summary>
    /// 挂起句柄黑名单（RI-6 治本）：记住哪些**句柄**让 <c>NtQueryObject</c> 永久挂起，
    /// 后续扫描直接跳过。
    ///
    /// <para><b>为什么按 <c>(pid, handle)</c> 而不是内核对象地址</b> —— 这是实测逼出来的：
    /// 句柄表里"对象指针"那一列在**新版 Windows 上恒为 0**（2026-09-29 dump 实测：
    /// <c>q0=q1=0x0</c>，只有 <c>+16 pid / +24 handle / +32 access</c> 有效），
    /// 显然系统不再向用户态暴露内核地址 ⇒ 按对象作键<b>根本无法实现</b>。
    /// <c>(pid, handle)</c> 两列都可读且已验证正确，是能定位到具体一条的唯一键。</para>
    ///
    /// <para><b>为什么不按句柄值单独作键</b>：句柄值会被复用给别的对象，必须带 pid 收窄；
    /// 即便如此仍有复用可能（同一进程内句柄号回收）⇒ 加 TTL 兜底，过期条目在下次写入时
    /// 被剪除并重新判定。</para>
    ///
    /// <para><b>为什么必须做这件事</b>：挂住的线程永远走不到 <c>finally</c>，于是
    /// <c>DuplicateHandle</c> 出来的副本**漏在 Core 里**；而那个副本本身又会被下一轮扫描当成
    /// 新的候选 ⇒ **自增强**。实测 Core 句柄数 273 → 458 → 700 → 1087 → 1746（逐次翻倍），
    /// 耗时 4.8 → 10.1 → 21.8 → 43.7 s 同步翻倍。跳过已挂起的句柄是唯一能止住它的办法。</para>
    ///
    /// <para><b>并发</b>：读路径是热路径（每条都要查一次，量级 10⁵~10⁶），所以用
    /// **写时复制 + lock-free 读**：<see cref="Contains"/> 只读 volatile 快照、不加锁；
    /// 写入很稀少（只在真的挂起时），加锁并可接受 O(n) 复制。</para>
    /// </summary>
    private static class StalledHandles
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
        private const int MaxEntries = 4096;

        private static readonly object Gate = new();
        private static volatile Dictionary<long, DateTimeOffset> _snapshot = new();

        /// <summary>当前规模的**近似**值（可能含尚未被剪除的过期条目 —— 剪枝发生在写入时）。</summary>
        internal static int Count => _snapshot.Count;

        internal static bool Contains(long key)
        {
            // 只读快照：读路径不加锁（热路径）。过期条目在这里只"判为过期"，不就地删除。
            return key != 0
                   && _snapshot.TryGetValue(key, out var seenAt)
                   && DateTimeOffset.UtcNow - seenAt < Ttl;
        }

        internal static void Remember(long key)
        {
            if (key == 0)
            {
                return;
            }

            lock (Gate)
            {
                var now = DateTimeOffset.UtcNow;
                var next = new Dictionary<long, DateTimeOffset>(_snapshot.Count + 1);
                foreach (var pair in _snapshot)
                {
                    if (now - pair.Value < Ttl)
                    {
                        next[pair.Key] = pair.Value;   // 顺带剪枝（写入稀少，代价可忽略）
                    }
                }

                if (next.Count >= MaxEntries)
                {
                    // 防御性：真触顶说明判据失准（句柄号复用率异常）。整体清空优于无界增长，
                    // 代价只是下一轮重新判定一次。
                    next.Clear();
                }

                next[key] = now;
                _snapshot = next;
            }
        }
    }

    // ── 条目布局（两条布局的步进都是 40 字节，字段位置不同！）──
    //
    // 经典布局（≤ Win11 23H2，PowerToys FileLocksmith 的 NtdllExtensions.h）：
    //   +0  Object(8) +8 ProcessId(8) +16 Handle(8) +24 GrantedAccess(4) ...
    //
    // Windows 11 24H2（build 26100）起内核换了新布局（实测 25H2 确认）：
    //   +0 保留(8) +8 保留(8) +16 ProcessId(4) +20 保留(4)
    //   +24 Handle(4) +28 保留(4) +32 GrantedAccess(4) +36 Attributes(4)
    // 老布局读新表会把 ProcessId 读成 0 —— 症状是"枚举永远返回空"，极难排查。
    // 本机实测（2026-09-20，Win11 25H2 build 279xx）：新布局 226 个不同 pid，与任务管理器一致。
    //
    // ⚠️ 补充实测（2026-09-29，RI-6 排查时 dump 原始条目）：新布局 +0/+8 **恒为 0x0**
    //    （经典布局那里是 Object 指针）⇒ 系统**不再向用户态暴露内核对象地址**。
    //    因此任何"按对象去重/黑名单"的设计在本机上都不可能实现，只能退到 (pid, handle)。
    private static readonly bool NewLayout = Environment.OSVersion.Version.Build >= 26100;

    private static ulong ReadPid(byte* entry) =>
        NewLayout ? *(uint*)(entry + 16) : (ulong)*(IntPtr*)(entry + 8);

    private static ulong ReadHandle(byte* entry) =>
        NewLayout ? *(uint*)(entry + 24) : (ulong)*(IntPtr*)(entry + 16);


    /// <summary>
    /// 检查单个已复制的句柄，命中磁盘文件则追加到 results。
    /// <b>多线程调用</b>（每段一个 worker）—— results 必须加锁。
    ///
    /// <para>顺带把"当前进行到哪一步"写进 <paramref name="slot"/>：看门狗据此知道挂起发生在
    /// 类型查询还是名称查询（RI-6 排查用）。这三步里只有 <c>NtQueryObject</c> 会挂。</para>
    /// </summary>
    private static void InspectHandle(IntPtr handle, uint pid, List<HandleEntry> results, WorkerSlot slot)
    {
        slot.SetStage(2);
        if (NativeInterop.GetFileType(handle) != NativeInterop.FileTypeDisk)
        {
            return;   // 快速排除绝大多数非文件句柄
        }

        slot.SetStage(3);
        var typeName = QueryObjectNameString(handle, NativeInterop.ObjectTypeInformation);
        if (typeName is null || !typeName.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        slot.SetStage(4);
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
