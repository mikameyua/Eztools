using System.Diagnostics;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Core.Primitives;

/// <summary>
/// 进程原语（<c>process.enumerate</c> / <c>process.terminate</c>）。
///
/// terminate 的保护名单是**静态最小集**（csrss/wininit/winlogon/smss/services/lsass
/// + Core 自身 + 调用方宿主），不是完备的"绝不能杀"清单 —— 兜底是审计日志
/// （P3 方案 §9）。设计上故意只杀目标进程、不递归杀进程树：杀伤半径越小越好。
/// </summary>
public static unsafe class ProcessPrimitives
{
    /// <summary>杀死这些进程基本等于杀死 Windows 会话本身。</summary>
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "smss", "services", "lsass",
    };

    /// <summary>输出上限：进程列表工具不需要一次看几万个进程。</summary>
    private const int MaxEntries = 500;

    public static JsonNode Enumerate(long? pidFilter)
    {
        var processes = new JsonArray();

        foreach (var process in Process.GetProcesses())
        {
            if (pidFilter is { } filter && process.Id != filter)
            {
                continue;
            }

            string? path = null;
            try
            {
                path = process.MainModule?.FileName;   // 拒绝读取的进程（高权限）拿不到 path，如实返回 null
            }
            catch
            {
                // path 为 null 即可
            }

            processes.Add(new JsonObject
            {
                ["pid"] = process.Id,
                ["name"] = process.ProcessName,
                ["path"] = path,
            });

            process.Dispose();

            if (processes.Count >= MaxEntries)
            {
                break;
            }
        }

        return new JsonObject
        {
            ["count"] = processes.Count,
            ["truncated"] = processes.Count >= MaxEntries,
            ["processes"] = processes,
        };
    }

    public static JsonNode Terminate(long pid, int callerPid)
    {
        // ── 保护名单（自上而下逐条拒绝）──
        if (pid <= 4)
        {
            throw new PrimitiveException(
                PrimitiveErrorCodes.PrimitiveDenied, $"pid {pid} 是系统保留进程，拒绝结束");
        }

        if (pid == Environment.ProcessId)
        {
            throw new PrimitiveException(
                PrimitiveErrorCodes.PrimitiveDenied, "拒绝结束 Core 自身");
        }

        if (pid == callerPid)
        {
            throw new PrimitiveException(
                PrimitiveErrorCodes.PrimitiveDenied, "拒绝结束调用方（宿主）自身");
        }

        Process process;
        try
        {
            process = Process.GetProcessById((int)pid);
        }
        catch (ArgumentException)
        {
            throw new PrimitiveException(
                RpcErrorCodes.InvalidParams, $"pid {pid} 不存在或已退出");
        }

        using (process)
        {
            var name = process.ProcessName;
            if (ProtectedNames.Contains(name))
            {
                throw new PrimitiveException(
                    PrimitiveErrorCodes.PrimitiveDenied,
                    $"进程 {name}（pid {pid}）在关键系统进程保护名单内，拒绝结束");
            }

            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (Exception ex)
            {
                throw new PrimitiveException(
                    RpcErrorCodes.InternalError, $"结束 {name}（pid {pid}）失败：{ex.Message}");
            }

            var exited = process.WaitForExit(3000);
            return new JsonObject
            {
                ["ok"] = true,
                ["killed"] = exited,
                ["pid"] = pid,
                ["name"] = name,
                ["note"] = exited ? null : "kill 已发出，进程 3 秒内未确认退出",
            };
        }
    }
}
