// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Desktop;

/// <summary>
/// 单实例守卫。
///
/// **为什么必须有**：托盘是"进程内宿主"，两个实例会各持一个 <c>ToolHostManager</c>
/// —— 同一个工具可能被起两份，而 <c>state.json</c>、配置与日志会互相覆盖。
/// 设计文档 §6.2 把"配置/私有数据/代码三处分离"定为目标，前提就是**只有一个写入者**。
///
/// 用 Mutex 而不是"找已有窗口"：托盘没有主窗口，进程之间没有天然的会合点。
/// 前缀用 <c>Local\</c>（每登录会话一个）—— 多用户/多会话下各人一个托盘是合理的，
/// 因为配置与状态本来就是按用户存的。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// 第二个实例的退出码。沿用既有的退出码约定（Cli：未找到命令 2 / 未找到工具 4），
    /// 让脚本能凭退出码区分"已经在跑"与"启动失败"。
    /// </summary>
    public const int AlreadyRunningExitCode = 3;

    private const string MutexName = @"Local\Eztools.Desktop.SingleInstance";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>拿到返回实例；已有实例在跑则返回 <c>null</c>。</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        // 没拿到就别留着 —— 它的句柄不属于我们
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 只有持有者能释放。走到这里说明线程归属和获取时不一致（不该发生），
            // 但**不能因此让进程退不出去** —— 忽略即可，句柄随进程结束回收。
        }

        _mutex.Dispose();
    }
}
