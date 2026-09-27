// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;

namespace Eztools.Contracts;

/// <summary>
/// <c>tool.panel.data</c> **请求参数**的构造 —— 单一来源。
///
/// <b>为什么需要它（而不是在调用点内联 `JsonObject`）</b>：
/// 协议 §3.7.7 要求新宿主**总是**带 <c>inputs</c>，且 20.10 断言它的形态必须是
/// **空对象**（既不是 <c>null</c> 也不是缺字段）。若各调用点自己拼 JSON，
/// 这条"形态契约"就退化成"每个调用方都记得加上"—— 而它恰恰是一个
/// **静默失败**：缺字段不会报错，工具侧只是永远收到空输入。
/// 抽成构造助手后，"总是有空对象"变成**结构性事实**，而不是纪律。
///
/// <b>为什么不抽成 record</b>：<c>inputs</c> 的来源（WPF 输入框）在第 5 步才存在，
/// 现在抽 record 等于为**尚未存在的调用方**设计。第 3 步（宿主拉取路径）真正接 UI 时
/// 再评估是否升级为强类型 —— 那时会有多个真实调用点，才看得出形状。
/// </summary>
public static class PanelDataArgs
{
    /// <summary>拉取原因（协议 §3.7.3 触发表）。用常量而不是裸字符串，避免拼错后静默变成"未知 reason"。</summary>
    public static class Reason
    {
        /// <summary>面板窗口刚打开。</summary>
        public const string Open = "open";

        /// <summary>手动 / 定时刷新，或输入节流后的拉取。</summary>
        public const string Refresh = "refresh";

        /// <summary>宿主状态变化（工具被启用、注册表重载等）。</summary>
        public const string HostEvent = "host-event";

        /// <summary>自检探针（不开窗口）。</summary>
        public const string SelfCheck = "selfcheck";
    }

    /// <summary>
    /// 构造 <c>tool.panel.data</c> 的 <c>params</c>。
    /// </summary>
    /// <param name="panelId">目标面板 id。</param>
    /// <param name="reason">拉取原因，取 <see cref="Reason"/> 里的常量。</param>
    /// <param name="inputs">
    /// 本面板上各 <c>input</c> 节点的当前值快照。传 <c>null</c> 表示"还没有 UI 来源"
    /// —— **仍然会输出空对象**（协议 §3.7.7 / 20.10），只是内容为空。
    /// </param>
    public static JsonObject Build(string panelId, string reason, JsonObject? inputs = null)
    {
        // Clone 必须：入参可能挂在其原树上，直接塞进来会抛 "The node already has a parent."。
        // 与本项目"跨帧一律 DeepClone"的既有铁律同源（见 MEMORY.md §四.4）。
        return new JsonObject
        {
            ["panelId"] = panelId,
            ["context"] = new JsonObject { ["reason"] = reason },
            ["inputs"] = inputs?.Clone() as JsonObject ?? new JsonObject(),
        };
    }
}
