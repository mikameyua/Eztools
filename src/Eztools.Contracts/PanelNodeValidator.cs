// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;

namespace Eztools.Contracts;

/// <summary>
/// 面板**载荷**（<c>tool.panel.data</c> 响应）里节点的校验 —— 纯函数，不依赖任何 UI。
///
/// <b>为什么需要它，而不是塞进清单解析器里</b>：<c>input</c> 节点出现在**工具的运行时响应**里，
/// 不在 <c>tool.json</c> 里 —— 清单解析期宿主**根本看不到节点**。所以这四条规则
/// （<c>key</c> 必需 / 重复检测 / <c>submitCommandId</c> 引用校验 / 未知类型）只能在这里做。
/// 详见 <c>docs/P4-Wave2c-面板协议.md</c> §3.7.6 与 §12.2 第 1 步。
///
/// <b>为什么是"校验"而不是"解析成强类型"</b>：渲染层（CLI / WPF）读的是原始 <see cref="JsonNode"/>，
/// 且要求"一个坏节点不拖垮整块面板"。把它解析成 record 会立刻需要一套"部分成功"的表达，
/// 得不偿失 —— 校验函数返回**问题清单**，渲染层照旧画原树、只是把问题报出来。
/// </summary>
public static class PanelNodeValidator
{
    /// <summary>一条节点级问题。`Severity` 与诊断同义（Warning 不阻塞渲染）。</summary>
    public sealed record NodeIssue(
        DiagnosticSeverity Severity,
        string Code,
        string Message,
        int NodeIndex);

    /// <summary>
    /// 校验一个面板载荷里的全部节点。返回空列表 = 全部合法。
    ///
    /// <paramref name="declaredCommands"/> 为**该工具清单里已声明的命令 id**；
    /// 传 null 或空表示"无从判断"（与 <c>menus</c>/<c>hotkeys</c> 的空清单口径一致），
    /// 此时**不做**引用校验 —— 空清单上做的任何判断都会把合法引用误报成非法。
    /// </summary>
    public static IReadOnlyList<NodeIssue> ValidateNodes(
        JsonNode? payload,
        IReadOnlyCollection<string>? declaredCommands)
    {
        var issues = new List<NodeIssue>();

        if (payload is not JsonObject obj || obj["nodes"] is not JsonArray nodes)
        {
            // 载荷级问题由渲染层负责提示（协议 §3.5），这里只关心节点级
            return issues;
        }

        // input 节点的 key 去重表。**跨整个面板范围**（协议 §3.7.2「同一面板内 key 重复」），
        // 不是"每个节点内部"—— 面板上两个不同的 input 节点用同一个 key，就是重复。
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node)
            {
                continue;
            }

            var type = node["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
            if (type != "input")
            {
                continue;
            }

            ValidateInputNode(node, i, seenKeys, declaredCommands, issues);
        }

        return issues;
    }

    /// <summary>
    /// 单个 <c>input</c> 节点的校验。公开出来是为了让它能被**独立断言** ——
    /// 不必构造整个载荷就能测一条规则。
    /// </summary>
    public static void ValidateInputNode(
        JsonObject node,
        int nodeIndex,
        HashSet<string> seenKeys,
        IReadOnlyCollection<string>? declaredCommands,
        List<NodeIssue> issues)
    {
        // ── 规则 1：key 必需且必须是非空字符串（协议 §3.7.6）──
        // 跳过（不渲染该节点）由渲染层执行；这里只报问题。
        var key = node["key"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : null;

        if (string.IsNullOrEmpty(key))
        {
            issues.Add(new NodeIssue(
                DiagnosticSeverity.Warning,
                DiagnosticCodes.PanelInputMissingKey,
                $"第 {nodeIndex} 个节点是 input 但缺少必填字段 key（key 必须是字符串），该节点会被跳过",
                nodeIndex));
            return;
        }

        // ── 规则 2：同一面板内 key 重复 ⇒ 保留第一个 + Warning（协议 §3.7.2 / §3.7.6）──
        // 口径同 §2.3 的 panels[].id：**先到先得**，后者被忽略。
        if (!seenKeys.Add(key))
        {
            issues.Add(new NodeIssue(
                DiagnosticSeverity.Warning,
                DiagnosticCodes.PanelInputDuplicateKey,
                $"第 {nodeIndex} 个 input 节点的 key '{key}' 在本面板内重复，后者被忽略（保留第一个）",
                nodeIndex));
            return;
        }

        // ── 规则 3：submitCommandId 引用的命令必须存在（协议 §3.7.1 末行，口径同 §2.4）──
        //
        // ⚠️ 与 20.8 的字面差异，以及为什么这样是对的：
        // 20.8 原写「加载期 Warning」，但**节点是运行时产物** —— 加载期宿主看不到它。
        // 在"整体重绘 + 工具侧零状态"模型下，节点根本不存在"加载期"这个概念。
        // 所以本校验发生在**首次拉取（路由到渲染层）时**，诊断码仍复用
        // `contributes.panel-unknown-command`（20.8 的判据落的是**码**，不是时刻）。
        // 已同步修正协议 §3.7.8 第 20.8 行的措辞（见变更记录 V1.3 补记）。
        if (declaredCommands is { Count: > 0 })
        {
            var submitId = node["submitCommandId"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;

            if (!string.IsNullOrWhiteSpace(submitId) &&
                !declaredCommands.Contains(submitId, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new NodeIssue(
                    DiagnosticSeverity.Warning,
                    DiagnosticCodes.PanelUnknownCommand,
                    $"input 节点 (key='{key}') 的 submitCommandId 引用了本工具未声明的命令 '{submitId}'"
                    + "（按 Enter 会报未知命令）",
                    nodeIndex));
            }
        }
    }

    /// <summary>
    /// 把节点列表里的 <c>input</c> 节点按 <c>key</c> 建成"当前该显示的键集合"。
    ///
    /// 用途：宿主拉取路径要按当前面板上有哪些 <c>input</c> 节点来过滤/构造 <c>inputs</c> 快照
    /// （协议 §3.7.2「只带上本面板的」）。重复 key 只取第一个（与规则 2 同口径）。
    /// </summary>
    public static IReadOnlyList<string> CollectInputKeys(JsonNode? payload)
    {
        var keys = new List<string>();

        if (payload is not JsonObject obj || obj["nodes"] is not JsonArray nodes)
        {
            return keys;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is not JsonObject o)
            {
                continue;
            }

            var type = o["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
            if (type != "input")
            {
                continue;
            }

            var key = o["key"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : null;
            if (!string.IsNullOrEmpty(key) && seen.Add(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}
