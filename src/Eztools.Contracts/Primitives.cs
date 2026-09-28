// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Eztools.Contracts;

/// <summary>首批特权原语名（设计方案 §9.2）。Core 与宿主两侧只有这一份定义。</summary>
public static class PrimitiveNames
{
    /// <summary>进程列表（低风险，无需提权）。</summary>
    public const string ProcessEnumerate = "process.enumerate";

    /// <summary>结束进程（中风险；Core 侧有关键进程保护名单）。</summary>
    public const string ProcessTerminate = "process.terminate";

    /// <summary>枚举系统句柄表（中风险；全量需管理员）。</summary>
    public const string HandlesEnumerate = "handles.enumerate";

    /// <summary>卷列表（低风险，无需提权）。</summary>
    public const string VolumeEnumerate = "volume.enumerate";

    /// <summary>读 MFT / USN 记录（中风险，需要管理员）。</summary>
    public const string VolumeReadMft = "volume.readMft";

    /// <summary>查 USN journal 元信息（中风险，需要管理员；W3-c 同步层，2026-09-24 特权评审新增）。</summary>
    public const string VolumeQueryJournal = "volume.queryJournal";

    /// <summary>读 USN 变更流一批（中风险，需要管理员；≤1MB/次，同 readMft 的"防灌爆"口径）。</summary>
    public const string VolumeReadUsn = "volume.readUsn";

    /// <summary>写 USN close 记录（心跳；中风险，需要管理员）。</summary>
    public const string VolumeWriteUsnClose = "volume.writeUsnClose";

    /// <summary>Core 当前实现的原语全集 —— 宿主侧用它区分"未声明"与"Core 版本过旧"。</summary>
    public static readonly IReadOnlyList<string> Known = new[]
    {
        ProcessEnumerate, ProcessTerminate, HandlesEnumerate, VolumeEnumerate, VolumeReadMft,
        VolumeQueryJournal, VolumeReadUsn, VolumeWriteUsnClose,
    };
}

/// <summary>宿主 ↔ Core 的协议方法名（命名管道上的 JSON-RPC，NewLineDelimited，与 stdio 同一套分帧约定）。</summary>
public static class CoreProtocolMethods
{
    /// <summary>连接后的首帧握手：宿主自报身份，Core 校验调用方。</summary>
    public const string Hello = "core.hello";

    /// <summary>存活探测（<c>ezt core ping</c>）。</summary>
    public const string Ping = "core.ping";

    /// <summary>执行特权原语。</summary>
    public const string Execute = "primitive.execute";

    /// <summary>请求 Core 优雅退出（<c>ezt core stop</c>）。</summary>
    public const string Shutdown = "core.shutdown";
}

/// <summary>本项目的扩展错误码（续接 ProtocolMethods.RpcErrorCodes 的 -32000 保留区间）。</summary>
public static class PrimitiveErrorCodes
{
    /// <summary>工具清单未声明该原语（<c>elevatedPrimitives</c>）。</summary>
    public const int PrimitiveNotDeclared = -32013;

    /// <summary>Core 不认识该原语（Core 版本过旧）。</summary>
    public const int UnknownPrimitive = -32014;

    /// <summary>原语需要提权的 Core（如读 MFT），当前 Core 未提权。</summary>
    public const int ElevationRequired = -32015;

    /// <summary>调用方身份/完整性校验未通过（Core 拒绝连接）。</summary>
    public const int CallerRejected = -32016;

    /// <summary>Core 未运行或不可达。</summary>
    public const int CoreUnavailable = -32017;

    /// <summary>参数在语义上被拒绝（如结束关键系统进程 —— 不同于格式错误的 InvalidParams）。</summary>
    public const int PrimitiveDenied = -32018;

    /// <summary>
    /// USN journal 不可用（未开启 / 删除中 / 条目已删，Win32 1178/1179/1181）。
    /// W3-c 同步层按此判"该卷静态快照"；消息带原 Win32 码。
    /// </summary>
    public const int JournalUnavailable = -32021;
}

/// <summary>
/// Core 端点发现（<c>&lt;安装根&gt;\core.json</c>）。
///
/// 这是<b>发现机制，不是安全机制</b> —— 管道 ACL 与连接时身份校验才是安全边界。
/// 文件可被同用户篡改，那属于"同用户已经能做任意事"的威胁模型，不设防。
/// 模型放在 Contracts 是因为 Core（写）与宿主（读）必须共用一份字段定义，
/// 两边各写一份迟早漂移。
/// </summary>
public static class CoreEndpoint
{
    public const string FileName = "core.json";

    public sealed record CoreInfo(
        string PipeName,
        int Pid,
        bool Elevated,
        DateTimeOffset StartedAt,
        string Version);

    /// <summary>管道名：ezt.core.&lt;安装根路径 SHA256 前 12 位&gt; —— 多安装根共存不冲突。</summary>
    public static string ComputePipeName(string installRoot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()));
        return "ezt.core." + Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    public static string EndpointFile(string installRoot) =>
        Path.Combine(installRoot, FileName);

    /// <summary>读取端点文件。不存在、损坏或字段缺失都返回 null（= 未运行）。</summary>
    public static CoreInfo? TryRead(string installRoot)
    {
        var file = EndpointFile(installRoot);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var node = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            var pipeName = node?.GetString("pipeName");
            var pid = node?["pid"]?.GetValue<int?>();
            if (string.IsNullOrEmpty(pipeName) || pid is null)
            {
                return null;
            }

            return new CoreInfo(
                pipeName!,
                pid.Value,
                node?["elevated"]?.GetValue<bool>() ?? false,
                node?["startedAt"] is { } ts && DateTimeOffset.TryParse(ts.GetValue<string>(), out var at)
                    ? at
                    : DateTimeOffset.MinValue,
                node?.GetString("version") ?? "");
        }
        catch
        {
            return null; // 损坏的端点文件当作"未运行"，绝不让发现层抛异常
        }
    }

    /// <summary>原子写入端点文件（先写临时名再替换，宿主读到半截文件的概率归零）。</summary>
    public static void Write(string installRoot, CoreInfo info)
    {
        var file = EndpointFile(installRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var obj = new JsonObject
        {
            ["pipeName"] = info.PipeName,
            ["pid"] = info.Pid,
            ["elevated"] = info.Elevated,
            ["startedAt"] = info.StartedAt.ToString("O"),
            ["version"] = info.Version,
        };

        var tmp = file + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>删除端点文件（Core 退出时调用；不存在不报错）。</summary>
    public static void Remove(string installRoot)
    {
        try
        {
            File.Delete(EndpointFile(installRoot));
        }
        catch
        {
            // review-guards:allow-empty-catch :: 忽略：文件清理失败不影响退出
        }
    }
}
