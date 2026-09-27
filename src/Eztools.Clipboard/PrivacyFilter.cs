// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.ClipboardLib;

/// <summary>隐私评估结论（FR-11）。Blocked / Paused 都是"不入库"，但要能区分（审计需要）。</summary>
public enum PrivacyDecision
{
    /// <summary>放行：正常捕获入库。</summary>
    Allow = 0,

    /// <summary>黑名单命中（密码管理器等）：静默丢弃，Debug 日志可查。</summary>
    Blocked = 1,

    /// <summary>手动暂停捕获中：丢弃（托盘 toggle / 探针开关）。</summary>
    Paused = 2,
}

/// <summary>
/// 隐私排除（W5-剪贴板-设计方案.md FR-11）：应用黑名单 + 临时暂停。
///
/// <para>黑名单匹配"进程名"（如 <c>1password.exe</c>），大小写不敏感、
/// <c>.exe</c> 后缀可写可不写；临时模式是运行时开关，不落盘（W5-c 接托盘）。</para>
/// </summary>
public sealed class PrivacyFilter
{
    private readonly HashSet<string> _blacklist = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>手动临时隐私模式：开启后停止捕获（不删已有历史）。</summary>
    public bool Paused { get; set; }

    /// <summary>替换整个黑名单（空输入 = 清空）。接受 ["1password.exe", "Bitwarden"] 混合形态。</summary>
    public void SetBlacklist(IEnumerable<string>? processNames)
    {
        lock (_gate)
        {
            _blacklist.Clear();
            if (processNames is null)
            {
                return;
            }

            foreach (var raw in processNames)
            {
                var name = Normalize(raw);
                if (name.Length > 0)
                {
                    _blacklist.Add(name);
                }
            }
        }
    }

    public void AddToBlacklist(string processName)
    {
        var name = Normalize(processName);
        if (name.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _blacklist.Add(name);
        }
    }

    public IReadOnlyCollection<string> BlacklistSnapshot()
    {
        lock (_gate)
        {
            return _blacklist.ToArray();
        }
    }

    public PrivacyDecision Evaluate(string? sourceApp)
    {
        if (Paused)
        {
            return PrivacyDecision.Paused;
        }

        if (sourceApp is null)
        {
            // 拿不到 owner（系统进程/剪贴板无主）不等于违规——放行。
            // "读不到内容"与"不知道来源"是两回事，后者不该触发隐私拦截。
            return PrivacyDecision.Allow;
        }

        lock (_gate)
        {
            return _blacklist.Contains(Normalize(sourceApp))
                ? PrivacyDecision.Blocked
                : PrivacyDecision.Allow;
        }
    }

    private static string Normalize(string raw) =>
        raw.Trim().TrimEnd('"').Replace("/", "\\") is { Length: > 0 } trimmed
            ? (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + ".exe")
                .Split('\\')[^1]
            : "";
}
