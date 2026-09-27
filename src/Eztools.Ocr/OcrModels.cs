// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Drawing;

namespace Eztools.Ocr;

/// <summary>识别出的单个单词：文本 + 包围盒（**预处理后位图坐标系**，物理像素）。</summary>
public sealed record OcrWord(string Text, Rectangle Bounds);

/// <summary>识别出的一行：行文本 + 单词序列（行内顺序由引擎给出，按阅读顺序）。</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>
/// 一次识别的完整结果。
/// <para>
/// 坐标系纪律（设计方案 §3.2）：这里的 Bounds / 点击点变换全部落在
/// <b>预处理后位图</b>的物理像素坐标系；要把原始位图坐标变换进来，
/// 用 <c>x * ScaleX + OffsetX</c> / <c>y * ScaleY + OffsetY</c>（与 PowerOCR 的 PreparedBitmap 同构）。
/// </para>
/// </summary>
/// <param name="Text">按行拼装的最终文本（行间 \r\n）。</param>
/// <param name="Lines">行序列。</param>
/// <param name="ScaleX">预处理横向缩放比（输出宽 / 源宽）。</param>
/// <param name="ScaleY">预处理纵向缩放比（输出高 / 源高）。</param>
/// <param name="OffsetX">预处理补边偏移（小图 &lt;64px 时四周补 8px 底色）。</param>
/// <param name="OffsetY">预处理补边偏移。</param>
public sealed record OcrResult(
    string Text,
    IReadOnlyList<OcrLine> Lines,
    double ScaleX,
    double ScaleY,
    int OffsetX,
    int OffsetY);
