// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using Eztools.Contracts;
using Eztools.Host;
using Eztools.Host.Primitives;

namespace Eztools.Desktop;

/// <summary>启动核心服务的结果。消费方据它决定状态行回显什么（W8·B1 的 FR-5）。</summary>
internal enum CoreLaunchOutcome
{
    /// <summary>已启动并登记了端点 ⇒ 索引侧应重建（重新自举）。</summary>
    Launched,

    /// <summary>本来就在跑（幂等 —— 与 <c>ezt core start</c> 同语义）。</summary>
    AlreadyRunning,

    /// <summary>用户在 UAC 弹窗上点了"否"或直接关掉了它。<b>这是用户决策，不是错误</b> —— 只回显、不重试、不催。</summary>
    Cancelled,

    /// <summary>启动失败（找不到 exe / 进程提前退出 / 其它异常）。</summary>
    Failed,

    /// <summary>进程起来了但规定时间内没登记端点（可能被安全软件拦下，或机器极慢）。</summary>
    NotRegistered,
}

/// <summary>
/// 提权启动核心服务（W8·B1 的可达出口）。
///
/// <para><b>为什么这一步必须在 Desktop 而不能在 Host/Index</b>：它是**用户动作的编排**
/// ——弹 UAC、等就绪、随后由托盘重建窗口。索引侧自举只在进程启动时跑一次且不重试
/// （<c>src/Eztools.Index/Program.cs</c>），所以"核心服务起来之后索引还得重启一次"
/// 是这套编排的一部分，而不是索引自己的事。</para>
///
/// <para><b>★ 红线：UAC 必过，禁静默拉起。</b>提权只能走 <c>UseShellExecute + Verb="runas"</c>
/// （由 Windows 弹框、由用户点"是"）。本项目**不存在也不得新增**任何后台/服务/计划任务式的
/// 静默提权路径 —— 用户取消即视为用户决策，只回显不重试。</para>
///
/// <para><b>★ 可判定部分全部抽成纯函数</b>（<see cref="BuildStartInfo"/> /
/// <see cref="MapLaunchFailure"/> / <see cref="DecideWaitStep"/> / <see cref="MessageFor"/>）：
/// "真起一个提权进程"没法自动化，但"**启动参数对不对、取消怎么识别、等到什么算成功**"
/// 全都能被机器穷举。其中"是不是真的走了 UAC 提权"是**一票否决级红线**（审查规范 §3.9），
/// 绝不该只靠人眼看弹窗 —— 观测面在 <c>--probe-launcher corestatus</c> 的 <c>launch</c> 段。
/// 本类因此只剩一个薄 IO 壳（<see cref="LaunchElevatedAsync"/>）。</para>
///
/// <para><b>为什么不用管道探测就绪</b>：宿主对核心服务零特权（只读端点文件，不连管道）。
/// 判定"起来了"= 端点文件出现且登记的 pid 活着，这对本场景足够 —— 本场景的入口前提正是
/// "核心服务缺席"，所以不存在"读到上一次留下的陈旧端点"这个歧义。</para>
/// </summary>
internal static class CoreLauncher
{
    /// <summary>UAC 被取消时 ShellExecute 抛的 Win32 码（ERROR_CANCELLED）。</summary>
    internal const int ErrorCancelled = 1223;

    /// <summary>等端点登记的轮询次数与间隔（40 × 200 ms ≈ 8 s，与 <c>ezt core start</c> 同口径）。</summary>
    private const int WaitAttempts = 40;
    private const int WaitIntervalMs = 200;

    // ── 可判定部分（纯函数；探针穷举，见 corestatus 的 launch 段）──────────────

    /// <summary>
    /// 构造提权启动参数 —— <b>这是"UAC 必过、禁静默拉起"的可断言形态</b>。
    ///
    /// <para><c>UseShellExecute=true</c> + <c>Verb="runas"</c> 是 Windows 弹 UAC 的**唯一正道**；
    /// 少了任何一个，拉起来的都是同令牌进程（提权 Core 的活一件也干不了，用户却看不到任何提示）。</para>
    ///
    /// <para>另外两条也是刻意的：<c>CreateNoWindow=false</c> + <c>WindowStyle=Hidden</c>
    /// （ShellExecute 下前者被忽略、后者生效 ⇒ 不闪黑窗）；不设任何重定向
    /// （ShellExecute 下设了会直接抛异常，而且重定向本来也收不到提权进程的输出）。</para>
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string exe, string installRoot) => new()
    {
        FileName = exe,
        Arguments = $"--root \"{installRoot}\"",
        UseShellExecute = true,
        Verb = "runas",
        CreateNoWindow = false,
        WindowStyle = ProcessWindowStyle.Hidden,
    };

    /// <summary>
    /// 启动异常 → 结果。用户取消 UAC 抛的是 <c>Win32Exception(1223)</c>（ERROR_CANCELLED）——
    /// **那是用户决策，不是错误**，所以给专门的 <see cref="CoreLaunchOutcome.Cancelled"/>，
    /// 文案也不能带"失败"字样。
    /// </summary>
    internal static (CoreLaunchOutcome Outcome, string Message) MapLaunchFailure(Exception ex) =>
        ex is Win32Exception { NativeErrorCode: ErrorCancelled }
            ? (CoreLaunchOutcome.Cancelled, MessageFor(CoreLaunchOutcome.Cancelled))
            : (CoreLaunchOutcome.Failed, FailedMessage(ex.Message));

    /// <summary>
    /// 等就绪的**单轮**决策，<c>null</c> = 继续等。
    ///
    /// <para>与"等多少轮"刻意分开：轮数上限是**策略**（改它不影响正确性），
    /// 单轮判定是**逻辑**（改它就改行为）—— 只有后者需要穷举。</para>
    /// </summary>
    /// <param name="exited">进程是否已退出。</param>
    /// <param name="exitCode">已退出时的退出码（未退出时忽略）。</param>
    /// <param name="endpointRegistered">端点文件已出现且登记的 pid 活着。</param>
    internal static CoreLaunchOutcome? DecideWaitStep(bool exited, int exitCode, bool endpointRegistered)
    {
        if (exited)
        {
            // exit=3 是核心服务自己的"已有实例在运行"约定（见 CoreCommand.StartAsync）
            return exitCode == 3 ? CoreLaunchOutcome.AlreadyRunning : CoreLaunchOutcome.Failed;
        }

        return endpointRegistered ? CoreLaunchOutcome.Launched : null;
    }

    /// <summary>
    /// 结局文案（**单点**）。状态行直接显示它，渲染层不拼字符串 ——
    /// 否则"用户取消了"迟早被写成"启动失败"（两者对用户的意义完全相反）。
    /// </summary>
    internal static string MessageFor(CoreLaunchOutcome outcome) => outcome switch
    {
        CoreLaunchOutcome.Launched => "核心服务已启动，正在重建索引…",
        CoreLaunchOutcome.AlreadyRunning => "核心服务已在运行",
        CoreLaunchOutcome.Cancelled => "已取消启动核心服务（搜索仍不可用）",
        CoreLaunchOutcome.NotRegistered => "核心服务已启动但未登记端点（可能被安全软件拦截）—— 请稍后重试",
        _ => FailedMessage("未知原因"),
    };

    /// <summary>失败文案（带原因）。</summary>
    internal static string FailedMessage(string reason) => $"启动核心服务失败：{reason}";

    // ── IO 壳 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 以管理员身份启动核心服务并等它就绪。
    ///
    /// <para>返回的 <c>Message</c> 是**给用户看的一句话**（状态行直接显示）。</para>
    /// </summary>
    internal static async Task<(CoreLaunchOutcome Outcome, string Message)> LaunchElevatedAsync(
        EztoolsPaths paths, CancellationToken ct = default)
    {
        // 幂等：已经在跑就不重复拉（与 ezt core start 同语义 —— 脚本/连点友好）
        if (CoreEndpoint.TryRead(paths.Root) is { } existing && IsPidAlive(existing.Pid))
        {
            return (CoreLaunchOutcome.AlreadyRunning, MessageFor(CoreLaunchOutcome.AlreadyRunning));
        }

        var exe = PrimitiveClient.FindCoreExe(paths);
        if (exe is null)
        {
            return (CoreLaunchOutcome.Failed,
                FailedMessage("找不到 ezt-core.exe（已安装形态应有 <安装根>\\bin\\ezt-core.exe）"));
        }

        Process? process;
        try
        {
            process = Process.Start(BuildStartInfo(exe, paths.Root));
        }
        catch (Exception ex)
        {
            return MapLaunchFailure(ex);
        }

        if (process is null)
        {
            return (CoreLaunchOutcome.Failed, FailedMessage("进程未创建"));
        }

        using (process)
        {
            for (var i = 0; i < WaitAttempts; i++)
            {
                try
                {
                    await Task.Delay(WaitIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (CoreLaunchOutcome.Failed, FailedMessage("已放弃等待核心服务就绪"));
                }

                var exited = process.HasExited;
                var exitCode = exited ? process.ExitCode : 0;
                var registered = !exited
                    && CoreEndpoint.TryRead(paths.Root) is { } ep
                    && IsPidAlive(ep.Pid);

                if (DecideWaitStep(exited, exitCode, registered) is not { } decided)
                {
                    continue;
                }

                // Failed 需要带原因（决策函数只知道"失败"，不知道退出码）
                return decided == CoreLaunchOutcome.Failed
                    ? (decided, FailedMessage($"核心服务提前退出（exit={exitCode}）—— 崩溃现场见安装根 logs"))
                    : (decided, MessageFor(decided));
            }
        }

        return (CoreLaunchOutcome.NotRegistered, MessageFor(CoreLaunchOutcome.NotRegistered));
    }

    /// <summary>进程是否还在（与 <c>CoreCommand</c> / <c>PrimitiveClient</c> 同款实现）。</summary>
    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
