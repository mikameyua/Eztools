// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Eztools.Index;

/// <summary>
/// 索引后台线程工厂（W3-e-1：长期常驻不打扰用户）。
///
/// <b>为什么不用 <c>Task.Run</c></b>：线程池线程是**共享**的 —— 改它的优先级会污染
/// 同一池里的其它任务（本项目自己的 RPC 服务核就跑在线程池上）。"索引空闲期不抢 IO"
/// 需要两个只对**专用线程**才有意义的东西：
/// <list type="number">
/// <item><see cref="Thread.Priority"/> = <see cref="ThreadPriority.BelowNormal"/>；
/// 线程体开头 <c>SetThreadPriority(THREAD_MODE_BACKGROUND_BEGIN)</c>（Win32 后台模式：
/// 磁盘/内存优先级降到最低，只在系统真的空闲时才满足 IO —— 前台应用无可感知卡顿）；</item>
/// <item>同步循环（见 <c>JournalTail.Run</c> 的说明）：async/await 的续体跳回线程池后
/// <b>不再带本线程的优先级</b>，优先级就白设了。</item>
/// </list>
///
/// <b>为什么不能对别的线程设</b>：<c>THREAD_MODE_BACKGROUND_BEGIN</c> 是**当前线程**语义
/// （文档明文），所以只能在线程体开头调 —— 这正是本工厂把调用包进线程体的原因。
/// </summary>
public static class IndexThreads
{
    /// <summary><c>THREAD_MODE_BACKGROUND_BEGIN</c>（winbase.h）= 0x0001_0000。
    /// 与之相对的是 <c>THREAD_MODE_BACKGROUND_END</c> = 0x0002_0000（本类不主动调：
    /// 线程即将结束，恢复与否无意义）。</summary>
    public const int ThreadModeBackgroundBegin = 0x0001_0000;

    /// <summary>最近一次线程体内的后台 IO 模式是否设置成功（断言观测量；Win32 失败原码见 <see cref="LastSetError"/>）。</summary>
    public static bool BackgroundIoSucceeded { get; private set; }

    /// <summary>最近一次 <c>SetThreadPriority</c> 失败的 Win32 错误码（成功 = 0）。</summary>
    public static int LastSetError { get; private set; }

    /// <summary>后台 IO 模式设置尝试过的次数（测试确认线程体真的跑过）。</summary>
    public static int Attempts { get; private set; }

    /// <summary>
    /// 最近一次起的线程被赋予的优先级（**在 Start 里记录**）。
    /// 为什么不能读 <c>thread.Priority</c>：线程一结束就读它会抛 <c>ThreadStateException</c>
    ///（"Thread is dead; priority cannot be accessed"）—— 探针/测试与线程退出天然有竞态。
    /// </summary>
    public static ThreadPriority LastPriority { get; private set; }

    /// <summary>
    /// 起一个索引后台线程（<c>IsBackground=true</c> 保证宿主退出时不吊住进程）。
    /// 线程体**先**进后台 IO 模式再执行 <paramref name="body"/>。
    /// </summary>
    public static Thread Start(string name, Action body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);

        var thread = new Thread(() =>
        {
            EnterBackgroundIo();
            body();
        })
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.BelowNormal,   // ① 托管侧优先级
        };

        LastPriority = thread.Priority;   // 线程活着时读，落静态（退出后读会抛）
        thread.Start();
        return thread;
    }

    /// <summary>
    /// 对**当前线程**进入 Win32 后台 IO 模式。返回是否成功；失败原因落在 <see cref="LastSetError"/>
    /// （不吞 —— 但调用方也不该因它失败而中止索引：这是"尽力而为的性能优化"，不是正确性前提）。
    /// </summary>
    public static bool EnterBackgroundIo()
    {
        Interlocked.Increment(ref _attempts);
        var ok = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        BackgroundIoSucceeded = ok;
        LastSetError = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    private static int _attempts;

    /// <summary>尝试次数（volatile 读）。</summary>
    public static int AttemptCount => Volatile.Read(ref _attempts);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
}

/// <summary>
/// 索引暂停闸（W3-e-1 ③"暂停索引"开关）。
///
/// <b>暂停的语义是"停止消费，不丢弃"</b>：tail 泵暂停期间**不读 journal**，游标原地不动 ——
/// 变更记录留在 journal 环形缓冲里，恢复时从游标一次性补齐。因此短暂停是**零丢失**的，
/// 唯一的边界是长时间暂停可能让 journal 回绕覆盖旧记录（已知限制，见设计方案）；
/// 回绕后的正确性由启动对账兜住（游标越界 ⇒ 全量重扫）。
///
/// <b>为什么是 volatile bool 而不是 ManualResetEvent</b>：泵的循环体本来就有空闲轮询
/// （500ms），复用同一节奏即可 —— 引入事件对象多一个可泄漏句柄，且取消路径要额外处理。
/// </summary>
public sealed class PauseGate
{
    private volatile bool _paused;
    private long _pausedPolls;

    /// <summary>当前是否暂停。</summary>
    public bool IsPaused => _paused;

    /// <summary>暂停。（幂等。）</summary>
    public void Pause() => _paused = true;

    /// <summary>恢复。（幂等。）</summary>
    public void Resume() => _paused = false;

    /// <summary>暂停期间被挡下的轮询次数（诊断 + 断言：证明"暂停真的挡住了消费"）。</summary>
    public long PausedPolls => Interlocked.Read(ref _pausedPolls);

    /// <summary>泵在暂停中空转一轮时调用。</summary>
    public void NotePausedPoll() => Interlocked.Increment(ref _pausedPolls);
}
