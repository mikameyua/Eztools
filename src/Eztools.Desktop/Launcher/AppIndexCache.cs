// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Desktop;

/// <summary>应用条目的来源（诊断与断言面：探针要能证明"这条来自我注入的临时根"）。</summary>
internal enum AppSource
{
    UserStartMenu,
    CommonStartMenu,
    UserDesktop,
    CommonDesktop,
    AppPaths,
}

/// <summary>一条应用条目。<see cref="TargetPath"/> 对快捷方式 = **.lnk/.url 自身的全路径**（不解析目标）。</summary>
internal sealed record AppEntry(string Title, string TargetPath, AppSource Source);

/// <summary>一个扫描根。</summary>
internal sealed record AppScanRoot(string Directory, bool Recursive, AppSource Source);

/// <summary>
/// 应用索引缓存（W7-b，设计方案 §10.6 A/B）—— **进程级 + 内存 + 指纹失效**（D8=A）。
///
/// <para><b>为什么用内存而非落盘缓存</b>：一次全扫在真机是数十毫秒级且发生在后台线程，
/// 用户感知不到；落盘缓存要付"缓存与真实不一致"这一类最难查的 bug，收益不成比例（D8 论证）。</para>
///
/// <para><b>★ 三个状态必须分清（否则用户看到的是"没装应用"）</b>：
/// <list type="bullet">
/// <item><b>扫描中</b>（<see cref="Ready"/>=false）：provider **静默缺席** —— 这是"还没好"，不是"没有"；</item>
/// <item><b>就绪</b>（Ready=true 且无错）：正常参与，空结果就是空结果；</item>
/// <item><b>读不到</b>（Ready=true + <see cref="LastError"/> 非空）：**必须可见**
///   （状态行报"应用索引不可用：…"）。★ 注意这里**不能**用 Ready=false 表达"坏了" ——
///   那样 router 会按"静默缺席"处理（<c>ILauncherProvider</c> 契约 C5），故障就永远没人看见。</item>
/// </list></para>
///
/// <para><b>重扫期间保留旧快照</b>：绝不用空列表冒充"没装应用"（空结果与"正在换血"是两回事）。</para>
/// </summary>
internal sealed class AppIndexCache
{
    /// <summary>兜底重扫间隔：App Paths 在注册表里，没有轻量变更通知（§10.6 B）。</summary>
    internal const int RescanIntervalMs = 10 * 60 * 1000;

    private readonly object _gate = new();
    private readonly IReadOnlyList<AppScanRoot> _roots;
    private readonly bool _includeAppPaths;
    private readonly Func<long> _nowMs;

    private IReadOnlyList<AppEntry> _items = Array.Empty<AppEntry>();
    private string _fingerprint = "";
    private long _lastScanMs = long.MinValue;
    private bool _ready;
    private bool _scanning;
    private string? _lastError;
    private int _scanCount;

    /// <param name="roots">扫描根（探针注入临时目录即可做确定性断言）。</param>
    /// <param name="includeAppPaths">是否扫注册表 App Paths（探针关掉 —— 注册表没法注入）。</param>
    /// <param name="nowMs">可注入时钟（selftest 假时钟）。</param>
    internal AppIndexCache(
        IReadOnlyList<AppScanRoot> roots,
        bool includeAppPaths = true,
        Func<long>? nowMs = null)
    {
        _roots = roots;
        _includeAppPaths = includeAppPaths;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>扫描完成且可用（探针/断言面）。</summary>
    internal bool Ready
    {
        get { lock (_gate) { return _ready; } }
    }

    /// <summary>当前快照（重扫期间是旧快照，见类注）。</summary>
    internal IReadOnlyList<AppEntry> Items
    {
        get { lock (_gate) { return _items; } }
    }

    /// <summary>读不到的原因（非空 ⇒ 必须对用户可见）。</summary>
    internal string? LastError
    {
        get { lock (_gate) { return _lastError; } }
    }

    /// <summary>已完成的扫描次数（探针断言"重扫真的发生了"）。</summary>
    internal int ScanCount
    {
        get { lock (_gate) { return _scanCount; } }
    }

    /// <summary>首扫就绪通知（窗口据此补发一次查询，见设计方案 R10）。</summary>
    internal event Action? BecameReady;

    /// <summary>启动首扫（幂等）。<b>由装配方在构造后调用</b> —— 不能等 <c>QueryAsync</c> 触发：
    /// 未就绪时 router 根本不会调 provider（IsReady 闸），那样扫描永远起不来（死锁）。</summary>
    internal void EnsureScanStarted()
    {
        lock (_gate)
        {
            if (_ready || _scanning)
            {
                return;
            }

            _scanning = true;
        }

        StartBackgroundScan();
    }

    /// <summary>
    /// 失效检测（唤出时调用）：指纹变了或超过兜底间隔 ⇒ 后台重扫。指纹 = 各根的
    /// (LastWriteTimeUtc, 条目数) 拼接 —— 装/卸应用会新建或删除开始菜单里的目录/文件，
    /// 必然带动父目录 mtime（深层嵌套改动不被感知是刻意的：成本与收益在这里不值）。
    /// </summary>
    internal void InvalidateIfChanged()
    {
        bool due;
        lock (_gate)
        {
            if (_scanning)
            {
                return;
            }

            // ★ `_lastScanMs == long.MinValue` 是显式分支而非常量参与减法：直接算
            //   `now - long.MinValue` 在 unchecked 下会溢出成负数 ⇒ 兜底重扫被静默跳过
            //   （首扫之前的那段窗口恰好落在"已开始但未完成"上，后果隐性但没必要留）。
            due = _lastScanMs == long.MinValue || _nowMs() - _lastScanMs >= RescanIntervalMs;
            if (due)
            {
                _scanning = true;
            }
        }

        if (due)
        {
            StartBackgroundScan();   // 兜底重扫（App Paths 无轻量变更通知）
            return;
        }

        string fingerprint;
        try
        {
            fingerprint = ComputeFingerprint();
        }
        catch (Exception)
        {
            return;   // 指纹算不出来（权限/竞态）⇒ 不因它触发重扫，等兜底间隔
        }

        lock (_gate)
        {
            if (_scanning || fingerprint == _fingerprint)
            {
                return;
            }

            _scanning = true;
        }

        StartBackgroundScan();
    }

    private void StartBackgroundScan() =>
        _ = Task.Run(() =>
        {
            try
            {
                ScanCore();
            }
            catch (Exception ex)
            {
                SetFailed(ex.Message);
            }
        });

    private void ScanCore()
    {
        string fingerprint;
        try
        {
            fingerprint = ComputeFingerprint();
        }
        catch (Exception)
        {
            fingerprint = "";
        }

        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var root in _roots)
        {
            try
            {
                if (!Directory.Exists(root.Directory))
                {
                    failures.Add($"{root.Source}:目录不存在");
                    continue;
                }

                var option = root.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(root.Directory, "*", option))
                {
                    var ext = Path.GetExtension(file);
                    if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;   // 只收快捷方式（desktop.ini / Thumbs.db 天然被扩展名挡掉）
                    }

                    var title = Path.GetFileNameWithoutExtension(file);
                    if (IsNoise(title))
                    {
                        continue;
                    }

                    if (seen.Add(file))
                    {
                        list.Add(new AppEntry(title, file, root.Source));
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{root.Source}:{ex.Message}");
            }
        }

        if (_includeAppPaths)
        {
            ScanAppPaths(list, seen, failures);
        }

        // 全部根都失败 ⇒ 这不是"没有应用"，是"读不到" ⇒ 走可见的失败态（B-2）
        if (list.Count == 0 && _roots.Count > 0 && failures.Count == _roots.Count)
        {
            SetFailed(string.Join("；", failures));
            return;
        }

        var ordered = list
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            _items = ordered;
            _fingerprint = fingerprint;
            _lastScanMs = _nowMs();
            _lastError = null;
            _ready = true;
            _scanning = false;
            _scanCount++;
        }

        BecameReady?.Invoke();
    }

    private void SetFailed(string error)
    {
        lock (_gate)
        {
            _lastError = error;
            _ready = true;      // ★ 必须就绪：否则 router 静默缺席，故障永不可见（类注）
            _scanning = false;
            _scanCount++;
        }

        BecameReady?.Invoke();
    }

    /// <summary>注册表 App Paths（HKLM + HKCU）。子键名 = exe 名，默认值 = exe 全路径。</summary>
    private static void ScanAppPaths(List<AppEntry> list, HashSet<string> seen, List<string> failures)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetSubKeyNames())
                {
                    using var entryKey = key.OpenSubKey(name);
                    if (entryKey?.GetValue(null) is not string path || path.Length == 0)
                    {
                        continue;
                    }

                    path = path.Trim('"', ' ');
                    if (path.Length == 0 || !seen.Add(path))
                    {
                        continue;
                    }

                    var title = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? name[..^4]
                        : name;
                    if (IsNoise(title))
                    {
                        continue;
                    }

                    list.Add(new AppEntry(title, path, AppSource.AppPaths));
                }
            }
            catch (Exception ex)
            {
                // 单个 hive 读不到（权限）不妨碍另一个 —— 但**必须留痕**（否则"少了半批应用"无从解释）
                failures.Add($"AppPaths({hive.Name}):{ex.Message}");
            }
        }
    }

    /// <summary>噪声过滤：开始菜单里真实存在的"卸载"项不算可启动应用。</summary>
    private static bool IsNoise(string title) =>
        title.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)
        || title.StartsWith("卸载", StringComparison.OrdinalIgnoreCase)
        || title.StartsWith("Remove ", StringComparison.OrdinalIgnoreCase);

    private string ComputeFingerprint()
    {
        var parts = new List<string>(_roots.Count);
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root.Directory))
            {
                parts.Add($"{root.Source}=x");
                continue;
            }

            var option = root.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var count = Directory.EnumerateFileSystemEntries(root.Directory, "*", option).Count();
            parts.Add($"{root.Source}={Directory.GetLastWriteTimeUtc(root.Directory).Ticks}:{count}");
        }

        return string.Join('|', parts);
    }

    /// <summary>生产扫描根（S1~S4；S5 由 <c>_includeAppPaths</c> 控制）。</summary>
    internal static IReadOnlyList<AppScanRoot> DefaultRoots()
    {
        var roots = new List<AppScanRoot>(4);
        AddRoot(roots, Environment.SpecialFolder.StartMenu, "Programs", recursive: true, AppSource.UserStartMenu);
        AddRoot(roots, Environment.SpecialFolder.CommonStartMenu, "Programs", recursive: true, AppSource.CommonStartMenu);
        AddRoot(roots, Environment.SpecialFolder.Desktop, sub: null, recursive: false, AppSource.UserDesktop);
        AddRoot(roots, Environment.SpecialFolder.CommonDesktopDirectory, sub: null, recursive: false, AppSource.CommonDesktop);
        return roots;
    }

    private static void AddRoot(
        List<AppScanRoot> roots,
        Environment.SpecialFolder folder,
        string? sub,
        bool recursive,
        AppSource source)
    {
        var dir = Environment.GetFolderPath(folder);
        if (dir.Length == 0)
        {
            return;
        }

        if (sub is not null)
        {
            dir = Path.Combine(dir, sub);
        }

        roots.Add(new AppScanRoot(dir, recursive, source));
    }
}
