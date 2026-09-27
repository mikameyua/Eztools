// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Host.Primitives;

/// <summary>
/// 宿主 → Core 的管道客户端（P3）。
///
/// <b>每调用一连接</b>，不维持长连接 —— 原语调用是低频事件（进程列表/结束进程/句柄枚举），
/// 无状态客户端没有并发与半开连接问题，代价（每调用 ~1ms 的管道建立）可以忽略。
///
/// 请求两步：先 <c>core.hello</c>（拿到 Core 版本与提权状态，供诊断），再 <c>primitive.execute</c>。
/// Core 的错误码原样透传给工具侧（如 <c>ElevationRequired</c>），工具能据此提示"去提权"而不是笼统失败。
/// </summary>
public sealed class PrimitiveClient
{
    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;

    public PrimitiveClient(EztoolsPaths paths, HostLog log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>执行一次原语调用。失败以 <see cref="ToolRpcException"/> 抛出（带 Core 侧错误码）。</summary>
    public async Task<JsonNode?> CallAsync(
        string name,
        JsonObject? args,
        string? toolId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var endpoint = CoreEndpoint.TryRead(_paths.Root)
                       ?? throw new ToolRpcException(
                           PrimitiveErrorCodes.CoreUnavailable,
                           "Core 特权服务未运行。请先执行 `ezt core start`（或 `ezt core start --elevate` 提权运行）。");

        // 陈旧端点预检：Core 被强杀时 core.json 来不及摘除（finally 不执行），
        // 不查 PID 的话 NamedPipeClientStream.ConnectAsync 会把超时轮询满（实测 30s 假死）。
        if (!IsPidAlive(endpoint.Pid))
        {
            CoreEndpoint.Remove(_paths.Root);   // 顺手清掉尸体端点，status 恢复如实
            throw new ToolRpcException(
                PrimitiveErrorCodes.CoreUnavailable,
                $"Core 端点已登记（pid={endpoint.Pid}）但进程已不存在（陈旧端点，已清理）。执行 `ezt core start` 重启。");
        }

        using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut);

        try
        {
            // 连接建立不超过 5s：管道在同一台机器上，连接慢 = Core 出问题了，别让调用方陪等
            await client.ConnectAsync(Math.Clamp((int)timeout.TotalMilliseconds, 1000, 5000), ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ToolRpcException(
                PrimitiveErrorCodes.CoreUnavailable,
                $"Core 端点已登记（pid={endpoint.Pid}）但连接超时 —— Core 可能已僵死，试试 `ezt core stop && ezt core start`。");
        }
        catch (IOException ex)
        {
            throw new ToolRpcException(
                PrimitiveErrorCodes.CoreUnavailable,
                $"无法连接 Core 管道（{ex.Message}）。`ezt core status` 查看，或 `ezt core start` 重启。");
        }

        // reader/writer 都 leaveOpen：连接的生命周期由上面的 using(client) 独占管理，避免多重 dispose
        using var reader = new StreamReader(
            client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        // ── 握手：Core 在此校验调用方身份与完整性（ACL 之外的第二道闸）──
        var hello = await RequestAsync(reader, writer, CoreProtocolMethods.Hello, new JsonObject
        {
            ["hostName"] = "ezt",
            ["hostPid"] = Environment.ProcessId,
        }, timeout, ct).ConfigureAwait(false);

        _log.Debug(
            $"Core 握手成功：version={hello?["version"]} elevated={hello?["elevated"]}", "primitive");

        // ── 执行 ──
        return await RequestAsync(reader, writer, CoreProtocolMethods.Execute, new JsonObject
        {
            ["name"] = name,
            ["args"] = args?.Clone() ?? new JsonObject(),
            ["toolId"] = toolId,
            ["callerPid"] = Environment.ProcessId,
        }, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>发一帧请求并等到匹配 id 的响应（跳过无关帧）。error 响应转 <see cref="ToolRpcException"/>。</summary>
    private static async Task<JsonNode?> RequestAsync(
        StreamReader reader,
        StreamWriter writer,
        string method,
        JsonObject prms,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var id = $"h{Interlocked.Increment(ref _seq)}";

        await writer.WriteLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = prms,
        }.ToJsonString()).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw new ToolRpcException(
                    PrimitiveErrorCodes.CoreUnavailable, "Core 在等待响应时关闭了连接");
            }

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
                continue;   // 坏帧跳过（协议约定 \n 分帧，一行一帧；Core 侧不会主动发坏帧，防御性处理）
            }

            if (frame?["id"] is null || frame["id"]!.GetValue<string>() != id)
            {
                continue;   // 不是本请求的响应
            }

            if (frame["error"] is { } error)
            {
                throw new ToolRpcException(
                    error["code"]?.GetValue<int>() ?? RpcErrorCodes.InternalError,
                    error["message"]?.GetValue<string>() ?? "Core 返回未知错误");
            }

            // 🔴 必须克隆：result 还挂在刚解析的响应帧上，直接回给工具帧会
            // "The node already has a parent."（设计方案 §14 的坑 5，这里又踩了一次）
            return frame["result"]?.DeepClone();
        }
    }

    private static int _seq;

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

    // ── ezt-core.exe 定位 ──

    /// <summary>
    /// 定位 ezt-core.exe（<c>ezt core start</c> 用）。顺序：
    /// 环境变量覆盖 → exe 同目录（已安装/同 build 输出）→ 安装根 bin → 仓库开发形态回退。
    /// </summary>
    public static string? FindCoreExe(EztoolsPaths paths)
    {
        var candidates = new List<string>();

        var env = Environment.GetEnvironmentVariable("EZTOOLS_CORE_EXE");
        if (!string.IsNullOrEmpty(env))
        {
            candidates.Add(env);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ezt-core.exe"));
        candidates.Add(Path.Combine(paths.BinDir, "ezt-core.exe"));
        candidates.AddRange(RepoFallbackCandidates());

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> RepoFallbackCandidates()
    {
        // 开发形态：ezt.exe 在 src/Eztools.Cli/bin/... 下，向上找仓库根，再落到 Core 的构建输出
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var coreDir = Path.Combine(dir.FullName, "src", "Eztools.Core", "bin");
            if (Directory.Exists(coreDir))
            {
                foreach (var config in new[] { "Debug", "Release" })
                {
                    var configDir = Path.Combine(coreDir, config);
                    if (!Directory.Exists(configDir))
                    {
                        continue;
                    }

                    foreach (var tfm in Directory.GetDirectories(configDir))
                    {
                        yield return Path.Combine(tfm, "ezt-core.exe");
                    }
                }
            }

            dir = dir.Parent;
        }
    }
}
