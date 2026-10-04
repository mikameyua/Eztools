// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Host.Launcher;

/// <summary>
/// 一条系统命令（W10-a）。<paramref name="LaunchExe"/> / <paramref name="LaunchArgs"/>
/// 直接落进 <see cref="LauncherActionKind.Launch"/> 动作的两个参数位，复用既有动作执行链。
/// </summary>
public sealed record CommandEntry(
    string Name,
    IReadOnlyList<string> Aliases,
    string Description,
    bool Destructive,
    string LaunchExe,
    string LaunchArgs);

/// <summary>
/// 系统命令表（W10-a，设计方案 §3 D6）—— 纯函数 + 白名单，`ezt selftest` 穷举，零进程。
///
/// <para><b>★ 触发用 "&gt;" 前缀，不用裸词</b>：应用段里 "cmd" 会命中"命令提示符"，
/// 任何裸词触发都必然与既有来源歧义。前缀还有一个好处：把"系统命令"与"内容检索"在语义上
/// 彻底分开（用户打 "&gt;" 时意图明确）。</para>
///
/// <para><b>★ 只收低危命令</b>：关机 / 重启 / 注销**不进表** —— 它们的代价是"误触即丢工作"，
/// 而启动器是"打了字、按了 Enter"的高频入口。清空回收站保留，但标
/// <see cref="CommandEntry.Destructive"/> ⇒ 标题带「（不可恢复）」警示（R6）。</para>
/// </summary>
public static class CommandCatalog
{
    /// <summary>触发前缀（"&gt;" 单独输入 = 列出全部命令，发现性）。</summary>
    public const string Prefix = ">";

    /// <summary>
    /// ASCII 等价前缀（W10-c，设计方案 §4.2 / R7 的备选落地）：中文输入法下 "&gt;" 要 Shift 切到半角，
    /// 部分 IME 会把它吞掉 —— 给一个**纯 ASCII、零切换**的同义入口。语义与 <see cref="Prefix"/>
    /// 逐字相同（同样支持"单独输入 = 列出全部"）。
    ///
    /// <para><b>★ 必须带冒号</b>：裸 <c>cmd</c> 会被当成普通检索词，把"命令提示符"这类应用结果
    /// 全挤掉；冒号让它成为一个**用户不会误打**的显式命名空间（与 <c>&gt;</c> 同族）。</para>
    /// </summary>
    public const string AsciiPrefix = "cmd:";

    /// <summary>命令白名单（顺序 = "&gt;" 单独输入时的展示顺序）。</summary>
    public static IReadOnlyList<CommandEntry> Entries { get; } =
    [
        new("锁屏", ["lock"],
            "锁定工作站（等同 Win+L）", false,
            "rundll32.exe", "user32.dll,LockWorkStation"),

        new("休眠", ["sleep", "hibernate"],
            "进入休眠（保留现场，唤醒后继续）", false,
            "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),

        new("清空回收站", ["emptytrash", "recyclebin"],
            "永久删除回收站里的全部内容", true,
            "powershell.exe", "-NoProfile -Command \"Clear-RecycleBin -Force\""),
    ];

    /// <summary>
    /// 输入是否命中触发前缀（<c>&gt;</c> 或 <c>cmd:</c>）。<paramref name="body"/> = 前缀之后、
    /// trim 过的正文（可能为空 —— 空正文 = 列出全部命令）。
    /// </summary>
    public static bool TryGetBody(string? text, out string body)
    {
        body = "";
        var s = (text ?? "").TrimStart();
        if (s.StartsWith(Prefix, StringComparison.Ordinal))
        {
            body = s[Prefix.Length..].Trim();
            return true;
        }

        // W10-c：ASCII 等价前缀（大小写不敏感 —— 用户可能打 CMD:）。
        if (s.StartsWith(AsciiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            body = s[AsciiPrefix.Length..].Trim();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 匹配命令表：<paramref name="body"/> 为空 ⇒ 全部（守表序）；否则取
    /// <see cref="CommandEntry.Name"/> 或别名**以 body 开头**（不区分大小写）的条目。
    /// 前缀语义覆盖"精确" —— 打全名自然命中。
    /// </summary>
    public static IReadOnlyList<CommandEntry> Match(string body)
    {
        if (body.Length == 0)
        {
            return Entries;
        }

        var hits = new List<CommandEntry>();
        foreach (var entry in Entries)
        {
            if (entry.Name.StartsWith(body, StringComparison.OrdinalIgnoreCase)
                || entry.Aliases.Any(a => a.StartsWith(body, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(entry);
            }
        }

        return hits;
    }
}
