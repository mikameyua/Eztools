// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using Eztools.Host;
using Eztools.Ocr;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt ocr</c> 子命令（W4-a：引擎原语 + CLI 探针，设计方案 §7）。
///
/// <list type="bullet">
/// <item><c>ezt ocr langs</c> —— 列出本机可用 OCR 语言包；缺失时给出安装引导并退出码 7（R1：禁静默）。</item>
/// <item><c>ezt ocr file &lt;图片路径&gt;</c> —— 对本地图片文件做 OCR（--lang 指定 BCP-47 标签）。</item>
/// <item><c>ezt ocr probe</c>（顶层别名 <c>ezt --probe-ocr</c>）—— 引擎自检探针：
/// 语言数 + 内置样图识别字数 + 耗时，JSON 输出全部可断言（设计方案"断言落数字"纪律）。</item>
/// </list>
///
/// <para>退出码契约：0 成功 · 1 探针自检未过 · 4 文件不存在/不可解码 ·
/// 7 无可用 OCR 语言包 · 64 用法错误。</para>
/// </summary>
internal static class OcrCommand
{
    /// <summary>自检样图文本：同时含字母与数字，"OCR" 子串 + 数字出现是"识别成功"的最低判据。</summary>
    private const string SampleText = "EZTOOLS OCR 2026";

    public static async Task<int> RunAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");

        // `--probe-ocr` 顶层别名：与设计文档 §7 W4-a 的探针命名一致。
        // 纯 flag 调用时 cli.Command 为 null，switch 会把它当 help 吞掉，所以必须在子命令分流之前接住。
        if (cli.GetBool("probe-ocr"))
        {
            return await ProbeAsync(cli).ConfigureAwait(false);
        }

        var sub = cli.Rest.Count > 0 ? cli.Rest[0] : "help";
        return sub switch
        {
            "langs" => await LangsAsync(cli).ConfigureAwait(false),
            "file" => await FileAsync(cli).ConfigureAwait(false),
            "screen" => await ScreenAsync(cli).ConfigureAwait(false),
            "probe" => await ProbeAsync(cli).ConfigureAwait(false),
            "help" or "--help" or "-h" => Help(),
            _ => UnknownSub(sub),
        };
    }

    private static int UnknownSub(string sub)
    {
        ConsoleUi.Error($"未知子命令: ocr {sub}（可用：langs / file / probe）");
        return 64;
    }

    private static int Help()
    {
        ConsoleUi.Header("ezt ocr —— 屏幕 OCR 引擎原语（W4-a）");
        ConsoleUi.Info("  ocr langs                可用 OCR 语言包清单（--json 机器可读）");
        ConsoleUi.Info("  ocr file <图片路径>      本地图片 OCR（--lang <tag> 指定语言；--json → chars/lines/elapsedMs/text）");
        ConsoleUi.Info("  ocr screen               真实屏幕 OCR（--rect x,y,w,h 物理像素；缺省=整个虚拟桌面；--json 同 file）");
        ConsoleUi.Info("  ocr probe / --probe-ocr  引擎自检：语言数 + 内置样图识别字数 + 耗时");
        ConsoleUi.Info("                           （--save-sample <png> 导出内置样图，供 ocr file 复测同一张图）");
        ConsoleUi.Info("  退出码: 0 成功 · 1 自检未过 · 4 文件不存在/不可解码 · 7 无可用语言包 · 64 用法错误");
        return 0;
    }

    // ── langs ──

    private static Task<int> LangsAsync(CliArgs cli)
    {
        var tags = OcrLanguages.AvailableTags();

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["ok"] = tags.Count > 0,
                ["count"] = tags.Count,
                ["languages"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Header("OCR 可用语言");
            ConsoleUi.Field("语言包", tags.Count > 0 ? $"可用（{tags.Count} 个）" : "缺失");

            foreach (var tag in tags)
            {
                ConsoleUi.Info($"  {tag}");
            }

            if (tags.Count == 0)
            {
                ConsoleUi.Fail("没有任何可用的 OCR 语言包（Windows Feature: Language.OCR~）", OcrLanguages.InstallHint);
            }
        }

        return Task.FromResult(tags.Count > 0 ? 0 : 7);
    }

    // ── file ──

    private static async Task<int> FileAsync(CliArgs cli)
    {
        var pathArg = cli.Rest.Count > 1 ? cli.Rest[1] : cli.Get("path", "file");
        if (string.IsNullOrWhiteSpace(pathArg))
        {
            ConsoleUi.Error("用法: ezt ocr file <图片路径> [--lang <tag>] [--json]");
            return 64;
        }

        // 与 CliArgs.BuildParams 同款：CLI 层负责绝对化（工具进程的 cwd 语义不在本命令）。
        var path = PathInput.NormalizeToFullPath(pathArg);
        if (!File.Exists(path))
        {
            ConsoleUi.Error($"文件不存在: {path}");
            return 4;
        }

        var engine = WindowsOcrEngine.TryCreate(cli.Get("lang"));
        if (engine is null)
        {
            return ReportNoEngine(cli);
        }

        OwnedBitmap? bitmap = LoadBitmap(path);
        if (bitmap is null)
        {
            return 4;
        }

        using (bitmap)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await engine.RecognizeAsync(bitmap.Image).ConfigureAwait(false);
            stopwatch.Stop();

            var chars = result.Text.Length;

            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject
                {
                    ["ok"] = true,
                    ["path"] = path,
                    ["lang"] = engine.LanguageTag,
                    ["chars"] = chars,
                    ["lines"] = result.Lines.Count,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["text"] = result.Text,
                }, cli.GetBool("compact"));
            }
            else
            {
                // 识别文本原样输出（可能为空 —— chars=0 本身就是可断言的结果）。
                Console.WriteLine(result.Text);
                ConsoleUi.Field("语言", engine.LanguageTag);
                ConsoleUi.Field("字符数", chars.ToString("N0"));
                ConsoleUi.Field("行数", result.Lines.Count.ToString("N0"));
                ConsoleUi.Field("耗时", $"{stopwatch.ElapsedMilliseconds} ms");
            }

            // 识别流程成功即 0；"一个字都没认出"用 chars=0 表达（数字可断言），不用退出码混载两种语义。
            return 0;
        }
    }

    /// <summary>
    /// 位图 + 其内存流的同生命周期持有者。GDI+ 的 <c>new Bitmap(stream)</c> 要求
    /// 流在位图整个存活期保持打开，两者必须一起 Dispose（<c>Image</c> 没有 Disposed 事件可用）。
    /// </summary>
    private sealed class OwnedBitmap : IDisposable
    {
        public OwnedBitmap(Bitmap image, MemoryStream stream)
        {
            Image = image;
            _stream = stream;
        }

        public Bitmap Image { get; }

        private readonly MemoryStream _stream;

        public void Dispose()
        {
            Image.Dispose();
            _stream.Dispose();
        }
    }

    private static OwnedBitmap? LoadBitmap(string path)
    {
        try
        {
            // 先整读进内存：new Bitmap(path) 会锁文件直到 Dispose；Bitmap(stream) 要求流存活到位图销毁。
            var stream = new MemoryStream(File.ReadAllBytes(path));
            return new OwnedBitmap(new Bitmap(stream), stream);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            ConsoleUi.Error($"无法解码图片: {path}（{ex.Message}）");
            return null;
        }
    }

    // ── screen ──
    // W4-a 增补（2026-09-26）：设计文档 §7 W4-a 交付了 ScreenCapture 截图原语，但 file/probe 都不经过它
    // —— 原语零覆盖违反"断言落点"纪律；同时精度评估关卡需要**实屏**证据（渲染样图 ≠ 屏幕实拍）。
    // 本子命令把两者一次闭环：抓真实屏幕区域 → OCR。

    private static async Task<int> ScreenAsync(CliArgs cli)
    {
        var engine = WindowsOcrEngine.TryCreate(cli.Get("lang"));
        if (engine is null)
        {
            return ReportNoEngine(cli);
        }

        Rectangle region;
        if (cli.Get("rect") is { } rectArg)
        {
            if (!TryParseRect(rectArg, out region))
            {
                ConsoleUi.Error($"--rect 格式应为 x,y,w,h（物理像素）: {rectArg}");
                return 64;
            }
        }
        else
        {
            region = ScreenCapture.GetVirtualScreenBounds();
        }

        Bitmap captured;
        try
        {
            captured = ScreenCapture.GrabRegion(region);
        }
        catch (Exception ex)
        {
            ConsoleUi.Error($"屏幕截图失败: {ex.Message}");
            return 5;
        }

        using (captured)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await engine.RecognizeAsync(captured).ConfigureAwait(false);
            stopwatch.Stop();

            var chars = result.Text.Length;

            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject
                {
                    ["ok"] = true,
                    ["rect"] = $"{region.X},{region.Y},{region.Width},{region.Height}",
                    ["lang"] = engine.LanguageTag,
                    ["chars"] = chars,
                    ["lines"] = result.Lines.Count,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["text"] = result.Text,
                }, cli.GetBool("compact"));
            }
            else
            {
                Console.WriteLine(result.Text);
                ConsoleUi.Field("区域", $"{region.X},{region.Y},{region.Width},{region.Height}");
                ConsoleUi.Field("语言", engine.LanguageTag);
                ConsoleUi.Field("字符数", chars.ToString("N0"));
                ConsoleUi.Field("行数", result.Lines.Count.ToString("N0"));
                ConsoleUi.Field("耗时", $"{stopwatch.ElapsedMilliseconds} ms");
            }

            return 0;
        }
    }

    private static bool TryParseRect(string text, out Rectangle rect)
    {
        rect = default;
        var parts = text.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        var nums = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out nums[i]))
            {
                return false;
            }
        }

        if (nums[2] <= 0 || nums[3] <= 0)
        {
            return false;
        }

        rect = new Rectangle(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    // ── probe ──

    private static async Task<int> ProbeAsync(CliArgs cli)
    {
        var tags = OcrLanguages.AvailableTags();

        var createStopwatch = Stopwatch.StartNew();
        var engine = WindowsOcrEngine.TryCreate(cli.Get("lang"));
        createStopwatch.Stop();

        if (engine is null)
        {
            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject
                {
                    ["ok"] = false,
                    ["languages"] = tags.Count,
                    ["engineCreated"] = false,
                    ["reason"] = tags.Count == 0 ? "no-language-pack" : "engine-create-failed",
                    ["message"] = tags.Count == 0 ? OcrLanguages.InstallHint : "指定语言不可用（--lang 检查标签）",
                }, cli.GetBool("compact"));
            }
            else
            {
                ConsoleUi.Fail("OCR 引擎创建失败", tags.Count == 0 ? OcrLanguages.InstallHint : "指定的 --lang 在本机不可用，先 `ezt ocr langs` 看清单");
            }

            return 1;
        }

        using var sample = SampleImage.RenderText(SampleText);
        var recognizeStopwatch = Stopwatch.StartNew();
        var result = await engine.RecognizeAsync(sample).ConfigureAwait(false);
        recognizeStopwatch.Stop();

        var text = result.Text.Trim();

        // --save-sample 两种输出模式都要生效（首次实现在 else 分支里，--json 下被静默跳过——已修）。
        string? savedSamplePath = null;
        if (cli.Get("save-sample") is { } savePath)
        {
            sample.Save(savePath, ImageFormat.Png);
            savedSamplePath = System.IO.Path.GetFullPath(savePath);
        }

        // 最低判据："OCR" 字样 + 至少一个数字 都被认出来，且总量不过少。
        // 样图文本 "EZTOOLS OCR 2026" 共 16 字符 —— 认出 <8 视为引擎状态异常。
        var matched = text.Contains("OCR", StringComparison.OrdinalIgnoreCase)
            && text.Any(char.IsDigit)
            && text.Length >= 8;

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["ok"] = matched,
                ["languages"] = tags.Count,
                ["language"] = engine.LanguageTag,
                ["engineCreated"] = true,
                ["engineCreateMs"] = createStopwatch.ElapsedMilliseconds,
                ["recognizeMs"] = recognizeStopwatch.ElapsedMilliseconds,
                ["sampleChars"] = text.Length,
                ["sampleLines"] = result.Lines.Count,
                ["sampleText"] = text,
                ["maxImageDimension"] = WindowsOcrEngine.MaxImageDimension,
                ["appliedScale"] = result.ScaleX,
                ["sampleSaved"] = savedSamplePath,
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Header("OCR 引擎自检");
            ConsoleUi.Field("语言包", tags.Count > 0 ? $"{tags.Count} 个可用" : "缺失");
            ConsoleUi.Field("识别语言", engine.LanguageTag);
            ConsoleUi.Field("样图文本", SampleText);
            ConsoleUi.Field("识别结果", string.IsNullOrEmpty(text) ? "（空）" : text);
            ConsoleUi.Field("字符数", text.Length.ToString());
            ConsoleUi.Field("创建/识别耗时", $"{createStopwatch.ElapsedMilliseconds} ms / {recognizeStopwatch.ElapsedMilliseconds} ms");

            if (savedSamplePath is not null)
            {
                ConsoleUi.Info($"样图已存盘: {savedSamplePath}");
            }

            if (matched)
            {
                ConsoleUi.Ok("自检通过：引擎可用，样图文字被识别");
            }
            else
            {
                ConsoleUi.Fail("自检未通过：样图文字未被正确识别（引擎在，但识别质量异常）",
                    "先 `ezt ocr langs` 确认语言包；再看样图 --save-sample 导出人工核查渲染");
            }
        }

        return matched ? 0 : 1;
    }

    private static int ReportNoEngine(CliArgs cli)
    {
        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["ok"] = false,
                ["reason"] = "no-ocr-language",
                ["message"] = OcrLanguages.InstallHint,
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Fail("OCR 引擎创建失败（语言包缺失，或 --lang 指定的语言不可用）", OcrLanguages.InstallHint);
        }

        return 7;
    }
}
