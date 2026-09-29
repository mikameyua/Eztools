// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Core.Primitives;

namespace Eztools.Core;

/// <summary>
/// Core 特权服务主体：命名管道 JSON-RPC 服务（NewLineDelimited 分帧，与 stdio 同约定）。
///
/// 安全闸的顺序（P3 方案 §3）：
///   1. 管道隔离 —— <c>PipeOptions.CurrentUserOnly</c>：内核在连接时校验客户端是同一用户；
///   2. 身份校验 —— 连接后、首帧前 <see cref="CoreSecurity.ValidateCaller"/>（对端令牌核 SID + 完整性级别）；
///   3. 参数校验 —— 每条 <c>primitive.execute</c> 由 <see cref="PrimitiveRegistry"/> 收口。
/// 每条调用（含被拒的连接）都写 <see cref="AuditLog"/>。
///
/// 与工具进程协议的两条铁律在这里同样生效（设计方案 §5.2）：
/// <b>逐条回复</b>（包括错误 —— 不回调用方就挂起）、<b>逐帧 flush</b>。
/// </summary>
public sealed class CoreServer
{
    private readonly string _installRoot;
    private readonly AuditLog _audit;
    private readonly string _version;
    private readonly CancellationTokenSource _stop = new();
    private int _connections;

    public CoreServer(string installRoot, bool echo = false)
    {
        _installRoot = Path.GetFullPath(installRoot);
        _audit = new AuditLog(Path.Combine(_installRoot, "logs"), echo);
        CoreConsole.BindTraceDir(_installRoot);   // 让全 Core（含原语实现）都能写文件 trace
        _version = typeof(CoreServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Elevated = VolumePrimitives.IsElevated();
        PipeName = CoreEndpoint.ComputePipeName(_installRoot);
    }

    public string PipeName { get; }

    public bool Elevated { get; }

    public int Pid => Environment.ProcessId;

    /// <summary>阻塞运行直到取消（Ctrl+C 或 core.shutdown）。返回进程退出码。</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var pipeName = CoreEndpoint.ComputePipeName(_installRoot);

        // 幂等保护：已有活的 Core 在这个安装根上 → 拒绝双开（端口/文件无竞争，core.json 是发现不是锁）
        if (CoreEndpoint.TryRead(_installRoot) is { } existing && await ProbeAliveAsync(existing.PipeName))
        {
            CoreConsole.Say($"ezt-core: 已有实例在运行（pid={existing.Pid}），退出。");
            return 3;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        CoreConsole.Say($"ezt-core {_version} 启动：pipe={pipeName} elevated={Elevated} pid={Pid}");
        CoreConsole.Say($"  安装根：{_installRoot}");
        Trace("启动：进入 RunAsync");

        CoreEndpoint.Write(_installRoot, new CoreEndpoint.CoreInfo(
            pipeName, Pid, Elevated, DateTimeOffset.Now, _version));
        Trace("启动：端点已写入 core.json");

        try
        {
            var acceptLoop = AcceptLoopAsync(pipeName, linked.Token);
            await acceptLoop.ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Trace("正常取消退出");
            return 0;
        }
        catch (Exception ex)
        {
            Trace("异常退出：" + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            throw;
        }
        finally
        {
            CoreEndpoint.Remove(_installRoot);   // 退出即摘除端点：status 如实反映"没在跑"
            CoreConsole.Say("ezt-core 已退出。");
        }
    }

    /// <summary>
    /// 排查用的文件 trace（不依赖父进程的 stdout 是否还活着）。
    /// 实现已提到 <see cref="CoreConsole.Trace"/>，好让原语实现也能用它
    /// （2026-09-29：诊断句柄扫描的累积变慢时，Console.WriteLine 那条路根本收不到 ——
    /// 见 <see cref="CoreConsole"/> 的类注释）。
    /// </summary>
    private static void Trace(string message) => CoreConsole.Trace(message);

    /// <summary>请求退出（core.shutdown 走这里）。优雅：当前连接处理完自然结束。</summary>
    public void RequestStop() => _stop.Cancel();

    // ── 接入层 ──

    private async Task AcceptLoopAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(pipeName);
                Trace($"启动：管道已创建（elevated={Elevated}），等待连接");
            }
            catch (Exception ex)
            {
                Trace("管道创建失败：" + ex.GetType().Name + ": " + ex.Message);
                CoreConsole.Error($"ezt-core: 创建管道失败：{ex.Message}");
                throw;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                Trace($"管道连接到达（第 {Interlocked.Increment(ref _connections)} 个）");
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                throw;
            }
            catch (Exception)
            {
                pipe.Dispose();
                continue;   // 单个连接失败不影响服务
            }

            // 每连接一个任务；多实例管道（16）允许宿主并发调用
            _ = Task.Run(() => HandleConnectionAsync(pipe, ct), ct);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            // ── 闸 2/3：身份与完整性（必须在读任何数据之前）──
            var check = CoreSecurity.ValidateCaller(pipe);
            if (!check.Ok)
            {
                // callerPid 用 check.CallerPid —— 它是**内核给出的对端真实 PID**（连接时由
                // GetNamedPipeClientProcessId 取），不是客户端自报。被拒连接尤其需要它：
                // 事后要查"是哪个进程在试着连特权管道"，没有 PID 就只能看到一条无主的拒绝记录。
                _audit.Append(AuditLog.NewEntry(
                    check.CallerUser, check.CallerPid, null, "(connect)", false, 0, check.RejectionReason));
                await WriteErrorAsync(writer, null, PrimitiveErrorCodes.CallerRejected, "调用方身份校验未通过")
                    .ConfigureAwait(false);
                return;
            }

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    break;   // 客户端断开 / 取消
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? request;
                try
                {
                    request = JsonNode.Parse(line);
                }
                catch (Exception ex)
                {
                    await WriteErrorAsync(writer, null, RpcErrorCodes.ParseError, "请求不是合法 JSON：" + ex.Message)
                        .ConfigureAwait(false);
                    continue;
                }

                var responded = await DispatchAsync(writer, request!, check.CallerUser, check.CallerPid, ct)
                    .ConfigureAwait(false);

                if (responded == DispatchOutcome.ShutdownRequested)
                {
                    _stop.Cancel();
                    return;
                }
            }
        }
    }

    private enum DispatchOutcome { Replied, ShutdownRequested }

    /// <summary>分发一条请求。**任何路径都必须回复**（有 id 时），否则宿主/工具侧会挂起。</summary>
    private async Task<DispatchOutcome> DispatchAsync(
        StreamWriter writer,
        JsonNode request,
        string callerUser,
        int callerPid,
        CancellationToken ct)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";

        try
        {
            switch (method)
            {
                case CoreProtocolMethods.Hello:
                    await WriteResultAsync(writer, id, new JsonObject
                    {
                        ["ok"] = true,
                        ["version"] = _version,
                        ["elevated"] = Elevated,
                        ["callerUser"] = callerUser,
                        // 审计健康：0 = 链路正常。提权 Core 的 stdout 无人接收（见 AuditLog 类注释），
                        // 这是运维侧唯一能读到"审计正在丢"的通道。
                        ["auditFailures"] = _audit.Failures,
                    }).ConfigureAwait(false);
                    return DispatchOutcome.Replied;

                case CoreProtocolMethods.Ping:
                    await WriteResultAsync(writer, id, new JsonObject
                    {
                        ["pong"] = true,
                        ["elevated"] = Elevated,
                        ["pid"] = Pid,
                        ["version"] = _version,
                        ["auditFailures"] = _audit.Failures,
                    }).ConfigureAwait(false);
                    return DispatchOutcome.Replied;

                case CoreProtocolMethods.Shutdown:
                    await WriteResultAsync(writer, id, new JsonObject { ["ok"] = true })
                        .ConfigureAwait(false);
                    return DispatchOutcome.ShutdownRequested;

                case CoreProtocolMethods.Execute:
                    await ExecutePrimitiveAsync(writer, id, request, callerUser, callerPid, ct)
                        .ConfigureAwait(false);
                    return DispatchOutcome.Replied;

                default:
                    await WriteErrorAsync(writer, id, RpcErrorCodes.MethodNotFound, $"Core 未实现方法 {method}")
                        .ConfigureAwait(false);
                    return DispatchOutcome.Replied;
            }
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(writer, id, RpcErrorCodes.InternalError, $"Core 内部错误：{ex.Message}")
                .ConfigureAwait(false);
            return DispatchOutcome.Replied;
        }
    }

    private async Task ExecutePrimitiveAsync(
        StreamWriter writer,
        JsonNode? id,
        JsonNode request,
        string callerUser,
        int callerPid,
        CancellationToken ct)
    {
        var name = request["params"]?.GetString("name");
        var toolId = request["params"]?.GetString("toolId");
        // callerPid 用的是验证过的对端真实 PID（连接时由内核给出），不采信客户端自报
        var args = request["params"]?["args"] as JsonObject ?? new JsonObject();

        if (string.IsNullOrEmpty(name))
        {
            await WriteErrorAsync(writer, id, RpcErrorCodes.InvalidParams, "primitive.execute 缺少 name")
                .ConfigureAwait(false);
            return;
        }

        var spec = PrimitiveRegistry.Find(name);
        if (spec is null)
        {
            await WriteErrorAsync(
                    writer, id, PrimitiveErrorCodes.UnknownPrimitive,
                    $"Core 未实现原语 '{name}'（已知：{string.Join(", ", PrimitiveNames.Known)}）")
                .ConfigureAwait(false);
            _audit.Append(AuditLog.NewEntry(callerUser, callerPid, toolId, name!, false, 0, "未实现的原语"));
            return;
        }

        if (spec.RequiresElevation && !Elevated)
        {
            // 结构化错误而不是笼统失败：调用方（工具/UI）可以据此提示"去提权"而不是"坏了"
            await WriteErrorAsync(
                    writer, id, PrimitiveErrorCodes.ElevationRequired,
                    $"原语 {name} 需要提权的 Core（当前未提权）")
                .ConfigureAwait(false);
            _audit.Append(AuditLog.NewEntry(callerUser, callerPid, toolId, name, false, 0, "需要提权"));
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = spec.Execute(args, new PrimitiveContext(callerPid, toolId, ct));
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            // ⚠️ 顺序很重要：**先写响应，成功之后才记 ok:true**。
            //    原先相反（先记成功、再写响应）—— 于是"调用方已超时放弃、结果没人收到"的场景
            //    会被记成一条**成功的审计**（审计在说谎），紧接着写响应失败又补一条 IOException。
            //    2026-09-29 实测到的正是这组双记录：
            //      {ok:true, 5213.8ms} + {ok:false, 5252.7ms, "IOException: Pipe is broken."}
            await WriteResultAsync(writer, id, result).ConfigureAwait(false);
            _audit.Append(AuditLog.NewEntry(callerUser, callerPid, toolId, name, ok: true, ms, null));
        }
        catch (PrimitiveException pe)
        {
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _audit.Append(AuditLog.NewEntry(callerUser, callerPid, toolId, name, false, ms, pe.Message));
            await WriteErrorAsync(writer, id, pe.Code, pe.Message).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // 写响应时管道断开 = **调用方已经不在了**（超时放弃 ⇒ CLI 进程退出）。
            // 必须与"Core 内部错误"区分开：记成内部错误会把排查方向引到 Core 自己身上。
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _audit.Append(AuditLog.NewEntry(
                callerUser, callerPid, toolId, name, false, ms,
                "结果未送达（连接已断开，调用方可能已超时放弃）：" + ex.Message));
        }
        catch (OperationCanceledException)
        {
            throw;   // 服务关停（ct 取消）：保持原语义上抛，由上层收尾
        }
        catch (Exception ex)
        {
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _audit.Append(AuditLog.NewEntry(callerUser, callerPid, toolId, name, false, ms, ex.GetType().Name + ": " + ex.Message));
            await WriteErrorAsync(writer, id, RpcErrorCodes.InternalError, $"{ex.GetType().Name}: {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    // ── 帧写出（\n 分帧 + 逐帧 flush）──

    private static async Task WriteResultAsync(
        StreamWriter writer, JsonNode? id, JsonNode result)
    {
        await WriteFrameAsync(writer, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        }).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        StreamWriter writer, JsonNode? id, int code, string message)
    {
        await WriteFrameAsync(writer, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }).ConfigureAwait(false);
    }

    private static async Task WriteFrameAsync(StreamWriter writer, JsonObject frame)
    {
        await writer.WriteLineAsync(frame.ToJsonString()).ConfigureAwait(false);
    }

    // ── 管道创建（闸 1：仅当前用户）──
    //
    // 两条路径，按是否提权分流（2026-09-24 实测坑，见设计方案 §踩坑）：
    //
    // · 非提权 → PipeOptions.CurrentUserOnly（.NET 5+ 内建）：内核在连接时校验客户端
    //   令牌是同一用户，别的用户/匿名一律 ERROR_ACCESS_DENIED。
    //
    // · 提权 → **禁止 CurrentUserOnly**：实测（W3-a-3 验收①手工探针）提权进程创建
    //   CurrentUserOnly 管道会**原生层静默死亡**——端点已写入、无托管异常、无事件日志、
    //   trace 停在端点写入后，管道从未出现在 \\.\pipe\ 命名空间；非提权同码完全正常。
    //   提权路径改用**显式 SD**（官方 NamedPipeServerStreamAcl API 构造）：
    //   DACL = 当前用户 FullControl —— 与 CurrentUserOnly 的校验面等价。
    //   （早期"手绘 SD 间歇性 Access Denied"的坑由官方 API + 固定字段绕开：
    //   Owner/Group/DACL 三段齐全，不再留空由内核补默认。）
    //   纵深不受影响：闸 2（CoreSecurity.ValidateCaller）在每条连接上仍核对
    //   对端令牌 SID 与完整性级别，管道 ACL 被放宽时它仍然兜底。

    private const int MaxInstances = 16;
    private const int DefaultBufferSize = 4096;

    private NamedPipeServerStream CreatePipe(string name)
    {
        if (!Elevated)
        {
            return new(name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: DefaultBufferSize, outBufferSize: DefaultBufferSize);
        }

        var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException(
            "提权 Core 无法解析当前用户 SID（token 异常），拒绝创建无 ACL 的特权管道");

        var acl = new PipeSecurity();
        acl.SetOwner(user);
        acl.SetGroup(user);
        acl.AddAccessRule(new PipeAccessRule(
            user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, DefaultBufferSize, DefaultBufferSize, acl);
    }

    private static async Task<bool> ProbeAliveAsync(string pipeName)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await client.ConnectAsync(800).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
