// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Eztools.ClipboardLib;

/// <summary>
/// 剪贴板快照 → <see cref="ClipEntry"/>（W5-a 原语层，W5-剪贴板-设计方案.md §3.2）。
///
/// <para>格式识别优先级：FileList &gt; Text。HTML 复制降级为纯文本（设计 §2.3 非目标：
/// 小众格式原生保真不做）；Image 捕获要 DIB→PNG 转换，属于 W5-b/c 的宿主监听层，
/// 本类只提供数据通道（ImagePath/ImageBytes 字段），不碰位图。</para>
/// </summary>
public static partial class CaptureService
{
    /// <summary>设计 §3.4：preview ≤200 字符。</summary>
    public const int PreviewLength = 200;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>把读取到的剪贴板快照转成待入库条目；无可捕获内容（空剪贴板）返回 null。</summary>
    public static ClipEntry? FromSnapshot(ClipboardReader.Snapshot snapshot)
    {
        // 文件路径列表优先（资源管理器复制文件时往往同时带 Text 格式的路径串，
        // 但那只是 FileDrop 的投影——按 FileList 存才能保住"多选"语义）。
        if (snapshot.Files.Count > 0)
        {
            var paths = snapshot.Files.Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            if (paths.Count == 0)
            {
                return null;
            }

            var content = string.Join("\n", paths);
            return new ClipEntry
            {
                Kind = ClipKind.FileList,
                Content = content,
                Preview = Truncate(string.Join("  ", paths.Select(Path.GetFileName))),
                Hash = ComputeHash(content),
                SourceApp = snapshot.OwnerProcess,
            };
        }

        var text = snapshot.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new ClipEntry
        {
            Kind = ClipKind.Text,
            Content = text,
            Preview = Truncate(CollapseWhitespace(text)),
            Hash = ComputeHash(text),
            SourceApp = snapshot.OwnerProcess,
        };
    }

    /// <summary>FR-11③ 占位条目：提权进程的内容读不到，只记"发生过"，绝不存内容。</summary>
    public static ClipEntry FromPlaceholder(string ownerProcess)
    {
        var text = string.Format(ClipEntry.PlaceholderFormat, ownerProcess);
        return new ClipEntry
        {
            Kind = ClipKind.Text,
            Content = text,
            Preview = text,
            Hash = ComputeHash(text),
            SourceApp = ownerProcess,
        };
    }

    /// <summary>
    /// 图片条目（PNG 已落盘后组装；文件写入与 20MB 阈值归消费方 —— 它才知道 imagesDir 与配置）。
    /// </summary>
    public static ClipEntry FromImagePng(
        byte[] pngBytes, int pixelWidth, int pixelHeight, string imageFileName, string? sourceApp)
    {
        var kb = pngBytes.Length / 1024.0;
        return new ClipEntry
        {
            Kind = ClipKind.Image,
            Content = null,
            Preview = $"图片 {pixelWidth}×{pixelHeight} · {(kb >= 1024 ? $"{kb / 1024:0.0} MB" : $"{kb:0} KB")}",
            ImagePath = imageFileName,
            ImageBytes = pngBytes.Length,
            Hash = ComputeHash(pngBytes),
            SourceApp = sourceApp,
        };
    }

    public static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    /// <summary>字节内容的指纹（图片 PNG 落盘后的去重键 —— 与文本指纹同一命名空间，冲突概率可忽略）。</summary>
    public static string ComputeHash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content));

    internal static string Truncate(string text) =>
        text.Length <= PreviewLength ? text : text[..PreviewLength];

    private static string CollapseWhitespace(string text) =>
        Whitespace().Replace(text, " ").Trim();
}
