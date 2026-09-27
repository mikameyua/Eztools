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
/// 写失败静默丢弃（与 HostLog 同策略）：审计不可用不能反过来把业务打死，
/// 但会在 Core 启动日志里告警一次。
/// </summary>
public sealed class AuditLog
{
    private readonly string _dir;
    private readonly bool _echo;

    public AuditLog(string logsDir, bool echo = false)
    {
        _dir = logsDir;
        _echo = echo;
        try
        {
            Directory.CreateDirectory(_dir);
        }
        catch
        {
            // 目录建不出来时 Append 会继续静默失败
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
        catch
        {
            // 静默：见类注释
        }

        if (_echo)
        {
            CoreConsole.Say("[audit] " + entry.ToJsonString());
        }
    }
}
