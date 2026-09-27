// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Eztools.Ocr;

/// <summary>
/// 内置样图工厂：把一段文本渲染成白底黑字位图。
/// 用途：`ezt ocr probe` 的引擎自检样张（免随包分发图片文件）、W4-b/w 的回归对照样张。
/// 刻意用 AntiAliasGridFit 而非 ClearType——样图要测"引擎对常规抗锯齿文字"的下限，
/// 不是测特定子像素渲染。
/// </summary>
public static class SampleImage
{
    /// <summary>渲染样图。调用方负责 Dispose。</summary>
    public static Bitmap RenderText(
        string text,
        int width = 880,
        int height = 220,
        float fontSizePx = 56f)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("样图文本不能为空", nameof(text));
        }

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var font = new Font("Segoe UI", fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.Black);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        SizeF measured = graphics.MeasureString(text, font);
        float x = (width - measured.Width) / 2f;
        float y = (height - measured.Height) / 2f;
        graphics.DrawString(text, font, brush, Math.Max(8f, x), Math.Max(8f, y));
        return bitmap;
    }
}
