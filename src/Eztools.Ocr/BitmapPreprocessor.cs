// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later
// 位图预处理机制改编自 PowerToys PowerOCR.Core/Imaging/BitmapPreprocessor.cs
//（MIT © Microsoft Corporation，本仓 PowerToys/ 目录内），已在 THIRD-PARTY-NOTICES 登记来源。

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Eztools.Ocr;

/// <summary>预处理产出：缩放后的位图 + 坐标变换参数（调用方负责 Dispose）。</summary>
public sealed class PreparedBitmap : IDisposable
{
    public PreparedBitmap(Bitmap bitmap, double scaleX, double scaleY, int offsetX, int offsetY)
    {
        Bitmap = bitmap;
        ScaleX = scaleX;
        ScaleY = scaleY;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public Bitmap Bitmap { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }

    /// <summary>原始位图坐标（物理像素）→ 预处理后位图坐标。</summary>
    public Point Transform(Point source) => new(
        (int)Math.Round(source.X * ScaleX) + OffsetX,
        (int)Math.Round(source.Y * ScaleY) + OffsetY);

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// 识别前的位图预处理：按给定倍率高质量缩放；小于 <see cref="MinimumDimension"/> 的维度
/// 补到 64px 并四周垫 8px 源图底色（引擎对过小图直接摆烂，补边是 PowerOCR 实测出的有效手法）。
/// </summary>
public sealed class BitmapPreprocessor
{
    private const int MinimumDimension = 64;
    private const int Padding = 8;

    public Size GetOutputSize(Bitmap source, double scale)
    {
        var (scaled, output, _) = CalculateDimensions(source, scale);
        return output.IsEmpty ? scaled : output;
    }

    public PreparedBitmap Prepare(Bitmap source, double scale)
    {
        var (scaled, output, offset) = CalculateDimensions(source, scale);
        var result = new Bitmap(output.Width, output.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.Clear(source.GetPixel(0, 0));
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(
            source,
            new Rectangle(offset, scaled),
            new Rectangle(Point.Empty, source.Size),
            GraphicsUnit.Pixel);

        return new PreparedBitmap(
            result,
            scaled.Width / (double)source.Width,
            scaled.Height / (double)source.Height,
            offset.X,
            offset.Y);
    }

    private static (Size Scaled, Size Output, Point Offset) CalculateDimensions(Bitmap source, double scale)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, 0);

        int scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        int scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        bool padHorizontal = scaledWidth < MinimumDimension;
        bool padVertical = scaledHeight < MinimumDimension;
        int outputWidth = padHorizontal ? Math.Max(scaledWidth + (Padding * 2), MinimumDimension + (Padding * 2)) : scaledWidth;
        int outputHeight = padVertical ? Math.Max(scaledHeight + (Padding * 2), MinimumDimension + (Padding * 2)) : scaledHeight;
        int offsetX = padHorizontal ? Padding : 0;
        int offsetY = padVertical ? Padding : 0;

        return (
            new Size(scaledWidth, scaledHeight),
            new Size(outputWidth, outputHeight),
            new Point(offsetX, offsetY));
    }
}
