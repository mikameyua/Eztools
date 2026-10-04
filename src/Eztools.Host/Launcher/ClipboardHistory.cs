// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>
/// 剪贴板历史条目的中立投影（W10-a）。
///
/// <para><b>★ 为什么是投影而不是直接用真实条目类型</b>：真实条目（含 Id / Kind / 图片路径等）
/// 属于 <c>Eztools.Clipboard</c>，该程序集是 <c>net10.0-windows10.0.19041.0</c> + <c>UseWPF</c>
/// （它要读系统剪贴板）。平台中立的 <c>Eztools.Host</c>（<c>net10.0</c>）**引用不了高 TFM 程序集**
/// —— 这是编译器约束，不是风格取舍。</para>
///
/// <para>投影只保留启动器确实用到的字段：判类型、显示、整段贴出。DB 侧的 id / 指纹 /
/// 图片路径一律不带过来（不需要 = 不带，避免"看着能用的死字段"）。</para>
/// </summary>
/// <param name="IsText">是否纯文本条目（false = 图片 / 文件列表）。</param>
/// <param name="Content">全文（直贴与 Ctrl+Enter 复制用，**不是**摘要）。图片条目为空串。</param>
/// <param name="Preview">列表摘要（入库时已折叠空白并截断 ⇒ 可安全上屏）。</param>
/// <param name="SourceApp">来源进程名（可空）。</param>
/// <param name="LastUsedAt">最近使用时间。</param>
/// <param name="IsImage">是否图片条目（W10-c：只有图片才提供"提字"动作）。</param>
/// <param name="ImagePath">图片在磁盘上的**绝对路径**（W10-c 新增；仅 <paramref name="IsImage"/> 时非空）。
/// W10-a 时刻意不带（"不需要 = 不带"），现在 OCR 动作要拿它去读盘 —— 由 Desktop 适配器把
/// store 里的相对路径解析成绝对路径（Host 不拼路径）。</param>
public sealed record ClipHistoryEntry(
    bool IsText,
    string Content,
    string Preview,
    string? SourceApp,
    DateTime LastUsedAt,
    bool IsImage = false,
    string? ImagePath = null);

/// <summary>
/// 剪贴板历史检索来源（W10-a）。**同步方法** —— 与 <c>Eztools.Clipboard</c> 的
/// <c>HistoryStore.Search</c> 同形（本地 SQLite + FTS5，毫秒级）；"包 Task.Run 让出 UI 线程"
/// 是调用方 <see cref="ClipProvider"/> 的责任。
///
/// <para>生产实现 = Desktop 侧 <c>ClipboardHistorySource</c>（包装托盘共享的同一个
/// <c>HistoryStore</c> 实例 —— 与 W5 面板 / <c>ezt clip</c> 靠 WAL 并存）；selftest / 探针用假实现。
/// <b>异常不吞</b> —— 抛出去由 <c>QueryRouter</c> 隔离成段位错误（FR-6）。</para>
/// </summary>
public interface IClipboardHistorySource
{
    /// <param name="query">已 trim 的非空查询串（调用方保证）。</param>
    /// <param name="limit">最多返回条数。</param>
    IReadOnlyList<ClipHistoryEntry> Search(string query, int limit);

    /// <summary>
    /// 最近的**图片**条目（W10-c）。为什么不能靠 <see cref="Search"/> 拿：
    /// 图片条目的 <c>content</c> 恒为 NULL（内容在盘上），FTS 索引的只有 content、
    /// 短查询走的是 <c>content LIKE</c> —— 两条路都**结构性地搜不到图片**。
    /// 所以图片必须有一个显式入口（<see cref="ClipProvider"/> 的触发词）。
    /// </summary>
    /// <param name="limit">最多返回条数（按 store 的"pinned 优先、最近使用在前"序）。</param>
    IReadOnlyList<ClipHistoryEntry> ListImages(int limit);
}
