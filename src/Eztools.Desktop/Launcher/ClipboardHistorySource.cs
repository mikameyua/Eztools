// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.ClipboardLib;
using Eztools.Host.Launcher;

namespace Eztools.Desktop;

/// <summary>
/// 把 W5 的剪贴板存储接到启动器的 <see cref="IClipboardHistorySource"/> 抽象上（W10-a）。
///
/// <para><b>★ 为什么需要这一层适配</b>：存储类型所在的程序集是 <c>net10.0-windows</c> + WPF，
/// 而启动器的纯逻辑层是平台中立的 <c>net10.0</c> —— 编译器不允许跨 TFM 直接引用。
/// 于是"检索能力"由 Host 声明、"真实实现"在这里落位（与 apps 侧 Win32 代码落 Desktop 同一条
/// 分层纪律，见 W7 设计方案 §3.2）。</para>
///
/// <para><b>★ 复用同一个 store 实例</b>：调用方传进来的就是托盘 <c>EnsureClipStore()</c> 的实例
/// —— 与 W5 面板 / <c>ezt clip</c> 共用一份库（SQLite WAL 并存，W5-c 已验证形态）。</para>
/// </summary>
internal sealed class ClipboardHistorySource : IClipboardHistorySource
{
    private readonly Func<HistoryStore> _ensure;

    /// <param name="ensure">惰性取库（生产 = 托盘的 <c>EnsureClipStore</c>，返回共享同一实例）。</param>
    internal ClipboardHistorySource(Func<HistoryStore> ensure) =>
        _ensure = ensure ?? throw new ArgumentNullException(nameof(ensure));

    /// <inheritdoc />
    public IReadOnlyList<ClipHistoryEntry> Search(string query, int limit)
    {
        // ★ 检索时才建库：建连/建表失败 ⇒ 这次查询抛 ⇒ QueryRouter 隔离成"clip 段暂不可用"，
        //   **不会**升级成"搜索窗创建失败"（FR-6：一个来源坏了不拖垮其它段）。
        var store = _ensure();
        return Map(store, store.Search(query, limit));
    }

    /// <inheritdoc />
    public IReadOnlyList<ClipHistoryEntry> ListImages(int limit)
    {
        // W10-c：图片只有这一个入口（content 为 NULL ⇒ FTS/LIKE 都搜不到）。
        // 与 W5 面板"图片筛选"走同一个 List(kind) —— 序（pinned 优先、最近使用在前）因此一致。
        var store = _ensure();
        return Map(store, store.List(ClipKind.Image, limit));
    }

    /// <summary>
    /// 真实条目 → 中立投影（单点转换）。<b>图片路径在这里解析为绝对路径</b>：store 里存的是
    /// 相对 <c>images/</c> 的相对路径（W5 D3 的选择），而动作执行方要读盘 ——
    /// 路径拼接由**知道 images 目录的这一层**做，Host 侧拿到的永远是可直接用的绝对路径。
    /// 解析不出来（目录未配置 / 文件已随保留期清掉）就置 null ⇒ provider 视为"该行不可执行"。
    /// </summary>
    private static List<ClipHistoryEntry> Map(HistoryStore store, List<ClipEntry> rows)
    {
        var dir = store.ImagesDirectory;
        var list = new List<ClipHistoryEntry>(rows.Count);
        foreach (var entry in rows)
        {
            var isImage = entry.Kind == ClipKind.Image;
            string? fullImagePath = null;
            if (isImage && !string.IsNullOrEmpty(entry.ImagePath) && dir is not null)
            {
                try
                {
                    fullImagePath = Path.GetFullPath(Path.Combine(dir, entry.ImagePath));
                }
                catch (Exception)
                {
                    // review-guards:allow-empty-catch :: 非法路径字符（手工动过库）⇒ 当作"路径不可用"，
                    // 由 provider 走"不可执行"分支；为它刷日志没有增量（用户看得见那行点不动）
                }
            }

            list.Add(new ClipHistoryEntry(
                IsText: entry.Kind == ClipKind.Text,
                Content: entry.Content ?? "",
                Preview: entry.Preview,
                SourceApp: entry.SourceApp,
                LastUsedAt: entry.LastUsedAt,
                IsImage: isImage,
                ImagePath: fullImagePath));
        }

        return list;
    }
}
