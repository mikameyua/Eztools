namespace Eztools.Host;

/// <summary>
/// 路径入参归一化。
///
/// <b>为什么需要这个</b>：在 Git Bash 里，人会自然地写 POSIX 形式路径（<c>/d/01-…</c>、<c>/tmp/x</c>）。
/// 这类路径在 Windows 上是"有根无盘符"，交给 .NET 后会按**进程当前盘符**解析：
/// <c>/d/01-…</c> → <c>D:\d\01-…</c>、<c>/tmp/x</c> → <c>C:\tmp\x</c>。
/// 结果是命令退出码 0、目录也真的建了，**只是建在别处**——实测在同一个项目里连续踩到三次
/// （一次来自 <c>pwd</c>、一次来自 <c>$TEMP</c>、一次来自 <c>--tools-dir</c> 参数）。
///
/// 靠文档提醒解决不了这件事：只要能写 POSIX 路径的地方，迟早会有人写。
/// 所以在**入口处**统一归一化。
///
/// 转换规则保守且无歧义：只认 <c>/&lt;单个 ASCII 字母&gt;/…</c> 这种形态
/// （即 MSYS 的盘符写法）。其他以 <c>/</c> 开头的串一律原样返回，不猜。
/// </summary>
public static class PathInput
{
    /// <summary>把 MSYS 风格的 <c>/d/…</c> 归一化为 Windows 的 <c>D:/…</c>；其余原样返回。</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        var text = path.Trim();

        // /d           → D:
        // /d/foo/bar   → D:/foo/bar
        if (text.Length >= 2
            && text[0] == '/'
            && IsAsciiLetter(text[1])
            && (text.Length == 2 || text[2] == '/'))
        {
            var drive = char.ToUpperInvariant(text[1]);
            return text.Length == 2 ? $"{drive}:" : $"{drive}:{text[2..]}";
        }

        return text;
    }

    /// <summary>归一化分号分隔的多路径。</summary>
    public static IReadOnlyList<string> NormalizeList(string? paths) =>
        string.IsNullOrWhiteSpace(paths)
            ? Array.Empty<string>()
            : paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize)
                .Where(p => p.Length > 0)
                .ToArray();

    /// <summary>归一化后取绝对路径。</summary>
    public static string NormalizeToFullPath(string path) => Path.GetFullPath(Normalize(path));

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
