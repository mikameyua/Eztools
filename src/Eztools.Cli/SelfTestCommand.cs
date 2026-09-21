using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eztools.Contracts;
using Eztools.Host;

namespace Eztools.Cli;

/// <summary>
/// 端到端自检：把 spike 阶段的验证用例变成正式回归。
///
/// 为什么值得正式化：spike 里 9 个用例有 2 个在首次运行时就失败了（BOM 导致首帧丢失、
/// PATHONPATH 泄漏），而且两者的症状都与原因相隔很远。这类坑只有靠"每次都能一键重跑"
/// 才能保证不在重构中复发。
///
/// 用例分组：
/// <list type="number">
/// <item><b>链路</b>：清单发现 → 命令解析 → 进程启动 → JSON-RPC 往返</item>
/// <item><b>编码</b>：中文 + emoji + 引号 + 反斜杠 + 制表符逐字符无损</item>
/// <item><b>分帧</b>：1MB payload 不截断</item>
/// <item><b>隔离</b>：<c>-I</c> 生效（父进程 PYTHONPATH 未泄漏、utf8_mode=1）</item>
/// <item><b>生命周期</b>：崩溃被检测、挂起被超时回收、无进程残留</item>
/// <item><b>零宿主改动</b>：清单新声明的工具立即可被调用</item>
/// <item><b>启用语义</b>：禁用阻止启动但不注销清单</item>
/// </list>
/// </summary>
internal static class SelfTestCommand
{
    private static int _pass;
    private static int _fail;
    private static readonly JsonArray Results = new();

    public static async Task<int> RunAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");
        _pass = 0;
        _fail = 0;
        Results.Clear();

        // 共用 Program 的宿主工厂（原先这里有一份私有副本，三份 HostOptions 会漂移）
        await using var host = await Program.CreateHostAsync(
            cli, echoLog: cli.GetBool("verbose"), hostName: "ezt-selftest");

        if (!cli.GetBool("json"))
        {
            ConsoleUi.Header("Eztools 端到端自检");
            ConsoleUi.Field("安装根", host.Paths.Root);
            ConsoleUi.Field("工具源", string.Join(" ; ", host.Registry.Sources.Select(s => s.Path)));
        }

        // 0) 运行时前置：自检必须跑在真实运行时上
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("运行时");
        }

        var provision = await host.EnsureRuntimeAsync("python", null, message =>
        {
            if (!cli.GetBool("json"))
            {
                ConsoleUi.Info(message);
            }
        });

        if (provision.Installation is null)
        {
            Report("运行时可用", false, provision.Error?.Split('\n')[0]);
            if (!cli.GetBool("json"))
            {
                ConsoleUi.Error(provision.Error ?? "运行时不可用");
            }

            return Finish(cli);
        }

        Report("运行时可用", true,
            $"{provision.Installation.Runtime} {provision.Installation.Version} " +
            $"({(provision.Installation.IsEmbedded ? "隔离" : "外部覆盖")})");

        // 1) 清单发现
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("清单发现");
        }

        var registry = host.Registry;
        Report("清单驱动发现工具", registry.Tools.Count > 0, $"发现 {registry.Tools.Count} 个");

        var declaredCommandCount = registry.Tools.Sum(t => t.Manifest.Contributes.Commands.Count);
        Report("贡献点被解析为可调用命令", registry.Commands.Count() == declaredCommandCount,
            $"{registry.Commands.Count()} / {declaredCommandCount}");

        // 2) 往返 + 编码 + 分帧
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("协议链路");
        }

        if (registry.TryResolveCommand("echo.echo", out _))
        {
            await RunEchoCasesAsync(host);
        }
        else
        {
            Report("echo.echo 可用", false, "未找到命令，跳过编码与分帧用例");
        }

        // 3) 环境隔离
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("环境隔离");
        }

        await RunIsolationCaseAsync(host);

        // 4) 生命周期
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("进程生命周期");
        }

        var pythonBefore = CountPythonProcesses();
        await RunCrashCaseAsync(host);
        await RunHangCaseAsync(host);

        // 4B) 生命周期：resident / task（P2 尾巴）
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("生命周期（resident / task）");
        }

        await RunLifecycleCasesAsync(host);

        // 5) 独立工具正确性（新增工具，宿主零改动）
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("独立工具（宿主零改动即可调用）");
        }

        await RunFileHashCaseAsync(host);

        // 6) 启用语义
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("启用 / 已加载 语义");
        }

        await RunEnableSemanticsCaseAsync(host);

        // 6B) 宿主内部事件总线（P4 Wave 2a，决策 D2）
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("宿主内部事件总线");
        }

        RunEventBusCases(host);

        // 7) 进程残留
        if (!cli.GetBool("json"))
        {
            ConsoleUi.Section("进程回收");
        }

        await Task.Delay(1200); // 给 kill 一点时间落地
        var pythonAfter = CountPythonProcesses();
        // resident 工具在宿主存活期就该常驻（Dispose 时才回收）——计数豁免常驻数量
        var residentExpected = registry.Tools.Count(t =>
            t.Enabled && t.Manifest.Lifecycle == ToolLifecycle.Resident);
        Report("无工具进程残留（resident 常驻除外）", pythonAfter <= pythonBefore + residentExpected,
            $"自检前 {pythonBefore} 个 python 进程，自检后 {pythonAfter} 个" +
            $"（resident 常驻 {residentExpected} 个，宿主退出时回收）");

        return Finish(cli);
    }

    // ── 生命周期（resident / task）──

    private static async Task RunLifecycleCasesAsync(EztoolsHost host)
    {
        if (!host.Registry.TryGetTool("keepalive", out _))
        {
            Report("resident 验收工具可用", false, "未找到 keepalive（tools/keepalive）");
            return;
        }

        // L1 启动钩子：task 执行一次 + resident 拉起（单工具失败在钩子内逐个吞掉）
        Exception? startupError = null;
        try
        {
            await host.Processes.RunStartupLifecycleAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            startupError = ex;
        }

        Report("生命周期启动钩子（task 执行 + resident 拉起）", startupError is null,
            startupError?.Message ?? "RunStartupLifecycleAsync 无异常返回");

        // L2 resident 跨调用复用同一进程（pid 不变、进程内计数递增）
        var ping1 = await host.Processes.InvokeCommandAsync("keepalive.ping", null);
        var pid1 = ping1.Result?["pid"]?.GetValue<int>();
        var calls1 = ping1.Result?["calls"]?.GetValue<int>();
        var ping2 = await host.Processes.InvokeCommandAsync("keepalive.ping", null);
        var pid2 = ping2.Result?["pid"]?.GetValue<int>();
        var calls2 = ping2.Result?["calls"]?.GetValue<int>();
        Report("resident 跨调用复用同一进程（pid 不变）",
            pid1 is int p1 && pid2 == p1, $"pid {pid1} → {pid2}");
        Report("resident 进程内状态保持（计数递增）",
            calls1 is int c1 && calls2 == c1 + 1, $"calls {calls1} → {calls2}");

        // L3 崩溃 → 自动重启（新 pid）。resident 熔断预算 5 分钟 2 次，单次崩溃不会触发。
        var crashed = false;
        try
        {
            await host.Processes.InvokeCommandAsync("keepalive.crash", null);
        }
        catch (ToolCrashedException)
        {
            crashed = true;
        }

        Report("resident 崩溃被检测", crashed, crashed ? "exit=3" : "未抛 ToolCrashedException");

        int? newPid = null;
        for (var i = 0; i < 20 && newPid is null; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                var ping3 = await host.Processes.InvokeCommandAsync("keepalive.ping", null);
                newPid = ping3.Result?["pid"]?.GetValue<int>();
            }
            catch
            {
                // 自动重启尚未完成，继续轮询（上限 10 秒）
            }
        }

        Report("resident 崩溃后自动重启（新 pid）",
            newPid is int np && pid1 is int oldPid && np != oldPid,
            $"pid {pid1} → {newPid?.ToString() ?? "<未恢复>"}");

        // L4 task 执行凭证：selftest 宿主日志里应有 tasktool 的执行记录
        var selftestLog = Path.Combine(
            host.Paths.LogsDir, $"host-ezt-selftest-{DateTime.Now:yyyyMMdd}.log");
        var taskDone = File.Exists(selftestLog)
                       && File.ReadAllText(selftestLog).Contains("task 工具 tasktool 执行完成");
        Report("task 工具由启动钩子执行（日志凭证）", taskDone, selftestLog);

        // L5 状态快照：托盘"常驻服务"菜单的数据源接口
        var snapshot = host.Processes.GetResidentSnapshot();
        var kaSnapshot = snapshot.FirstOrDefault(s => s.ToolId == "keepalive");
        Report("resident 状态快照可用（托盘数据源）",
            kaSnapshot is { Running: true, Pid: not null, State: "运行中" },
            kaSnapshot is null
                ? "未发现 keepalive 快照"
                : $"keepalive：{kaSnapshot.State}，pid={kaSnapshot.Pid}，崩溃 {kaSnapshot.CrashCount} 次");
    }

    // ── 用例实现 ──

    private static async Task RunEchoCasesAsync(EztoolsHost host)
    {
        // 2.1 基本往返
        var basic = await host.Processes.InvokeCommandAsync("echo.echo", new JsonObject { ["text"] = "hello" });
        var echo = basic.Result?["echo"]?.GetValue<string>();
        Report("命令往返（echo.echo）", echo == "hello", $"返回 {echo ?? "<null>"}，用时 {basic.Elapsed.TotalMilliseconds:0}ms");

        // 2.2 中文 + emoji + 引号 + 反斜杠 + 制表符
        const string tricky = "中文测试 · emoji 🍡 · 引号 \" 单引号 ' 反斜杠 \\ 制表\t结尾 · 日文かな · 한글";
        var utf8Case = await host.Processes.InvokeCommandAsync("echo.echo", new JsonObject { ["text"] = tricky });
        var echoed = utf8Case.Result?["echo"]?.GetValue<string>();
        Report("UTF-8 中文/emoji 逐字符无损", echoed == tricky, echoed == tricky ? null : $"实际: {Truncate(echoed)}");

        // 2.3 1MB payload
        var big = new string('A', 512 * 1024) + "中文" + new string('B', 512 * 1024);
        var bigCase = await host.Processes.InvokeCommandAsync("echo.echo", new JsonObject { ["text"] = big },
            TimeSpan.FromSeconds(30));
        var bigEcho = bigCase.Result?["echo"]?.GetValue<string>();
        Report($"1MB payload 不截断（{bigEcho?.Length ?? 0}/{big.Length} 字符）", bigEcho == big);

        // 2.4 工具 → 宿主 的反向调用（host.log / host.storage 的完整往返）
        await RunHostCallbackCaseAsync(host);
    }

    private static async Task RunHostCallbackCaseAsync(EztoolsHost host)
    {
        if (!host.Registry.TryResolveCommand("probe.storage", out _))
        {
            Report("工具 → 宿主 反向调用", false, "未找到 probe.storage");
            return;
        }

        var result = await host.Processes.InvokeCommandAsync("probe.storage", new JsonObject());
        var roundTrip = result.Result?["roundTrip"]?.GetValue<bool>() == true;

        Report("工具 → 宿主 反向调用（host.storage 往返）", roundTrip,
            roundTrip ? null : $"读回值与写入值不一致: {result.Result?["read"]} / {result.Result?["wrote"]}");

        // 私有存储应落在工具数据目录，与工具代码目录分离
        var storageFile = host.Paths.ToolStorageFile("probe");
        Report("工具私有存储落在数据目录（与代码分离）", File.Exists(storageFile), storageFile);
    }

    private static async Task RunIsolationCaseAsync(EztoolsHost host)
    {
        if (!host.Registry.TryResolveCommand("probe.env", out _))
        {
            Report("探测工具可用", false, "未找到 probe.env");
            return;
        }

        var result = await host.Processes.InvokeCommandAsync("probe.env", new JsonObject());
        var r = result.Result;

        if (r is null)
        {
            Report("probe.env 返回", false, "空结果");
            return;
        }

        var isolated = r["isolated"]?.GetValue<int>() == 1;
        var utf8 = r["utf8Mode"]?.GetValue<int>() == 1;
        var leaked = r["pythonPathLeaked"]?.GetValue<bool>() == true;
        var sitePackages = r["sitePackagesOnPath"] as JsonArray;
        var sdkVersion = r["sdkVersion"]?.GetValue<string>();

        Report("隔离模式生效（-I：isolated=1）", isolated, isolated ? null : "sys.flags.isolated != 1");
        Report("强制 UTF-8（-X utf8：utf8_mode=1）", utf8, utf8 ? null : "sys.flags.utf8_mode != 1");
        Report("父进程 PYTHONPATH 未泄漏", !leaked, leaked ? "PYTHONPATH 出现在 sys.path 中" : null);
        Report("SDK 从运行时时 site-packages 加载", sitePackages is { Count: > 0 } && !string.IsNullOrEmpty(sdkVersion),
            $"sdk={sdkVersion ?? "未报告"}，site-packages 条目 {sitePackages?.Count ?? 0}");

        if (!ConsoleUi.JsonMode)
        {
            ConsoleUi.Info($"工具侧 Python: {r["python"]?.GetValue<string>()}");
            ConsoleUi.Info($"工具侧可执行: {r["executable"]?.GetValue<string>()}");
            ConsoleUi.Info($"工具侧标准输出编码: {r["stdoutEncoding"]?.GetValue<string>()}");
        }
    }

    private static async Task RunCrashCaseAsync(EztoolsHost host)
    {
        if (!host.Registry.TryResolveCommand("probe.crash", out _))
        {
            Report("崩溃检测", false, "未找到 probe.crash");
            return;
        }

        try
        {
            await host.Processes.InvokeCommandAsync("probe.crash", new JsonObject { ["exitCode"] = 3 });
            Report("工具主动崩溃被检测", false, "未抛出异常（宿主误以为调用成功）");
        }
        catch (ToolCrashedException ex)
        {
            Report("工具主动崩溃被检测", true, $"exit={ex.ExitCode}");
        }
        catch (ToolProtocolException ex)
        {
            Report("工具主动崩溃被检测", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // 崩溃后该工具应仍可再次启动（单次崩溃不该熔断）
        try
        {
            await host.Processes.InvokeCommandAsync("probe.echo", new JsonObject { ["text"] = "after-crash" });
            Report("崩溃后工具仍可重启（未误熔断）", true);
        }
        catch (Exception ex)
        {
            Report("崩溃后工具仍可重启（未误熔断）", false, ex.Message);
        }
    }

    private static async Task RunHangCaseAsync(EztoolsHost host)
    {
        if (!host.Registry.TryResolveCommand("probe.hang", out _))
        {
            Report("超时回收", false, "未找到 probe.hang");
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await host.Processes.InvokeCommandAsync("probe.hang", new JsonObject { ["seconds"] = 600 },
                TimeSpan.FromSeconds(3));
            Report("工具挂起被超时中断", false, "未超时");
        }
        catch (ToolTimeoutException)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            Report("工具挂起被超时中断", elapsed < TimeSpan.FromSeconds(20),
                $"{elapsed.TotalSeconds:0.0}s 后放弃等待（含进程回收）");
        }
        catch (Exception ex)
        {
            Report("工具挂起被超时中断", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task RunFileHashCaseAsync(EztoolsHost host)
    {
        if (!host.Registry.TryResolveCommand("filehash.hash", out var binding))
        {
            Report("新增工具被自动发现", false, "未找到 filehash.hash");
            return;
        }

        Report("新增工具被自动发现（未改宿主任何代码）", true,
            $"{binding.Tool.Id} v{binding.Tool.Manifest.Version}，声明 {binding.Tool.Manifest.Contributes.Commands.Count} 条命令");

        var sample = ResolveSampleFile(host);
        var result = await host.Processes.InvokeCommandAsync("filehash.hash", new JsonObject
        {
            ["path"] = sample,
            ["algorithm"] = "sha256",
        });

        var actual = result.Result?["hash"]?.GetValue<string>();
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample))).ToLowerInvariant();

        Report("工具计算结果的独立校验（与 .NET SHA256 对比）", actual == expected,
            actual == expected ? $"{actual?[..16]}…（{Path.GetFileName(sample)}）" : $"工具 {actual} ≠ 期望 {expected}");

        // 配置 schema 的声明也应被解析（P1 渲染设置页的依据）
        Report("配置 schema 被解析", binding.Tool.Manifest.HasConfigSchema,
            binding.Tool.Manifest.HasConfigSchema ? $"声明 {binding.Tool.Manifest.ConfigSchema!["properties"]!.AsObject().Count} 个配置项" : null);
    }

    private static async Task RunEnableSemanticsCaseAsync(EztoolsHost host)
    {
        if (!host.Registry.TryGetTool("echo", out var echo))
        {
            Report("启用语义", false, "未找到 echo 工具");
            return;
        }

        host.SetEnabled("echo", false, "selftest");

        try
        {
            await host.Processes.InvokeCommandAsync("echo.echo", new JsonObject { ["text"] = "x" });
            Report("禁用后拒绝启动", false, "禁用状态下仍然启动了进程");
        }
        catch (ToolDisabledException)
        {
            Report("禁用后拒绝启动", true);
        }

        // 「已启用」与「已加载」是两件事：禁用不该把清单也注销掉
        var stillRegistered = host.Registry.TryGetTool("echo", out var echoStill) &&
                              echoStill.Manifest.Contributes.Commands.Count > 0;
        Report("禁用不注销清单（已启用 ≠ 已加载）", stillRegistered);

        host.SetEnabled("echo", true);

        var afterEnable = await host.Processes.InvokeCommandAsync("echo.echo", new JsonObject { ["text"] = "back" });
        Report("重新启用后恢复可用", afterEnable.Result?["echo"]?.GetValue<string>() == "back");
    }

    // ── 宿主内部事件总线（P4 Wave 2a，决策 D2）──

    /// <summary>
    /// 断言事件总线的**契约**而不是实现细节：订阅收到事件、退订后不再收到、
    /// 订阅者抛异常不反噬事件源、载荷字段正确。
    ///
    /// <b>为什么不只在托盘里"看着没事"就算过</b>：托盘是 GUI，自动化里跑不了；
    /// 而事件总线最危险的两个失败模式（<b>漏退订</b>导致已死对象继续收到事件、
    /// <b>坏订阅者拖垮事件源</b>）恰恰在"看着没事"的表面下悄悄发生。
    /// </summary>
    private static void RunEventBusCases(EztoolsHost host)
    {
        var bus = host.Events;
        var received = new List<HostEvent>();

        // 1) 订阅后能收到，且**同一事件源**（host.SetEnabled）真的会发事件 ——
        //    只测 Publish 是测了个寂寞：那没验证"状态变更点接上了总线"。
        using (var sub = bus.Subscribe(received.Add))
        {
            Report("订阅后 SubscriberCount 增加", bus.SubscriberCount >= 1,
                $"当前订阅者 {bus.SubscriberCount} 个");

            host.SetEnabled("echo", false, "selftest-event");
            Thread.Sleep(50);   // 事件是同步投递的，这里只是给日志一点落盘时间

            var gotEnable = received.FirstOrDefault(e => e.Kind == HostEventKind.ToolEnabledChanged);
            Report("启用/禁用状态变更被投递到总线", gotEnable is not null,
                gotEnable?.ToString());

            Report("事件载荷带正确 ToolId",
                gotEnable?.ToolId == "echo",
                $"ToolId={gotEnable?.ToolId ?? "<null>"}");

            Report("事件时间戳非默认值（未传 At 时自动取当前时间）",
                gotEnable is not null && gotEnable.Timestamp.Year >= 2020,
                gotEnable?.Timestamp.ToString("O"));

            host.SetEnabled("echo", true, null);
        }

        // 2) 退订后不再收到 —— 这是"订阅/退订必须成对"的守门断言
        received.Clear();
        bus.Publish(HostEventKind.RuntimeChanged, "probe", "退订后不该收到");
        Report("退订后不再收到事件", received.Count == 0,
            $"退订后收到 {received.Count} 条（应为 0）");

        // 3) 坏订阅者不反噬事件源：一个抛异常的订阅者不能影响后续订阅者与调用方
        var healthyCount = 0;
        using (bus.Subscribe(_ => throw new InvalidOperationException("selftest 故意抛的")))
        using (bus.Subscribe(_ => healthyCount++))
        {
            var threw = false;
            try
            {
                bus.Publish(HostEventKind.RuntimeChanged, "probe", "坏订阅者隔离测试");
            }
            catch
            {
                threw = true;
            }

            Report("坏订阅者不把异常抛回事件源", !threw);
            Report("坏订阅者不影响后续订阅者", healthyCount == 1,
                $"健康订阅者收到 {healthyCount} 条（应为 1）");
        }

        // 4) 快照投递：订阅者在回调里退订自己不该引发"集合被修改"
        var selfUnsubCount = 0;
        IDisposable? selfSub = null;
        selfSub = bus.Subscribe(_ =>
        {
            selfUnsubCount++;
            selfSub!.Dispose();   // 回调里退订自己
        });

        var snapshotOk = true;
        try
        {
            bus.Publish(HostEventKind.RuntimeChanged, "probe", "自退订测试");
            bus.Publish(HostEventKind.RuntimeChanged, "probe", "第二次不该送达");
        }
        catch
        {
            snapshotOk = false;
        }

        Report("订阅者在回调里退订不破坏投递", snapshotOk);
        Report("自退订的订阅者只收到一次", selfUnsubCount == 1,
            $"收到 {selfUnsubCount} 次（应为 1）");
    }

    // ── 辅助 ──

    private static string ResolveSampleFile(EztoolsHost host)
    {
        var candidates = new List<string>();

        // 优先用仓库里的设计文档：一定存在，且含中文（顺带验证非 ASCII 路径与内容）
        if (Eztools.Host.AppLayout.FindRepoRoot() is { } repoRoot)
        {
            candidates.Add(Path.Combine(repoRoot, "docs", "Eztools-设计方案.md"));
            candidates.Add(Path.Combine(repoRoot, "Directory.Build.props"));
        }

        candidates.Add(typeof(SelfTestCommand).Assembly.Location);
        candidates.Add(host.Paths.StateFile);

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        // 兜底：造一个
        var temp = Path.Combine(Path.GetTempPath(), "ezt-selftest-sample.txt");
        File.WriteAllText(temp, "eztools selftest sample 中文\n", new UTF8Encoding(false));
        return temp;
    }

    private static int CountPythonProcesses()
    {
        try
        {
            return Process.GetProcessesByName("python").Length;
        }
        catch
        {
            return 0;
        }
    }

    private static void Report(string what, bool ok, string? detail = null)
    {
        Results.Add(new JsonObject
        {
            ["case"] = what,
            ["ok"] = ok,
            ["detail"] = detail,
        });

        if (ok)
        {
            _pass++;
            ConsoleUi.Pass(what + (detail is null ? "" : $"  ({detail})"));
        }
        else
        {
            _fail++;
            ConsoleUi.Fail(what, detail);
        }
    }

    private static string Truncate(string? text) =>
        text is null ? "<null>" : text.Length <= 80 ? text : text[..80] + "…";

    private static int Finish(CliArgs cli)
    {
        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["pass"] = _pass,
                ["fail"] = _fail,
                ["cases"] = JsonNode.Parse(Results.ToJsonString()),
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Header("自检结果");
            ConsoleUi.Field("通过", _pass.ToString());
            ConsoleUi.Field("失败", _fail.ToString());

            if (_fail == 0)
            {
                ConsoleUi.Ok("全部通过：链路、编码、隔离、生命周期、零宿主改动、启用语义均符合设计");
            }
            else
            {
                ConsoleUi.Error($"{_fail} 个用例失败，详见上方 [FAIL] 行。日志见 logs 目录。");
            }
        }

        return _fail == 0 ? 0 : 1;
    }

    private static JsonArray DeepCloneJson(this JsonArray array) =>
        (JsonArray)JsonNode.Parse(array.ToJsonString())!;
}
