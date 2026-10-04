// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Contracts;

namespace Eztools.Host.Launcher;

/// <summary>
/// 陈旧索引提示（W9）—— **Ready=true 而 Core 缺席**时，界面必须说的话与给的出口。
///
/// <para><b>它补的是 W8·B1 没覆盖的第四个面</b>：W8 分流的是"-32001（索引未就绪）"的两种成因；
/// 但索引可以**已经就绪**（自举快照在）而核心服务随后退出 —— 此时查询一切正常、索引却
/// 停止更新（USN 增量无人应用），旧文件搜得到、新文件搜不到且**无从察觉**（实测：10-02 快照
/// 1,892,376 条 vs 重建 1,911,112 条，差 ~1.9 万条）。这是"沉默"，与 W8 的"说假话"判据不同。</para>
///
/// <para><b>判据的保守性继承 W8</b>：<see cref="CoreAvailability.Unknown"/> 与
/// <see cref="CoreAvailability.CoreOk"/> 一律返回 <c>null</c>（不提示）——
/// 探测说不清就闭嘴，绝不为了"看起来负责任"制造一个点了没用的按钮。</para>
///
/// <para><b>CoreNotElevated 不给可点出口的原因</b>：<see cref="Eztools.Desktop.CoreLauncher"/>
/// 的幂等语义（端点在 + pid 活 ⇒ AlreadyRunning）决定了"再点一次"只会回一句
/// "核心服务已在运行" —— 那是对用户决策权的嘲弄。此处只说明事实与正确动作
/// （以管理员身份重启核心服务），动作本身留给用户/CLI。这与 W8 错误路径
/// （CoreNotElevated ⇒ CanLaunch=true）**刻意不一致**：错误路径的提示是"索引没就绪"，
/// 重启窗口（重建自举）碰巧是对的第一步；陈旧路径的核心服务明明活着，
/// "启动"语义已不成立 —— 文案必须如实。</para>
/// </summary>
public static class CoreStaleNotice
{
    /// <summary>
    /// 状态行提示。返回 <c>null</c> = 该可达性下**不提示**（CoreOk 正常、Unknown 保守闭嘴）。
    /// <c>CanLaunch</c> 仅在"启动"语义成立（核心服务确实没在跑）时为 true。
    /// </summary>
    public static (string Text, bool CanLaunch)? For(CoreAvailability availability) => availability switch
    {
        CoreAvailability.CoreNotRunning
            => ($"{SuffixFor(availability)!}（结果可能陈旧）—— 点此启动", true),
        CoreAvailability.CoreNotElevated
            => ($"{SuffixFor(availability)} —— 请以管理员身份重启核心服务", false),
        _ => null,
    };

    /// <summary>
    /// 卷清单行的**行尾后缀**（同一事实的第二个可见出口 —— 与卷漂移提示 §7.4.1 同款打法：
    /// 写在已有可见处，不新造 UI 元素）。两处文案单一来源，防止漂移。
    /// </summary>
    public static string? SuffixFor(CoreAvailability availability) => availability switch
    {
        CoreAvailability.CoreNotRunning => "核心服务未运行，索引已停止更新",
        CoreAvailability.CoreNotElevated => "核心服务未提权，索引已停止更新",
        _ => null,
    };
}
