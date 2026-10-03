// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>一次匹配的结果。<see cref="Highlights"/> 是 UTF-16 code unit 区间（与 files 段同口径）。</summary>
public readonly record struct FuzzyMatch(bool Matched, double Score, IReadOnlyList<(int Start, int Len)> Highlights);

/// <summary>
/// 应用名模糊匹配（W7-b，设计方案 §10.6 C）—— **纯函数**，selftest 直测。
///
/// <para><b>与索引进程的匹配器是两套，刻意不复用</b>：那个是"文件路径域"专用（USN 全量索引 + 倒排），
/// 本类只服务内存里几百条应用条目。硬要共享会把"文件搜索怎么排"绑到"应用怎么排"上，
/// 而两者的正确排序根本不是一回事（§2.3 已明确 files 排序归索引进程、本波不重排它）。</para>
///
/// <para><b>打分是"分档 + 惩罚"，不是连续相似度</b>：档位决定量级（完全相等 ≫ 前缀 ≫ 词边界 ≫ 子串 ≫ 子序列），
/// 惩罚只做档内微调。这样"用户想要的排第一"靠的是**档位**，而不是某个调参常数 —— 换常数不会翻盘。</para>
/// </summary>
public static class FuzzyMatcher
{
    public const double ScoreExact = 1000;
    public const double ScorePrefix = 800;
    public const double ScoreWordPrefix = 700;
    public const double ScoreSubstring = 500;
    public const double ScoreSubsequence = 200;

    /// <summary>词边界字符（这些字符之后的位置算"词的开始"）。</summary>
    private static readonly char[] WordBoundary = [' ', '-', '_', '.', '\\', '/', '(', '['];

    /// <summary>
    /// 查询分词（按空白），**每段都必须命中**（AND），总分取各段平均。
    /// 高亮 = 各段高亮的并集（已排序、已合并重叠）。
    /// </summary>
    public static FuzzyMatch MatchTokens(string? query, string? target)
    {
        var q = (query ?? "").Trim();
        var t = target ?? "";
        if (q.Length == 0)
        {
            // 空查询不参与匹配（调用方负责"空查询 = 不出结果"）
            return new FuzzyMatch(false, 0, Array.Empty<(int, int)>());
        }

        if (t.Length == 0)
        {
            return new FuzzyMatch(false, 0, Array.Empty<(int, int)>());
        }

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return new FuzzyMatch(false, 0, Array.Empty<(int, int)>());
        }

        var total = 0.0;
        var spans = new List<(int Start, int Len)>();
        foreach (var token in tokens)
        {
            var part = MatchSingle(token, t);
            if (!part.Matched)
            {
                return new FuzzyMatch(false, 0, Array.Empty<(int, int)>());
            }

            total += part.Score;
            spans.AddRange(part.Highlights);
        }

        return new FuzzyMatch(true, total / tokens.Length, MergeSpans(spans));
    }

    /// <summary>单段匹配（分档见类注）。</summary>
    internal static FuzzyMatch MatchSingle(string token, string target)
    {
        if (string.Equals(token, target, StringComparison.OrdinalIgnoreCase))
        {
            return new FuzzyMatch(true, ScoreExact, [(0, target.Length)]);
        }

        if (target.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return new FuzzyMatch(true, ScorePrefix - LengthPenalty(target.Length - token.Length), [(0, token.Length)]);
        }

        // 词边界前缀：`Visual Studio Code` 里的 `Code`
        for (var i = 1; i + token.Length <= target.Length; i++)
        {
            if (!WordBoundary.Contains(target[i - 1]))
            {
                continue;
            }

            if (target.AsSpan(i).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return new FuzzyMatch(
                    true,
                    ScoreWordPrefix - LengthPenalty(target.Length - token.Length),
                    [(i, token.Length)]);
            }
        }

        var at = target.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return new FuzzyMatch(true, ScoreSubstring - Math.Min(200, at * 2), [(at, token.Length)]);
        }

        return MatchSubsequence(token, target);
    }

    /// <summary>
    /// 子序列（保序不连续）。命中即给最低档分，惩罚由"起点"与"跳过的字符数"决定。
    /// 贪心取最左匹配对"是否存在"是最优的（经典结论），所以不做回溯。
    /// </summary>
    private static FuzzyMatch MatchSubsequence(string token, string target)
    {
        var spans = new List<(int Start, int Len)>();
        var ti = 0;
        var firstAt = -1;
        var skipped = 0;
        var runStart = -1;
        var runLen = 0;

        for (var i = 0; i < target.Length && ti < token.Length; i++)
        {
            if (char.ToUpperInvariant(target[i]) != char.ToUpperInvariant(token[ti]))
            {
                if (runLen > 0)
                {
                    spans.Add((runStart, runLen));
                    runStart = -1;
                    runLen = 0;
                }

                skipped++;
                continue;
            }

            if (firstAt < 0)
            {
                firstAt = i;
            }

            if (runStart < 0)
            {
                runStart = i;
            }

            runLen++;
            ti++;
        }

        if (ti < token.Length)
        {
            return new FuzzyMatch(false, 0, Array.Empty<(int, int)>());
        }

        if (runLen > 0)
        {
            spans.Add((runStart, runLen));
        }

        var score = ScoreSubsequence - Math.Min(100, (firstAt * 2) + (skipped * 5));
        return new FuzzyMatch(true, score, spans);
    }

    /// <summary>档内长度惩罚：目标越长越轻，封顶 100（保证不垮档）。</summary>
    private static double LengthPenalty(int extraChars) => Math.Min(100, Math.Max(0, extraChars) * 2);

    /// <summary>区间并集（排序 + 合并重叠/相邻），供主行着色逐段应用。</summary>
    private static IReadOnlyList<(int Start, int Len)> MergeSpans(List<(int Start, int Len)> spans)
    {
        if (spans.Count == 0)
        {
            return Array.Empty<(int, int)>();
        }

        var ordered = spans.Where(s => s.Len > 0).OrderBy(s => s.Start).ToList();
        var merged = new List<(int Start, int Len)>(ordered.Count);
        foreach (var span in ordered)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (span.Start <= last.Start + last.Len)
                {
                    merged[^1] = (last.Start, Math.Max(last.Start + last.Len, span.Start + span.Len) - last.Start);
                    continue;
                }
            }

            merged.Add(span);
        }

        return merged;
    }
}
