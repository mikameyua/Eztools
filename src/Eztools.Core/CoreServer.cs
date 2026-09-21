using System.IO.Pipes;
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

    public CoreServer(string installRoot, bool echo = false)
    {
        _installRoot = Path.GetFullPath(installRoot);
        _audit = new AuditLog(Path.Combine(_installRoot, "logs"), echo);
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

        CoreEndpoint.Write(_installRoot, new CoreEndpoint.CoreInfo(
            pipeName, Pid, Elevated, DateTimeOffset.Now, _version));

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

    /// <summary>排查用的文件 trace（不依赖父进程的 stdout 是否还活着）。</summary>
    private void Trace(string message)
    {
        try
        {
            var dir = Path.Combine(_installRoot, "logs");
            var file = Path.Combine(dir, "core-trace.log");

            // 按大小轮转，留 3 份历史（.1 最新 → .3 最旧）。
            // 为什么必须轮转：Core 是**长期驻留**的特权进程，trace 每次连接/异常都写一行，
            // 不轮转就是无界增长——而它落在用户磁盘的安装根里，没有任何东西会替它收拾。
            // 阈值取 1 MB：单行约 100 B，1 万行足够回溯最近几次失败，又不至于占空间。
            const long MaxBytes = 1024 * 1024;
            if (File.Exists(file) && new FileInfo(file).Length >= MaxBytes)
            {
                for (var i = 2; i >= 1; i--)
                {
                    var older = file + "." + i;
                    var newer = file + "." + (i + 1);
                    if (File.Exists(older))
                    {
                        File.Move(older, newer, overwrite: true);
                    }
                }

                File.Move(file, file + ".1", overwrite: true);
            }

            Directory.CreateDirectory(dir);
            File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [pid={Pid}] {message}\n");
        }
        catch
        {
            // trace 失败无所谓
        }
    }

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
            }
            catch (Exception ex)
            {
                CoreConsole.Error($"ezt-core: 创建管道失败：{ex.Message}");
                throw;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
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
                    }).ConfigureAwait(false);
                    return DispatchOutcome.Replied;

                case CoreProtocolMethods.Ping:
                    await WriteResultAsync(writer, id, new JsonObject
                    {
                        ["pong"] = true,
                        ["elevated"] = Elevated,
                        ["pid"] = Pid,
                        ["version"] = _version,
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
            _audit.Append(AuditLog.NewEntry(callerUser, callerPid, toolId, name, ok: true, ms, null));
            await WriteResultAsync(writer, id, result).ConfigureAwait(false);
        }
        catch (PrimitiveException pe)
        {
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _audit.Append(AuditLog.NewEntry(callerUser, callerPid, toolId, name, false, ms, pe.Message));
            await WriteErrorAsync(writer, id, pe.Code, pe.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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
    // 用 PipeOptions.CurrentUserOnly（.NET 5+ 内建）：内核在连接时校验客户端令牌
    // 是同一用户，别的用户/匿名一律 ERROR_ACCESS_DENIED。
    // 最初手写 PipeSecurity + NamedPipeServerStreamAcl，实测在跨父进程场景下
    // CreateNamedPipe 间歇性 Access Denied（自绘 SD 的坑）；CurrentUserOnly 一条搞定。

    private static NamedPipeServerStream CreatePipe(string name) =>
        new(name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096, outBufferSize: 4096);

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
