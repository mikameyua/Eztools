// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace Eztools.Host.Launcher;

/// <summary>编码转换方向（W7-d，设计方案 §10.8 B 的六种前缀）。</summary>
public enum EncodeDirection
{
    Base64,
    Base64Decode,
    Url,
    UrlDecode,
    CodePoints,
    FromCodePoints,
}

/// <summary>一次编码转换请求。<paramref name="Raw"/> = 用户原样输入（含前缀，Ctrl+Enter 复制用）。</summary>
public readonly record struct EncodeRequest(EncodeDirection Direction, string Payload, string Raw);

/// <summary>转换结局。<see cref="Ok"/>=false 时 <see cref="ErrorText"/> 是**行上直接显示的文案**。</summary>
public readonly record struct EncodeOutcome(bool Ok, string Result, string ErrorText);

/// <summary>
/// 编码转换来源（W7-d，设计方案 §10.8 B / D9=A）—— `b64:` / `b64d:` / `url:` / `urld:` / `u:` / `ud:` ⇒ 一行结果。
///
/// <para><b>★ 只认显式前缀（D9=A），不做任何自动嗅探</b>：base64 是**弱特征** —— `abcd`、`test`、
/// 任何"长度为 4 的倍数且字符都在 base64 字母表里"的纯字母串都合法。自动嗅探必然误报，
/// 而误报的代价是**污染文件搜索结果**（用户搜 `test` 却收到一条"编码结果"）。前缀只多打 4 个字符。</para>
///
/// <para><b>★ 非法输入 = 显式错误行，不是静默</b>：与 calc 的求值错同一条理由 —— 用户**已经明确
/// 表达了意图**（打了 `b64d:`），此时静默会让人以为功能坏了。对照 calc 的**语法错**（`1+`）静默：
/// 那是"用户还没打完"，与这里语义不同。</para>
/// </summary>
public sealed class EncodeProvider : ILauncherProvider
{
    /// <summary>段内置顶分。</summary>
    public const double PinScore = 1e9;

    /// <summary>前缀表。★ **长前缀必须排在短前缀之前**（`b64d:` 先于 `b64:`，`urld:` 先于 `url:`，`ud:` 先于 `u:`）。</summary>
    private static readonly (string Prefix, EncodeDirection Direction)[] Prefixes =
    [
        ("b64d:", EncodeDirection.Base64Decode),
        ("b64:", EncodeDirection.Base64),
        ("urld:", EncodeDirection.UrlDecode),
        ("url:", EncodeDirection.Url),
        ("ud:", EncodeDirection.FromCodePoints),
        ("u:", EncodeDirection.CodePoints),
    ];

    /// <inheritdoc />
    public string Id => LauncherProviderRegistry.Encode;

    /// <inheritdoc />
    public string DisplayName => "编码转换";

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
    {
        if (!TryParse(query.Text, out var request))
        {
            return Task.FromResult(Empty(query.Generation));
        }

        var outcome = Transform(request);
        return Task.FromResult(new LauncherResultSet(
            query.Generation, Id, [Row(request, outcome)], 1, 0, Dropped: false, Error: null));
    }

    /// <summary>
    /// 前缀识别：**大小写不敏感**，前缀后允许**一个**空格，其余原样当载荷。
    /// 载荷为空 ⇒ false（半成品输入 `b64:` 静默 —— 与 calc 的语法错同处置）。
    /// </summary>
    public static bool TryParse(string? text, out EncodeRequest request)
    {
        request = default;
        var s = (text ?? "").TrimStart();
        if (s.Length == 0)
        {
            return false;
        }

        foreach (var (prefix, direction) in Prefixes)
        {
            if (s.Length < prefix.Length
                || !s.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = s[prefix.Length..];
            if (payload.StartsWith(' '))
            {
                payload = payload[1..];
            }

            if (payload.Length == 0)
            {
                return false;
            }

            request = new EncodeRequest(direction, payload, s);
            return true;
        }

        return false;
    }

    /// <summary>执行转换。失败时 <c>Ok=false</c> 且 <see cref="EncodeOutcome.ErrorText"/> 可直接上屏。</summary>
    public static EncodeOutcome Transform(EncodeRequest request) => request.Direction switch
    {
        EncodeDirection.Base64 => Ok(System.Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Payload))),

        EncodeDirection.Base64Decode => DecodeBase64(request.Payload),

        EncodeDirection.Url => Ok(Uri.EscapeDataString(request.Payload)),

        EncodeDirection.UrlDecode => DecodeUrl(request.Payload),

        EncodeDirection.CodePoints => Ok(ToCodePoints(request.Payload)),

        _ => FromCodePoints(request.Payload),
    };

    private static EncodeOutcome Ok(string result) => new(true, result, "");

    private static EncodeOutcome Fail(string errorText) => new(false, "", errorText);

    private static EncodeOutcome DecodeBase64(string payload)
    {
        try
        {
            var bytes = System.Convert.FromBase64String(payload);
            return Ok(Encoding.UTF8.GetString(bytes));
        }
        catch (FormatException)
        {
            // 长度不是 4 的倍数 / 含字母表外字符 —— 都是"这不是 base64"，给同一条可读文案
            return Fail("无效的 base64 输入");
        }
    }

    private static EncodeOutcome DecodeUrl(string payload)
    {
        // ★ `Uri.UnescapeDataString` 对残缺的 `%XX` **不抛也不报**，原样返回 —— 那样"非法输入"
        //   就会静默成"转换成功但结果等于输入"。自己先扫一遍，把这条路堵掉。
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != '%')
            {
                continue;
            }

            if (i + 2 >= payload.Length
                || !IsHex(payload[i + 1])
                || !IsHex(payload[i + 2]))
            {
                return Fail("无效的 URL 编码");
            }

            i += 2;
        }

        // `+` **不视作空格**（Uri.UnescapeDataString 语义，写死）—— 否则 base64 结果里的 `+` 会被吃掉
        return Ok(Uri.UnescapeDataString(payload));
    }

    private static bool IsHex(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>
    /// 字符 → 码位。**按码点而非码元走**：emoji 是代理对，逐 <c>char</c> 会吐出两个
    /// `U+D83D`/`U+DE00` 这种半截码位（用户看到的是一对无意义数字）。
    /// </summary>
    private static string ToCodePoints(string payload)
    {
        var sb = new StringBuilder(payload.Length * 8);
        for (var i = 0; i < payload.Length; i++)
        {
            var cp = char.IsHighSurrogate(payload[i]) && i + 1 < payload.Length
                     && char.IsLowSurrogate(payload[i + 1])
                ? char.ConvertToUtf32(payload[i], payload[i + 1])
                : payload[i];

            if (cp > 0xFFFF)
            {
                i++;   // 吃掉低代理项
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append("U+").Append(cp.ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 码位 → 字符。接受 `U+XXXX` 与 `\uXXXX` 两种写法（BMP 内），以及 `U+1F600` 这类代理对码位。
    /// </summary>
    private static EncodeOutcome FromCodePoints(string payload)
    {
        var sb = new StringBuilder(payload.Length);
        var i = 0;
        while (i < payload.Length)
        {
            while (i < payload.Length && char.IsWhiteSpace(payload[i]))
            {
                i++;
            }

            if (i >= payload.Length)
            {
                break;
            }

            if (!TryReadCodePoint(payload, ref i, out var cp))
            {
                return Fail("无效的码位（应形如 U+4E2D 或 \\u4E2D）");
            }

            try
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
            catch (ArgumentOutOfRangeException)
            {
                // 代理区（U+D800~U+DFFF）不是合法码位，ConvertFromUtf32 会抛 —— 转成可读文案
                return Fail("无效的码位（代理区不是码位）");
            }
        }

        return sb.Length == 0
            ? Fail("无效的码位（应形如 U+4E2D 或 \\u4E2D）")
            : Ok(sb.ToString());
    }

    private static bool TryReadCodePoint(string s, ref int i, out int codePoint)
    {
        codePoint = 0;
        var body = false;

        if (i + 1 < s.Length && (s[i] is 'U' or 'u') && s[i + 1] == '+')
        {
            i += 2;
        }
        else if (i + 1 < s.Length && s[i] == '\\' && (s[i + 1] is 'u' or 'U'))
        {
            i += 2;
        }
        else
        {
            return false;
        }

        var start = i;
        while (i < s.Length && IsHex(s[i]) && i - start < 6)
        {
            i++;
        }

        if (i == start)
        {
            return false;
        }

        body = int.TryParse(s[start..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codePoint);
        return body;
    }

    /// <summary>方向说明（副行文案，§4.3）。单点确定，渲染层不拼字符串。</summary>
    internal static string DirectionText(EncodeDirection direction) => direction switch
    {
        EncodeDirection.Base64 => "base64 编码",
        EncodeDirection.Base64Decode => "base64 解码",
        EncodeDirection.Url => "URL 编码",
        EncodeDirection.UrlDecode => "URL 解码",
        EncodeDirection.CodePoints => "字符 → 码位",
        _ => "码位 → 字符",
    };

    /// <summary>
    /// 一行结果。成功 ⇒ 主行 = 结果、动作可复制；失败 ⇒ 主行 = 错误文案、**动作禁用**
    /// （"没有可复制的结果"必须与"有结果"可区分，不能靠一个空串糊过去）。
    /// </summary>
    internal static LauncherItem Row(EncodeRequest request, EncodeOutcome outcome)
    {
        var direction = DirectionText(request.Direction);

        if (!outcome.Ok)
        {
            return new LauncherItem(
                Kind: LauncherKind.Encode,
                Title: outcome.ErrorText,
                Subtitle: direction,
                IconHint: "",
                Score: PinScore,
                Highlights: Array.Empty<(int, int)>(),
                PrimaryAction: null,
                SecondaryAction: null,
                FileHit: null);
        }

        return new LauncherItem(
            Kind: LauncherKind.Encode,
            Title: outcome.Result,
            Subtitle: direction,
            IconHint: "",
            Score: PinScore,
            Highlights: Array.Empty<(int, int)>(),
            PrimaryAction: new LauncherAction(LauncherActionKind.CopyText, outcome.Result),
            // §4.2：Ctrl+Enter 复制「原式 = 结果」整串（原式含前缀，用户能看出这是从哪来的）
            SecondaryAction: new LauncherAction(
                LauncherActionKind.CopyText, $"{request.Raw} = {outcome.Result}"),
            FileHit: null);
    }

    private LauncherResultSet Empty(long generation) =>
        new(generation, Id, Array.Empty<LauncherItem>(), 0, 0, Dropped: false, Error: null);
}
