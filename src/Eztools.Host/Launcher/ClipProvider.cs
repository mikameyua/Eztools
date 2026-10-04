// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>
/// 剪贴板历史来源（W10-a，设计方案 §3）—— 在启动器里搜剪贴板历史（复用 W5 的 SQLite + FTS5
/// 双路检索），Enter **直贴**回唤出前的前台窗口，Ctrl+Enter 只复制。
///
/// <para><b>为什么检索走 <see cref="IClipboardHistorySource"/> 而不是直接持有真实存储类型</b>：
/// 那个存储类型所在的程序集带 Windows/WPF 依赖（要读系统剪贴板），而本层是平台中立的
/// <c>net10.0</c> —— <b>编译器层面就引用不了</b>。抽象把"平台中立逻辑"与"平台相关实现"分开：
/// 本 provider 的全部行为（过滤 / 映射 / 动作构造）因此可被 <c>ezt selftest</c> 直接驱动
/// （设计方案 §7 的验收策略）。</para>
///
/// <para><b>段内顺序</b>：Score 恒为 0 ⇒ 不参与段内重排（D5），保持 store 返回序
/// （pinned 优先、最近使用在前）—— 与 W5 面板 <c>clip search</c> 同序。</para>
/// </summary>
public sealed class ClipProvider : ILauncherProvider
{
    /// <summary>
    /// 图片入口的触发词（W10-c）：**精确匹配**（trim 后全等，忽略大小写）才触发。
    ///
    /// <para><b>为什么是"图片"这个纯词，而不是 <c>图:</c> 这类符号前缀</b>：符号前缀在中文输入法下
    /// 要么需要 Shift（<c>:</c>）、要么要先切到半角 —— 正是 R7 记下的 <c>&gt;</c> 同款痛点。
    /// 纯汉字"图片"用中文输入法**直接打**即可，零切换。用"精确匹配"而不是"前缀匹配"是为了
    /// 不干扰普通检索：搜"图片处理"仍走正常文本检索，不会莫名变成图片列表。</para>
    /// </summary>
    private static readonly string[] ImageTriggers = ["图片", "img"];

    private readonly IClipboardHistorySource _source;

    /// <param name="source">剪贴板历史来源（生产 = Desktop 侧包装真实存储；selftest = 夹具）。</param>
    public ClipProvider(IClipboardHistorySource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public string Id => LauncherProviderRegistry.Clip;

    /// <inheritdoc />
    public string DisplayName => "剪贴板历史";

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public async Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
    {
        // 空查询 ⇒ 空结果（防御）：搜索窗的"清空输入框"是**复位**语义（QueryRouter 在空文本时
        // 根本不派发），这里再兜一道 —— 与 W5 面板"空输入列历史"的入口语义刻意不同。
        var text = (query.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return Empty(query.Generation);
        }

        // W10-c：精确命中触发词 ⇒ 列**最近图片**。图片的 content 恒为 NULL（内容在盘上），
        // FTS 索引的只有 content、短查询走 content LIKE —— 两条路都结构性地搜不到图片，
        // 所以图片必须走这个显式入口（见 IClipboardHistorySource.ListImages 的注释）。
        if (IsImageQuery(text))
        {
            var images = await Task.Run(() => _source.ListImages(query.Limit)).ConfigureAwait(false);
            var imageItems = new List<LauncherItem>(images.Count);
            foreach (var row in images)
            {
                if (row.IsImage)
                {
                    imageItems.Add(ImageRow(row));
                }
            }

            return new LauncherResultSet(
                query.Generation, Id, imageItems, imageItems.Count, 0, Dropped: false, Error: null);
        }

        // Search 是**同步**方法（本地 SQLite，毫秒级）：包 Task.Run 交出 UI 线程（R3）。
        // 不传 ct —— 检索本身不可中断，且 QueryRouter 传的就是 CancellationToken.None。
        var rows = await Task.Run(() => _source.Search(text, query.Limit)).ConfigureAwait(false);

        var items = new List<LauncherItem>(rows.Count);
        foreach (var row in rows)
        {
            if (row.IsText)
            {
                items.Add(Row(row));   // D3：v1 只收录文本条目（图片走"图片"触发词，见上）
            }
        }

        return new LauncherResultSet(
            query.Generation, Id, items, items.Count, 0, Dropped: false, Error: null);
    }

    /// <summary>查询是否命中图片入口（精确匹配，忽略大小写）。</summary>
    internal static bool IsImageQuery(string trimmedText) =>
        ImageTriggers.Any(t => string.Equals(t, trimmedText, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 条目 → 结果行。主行 = <see cref="ClipHistoryEntry.Preview"/>（入库时已折叠空白并截断，
    /// 渲染层还有 ellipsis 兜底）；副行 = 来源 + 时间（与 W5 面板元信息同一口径）。
    /// 主/次动作都携带**全文** —— Preview 只是摘要，贴出去/复制出去的必须是完整内容。
    /// </summary>
    internal static LauncherItem Row(ClipHistoryEntry entry) => new(
        Kind: LauncherKind.Clip,
        Title: entry.Preview.Length == 0 ? "（无预览）" : entry.Preview,
        Subtitle: Subtitle(entry),
        IconHint: "",
        Score: 0,                                        // D5：段内原序（Clip 段不做重排）
        Highlights: Array.Empty<(int, int)>(),
        PrimaryAction: new LauncherAction(LauncherActionKind.PasteBack, entry.Content),
        SecondaryAction: new LauncherAction(LauncherActionKind.CopyText, entry.Content),
        FileHit: null);

    /// <summary>
    /// 图片条目 → 结果行（W10-c）。主行 = 预览（<c>图片 800×600 · 12 KB</c>，入库时生成）；
    /// 主动作 = **提字**（OCR → 文本直贴回原前台，复用与文本行同一条直贴链）。
    ///
    /// <para>次动作**刻意留空**：图片没有"可复制的文本"，而"把图片重新写回剪贴板"对
    /// 已经能看见这张图的用户没有增量 —— 与其造一个语义含糊的次动作，不如明说没有
    /// （渲染层对 null 次动作走既有的"不可执行"分支）。</para>
    /// </summary>
    internal static LauncherItem ImageRow(ClipHistoryEntry entry) => new(
        Kind: LauncherKind.Clip,
        Title: entry.Preview.Length == 0 ? "（图片）" : entry.Preview,
        Subtitle: Subtitle(entry),
        IconHint: "",
        Score: 0,
        Highlights: Array.Empty<(int, int)>(),
        PrimaryAction: string.IsNullOrEmpty(entry.ImagePath)
            ? null                                       // 路径缺失（旧记录/解析失败）⇒ 不可执行，不编假动作
            : new LauncherAction(LauncherActionKind.OcrCopy, entry.ImagePath!),
        SecondaryAction: null,
        FileHit: null);

    /// <summary>副行文案（单点确定 —— 渲染层不拼字符串）。</summary>
    internal static string Subtitle(ClipHistoryEntry entry)
    {
        var source = string.IsNullOrWhiteSpace(entry.SourceApp) ? "未知" : entry.SourceApp;
        return $"来源 {source} · {entry.LastUsedAt:MM-dd HH:mm}";
    }

    private LauncherResultSet Empty(long generation) =>
        new(generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false, Error: null);
}
