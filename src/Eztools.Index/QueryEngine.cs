// ============================================================================
// Eztools.Index / QueryEngine —— W3-b 查询核心（实施计划 W3-b-1/2/3）
//
// 职责（设计方案 §5，单一职责不越界）：
//   W3-b-1 三种匹配模式：前缀 / 子串 / 模糊（fzf 式子序列 + 连续度得分）；
//   W3-b-2 分层打分 + 固定大小最小堆 Top-K（顺序是契约，不是实现细节）；
//   W3-b-3 递减式查询管道：超阈值只回 total + 在上一轮结果集上二次筛选。
//
// 性能红线（W3-b-1 验收①）：各模式 100 万条 P95 ≤ 20 ms。为此：
//   - 匹配全部在 UTF-16 上进行（§5.1 🔴：先转 UTF-8 再比 = 转换成本远超收益）；
//   - 每候选把名字从堆解码进**栈上缓冲**（零分配），折叠进第二个栈缓冲后走
//     `MemoryExtensions.StartsWith/IndexOf`（Ordinal = SIMD 路径，禁止手写循环）；
//   - **大小写折叠只有一个入口** <see cref="Fold"/>：查询侧折一次、名字侧每候选
//     折一次，两侧都调同一个函数（实施计划 W3-b-1 风险行 🟡 的应对）；
//   - Top-K 只在"堆未满或 score 优于堆顶"时才记候选（§5.2 🔴：百万次对象分配 = GC
//     灾难）；候选是 struct，结果对象（SearchHit）只在收敛后的 ≤ K 个上构造。
//
// 打分与 §5.2 草案的落地偏差（推荐方案，登记于设计方案 §5.5）：
//   **路径深度**不参与扫描期打分（每候选走父链 = 百万次二分，🔴 不可接受），改为在
//   收敛后的 ≤ K 个候选上做**精排**（深度走父链回溯，≤ K 次回溯，零预算压力）。
//   代价：K 边界由"名字分"决定，深度极端的条目可能被挤进/挤出 Top-K —— 已知且接受。
//
// 模糊模式与协议的关系：W3 搜索协议 V1.0（冻结）只有 prefix/substr 两态（`substr` 字段），
// 模糊是**引擎内部能力**（§5.1：查询含空格触发；协议不暴露 = 无 V2.0 变更）。
//
// 并发：QueryEngine 内含递减管道状态（可变），**单线程**使用（与 IndexStore 同契约）。
// ============================================================================

using System.Text;

namespace Eztools.Index;

/// <summary>查询命中区间（UTF-16 code unit 网格；协议 §3.2.2 highlights 的内存形态）。</summary>
public readonly record struct HighlightRange(int Start, int Length);

/// <summary>单条查询命中（协议 hits[] 的内存形态）。仅在收敛后的 ≤ K 个候选上构造。</summary>
public sealed record SearchHit(
    ulong Frn,
    string Name,
    bool Dir,
    string Path,
    IReadOnlyList<HighlightRange> Highlights);

/// <summary>一次查询的结果。<see cref="Total"/> 是**全库命中总数**（截断前），与 <see cref="Hits"/> 分离（协议 §3.2.2 硬契约）。</summary>
public sealed record QueryResult(
    int Total,
    double ElapsedMs,
    IReadOnlyList<SearchHit> Hits,
    bool Thresholded);

/// <summary>匹配模式（引擎内部；协议 V1.0 只暴露 Prefix/Substring 两态）。</summary>
public enum MatchMode
{
    Prefix,
    Substring,
    Fuzzy,
}

/// <summary>查询引擎可调参数（实施计划 W3-b-3 风险行 🟡：阈值设为常量**并允许配置**，断言只锁行为分界）。</summary>
public sealed class QueryOptions
{
    /// <summary>命中数超过此值 ⇒ 只返回 total 不返回列表（W3-b-3）。默认 10 万。</summary>
    public int DecreasingThreshold { get; init; } = 100_000;

    /// <summary>上一轮结果集可缓存的占比上限（命中 &gt; 全量 20% ⇒ 不缓存，全量扫更快）。</summary>
    public double CacheMaxTotalRatio { get; init; } = 0.20;

    /// <summary>目录路径 LRU 容量（设计方案 §3.5：按目录缓存，512 条命中率极高）。</summary>
    public int PathCacheCapacity { get; init; } = 512;
}

/// <summary>
/// 查询引擎：跨卷扫描 + 打分 + Top-K + 递减管道 + 路径回溯。
/// 每卷一个 <see cref="IndexStore"/>（FRN 只在卷内唯一，W3-a-3 契约），查询跨卷合并。
/// </summary>
public sealed class QueryEngine
{
    /// <summary>引擎 API 的 limit 上限（协议层另有更严的 ≤ 200 约束，协议 §3.2.1）。</summary>
    public const int MaxLimit = 2000;

    // ── 打分权重（命名常量 + 理由，设计方案 §5.2；断言只锁"相对顺序"不锁绝对分）──

    /// <summary>前缀命中（位置 0）：体感上最"精确"的命中。</summary>
    public const int ScorePrefix = 1000;

    /// <summary>词首命中（位置前是分隔符）：如 "my-report" 命中 "rep"。</summary>
    public const int ScoreWordStart = 800;

    /// <summary>普通子串命中。</summary>
    public const int ScoreSubstring = 500;

    /// <summary>模糊命中基分（连续度加分封顶后必须 &lt; ScoreSubstring —— 全连续的模糊 ≈ 子串，不应排在它前面）。</summary>
    public const int ScoreFuzzyBase = 400;

    /// <summary>模糊连续奖励：连续段每多 1 个查询字符 +15，封顶 +60（"ab" 连续命中 "abc" 好过 a…b 离散）。</summary>
    public const int FuzzyStreakBonusPerChar = 15;

    /// <summary>模糊连续奖励上限。</summary>
    public const int FuzzyStreakBonusMax = 60;

    /// <summary>模糊词首奖励：命中的查询字符落在分隔符后 +15，封顶 +30。</summary>
    public const int FuzzyWordBonusPerChar = 15;

    /// <summary>模糊词首奖励上限。</summary>
    public const int FuzzyWordBonusMax = 30;

    /// <summary>词首判定分隔符（在折叠后的名字上判定；分隔符本身无大小写）。</summary>
    private const string WordSeparators = " .-_()[]{},;'!@#$%^&+=~";

    /// <summary>栈上名字缓冲（UTF-16 字符数）。NTFS 单组件上限 255 字符 ⇒ ≤ 1020 B UTF-8，2048 有 2 倍余量。</summary>
    private const int NameBufferChars = 2048;

    /// <summary>父链防御上限（成环/异常超长的兜底；正常 NTFS 深度远小于此）。</summary>
    private const int MaxChainDepth = 128;

    private readonly IReadOnlyList<VolumeTarget> _volumes;
    private readonly QueryOptions _options;
    private readonly PathResolver?[] _resolvers; // 按卷惰性创建（无命中的卷永不建缓存）

    // ── 递减管道状态（W3-b-3：上一轮结果集）──
    private string? _cacheQuery;
    private bool _cacheSubstr;
    private List<CachedEntry>? _cacheFrns;

    /// <summary>管道缓存当前条数（诊断观测量）。</summary>
    public int CachedEntryCount => _cacheFrns?.Count ?? 0;

    /// <summary>search.start 副作用观测量：管道复位次数（协议 §3.1"清空递减式管道缓存"）。</summary>
    public int PipelineResetCount { get; private set; }

    /// <summary>诊断拆分（常驻）：最近一次查询的扫描段耗时 ms（位图预筛 + 解码折叠 + 匹配 + 堆）。</summary>
    public double LastScanMs { get; private set; }

    /// <summary>诊断拆分（常驻）：最近一次查询的命中构建段耗时 ms（精排 + 结果构造 + 路径回溯）；超阈值路径为 0。</summary>
    public double LastBuildMs { get; private set; }

    public QueryEngine(IReadOnlyList<VolumeTarget> volumes, QueryOptions? options = null)
    {
        _volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        _options = options ?? new QueryOptions();
        _resolvers = new PathResolver?[volumes.Count];
    }

    /// <summary>清空递减管道缓存（协议 search.start 的副作用，§3.1）。</summary>
    public void ResetPipeline()
    {
        _cacheQuery = null;
        _cacheFrns = null;
        PipelineResetCount++;
    }

    /// <summary>
    /// 执行一次查询。<paramref name="query"/> 含空格 ⇒ 模糊模式（§5.1 触发规则），
    /// 否则 <paramref name="substr"/> 决定前缀/子串。
    /// </summary>
    public QueryResult Query(string query, bool substr, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var mode = SelectMode(query, substr);

        // 折叠入口唯一（Fold）：查询侧折一次；名字侧在扫描循环里每候选折一次。
        Span<char> qbuf = stackalloc char[NameBufferChars];
        if (query.Length > qbuf.Length)
        {
            throw new ArgumentException($"查询长度 {query.Length} 超过上限 {NameBufferChars}", nameof(query));
        }

        var foldedQuery = qbuf[..Fold(query, qbuf)];

        // ── 递减管道：上一轮结果集上二次筛选（W3-b-3 ②）──
        // 复用条件（全部满足才走子集，否则回退全量扫）：
        //   非模糊（模糊不走管道，简化语义）
        //   ∧ 查询是上一轮的**延伸**（StartsWith —— 打字变长）
        //   ∧ substr 开关没变（模式不同 ⇒ 旧集合不是新查询的超集，子集筛选会漏结果）
        List<CachedEntry>? subset = null;
        if (mode != MatchMode.Fuzzy
            && _cacheQuery is { } cached
            && substr == _cacheSubstr
            && query.StartsWith(cached, StringComparison.Ordinal))
        {
            subset = _cacheFrns;
        }
        else
        {
            _cacheQuery = null;
            _cacheFrns = null;
        }

        int total = 0;
        var heap = new MinHeap(limit);
        List<CachedEntry>? allHits = null; // 仅在"可能进缓存"时追加（上限见下，防 1M 命中白加百万次）
        bool cacheOverflowed = false;
        // ratio ≤ 0 ⇒ 管道禁用（性能验收口径）：连收集都不做，否则每轮白建 10 万级
        // List + 扩容拷贝，分配垃圾反过来污染下一轮的扫描段 P95（实测 61→19 ms 级差异）。
        int cacheCap = _options.CacheMaxTotalRatio <= 0 ? 0 : _options.DecreasingThreshold;

        if (subset is not null)
        {
            Span<char> nameBuf = stackalloc char[NameBufferChars];
            Span<char> foldBuf = stackalloc char[NameBufferChars];
            foreach (var ce in subset)
            {
                var store = _volumes[ce.Store].Store;

                // 自有子树排除（P4）：与全扫口径**必须**一致 —— 子集来自管道的上一轮命中，
                // 若增量期新条目落进了自有子树，这里不判就会让同一查询在两条路径上给出不同 total。
                if (_volumes[ce.Store].Own?.Contains(ce.Frn) == true)
                {
                    continue;
                }

                int slot = store.FindSlot(ce.Frn);
                if (slot < 0)
                {
                    continue; // 上轮命中后条目被删（墓碑）⇒ 跳过；子集是超集，total 仍正确
                }

                int chars = DecodeName(store, slot, nameBuf);
                var folded = foldBuf[..Fold(nameBuf[..chars], foldBuf)];
                if (!TryMatch(folded, foldedQuery, mode, out var quality, out var pos, out _))
                {
                    continue;
                }

                total++;
                heap.Consider(MakeCandidate(store, slot, ce.Store, ce.Frn, quality, pos, chars), limit);
            }
        }
        else
        {
            int entryTotal = 0;
            foreach (var v in _volumes)
            {
                entryTotal += v.Store.EntryCount;
            }

            for (int si = 0; si < _volumes.Count; si++)
            {
                var store = _volumes[si].Store;

                // 首字符位图预筛（W3-b-1 ⑤）：只对**前缀**模式有效（子串可命中任意位置，位图判定不了）；
                // 折叠入口与位图同源（Fold）；首字符非 ASCII 的查询跳过预筛（位图映射 & 0x7F 只对 ASCII 全保真）。
                if (mode == MatchMode.Prefix && foldedQuery[0] < 0x80
                    && !store.IsFirstCharPossible(foldedQuery[0]))
                {
                    continue; // 该卷必然 0 命中，整卷跳过
                }

                ScanStore(si, store, _volumes[si].Own, foldedQuery, mode, limit,
                    ref total, ref heap, ref allHits, ref cacheOverflowed, cacheCap);
            }

            // 管道缓存落位（W3-b-3 ③ 的另一半：什么不缓存）。cacheCap==0 = 管道禁用。
            if (cacheCap > 0 && mode != MatchMode.Fuzzy && !cacheOverflowed
                && total <= cacheCap && total <= _options.CacheMaxTotalRatio * Math.Max(entryTotal, 1))
            {
                _cacheQuery = query;
                _cacheSubstr = substr;
                _cacheFrns = allHits;
            }
            else
            {
                _cacheQuery = null;
                _cacheFrns = null;
            }
        }

        // ── W3-b-3 ①：命中数超阈值 ⇒ 只回 total 不回列表（协议 hits=[] + total）──
        if (total > _options.DecreasingThreshold)
        {
            sw.Stop();
            LastScanMs = sw.Elapsed.TotalMilliseconds;
            LastBuildMs = 0;
            return new QueryResult(total, LastScanMs, Array.Empty<SearchHit>(), Thresholded: true);
        }

        // 拆分计时（W3-b-1 验收①的定位手段，常驻诊断）：扫描段 = 位图预筛 + 解码折叠
        // + 匹配 + 堆；构建段 = 精排 + 结果对象构造 + 路径回溯。两段各有预算口径。
        sw.Stop();
        LastScanMs = sw.Elapsed.TotalMilliseconds;
        var bsw = System.Diagnostics.Stopwatch.StartNew();
        var hits = BuildHits(heap, foldedQuery, mode);
        bsw.Stop();
        LastBuildMs = bsw.Elapsed.TotalMilliseconds;
        return new QueryResult(total, LastScanMs + LastBuildMs, hits, Thresholded: false);
    }

    /// <summary>模式选择（§5.1：查询含空格 ⇒ 模糊；否则按 substr）。</summary>
    internal static MatchMode SelectMode(string query, bool substr) =>
        query.Contains(' ') ? MatchMode.Fuzzy : (substr ? MatchMode.Substring : MatchMode.Prefix);

    // ── 单一折叠入口（两侧都调它：查询侧折一次，名字侧每候选折一次）──

    /// <summary>
    /// 大小写折叠唯一入口。char→char 一比一（长度不变，折叠侧偏移可直接用作高亮区间）。
    /// ASCII 走快速路径（A-Z 位移，其余原样）—— 与 <see cref="char.ToLowerInvariant"/> 严格等价，
    /// 但省掉每字符的慢路径检查：100 万名 × ~10 字符的扫描热点上实测数 ms 级收益。
    /// </summary>
    internal static int Fold(ReadOnlySpan<char> src, Span<char> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            dst[i] = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A'))
                : (c < 0x80 ? c : char.ToLowerInvariant(c));
        }

        return src.Length;
    }

    // ── 扫描 ──

    private void ScanStore(
        int storeIndex, IndexStore store, OwnScope? own, ReadOnlySpan<char> foldedQuery, MatchMode mode, int limit,
        ref int total, ref MinHeap heap, ref List<CachedEntry>? allHits, ref bool cacheOverflowed, int cacheCap)
    {
        Span<char> nameBuf = stackalloc char[NameBufferChars];
        Span<char> foldBuf = stackalloc char[NameBufferChars];

        var heapBytes = store.HeapBytes;
        var offs = store.NameOffSlots;
        var lens = store.NameLenSlots;
        var frns = store.FrnSlots;

        for (int slot = 0; slot < lens.Length; slot++)
        {
            int len = lens[slot];
            if (len == IndexStore.TombstoneNameLen)
            {
                continue;
            }

            // 自有子树排除（P4）：必须在 `total++` 之前 —— 否则出现"共 N 条但列表里翻不到"
            // 的自相矛盾（这正是当初否决"只在 Top-K 之后过滤"的原因）。
            // 放在解码之前：被排除的条目连名字都不用解，扫描段反而更省。
            if (own is not null && own.Contains(frns[slot]))
            {
                continue;
            }

            // NTFS 名字 ≤ 255 UTF-16 字符（≤ 1020 B UTF-8）；超缓冲 = 索引被外部写坏 ⇒ fail-fast
            //（与 Persister 五级拒绝同精神：结构非法不静默吞）。
            if (len > NameBufferChars)
            {
                throw new InvalidOperationException(
                    $"名字堆内出现 {len} 字节的名字（超过 NTFS 255 字符上限的 2 倍缓冲），索引疑似损坏");
            }

            int chars = Encoding.UTF8.GetChars(heapBytes.Slice(offs[slot], len), nameBuf);
            var folded = foldBuf[..Fold(nameBuf[..chars], foldBuf)];

            if (!TryMatch(folded, foldedQuery, mode, out var quality, out var pos, out _))
            {
                continue;
            }

            total++;
            var frn = frns[slot];
            heap.Consider(MakeCandidate(store, slot, storeIndex, frn, quality, pos, chars), limit);

            // 缓存收集：只在"还没溢出"时追加；到上限即放弃整轮缓存（超阈值查询不为缓存白干）
            if (!cacheOverflowed)
            {
                allHits ??= new List<CachedEntry>(256);
                if (allHits.Count >= cacheCap)
                {
                    cacheOverflowed = true;
                    allHits = null;
                }
                else
                {
                    allHits.Add(new CachedEntry(storeIndex, frn));
                }
            }
        }
    }

    /// <summary>把槽位上的名字解码进缓冲（零分配；调用方保证 len ≤ NameBufferChars）。</summary>
    private static int DecodeName(IndexStore store, int slot, Span<char> nameBuf)
    {
        var heapBytes = store.HeapBytes;
        var offs = store.NameOffSlots;
        var lens = store.NameLenSlots;
        return Encoding.UTF8.GetChars(heapBytes.Slice(offs[slot], lens[slot]), nameBuf);
    }

    /// <summary>扫描期候选构造（struct；key = quality 主序 + 目录次序 + 名字短者优先）。</summary>
    private Candidate MakeCandidate(IndexStore store, int slot, int storeIndex, ulong frn, int quality, int pos, int nameLenChars)
    {
        bool dir = (store.FlagsSlots[slot] & EntryFlags.Directory) != 0;
        ulong dirBit = dir ? 1ul << 31 : 0ul;
        int lenField = 0x7FFF - Math.Min(nameLenChars, 0x7FFF);
        ulong key = ((ulong)quality << 32) | dirBit | (ulong)(uint)lenField;
        return new Candidate(key, frn, storeIndex, pos);
    }

    /// <summary>按模式匹配已折叠的名字。quality/pos 语义见打分常量；pos = 匹配起始（模糊 = -1，高亮重建时再算）。</summary>
    private static bool TryMatch(
        ReadOnlySpan<char> foldedName, ReadOnlySpan<char> foldedQuery, MatchMode mode,
        out int quality, out int pos, out (int Start, int Length)[]? fuzzyPositions)
    {
        quality = 0;
        pos = -1;
        fuzzyPositions = null;

        switch (mode)
        {
            case MatchMode.Prefix:
                if (!foldedName.StartsWith(foldedQuery, StringComparison.Ordinal))
                {
                    return false;
                }

                quality = ScorePrefix;
                pos = 0;
                return true;

            case MatchMode.Substring:
                pos = foldedName.IndexOf(foldedQuery, StringComparison.Ordinal);
                if (pos < 0)
                {
                    return false;
                }

                quality = pos == 0 ? ScorePrefix
                    : (IsWordStart(foldedName, pos) ? ScoreWordStart : ScoreSubstring);
                return true;

            case MatchMode.Fuzzy:
                return TryFuzzy(foldedName, foldedQuery, out quality, out fuzzyPositions);

            default:
                return false;
        }
    }

    /// <summary>
    /// fzf 式匹配（V1 语义，设计 §5.1）：查询按空格拆词，词间 AND；词内 = 贪心子序列
    /// （左到右不回溯），词必须依次向前命中。得分 = 基分 + 连续度奖励 + 词首奖励。
    /// </summary>
    private static bool TryFuzzy(
        ReadOnlySpan<char> name, ReadOnlySpan<char> query, out int quality, out (int Start, int Length)[]? positions)
    {
        quality = 0;
        positions = null;

        Span<Range> termRanges = stackalloc Range[132];
        int termCount = query.Split(termRanges, ' ', StringSplitOptions.RemoveEmptyEntries);
        if (termCount == 0)
        {
            return false; // 纯空格查询：无词可匹配
        }

        Span<int> posBuf = stackalloc int[512];
        int qi = 0;
        int maxStreak = 0, streak = 0, lastMatch = -2, wordBonus = 0;
        int scanFrom = 0;

        for (int t = 0; t < termCount; t++)
        {
            var term = query[termRanges[t]];
            streak = 0;   // 词边界重置连续段
            lastMatch = -2;
            foreach (char c in term)
            {
                // 词内子序列找 c：NTFS 名字组件 ≤ 255 字符（短跨度），手写顺序扫描
                // 省掉 Span.IndexOf 每次的向量化启动开销 —— 100 万级调用下实测显著
                //（扫描段 P95 103.8 → 见验收报告；SIMD 红线注释只约束前缀/子串的长跨度路径）。
                int i = -1;
                for (int j = scanFrom; j < name.Length; j++)
                {
                    if (name[j] == c)
                    {
                        i = j;
                        break;
                    }
                }

                if (i < 0)
                {
                    return false; // 词内子序列断掉（词间 AND 不成立）
                }

                streak = i == lastMatch + 1 ? streak + 1 : 1;
                if (streak > maxStreak)
                {
                    maxStreak = streak;
                }

                if (IsWordStart(name, i))
                {
                    wordBonus += FuzzyWordBonusPerChar;
                }

                posBuf[qi++] = i;
                lastMatch = i;
                scanFrom = i + 1;
            }
        }

        quality = ScoreFuzzyBase
            + Math.Min((maxStreak - 1) * FuzzyStreakBonusPerChar, FuzzyStreakBonusMax)
            + Math.Min(wordBonus, FuzzyWordBonusMax);

        positions = new (int, int)[qi];
        for (int k = 0; k < qi; k++)
        {
            positions[k] = (posBuf[k], 1);
        }

        return true;
    }

    private static bool IsWordStart(ReadOnlySpan<char> name, int pos) =>
        pos > 0 && WordSeparators.Contains(name[pos - 1]);

    // ── 收敛与结果构造（W3-b-2 精排；唯一允许分配结果对象的阶段）──

    private List<SearchHit> BuildHits(MinHeap heap, ReadOnlySpan<char> foldedQuery, MatchMode mode)
    {
        var candidates = heap.Drain();

        // 精排：路径深度（§5.2 规则 2）。只对 ≤ K 个候选走父链（扫描期做 = 百万次二分，🔴）。
        var scored = new List<(Candidate c, int depth)>(candidates.Count);
        foreach (var c in candidates)
        {
            scored.Add((c, ResolverFor(c.Store).GetDepth(c.Frn)));
        }

        scored.Sort((a, b) =>
        {
            int r = b.c.Key.CompareTo(a.c.Key); // 分数键降序（主序；key 内含 quality/dir/名字长）
            if (r != 0)
            {
                return r;
            }

            r = a.depth.CompareTo(b.depth); // 深度浅者优先
            return r != 0 ? r : a.c.Frn.CompareTo(b.c.Frn); // FRN 兜底：全序确定（顺序是契约）
        });

        var hits = new List<SearchHit>(scored.Count);
        foreach (var (c, _) in scored)
        {
            var store = _volumes[c.Store].Store;
            if (!store.TryGet(c.Frn, out var entry))
            {
                continue; // 理论不可达（扫描与构建之间无删除方）；防御性跳过
            }

            IReadOnlyList<HighlightRange> highlights = mode switch
            {
                MatchMode.Prefix => new[] { new HighlightRange(0, foldedQuery.Length) },
                MatchMode.Substring => new[] { new HighlightRange(c.Pos, foldedQuery.Length) },
                _ => MergeFuzzyHighlights(foldedQuery, entry.Name),
            };

            hits.Add(new SearchHit(
                entry.Frn,
                entry.Name,
                (entry.Flags & EntryFlags.Directory) != 0,
                ResolverFor(c.Store).GetPath(entry.Frn),
                highlights));
        }

        return hits;
    }

    /// <summary>模糊高亮：匹配位置合并连续段（UTF-16 code unit 网格，协议 §3.2.2）。无命中段回 []（协议明文）。</summary>
    private static HighlightRange[] MergeFuzzyHighlights(ReadOnlySpan<char> foldedQuery, string name)
    {
        Span<char> foldBuf = stackalloc char[NameBufferChars];
        var folded = foldBuf[..Fold(name, foldBuf)];

        if (!TryFuzzy(folded, foldedQuery, out _, out var positions) || positions is null)
        {
            return Array.Empty<HighlightRange>();
        }

        var list = new List<HighlightRange>(positions.Length);
        int i = 0;
        while (i < positions.Length)
        {
            int start = positions[i].Start;
            int end = start + positions[i].Length;
            int j = i + 1;
            while (j < positions.Length && positions[j].Start == end)
            {
                end += positions[j].Length;
                j++;
            }

            list.Add(new HighlightRange(start, end - start));
            i = j;
        }

        return list.ToArray();
    }

    private PathResolver ResolverFor(int storeIndex) =>
        _resolvers[storeIndex] ??= new PathResolver(
            _volumes[storeIndex].Store, _volumes[storeIndex].Volume, _options.PathCacheCapacity);

    /// <summary>管道缓存条目（卷索引 + FRN —— FRN 只在卷内唯一，必须带上卷）。</summary>
    private readonly record struct CachedEntry(int Store, ulong Frn);

    /// <summary>扫描期候选（struct；禁止在此阶段构造结果对象，§5.2 🔴）。</summary>
    private readonly record struct Candidate(ulong Key, ulong Frn, int Store, int Pos);

    /// <summary>
    /// 固定大小最小堆（Top-K）。比较器：Key 大者优先入选；Key 同则 FRN 小者优先
    ///（与精排的 FRN 兜底一致，保证"堆淘汰"与"最终排序"的边界一致）。
    /// </summary>
    private struct MinHeap
    {
        private Candidate[] _items;
        private int _count;

        public MinHeap(int capacity)
        {
            _items = new Candidate[Math.Max(capacity, 1)];
            _count = 0;
        }

        public void Consider(Candidate c, int limit)
        {
            if (_count < limit)
            {
                Push(c);
                return;
            }

            if (Better(c, _items[0]))
            {
                _items[0] = c;
                SiftDown(0);
            }
        }

        public List<Candidate> Drain()
        {
            var list = new List<Candidate>(_count);
            while (_count > 0)
            {
                list.Add(Pop());
            }

            list.Reverse(); // Pop 出来"最差在前"⇒ 反转成"最好在前"
            return list;
        }

        private void Push(Candidate c)
        {
            _items[_count] = c;
            int i = _count++;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Better(_items[i], _items[parent]))
                {
                    break;
                }

                (_items[i], _items[parent]) = (_items[parent], _items[i]);
                i = parent;
            }
        }

        private Candidate Pop()
        {
            var top = _items[0];
            _items[0] = _items[--_count];
            SiftDown(0);
            return top;
        }

        private void SiftDown(int i)
        {
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, best = i;
                if (l < _count && Better(_items[l], _items[best]))
                {
                    best = l;
                }

                if (r < _count && Better(_items[r], _items[best]))
                {
                    best = r;
                }

                if (best == i)
                {
                    return;
                }

                (_items[i], _items[best]) = (_items[best], _items[i]);
                i = best;
            }
        }

        /// <summary>a 是否优于 b（Key 大者优先；Key 同则 FRN 小者优先）。</summary>
        private static bool Better(Candidate a, Candidate b) =>
            a.Key != b.Key ? a.Key > b.Key : a.Frn < b.Frn;
    }
}

/// <summary>
/// 全路径回溯器（设计方案 §3.5）：只存 parentFrn，路径沿父链拼接 + 按 frn 做 LRU 缓存。
/// 卷根判定 = <c>ParentFrn == Frn</c>（MFT 根目录自指）。
/// </summary>
public sealed class PathResolver
{
    /// <summary>父链防御上限（成环/异常超长的兜底；正常 NTFS 深度远小于此）。</summary>
    private const int MaxChainDepth = 128;

    /// <summary>
    /// NTFS 卷根 FRN（自 Windows XP 以来恒为 5，Everything/WizTree 同款假设）。
    /// ★ 实测（2026-09-25，C2 手工抓出）：FSCTL_ENUM_USN_DATA <b>不返回</b>根目录记录 ——
    /// 回溯爬到 FRN=5 时 TryGet 必失败，若只依赖"根条目在 store"判停，
    /// <b>全卷每条路径</b>都会被打上 "?\" 断链前缀（索引数据本身没错，是判停条件错了）。
    /// </summary>
    private const ulong RootFrn = 5;

    private readonly IndexStore _store;
    private readonly string _volumePrefix; // "C:\"
    private readonly int _capacity;
    private readonly Dictionary<ulong, LinkedListNode<(ulong Frn, string Path)>> _cache = new();
    private readonly LinkedList<(ulong Frn, string Path)> _lru = new();
    private int _seenVersion = -1; // IndexStore.Version 快照（-1 = 首查必清，空缓存清了也无妨）

    public PathResolver(IndexStore store, string volume, int cacheCapacity = 512)
    {
        _store = store;
        _volumePrefix = volume.TrimEnd('\\', ':') + ":\\";
        _capacity = Math.Max(cacheCapacity, 1);
    }

    public long CacheHits { get; private set; }

    public long CacheMisses { get; private set; }

    /// <summary>
    /// 取全路径（"C:\Users\ishe\Desktop\report.docx" 形态）。
    /// frn 本身不在索引 ⇒ 返回卷根路径（调用方应先用 TryGet 确认存在）；
    /// 父链中途缺失（全量枚举下不应发生）⇒ "?\" 前缀兜底 —— 确定性可见，不静默给错路径。
    /// </summary>
    public string GetPath(ulong frn)
    {
        // 缓存失效判据（W3-c-2）：USN 增量改过索引（Version 变了）⇒ 全清。
        // 漏掉这条 = 目录改名/删除后路径缓存吐旧路径 —— 查询层最刺眼的静默错。
        if (_store.Version != _seenVersion)
        {
            _seenVersion = _store.Version;
            _cache.Clear();
            _lru.Clear();
        }

        if (_cache.TryGetValue(frn, out var node))
        {
            CacheHits++;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Path;
        }

        CacheMisses++;

        List<string>? parts = null;
        ulong cur = frn;
        bool missing = false;
        while (true)
        {
            if (cur == RootFrn)
            {
                break; // 卷根：不入路径组件（NTFS 根 FRN 恒 5；ENUM_USN_DATA 不吐它，store 里没有）
            }

            if (!_store.TryGet(cur, out var entry))
            {
                if (cur != frn)
                {
                    missing = true; // 父链中途断掉
                }

                break;
            }

            if (entry.ParentFrn == entry.Frn)
            {
                break; // 根自指兜底（万一未来根条目入库，行为不变）
            }

            (parts ??= new List<string>(8)).Add(entry.Name);
            cur = entry.ParentFrn;
            if (parts.Count > MaxChainDepth)
            {
                missing = true; // 防御：父链异常成环/超长
                break;
            }
        }

        parts?.Reverse(); // 回溯出来是"子→父"，拼接要"父→子"
        var path = _volumePrefix + string.Join("\\", parts ?? (IReadOnlyList<string>)Array.Empty<string>());
        if (missing)
        {
            path = "?\\" + path;
        }

        CachePut(frn, path);
        return path;
    }

    /// <summary>路径深度（父链跳数；卷根 = 0）。供精排用（设计方案 §5.2 规则 2）。</summary>
    public int GetDepth(ulong frn)
    {
        int depth = 0;
        ulong cur = frn;
        while (_store.TryGet(cur, out var entry) && entry.ParentFrn != entry.Frn)
        {
            depth++;
            cur = entry.ParentFrn;
            if (depth > MaxChainDepth)
            {
                break; // 与 GetPath 同一防御上限
            }
        }

        return depth;
    }

    private void CachePut(ulong frn, string path)
    {
        if (_cache.ContainsKey(frn))
        {
            return;
        }

        if (_cache.Count >= _capacity)
        {
            var oldest = _lru.Last;
            if (oldest is not null)
            {
                _lru.RemoveLast();
                _cache.Remove(oldest.Value.Frn);
            }
        }

        var node = new LinkedListNode<(ulong, string)>((frn, path));
        _lru.AddFirst(node);
        _cache[frn] = node;
    }
}

/// <summary>
/// 一卷的自有子树作用域状态（P4 观测面）。
/// <see cref="Anchored"/>=false 表示**本次不排除任何条目**（<see cref="Reason"/> 说明为什么）——
/// "没锚定"必须能被看见，否则"排除静默不生效"和"排除了但没声音"在外部完全同形。
/// </summary>
public sealed record OwnScopeStatus(
    string Volume,
    bool Anchored,
    int Count,
    long ExtendedByUsn,
    string Reason);

/// <summary>
/// 搜索服务（协议面背后的内存门面，W3-b-4）：持卷清单 + ready 状态 + 引擎。
/// IndexRpcServer 只跟它说话，不直接碰 <see cref="QueryEngine"/> —— 协议层与引擎解耦。
/// </summary>
public sealed class SearchService
{
    private readonly object _gate = new();
    private QueryEngine _engine;

    /// <summary>
    /// 空构造：启动自举（<see cref="IndexBootstrap"/>）前的初始形态 —— ready=false、零卷，
    /// query 回 -32001（协议 §3.1），ping/status 照常可用（协议面在索引就绪前就要活着）。
    /// </summary>
    public SearchService(QueryOptions? options = null) : this([], ready: false, options)
    {
    }

    public SearchService(IReadOnlyList<VolumeTarget> volumes, bool ready = true, QueryOptions? options = null)
    {
        Volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        Ready = ready;
        _engine = new QueryEngine(volumes, options);
    }

    /// <summary>
    /// 当前卷清单快照。自举完成后由 <see cref="InstallVolumes"/> 原子替换（单次引用交换，
    /// 读者要么看到旧清单要么看到新清单，不存在中间态 —— RPC 主循环单线程 + 自举线程的一次竞争点）。
    /// </summary>
    public IReadOnlyList<VolumeTarget> Volumes { get; private set; }

    /// <summary>索引是否可查询（建索引期间 false ⇒ query 回 -32001，协议 §3.1）。</summary>
    public bool Ready { get; set; }

    /// <summary>
    /// 自有子树作用域快照（P4 观测面）：每卷一条 —— 卷名 / 是否锚定 / 排除条数 / 原因。
    /// **结构化而非拼接文本**：缺口②的教训 —— 想让"排除真的生效了"可断言，就必须有可机读的字段，
    /// 否则只能正则挖日志（而日志正是最容易静默退化的地方）。
    /// </summary>
    public IReadOnlyList<OwnScopeStatus> OwnScopes =>
        Volumes.Select(v => new OwnScopeStatus(
            v.Volume,
            v.Own?.Anchored ?? false,
            v.Own?.Count ?? 0,
            v.Own?.ExtendedByUsn ?? 0,
            v.Own?.Reason ?? "未标记")).ToArray();

    /// <summary>
    /// 自举完成后热替换卷清单与引擎（W3-b-4：空构造 → 建索引 → 一次交换进入可查询态）。
    /// 置 <see cref="Ready"/>=true。RPC 主循环在交换瞬间可能正拿着旧引擎查询 —— 引擎对象
    /// 不可变引用仍有效，本次查询在旧快照上完成（一致性无损；下一次查询用新引擎）。
    /// </summary>
    public void InstallVolumes(IReadOnlyList<VolumeTarget> volumes)
    {
        lock (_gate)
        {
            Volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
            _engine = new QueryEngine(volumes);
            Ready = true;
        }
    }

    /// <summary>索引内文件总数（诊断用，非查询结果数）。</summary>
    public int TotalFiles
    {
        get
        {
            int sum = 0;
            foreach (var v in Volumes)
            {
                sum += v.Store.EntryCount;
            }

            return sum;
        }
    }

    /// <summary>最近一次内部错误摘要（协议 search.status.lastError，恒定字段必填回）。</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// 索引暂停闸（W3-e-1 ③）：tail 泵暂停期间不消费 USN 变更流（游标保持，恢复后补齐）。
    /// **不影响既有索引的查询** —— ready 不变，已建索引照常服务；它只管"增量消费"这一件事。
    /// </summary>
    public PauseGate Pause { get; } = new();

    /// <summary>当前是否暂停索引消费（协议 search.status.indexing.paused）。</summary>
    public bool Paused => Pause.IsPaused;

    /// <summary>
    /// 被跳过的卷 + 原因（W3-e-2：跳过必须可见）。自举时写入；显式限定卷时为空数组。
    /// ping/status 会回传它 —— 用户据此知道"哪些卷没被索引、为什么"。
    /// </summary>
    public IReadOnlyList<SkippedVolume> SkippedVolumes { get; set; } = Array.Empty<SkippedVolume>();

    /// <summary>
    /// **尝试索引但失败**的卷 + 结构化原因（2026-09-25 缺口②）。与 <see cref="SkippedVolumes"/>
    /// 合起来才构成完整的"为什么这个卷不在结果里"—— 缺任一半，`0+0≠2` 就会重演。
    /// </summary>
    public IReadOnlyList<FailedVolume> FailedVolumes { get; set; } = Array.Empty<FailedVolume>();

    /// <summary>
    /// 自举时**检测到的卷总数**（进入尝试的 + 被跳过的）。**由自举写入而非按 <see cref="Volumes"/> 推算**
    /// —— 失败卷不在 <see cref="Volumes"/> 里，推算出来的数会漂。守恒式（可断言）：
    /// <c>Volumes.Count + SkippedVolumes.Count + FailedVolumes.Count == DetectedVolumes</c>。
    /// </summary>
    public int DetectedVolumes { get; set; }

    public QueryResult Query(string query, bool substr, int limit)
    {
        lock (_gate)
        {
            return _engine.Query(query, substr, limit);
        }
    }

    /// <summary>最近一次查询的扫描段/构建段拆分耗时（性能验收定位用，W3-b-1 验收①）。</summary>
    public (double ScanMs, double BuildMs) LastSplitMs
    {
        get
        {
            lock (_gate)
            {
                return (_engine.LastScanMs, _engine.LastBuildMs);
            }
        }
    }

    /// <summary>search.start 的副作用：清空递减管道（协议 §3.1）。</summary>
    public void ResetPipeline()
    {
        lock (_gate)
        {
            _engine.ResetPipeline();
        }
    }

    /// <summary>
    /// USN 变更批次应用（W3-c-2 实时路径）。**与 Query 同一把 <see cref="_gate"/> 互斥**：
    /// IndexStore 是单线程契约（§3.6），tail 的写必须与并发查询串行化；
    /// 批次粒度 = 一批 USN 记录（毫秒级），对查询延迟无感。
    /// applier 由调用方按卷持有（pending 配对状态是卷内概念）。
    /// 卷不存在（例如该卷静态快照后已被移除）⇒ false，调用方如实记录。
    /// </summary>
    public bool ApplyUsn(string volume, IReadOnlyList<UsnRecord> records, UsnApplier applier)
    {
        lock (_gate)
        {
            foreach (var target in Volumes)
            {
                if (!string.Equals(target.Volume, volume, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // P4：自有子树作用域随增量一起维护（快照之后新建于 DataRoot 下的文件才不会被漏排除）。
                // **先于 Apply 调用**：只看记录的 (Frn, ParentFrn)，与 store 是否已变无关。
                target.Own?.Extend(records);
                applier.Apply(records, target.Store);
                return true;
            }

            return false;
        }
    }

    /// <summary>search.start 副作用观测量（自检断言用）。</summary>
    public int PipelineResetCount
    {
        get
        {
            lock (_gate)
            {
                return _engine.PipelineResetCount;
            }
        }
    }
}
