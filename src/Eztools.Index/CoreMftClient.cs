// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Index;

/// <summary>
/// ezt-index → Core 的管道客户端（W3-b-4 宿主接线）：把 <c>volume.readMft</c> 的响应
/// 翻译成 <see cref="MftReader"/> 委托 —— <see cref="VolumeWorker"/> 对响应来源零感知
///（selftest 注入假 reader 的断言面在真进程上原样成立）。
///
/// 与宿主侧 <c>Eztools.Host.Primitives.PrimitiveClient</c> 同构但独立落码（ezt-index
/// 不引用宿主程序集 —— 索引进程依赖宿主会形成反向依赖；两边共享的只有 Contracts）：
/// <list type="bullet">
/// <item>每批一连接（原语调用间无状态：无并发问题、无半开连接；代价 ~1ms/批 的建立开销，
/// 26 万条 ≈ 52 批 ≈ 100ms，相对枚举本身秒级可忽略 —— 若真卷实测占比 &gt; 10% 再改长连接，登记 §5.5）；</item>
/// <item>先 <c>core.hello</c> 拿版本与提权状态，再 <c>primitive.execute</c>；</item>
/// <item>Core 结构化错误（如 -32015 ElevationRequired）**原码透传**成 <see cref="MftReaderException"/>
/// —— VolumeWorker 的"未提权错误结构化传播"断言（ErrorCode == -32015 字面量）在真通路上成立。</item>
/// </list>
/// </summary>
public sealed class CoreMftClient
{
    /// <summary>每批的超时。5000 条批在机械盘冷缓存可达秒级；30s 是宽上限（连接建立另算，≤5s）。</summary>
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(30);

    private readonly string _installRoot;

    public CoreMftClient(string installRoot)
    {
        _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
    }

    /// <summary>
    /// 读一批 MFT 记录（<see cref="MftReader"/> 委托签名）。
    /// 失败以 <see cref="MftReaderException"/> 抛出（带 Core 侧错误码，-32017 = Core 不可达）。
    /// </summary>
    public JsonNode ReadBatch(string volume, long? cursor, int maxRecords)
    {
        var endpoint = CoreEndpoint.TryRead(_installRoot)
            ?? throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable,
                "Core 特权服务未运行（无端点登记）。建索引需要先 `ezt core start --elevate`。");

        // 陈旧端点预检：Core 被强杀时 core.json 来不及摘除 —— 不查 PID 会让
        // NamedPipeClientStream.ConnectAsync 把超时轮询满（宿主侧实测 30s 假死，同一坑不踩第二次）。
        if (!IsPidAlive(endpoint.Pid))
        {
            CoreEndpoint.Remove(_installRoot);   // 顺手清尸体端点
            throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable,
                $"Core 端点已登记（pid={endpoint.Pid}）但进程已不存在（陈旧端点，已清理）。");
        }

        using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut);
        try
        {
            client.ConnectAsync(5000).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable,
                $"Core 端点已登记（pid={endpoint.Pid}）但连接超时 —— Core 可能已僵死。");
        }
        catch (IOException ex)
        {
            throw new MftReaderException(
                PrimitiveErrorCodes.CoreUnavailable, $"无法连接 Core 管道（{ex.Message}）");
        }

        // reader/writer 都 leaveOpen：连接生命周期由 using(client) 独占管理
        using var reader = new StreamReader(
            client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        // 握手（诊断价值：版本与提权状态进 stderr 日志；校验语义在连接层 ACL 之外还有 CallerUser 一道）
        var hello = Request(reader, writer, CoreProtocolMethods.Hello, new JsonObject
        {
            ["hostName"] = "ezt-index",
            ["hostPid"] = Environment.ProcessId,
        });
        if (hello?["ok"]?.GetValue<bool>() != true)
        {
            throw new MftReaderException(PrimitiveErrorCodes.CoreUnavailable, "Core 握手未返回 ok=true");
        }

        if (hello["elevated"]?.GetValue<bool>() != true)
        {
            // 提前失败而不是等每批 readMft 都回 -32015：建索引必须提权 Core，这里如实说明
            throw new MftReaderException(
                PrimitiveErrorCodes.ElevationRequired,
                $"Core 在运行但未提权（version={hello["version"]}）。建索引需要 `ezt core start --elevate`。");
        }

        // result 缺失 = 契约破坏（Core 侧 result 可为 null 的原语没有 readMft）—— fail-fast，
        // 与 VolumeWorker 的 BadBatchShape 防御同源（那一层兜 null 只该针对注入夹具）
        return Request(reader, writer, CoreProtocolMethods.Execute, new JsonObject
        {
            ["name"] = PrimitiveNames.VolumeReadMft,
            ["args"] = new JsonObject
            {
                ["volume"] = volume,
                ["cursor"] = cursor,
                ["maxRecords"] = maxRecords,
            },
            ["toolId"] = "ezt-index",
            ["callerPid"] = Environment.ProcessId,
        }) ?? throw new MftReaderException(
            VolumeWorker.BadBatchShape, $"卷 {volume} 的 readMft 响应缺 result");
    }

    /// <summary>发一帧请求并等到匹配 id 的响应。error 响应翻译成 <see cref="MftReaderException"/>（原码透传）。</summary>
    /// <param name="fixedId">
    /// 测试注入：固定请求 id（selftest 用预置响应行做假服务端，动态 id 无法预配对）。
    /// null = 生产形态（tick+guid，串不了帧）。
    /// </param>
    public static JsonNode? Request(
        TextReader reader, TextWriter writer, string method, JsonObject prms, string? fixedId = null)
    {
        var id = fixedId ?? "ix" + Environment.TickCount64 + "-" + Guid.NewGuid().ToString("N")[..8];

        writer.WriteLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = prms,
        }.ToJsonString()).ConfigureAwait(false).GetAwaiter().GetResult();

        using var cts = new CancellationTokenSource(BatchTimeout);
        while (true)
        {
            var line = reader.ReadLineAsync(cts.Token).ConfigureAwait(false).GetAwaiter().GetResult()
                ?? throw new MftReaderException(PrimitiveErrorCodes.CoreUnavailable, "Core 在等待响应时关闭了连接");

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? frame;
            try
            {
                frame = JsonNode.Parse(line);
            }
            catch
            {
                continue;   // 坏帧跳过（\n 分帧；Core 侧不会主动发坏帧，防御性处理）
            }

            if (frame?["id"] is null || frame["id"]!.GetValue<string>() != id)
            {
                continue;   // 不是本请求的响应
            }

            if (frame["error"] is { } error)
            {
                throw new MftReaderException(
                    error["code"]?.GetValue<int>() ?? -32603,
                    error["message"]?.GetValue<string>() ?? "Core 返回未知错误");
            }

            // 🔴 必须 DeepClone：result 挂在刚解析的响应帧上，跨帧复用会抛
            // "The node already has a parent."（§14 坑 5 / §四.4 —— JsonNode 挂过父不能复用）
            return frame["result"]?.DeepClone();
        }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
