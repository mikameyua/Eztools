namespace Eztools.Host;

/// <summary>
/// 位置解析：把"仓库开发布局"与"部署布局"统一到一套查找逻辑上。
///
/// 开发时宿主跑在 <c>src\Eztools.Cli\bin\Debug\net10.0\</c>，工具与 SDK 在仓库根；
/// 部署时宿主跑在 <c>%LOCALAPPDATA%\Eztools\bin\</c>，工具与 SDK 就在同一安装根下。
/// 两种布局都必须能找到，否则"开发跑的"和"发布出去的"行为不一致——那是最糟的情况。
/// </summary>
public static class AppLayout
{
    /// <summary>仓库根标记：存在其中之一即认为是仓库根。</summary>
    private static readonly string[] RootMarkers = { "Eztools.sln", "Directory.Build.props" };

    /// <summary>从程序集位置向上寻找仓库根（可能找不到）。</summary>
    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var marker in RootMarkers)
            {
                if (File.Exists(Path.Combine(dir.FullName, marker)))
                {
                    return dir.FullName;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>仓库相对候选目录（由近及远）。</summary>
    public static IEnumerable<string> RepoCandidates()
    {
        var root = FindRepoRoot();
        if (root is not null)
        {
            yield return root;
        }
    }

    public static void AddRepoRelative(ICollection<string> target, string relative)
    {
        foreach (var root in RepoCandidates())
        {
            target.Add(Path.Combine(root, relative));
        }
    }

    /// <summary>
    /// 解析内置工具目录（部署形态）。
    ///
    /// ⚠️ 这里有一个实测踩到的陷阱：宿主首次运行会在安装根下**创建空的 <c>tools\</c> 目录**，
    /// 如果只判断"目录是否存在"，这个空目录会遮蔽仓库里的真实工具源，导致开发时一个工具都发现不到。
    /// 所以判据必须是"目录里有清单"，而不是"目录存在"。
    /// </summary>
    public static string ResolveBuiltinToolsDir(EztoolsPaths paths)
    {
        if (HasToolManifests(paths.BuiltinToolsDir))
        {
            return paths.BuiltinToolsDir;
        }

        foreach (var root in RepoCandidates())
        {
            var candidate = Path.Combine(root, "tools");
            if (HasToolManifests(candidate))
            {
                return candidate;
            }
        }

        return paths.BuiltinToolsDir;
    }

    /// <summary>
    /// 全部的"内置性质"工具目录源：安装根优先，仓库其次，两者都有内容时都返回。
    ///
    /// 三种形态都要正确：
    /// <list type="bullet">
    /// <item><b>部署</b>：只有安装根的 <c>tools\</c> 有内容 → 用它</item>
    /// <item><b>开发</b>：安装根的 <c>tools\</c> 是刚建出来的空目录 → 用仓库的</item>
    /// <item><b>混合</b>：两边都有 → 都扫，先出现的源在同版本冲突时胜出</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<(string Name, string Path)> ResolveToolDirectories(EztoolsPaths paths)
    {
        var result = new List<(string Name, string Path)>();

        if (HasToolManifests(paths.BuiltinToolsDir))
        {
            result.Add(("builtin", paths.BuiltinToolsDir));
        }

        foreach (var root in RepoCandidates())
        {
            var candidate = Path.Combine(root, "tools");
            if (!HasToolManifests(candidate))
            {
                continue;
            }

            if (result.Any(existing => PathsEqual(existing.Path, candidate)))
            {
                continue;
            }

            result.Add(("repo", candidate));
        }

        if (result.Count == 0)
        {
            // 保底：返回预期位置，让体检能明确显示"这里没东西"，而不是静默发现 0 个工具
            result.Add(("builtin", paths.BuiltinToolsDir));
        }

        return result;
    }

    /// <summary>目录里是否至少有一个工具清单（判定"这个源有内容"的唯一依据）。</summary>
    public static bool HasToolManifests(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(directory, ManifestFileName, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    public const string ManifestFileName = "tool.json";

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>解析 SDK 目录（含可导入的 eztools 包）。同样以"有内容"为判据。</summary>
    public static string ResolveSdkDir(EztoolsPaths paths)
    {
        if (HasSdkPackage(paths.SdkDir))
        {
            return paths.SdkDir;
        }

        foreach (var root in RepoCandidates())
        {
            var candidate = Path.Combine(root, "sdk");
            if (HasSdkPackage(candidate))
            {
                return candidate;
            }
        }

        return paths.SdkDir;
    }

    /// <summary>SDK 目录下是否存在可导入的 eztools 包。</summary>
    public static bool HasSdkPackage(string sdkDir) =>
        File.Exists(Path.Combine(sdkDir, "python", "eztools", "__init__.py"));

    /// <summary>宿主可执行文件所在目录。</summary>
    public static string HostDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>工具源覆盖环境变量：分号分隔的多目录。</summary>
    public const string EnvToolsDirs = "EZTOOLS_TOOLS_DIR";
}
