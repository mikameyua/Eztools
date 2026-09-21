using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eztools.Contracts;

/// <summary>
/// JSON 序列化的统一出口。
///
/// 为什么需要它：**.NET 没有任何内置编码器能做到"完全不转义非 ASCII"** ——
/// <list type="bullet">
/// <item><c>JavaScriptEncoder.Default</c> 连中文都转义</item>
/// <item><c>UnsafeRelaxedJsonEscaping</c> 基于 <c>UnicodeRanges.All</c>，只覆盖 BMP
///   （U+0000–U+FFFF），**emoji 这类星平面字符照样被转义成代理对**</item>
/// </list>
/// 所以在序列化后统一做一次还原。控制台与配置文件都需要它，
/// 之前 <c>ConsoleUi</c> 里有一份私有实现，这里收口成一处，免得两份逻辑各自漂移。
/// </summary>
public static partial class JsonText
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>序列化。默认缩进（配置文件给人看，控制台输出同理）。</summary>
    public static string Write(JsonNode? node, bool indented = true)
    {
        if (node is null)
        {
            return "null";
        }

        var json = node.ToJsonString(indented ? Indented : Compact);
        return RestoreNonAsciiEscapes(json);
    }

    /// <summary>
    /// 把 <c>\uXXXX</c> 还原成真实字符。
    ///
    /// 只还原 <b>&gt;= U+0080</b> 的码点：ASCII 控制字符（<c>\t</c>、<c>\u0000</c> 等）保持转义，
    /// 否则会把制表符之类真的打进终端/文件里。
    /// 代理对是连续两个 <c>\uXXXX</c>，逐字符还原后自然拼回完整的 emoji。
    /// </summary>
    private static string RestoreNonAsciiEscapes(string json) =>
        UnicodeEscapeRegex().Replace(json, match =>
        {
            var codePoint = Convert.ToInt32(match.Groups[1].Value, 16);
            return codePoint >= 0x80 ? ((char)codePoint).ToString() : match.Value;
        });

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();
}
