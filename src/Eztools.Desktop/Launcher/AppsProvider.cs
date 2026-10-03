// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Eztools.Host.Launcher;

namespace Eztools.Desktop;

/// <summary>
/// 应用来源（W7-b，设计方案 §10.6）—— 开始菜单 / 桌面快捷方式 / 注册表 App Paths → 可启动条目。
///
/// <para><b>★ v1 不解析 .lnk（D7=A）</b>：启动与取图标都不需要解析 —— <c>UseShellExecute</c> 让 shell
/// 自己去解，<c>SHGetFileInfo</c> 对 .lnk 直接给图标，标题取文件名。省掉一整块 COM 互操作
/// （<c>IShellLinkW</c>）与"目标不存在/指向 UWP"的边界。代价：不能按目标名匹配、不能对同目标去重
/// —— 两者都登记为 v1 已知限制（复活条件见 §8 D7）。</para>
///
/// <para><b>为什么在 Desktop 而不在 Host</b>：它读注册表（`Microsoft.Win32.Registry`）—— 放 Host
/// （net10.0 平台中立）会产生 CA1416 平台兼容告警，破坏"警告数 = 基线"这条验收判据（§3.2）。</para>
///
/// <para><b>匹配用本地 <see cref="FuzzyMatcher"/></b>（Host 纯函数）：apps 是内存里几百条的模糊匹配，
/// 与索引进程那套"文件路径域"匹配器不是一回事，刻意不复用（§10.6 C）。</para>
///
/// <para><b>W7-e 两个排序加成</b>（都只作用于本段 —— FR-8 收窄：files 排序归索引进程）：
/// ① <b>频次</b>：用户启动过 ⇒ <c>+Boost(count, 天数)</c>（§10.11，上限 400、90 天衰减地板 0.25）；
/// ② <b>别名</b>：<c>launcher.alias</c> 的别名命中 ⇒ <c>别名分 + 300</c>（§10.6 C）。
/// 加成**取 max 不叠加**——同一查询里别名命中往往意味着频次也高，叠加会把上限顶穿变成"必第一"。</para>
/// </summary>
internal sealed class AppsProvider : ILauncherProvider, ILauncherReadyNotifier
{
    private readonly AppIndexCache _cache;
    private readonly LauncherUsageStore? _usage;
    private readonly LauncherAliases _aliases;

    internal AppsProvider(AppIndexCache cache, LauncherUsageStore? usage = null, LauncherAliases? aliases = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _usage = usage;
        _aliases = aliases ?? LauncherAliases.Empty;
        _cache.BecameReady += () => BecameReady?.Invoke();

        // ★ 扫描在这里启动，不能等 QueryAsync：未就绪时 router 按 IsReady 闸**根本不会调 provider**
        //   （契约 I6）—— 等它触发就永远不会触发（死锁）。
        _cache.EnsureScanStarted();
    }

    /// <inheritdoc />
    public string Id => LauncherProviderRegistry.Apps;

    /// <inheritdoc />
    public string DisplayName => "应用";

    /// <inheritdoc />
    public bool IsReady => _cache.Ready;

    /// <summary>扫描完成（含失败）时触发 —— 窗口据此补发一次查询（设计方案 R10）。</summary>
    public event Action? BecameReady;

    /// <summary>失效检测转发（唤出时调用）。</summary>
    internal void InvalidateIfChanged() => _cache.InvalidateIfChanged();

    /// <summary>缓存扫描次数（探针断言"重扫真的发生了"）。</summary>
    internal int ScanCount => _cache.ScanCount;

    /// <inheritdoc />
    public Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
    {
        if (!_cache.Ready)
        {
            // 防御分支：router 不会在未就绪时调用（I6），但并发窗口内可能刚好翻状态。
            // 返回空段 + 无错误 = **静默缺席**（"还没好"不等于"坏了"，契约 C5）。
            return Task.FromResult(Empty(query.Generation));
        }

        if (_cache.LastError is { } error)
        {
            // 读不到（目录全失败）⇒ **必须可见**：走段位错误，状态行会显示原因（§11.1）
            return Task.FromResult(new LauncherResultSet(
                query.Generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false,
                Error: new LauncherError(LauncherErrorKind.ProviderFailed, 0, $"应用索引不可用：{error}", error)));
        }

        // 懒加载兜底：构造后没人调 EnsureLoaded 也不至于永远拿不到加成（fire-and-forget，不阻塞本查询）
        _usage?.EnsureLoaded();

        var sw = Stopwatch.StartNew();
        var hits = new List<LauncherItem>();
        foreach (var entry in _cache.Items)
        {
            var match = FuzzyMatcher.MatchTokens(query.Text, entry.Title);
            var boost = _usage?.BoostFor(Id, entry.TargetPath) ?? 0;

            // 别名候选：与标题分**取 max**（不是叠加）—— 真正的"二选一"。
            // 别名命中且标题未命中也算命中，但**高亮为空**：高亮区间是标题里的字符位，
            // 别名匹配给出的区间对不上标题，硬套会加粗错位（宁可不加粗）。
            var aliasScore = -1.0;
            foreach (var alias in _aliases.AliasesOf(entry.Title, entry.TargetPath))
            {
                var aliasMatch = FuzzyMatcher.MatchTokens(query.Text, alias);
                if (aliasMatch.Matched && aliasMatch.Score > aliasScore)
                {
                    aliasScore = aliasMatch.Score;
                }
            }

            if (!match.Matched && aliasScore < 0)
            {
                continue;
            }

            if (aliasScore >= 0 && aliasScore + 300 > match.Score + boost)
            {
                match = match.Matched
                    ? match with { Score = aliasScore + 300, Highlights = [] }
                    : new FuzzyMatch(true, aliasScore + 300, []);
            }

            hits.Add(ToItem(entry, match, match.Score == aliasScore + 300 ? 0 : boost));
        }

        // 段内排序在**截断之前**做：先截断会把高分项丢掉（Top-N 必须是"最高分的 N 条"）。
        // 同分按 Title 序号 —— 与 router 的段内排序同一条判据，保证两次渲染顺序一致。
        hits.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Title, b.Title);
        });

        var total = hits.Count;
        if (total > query.Limit)
        {
            hits.RemoveRange(query.Limit, total - query.Limit);
        }

        return Task.FromResult(new LauncherResultSet(
            query.Generation, Id, hits, total, (int)sw.ElapsedMilliseconds, Dropped: false, Error: null));
    }

    private LauncherResultSet Empty(long generation) =>
        new(generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false, Error: null);

    /// <summary>
    /// 条目 → 结果项。<see cref="LauncherItem.IconHint"/> = 快捷方式/exe 全路径（渲染层凭它取图标）；
    /// <c>Subtitle</c> = 同一路径（用户要能一眼看出这条到底指向哪个文件）。
    /// </summary>
    private static LauncherItem ToItem(AppEntry entry, FuzzyMatch match, double boost = 0) => new(
        Kind: LauncherKind.App,
        Title: entry.Title,
        Subtitle: entry.TargetPath,
        IconHint: entry.TargetPath,
        Score: match.Score + boost,
        Highlights: match.Highlights,
        PrimaryAction: new LauncherAction(LauncherActionKind.Launch, entry.TargetPath),
        SecondaryAction: new LauncherAction(LauncherActionKind.RevealApp, entry.TargetPath),
        FileHit: null);

    /// <summary>生产装配（默认根 + 含 App Paths）。</summary>
    internal static AppsProvider CreateDefault(LauncherUsageStore? usage = null, LauncherAliases? aliases = null) =>
        new(new AppIndexCache(AppIndexCache.DefaultRoots()), usage, aliases);
}
