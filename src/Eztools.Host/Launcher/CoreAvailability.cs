// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Eztools.Contracts;

namespace Eztools.Host.Launcher;

/// <summary>
/// 核心服务可达性（W8·B1）—— 把"索引为什么没就绪"从**一个布尔**拆成可判定的三态。
///
/// <para><b>为什么需要它</b>：索引侧 <c>if (!service.Ready)</c> 是一个布尔，而 <c>Ready=false</c>
/// 有两种完全不同的成因 —— "真的在建（等一会就好）"与"核心服务缺席所以根本建不了（等到天荒地老也不会好）"。
/// 两者压成同一个 <c>-32001</c> 之后，界面只能一律说"正在建索引…"，用户既等不到也不知道该做什么。
/// 判定所需的全部事实（端点登记 + 进程存活 + 是否提权）**本来就在本机可读**，只是从没被读过。</para>
///
/// <para><b>判据的保守性</b>：<see cref="Unknown"/> 是**刻意保留**的第四态，不是失败分支的兜底 ——
/// 探测不确定时若谎报"核心服务没跑"，用户会去反复点一个没用的按钮（比原来更差）。
/// 消费方（<c>FilesProvider.MapError</c>）对 <see cref="Unknown"/> 一律回落到既有文案，
/// 即"最坏结果只是多等一会儿"，不产生新的误导。</para>
/// </summary>
public enum CoreAvailability
{
    /// <summary>核心服务在跑且已提权 ⇒ "正在建索引"这句是真话，用户等即可。</summary>
    CoreOk,

    /// <summary>无端点登记，或登记了但进程已不在 ⇒ 索引建不起来，且**不会自己好**。</summary>
    CoreNotRunning,

    /// <summary>核心服务在跑但未提权 ⇒ 读 MFT 会被拒，同样只能由用户动作解决。</summary>
    CoreNotElevated,

    /// <summary>探测本身无法下结论（端点文件读不出 / 进程名取不到）⇒ 消费方必须保守处理。</summary>
    Unknown,
}

/// <summary>
/// 端点登记的 pid 与实际进程的对应关系（探测的中间结论，**独立成态**是因为
/// "pid 活着"与"那个 pid 就是核心服务"是两件事 —— 前者会被 pid 复用骗到）。
/// </summary>
public enum CoreProcessState
{
    /// <summary>取不到进程信息（已退出但抛的是别的异常 / 进程名需要权限而拿不到）。</summary>
    Unknown,

    /// <summary>进程不存在（端点陈旧）。</summary>
    Dead,

    /// <summary>pid 活着，且进程名就是核心服务。</summary>
    AliveSameName,

    /// <summary>pid 活着，但属于别的进程 ⇒ 核心服务已退出、这个 pid 被系统复用了。</summary>
    AliveOtherName,
}

/// <summary>
/// 探测原料（**纯数据、零 IO**）。判定（<see cref="CoreAvailabilityRules.Decide"/>）只看它 ——
/// 所以三态逻辑可以被 selftest 以全真值表覆盖，不需要构造真实进程或端点文件。
/// </summary>
public readonly record struct CoreProbeRaw(
    bool EndpointPresent,
    CoreProcessState Process,
    bool Elevated);

/// <summary>
/// 三态判定（**纯函数**）。采集与判定分离是本设计的核心：判定能被机器穷举，采集才需要真实环境。
/// </summary>
public static class CoreAvailabilityRules
{
    /// <summary>核心服务的进程名（<c>Process.ProcessName</c> 不含扩展名）。</summary>
    public const string CoreProcessName = "ezt-core";

    /// <summary>
    /// 由原料算出可达性。
    ///
    /// <para>真值表（selftest 逐格覆盖）：
    /// 端点缺失 ⇒ 未运行；
    /// 端点陈旧（pid 已死）⇒ 未运行；
    /// pid 被别的进程占用 ⇒ 未运行（★ 不看进程名就会把复用当成"在跑"）；
    /// 进程名相符但未提权 ⇒ 未提权；
    /// 进程名相符且已提权 ⇒ 就绪；
    /// 进程信息取不到 ⇒ 未知（保守）。</para>
    /// </summary>
    public static CoreAvailability Decide(CoreProbeRaw raw)
    {
        if (!raw.EndpointPresent)
        {
            return CoreAvailability.CoreNotRunning;
        }

        return raw.Process switch
        {
            CoreProcessState.Dead => CoreAvailability.CoreNotRunning,
            CoreProcessState.AliveOtherName => CoreAvailability.CoreNotRunning,
            CoreProcessState.AliveSameName when !raw.Elevated => CoreAvailability.CoreNotElevated,
            CoreProcessState.AliveSameName => CoreAvailability.CoreOk,
            _ => CoreAvailability.Unknown,
        };
    }
}

/// <summary>
/// 可达性探测（W8·B1）—— **只读端点文件与进程元数据，不连管道、不写任何东西**（宿主零特权）。
///
/// <para><b>成本与时机</b>：一次探测 = 一次文件存在性判断 + 一次小 JSON 读取 + 一次进程查询，
/// 微秒~毫秒级。但它**只在已经出错之后**才被调用（<c>FilesProvider</c> 收到 <c>-32001</c> 时），
/// 因此正常路径（查询成功）零开销 —— 这也是菜单构建路径唯一安全的取状态方式。</para>
///
/// <para><b>2 秒缓存</b>：上界由"用户点了启动、核心服务起来后 UI 要尽快改口"决定（再长会让用户
/// 觉得点了没用）；下界由"连续打字不该每敲一个字符都读一次盘"决定。2 秒 ≈ 一次打字停顿的尺度。</para>
/// </summary>
public sealed class CoreAvailabilityProbe
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    private readonly string _installRoot;
    private readonly object _gate = new();
    private CoreAvailability _cached = CoreAvailability.Unknown;
    private long _cachedAtMs;

    public CoreAvailabilityProbe(string installRoot)
    {
        _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
    }

    /// <summary>取当前可达性（命中缓存则不发问）。线程安全（在途查询的回调线程上调用）。</summary>
    public CoreAvailability Snapshot()
    {
        lock (_gate)
        {
            if (_cachedAtMs != 0 && Environment.TickCount64 - _cachedAtMs < (long)CacheTtl.TotalMilliseconds)
            {
                return _cached;
            }
        }

        var value = CoreAvailabilityRules.Decide(Collect(_installRoot));

        lock (_gate)
        {
            _cached = value;
            _cachedAtMs = Environment.TickCount64;
        }

        return value;
    }

    /// <summary>
    /// 作废缓存。**启动成功后必须调** —— 否则界面还在念 2 秒前的旧结论，
    /// 用户会以为"点了没用"。
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cachedAtMs = 0;
        }
    }

    /// <summary>
    /// 采集原料（**本类唯一的 IO 点**）。任何异常都在这里被吸收成
    /// <see cref="CoreProcessState.Unknown"/> / 保守结果 —— 探测失败不能反过来把查询路径搞崩。
    /// </summary>
    internal static CoreProbeRaw Collect(string installRoot)
    {
        try
        {
            if (!File.Exists(CoreEndpoint.EndpointFile(installRoot)))
            {
                return new CoreProbeRaw(EndpointPresent: false, CoreProcessState.Unknown, false);
            }

            var info = CoreEndpoint.TryRead(installRoot);
            if (info is null)
            {
                // 文件在但读不出（半写/损坏）⇒ 说不清 ⇒ 未知。**不冒充"未运行"** ——
                // 那会给出一个点了没用的按钮（见 CoreAvailability 的保守性说明）。
                return new CoreProbeRaw(EndpointPresent: true, CoreProcessState.Unknown, false);
            }

            return new CoreProbeRaw(true, Classify(info.Pid), info.Elevated);
        }
        catch
        {
            return new CoreProbeRaw(EndpointPresent: false, CoreProcessState.Unknown, false);
        }
    }

    /// <summary>
    /// 端点里登记的 pid 现在是谁。★ <b>必须连进程名一起看</b>：pid 会被系统复用，
    /// 只看"pid 活着"就会把复用的无关进程当成核心服务在跑 ⇒ 又回到"说正在建索引"的老谎。
    /// </summary>
    internal static CoreProcessState Classify(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                return CoreProcessState.Dead;
            }

            string name;
            try
            {
                name = proc.ProcessName;
            }
            catch
            {
                // 进程在但名字取不到（权限）⇒ 不下结论。保守方向：宁可说"未知"（回落旧文案），
                // 也不说"没在跑"（给出无用的启动按钮）。
                return CoreProcessState.Unknown;
            }

            return string.Equals(name, CoreAvailabilityRules.CoreProcessName, StringComparison.OrdinalIgnoreCase)
                ? CoreProcessState.AliveSameName
                : CoreProcessState.AliveOtherName;
        }
        catch (ArgumentException)
        {
            return CoreProcessState.Dead;   // GetProcessById 对不存在的 pid 抛这个
        }
        catch
        {
            return CoreProcessState.Unknown;
        }
    }
}
