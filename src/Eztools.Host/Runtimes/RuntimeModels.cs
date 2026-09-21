using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eztools.Host.Runtimes;

/// <summary>本机运行时标识（RID），用于匹配载荷文件名。</summary>
public static class NativeRid
{
    public static string Current { get; } = Build();

    public static bool IsWindows => OperatingSystem.IsWindows();

    private static string Build()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "unknown",
        };

        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";
        return $"{os}-{arch}";
    }
}

/// <summary>
/// 一个可部署的运行时载荷（磁盘上的 <c>payload/&lt;runtime&gt;-&lt;version&gt;-&lt;rid&gt;.tar.gz</c>）。
/// </summary>
public sealed class RuntimePayload
{
    public required string Runtime { get; init; }

    public required string Version { get; init; }

    public required string Rid { get; init; }

    public required string ArchivePath { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>尝试从文件名解析载荷元信息。</summary>
    public static bool TryParse(string archivePath, out RuntimePayload? payload, out string? error)
    {
        payload = null;
        error = null;

        var fileName = Path.GetFileName(archivePath);
        const string suffix = ".tar.gz";
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"载荷必须是 .tar.gz：{fileName}";
            return false;
        }

        var stem = fileName[..^suffix.Length];

        // 约定：<runtime>-<version>-<os>-<arch>，例如 python-3.13.14-win-x64
        var lastDash = stem.LastIndexOf('-');
        if (lastDash <= 0)
        {
            error = $"载荷文件名不符合 <runtime>-<version>-<os>-<arch> 约定：{fileName}";
            return false;
        }

        var arch = stem[(lastDash + 1)..];
        var head = stem[..lastDash];

        var osDash = head.LastIndexOf('-');
        if (osDash <= 0)
        {
            error = $"载荷文件名不符合 <runtime>-<version>-<os>-<arch> 约定：{fileName}";
            return false;
        }

        var os = head[(osDash + 1)..];
        var runtimeAndVersion = head[..osDash];

        var runtimeDash = runtimeAndVersion.IndexOf('-');
        if (runtimeDash <= 0)
        {
            error = $"载荷文件名缺少版本号：{fileName}";
            return false;
        }

        var runtime = runtimeAndVersion[..runtimeDash].ToLowerInvariant();
        var version = runtimeAndVersion[(runtimeDash + 1)..];

        if (!Contracts.SemVer.TryParse(version, out _))
        {
            error = $"载荷文件名中的版本号不合法：{version}";
            return false;
        }

        payload = new RuntimePayload
        {
            Runtime = runtime,
            Version = version,
            Rid = $"{os}-{arch}",
            ArchivePath = Path.GetFullPath(archivePath),
            SizeBytes = TryGetSize(archivePath),
        };
        return true;
    }

    private static long TryGetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    public override string ToString() => $"{Runtime} {Version} ({Rid}) ← {Path.GetFileName(ArchivePath)}";
}

/// <summary>已就绪的运行时（供工具进程启动使用）。</summary>
public sealed class RuntimeInstallation
{
    public required string Runtime { get; init; }

    public required string Version { get; init; }

    /// <summary>运行时根目录。</summary>
    public required string Root { get; init; }

    /// <summary>解释器绝对路径（python 为 python.exe，node 为 node.exe）。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>true = 宿主自带的隔离运行时；false = 开发期外部环境覆盖（不可交付）。</summary>
    public required bool IsEmbedded { get; init; }

    /// <summary>来源说明，用于 <c>ezt doctor</c> 展示。</summary>
    public required string Origin { get; init; }

    /// <summary>运行时的 site-packages 目录（SDK 植入点）；非 python 或无该目录时为 null。</summary>
    public string? SitePackagesDir
    {
        get
        {
            var dir = Path.Combine(Root, "Lib", "site-packages");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    public bool IsUsable => File.Exists(ExecutablePath);

    public override string ToString() =>
        $"{Runtime} {Version} @ {Root}{(IsEmbedded ? "" : "（外部覆盖）")}";
}

/// <summary>运行时安装标记，写在运行时目录内的 <c>.ezt-runtime.json</c>。</summary>
internal sealed class RuntimeMarker
{
    public const string FileName = ".ezt-runtime.json";

    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public string Runtime { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Rid { get; set; } = string.Empty;

    public string? PayloadFile { get; set; }

    public string? PayloadSha256 { get; set; }

    public string? InstalledAt { get; set; }

    public string? SdkVersion { get; set; }

    public int FileCount { get; set; }

    public static RuntimeMarker? Read(string runtimeRoot)
    {
        var path = Path.Combine(runtimeRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // 🔴 PropertyNameCaseInsensitive 必须开：本标记文件的键是 camelCase（"sdkVersion"），
            //    而 System.Text.Json 默认按大小写严格匹配，于是 SdkVersion 永远反序列化成 null，
            //    表现为"SDK 指纹每次都判定为变化 → 每次调用都重新植入 SDK"（实测每次 505ms）。
            //    这类"读得回但绑定不上"的问题不会报错，只会静默降级成最慢的路径。
            return JsonSerializer.Deserialize<RuntimeMarker>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    public void Write(string runtimeRoot)
    {
        var node = new JsonObject
        {
            ["schema"] = Schema,
            ["runtime"] = Runtime,
            ["version"] = Version,
            ["rid"] = Rid,
            ["payloadFile"] = PayloadFile,
            ["payloadSha256"] = PayloadSha256,
            ["installedAt"] = InstalledAt ?? DateTimeOffset.Now.ToString("O"),
            ["sdkVersion"] = SdkVersion,
            ["fileCount"] = FileCount,
        };

        File.WriteAllText(
            Path.Combine(runtimeRoot, FileName),
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
    }
}
