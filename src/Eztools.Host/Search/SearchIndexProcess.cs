// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Host.Search;

/// <summary>
/// 宿主托管的 <c>ezt-index.exe</c> stdio 传输（W3-d-1）——
/// <see cref="ISearchIndexTransport"/> 的真进程实现（W3-b-4 注明"真进程 stdio 传输随 W3-d"）。
///
/// <b>进程托管模型（懒启动 + 冷却重启）</b>：
/// <list type="bullet">
/// <item>首次 RoundTrip 才拉起进程（搜索窗没开过 = 零常驻开销 —— index 不是宿主启动即起，
///   那是 <c>lifecycle: resident</c> 工具的语义，索引进程按需服务）。</item>
/// <item>spawn 后先 ping 一发（10 s 宽限）确认协议活着 —— exe 缺件/坏参数在此**当场**结构化报错，
///   不留到第一次查询时才炸（S11 同族：缺席无信号 = 最恶劣的失败形态）。</item>
/// <item>进程死亡 ⇒ 在途请求全部结构化失败；**下一次**请求自动重启（冷却 2 s 防崩溃循环）。
///   不做无人查询时的后台重启 —— "下次查询时活着"就是搜索窗需要的全部。</item>
/// <item>Dispose 走 <c>tool.stop</c> 优雅停止（索引进程有落盘收尾），等不满宽限期再整树 kill。</item>
/// </list>
///
/// <b>分帧与编码纪律（索引进程侧 IndexRpcServer 的镜像，两侧都要做）</b>：
/// 一行一个 JSON、只写 <c>\n</c>、逐帧 flush、UTF-8 无 BOM（BOM 容忍是索引侧的义务，
/// 但宿主侧的承诺仍然是不写 —— 两侧各自守住自己的半边）；stdout 出现非 JSON 帧
/// ⇒ 记入 <see cref="LastProtocolError"/> 继续（不猜它是谁的响应）；
/// id 配不上的帧同理 —— **绝不静默吞帧**，也绝不把 A 的响应交给 B（配对闸由响应帧 id 决定）。
///
/// <b>并发模型</b>：<see cref="RoundTripAsync"/> 用 <see cref="SemaphoreSlim"/> 全程串行
/// （并发写 stdin 会帧间交错）。上层的 <c>QueryPump</c> 用节流 + 单在途把请求频率
/// 压在"毫秒级查询串行无感知"的量级，这里的信号量是正确性兜底而非吞吐手段。
/// 内部状态（进程句柄 / pending 表）另用一把轻锁，两把锁不嵌套（先 state 后不碰 roundTrip）。
/// </summary>
public sealed class SearchIndexProcess : ISearchIndexTransport, IDisposable
{
    /// <summary>单次往返的响应宽限。查询是毫秒级；60 s = bootstrap 慢机器上的极端余量。</summary>
    private const int RoundTripTimeoutMs = 60_000;

    /// <summary>spawn 后 ping 的宽限（ping 不等 bootstrap，10 s 足够进程把自己跑起来）。</summary>
    private const int StartPingTimeoutMs = 10_000;

    /// <summary>重启冷却：进程刚死就来的请求在冷却期内直接报错，不无限拉起。</summary>
    private const int RestartCooldownMs = 2_000;

    /// <summary>优雅停止（tool.stop）后等退出的宽限，超时 kill。</summary>
    private const int GracefulStopTimeoutMs = 3_000;

    private readonly EztoolsPaths _paths;
    private readonly string? _dataRoot;
    private readonly SemaphoreSlim _roundTrip = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _pending = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private long _nextId;
    private long _lastExitAtMs;
    private bool _disposed;

    /// <summary>最近一次"stdout 出现配不上的帧"的诊断（非致命：帧被丢弃并记录，不猜归属）。</summary>
    public string? LastProtocolError { get; private set; }

    /// <summary>索引进程是否活着（诊断/自检显示用；懒启动下未拉起 = false，不是错误）。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public SearchIndexProcess(EztoolsPaths paths, string? dataRoot = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dataRoot = dataRoot;
    }

    /// <summary>定位 <c>ezt-index.exe</c>（与 <c>PrimitiveClient.FindCoreExe</c> 同族候选序列）。</summary>
    public static string? FindIndexExe(EztoolsPaths paths)
    {
        var candidates = new List<string>();

        var env = Environment.GetEnvironmentVariable("EZTOOLS_INDEX_EXE");
        if (!string.IsNullOrEmpty(env))
        {
            candidates.Add(env);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ezt-index.exe"));
        candidates.Add(Path.Combine(paths.BinDir, "ezt-index.exe"));
        candidates.AddRange(RepoFallbackCandidates());

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> RepoFallbackCandidates()
    {
        // 开发形态：向上找仓库根，落到 Index 的构建输出。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var indexDir = Path.Combine(dir.FullName, "src", "Eztools.Index", "bin");
            if (Directory.Exists(indexDir))
            {
                foreach (var config in new[] { "Debug", "Release" })
                {
                    var configDir = Path.Combine(indexDir, config);
                    if (!Directory.Exists(configDir))
                    {
                        continue;
                    }

                    foreach (var tfm in Directory.GetDirectories(configDir))
                    {
                        yield return Path.Combine(tfm, "ezt-index.exe");
                    }
                }
            }

            dir = dir.Parent;
        }
    }

    /// <inheritdoc />
    public async Task<JsonObject> RoundTripAsync(JsonObject request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _roundTrip.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RoundTripCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _roundTrip.Release();
        }
    }

    /// <summary>发一次 ping（诊断/自检用；返回 pong 的 result；进程没起来会在 ensure 阶段报错）。</summary>
    public async Task<JsonObject?> PingAsync(CancellationToken ct = default)
    {
        var response = await RoundTripAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = $"p{Interlocked.Increment(ref _nextId)}",
            ["method"] = "ping",
        }, ct).ConfigureAwait(false);
        return response["result"] as JsonObject;
    }

    private async Task<JsonObject> RoundTripCoreAsync(JsonObject request, CancellationToken ct)
    {
        var idText = request["id"]?.GetValue<string>()
            ?? throw new SearchIndexException(RpcErrorCodes.InvalidRequest, "请求帧缺 id（宿主 bug）");
        var id = ParseId(idText);

        await EnsureStartedAsync(ct).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateGate)
        {
            _pending[id] = tcs;
        }

        try
        {
            // Clone 语义说明：request 由调用方构造后即交给我们序列化，不存在跨帧复用；
            // ToJsonString 快照写出行后请求树的生命周期就结束了。
            await _stdin!.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await _stdin!.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RemovePending(id);
            MarkDead();
            throw new SearchIndexException(
                RpcErrorCodes.InternalError, $"写索引进程 stdin 失败（进程疑似退出）：{ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RoundTripTimeoutMs);
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                .ConfigureAwait(false);
            if (done != tcs.Task)
            {
                throw new SearchIndexException(
                    RpcErrorCodes.InternalError,
                    $"索引进程响应超时（{RoundTripTimeoutMs} ms 无配对响应帧，id={idText}）");
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            RemovePending(id);
        }
    }

    // ── 进程生命周期 ─────────────────────────────────────────────────────────

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (_process is { HasExited: false })
            {
                return;
            }
        }

        // 冷却（不持锁判定即可 —— 竞态的代价只是多一次 spawn 尝试，无正确性影响）
        var sinceExit = Environment.TickCount64 - _lastExitAtMs;
        if (_lastExitAtMs != 0 && sinceExit < RestartCooldownMs)
        {
            throw new SearchIndexException(
                RpcErrorCodes.InternalError,
                $"索引进程刚退出（{sinceExit} ms 前），重启冷却中 —— 稍后自动重试");
        }

        var exe = FindIndexExe(_paths)
            ?? throw new SearchIndexException(
                RpcErrorCodes.InternalError,
                "找不到 ezt-index.exe（候选：EZTOOLS_INDEX_EXE / 宿主目录 / 安装根 bin / 仓库构建输出）。"
                + "已安装形态应有 <安装根>\\bin\\ezt-index.exe");

        var args = new StringBuilder();
        if (_dataRoot is { } root)
        {
            args.Append("--data-root \"").Append(root).Append('"');
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // 编码纪律：两侧都钉死 UTF-8 无 BOM（索引进程承诺 stdout 只有 JSON 帧；
            // 宿主承诺 stdin 不写 BOM —— BOM 容忍是它的义务，但承诺不能拿来当借口）。
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        var process = Process.Start(startInfo)
            ?? throw new SearchIndexException(RpcErrorCodes.InternalError, "ezt-index 启动失败（Process.Start 返回空）");

        var stdin = process.StandardInput;
        var stdout = process.StandardOutput;

        lock (_stateGate)
        {
            _process = process;
            _stdin = stdin;
            _stdout = stdout;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            _lastExitAtMs = Environment.TickCount64;
            FailAllPending($"索引进程退出（exit={process.ExitCode}）");
        };

        StartReaderLoop(stdout);

        // 协议活性证明：ping 一发。exe 缺件 / runtime 缺失 / 首帧崩坏在此当场暴露，
        // 而不是伪装成"查询无结果"（恒真假象同族）。
        var pongTcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingId = ParseId($"p{Interlocked.Increment(ref _nextId)}");
        lock (_stateGate)
        {
            _pending[pingId] = pongTcs;
        }

        try
        {
            await stdin.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = $"p{pingId}",
                ["method"] = "ping",
            }.ToJsonString()).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);

            var done = await Task.WhenAny(pongTcs.Task, Task.Delay(StartPingTimeoutMs, ct)).ConfigureAwait(false);
            if (done != pongTcs.Task)
            {
                throw new SearchIndexException(
                    RpcErrorCodes.InternalError,
                    $"ezt-index ping 无应答（{StartPingTimeoutMs} ms）—— 进程启动异常，详见安装根 logs");
            }

            var pong = await pongTcs.Task.ConfigureAwait(false);
            if (pong["result"]?["ok"]?.GetValue<bool>() != true)
            {
                throw new SearchIndexException(
                    RpcErrorCodes.InternalError, "ezt-index ping 应答异常（缺 result.ok）");
            }
        }
        catch (OperationCanceledException)
        {
            RemovePending(pingId);
            throw;
        }
        catch (SearchIndexException)
        {
            RemovePending(pingId);
            MarkDead();
            throw;
        }
    }

    /// <summary>
    /// 读循环：持续把 stdout 帧按 id 配对到等待者。<b>从不吞帧</b>：
    /// 非 JSON / id 配不上 ⇒ 记 <see cref="LastProtocolError"/> 后丢弃（不猜归属、不阻塞后续帧）；
    /// 流结束 / 读异常 ⇒ 全部等待者结构化失败（挂死比对端错误更恶劣，§四.3）。
    /// </summary>
    private void StartReaderLoop(StreamReader stdout)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    JsonObject? frame;
                    try
                    {
                        frame = JsonNode.Parse(line) as JsonObject;
                    }
                    catch (JsonException ex)
                    {
                        LastProtocolError = $"非 JSON 帧（谁把调试输出写进了 stdout？）: {ex.Message}";
                        continue;
                    }

                    if (frame?["id"] is not { } idNode)
                    {
                        LastProtocolError = "响应帧缺 id（协议破坏），已丢弃";
                        continue;
                    }

                    var key = idNode.GetValue<string>();
                    TaskCompletionSource<JsonObject>? waiter;
                    lock (_stateGate)
                    {
                        _pending.Remove(ParseId(key), out waiter);
                    }

                    if (waiter is null)
                    {
                        LastProtocolError = $"配不上等待者的响应帧（id={key}），已丢弃";
                        continue;
                    }

                    waiter.TrySetResult(frame);
                }

                FailAllPending("索引进程输出流已关闭（stdin 收手或进程退出）");
            }
            catch (Exception ex)
            {
                FailAllPending($"索引进程读循环异常：{ex.Message}");
            }
        });
    }

    private void RemovePending(long id)
    {
        lock (_stateGate)
        {
            _pending.Remove(id, out _);
        }
    }

    /// <summary>进程判死：kill + 释放流 + 记退出时刻（冷却计时起点）。自带轻锁，不要求调用方持锁。</summary>
    private void MarkDead()
    {
        _lastExitAtMs = Environment.TickCount64;
        Process? process;
        lock (_stateGate)
        {
            process = _process;
            _process = null;
        }

        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch
        {
            // review-guards:allow-empty-catch :: 进程已死 = 目标达成；kill 的失败（如已退出竞态）无信息量
        }

        _stdin?.Dispose();
        _stdin = null;
        _stdout?.Dispose();
        _stdout = null;
    }

    private void FailAllPending(string reason)
    {
        KeyValuePair<long, TaskCompletionSource<JsonObject>>[] snapshot;
        lock (_stateGate)
        {
            snapshot = _pending.ToArray();
            _pending.Clear();
            _lastExitAtMs = Environment.TickCount64;
        }

        foreach (var p in snapshot)
        {
            p.Value.TrySetException(new SearchIndexException(RpcErrorCodes.InternalError, reason));
        }
    }

    private static long ParseId(string id) =>
        id.Length >= 2 && long.TryParse(id[1..], out var v)
            ? v
            : 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Process? process;
        lock (_stateGate)
        {
            process = _process;
            _process = null;
        }

        if (process is null || process.HasExited)
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            return;
        }

        // 优雅优先：tool.stop 应答后索引进程自行退出（有落盘收尾）；
        // 等不满宽限期再整树 kill —— 与 ezt core stop 同款两级策略。
        try
        {
            _stdin!.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "stop",
                ["method"] = "tool.stop",
            }.ToJsonString()).GetAwaiter().GetResult();
            _stdin.Flush();
        }
        catch
        {
            // review-guards:allow-empty-catch :: 管道已断 = 进程已死，kill 兜底即可
        }

        if (!process.WaitForExit(GracefulStopTimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // review-guards:allow-empty-catch :: 强杀失败说明进程已自行退出 —— 目标已达成
            }
        }

        process.Dispose();
        _stdin?.Dispose();
        _stdout?.Dispose();
        _roundTrip.Dispose();
    }
}
