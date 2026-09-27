// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.ClipboardLib;

/// <summary>条目类型（W5-剪贴板-设计方案.md §3.4：0=Text 1=Image 2=FileList）。</summary>
public enum ClipKind
{
    /// <summary>纯文本（HTML 复制降级存纯文本，见设计 §2.3 非目标）。</summary>
    Text = 0,

    /// <summary>图片（PNG 落盘，DB 只存相对路径——D3 拍板）。</summary>
    Image = 1,

    /// <summary>文件路径列表（资源管理器"复制文件"，换行分隔）。</summary>
    FileList = 2,
}

/// <summary>一条剪贴板历史。DB 行的内存形态；写入走 <see cref="HistoryStore.Upsert"/>。</summary>
public sealed record ClipEntry
{
    public long Id { get; init; }

    public ClipKind Kind { get; init; }

    /// <summary>文本全文 / 文件路径列表（换行分隔）；图片条目此列为 null（内容在盘上）。</summary>
    public string? Content { get; init; }

    /// <summary>列表摘要（入库时生成 ≤200 字符，读路径不做二次加工——设计 §3.4）。</summary>
    public string Preview { get; init; } = "";

    /// <summary>图片相对路径（相对 imagesDir，kind=Image 时非空）。</summary>
    public string? ImagePath { get; init; }

    public long ImageBytes { get; init; }

    /// <summary>SHA-256 内容指纹（去重键，设计 §3.2）。</summary>
    public string Hash { get; init; } = "";

    /// <summary>来源进程名（审计 + 隐私黑名单命中显示，FR-11）。</summary>
    public string? SourceApp { get; init; }

    public bool Pinned { get; init; }

    public int CopyCount { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime LastUsedAt { get; init; }

    /// <summary>占位条目固定文案（FR-11③：提权进程内容读不到，只记"发生过"不记内容）。</summary>
    public const string PlaceholderFormat = "[提权进程 {0} 复制的内容未保存]";
}
