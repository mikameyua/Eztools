// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>
/// 单位换算来源（W7-d，设计方案 §10.8 A / D10=A）—— `<数值><单位>` ⇒ **置顶若干行换算结果**。
///
/// <para><b>两种输入形态</b>：
/// <list type="bullet">
/// <item><b>带触发词</b>（`10km to mi` / `10km -> mi` / `10km转mi`）⇒ 一行精确结果；</item>
/// <item><b>不带触发词</b>（`10km`）⇒ 列出该类别常用单位**若干行**（不含源单位，见 <see cref="UnitTable.CommonOf"/>）——
///   "随手可用"是 D10=A 的全部理由：`10km` 单独输入时用户十有八九想看别的单位。</item>
/// </list></para>
///
/// <para><b>与 calc 的关系</b>：两者都属 pin 段，但**输入特征互斥** —— calc 的 L0 门槛只放行
/// 数字与运算符，而单位记号必然含字母（`km`/`°C`）。所以不可能同时命中，不需要仲裁。</para>
/// </summary>
public sealed class UnitProvider : ILauncherProvider
{
    /// <summary>段内置顶分（与 calc 同量级；两者不会同时出行，见类注）。</summary>
    public const double PinScore = 1e9;

    /// <inheritdoc />
    public string Id => LauncherProviderRegistry.Unit;

    /// <inheritdoc />
    public string DisplayName => "单位换算";

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
    {
        if (!UnitTable.TryParse(query.Text, out var request))
        {
            return Task.FromResult(Empty(query.Generation));
        }

        var items = request.To is { } target
            ? [Row(request, target, PinScore)]
            : BuildCommonRows(request, query.Limit);

        return Task.FromResult(new LauncherResultSet(
            query.Generation, Id, items, items.Count, 0, Dropped: false, Error: null));
    }

    /// <summary>无触发词 ⇒ 列常用单位（Score 递减，保证行序稳定可断言）。</summary>
    private static IReadOnlyList<LauncherItem> BuildCommonRows(UnitRequest request, int limit)
    {
        var commons = UnitTable.CommonOf(request.From.Category, request.From.Key);
        var take = Math.Min(commons.Count, Math.Max(0, limit));
        var items = new LauncherItem[take];
        for (var i = 0; i < take; i++)
        {
            items[i] = Row(request, commons[i], PinScore - 100 - i);
        }

        return items;
    }

    /// <summary>
    /// 一行换算结果。<b>主行 = 结果</b>（用户要复制的东西）· <b>副行 = `原式 = 结果`</b>
    /// （§4.3 的视觉契约，同时也是 Ctrl+Enter 复制的内容）。
    /// </summary>
    internal static LauncherItem Row(UnitRequest request, UnitDef target, double score)
    {
        var display = Display(request, target);
        var subtitle = Legend(request, target, display);

        return new LauncherItem(
            Kind: LauncherKind.Unit,
            Title: display,
            Subtitle: subtitle,
            IconHint: "",
            Score: score,
            Highlights: Array.Empty<(int, int)>(),
            PrimaryAction: new LauncherAction(LauncherActionKind.CopyText, display),
            SecondaryAction: new LauncherAction(LauncherActionKind.CopyText, subtitle),
            FileHit: null);
    }

    /// <summary>`{数值} {单位}` 形态的结果文本（数值格式化复用 calc 那一套口径，避免两处各写一份）。</summary>
    internal static string Display(UnitRequest request, UnitDef target) =>
        $"{CalcExpression.Format(UnitTable.Convert(request.From, target, request.Value))} {target.Display}";

    /// <summary>
    /// `原式 = 结果`（+ 数据量的**制式说明**）。
    ///
    /// <para><b>制式说明为什么必须可见</b>：`1GB → 953.674MiB` 是本功能最常被当成 bug 的一处 ——
    /// 两个数都对，只是进制不同。把 `（1000 进制 → 1024 进制）` 直接写在行上，
    /// 用户一眼能自证；藏在文档里等于没有。</para>
    /// </summary>
    internal static string Legend(UnitRequest request, UnitDef target, string display)
    {
        var line = $"{request.Raw} = {display}";
        if (request.From.Category != UnitCategory.Data)
        {
            return line;
        }

        var from = Radix(request.From);
        var to = Radix(target);
        return from == to ? $"{line}（{from}）" : $"{line}（{from} → {to}）";
    }

    private static string Radix(UnitDef unit) => unit.Binary ? "1024 进制" : "1000 进制";

    private LauncherResultSet Empty(long generation) =>
        new(generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false, Error: null);
}
