// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Core.Primitives;

/// <summary>原语执行上下文（调用方信息，全部来自宿主转发，进审计）。</summary>
public sealed record PrimitiveContext(int CallerPid, string? ToolId, CancellationToken Ct);

/// <summary>
/// 原语注册表 —— **Core 能做的全部事情**。
///
/// 设计原则（设计方案 §9.2）：固定数量级 · 每个原语是明确可审计的具体操作 ·
/// 缺能力就新增一个原语（改这里），**不开"调任意 Win32 API"的后门**。
/// 新原语三步：写实现 → 在 <see cref="All"/> 登记 → 加进 <see cref="PrimitiveNames"/> 与宿主侧 Known。
/// </summary>
public static class PrimitiveRegistry
{
    public sealed record Spec(
        string Name,
        string Risk,
        bool RequiresElevation,
        Func<JsonObject, PrimitiveContext, JsonNode> Execute);

    public static readonly IReadOnlyDictionary<string, Spec> All = Build();

    public static Spec? Find(string name) =>
        name is not null && All.TryGetValue(name, out var spec) ? spec : null;

    private static Dictionary<string, Spec> Build() => new(StringComparer.Ordinal)
    {
        [PrimitiveNames.ProcessEnumerate] = new(
            PrimitiveNames.ProcessEnumerate, Risk: "低", RequiresElevation: false,
            (args, _) => ProcessPrimitives.Enumerate(OptionalLong(args, "pid"))),

        [PrimitiveNames.ProcessTerminate] = new(
            PrimitiveNames.ProcessTerminate, Risk: "中", RequiresElevation: false,
            // 结束同用户进程本不需要管理员（File Locksmith 同语义）；防线是保护名单 + 审计。
            // 只有"杀高权限进程"才真正需要提权，那种场景会自然失败（OpenProcess 拒绝）。
            (args, ctx) => ProcessPrimitives.Terminate(
                RequireLong(args, "pid"), ctx.CallerPid)),

        [PrimitiveNames.HandlesEnumerate] = new(
            PrimitiveNames.HandlesEnumerate, Risk: "中", RequiresElevation: false,
            (args, ctx) =>
            {
                var pid = OptionalLong(args, "pid");
                var outcome = HandlePrimitives.Enumerate((ulong?)pid, ctx.Ct);
                var entries = outcome.Entries;
                var array = new JsonArray();
                foreach (var entry in entries.Take(2000))
                {
                    array.Add(new JsonObject
                    {
                        ["pid"] = entry.Pid,
                        ["type"] = entry.Type,
                        ["path"] = entry.Path,
                    });
                }

                return new JsonObject
                {
                    ["count"] = entries.Count,
                    ["truncated"] = entries.Count > 2000,
                    // ★ RI-6：挂起对象黑名单跳过的条数 + 已知挂起对象规模。
                    //   两者都是**已知的数据缺失 / 机制状态**，必须对外可见
                    //   （规范 §3.3 3.9：降级/丢弃类信号必须有可达出口 —— 否则调用方会把
                    //   "少了几条"误读成"系统里就这么多"）。
                    ["skippedStalled"] = outcome.SkippedStalled,
                    ["stalledObjects"] = outcome.StalledObjectsKnown,
                    ["handles"] = array,
                };
            }),

        [PrimitiveNames.VolumeEnumerate] = new(
            PrimitiveNames.VolumeEnumerate, Risk: "低", RequiresElevation: false,
            (_, _) => VolumePrimitives.Enumerate()),

        [PrimitiveNames.VolumeReadMft] = new(
            PrimitiveNames.VolumeReadMft, Risk: "中", RequiresElevation: true,
            (args, _) => VolumePrimitives.ReadMft(
                RequireString(args, "volume"),
                OptionalLong(args, "cursor"),
                OptionalLong(args, "maxRecords"))),

        // ── W3-c 同步层（USN journal；2026-09-24 特权评审：零提权读 USN 实测不可行
        //    —— 卷设备 SD 不给非提权令牌数据访问权（err=5），FSCTL 在属性句柄上 err=1 ——
        //    增量读取改为经提权 Core 原语，与 readMft 同族同审计）──
        [PrimitiveNames.VolumeQueryJournal] = new(
            PrimitiveNames.VolumeQueryJournal, Risk: "低", RequiresElevation: true,
            (args, _) => VolumePrimitives.QueryJournal(RequireString(args, "volume"))),

        [PrimitiveNames.VolumeReadUsn] = new(
            PrimitiveNames.VolumeReadUsn, Risk: "中", RequiresElevation: true,
            (args, _) => VolumePrimitives.ReadUsn(
                RequireString(args, "volume"),
                RequireLong(args, "journalId"),
                RequireLong(args, "fromUsn"),
                OptionalLong(args, "maxBytes"))),

        [PrimitiveNames.VolumeWriteUsnClose] = new(
            PrimitiveNames.VolumeWriteUsnClose, Risk: "中", RequiresElevation: true,
            (args, _) => VolumePrimitives.WriteUsnClose(
                RequireString(args, "volume"))),
    };

    // ── 参数校验帮手（每个原语的参数面在这里收口，Core 不信任转发层）──

    private static long? OptionalLong(JsonObject args, string name)
    {
        if (args.TryGetPropertyValue(name, out var node) && node is not null)
        {
            try
            {
                return node.GetValue<long>();
            }
            catch
            {
                if (node.GetValue<double>() is { } d && d == Math.Floor(d))
                {
                    return (long)d;
                }
            }

            throw new PrimitiveException(
                RpcErrorCodes.InvalidParams, $"参数 {name} 必须是整数");
        }

        return null;
    }

    private static long RequireLong(JsonObject args, string name) =>
        OptionalLong(args, name)
        ?? throw new PrimitiveException(RpcErrorCodes.InvalidParams, $"缺少必填参数 {name}");

    private static string RequireString(JsonObject args, string name)
    {
        if (args.TryGetPropertyValue(name, out var node)
            && node is not null
            && node.GetValue<string>() is { } value
            && value.Length > 0)
        {
            return value;
        }

        throw new PrimitiveException(RpcErrorCodes.InvalidParams, $"缺少必填参数 {name}");
    }
}
