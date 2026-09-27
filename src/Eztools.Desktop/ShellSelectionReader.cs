// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace Eztools.Desktop;

/// <summary>
/// 资源管理器"当前选中项"读取器 —— <c>input: shellSelection</c> 的宿主侧实现。
///
/// **为什么放在 Desktop 而不是 Eztools.Host**：读 Shell 视图是托盘/热键触发的伴随动作，
/// 与 <c>ReadClipboardSafely</c> 同级 —— 都是"触发时宿主替工具取上下文"。
/// 放进 Host 会让宿主库背上 Shell 互操作，而 CLI 根本用不到它。
///
/// **为什么不是特权原语**：读选中项不需要提权（Core 的定位是"特权操作"），语义不匹配。
///
/// **为什么走 IDispatch 自动化（dynamic）而不是手写 COM 接口**：
/// ① .NET SDK 的 MSBuild 不支持 &lt;COMReference&gt;（MSB4803），手写 vtable 错一槽即内存破坏；
/// ② 实测（_scratch/probe-shellcom.ps1）：Win11 标签页对象上
///    <c>QueryService(SID_SShellBrowser)</c> 返回 **E_NOTIMPL**（CLR 映射成
///    NotImplementedException），PowerToys peek 的 IShellBrowser 老路在此走不通；
/// ③ <c>Document.SelectedItems()</c> 直接给出完整文件系统路径，纯自动化即可。
///
/// **失败一律按"空选中项"处理，绝不抛**，但原因写进 <see cref="LastError"/> 由托盘落日志
/// —— 没有这个出口，"为什么速览拿不到文件"就只能靠猜。
/// </summary>
internal static class ShellSelectionReader
{
    /// <summary>最近一次返回空选中项的原因；成功时为 null。诊断出口，勿用于逻辑判断。</summary>
    internal static string? LastError { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(
        nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    /// <summary>以当前前台窗口为基准读取选中项（托盘/热键触发的人口）。</summary>
    public static IReadOnlyList<string> ReadForegroundSelection()
    {
        LastError = null;
        var hwnd = GetForegroundWindow();
        try
        {
            return hwnd != 0 ? ReadSelection(hwnd) : Bail("前台窗口为空（没有活动窗口）");
        }
        catch (Exception ex)
        {
            return Bail($"读 Shell 视图异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取"前台窗口对应的资源管理器视图"的选中项路径。
    ///
    /// 匹配方式：Shell.Windows() 里每个条目的 HWND 都是**顶层框架窗口**
    /// （桌面条目则是 SHELLDLL_DefView 子窗口），与解析出的目标视图窗口比对即可。
    /// Win11 多标签下多个条目共享同一 HWND —— 此时取**集合中最后一个**
    /// （Shell 按打开顺序登记，最近打开的标签排最后；这是启发式而非保证，
    /// 若将来出问题，需要另行解决"活动标签"判别 —— QueryService 路在 Win11 已实测
    /// 返回 E_NOTIMPL，走不通）。
    /// </summary>
    private static IReadOnlyList<string> ReadSelection(nint foregroundWindowHandle)
    {
        // 桌面（Progman/WorkerW）也是速览的合法来源：它的视图窗口是 SHELLDLL_DefView
        // 子窗口，且**本身就登记在 Shell.Windows() 里**（实测确认）——
        // 所以只需把匹配目标换成那个子窗口，后续流程与普通文件夹窗口完全一致。
        var targetView = foregroundWindowHandle;
        if (IsDesktopWindow(foregroundWindowHandle))
        {
            targetView = FindWindowEx(foregroundWindowHandle, 0, "SHELLDLL_DefView", null);
            if (targetView == 0)
            {
                return Bail("前台是桌面但找不到桌面视图窗口（SHELLDLL_DefView）");
            }
        }

        dynamic windows;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application")
                ?? throw new InvalidOperationException("Shell.Application 未注册（Windows 必有）");
            dynamic shell = Activator.CreateInstance(shellType)!;
            windows = shell.Windows();
        }
        catch (Exception ex)
        {
            return Bail($"创建 Shell.Application 失败：{ex.GetType().Name}: {ex.Message}");
        }

        int count;
        try
        {
            count = (int)windows.Count;
        }
        catch (Exception ex)
        {
            return Bail($"取 Shell 窗口数失败：{ex.GetType().Name}: {ex.Message}");
        }

        IReadOnlyList<string>? chosen = null;
        for (var i = 0; i < count; i++)
        {
            object windowObj;
            nint hwnd;
            dynamic document;
            try
            {
                windowObj = windows.Item(i);
                hwnd = (nint)((dynamic)windowObj).HWND;
                document = ((dynamic)windowObj).Document;
            }
            catch (Exception ex)
            {
                return Bail($"取第 {i} 个 Shell 窗口失败：{ex.GetType().Name}: {ex.Message}");
            }

            if (hwnd != targetView)
            {
                continue;
            }

            // Document 是 ShellFolderViewDual（IDispatch），SelectedItems() 给 FolderItems
            IReadOnlyList<string> paths;
            try
            {
                dynamic selected = document.SelectedItems();
                var n = (int)selected.Count;
                var list = new List<string>(n);
                for (var k = 0; k < n; k++)
                {
                    list.Add((string)selected.Item(k).Path);
                }

                paths = list;
            }
            catch (Exception ex)
            {
                return Bail($"读选中项失败：{ex.GetType().Name}: {ex.Message}");
            }

            chosen = paths;   // 多标签同 HWND 时取最后一个（见方法注释）
        }

        if (chosen is null)
        {
            return Bail($"前台窗口 0x{foregroundWindowHandle:X} 不是资源管理器视图");
        }

        if (chosen.Count == 0)
        {
            return Bail("视图里没有选中的项");
        }

        return chosen;
    }

    private static IReadOnlyList<string> Bail(string reason)
    {
        LastError = reason;
        return Array.Empty<string>();
    }

    private static bool IsDesktopWindow(nint windowHandle)
    {
        var className = new StringBuilder(256);
        if (GetClassName(windowHandle, className, className.Capacity) == 0)
        {
            return false;
        }

        var name = className.ToString();
        if (name != "Progman" && name != "WorkerW")
        {
            return false;
        }

        // 桌面判据的另一半：真桌面必有 SHELLDLL_DefView 子窗口
        return FindWindowEx(windowHandle, 0, "SHELLDLL_DefView", null) != 0;
    }
}
