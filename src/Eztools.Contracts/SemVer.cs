namespace Eztools.Contracts;

/// <summary>
/// 最小 semver 版本与范围实现。
/// 只需要覆盖 `tool.json` 里 `runtimeVersion` 的常见写法：`>=3.11 &lt;4.0`、`^3.11`、`3.13.14`、`*`。
/// 刻意不引第三方包——契约层保持零依赖。
/// </summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public SemVer(int major, int minor = 0, int patch = 0, string? preRelease = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string? PreRelease { get; }

    public static bool TryParse(string? raw, out SemVer version)
    {
        version = new SemVer(0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        string? pre = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var parts = text.Split('.');
        if (parts.Length is 0 or > 4)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length && i < 3; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
            {
                return false;
            }
        }

        version = new SemVer(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // 有预发布标记的版本小于同号正式版
        var thisPre = PreRelease is null;
        var otherPre = other.PreRelease is null;
        if (thisPre == otherPre) return 0;
        return thisPre ? 1 : -1;
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

/// <summary>
/// 版本范围。支持：<c>*</c> / 空、精确版本、<c>&gt;=X</c> <c>&gt;X</c> <c>&lt;=X</c> <c>&lt;X</c> <c>=X</c>（空格分隔为 AND）、
/// <c>^X.Y</c>（同主版本）、<c>~X.Y</c>（同主次版本）。
/// </summary>
public sealed class VersionRange
{
    private readonly List<Func<SemVer, bool>> _predicates = new();

    private VersionRange(string raw)
    {
        Raw = raw;
    }

    public string Raw { get; }

    public static VersionRange Any { get; } = new("*");

    /// <summary>解析范围；无法识别时返回 Any 并把原因写进 <paramref name="error"/>。</summary>
    public static VersionRange Parse(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "*")
        {
            return Any;
        }

        var range = new VersionRange(raw.Trim());
        var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (!range.AddToken(token))
            {
                error = $"无法解析的版本约束片段: '{token}'";
                return Any;
            }
        }

        return range;
    }

    private bool AddToken(string token)
    {
        if (token.StartsWith('^'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            var upper = new SemVer(v.Major + 1, 0, 0);
            _predicates.Add(x => v.CompareTo(x) <= 0 && x.CompareTo(upper) < 0);
            return true;
        }

        if (token.StartsWith('~'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            var upper = new SemVer(v.Major, v.Minor + 1, 0);
            _predicates.Add(x => v.CompareTo(x) <= 0 && x.CompareTo(upper) < 0);
            return true;
        }

        if (token.StartsWith(">="))
        {
            if (!SemVer.TryParse(token[2..], out var v)) return false;
            _predicates.Add(x => x.CompareTo(v) >= 0);
            return true;
        }

        if (token.StartsWith("<="))
        {
            if (!SemVer.TryParse(token[2..], out var v)) return false;
            _predicates.Add(x => x.CompareTo(v) <= 0);
            return true;
        }

        if (token.StartsWith('>'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            _predicates.Add(x => x.CompareTo(v) > 0);
            return true;
        }

        if (token.StartsWith('<'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            _predicates.Add(x => x.CompareTo(v) < 0);
            return true;
        }

        var text = token.StartsWith('=') ? token[1..] : token;
        if (!SemVer.TryParse(text, out var exact)) return false;

        // 精确到主版本（如 "3"）时按前缀匹配，符合直觉
        var dotCount = text.Count(c => c == '.');
        _predicates.Add(dotCount switch
        {
            0 => x => x.Major == exact.Major,
            1 => x => x.Major == exact.Major && x.Minor == exact.Minor,
            _ => x => x.Equals(exact),
        });
        return true;
    }

    public bool IsSatisfied(SemVer version) => _predicates.TrueForAll(p => p(version));

    public bool IsSatisfied(string? versionText) =>
        SemVer.TryParse(versionText, out var v) && IsSatisfied(v);

    public override string ToString() => Raw;
}
