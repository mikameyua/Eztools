// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Windows;
using Eztools.ClipboardLib;
using Eztools.Host.Launcher;
using Eztools.Host.Search;
using Eztools.Ocr;

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
                    using (var process = Process.Start(
                        BuildLaunchStartInfo(action.Argument, action.Arguments)))
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

                case LauncherActionKind.OcrCopy:
                    // 提字要先异步跑 OCR、成功后再走**直贴链**（Hide → 还原前台 → Ctrl+V），
                    // 那两步都是 SearchWindow 的职责（它持有 _preSummonHwnd）。走到这里说明有调用方
                    // 绕过了那条链 —— 明示，而不是让 default 谎报"暂不支持"。
                    return "提字动作需要走直贴链路（调用方未接）";

                default:
                    return $"暂不支持的动作：{action.Kind}";
            }
        }
        catch (Exception ex)
        {
            return DescribeFailure(action, ex);
        }
    }

    /// <summary>
    /// 直贴的**后半程**（W10-b，设计方案 §3 R4 铁律）—— 调用方**必须先 Hide 本窗**再调这里，
    /// 否则注入的 Ctrl+V 会贴进启动器自己。本方法只负责"贴到哪"：
    /// 还原 <paramref name="targetHwnd"/> 到前台 → 等激活消息落地 → 注入 Ctrl+V。
    ///
    /// <para><b>内容上剪贴板是调用方的事</b>：本方法不碰剪贴板内容（拆开的理由与 W5 面板同款 ——
    /// "传给系统的到底是什么"可被断言，而"真的贴进了用户的编辑器"依赖桌面环境，属手工项）。</para>
    ///
    /// <para>返回 <c>null</c> = 成功；非 <c>null</c> = 可读失败文案（调用方上状态行）。</para>
    /// </summary>
    internal static string? ExecutePasteBack(nint targetHwnd)
    {
        if (targetHwnd == nint.Zero)
        {
            return "已复制（未能直贴：没有可还原的目标窗口）";
        }

        if (!PasteBack.RestoreForeground(targetHwnd))
        {
            // 目标窗口多半已销毁（用户 Alt+F4 关掉了复制来源）—— 如实返回，不假装贴成功
            return "已复制，但原窗口已关闭，请手动粘贴";
        }

        // 目标窗口处理激活消息（与 W5 面板同款 120ms：可见窗已隐藏，UI 线程短睡无感）
        Thread.Sleep(120);
        PasteBack.InjectCtrlV();
        return null;
    }

    /// <summary>
    /// 图片提字（W10-c）：读盘 → 系统 OCR → 返回文本。**同步**（内部等异步引擎）——
    /// 调用方是 UI 线程上一次 Enter，动作一次性且成功后立刻收窗，阻塞窗口极短
    /// （与 <see cref="ExecutePasteBack"/> 里 120 ms 的 <c>Thread.Sleep</c> 同一条实践）。
    ///
    /// <para><b>R1 纪律照搬 W5 面板</b>：语言包缺失 / 引擎建不起来 / 图里确实没字，
    /// 三种"提不出字"的原因**必须分开说** —— 绝不把"我没能力"讲成"你没内容"（S9 家族）。</para>
    ///
    /// <para>返回 <c>(null, 文案)</c> = 失败；<c>(文本, null)</c> = 成功。文本非空白由本方法保证。</para>
    /// </summary>
    internal static (string? Text, string? Error) RecognizeImageText(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            return (null, "提字失败：图片文件已不在（可能超过保留期被清掉）");
        }

        if (!OcrLanguages.IsAvailable)
        {
            return (null, "提字失败：本机没有可用的 OCR 语言包（`ezt ocr langs` 可查）");
        }

        try
        {
            var engine = WindowsOcrEngine.TryCreate(null);
            if (engine is null)
            {
                return (null, "提字失败：OCR 引擎创建失败（本机语言包不可用）");
            }

            using var bitmap = new Drawing.Bitmap(imagePath);
            var result = engine.RecognizeAsync(bitmap).GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(result.Text)
                ? (null, "提字完成，但这张图里没识别到文字")
                : (result.Text, null);
        }
        catch (Exception ex)
        {
            // 图片损坏 / 解码失败 / 引擎异常 —— 全部明示，不静默
            return (null, $"提字失败：{ex.Message}");
        }
    }

    /// <summary>打开动作的进程参数（FileName = 全路径，UI 不拼路径）。</summary>
    internal static ProcessStartInfo BuildOpenStartInfo(string path) =>
        new(path) { UseShellExecute = true };

    /// <summary>
    /// 启动动作的进程参数（带工作目录）。<paramref name="arguments"/> 非空时作为命令行参数下发
    /// （W10-a：系统命令要的是"可执行文件 + 参数"，如 <c>rundll32.exe user32.dll,LockWorkStation</c>；
    /// apps 的 Launch 不传 ⇒ 行为逐字不变）。
    /// </summary>
    internal static ProcessStartInfo BuildLaunchStartInfo(string path, string? arguments = null)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(arguments))
        {
            info.Arguments = arguments;
        }
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
            LauncherActionKind.OcrCopy => "提字",
            _ => "执行",
        };

        if (action.Kind is LauncherActionKind.CopyText)
        {
            return $"复制失败：{ex.Message}";
        }

        // 提字的 Argument 是图片路径，不是"用户想打开的目标" —— 别套用下面的"目标不存在"话术
        if (action.Kind is LauncherActionKind.OcrCopy)
        {
            return $"{verb}失败：{ex.Message}";
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
