// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>
/// 计算器来源（W7-c，设计方案 §10.7）—— 输入算式 ⇒ **置顶一行结果**。
///
/// <para><b>本类只做三件事</b>：调门槛与解析（<see cref="CalcExpression.Evaluate"/>）、
/// 把结局映射成"出行/不出行"、把结局组进 <see cref="LauncherItem"/>。
/// 词法/语法/求值/格式化全在 <see cref="CalcExpression"/>（纯函数，selftest 直测）。</para>
///
/// <para><b>★ 两类失败的处置不同（本 provider 的核心语义，§10.7 A）</b>：
/// <list type="bullet">
/// <item><b>静默</b>（L0 未过 / 语法错）：用户多半还在打字（`1+`）或在搜文件（`report-1.txt`）
///   —— 出行就是噪声。<b>但错误码不会消失</b>：selftest 直测 <see cref="CalcExpression.ParseAndEvaluate"/>，
///   探针把状态落进 JSON（"UI 不显示"≠"错误不存在"，S2 家族）。</item>
/// <item><b>出行并显示错误文本</b>（除零/溢出/未定义）：输入已是完整合法表达式，用户**期望**一个结果；
///   静默会让人以为计算器坏了。<b>但动作被禁用</b>（<c>PrimaryAction = null</c>）——
///   "没有可复制的结果"必须与"有结果"可区分，不能靠一个空串糊过去。</item>
/// </list></para>
///
/// <para><b>为什么 <see cref="IsReady"/> 恒真</b>：本地即时计算，没有"准备中"状态
/// （对照 apps 的首扫）。契约 C5 要求 <c>IsReady=false</c> 只能表达"暂不可用"，
/// 用它表达"这次没命中"是错的（"没准备好"与"没结果"混同 —— S9 家族）。</para>
/// </summary>
public sealed class CalcProvider : ILauncherProvider
{
    /// <summary>
    /// 段内置顶分（设计方案 §10.7 E：`Score = 1e9`）。
    /// 取"远大于一切"的常数而不是无穷：pin 段的排序还要比 Title（同分兜底），
    /// 用 <c>double.MaxValue</c> 会让"同分"这件事永远不发生。
    /// </summary>
    public const double PinScore = 1e9;

    /// <inheritdoc />
    public string Id => LauncherProviderRegistry.Calc;

    /// <inheritdoc />
    public string DisplayName => "计算器";

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
    {
        var outcome = CalcExpression.Evaluate(query.Text);
        return Task.FromResult(outcome.HasRow
            ? new LauncherResultSet(
                query.Generation, Id, [ToItem(query.Text, outcome)], 1, 0, Dropped: false, Error: null)
            : Empty(query.Generation));
    }

    /// <summary>不出行（L0 未过 / 语法错）：**空段且无错误** —— 这不是故障，是"没有可显示的结果"。</summary>
    private LauncherResultSet Empty(long generation) =>
        new(generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false, Error: null);

    /// <summary>
    /// 结局 → 结果行。
    ///
    /// <para><b>成功行</b>：主行 = 格式化结果值（用户要复制的东西）· 副行 = `= 去空白原表达式`
    /// （让用户确认"算的是我打的这串"）· 主动作 = <see cref="LauncherActionKind.CopyText"/> 复制**格式化值**。</para>
    ///
    /// <para><b>错误行</b>：副行 = 错误文案（§10.7 C 表的口径）· 主行 = 去的空白原表达式（无结果值可显示）
    /// · 动作 = <c>null</c>（Enter 无障碍可走，但会被窗口拦成"该结果不可执行"并**不吞键**）。</para>
    /// </summary>
    internal static LauncherItem ToItem(string raw, CalcOutcome outcome)
    {
        var expression = CalcExpression.StripWhitespace(raw);

        if (outcome.Status != CalcStatus.Ok)
        {
            return new LauncherItem(
                Kind: LauncherKind.Calc,
                Title: expression,
                Subtitle: outcome.ErrorText,
                IconHint: "",
                Score: PinScore,
                Highlights: Array.Empty<(int, int)>(),
                PrimaryAction: null,          // ★ 无结果可复制 ⇒ 禁用动作（不静默吞键：窗口给显式文案）
                SecondaryAction: null,
                FileHit: null);
        }

        var value = CalcExpression.Format(outcome.Value);
        return new LauncherItem(
            Kind: LauncherKind.Calc,
            Title: value,
            Subtitle: $"= {expression}",
            IconHint: "",
            Score: PinScore,
            Highlights: Array.Empty<(int, int)>(),
            PrimaryAction: new LauncherAction(LauncherActionKind.CopyText, value),
            // §4.2：Ctrl+Enter 复制「表达式 = 结果」整串（把"算过什么"一起带走，贴进文档/聊天时自带算式）
            SecondaryAction: new LauncherAction(
                LauncherActionKind.CopyText, $"{expression} = {value}"),
            FileHit: null);
    }
}
