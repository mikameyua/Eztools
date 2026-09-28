// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eztools.Cli;

/// <summary>控制台输出助手。含 CJK 宽度感知的表格对齐——中文环境下不自作这件事会很难看。</summary>
internal static partial class ConsoleUi
{
    public static bool JsonMode { get; set; }

    /// <summary>准备好控制台编码。重定向到管道时设置编码可能抛异常，忽略即可。</summary>
    public static void Prepare()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // review-guards:allow-empty-catch :: 输出被重定向时可能失败，不影响功能
        }
    }

    public static void Header(string title)
    {
        if (JsonMode)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=== " + title + " " + new string('=', Math.Max(0, 56 - DisplayWidth(title))));
    }

    public static void Section(string title)
    {
        if (JsonMode)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("── " + title + " " + new string('─', Math.Max(0, 56 - DisplayWidth(title))));
    }

    public static void Line(string text = "") => Console.WriteLine(JsonMode ? text : text);

    public static void Field(string label, string? value)
    {
        if (JsonMode)
        {
            return;
        }

        var pad = new string(' ', Math.Max(0, 18 - DisplayWidth(label)));
        Console.WriteLine($"  {label}{pad} {value}");
    }

    public static void Ok(string message) => WriteColored("  [ OK ] " + message, ConsoleColor.Green);

    public static void Pass(string message) => WriteColored("  [PASS] " + message, ConsoleColor.Green);

    // JSON 模式下：人类可读的进度信息一律不输出（stdout 要留给机器解析），
    // 但**错误与失败必须仍然可见**，且走 stderr —— 否则会出现最糟的情况：
    // 命令退出码非 0、stdout 是空文件、盘上什么都没有，而用户看不到任何原因。
    // （这个坑是验收脚本第一次跑就撞出来的：`list --json` 静默返回空。）
    public static void Fail(string message, string? detail = null) =>
        WriteColored("  [FAIL] " + message + (detail is null ? "" : $"  → {detail}"), ConsoleColor.Red,
            alwaysVisible: true);

    public static void Warn(string message) => WriteColored("  [警告] " + message, ConsoleColor.Yellow);

    public static void Info(string message) => WriteColored("  [信息] " + message, ConsoleColor.DarkGray);

    public static void Error(string message) =>
        WriteColored("[错误] " + message, ConsoleColor.Red, alwaysVisible: true);

    private static void WriteColored(string text, ConsoleColor color, bool alwaysVisible = false)
    {
        if (JsonMode && !alwaysVisible)
        {
            return;
        }

        // 机器可读模式下把诊断信息送 stderr，保证 stdout 里只有 JSON
        var target = JsonMode ? Console.Error : Console.Out;
        var previous = Console.ForegroundColor;
        try
        {
            if (!JsonMode)
            {
                Console.ForegroundColor = color;
            }

            target.WriteLine(text);
        }
        finally
        {
            if (!JsonMode)
            {
                Console.ForegroundColor = previous;
            }
        }
    }

    /// <summary>打印一个对齐的表格。</summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        if (JsonMode || rows.Count == 0)
        {
            return;
        }

        var columns = headers.Count;
        var widths = new int[columns];
        for (var i = 0; i < columns; i++)
        {
            widths[i] = DisplayWidth(headers[i]);
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < columns && i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], DisplayWidth(row[i]));
            }
        }

        Console.WriteLine("  " + string.Join("  ", headers.Select((h, i) => Pad(h, widths[i]))));
        Console.WriteLine("  " + string.Join("  ", widths.Select(w => new string('─', w))));

        foreach (var row in rows)
        {
            Console.WriteLine("  " + string.Join("  ", Enumerable.Range(0, columns)
                .Select(i => Pad(i < row.Length ? row[i] : string.Empty, widths[i]))));
        }
    }

    private static string Pad(string text, int width) => text + new string(' ', Math.Max(0, width - DisplayWidth(text)));

    /// <summary>终端显示宽度：CJK 与全角字符占 2 列。</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text)
        {
            width += IsWide(ch) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(char ch) =>
        (ch >= 0x1100 && ch <= 0x115F) ||
        (ch >= 0x2E80 && ch <= 0x303E) ||
        (ch >= 0x3041 && ch <= 0x33FF) ||
        (ch >= 0x3400 && ch <= 0x4DBF) ||
        (ch >= 0x4E00 && ch <= 0x9FFF) ||
        (ch >= 0xA000 && ch <= 0xA4CF) ||
        (ch >= 0xAC00 && ch <= 0xD7A3) ||
        (ch >= 0xF900 && ch <= 0xFAFF) ||
        (ch >= 0xFE30 && ch <= 0xFE6F) ||
        (ch >= 0xFF00 && ch <= 0xFF60) ||
        (ch >= 0xFFE0 && ch <= 0xFFE6);

    // 默认的 JSON 编码器会把所有非 ASCII 转义成 \uXXXX，中文输出会变成不可读的一串码点。
    // 用 UnsafeRelaxedJsonEscaping 输出原始字符。
    //   · UnicodeRanges.All 不够：它只覆盖 BMP，emoji（U+1F361 等代理对）照样被转义
    //   · "Unsafe" 指不转义 < > & ' + —— 这些在终端与 JSON 文件里无风险，
    //     但**不要**用它生成要嵌进 HTML 的内容
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>打印 JSON（结果输出的统一形态）。</summary>
    public static void PrintJson(JsonNode? node, bool compact = false)
    {
        if (node is null)
        {
            Console.WriteLine("null");
            return;
        }

        Console.WriteLine(RestoreNonAsciiEscapes(node.ToJsonString(compact ? CompactJson : IndentedJson)));
    }

    /// <summary>
    /// 把 JSON 里的非 ASCII <c>\uXXXX</c> 转义还原成真实字符。
    ///
    /// 为什么需要这一步：.NET 没有任何内置编码器能做到"完全不转义非 ASCII"——
    /// <c>JavaScriptEncoder.Default</c> 连中文都转义；<c>UnsafeRelaxedJsonEscaping</c> 基于
    /// <c>UnicodeRanges.All</c>，只覆盖 BMP（U+0000–U+FFFF），**emoji 这类星平面字符照样被转义**。
    /// 控制台已经是 UTF-8，所以在这里做一次还原最省事。
    ///
    /// 只还原 &gt;= U+0080 的码点：ASCII 控制字符（如 <c>\t</c>/<c>\u0000</c>）保持转义，
    /// 否则会把制表符之类真的打进终端里。
    /// 代理对是连续两个 <c>\uXXXX</c>，逐字符还原后自然拼回完整的 emoji。
    /// </summary>
    /// <summary>只还原"真转义"的 \uXXXX：前面紧挨着反斜杠的（即文本里字面出现的 \uXXXX，
    /// 序列化时反斜杠已被转义为 \\）不还原 —— 否则会吃掉一层转义留下孤反斜杠，产出非法 JSON
    /// （实测：detail 内嵌 ToJsonString() 输出时，Default 编码器的 \u5FC5 变成 \必，下游解析当场炸）。
    /// 代价：字面 \uXXXX 文本不再被还原 —— 正确性优先于美观。</summary>
    private static string RestoreNonAsciiEscapes(string json) =>
        UnicodeEscapeRegex().Replace(json, match =>
        {
            var codePoint = Convert.ToInt32(match.Groups[1].Value, 16);
            return codePoint >= 0x80 ? ((char)codePoint).ToString() : match.Value;
        });

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\\)\\u([0-9a-fA-F]{4})")]
    private static partial System.Text.RegularExpressions.Regex UnicodeEscapeRegex();

    public static void PrintJsonObject(JsonObject node) => PrintJson(node);
}
