// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host.Runtimes;

namespace Eztools.Host.Processes;

/// <summary>工具进程的启动与调用策略。</summary>
public sealed class ToolProcessOptions
{
    /// <summary>进程启动 + <c>tool.initialize</c> 的总预算。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>请求 <c>tool.stop</c> 后等待优雅退出的时间。</summary>
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>调用超时；null 表示按 <c>weight</c> 取默认值。</summary>
    public TimeSpan? CallTimeout { get; init; }

    /// <summary>stderr 环形缓冲行数（诊断用）。</summary>
    public int StderrBufferLines { get; init; } = 200;

    /// <summary>传给工具的配置（P1 配置中心接入后由宿主填充）。</summary>
    public JsonNode? Config { get; init; }

    public static ToolProcessOptions Default { get; } = new();
}

/// <summary>一次工具调用的结果。</summary>
public sealed class ToolCallResult
{
    public JsonNode? Result { get; init; }

    /// <summary>JSON-RPC 的 <c>result</c> 节点，便于取任意形状的返回值。</summary>
    public JsonNode? Raw { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 工具进程宿主（P0 从 spike 提升为正式实现）。
///
/// 承担四件事，每一件都对应一个 spike 实测过的坑：
/// <list type="number">
/// <item><b>启动</b>：标准参数 <c>-I -X utf8 -u &lt;entry&gt;</c>。
///   <c>-I</c> 阻断父进程 <c>PYTHONPATH</c> 与用户 site-packages 泄漏（实测会进入 <c>sys.path[1]</c>），
///   且实测比不隔离<b>快 41ms</b>；<c>-X utf8</c> 是唯一在隔离模式下仍生效的编码手段
///   （<c>-I</c> 隐含 <c>-E</c>，会忽略 <c>PYTHONUTF8</c> / <c>PYTHONIOENCODING</c>）；<c>-u</c> 避免宿主等首帧永久阻塞。</item>
/// <item><b>编码</b>：stdin 必须用 <c>new UTF8Encoding(false)</c>。BCL 的 <c>Encoding.UTF8</c> 是"带 BOM 标识"的实例，
///   用作 <c>StandardInputEncoding</c> 时会往子进程 stdin 开头写 <c>EF BB BF</c>，工具解析首帧直接失败，
///   <b>症状表现为"调用超时"</b>，症状与原因距离极远。</item>
/// <item><b>协议</b>：NewLineDelimited，只写 <c>\n</c>，逐帧 flush，stderr 独立任务读取（否则缓冲区写满死锁）。</item>
/// <item><b>回收</b>：超时/崩溃时 kill 整个进程树，避免僵尸进程。</item>
/// </list>
/// </summary>
public sealed class ToolProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HostLog _log;
    private readonly ScopedLog _toolLog;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentQueue<string> _stderr = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly TaskCompletionSource<int> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? _hostCallHandler;

    /// <summary>工具进程归属的 kill-on-close Job 句柄（宿主死亡时由 OS 关闭触发收割；Dispose 时归还）。</summary>
    private IntPtr _jobHandle;

    private long _seq;
    private int _disposed;
    private volatile bool _stopping;

    /// <summary>
    /// 本进程的关停令牌。<see cref="StopAsync"/> 置取消，工具 → 宿主的请求处理用它，
    /// 这样宿主关停时**正在执行的宿主侧调用**（原语 / 工具间调用）能立刻收手，
    /// 而不是等满各自的 30s 超时。
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    private ToolProcess(
        ToolManifest manifest,
        RuntimeInstallation runtime,
        Process process,
        ToolProcessOptions options,
        HostLog log,
        Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? hostCallHandler)
    {
        Manifest = manifest;
        Runtime = runtime;
        Options = options;
        _process = process;
        _log = log;
        _toolLog = log.Scope("tool:" + manifest.Id, manifest.Id);
        _hostCallHandler = hostCallHandler;

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            var code = SafeExitCode();
            _exited.TrySetResult(code);
            FailPending(new ToolCrashedException(
                $"工具 {manifest.Id} 进程已退出（exit={code}）"
                + (_stderr.IsEmpty ? "" : $"\nstderr:\n{string.Join("\n", _stderr.TakeLast(10))}"),
                code));
        };

        _stdoutTask = Task.Run(ReadStdoutLoopAsync);
        _stderrTask = Task.Run(ReadStderrLoopAsync);
    }

    public ToolManifest Manifest { get; }

    public RuntimeInstallation Runtime { get; }

    public ToolProcessOptions Options { get; }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public bool IsStopping => _stopping;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>工具在 <c>tool.initialize</c> 里自报的信息（SDK 版本、Python 版本、pid）。</summary>
    public JsonNode? InitializeInfo { get; private set; }

    /// <summary>stderr 最近若干行（诊断用）。</summary>
    public IReadOnlyList<string> StderrTail => _stderr.ToArray();

    /// <summary>启动工具进程并完成 <c>tool.initialize</c> 握手。</summary>
    public static async Task<ToolProcess> StartAsync(
        ToolManifest manifest,
        RuntimeInstallation? runtime,
        ToolProcessOptions options,
        HostLog log,
        Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? hostCallHandler = null,
        CancellationToken ct = default,
        bool attachKillOnCloseJob = true)
    {
        var psi = BuildStartInfo(manifest, runtime);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null");
        }
        catch (Exception ex)
        {
            throw new ToolProtocolException(
                $"无法启动工具 {manifest.Id}：{ex.Message}（可执行: {psi.FileName}）", inner: ex);
        }

        var instance = new ToolProcess(manifest, runtime!, process, options, log, hostCallHandler);

        // 归属 kill-on-close Job：宿主无论优雅退出还是崩溃，工具进程都会被内核一并收割。
        // resident 工具不挂（宿主崩溃/强杀时必须存活）；失败只记警告不阻断（增益路径）。P2 方案 §3.1。
        instance._jobHandle = ToolJobObject.Assign(process, log, attachKillOnCloseJob);

        try
        {
            var initResult = await instance
                .CallCoreAsync(
                    ProtocolMethods.ToolInitialize,
                    new JsonObject
                    {
                        ["toolId"] = manifest.Id,
                        ["toolVersion"] = manifest.Version,
                        ["config"] = options.Config?.Clone() ?? new JsonObject(),
                        ["context"] = new JsonObject
                        {
                            ["dataDir"] = ResolveToolDataDir(manifest.Id),
                            ["toolDir"] = manifest.ToolDirectory,
                            ["logFile"] = null,
                            ["hostVersion"] = typeof(ToolProcess).Assembly.GetName().Version?.ToString(),
                            ["protocol"] = 1,
                        },
                    },
                    options.StartupTimeout,
                    ct)
                .ConfigureAwait(false);

            instance.InitializeInfo = initResult;
            instance._toolLog.Info(
                $"进程已就绪 pid={process.Id} runtime={runtime?.Version ?? "exe"}");
            return instance;
        }
        catch
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ProcessStartInfo BuildStartInfo(ToolManifest manifest, RuntimeInstallation? runtime)
    {
        var psi = new ProcessStartInfo
        {
            FileName = runtime?.ExecutablePath ?? manifest.EntryPath,
            WorkingDirectory = manifest.ToolDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // 按 UTF-8 解码工具输出（否则在中文 Windows 上会按 OEM 代码页解出乱码）
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // 🔴 禁用 BOM：见类注释。这一行缺失会让首帧永久丢失，表现为"超时"
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        if (ToolRuntimes.IsInterpreted(manifest.Runtime))
        {
            switch (manifest.Runtime.ToLowerInvariant())
            {
                case ToolRuntimes.Python:
                    // -I 隔离 / -X utf8 编码（不受 -I 隐含的 -E 影响）/ -u 不缓冲
                    psi.ArgumentList.Add("-I");
                    psi.ArgumentList.Add("-X");
                    psi.ArgumentList.Add("utf8");
                    psi.ArgumentList.Add("-u");
                    psi.ArgumentList.Add(manifest.Entry);
                    // 刻意不设置 PYTHONUTF8 / PYTHONIOENCODING：-I 隐含 -E，这些环境变量会被忽略，
                    // 设了只会制造"以为有保险"的错觉。编码只靠 CLI 参数 + 工具入口 reconfigure。
                    break;

                case ToolRuntimes.Node:
                    psi.ArgumentList.Add(manifest.Entry);
                    break;

                default:
                    psi.ArgumentList.Add(manifest.Entry);
                    break;
            }
        }

        // 私有数据目录：与工具代码分离（卸载工具不丢数据）
        psi.Environment["EZTOOLS_TOOL_ID"] = manifest.Id;
        psi.Environment["EZTOOLS_TOOL_DATA_DIR"] = ResolveToolDataDir(manifest.Id);

        return psi;
    }

    /// <summary>工具私有数据目录（与工具代码目录分离）。</summary>
    private static string ResolveToolDataDir(string toolId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            EztoolsPaths.AppFolderName,
            "toolsdata",
            toolId,
            "data");

    /// <summary>按声明的命令 id 调用工具。</summary>
    public Task<JsonNode?> InvokeCommandAsync(string commandId, JsonNode? args, CancellationToken ct = default)
        => InvokeCommandAsync(commandId, args, Options.CallTimeout ?? Manifest.Weight.DefaultTimeout(), ct);

    public async Task<JsonNode?> InvokeCommandAsync(
        string commandId,
        JsonNode? args,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var result = await CallCoreAsync(
            ProtocolMethods.ToolInvoke,
            new JsonObject
            {
                ["commandId"] = commandId,
                ["args"] = args ?? new JsonObject(),
            },
            timeout,
            ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>直接按 handler 名调用（调试用；正常路径应走命令 id）。</summary>
    public async Task<JsonNode?> InvokeHandlerAsync(
        string handler,
        JsonNode? args,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        return await CallCoreAsync(
            ProtocolMethods.ToolInvoke,
            new JsonObject
            {
                ["handler"] = handler,
                ["args"] = args ?? new JsonObject(),
            },
            timeout,
            ct).ConfigureAwait(false);
    }

    /// <summary>发送一次 JSON-RPC 请求并等待响应。</summary>
    public async Task<JsonNode?> CallAsync(
        string method,
        JsonNode? prms,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        return await CallCoreAsync(method, prms, timeout, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode?> CallCoreAsync(
        string method,
        JsonNode? prms,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_process.HasExited)
        {
            throw new ToolCrashedException(
                $"工具 {Manifest.Id} 进程已退出（exit={SafeExitCode()}），无法调用 {method}", SafeExitCode());
        }

        var id = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            // Clone 是防御性的：JsonNode 一旦挂上父节点就不能再挂到别处，
            // 调用方复用同一个 args 节点时会抛 "The node already has a parent."
            ["params"] = prms?.Clone() ?? new JsonObject(),
        };

        var started = Stopwatch.GetTimestamp();

        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 分帧：一行一个 JSON，只写 \n（写 \r\n 会污染分帧），写完立即 flush
                await _process.StandardInput
                    .WriteAsync(envelope.ToJsonString() + "\n")
                    .ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(timeout, timeoutCts.Token);

            var completed = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                _toolLog.Warn($"调用 {method} 超过 {timeout.TotalSeconds:0.#}s 未返回，回收进程");

                if (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                }

                await KillAsync().ConfigureAwait(false);
                throw new ToolTimeoutException(
                    $"调用 {method} 超过 {timeout.TotalSeconds:0.#}s 未返回（进程已回收）");
            }

            timeoutCts.Cancel();

            var response = await tcs.Task.ConfigureAwait(false);
            if (response?["error"] is JsonNode error)
            {
                var code = error["code"]?.GetValue<int>() ?? RpcErrorCodes.InternalError;
                var message = error["message"]?.GetValue<string>() ?? "未提供错误信息";
                throw new ToolRpcException(code, $"{Manifest.Id} 的 {method} 返回错误: {message}",
                    error["data"]?.ToJsonString());
            }

            return response?["result"];
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _log.Debug(
                $"{Manifest.Id} {method} 用时 {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms",
                "ipc");
        }
    }

    private async Task ReadStdoutLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return; // 工具退出
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // 分帧被污染：多半是工具把调试输出写到了 stdout。协议要求日志走 stderr。
                    _toolLog.Warn($"stdout 出现非 JSON 输出（日志应写 stderr）: {Truncate(line)}");
                    continue;
                }

                if (node?["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(node);
                    }

                    continue;
                }

                // 工具 → 宿主的主动请求：必须回复，否则工具侧 await 会永久挂起
                _ = Task.Run(() => HandleToolRequestAsync(node));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_stopping)
            {
                _toolLog.Error($"stdout 读取循环异常: {ex.Message}");
            }
        }
    }

    private async Task HandleToolRequestAsync(JsonNode? node)
    {
        var method = node?["method"]?.GetValue<string>();
        var id = node?["id"];

        if (method is null)
        {
            return;
        }

        JsonObject response = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.Clone(),
        };

        try
        {
            JsonNode? result;
            if (_hostCallHandler is not null)
            {
                // 用本进程的关停令牌，而不是 CancellationToken.None：
                // 宿主在关停时不该为了等一条工具请求跑完而多耗 30s（见 _lifetime 的注释）。
                result = await _hostCallHandler(method, node?["params"], _lifetime.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new ToolRpcException(RpcErrorCodes.MethodNotFound, $"宿主未实现方法 {method}");
            }

            response["result"] = result ?? new JsonObject();
        }
        catch (ToolRpcException ex)
        {
            response["error"] = new JsonObject
            {
                ["code"] = ex.RpcCode,
                ["message"] = ex.Message,
            };
        }
        catch (Exception ex)
        {
            response["error"] = new JsonObject
            {
                ["code"] = RpcErrorCodes.InternalError,
                ["message"] = ex.Message,
            };
        }

        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteAsync(response.ToJsonString() + "\n").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            _toolLog.Warn($"回复工具请求 {method} 失败: {ex.Message}");
        }
    }

    private async Task ReadStderrLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                _stderr.Enqueue(line);
                while (_stderr.Count > Options.StderrBufferLines)
                {
                    _stderr.TryDequeue(out _);
                }

                _toolLog.Info("stderr: " + line);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 忽略：进程结束时会抛
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(exception);
        }

        _pending.Clear();
    }

    private int SafeExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>kill 整个进程树（工具可能拉起了子进程）。</summary>
    public async Task KillAsync()
    {
        try
        {
            if (_process.HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // review-guards:allow-empty-catch :: 已被回收 / 无权 kill：忽略
        }
    }

    /// <summary>优雅停止：先 <c>tool.stop</c>，超时再 kill。</summary>
    public async Task StopAsync()
    {
        if (_stopping)
        {
            await KillAsync().ConfigureAwait(false);
            return;
        }

        _stopping = true;

        // 先取消寿命令牌：让**正在处理中的**工具请求立刻失败返回，
        // 而不是等 CallCoreAsync 的宽限期跑完。
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // review-guards:allow-empty-catch :: 已 Dispose：没什么可取消的
        }

        try
        {
            if (!_process.HasExited)
            {
                await CallCoreAsync(ProtocolMethods.ToolStop, new JsonObject(), Options.StopGracePeriod)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // review-guards:allow-empty-catch :: 工具可能不接受 tool.stop 就直接退了：不算错误
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                await Task.WhenAny(_exited.Task, Task.Delay(Options.StopGracePeriod)).ConfigureAwait(false);
            }
        }
        catch
        {
            // review-guards:allow-empty-catch :: 已发过停止请求：管道断/进程已退都说明停止生效，后续强杀兜底
        }

        if (!_process.HasExited)
        {
            _toolLog.Warn("优雅退出超时，强制回收进程树");
            await KillAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            await KillAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAny(Task.WhenAll(_stdoutTask, _stderrTask), Task.Delay(1000)).ConfigureAwait(false);
        }
        catch
        {
            // review-guards:allow-empty-catch :: 收尾时等输出收集线程结算：进程已死，超时或异常都无需处理
        }

        _writeGate.Dispose();
        _lifetime.Dispose();
        try
        {
            _process.Dispose();
        }
        catch
        {
            // review-guards:allow-empty-catch :: 进程已退出：Dispose 抛异常无补救意义，句柄随对象释放
        }

        // 工具进程已终止，Job 句柄此时关闭只是句柄卫生；
        // "宿主崩溃 → 工具收割"由 OS 关闭句柄触发，不依赖这条路径
        ToolJobObject.Release(_jobHandle);
        _jobHandle = IntPtr.Zero;
    }
}
