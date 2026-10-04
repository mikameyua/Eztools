// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>
/// 系统命令来源（W10-a，设计方案 §3 D6）—— "&gt;" 前缀触发命令表，
/// Enter 走**既有** <see cref="LauncherActionKind.Launch"/> 动作（不新增动作类型）。
///
/// <para>命令条目全部落在 pin 段（确定性结果置顶）；<see cref="CommandCatalog.Entries"/>
/// 顺序即展示顺序 —— 用**递减 Score** 钉住，避免同分时被按 Title 重排（表序可断言）。</para>
/// </summary>
public sealed class CommandProvider : ILauncherProvider
{
    /// <summary>段内置顶分（与 calc/unit/encode 同量级 —— 段内排序只看相对大小）。</summary>
    public const double PinScore = 1e9;

    /// <inheritdoc />
    public string Id => LauncherProviderRegistry.Command;

    /// <inheritdoc />
    public string DisplayName => "系统命令";

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
    {
        if (!CommandCatalog.TryGetBody(query.Text, out var body))
        {
            return Task.FromResult(Empty(query.Generation));
        }

        var matched = CommandCatalog.Match(body);
        var items = new List<LauncherItem>(matched.Count);
        for (var i = 0; i < matched.Count; i++)
        {
            items.Add(Row(matched[i], PinScore - i));   // 递减 ⇒ 段内保持表序
        }

        return Task.FromResult(new LauncherResultSet(
            query.Generation, Id, items, items.Count, 0, Dropped: false, Error: null));
    }

    /// <summary>条目 → 结果行。<see cref="CommandEntry.Destructive"/>=true ⇒ 标题带警示后缀（R6）。</summary>
    internal static LauncherItem Row(CommandEntry entry, double score) => new(
        Kind: LauncherKind.Command,
        Title: entry.Destructive ? $"{entry.Name}（不可恢复）" : entry.Name,
        Subtitle: entry.Description,
        IconHint: "",
        Score: score,
        Highlights: Array.Empty<(int, int)>(),
        PrimaryAction: new LauncherAction(
            LauncherActionKind.Launch, entry.LaunchExe, entry.LaunchArgs),
        SecondaryAction: null,
        FileHit: null);

    private LauncherResultSet Empty(long generation) =>
        new(generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false, Error: null);
}
