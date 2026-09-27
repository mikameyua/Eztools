// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Contracts;

namespace Eztools.Host.Processes;

/// <summary>
/// 工具进程组（**N2 补齐**：设计方案 §5.1「独立进程的语义补齐」，W3-a-1 落地）。
///
/// <b>背景</b>：Wave 2c 落地时如实记录过 —— <c>weight: full</c> 与 <c>lite</c> 共用同一套
/// 进程管理路径，"独立进程组"只有语义上的说法、没有可观测的落点。Wave 3 引入第一个真正的
/// 独立进程（<c>ezt-index.exe</c>，崩溃爆炸半径要求硬隔离）之后，这层语义必须**可观测**：
///
/// <list type="bullet">
/// <item><b>进程归属</b>：<c>full</c> 档工具自成一组（组 id = <c>full:&lt;toolId&gt;</c>），
///   其余档位共用 <see cref="HostedGroup"/>。</item>
/// <item><b>崩溃隔离</b>：崩溃 / 超时 / 回收只作用于所属会话。<see cref="ToolHostManager"/> 的每条
///   治理路径（<c>HandleCrashAsync</c> / <c>HandleTimeoutAsync</c> / <c>ReleaseAsync</c> / 清扫线程）
///   都按 toolId 单会话操作、从不动其他会话 —— N2 之后这是**显式契约**，不是实现巧合
///   （因此任何把这些路径改成"批量/全局"操作的改动都直接违反 §5.1，评审时按本类注释对照）。</item>
/// <item><b>资源核算</b>：<see cref="ToolHostManager.GetProcessGroups"/> 按**组**给出工具数 / 运行数 /
///   pid 清单，预算表可以对 <c>hosted</c> 与 <c>full:*</c> 分别断言（解决"67.8 + 40 MB 说不清是谁"）。</item>
/// <item><b>回收时机</b>：空闲回收按会话独立判定（清扫线程逐会话比较 LastUsed），组间互不牵连。</item>
/// </list>
///
/// <b>刻意不做的</b>：不引入 Windows Job 级的"组捆绑"内核对象 —— full 组与 hosted 组本就是
/// 不同的进程，kill-on-close Job 的归属已由 lifecycle 决定（P2，resident 不挂、其余挂）。
/// 这里的"组"是**托管语义与核算口径**，不是新的内核对象；§5.1 明文"协议、启动参数、stdio 分帧、
/// weight 闸门全部不变"。
/// </summary>
public static class ProcessGroups
{
    /// <summary>非 full 档工具共用组的组 id。</summary>
    public const string HostedGroup = "hosted";

    /// <summary>
    /// 工具进程的组 id：<c>full</c> 档自成一组（<c>full:&lt;toolId&gt;</c>），其余归 <see cref="HostedGroup"/>。
    /// 组 id 同时出现在进程启动日志里（<c>group=...</c>），让"这个进程归谁"在日志里可查。
    /// </summary>
    public static string GroupIdOf(ToolManifest manifest) =>
        manifest.Weight == ToolWeight.Full ? "full:" + manifest.Id : HostedGroup;

    /// <summary>一个工具的组归属与运行态快照（<see cref="Summarize"/> 的输入行）。</summary>
    public sealed record GroupEntry(string ToolId, string GroupId, bool Running, int? Pid);

    /// <summary>一个组的核算汇总（预算断言的落点）。</summary>
    public sealed record GroupSummary(string GroupId, int ToolCount, int RunningCount, IReadOnlyList<int> RunningPids);

    /// <summary>
    /// 把条目按组聚合。<b>纯函数</b>：selftest 不起任何进程就能验分组口径（同组工具数、运行数、
    /// pid 清单）；<see cref="ToolHostManager.GetProcessGroups"/> 负责采集真实会话后调它。
    /// </summary>
    public static IReadOnlyList<GroupSummary> Summarize(IEnumerable<GroupEntry> entries)
    {
        return entries
            .GroupBy(e => e.GroupId, StringComparer.Ordinal)
            .Select(g => new GroupSummary(
                g.Key,
                g.Count(),
                g.Count(e => e.Running),
                g.Where(e => e.Running && e.Pid.HasValue)
                    .Select(e => e.Pid!.Value)
                    .OrderBy(p => p)
                    .ToList()))
            .OrderBy(g => g.GroupId, StringComparer.Ordinal)
            .ToList();
    }
}
