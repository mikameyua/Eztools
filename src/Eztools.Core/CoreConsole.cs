// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Core;

/// <summary>
/// 容错的控制台输出。Core 的 stdout/stderr 可能是父进程的重定向管道 ——
/// 父进程退出后管道断裂，此时 <see cref="Console.WriteLine"/> 会抛 IOException；
/// <b>控制台输出永远不该杀死特权服务</b>，所以所有输出都吞掉 IO 异常。
/// </summary>
internal static class CoreConsole
{
    public static void Say(string message)
    {
        try
        {
            Console.WriteLine(message);
        }
        catch
        {
            // review-guards:allow-empty-catch :: stdout 断了不致命
        }
    }

    public static void Error(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // review-guards:allow-empty-catch :: stderr 断了不致命
        }
    }
}
