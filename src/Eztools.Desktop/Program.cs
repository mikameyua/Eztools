// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Forms;

namespace Eztools.Desktop;

/// <summary>
/// 桌面宿主入口（WinExe）。
///
/// 与 <c>ezt</c> CLI 共用同一个 <c>Eztools.Host</c> 门面 —— 两者只是同一宿主的不同前端，
/// 不存在两套逻辑。这里只负责三件与 GUI 相关的事：单实例、DPI/视觉样式、消息循环。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = DesktopOptions.Parse(args);

        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // 已有托盘在跑。给一句反馈 —— 双击第二次却毫无反应很容易被当成"程序坏了"，
            // 但模态框会阻塞自动化，所以留了 --no-prompt 开关。
            if (!options.NoPrompt)
            {
                WinForms.MessageBox.Show(
                    "Eztools 已在运行。\n\n图标在任务栏通知区域（可能在溢出区，点小三角可看到）。",
                    "Eztools",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return SingleInstance.AlreadyRunningExitCode;
        }

        using (instance)
        {
            // 手写而不用 ApplicationConfiguration.Initialize()：
            // 后者依赖 SDK 的源码生成器，写明白更不容易在换 SDK 时出意外。
            WinForms.Application.EnableVisualStyles();
            WinForms.Application.SetCompatibleTextRenderingDefault(false);
            WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.PerMonitorV2);

            using var app = new TrayApplication(options);
            return app.Run();
        }
    }
}
