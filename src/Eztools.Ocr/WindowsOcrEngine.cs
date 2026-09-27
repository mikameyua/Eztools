// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later
// 引擎封装机制改编自 PowerToys PowerOCR.Core 的 WindowsOcrRecognizer.cs 与
// Services/TextExtractorService.cs（MIT © Microsoft Corporation，本仓 PowerToys/ 目录内），
// 已在 THIRD-PARTY-NOTICES 登记来源。

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Eztools.Ocr;

/// <summary>OCR 语言环境快照：本机是否可用 + 可用语言标签（BCP-47）。</summary>
public static class OcrLanguages
{
    /// <summary>本机可用的 OCR 识别语言（Windows Feature <c>Language.OCR~</c> 驱动）。</summary>
    public static IReadOnlyList<string> AvailableTags()
        => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>
    /// 语言包是否缺失。缺失时引擎创建必然失败 —— 对应设计方案 R1：
    /// 必须显式引导 <c>Add-WindowsCapability</c>，禁止静默失败。
    /// </summary>
    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>语言包缺失时的安装引导文案（zh-CN 为主；其它语言包名按需替换）。</summary>
    public static string InstallHint => """
        未检测到任何可用的 OCR 语言包（Windows Feature: Language.OCR~）。
        以管理员 PowerShell 执行（示例为简体中文）：
          Add-WindowsCapability -Online -Name Language.OCR~~~zh-CN~0.0.1
        装完无需重启 Eztools，重新唤起 OCR 即可。
        """;
}

/// <summary>
/// Windows 系统本地 OCR 引擎（<c>Windows.Media.Ocr</c>）的封装。
/// 零模型文件、零网络、零第三方运行时；线程约定：同一实例的各方法无跨线程共享状态，
/// 但请在**同一线程**创建并使用（WinRT 对象的 apartment 亲和，设计方案 R6）。
/// </summary>
public sealed class WindowsOcrEngine
{
    private const double DefaultScale = 1.0;
    private const double EnhancedScale = 1.5;

    private readonly BitmapPreprocessor _preprocessor = new();

    private WindowsOcrEngine(OcrEngine engine)
    {
        Inner = engine;
        LanguageTag = engine.RecognizerLanguage.LanguageTag;
    }

    /// <summary>底层 WinRT 引擎（已按语言创建成功）。</summary>
    public OcrEngine Inner { get; }

    /// <summary>本实例使用的识别语言（BCP-47 标签）。</summary>
    public string LanguageTag { get; }

    /// <summary>引擎单边最大可接受边长（超出必须先缩，否则识别直接抛异常）。</summary>
    public static uint MaxImageDimension => OcrEngine.MaxImageDimension;

    /// <summary>
    /// 创建引擎。优先级：显式指定语言 → 用户配置语言 → 系统首选语言 → 任一可用语言。
    /// 全部落空（含语言包缺失）返回 <c>null</c> —— 调用方必须给出 R1 引导，不得吞掉。
    /// </summary>
    /// <param name="bcp47Tag">BCP-47 语言标签（如 zh-cn、en-us）；null 走自动选择。</param>
    public static WindowsOcrEngine? TryCreate(string? bcp47Tag = null)
    {
        if (!string.IsNullOrWhiteSpace(bcp47Tag))
        {
            var requested = new Language(bcp47Tag);
            return OcrEngine.TryCreateFromLanguage(requested) is { } byTag
                ? new WindowsOcrEngine(byTag)
                : null;
        }

        if (OcrEngine.TryCreateFromUserProfileLanguages() is { } fromProfile)
        {
            return new WindowsOcrEngine(fromProfile);
        }

        foreach (var candidate in OcrEngine.AvailableRecognizerLanguages)
        {
            if (OcrEngine.TryCreateFromLanguage(candidate) is { } fallback)
            {
                return new WindowsOcrEngine(fallback);
            }
        }

        return null;
    }

    /// <summary>识别一张位图（内部自动做 EnhancedScale 放大与 MaxImageDimension 钳制）。</summary>
    public async Task<OcrResult> RecognizeAsync(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using PreparedBitmap prepared = Prepare(bitmap);
        using SoftwareBitmap softwareBitmap = await DecodeAsync(prepared.Bitmap).ConfigureAwait(false);
        OcrResult ocrResult = await RunEngineAsync(softwareBitmap, prepared).ConfigureAwait(false);
        return ocrResult;
    }

    /// <summary>
    /// 命中测试：给一个**原始位图**坐标（物理像素），返回覆盖该点的单词；没有则 <c>null</c>。
    /// 这是 Text-Grab「单击取词」的引擎侧支撑（word 包围盒是系统引擎的免费输出）。
    /// </summary>
    public async Task<string?> TryGetWordAtAsync(Bitmap bitmap, Point sourcePoint)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using PreparedBitmap prepared = Prepare(bitmap);
        using SoftwareBitmap softwareBitmap = await DecodeAsync(prepared.Bitmap).ConfigureAwait(false);
        OcrResult ocrResult = await RunEngineAsync(softwareBitmap, prepared).ConfigureAwait(false);

        return FindWordAt(ocrResult, sourcePoint);
    }

    /// <summary>
    /// 在已有识别结果上做命中测试（避免为取词重复识别一次）：
    /// 把**源位图**坐标变换进预处理坐标系（×Scale + Offset），再对 word 包围盒做 Contains。
    /// </summary>
    public static string? FindWordAt(OcrResult result, Point sourcePoint)
    {
        ArgumentNullException.ThrowIfNull(result);

        var preparedPoint = new Point(
            (int)Math.Round(sourcePoint.X * result.ScaleX) + result.OffsetX,
            (int)Math.Round(sourcePoint.Y * result.ScaleY) + result.OffsetY);

        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                if (word.Bounds.Contains(preparedPoint))
                {
                    return word.Text;
                }
            }
        }

        return null;
    }

    // ── 内部 ──

    /// <summary>
    /// 三段回退选缩放比（PowerOCR 实测结论）：1.5× 放大（屏幕小字显著受益）→ 1.0× →
    /// 压回 MaxImageDimension 内。放大后超限就放弃放大，绝不交给引擎一张超限图。
    /// </summary>
    private PreparedBitmap Prepare(Bitmap source)
    {
        uint max = OcrEngine.MaxImageDimension;

        if (source.Width * EnhancedScale <= max
            && source.Height * EnhancedScale <= max)
        {
            return _preprocessor.Prepare(source, EnhancedScale);
        }

        if (source.Width <= max && source.Height <= max)
        {
            return _preprocessor.Prepare(source, DefaultScale);
        }

        double shrink = Math.Min(
            max / source.Width,
            max / source.Height);
        return _preprocessor.Prepare(source, shrink);
    }

    /// <summary>GDI 位图 → SoftwareBitmap。走 BMP 内存流 + BitmapDecoder 通道（PowerOCR 同款，
    /// 避免 SoftwareBitmap.CreateCopyFromSurfaceAsync 的 WinRT 表面互操作坑）。</summary>
    private static async Task<SoftwareBitmap> DecodeAsync(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        stream.Position = 0;

        using var randomAccessStream = stream.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask().ConfigureAwait(false);
        return await decoder.GetSoftwareBitmapAsync().AsTask().ConfigureAwait(false);
    }

    private async Task<OcrResult> RunEngineAsync(SoftwareBitmap softwareBitmap, PreparedBitmap prepared)
    {
        // ⚠️ 本命名空间里也有个 OcrResult —— 引擎原生结果必须全限定（Eztools.Ocr.OcrResult 优先于 using）。
        Windows.Media.Ocr.OcrResult native = await Inner.RecognizeAsync(softwareBitmap).AsTask().ConfigureAwait(false);

        var lines = native.Lines
            .Select(line => new OcrLine(
                line.Text,
                line.Words
                    .Select(word =>
                    {
                        // Windows.Foundation.Rect → System.Drawing.Rectangle 无隐式转换；
                        // 取整策略：原点向下取整、尺寸向上取整，保证包围盒只大不小（命中测试宁多勿漏）。
                        var r = word.BoundingRect;
                        var bounds = new Rectangle(
                            (int)Math.Floor(r.X),
                            (int)Math.Floor(r.Y),
                            (int)Math.Ceiling(r.Width),
                            (int)Math.Ceiling(r.Height));
                        return new OcrWord(word.Text, bounds);
                    })
                    .ToList()))
            .ToList();

        var text = string.Join("\r\n", lines.Select(l => l.Text));
        return new OcrResult(text, lines, prepared.ScaleX, prepared.ScaleY, prepared.OffsetX, prepared.OffsetY);
    }
}
