// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Core;

/// <summary>
/// 容错的控制台输出。Core 的 stdout/stderr 可能是父进程的重定向管道 ——
/// 父进程退出后管道断裂，此时 <see cref="Console.WriteLine"/> 会抛 IOException；
/// <b>控制台输出永远不该杀死特权服务</b>，所以所有输出都吞掉 IO 异常。
///
/// <para><b>★ 但"能写"不等于"有人收得到"</b>（安全面专项审 RI-1 的教训）：实测
/// <c>ezt core start</c> 返回（约 1 秒）之后，它重定向的 stdout 管道就断了，
/// 之后所有 <see cref="Say"/> 都静默丢弃 —— <c>host-ezt-core-*.log</c> 里只剩启动那两行。
/// 提权形态更彻底：走 ShellExecute，stdout **从头到尾无人接收**。
/// 因此凡是需要**事后排查**的信息一律走 <see cref="Trace"/>（文件），不要走 Say。</para>
/// </summary>
internal static class CoreConsole
{
    /// <summary>trace 文件的落点（由 <c>CoreServer</c> 启动时绑定安装根；未绑定则 Trace 静默）。</summary>
    private static string? _traceDir;

    /// <summary>绑定 trace 目录（= 安装根的 logs）。由 CoreServer 构造时调用。</summary>
    internal static void BindTraceDir(string installRoot) =>
        _traceDir = Path.Combine(installRoot, "logs");

    /// <summary>
    /// 写一行到 <c>logs\core-trace.log</c>（按 1 MB 轮转，留 3 份历史）。
    ///
    /// <para>与 <see cref="Say"/> 的本质区别：<b>走文件，不依赖父进程的 stdout 是否还活着</b>，
    /// 也不受提权 ShellExecute 吞掉 stdout 的影响 ⇒ 这是 Core 里唯一"事后一定能读到"的出口。</para>
    /// </summary>
    internal static void Trace(string message)
    {
        var dir = _traceDir;
        if (dir is null)
        {
            return;
        }

        try
        {
            var file = Path.Combine(dir, "core-trace.log");

            // 轮转：Core 是长期驻留进程，trace 会持续增长，而它落在用户磁盘的安装根里 ——
            // 没有任何东西会替它收拾。1 MB ≈ 1 万行，足够回溯最近几次失败。
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
            File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [pid={Environment.ProcessId}] {message}\n");
        }
        catch
        {
            // review-guards:allow-empty-catch :: trace 失败无所谓
        }
    }

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
