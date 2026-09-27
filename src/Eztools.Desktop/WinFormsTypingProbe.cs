// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Eztools.Desktop;

/// <summary>
/// 判决性实验探针（--probe-winforms-typing，2026-09-27 键盘终局的换方案判决）：
/// 纯 WinForms Form + TextBox（Win32 EDIT 控件，与记事本同代技术）——
/// keybd_event 注入 "hi" → 断言 TextBox.Text == "hi"。
///
/// 背景：WPF TextBox 在本机键盘环境下逐键丢字（TSF 层吞 WM_KEYDOWN，多轮方案均失败）；
/// 本探针回答唯一问题：**同一环境里 WinForms EDIT 能否正常打字**——
/// 能 ⇒ 面板整体转 WinForms（脱离 WPF 输入栈）；不能 ⇒ 面板转免打字列表模式。
/// </summary>
internal static class WinFormsTypingProbe
{
    public static JsonObject Run()
    {
        var json = new JsonObject { ["ok"] = false };
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var form = new System.Windows.Forms.Form
                {
                    Text = "WinForms typing probe",
                    Size = new System.Drawing.Size(400, 120),
                    StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                    TopMost = true,
                };
                var box = new System.Windows.Forms.TextBox
                {
                    Size = new System.Drawing.Size(360, 30),
                    Location = new System.Drawing.Point(10, 20),
                };
                form.Controls.Add(box);
                form.Shown += (_, _) => box.Focus();

                form.Show();
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(500);

                // 注入 "hi"（真键）
                InjectKey(0x48);   // H
                Thread.Sleep(120);
                InjectKey(0x49);   // I
                Thread.Sleep(120);

                // 泵 1 秒
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1000)
                {
                    System.Windows.Forms.Application.DoEvents();
                    Thread.Sleep(10);
                }

                json["ok"] = box.Text == "hi";
                json["text"] = box.Text;
                json["expected"] = "hi";
                json["closed"] = false;

                form.Close();
                System.Windows.Forms.Application.DoEvents();
            }
            catch (Exception ex)
            {
                json["error"] = ex.Message;
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait(15000);

        return json;
    }

    private static void InjectKey(int vk)
    {
        keybd_event((byte)vk, 0, 0, 0);
        Thread.Sleep(30);
        keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, 0);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;
}
