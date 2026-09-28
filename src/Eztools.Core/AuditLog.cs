// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json.Nodes;

namespace Eztools.Core;

/// <summary>
/// 审计日志（设计方案 §9.2 原则 3：每个原语做审计）。
///
/// <b>独立于普通宿主日志</b>：审计是安全事件记录，不是诊断信息 ——
/// 每条调用一行 JSON，落 <c>logs\audit-yyyyMMdd.log</c>，不分级、不过滤、不省略。
/// 写失败策略：**不把业务打死，但不许静默** —— 首次失败告警一次，之后每 100 次再告警一次。
/// （原实现只吞异常、靠类注释"承诺"告警；那等于把安全链路的降级做成零信号，见
/// <c>docs/代码审查报告-静态守卫首跑-2026-09-28.md</c> §三。）
/// </summary>
public sealed class AuditLog
{
    /// <summary>每累计这么多次写失败就再告警一次（首次必告警）—— 长跑进程不会变成瞎子。</summary>
    private const int RewarnEvery = 100;

    private readonly string _dir;
    private readonly bool _echo;
    private int _failures;

    public AuditLog(string logsDir, bool echo = false)
    {
        _dir = logsDir;
        _echo = echo;
        try
        {
            Directory.CreateDirectory(_dir);
        }
        catch (Exception ex)
        {
            Warn("审计目录不可用", ex);
        }
    }

    /// <summary>累计写失败次数（0 = 审计链路健康）。首次失败即会写进 Core 控制台。</summary>
    public int Failures => Volatile.Read(ref _failures);

    private void Warn(string what, Exception ex)
    {
        int n = Interlocked.Increment(ref _failures);
        if (n == 1 || n % RewarnEvery == 0)
        {
            CoreConsole.Say($"[audit] 警告：{what}（累计 {n} 次）：{ex.GetType().Name}: {ex.Message}"
                            + " —— 审计记录正在丢失，请检查 logs 目录权限与磁盘空间");
        }
    }

    public static JsonObject NewEntry(
        string callerUser, int callerPid, string? toolId,
        string primitive, bool ok, double durationMs, string? error)
    {
        var entry = new JsonObject
        {
            ["ts"] = DateTimeOffset.Now.ToString("O"),
            ["callerPid"] = callerPid,
            ["callerUser"] = callerUser,
            ["toolId"] = toolId,
            ["primitive"] = primitive,
            ["ok"] = ok,
            ["durationMs"] = Math.Round(durationMs, 1),
        };
        if (error is not null)
        {
            entry["error"] = error;
        }

        return entry;
    }

    public void Append(JsonObject entry)
    {
        try
        {
            var file = Path.Combine(_dir, $"audit-{DateTime.Now:yyyyMMdd}.log");
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(entry.ToJsonString());
            writer.Write('\n');
        }
        catch (Exception ex)
        {
            // 不抛出（审计不可用不该打死业务），但**必须可见**：首次 + 每 100 次告警
            Warn("审计写入失败", ex);
        }

        if (_echo)
        {
            CoreConsole.Say("[audit] " + entry.ToJsonString());
        }
    }
}
