// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Eztools.Index;

/// <summary>
/// ezt-index 启动自举（W3-b-4 宿主接线）：把"真卷 → IndexStore → SearchService"的编排落地。
///
/// 流程（每卷独立，一卷失败不拖垮其他卷 —— 部分成功也算 ready，让搜索先覆盖可用卷）：
///   1. 列卷：本地固定卷（DriveType.Fixed 且 Ready），经 <see cref="VolumeWorker.OrderVolumes"/> 归一（C: 优先）；
///   2. 热启动：`&lt;dataRoot&gt;/index/&lt;volumeSerial&gt;.ezidx` 存在且卷序列号匹配 → 直接 mmap 加载（W3-a-4，实测 190ms）；
///   3. 全量重建：连 Core 走 <see cref="MftReader"/>（真进程 = <see cref="CoreMftClient"/>，selftest 注入假 reader）；
///   4. 成功即 <see cref="Persister.Save"/>（W3-a-4 落盘时机 = "构建完成"时点）；
///   5. 全部就绪后 <see cref="SearchService.InstallVolumes"/> 热替换 —— ready 之前 query 一律 -32001（协议 §3.1）。
///
/// 可测试性（S9/S14）：编排逻辑全部可注入 —— reader 注入假实现、卷清单直接给、磁盘目录用临时根；
/// 只有 <see cref="CoreMftClient"/> 与卷序列号 P/Invoke 是真进程专属薄层（不在零进程断言面内）。
///
/// 已知边界（显式登记，不做静默降级）：
///   - ~~readMft 不返回 USN journal id / nextUsn ⇒ Save 的两字段写 0~~ ✅ W3-c-3 关闭：
///     重建完成时点经 FSCTL_QUERY_USN_JOURNAL 建游标（UsnSourceFactory 非空时），
///     journal 不可用 ⇒ 如实落 (0,0)，下次启动按"无游标"重建建立游标；
///   - 卷序列号用 GetVolumeInformation 本地读取（中完整性可调，不碰 Core —— Core 改动须走提权评审）；
///   - flags 一律 0（readMft 无 attributes）；USN 增量路径的**新/改条目**带真实
///     FileAttributes 映射（UsnRecordParser.MapFlags），存量条目首改时补上。
/// </summary>
public static class IndexBootstrap
{
    /// <summary>.ezidx 落盘子目录（设计方案 §3.4：`&lt;安装根&gt;/index/`；回滚 = 删该目录即回全量重建）。</summary>
    public const string IndexDirName = "index";

    /// <summary>自举选项。</summary>
    public sealed class Options
    {
        /// <summary>安装根：Core 端点（core.json）与 .ezidx 落盘的根目录。宿主拉起时传 paths.Root。</summary>
        public required string DataRoot { get; init; }

        /// <summary>卷清单（"C:"/"D:" 形态）。null = 默认本地全部固定卷。</summary>
        public IReadOnlyList<string>? Volumes { get; init; }

        /// <summary>读取注入。null = 真 Core 通路（<see cref="CoreMftClient"/>）。</summary>
        public MftReader? Reader { get; init; }

        /// <summary>false = 跳过持久化（纯内存，重启重建；selftest 的临时目录场景用）。</summary>
        public bool Persist { get; init; } = true;

        /// <summary>
        /// 卷序列号解析注入（"C:" → serial）。null = 真 P/Invoke（<see cref="GetVolumeSerialNumber"/>）。
        /// 测试注入假 resolver：换盘拒载（.ezidx 的 serial ≠ resolver 返回值 ⇒ 重建）与
        /// "读不到序列号 ⇒ 该卷失败"两条路径否则只能真插拔磁盘才能触发。
        /// </summary>
        public Func<string, ulong?>? VolumeSerialResolver { get; init; }

        /// <summary>
        /// USN 源工厂（W3-c-3 同步层注入）。null = **同步整体停用**（W3-a-4 原语义：
        /// journal 字段落 0、静态快照、不增量）；非 null = 每卷调一次，返回 null 表示该卷
        /// journal 打不开（按静态快照处理）。真进程 = <see cref="UsnJournalSource.TryOpen"/>。
        /// </summary>
        public Func<string, IUsnSource?>? UsnSourceFactory { get; init; }

        /// <summary>进度/诊断落点（stderr 或测试捕获）。每卷一行汇总。</summary>
        public TextWriter? Diagnostics { get; init; }

        /// <summary>
        /// 卷筛选注入（W3-e-2）。null = 真实盘符扫描（<see cref="VolumeClassifier.ScanLocalDrives"/>）。
        /// 注入假扫描器才能零设备验证"跳过可见 + 原因枚举 + 计数守恒"。
        /// <b>显式指定 <see cref="Volumes"/> 时本项被忽略</b>（调用方已经圈定了范围，
        /// 此时"跳过"无从谈起 —— 报告里如实为空，不编造）。
        /// </summary>
        public Func<VolumeScan>? DriveScan { get; init; }

        /// <summary>
        /// 增量补齐批数上限（W3-c 有界化，2026-09-25）。null = <see cref="JournalTail.DefaultMaxBatches"/>。
        /// <b>注入小值才能零设备断言"追不平 ⇒ 可见降级为全量重扫"</b>（29.4）——
        /// 否则测试得等到 20 万批 / 10 s 才看得到降级。
        /// </summary>
        public int? DrainMaxBatches { get; init; }

        /// <summary>增量补齐时长上限（ms）。null = <see cref="JournalTail.DefaultMaxMs"/>。</summary>
        public int? DrainMaxMs { get; init; }
    }

    /// <summary>自举结果（全数字，供 stderr 诊断与断言）。</summary>
    public sealed record BootstrapReport(
        int VolumesTotal,
        int VolumesLoaded,      // 热启动复用
        int VolumesBuilt,       // 全量重建
        int VolumesFailed,
        long TotalEntries,
        double ElapsedMs,
        IReadOnlyList<string> Errors,
        IReadOnlyList<VolumeSyncOutcome> SyncOutcomes,
        IReadOnlyList<SkippedVolume> SkippedVolumes,
        IReadOnlyList<FailedVolume> FailedVolumes)
    {
        /// <summary>
        /// 检测到的卷总数 = **尝试过的**（<see cref="VolumesTotal"/>，含失败）+ **跳过的**。
        /// 两个下式**必须同时成立**（缺口②的守恒面，selftest 31.x 双向钉住）：
        ///   ① <c>VolumesLoaded + VolumesBuilt + VolumesFailed == VolumesTotal</c>（尝试内部三分）；
        ///   ② <c>VolumesTotal + SkippedVolumes.Count == DetectedVolumes</c>（检测外部二分）。
        /// </summary>
        public int DetectedVolumes => VolumesTotal + SkippedVolumes.Count;
    }

    /// <summary>
    /// 单卷同步结局（W3-c-3 验收纪律：**"重扫"是显式状态，不靠日志文本猜**）。
    /// Plan=Incremental ⇒ <see cref="NextUsn"/> 为实时 tail 起点（Program 据此起泵）。
    /// </summary>
    public sealed record VolumeSyncOutcome(
        string Volume,
        SyncPlanKind Plan,
        string Reason,
        long JournalId,
        long FirstUsn,
        long NextUsn,
        int AppliedBatches,
        int AppliedRecords)
    {
        /// <summary>
        /// 缺口③ 取证面：本轮增量补齐中贡献记录最多的文件名（按条数降序，最多 5 条；空 = 没跑补齐/无记录）。
        /// **结构化**而非只写进 <see cref="Reason"/> 文本 —— 只有结构才能被断言、被突变验证钉住。
        /// </summary>
        public IReadOnlyList<RecordSource> TopSources { get; init; } = [];

        /// <summary>
        /// 缺口③：本轮"追不平"是否由**我们自己的文件**（安装根下的日志/索引/core.json）造成。
        /// true ⇒ 自喂回路（代码在自己的日志上打转），而非"卷太忙"。
        /// </summary>
        public bool SelfInflicted { get; init; }
    }

    /// <summary>
    /// 执行自举并把结果装进 <paramref name="service"/>（<see cref="SearchService.InstallVolumes"/>）。
    /// **不抛异常**：每卷失败都进 Errors（可见失败优于抛穿后台线程 —— 未观察的 Task 异常是静默失败）。
    /// </summary>
    public static async Task<BootstrapReport> RunAsync(
        SearchService service, Options opt, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var errors = new List<string>();
        var failedVolumes = new List<FailedVolume>();
        var targets = new List<VolumeTarget>();
        int loaded = 0, built = 0, failed = 0;
        long totalEntries = 0;

        // 卷筛选（W3-e-2）：显式清单优先（跳过无从谈起 ⇒ 空）；否则分类真实盘符
        // ——**被跳过的卷必须带着原因进报告**，绝不静默丢弃（S9′ 同族）。
        IReadOnlyList<string> volumes;
        IReadOnlyList<SkippedVolume> skippedVolumes;
        if (opt.Volumes is { } explicitVolumes)
        {
            volumes = explicitVolumes;
            skippedVolumes = Array.Empty<SkippedVolume>();
        }
        else
        {
            var scan = (opt.DriveScan ?? VolumeClassifier.ScanLocalDrives)();
            volumes = scan.Indexed;
            skippedVolumes = scan.Skipped;
        }

        var ordered = VolumeWorker.OrderVolumes(volumes);
        var syncOutcomes = new List<VolumeSyncOutcome>();

        // 缺口③：自有文件判据**只算一次**（枚举安装根目录；每卷重算是无谓的 IO）。
        // 取不到就退化成空集 ⇒ 结果里 SelfInflicted=false（如实，不编结论）——见 CollectOwnNames 注释。
        var ownNames = JournalTail.CollectOwnNames(opt.DataRoot);

        foreach (var sk in skippedVolumes)
        {
            await WriteDiagAsync(opt, $"volume {sk.Volume}: 跳过（{sk.Reason}）——{sk.ReasonText}")
                .ConfigureAwait(false);
        }

        foreach (var volume in ordered)
        {
            ct.ThrowIfCancellationRequested();

            var serial = (opt.VolumeSerialResolver ?? GetVolumeSerialNumber)(volume);
            if (serial is null)
            {
                failed++;
                errors.Add($"卷 {volume} 读不到卷序列号（未就绪或非本地卷）");
                // 结构化同落（缺口②）：自由文本给人看，结构体给断言与 UI 用 —— 两者都要，
                // 但**守恒式只认结构体**（原来只有文本 ⇒ `0+0≠2` 无人发现）。
                failedVolumes.Add(VolumeClassifier.Failure(
                    volume, VolumeClassifier.SerialUnavailableCode, "读不到卷序列号（未就绪或非本地卷）"));
                await WriteDiagAsync(opt, $"volume {volume}: 失败（读不到卷序列号）").ConfigureAwait(false);
                continue;
            }

            IndexStore? store = null;
            ulong hdrJournalId = 0;
            long hdrNextUsn = 0;
            bool hotLoaded = false;
            int hotEntries = 0;
            string? rebuildReason = null;   // 非 null = 对账判定 FullRebuild 的原因（重建后拼进 outcome）
            long journalId = 0;
            long tailCursor = 0;
            bool rebuilt = false;
            // 缺口③：本轮补齐的取证面，跨"放弃→重建"分支保留，最后统一贴到终态 outcome 上。
            IReadOnlyList<RecordSource> drainSources = [];
            bool selfInflicted = false;

            // ── 热启动优先（W3-a-4：.ezidx + 卷序列号匹配 ⇒ 拒绝换盘旧索引）──
            // GetIndexPath 自带 index/ 子目录（&lt;root&gt;/index/vol-&lt;serial:X16&gt;.ezidx，回滚 = 删 index/）
            if (opt.Persist)
            {
                var path = Persister.GetIndexPath(opt.DataRoot, serial.Value);
                var load = Persister.TryLoad(path, serial.Value);
                if (load.Status == EzidxLoadStatus.Ok && load.Store is not null)
                {
                    store = load.Store;
                    hotLoaded = true;
                    hotEntries = store.EntryCount;
                    hdrJournalId = load.Header?.UsnJournalId ?? 0;
                    hdrNextUsn = load.Header?.NextUsn ?? 0;
                    loaded++;
                    totalEntries += store.EntryCount;
                    await WriteDiagAsync(opt, $"volume {volume}: 热启动复用 {store.EntryCount} 条（{path}）")
                        .ConfigureAwait(false);
                }
                else if (load.Status != EzidxLoadStatus.FileMissing)
                {
                    // 存在但拒绝加载（BadMagic/BadVersion/CrcMismatch/…）：如实记录后走重建
                    //（宁可重建也不吞 —— 结构化拒绝的第五级语义就是"绝不尽力解析"）
                    await WriteDiagAsync(opt, $"volume {volume}: .ezidx 拒绝加载（{load.Status}）⇒ 全量重建")
                        .ConfigureAwait(false);
                }
            }

            // ── 同步层判定（W3-c-3：设计方案 §4.2 判定链）──
            // source 生命周期：自举内用完即关（Drain 完毕）；实时 tail 由 Program 重开一个
            //（TryOpen = 开句柄 + 一次 query，微秒级 —— 换进程边界清晰的持有权，不搞共享）。
            var outcome = new VolumeSyncOutcome(
                volume, SyncPlanKind.StaticSnapshot, "USN 同步未启用", 0, 0, 0, 0, 0);
            IUsnSource? source = null;
            try
            {
                if (opt.UsnSourceFactory is not null)
                {
                    source = opt.UsnSourceFactory(volume);
                    if (source is null)
                    {
                        outcome = outcome with { Reason = "USN journal 不可用（打开失败）" };
                    }
                    else
                    {
                        var jinfo = source.QueryJournal();
                        if (!jinfo.IsOk)
                        {
                            outcome = outcome with
                            {
                                Reason = $"journal 不可用（{jinfo.Status}，Win32={jinfo.Win32Error}）",
                            };
                        }
                        else if (hotLoaded)
                        {
                            var plan = UsnReconciler.Decide(hdrJournalId, hdrNextUsn, jinfo);
                            if (plan.Kind == SyncPlanKind.Incremental)
                            {
                                // 游标有效 ⇒ 增量补齐：从 header 游标追平到实时（Drain 到空批），
                                // 补齐结果直接应用到热加载的 store（InstallVolumes 之前，无并发）
                                var applier = new UsnApplier();
                                var drainer = new JournalTail(
                                    volume, source, recs => applier.Apply(recs, store!), opt.Diagnostics,
                                    ownNames: ownNames);
                                var drained = drainer.Drain(
                                    hdrNextUsn,
                                    opt.DrainMaxBatches ?? JournalTail.DefaultMaxBatches,
                                    opt.DrainMaxMs ?? JournalTail.DefaultMaxMs,
                                    // ①′ 目标点 = 本次自举开始时量到的 journal 尾（上面的 jinfo）。
                                    //    没有它，Drain 只能靠"读到空批"判定追平 —— 而卷上只要有
                                    //    持续写入，空批就永远不出现（真机实证见踩坑 §2.21）。
                                    jinfo.NextUsn);
                                // 取证面无论成败都留（缺口③：成败都能回答"谁在写"）
                                drainSources = drained.TopSources;
                                selfInflicted = drained.SelfInflicted;
                                if (!drained.CaughtUp)
                                {
                                    // 🔴 追不平 / 游标不前进（2026-09-25 真机实证，踩坑 §2.21）：
                                    //    热加载的旧快照**不能**当增量起点 —— 否则会带着"半追平"的
                                    //    索引继续服务，丢掉的变更窗口谁也说不清。宁可付一次全量重扫，
                                    //    也不留静默丢变更（§4.2 红表第一行同款纪律）。原因写进诊断与
                                    //    outcome（S9′：降级必须可见，且说得出为什么）。
                                    await WriteDiagAsync(
                                        opt, $"volume {volume}: 增量补齐放弃（{drained.StoppedBecause}）⇒ 全量重扫")
                                        .ConfigureAwait(false);
                                    loaded--;
                                    totalEntries -= hotEntries;
                                    store = null;
                                    rebuildReason = drained.StoppedBecause;
                                }
                                else
                                {
                                    journalId = jinfo.JournalId;
                                    tailCursor = drainer.Cursor;
                                    outcome = new VolumeSyncOutcome(
                                        volume, SyncPlanKind.Incremental,
                                        $"增量补齐 {drained.Records} 条（{drained.Batches} 批）",
                                        journalId, jinfo.FirstUsn, tailCursor, drained.Batches, drained.Records);
                                    if (opt.Persist)
                                    {
                                        SaveIndex(opt, store!, serial.Value, journalId, tailCursor, errors, volume);
                                    }
                                }
                            }
                            else if (plan.Kind == SyncPlanKind.FullRebuild)
                            {
                                // 🔴 journal 重建 / 游标越界 ⇒ 旧游标无意义，丢弃热加载走重建。
                                // 宁可 11s 重扫，不可从中间读（静默丢变更且退出码 0，§4.2 红表第一行）
                                await WriteDiagAsync(opt, $"volume {volume}: {plan.Reason} ⇒ 全量重扫")
                                    .ConfigureAwait(false);
                                loaded--;
                                totalEntries -= hotEntries;
                                store = null;
                                rebuildReason = plan.Reason;
                            }
                            else
                            {
                                outcome = outcome with { Reason = plan.Reason };
                            }
                        }
                        // 非 hotLoaded（首次建索引）⇒ 重建后统一建游标，这里不动
                    }
                }

                // ── 全量重建（CoreMftClient → VolumeWorker；失败只记 Errors，不拖垮其他卷）──
                if (store is null)
                {
                    rebuilt = true;
                    store = new IndexStore();
                    var reader = opt.Reader ?? new CoreMftClient(opt.DataRoot).ReadBatch;
                    var report = await VolumeWorker.EnumerateVolumeAsync(store, volume, reader, ct).ConfigureAwait(false);
                    if (report.Error is not null)
                    {
                        failed++;
                        errors.Add($"卷 {volume} 枚举失败（code={report.ErrorCode}）: {report.Error}");
                        // 结构化同落（缺口②）：`ErrorCode` **原码透传**，绝不归一成"失败"——
                        // 用户要看到的是"D: 访问被拒绝"而不是"有个卷没成"。
                        failedVolumes.Add(VolumeClassifier.Failure(
                            volume, report.ErrorCode ?? VolumeClassifier.SerialUnavailableCode, report.Error));
                        await WriteDiagAsync(opt,
                                $"volume {volume}: 枚举失败（code={report.ErrorCode}）——{report.Error}")
                            .ConfigureAwait(false);
                        continue;
                    }

                    built++;
                    totalEntries += report.Count;
                    await WriteDiagAsync(opt,
                            $"volume {volume}: 全量枚举 {report.Count} 条 / {report.Batches} 批 / 重放 {report.Replays} / "
                            + $"更新 {report.Updates} / 升序={report.Sorted} / {report.ElapsedMs} ms")
                        .ConfigureAwait(false);

                    // 重建完成时点查 journal：NextUsn 即无损增量起点（枚举期间的变更全部
                    // 落在 [枚举开始, NextUsn) 之外 —— journal 里还在，tail 从这里读不丢）
                    if (source is not null)
                    {
                        var j2 = source.QueryJournal();
                        if (j2.IsOk)
                        {
                            journalId = j2.JournalId;
                            tailCursor = j2.NextUsn;
                            outcome = new VolumeSyncOutcome(
                                volume, SyncPlanKind.Incremental,
                                (rebuildReason is null ? "全量重建" : rebuildReason + " ⇒ 全量重建")
                                + $"，游标 = NextUsn {tailCursor}",
                                journalId, j2.FirstUsn, tailCursor, 0, 0);
                        }
                        else
                        {
                            outcome = outcome with
                            {
                                Reason = (rebuildReason ?? "全量重建")
                                + $"；journal 不可用（{j2.Status}，Win32={j2.Win32Error}）⇒ 静态快照",
                            };
                        }
                    }
                }

                // ── 落盘（构建完成时点；W3-c 起 journalId/nextUsn 落真实游标，不再恒 0）──
                // 静态快照的重建也落 (0,0)：下次启动 journalId=0 ⇒ "无游标"判定如实生效
                if (opt.Persist && rebuilt)
                {
                    SaveIndex(opt, store, serial.Value, journalId, tailCursor, errors, volume);
                }
            }
            finally
            {
                source?.Dispose();
            }

            // P4：自有子树作用域 —— 位置是刻意的：**在增量补齐之后**才标，
            // 于是这个集合描述的正是"此刻 store 的真实内容"，不需要任何持久化或跨表对账。
            // 代价 = 一趟 O(n)（且锚点之前的槽位整段剪掉），只对 DataRoot 所在的那一个卷非空。
            var ownScope = OwnScope.Mark(store, volume, opt.DataRoot);
            if (ownScope.Anchored)
            {
                await WriteDiagAsync(opt, $"volume {volume}: {ownScope.Reason}").ConfigureAwait(false);
            }

            targets.Add(new VolumeTarget(volume, store, ownScope));

            // 缺口③：终态 outcome 统一贴取证面（跨"放弃→全量重建"分支保留下来）。
            // 只在真有记录时贴 —— 空集合贴上去只会让消费方多一层"这到底是没跑还是没记录"的歧义。
            if (drainSources.Count > 0)
            {
                outcome = outcome with { TopSources = drainSources, SelfInflicted = selfInflicted };
            }

            // 自喂回路 = 我们自己的日志/索引文件落在正被 tail 的卷上，导致 readUsn 每次带回
            // 自己刚写的记录（缺口③根因）。这一行让**任何用户环境**都能自证，不必复刻我们的实验台。
            if (selfInflicted)
            {
                var detail = string.Join("、", drainSources.Select(s => $"{s.Name}×{s.Hits}"));
                await WriteDiagAsync(opt,
                        $"volume {volume}: 追不平系**自喂回路**（过半记录来自安装根下的自有文件）；Top 来源：{detail}")
                    .ConfigureAwait(false);
            }

            syncOutcomes.Add(outcome);
        }

        if (targets.Count > 0)
        {
            // 热替换 + ready（部分成功也算 ready：搜索先覆盖可用卷，失败卷在 lastError 可见）
            service.InstallVolumes(targets);
        }

        if (errors.Count > 0)
        {
            service.LastError = string.Join("；", errors);
        }

        // 跳过清单落到服务面（ping/status 暴露给宿主与 UI —— "跳过必须可见"的落点）
        service.SkippedVolumes = skippedVolumes;

        // 失败清单同样落服务面（缺口②）：结构化的"试了没成"。
        // ★ 守恒式在此闭合：Volumes + Skipped + Failed == Detected（三档不重不漏）。
        service.FailedVolumes = failedVolumes;
        service.DetectedVolumes = ordered.Count + skippedVolumes.Count;

        sw.Stop();
        return new BootstrapReport(ordered.Count, loaded, built, failed, totalEntries,
            sw.ElapsedMilliseconds, errors, syncOutcomes, skippedVolumes, failedVolumes);
    }

    /// <summary>落盘（构建完成时点）。游标 = 重建/补齐后的真实值；静态快照落 (0,0)（"无游标"如实持久化）。</summary>
    private static void SaveIndex(
        Options opt, IndexStore store, ulong serial, long journalId, long nextUsn,
        List<string> errors, string volume)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(opt.DataRoot, IndexDirName));
            Persister.Save(store, Persister.GetIndexPath(opt.DataRoot, serial), serial,
                (ulong)Math.Max(journalId, 0), Math.Max(nextUsn, 0));
        }
        catch (Exception ex)
        {
            // 落盘失败不回滚内存索引（本次会话仍可查询），如实记录
            errors.Add($"卷 {volume} 落盘失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 默认卷清单：本地固定、就绪、NTFS 的卷（设计方案 q6）。
    /// W3-e-2 起改为<b>经 <see cref="VolumeClassifier"/></b> —— 被排除的卷不再是"消失"，
    /// 而是带原因进 <see cref="BootstrapReport.SkippedVolumes"/>（跳过必须可见）。
    /// </summary>
    public static IReadOnlyList<string> DefaultVolumes() => VolumeClassifier.ScanLocalDrives().Indexed;

    /// <summary>
    /// 卷序列号（Persister 换盘拒载的判据）。GetVolumeInformation 中完整性可调（只读卷信息，
    /// 与 §4.1 "USN 读取不需提权" 同源）；失败返回 null（调用方按"卷未就绪"处理）。
    /// </summary>
    internal static ulong? GetVolumeSerialNumber(string volumeRoot)
    {
        var root = volumeRoot.EndsWith(Path.DirectorySeparatorChar)
            ? volumeRoot
            : volumeRoot + Path.DirectorySeparatorChar;
        var nameBuf = new char[261];
        var fsBuf = new char[261];
        uint flags = 0;
        var ok = GetVolumeInformationW(root, nameBuf, (uint)nameBuf.Length, out uint serial,
            out _, ref flags, fsBuf, (uint)fsBuf.Length);
        return ok ? serial : null;
    }

    private static async Task WriteDiagAsync(Options opt, string message)
    {
        if (opt.Diagnostics is null)
        {
            return;
        }

        await opt.Diagnostics.WriteLineAsync("[ezt-index] " + message).ConfigureAwait(false);
        await opt.Diagnostics.FlushAsync().ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        [Out] char[] lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        ref uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer,
        uint nFileSystemNameSize);
}
