// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Eztools.Desktop;

/// <summary>
/// 图标缓存（W7-b，设计方案 §10.6 D / §11.5）—— Shell 提取一次、按路径缓存，**LRU 上限 512**。
///
/// <para><b>★ 句柄纪律是本类的全部难点</b>：<c>SHGetFileInfo(SHGFI_ICON)</c> 每次调用产出一个
/// <c>HICON</c>，不销毁 = 每次渲染漏一个 GDI 句柄（安全专项 RI-4 同族）。所以
/// <c>DestroyIcon</c> 必须走 <b>try/finally</b>，且"连续 N 次提取后进程句柄斜率 = 0"是要断言的
/// （<c>--probe-launcher apps</c>）。</para>
///
/// <para><b>失败一律返回 null 且静默</b>：图标是**纯装饰** —— 它失败绝不能影响"能不能搜到 /
/// 能不能启动"。这是本类唯一允许静默的地方（其余一律不许静默）。</para>
///
/// <para><b>为什么自己声明 P/Invoke 而不复用 <c>Eztools.Core.NativeInterop</c></b>：后者是特权层
/// （G7 守卫 + NFR-2 禁止 Launcher 代码引用 <c>Eztools.Core</c>）。★ 这是相对设计方案 §14
/// 的一处**实施期调整**（原写"直接用 NativeInterop"，实际不可用）。</para>
/// </summary>
internal static class IconCache
{
    /// <summary>缓存上限（设计方案 R11：大型软件环境开始菜单条目可达数千，无上限会累积位图）。</summary>
    internal const int Capacity = 512;

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Entry> Order = new();   // 最近使用在表头

    private static int _hits;
    private static int _misses;

    private sealed record Entry(string Path, BitmapSource Icon);

    /// <summary>当前缓存条目数（探针断言 LRU 上限）。</summary>
    internal static int CachedCount
    {
        get { lock (Gate) { return Map.Count; } }
    }

    /// <summary>缓存命中数（探针断言"第二行真的走了缓存"）。</summary>
    internal static int Hits
    {
        get { lock (Gate) { return _hits; } }
    }

    /// <summary>缓存未命中数（= 真实提取次数）。</summary>
    internal static int Misses
    {
        get { lock (Gate) { return _misses; } }
    }

    /// <summary>清空缓存（探针在跑句柄斜率前先清，避免命中把"真提取"盖掉）。</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Map.Clear();
            Order.Clear();
            _hits = 0;
            _misses = 0;
        }
    }

    /// <summary>取图标（null = 取不到，调用方不显示图标即可）。线程安全。</summary>
    internal static BitmapSource? GetIcon(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Map.TryGetValue(path, out var hit))
            {
                Order.Remove(hit);
                Order.AddFirst(hit);
                _hits++;
                return hit.Value.Icon;
            }
        }

        // 提取放在锁外：两个线程同时提取同一路径只会"白做一次"，而锁内做 I/O 会拖住渲染
        var icon = Extract(path);
        lock (Gate)
        {
            _misses++;
            if (icon is null)
            {
                return null;
            }

            if (Map.TryGetValue(path, out var raced))
            {
                return raced.Value.Icon;   // 竞态里别人先放进去 ⇒ 用它的
            }

            var node = Order.AddFirst(new Entry(path, icon));
            Map[path] = node;
            while (Map.Count > Capacity && Order.Last is { } oldest)
            {
                Order.RemoveLast();
                Map.Remove(oldest.Value.Path);
            }

            return icon;
        }
    }

    private static BitmapSource? Extract(string path)
    {
        var info = default(SHFILEINFO);
        var result = SHGetFileInfo(
            path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Freeze：跨线程安全（渲染可能在任何线程读到它）+ 允许 WPF 缓存位图
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;   // 图标失败 = 纯装饰降级，绝不拖垮功能
        }
        finally
        {
            _ = DestroyIcon(info.hIcon);   // ★ 必须在 finally：漏一个 = 一次渲染漏一个 GDI 句柄
        }
    }

    // ── 进程句柄读数（斜率自测用）──────────────────────────────────────────
    //
    // 自带声明而非复用 Eztools.Core 的版本：Launcher 代码引用特权层被 G7 守卫 + NFR-2 禁止。

    /// <summary>当前进程句柄数（读不到返回 0 —— 调用方按"斜率不可判"处理，不假装是 0 泄漏）。</summary>
    internal static int ProcessHandleCount() =>
        GetProcessHandleCount(GetCurrentProcess(), out var count) ? (int)count : 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
}
