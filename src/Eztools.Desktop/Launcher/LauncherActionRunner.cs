// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Windows;
using Eztools.Host.Launcher;
using Eztools.Host.Search;

namespace Eztools.Desktop;

/// <summary>
/// 动作执行器（W7-b，设计方案 §10.6 E / §4.2）—— 把 <see cref="LauncherAction"/> 变成真实动作，
/// 并把"失败原因"变成**可读文案**还给调用方。
///
/// <para><b>为什么把动作实现集中在这里</b>：文件的"打开/定位"原本住在 <c>SearchWindow</c>（W3-d-3
/// 抽出纯函数供探针断言）。apps 引入后动作变成五种，再散在窗口里就会变成一坨 switch；
/// 而窗口只该关心"成功收窗 / 失败写状态行"。所以实现搬到这里，
/// <c>SearchWindow</c> 保留三个 <see cref="SearchHitDto"/> 重载**委托**过来
/// —— 探针断言面（`actions` 断言块）因此零迁移。</para>
///
/// <para><b>★ 两条既有血案必须带着走</b>：
/// <list type="number">
/// <item><c>explorer /select</c> **只认反斜杠**（正斜杠是静默 no-op，2026-09-24 实锤）；</item>
/// <item>引号**只包路径、不包 <c>/select</c>**（2026-10-01 实测：整体加引号 ⇒ Explorer 只开默认窗口不定位）。</item>
/// </list></para>
/// </summary>
internal static class LauncherActionRunner
{
    /// <summary>
    /// 执行动作。返回 <c>null</c> = 成功；非 null = **失败文案**（调用方写在状态行）。
    /// <paramref name="action"/> 为 null = 该结果不可执行（调用方给"该结果不可执行"）。
    /// </summary>
    internal static string? Execute(LauncherAction? action)
    {
        if (action is null)
        {
            return null;   // 调用方按 null 单独处理，不在这里编文案
        }

        try
        {
            switch (action.Kind)
            {
                case LauncherActionKind.Open:
                    using (var process = Process.Start(BuildOpenStartInfo(action.Argument)))
                    {
                        _ = process;
                    }

                    return null;

                case LauncherActionKind.Launch:
                    // 工作目录 = 目标所在目录：有些应用（便携版/相对资源）靠 cwd 找自身文件
                    using (var process = Process.Start(BuildLaunchStartInfo(action.Argument)))
                    {
                        _ = process;
                    }

                    return null;

                case LauncherActionKind.Reveal:
                case LauncherActionKind.RevealApp:
                    using (var process = Process.Start(BuildRevealStartInfo(action.Argument)))
                    {
                        _ = process;
                    }

                    return null;

                case LauncherActionKind.CopyText:
                    Clipboard.SetText(action.Argument);
                    return null;

                default:
                    return $"暂不支持的动作：{action.Kind}";
            }
        }
        catch (Exception ex)
        {
            return DescribeFailure(action, ex);
        }
    }

    /// <summary>打开动作的进程参数（FileName = 全路径，UI 不拼路径）。</summary>
    internal static ProcessStartInfo BuildOpenStartInfo(string path) =>
        new(path) { UseShellExecute = true };

    /// <summary>启动动作的进程参数（带工作目录）。</summary>
    internal static ProcessStartInfo BuildLaunchStartInfo(string path)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = true };
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                info.WorkingDirectory = dir;
            }
        }
        catch (Exception)
        {
            // review-guards:allow-empty-catch :: 目录路径算不出来（根路径 / UNC / 非法字符）⇒
            // 不设 cwd 也能启动，属**可预期降级**而非故障；为它写日志只会刷屏
        }

        return info;
    }

    /// <summary>
    /// 资源管理器定位的进程参数。两条血案见类注：反斜杠归一 + 引号只包路径。
    /// </summary>
    internal static ProcessStartInfo BuildRevealStartInfo(string path) =>
        new("explorer.exe", $"/select,\"{path.Replace('/', '\\')}\"");

    /// <summary>
    /// 失败文案（**区分原因**：只断"出了错"不够 —— §30.6）。
    /// 目标不存在（索引/快捷键已失效）与其它异常给不同文案，且都带上路径。
    /// </summary>
    internal static string DescribeFailure(LauncherAction action, Exception ex)
    {
        var verb = action.Kind switch
        {
            LauncherActionKind.Open => "打开",
            LauncherActionKind.Launch => "启动",
            LauncherActionKind.Reveal or LauncherActionKind.RevealApp => "定位",
            LauncherActionKind.CopyText => "复制",
            _ => "执行",
        };

        if (action.Kind is LauncherActionKind.CopyText)
        {
            return $"复制失败：{ex.Message}";
        }

        return File.Exists(action.Argument) || Directory.Exists(action.Argument)
            ? $"{verb}失败：{ex.Message}"
            : $"{verb}失败：目标不存在（索引或快捷方式可能已失效）—— {action.Argument}";
    }

    /// <summary>
    /// 打开失败文案（<see cref="SearchHitDto"/> 形态 —— W3-d-3 的探针断言面，签名与文案逐字保留）。
    /// </summary>
    internal static string DescribeOpenFailure(SearchHitDto hit, Exception ex) =>
        File.Exists(hit.Path)
            ? $"打开失败：{ex.Message}"
            : $"打开失败：文件不存在（索引尚未同步到删除？）—— {hit.Path}";
}
