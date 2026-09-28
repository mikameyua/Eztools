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
///
/// <b>告警有两条出口，缺一不可</b>（2026-09-29 安全面专项审 RI-1 修）：
/// <list type="number">
/// <item>stdout（<see cref="CoreConsole.Say"/>）—— 只在 <b>非提权</b> 启动时被宿主重定向收集
///   （<c>Scripts/Eztools.Cli/CoreCommand.cs</c> 里 <c>!elevate</c> 才 <c>RedirectStandardOutput</c>）；</item>
/// <item><b>文件</b> <c>logs\core-audit-health.log</c> —— <b>提权 Core 上唯一可达的出口</b>。
///   提权只能走 ShellExecute，其 stdout <b>没有任何接收者</b>（该处代码注释原文："ShellExecute 会把它吞掉"），
///   而提权恰恰是常态形态（索引 / 需提权原语都靠它）。</item>
/// </list>
/// 只留 stdout 的后果：**告警在提权 Core 上从未到达过任何人**，审计可用性降级＝完全静默 ——
/// 与"<c>CurrentUserOnly</c> 在提权进程静默死亡"同族（规范 §7.3 FAQ 12）。健康状态另经
/// <see cref="Failures"/> 暴露给 <c>primitive.hello</c> / <c>primitive.ping</c> 的 <c>auditFailures</c> 字段。
/// （本类先前只吞异常、靠类注释"承诺"告警；改法见
/// <c>docs/代码审查报告-静态守卫首跑-2026-09-28.md</c> §三。）
/// </summary>
public sealed class AuditLog
{
    /// <summary>每累计这么多次写失败就再告警一次（首次必告警）—— 长跑进程不会变成瞎子。</summary>
    private const int RewarnEvery = 100;

    private readonly string _dir;
    private readonly string _healthFile;
    private readonly bool _echo;
    private int _failures;

    public AuditLog(string logsDir, bool echo = false)
    {
        _dir = logsDir;
        _healthFile = Path.Combine(logsDir, "core-audit-health.log");
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

    /// <summary>累计写失败次数（0 = 审计链路健康）。经 hello / ping 的 auditFailures 字段对外暴露。</summary>
    public int Failures => Volatile.Read(ref _failures);

    private void Warn(string what, Exception ex)
    {
        int n = Interlocked.Increment(ref _failures);
        if (n == 1 || n % RewarnEvery == 0)
        {
            var line = $"[audit] 警告：{what}（累计 {n} 次）：{ex.GetType().Name}: {ex.Message}"
                       + " —— 审计记录正在丢失，请检查 logs 目录权限与磁盘空间";
            CoreConsole.Say(line);      // 出口 1：非提权形态可达（提权形态下无人接收）
            WriteHealthFile(line);      // 出口 2：提权形态下唯一可达
        }
    }

    /// <summary>审计降级的文件出口 —— 提权 Core 上唯一可达的告警路径（见类注释）。</summary>
    private void WriteHealthFile(string line)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.AppendAllText(_healthFile, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch
        {
            // review-guards:allow-empty-catch :: 连健康文件都写不进去时已无任何出口，
            //   调用方只能读 Failures 属性（hello / ping 的 auditFailures）判断
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
