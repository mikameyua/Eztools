// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

// ============================================================================
// Eztools.Index / JournalTail —— W3-c-1/c-3 同步层：USN 变更流读取 + 启动对账
//
// ⚠️ 重估记录（2026-09-24，实施计划 W3-c-1 ★★ 验收条款触发）：
//   原架构前提"READ/QUERY_USN_JOURNAL 零提权可读（FILE_READ_DATA 中完整性）"被实测证伪
//   —— Win11 25H2 真机矩阵实验：非提权连卷句柄都打不开（FILE_READ_DATA/GENERIC_READ
//   均 err=5），0/FILE_READ_ATTRIBUTES/±BACKUP_SEMANTICS/SYNC 句柄上 QUERY 一律 err=1。
//   重估结论（用户拍板）：USN 读取经**提权 Core 新原语**（volume.queryJournal / readUsn /
//   writeUsnClose，与 readMft 同族同审计），ezt-index 走既有管道客户端 —— 见 CoreUsnClient。
//
// 三条静默失效路径（实施计划 W3-c 风险区，全部显式化）：
//   ① journal 被系统删除重建（JournalId 变了）→ UsnReconciler 判定全量重扫，绝不从中间读；
//   ② journal 被截断（FirstUsn 前移越过游标）→ 同上；
//   ③ Reason 用 == 判等 → 复合记录（CLOSE 同现）整条丢失 → UsnRecord.Has 位与 + 24.x 复合夹具守卫。
//
// 可测试性（S9/S14 同款纪律）：<see cref="IUsnSource"/> 全注入；<see cref="CoreUsnClient"/>
// 真管道薄层不进零进程断言面（实证交给 --probe-usn 连提权 Core）。
// ============================================================================

using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Index;

/// <summary>journal 查询的结构化结局（§4.2 判定链第一步）。</summary>
public enum UsnJournalStatus
{
    Ok,
    NotActive,          // ERROR_JOURNAL_NOT_ACTIVE (1179)：卷未开 journal
    DeleteInProgress,   // ERROR_JOURNAL_DELETE_IN_PROGRESS (1178)
    EntryDeleted,       // ERROR_JOURNAL_ENTRY_DELETED (1181)
    Error,              // 其余失败（Win32Error 带原码）
}

/// <summary>FSCTL_QUERY_USN_JOURNAL 的结果。Status != Ok 时数值字段无意义。</summary>
public sealed record UsnJournalInfo(
    UsnJournalStatus Status,
    long JournalId,
    long FirstUsn,
    long NextUsn,
    int Win32Error)
{
    public bool IsOk => Status == UsnJournalStatus.Ok;
}

/// <summary>一次 READ_USN_JOURNAL 的产出：新游标 + 本批记录（可能为空批 = 已追平）。</summary>
public sealed record UsnBatch(long NextUsn, IReadOnlyList<UsnRecord> Records);

/// <summary>
/// 一次补齐里**某个文件名**贡献了多少条记录（2026-09-25 缺口③ 的取证面）。
/// <paramref name="Own"/> = 该名字对应"我们自己写在安装根下的文件"（诊断/审计/索引落盘）。
/// </summary>
public readonly record struct RecordSource(string Name, int Hits, bool Own);

/// <summary>
/// 增量补齐（<see cref="JournalTail.Drain"/>）的**有界**结局。
///
/// <b>为什么必须带 CaughtUp</b>（2026-09-25 真机实证，踩坑全集 §2.21）：
/// Drain 原本只以"读到空批"为出口 ⇒ 当卷上**持续有新变更**时永远读不到空批，
/// 于是无限空转（实测 D: 以 ~750 批/秒跑了 40 s+ 不停，每批 = 一次到提权 Core 的
/// 管道连接 + 一条审计记录，且**不打印任何诊断**，自举永远走不到"自举完成"）。
/// 只要卷承载了写入方（安装根 / 日志 / 索引文件自己都在同一个卷上时几乎必然），
/// "追不平"就是常态而非异常 —— 所以它必须是一个**可返回、可断言、可降级**的终局。
///
/// <b>为什么还要带 TopSources / SelfInflicted</b>（缺口③）：
/// "追不平"只说出了症状。当时的归因只能靠一次外部对照实验（把安装根挪到别的卷再跑），
/// 而正确做法是让**这段代码自己说出谁在写**：把被应用记录的文件名按条数排前五报出来，
/// 并标出其中哪些是**我们自己**落在安装根下的诊断/索引文件。有了它，任何用户环境都能自证，
/// 不必复刻我们的实验台 —— 这就是本项目"诊断做常驻能力"（W3-b 教训④）的落点。
/// </summary>
public sealed record DrainResult(int Batches, int Records, bool CaughtUp, string? StoppedBecause)
{
    /// <summary>本次补齐中被应用记录的**来源 Top-N**（按条数降序；空 = 没读到任何记录）。</summary>
    public IReadOnlyList<RecordSource> TopSources { get; init; } = [];

    /// <summary>
    /// 本轮追不平**是否由我们自己造成**（过半记录来自安装根下的自有文件）。
    /// 判定口径写在 <see cref="JournalTail.Drain"/>：按<span>条数</span>过半，不看名字个数。
    /// </summary>
    public bool SelfInflicted { get; init; }
}

/// <summary>USN 读取失败（fail-fast；journal 运行中被删等场景由调用方按 Status 处理）。</summary>
public sealed class UsnSourceException : Exception
{
    public UsnSourceException(UsnJournalStatus status, int win32Error, string message)
        : base(message)
    {
        Status = status;
        Win32Error = win32Error;
    }

    public UsnJournalStatus Status { get; }

    public int Win32Error { get; }
}

/// <summary>
/// USN 变更流来源抽象。一次 <see cref="ReadUsn"/> = 一批（缓冲区上限内全部可用记录），
/// BytesToWaitFor=0 ⇒ 立即返回（空闲轮询模型，不忙等不阻塞）。
/// </summary>
public interface IUsnSource : IDisposable
{
    UsnJournalInfo QueryJournal();

    /// <summary>从 <paramref name="fromUsn"/> 读一批。journal 异常 ⇒ <see cref="UsnSourceException"/>。</summary>
    UsnBatch ReadUsn(long fromUsn, int bufferBytes);

    /// <summary>
    /// 心跳（FSCTL_WRITE_USN_CLOSE_RECORD，经提权 Core 的卷写句柄）。
    /// false = 心跳不可用（**显式降级**：journal 截断防护退回启动对账，绝不静默假装在跳；
    /// 失败原因经实现方的诊断通道透出，不吞）。
    /// </summary>
    bool TryWriteCloseRecord();
}

/// <summary>同步计划（§4.2 判定链终局；显式状态可断言，不靠日志文本猜）。</summary>
public enum SyncPlanKind
{
    /// <summary>游标有效：增量补齐后进入实时 tail。</summary>
    Incremental,

    /// <summary>journal 重建 / 游标越界 / 索引无游标 ⇒ 全量重扫该卷。</summary>
    FullRebuild,

    /// <summary>该卷无 journal 可用 ⇒ 静态快照（索引可查，不增量；UI 必须可见标注，§4.1 反向边界）。</summary>
    StaticSnapshot,
}

/// <summary>判定结果：终局 + 人类可读原因（进诊断与 SyncOutcomes）。</summary>
public readonly record struct SyncPlan(SyncPlanKind Kind, string Reason);

/// <summary>§4.2 判定链（纯函数）。输入 = .ezidx header 存的 (journalId, nextUsn) + 实时 journal 信息。</summary>
public static class UsnReconciler
{
    public static SyncPlan Decide(ulong savedJournalId, long savedNextUsn, UsnJournalInfo journal)
    {
        switch (journal.Status)
        {
            case UsnJournalStatus.NotActive:
            case UsnJournalStatus.DeleteInProgress:
            case UsnJournalStatus.EntryDeleted:
            case UsnJournalStatus.Error:
                return new SyncPlan(SyncPlanKind.StaticSnapshot, $"journal 不可用（{journal.Status}，Win32={journal.Win32Error}）");
        }

        // 索引从未建立游标（旧格式 .ezidx / 上一轮重建时 journal 不可用）：
        // journal 现在可用 ⇒ 重建一次建立游标（一次性 11s 级成本换永久实时；此后不再走这里）
        if (savedJournalId == 0)
        {
            return new SyncPlan(SyncPlanKind.FullRebuild, "索引无游标（journalId=0）⇒ 重建建立游标");
        }

        // 🔴🔴🔴 第一优先级（§4.2）：journal 被删除重建后 Id 必变。不比对 ⇒ 新 journal 的
        // 某个 USN 恰好 ≥ 旧游标 ⇒ 从中间读 ⇒ **静默丢失之前的全部变更且退出码 0**。
        if (savedJournalId != (ulong)journal.JournalId)
        {
            return new SyncPlan(SyncPlanKind.FullRebuild,
                $"journal 已重建（存 {savedJournalId} ≠ 现 {journal.JournalId}）⇒ 游标无意义");
        }

        // 截断/瘦身：FirstUsn 前移越过游标 ⇒ 中间段已不可读
        if (savedNextUsn < journal.FirstUsn)
        {
            return new SyncPlan(SyncPlanKind.FullRebuild,
                $"游标越界（存 {savedNextUsn} &lt; FirstUsn {journal.FirstUsn}）⇒ 中段变更不可读");
        }

        return new SyncPlan(SyncPlanKind.Incremental, $"游标有效（nextUsn {savedNextUsn} ≥ FirstUsn {journal.FirstUsn}）");
    }
}

/// <summary>
/// 实时 tail 泵：从游标起追平（补齐）→ 空闲轮询 → 每 120 s 心跳尝试。
/// apply 回调负责互斥（SearchService.ApplyUsn 在查询锁内应用）；
/// 泵本体无状态推进 cursor（重放语义幂等：upsert/delete 重复应用结果不变）。
/// </summary>
public sealed class JournalTail
{
    /// <summary>增量补齐默认批数上限（有界化默认值；可注入小值以便零设备断言，见 29.4）。</summary>
    public const int DefaultMaxBatches = 200_000;

    /// <summary>增量补齐默认时长上限（ms）。</summary>
    public const int DefaultMaxMs = 10_000;

    private readonly string _volume;
    private readonly IUsnSource _source;
    private readonly Action<IReadOnlyList<UsnRecord>> _apply;
    private readonly TextWriter? _diag;
    private readonly int _idlePollMs;
    private readonly int _heartbeatIntervalMs;
    private readonly int _bufferBytes;
    private readonly ISet<string>? _ownNames;

    public JournalTail(
        string volume,
        IUsnSource source,
        Action<IReadOnlyList<UsnRecord>> apply,
        TextWriter? diag = null,
        int idlePollMs = 500,
        int heartbeatIntervalMs = 120_000,
        int bufferBytes = 64 * 1024,
        ISet<string>? ownNames = null)
    {
        _volume = volume;
        _source = source;
        _apply = apply;
        _diag = diag;
        _idlePollMs = idlePollMs;
        _heartbeatIntervalMs = heartbeatIntervalMs;
        _bufferBytes = bufferBytes;
        _ownNames = ownNames;
    }

    /// <summary>来源 Top-N 的条数上限（再多对回答"谁在写"没有增量信息，只是刷屏）。</summary>
    public const int TopSourcesShown = 5;

    /// <summary>来源计数的不同名字上限（防病态输入把字典撑爆；超出的归"（其它）"桶）。</summary>
    public const int MaxDistinctSources = 4096;

    /// <summary>
    /// 从安装根下的已知文件名集合构造"自有文件"判据（缺口③）。
    /// 只取**直接位于** <paramref name="dataRoot"/> 及其 <c>logs/</c>、<c>index/</c> 下的文件名 ——
    /// 这正是审计日志、core.json、.ezidx 的落点（索引器自己写的东西全在这儿）。
    ///
    /// 为什么按**文件名**而不是路径：USN 记录只带叶子名（<see cref="UsnRecord.Name"/>），
    /// 拿到全路径要回溯父链，代价远超一条诊断的价值。同名文件撞在别的目录会误判，
    /// 但"自有文件"的名字（<c>audit-20260925.log</c> / <c>vol-….ezidx</c>）撞车概率可忽略，
    /// 而且这个判据只用于**诊断归类**，不参与任何正确性决策 —— 判错也不会做错事。
    /// 取不到目录（不存在 / 无权限）时**如实返回空集**：判据退化成"都不是自有"，
    /// 结果里会写 <c>SelfInflicted=false</c>，而不是编一个结论。
    /// </summary>
    public static ISet<string> CollectOwnNames(string dataRoot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "core.json" };
        foreach (var dir in new[]
                 {
                     dataRoot,
                     Path.Combine(dataRoot, "logs"),
                     Path.Combine(dataRoot, IndexBootstrap.IndexDirName),
                 })
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    names.Add(Path.GetFileName(file));
                }
            }
            catch (Exception)
            {
                // 诊断用，取不到就算了（**绝不能**让诊断把自举搞挂）
            }
        }

        return names;
    }

    /// <summary>当前游标（泵推进后更新；诊断与测试观测量）。</summary>
    public long Cursor { get; private set; }

    /// <summary>心跳可用性（false = 写句柄不可得，显式降级；只在真试过之后才有意义）。</summary>
    public bool? HeartbeatAvailable { get; private set; }

    /// <summary>实时泵累计应用的变更条数（诊断 + 断言：证明"恢复后补齐"真的补上了）。</summary>
    public long AppliedRecords { get; private set; }

    /// <summary>实时泵累计应用的批数。</summary>
    public int AppliedBatches { get; private set; }

    /// <summary>
    /// 追平一次：读到"空批"为止（§4.3 分级只影响 UI 文案，此处全量补齐；
    /// 落后量 = 字节差只能当量级估计，**不把字节数当条数报给用户**）。
    ///
    /// <b>有界（2026-09-25 起）</b>：四个出口，绝不无限追 ——
    ///   ① 空批 ⇒ <c>CaughtUp=true</c>（静态卷上的正终局）；
    ///   ①′ **到达目标点**（<paramref name="targetUsn"/>）⇒ <c>CaughtUp=true</c> ——
    ///      **这才是"卷持续有写入"时的正解**：调用方从 QUERY_USN_JOURNAL 量到 journal 尾，
    ///      读到那里就算追平了测量点，其后新产生的记录交给实时 tail（游标已落盘，不丢）。
    ///      只看"空批"在持续写入的卷上永远不成立 —— 那正是真机上空转 40 s+ 的成因；
    ///   ② **无进展**：非空批但 <c>NextUsn</c> 没有前进 ⇒ 同一批会被反复应用，
    ///      表现为"永远追不平"的死循环 ⇒ 立即放弃并说明原因（内核/原语契约异常，必须可见）；
    ///   ③ **追不平**：达到 <paramref name="maxBatches"/> / <paramref name="maxMs"/> 仍没到
    ///      ①/①′ ⇒ 兜底放弃并说明原因（正常路径不该走到这里；走到就是原语行为偏离预期）。
    /// ②③ 的 <c>CaughtUp=false</c> 由调用方接成**可见降级**（<see cref="IndexBootstrap"/> ⇒
    /// 全量重扫），不是静默继续 —— 这是 S9′「跳过必须可见」的同族纪律。
    ///
    /// journal 异常原样抛 <see cref="UsnSourceException"/>。
    /// </summary>
    public DrainResult Drain(
        long startUsn,
        int maxBatches = DefaultMaxBatches,
        int maxMs = DefaultMaxMs,
        long? targetUsn = null)
    {
        int batches = 0, records = 0;
        Cursor = startUsn;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 缺口③：本轮补齐**谁在写**的取证面。名字 → 条数；无名/超限的归 overflow 桶。
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int overflow = 0;

        // 四个出口统一经 Make —— **任何**结局都必须带上来源诊断（缺口③ 的核心：
        // 不让归因依赖另跑一次对照实验，让这段代码自己说出谁在写）。
        DrainResult Make(int b, int r, bool caughtUp, string? stopped)
            => Compose(b, r, caughtUp, stopped, counts, overflow);

        while (true)
        {
            var batch = _source.ReadUsn(Cursor, _bufferBytes);
            batches++;
            if (batch.Records.Count > 0)
            {
                _apply(batch.Records);
                records += batch.Records.Count;
                CountSources(counts, ref overflow, batch.Records);
            }

            // ② 无进展保护：非空批却不推进游标 ⇒ Cursor 原地不动，下一轮读到**同一批**，
            //    理论上无限循环。这不是"卷太忙"，而是契约被破坏，必须立刻停下并点名。
            if (batch.Records.Count > 0 && batch.NextUsn == Cursor)
            {
                return Make(
                    batches, records, false,
                    $"游标未推进（nextUsn={batch.NextUsn} == 起点，本批 {batch.Records.Count} 条）");
            }

            Cursor = batch.NextUsn;
            if (batch.Records.Count == 0)
            {
                return Make(batches, records, true, null); // 空批 = 追平（NextUsn 即实时游标）
            }

            // ①′ 到达自举开始时量到的 journal 尾 ⇒ 追平测量点。
            //    为什么不是丢数据：journal 在 ~1 s 内不可能整卷回绕/截断（C: 的 journal 跨度
            //    约 37 MB，回绕需要 37 MB 新记录），所以 [startUsn, targetUsn) 的记录必然可读；
            //    targetUsn 之后的记录不在本次目标内，由 tail 从 Cursor 继续读。
            if (targetUsn is { } target && Cursor >= target)
            {
                return Make(batches, records, true, null);
            }

            // ③ 兜底有界：走到这里说明既没空批、也没到目标点 —— 只可能是原语行为偏离预期。
            //    这个出口**必须**带来源明细：真机上就是它被持续写入卡住（踩坑全集 §2.21），
            //    只说"追不平"等于把归因留给下一个倒霉的排查者。
            if (batches >= maxBatches || sw.ElapsedMilliseconds >= maxMs)
            {
                var result = Make(
                    batches, records, false,
                    $"追不平（{batches} 批 / {sw.ElapsedMilliseconds} ms 仍未读到空批或目标点）"
                    + "——卷变更速率不低于排空速率");
                return result with
                {
                    StoppedBecause = result.StoppedBecause + "；Top 来源：" + DescribeSources(result.TopSources),
                };
            }
        }
    }

    /// <summary>把一批记录按 <see cref="UsnRecord.Name"/> 折进计数表（无名/超限归 <paramref name="overflow"/>）。</summary>
    private static void CountSources(
        Dictionary<string, int> counts, ref int overflow, IReadOnlyList<UsnRecord> batch)
    {
        foreach (var rec in batch)
        {
            var name = rec.Name;
            if (string.IsNullOrEmpty(name))
            {
                overflow++;
                continue;
            }

            if (counts.TryGetValue(name, out var hits))
            {
                counts[name] = hits + 1;
            }
            else if (counts.Count < MaxDistinctSources)
            {
                counts[name] = 1;
            }
            else
            {
                // 病态输入护栏：键数到上限后不再新增（防被唯一文件名撑爆内存），
                // 条数仍如实计入 overflow，**不丢**（否则比率失真）。
                overflow++;
            }
        }
    }

    /// <summary>
    /// 组装结局：Top-N 来源（按条数降序，同名次按名字稳定排序）+ 自有占比判定。
    /// <see cref="DrainResult.SelfInflicted"/> = 自有名字的条数**过半**（口径写在字段注释里，
    /// 这里落码；只看名字个数会把"我写了一个 10 万条的大日志 + 别人 3 个零星文件"判成不是自己）。
    /// </summary>
    private DrainResult Compose(
        int batches, int records, bool caughtUp, string? stoppedBecause,
        Dictionary<string, int> counts, int overflow)
    {
        int ownHits = 0;
        var top = new List<RecordSource>(TopSourcesShown);
        foreach (var kv in counts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var own = _ownNames is not null && _ownNames.Contains(kv.Key);
            if (own)
            {
                ownHits += kv.Value;
            }

            if (top.Count < TopSourcesShown)
            {
                top.Add(new RecordSource(kv.Key, kv.Value, own));
            }
        }

        if (overflow > 0 && top.Count < TopSourcesShown)
        {
            top.Add(new RecordSource("（其它）", overflow, Own: false));
        }

        return new DrainResult(batches, records, caughtUp, stoppedBecause)
        {
            TopSources = top,
            SelfInflicted = records > 0 && ownHits * 2 > records,
        };
    }

    /// <summary>来源明细的人类可读渲染（进诊断文本；自有名字标 <c>[自有]</c>）。</summary>
    private static string DescribeSources(IReadOnlyList<RecordSource> top)
    {
        if (top.Count == 0)
        {
            return "（本轮无记录）";
        }

        return string.Join("、", top.Select(s => s.Own ? $"{s.Name}×{s.Hits}[自有]" : $"{s.Name}×{s.Hits}"));
    }

    /// <summary>
    /// 实时循环（<b>同步版 = 生产路径</b>，W3-e-1 起）。
    ///
    /// <b>为什么是同步的</b>：它跑在 <see cref="IndexThreads"/> 起的专用线程上，靠
    /// <c>Priority=BelowNormal</c> + <c>THREAD_MODE_BACKGROUND_BEGIN</c> 保证空闲期不抢 IO。
    /// 若用 async/await，<c>Task.Delay</c> 的续体会跳回**线程池线程** —— 那里既没有我们的
    /// 线程优先级、也不是后台 IO 模式，优先级就白设了。所以泵体刻意用 <c>Thread.Sleep</c> 轮询。
    ///
    /// 暂停闸（<paramref name="pause"/>）：暂停期间**不读 journal**（游标原地不动）——
    /// 变更留在 journal 里，恢复后从游标一次性补齐；暂停不丢游标、不崩进程。
    ///
    /// journal 运行中被删（NotActive/DeleteInProgress）⇒ 记诊断后**有序退出**：
    /// 索引保持"上次追平点"的有效快照，恢复路径 = 重启走 §4.2 对账（全量重建）——可见、不静默。
    /// </summary>
    public void Run(long startUsn, PauseGate? pause, CancellationToken ct)
    {
        Cursor = startUsn;
        var lastHeartbeat = System.Diagnostics.Stopwatch.StartNew();
        var heartbeatWarned = false;
        var stallWarned = false;

        while (!ct.IsCancellationRequested)
        {
            // 暂停：不消费。变更留在 journal 环形缓冲里，恢复后从 Cursor 补齐。
            if (pause is { IsPaused: true })
            {
                pause.NotePausedPoll();
                SleepIdle(ct);
                continue;
            }

            try
            {
                var batch = _source.ReadUsn(Cursor, _bufferBytes);
                if (batch.Records.Count > 0)
                {
                    _apply(batch.Records);
                    AppliedRecords += batch.Records.Count;
                    AppliedBatches++;
                }

                // 无进展：非空批却不推进游标 ⇒ 同一批会被反复应用（有界：2 次/秒，不会空转），
                // 但**索引永远不会前进**。只报一次（诊断通道自身也写盘，不制造二次噪音）。
                if (batch.Records.Count > 0 && batch.NextUsn == Cursor && !stallWarned)
                {
                    stallWarned = true;
                    WriteDiag(
                        $"volume {_volume}: 游标未推进（nextUsn={batch.NextUsn} 未变）"
                        + "——tail 原地打转，同步永远不会前进（内核/原语契约异常）");
                }

                Cursor = batch.NextUsn;

                if (batch.Records.Count == 0)
                {
                    if (lastHeartbeat.ElapsedMilliseconds >= _heartbeatIntervalMs)
                    {
                        lastHeartbeat.Restart();
                        HeartbeatAvailable = _source.TryWriteCloseRecord();
                        if (HeartbeatAvailable == false && !heartbeatWarned)
                        {
                            heartbeatWarned = true;
                            // 已知行为（S9 偏差，2026-09-25 实证）：FSCTL_WRITE_USN_CLOSE_RECORD
                            // 只支持文件句柄（卷句柄 err=1；OSR + 旧版 MSDN "file or directory handle"）。
                            // 不引入每卷标记文件（产品可见性代价 > 冗余诊断价值）——journal 运行中被删
                            // 已由 ReadUsn 的 NotActive 分支覆盖，心跳恒为显式降级。
                            WriteDiag(
                                $"volume {_volume}: 心跳不可用——journal 截断防护退回启动对账"
                                + "（FSCTL_WRITE_USN_CLOSE_RECORD 需文件句柄，卷句柄不可用；S9）");
                        }
                    }
                }

                SleepIdle(ct);
            }
            catch (UsnSourceException ex) when (
                ex.Status is UsnJournalStatus.NotActive or UsnJournalStatus.DeleteInProgress)
            {
                WriteDiag(
                    $"volume {_volume}: journal 运行中被删（{ex.Status}）——tail 停止，索引保持有效快照；恢复 = 重启对账");
                return;
            }
        }
    }

    /// <summary>
    /// 实时循环的异步薄包装（**测试用**）。生产路径是同步 <see cref="Run"/>——
    /// 见那里的说明（async 续体跳线程池会丢掉线程优先级）。
    /// </summary>
    public Task RunAsync(long startUsn, PauseGate? pause, CancellationToken ct)
        => Task.Run(() => Run(startUsn, pause, ct), ct);

    /// <summary>空闲等待（可被取消立即唤醒，不必等满一个轮询周期）。</summary>
    private void SleepIdle(CancellationToken ct)
    {
        // 取消时立即返回（WaitOne 对已取消的 token 立即返回 true）
        ct.WaitHandle.WaitOne(_idlePollMs);
    }

    /// <summary>同步诊断（生产路径无线程池上下文，不 await）。</summary>
    private void WriteDiag(string message)
    {
        if (_diag is null)
        {
            return;
        }

        _diag.WriteLine("[ezt-index] " + message);
        _diag.Flush();
    }
}

/// <summary>
/// 真 USN 源 = **经提权 Core 原语**（W3-c-1 重估后的落地形态，2026-09-24）。
///
/// 原计划的"零提权本地 P/Invoke"（卷句柄 FILE_READ_DATA）被实测证伪：Win11 25H2 上
/// 非提权进程连卷句柄都打不开（err=5），FSCTL 在属性句柄上一律 err=1 —— 见
/// 设计方案 §4.1 的重估记录。增量读取改为走 <c>volume.queryJournal</c> /
/// <c>volume.readUsn</c> / <c>volume.writeUsnClose</c>（提权 Core 内 P/Invoke，
/// 与 readMft 同族同审计；特权面评审记录见 PrimitiveRegistry）。
///
/// 与 <see cref="CoreMftClient"/> 同构：每批一连接（~1ms/批，500ms 轮询下完全可忽略）、
/// 先 hello 再 execute、Core 结构化错误**原码透传**。
/// </summary>
public sealed class CoreUsnClient : IUsnSource
{
    private readonly string _installRoot;
    private readonly string _volume;
    private long _journalId;

    /// <summary>最近一次心跳失败的结构化原因（成功或未调用 = null；S2 纪律：失败不吞）。</summary>
    public string? LastHeartbeatError { get; private set; }

    public CoreUsnClient(string installRoot, string volume)
    {
        _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
    }

    public UsnJournalInfo QueryJournal()
    {
        try
        {
            var result = ExecuteCore(PrimitiveNames.VolumeQueryJournal, new JsonObject
            {
                ["volume"] = _volume,
            });

            _journalId = result?["journalId"]?.GetValue<long>() ?? 0;
            return new UsnJournalInfo(
                UsnJournalStatus.Ok,
                _journalId,
                result?["firstUsn"]?.GetValue<long>() ?? 0,
                result?["nextUsn"]?.GetValue<long>() ?? 0,
                0);
        }
        catch (MftReaderException ex)
        {
            return FromError(ex);
        }
    }

    public UsnBatch ReadUsn(long fromUsn, int bufferBytes)
    {
        // journalId 由 QueryJournal 建立（自举对账链保证顺序；防御兜底）
        if (_journalId == 0)
        {
            var info = QueryJournal();
            if (!info.IsOk)
            {
                throw new UsnSourceException(info.Status, info.Win32Error, "journal 不可用（QueryJournal 未建立游标）");
            }
        }

        try
        {
            var result = ExecuteCore(PrimitiveNames.VolumeReadUsn, new JsonObject
            {
                ["volume"] = _volume,
                ["journalId"] = _journalId,
                ["fromUsn"] = fromUsn,
                ["maxBytes"] = bufferBytes,
            });

            var records = new List<UsnRecord>(64);
            if (result?["records"] is JsonArray arr)
            {
                foreach (var node in arr)
                {
                    records.Add(new UsnRecord(
                        node?["usn"]?.GetValue<long>() ?? 0,
                        unchecked((ulong)(node?["frn"]?.GetValue<long>() ?? 0)),
                        unchecked((ulong)(node?["parent"]?.GetValue<long>() ?? 0)),
                        unchecked((uint)(node?["reason"]?.GetValue<long>() ?? 0)),
                        unchecked((uint)(node?["attributes"]?.GetValue<long>() ?? 0)),
                        node?["name"]?.GetValue<string>() ?? string.Empty));
                }
            }

            return new UsnBatch(result?["nextUsn"]?.GetValue<long>() ?? fromUsn, records);
        }
        catch (MftReaderException ex)
        {
            var info = FromError(ex);
            throw new UsnSourceException(info.Status, info.Win32Error, ex.Message);
        }
    }

    public bool TryWriteCloseRecord()
    {
        LastHeartbeatError = null;
        try
        {
            var result = ExecuteCore(PrimitiveNames.VolumeWriteUsnClose, new JsonObject
            {
                ["volume"] = _volume,
            });
            return result?["ok"]?.GetValue<bool>() == true;
        }
        catch (MftReaderException ex)
        {
            // 心跳失败 = 显式降级（JournalTail 记一次警告），不抛；但原因必须可见（S2：失败不吞）。
            LastHeartbeatError = ex.Message;
            return false;
        }
    }

    /// <summary>Core 错误 → journal 状态（-32021 = journal 不可用 ⇒ 静态快照语义；其余 = 一般错误）。</summary>
    private static UsnJournalInfo FromError(MftReaderException ex) => new(
        ex.ErrorCode == PrimitiveErrorCodes.JournalUnavailable
            ? UsnJournalStatus.NotActive
            : UsnJournalStatus.Error,
        0, 0, 0, ex.ErrorCode);

    private JsonNode? ExecuteCore(string primitive, JsonObject args)
    {
        var endpoint = CoreEndpoint.TryRead(_installRoot)
            ?? throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable,
                "Core 特权服务未运行（无端点登记）。USN 同步需要 `ezt core start --elevate`。");

        if (!IsPidAlive(endpoint.Pid))
        {
            CoreEndpoint.Remove(_installRoot);
            throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable,
                $"Core 端点已登记（pid={endpoint.Pid}）但进程已不存在（陈旧端点，已清理）。");
        }

        using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut);
        try
        {
            client.ConnectAsync(5000).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable,
                $"Core 端点已登记（pid={endpoint.Pid}）但连接超时 —— Core 可能已僵死。");
        }
        catch (IOException ex)
        {
            throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable, $"无法连接 Core 管道（{ex.Message}）");
        }

        using var reader = new StreamReader(
            client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        var hello = CoreMftClient.Request(reader, writer, CoreProtocolMethods.Hello, new JsonObject
        {
            ["hostName"] = "ezt-index",
            ["hostPid"] = Environment.ProcessId,
        });
        if (hello?["ok"]?.GetValue<bool>() != true)
        {
            throw new MftReaderException(PrimitiveErrorCodes.CoreUnavailable, "Core 握手未返回 ok=true");
        }

        return CoreMftClient.Request(reader, writer, CoreProtocolMethods.Execute, new JsonObject
        {
            ["name"] = primitive,
            ["args"] = args,
            ["toolId"] = "ezt-index",
            ["callerPid"] = Environment.ProcessId,
        });
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        // 连接不跨调用持有（每批一连接），无可释放资源
    }
}
