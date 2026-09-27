// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace Eztools.ClipboardLib;

/// <summary>
/// 读一次系统剪贴板（W5-a 的 CLI 捕获出口；Desktop 常驻监听在 W5-c 接线）。
///
/// <para>System.Windows.Clipboard 要求 STA —— 这里自建 STA 线程收口，
/// 调用方（控制台 Main 是 MTA、托盘任意线程）都不必关心套间。</para>
///
/// <para>UIPI（W5 设计 §8 R1）：非提权进程读不到提权进程写入的内容——表现为
/// "有 owner 但读出来是空"。本类不假装读到了什么：owner 进程名 + 是否提权
/// 一并返回，由上层决定记占位条目（FR-11③）还是丢弃。</para>
/// </summary>
public static class ClipboardReader
{
    public sealed record Snapshot(
        string? Text,
        IReadOnlyList<string> Files,
        BitmapSource? Image,
        string? OwnerProcess,
        bool OwnerElevated)
    {
        public static readonly Snapshot Empty = new(null, Array.Empty<string>(), null, null, false);
    }

    /// <summary>读当前剪贴板。任何 Win32/WPF 异常都收敛为 Empty 快照并带回原因（禁静默：原因给上层）。</summary>
    public static (Snapshot Snapshot, string? Error) ReadCurrent()
    {
        Snapshot result = Snapshot.Empty;
        string? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var (owner, elevated) = GetOwnerProcess();
                var files = ReadFileDropList();
                string? text = null;
                BitmapSource? image = null;
                if (files.Count == 0)
                {
                    text = System.Windows.Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(text) && System.Windows.Clipboard.ContainsImage())
                    {
                        // 截图工具（Snipping Tool 等）只设 DIB 不设文本 —— 图片通道是它们的唯一捕获路径
                        image = System.Windows.Clipboard.GetImage();
                        image?.Freeze();   // 跨线程交给消费方（编码 PNG 在调用方线程）
                    }
                }

                result = new Snapshot(text, files, image, owner, elevated);
            }
            catch (Exception ex)
            {
                // 剪贴板被别的进程短暂占住是常态（W4 剪贴板竞争先例），收敛为空 + 原因。
                error = ex.Message;
                result = Snapshot.Empty;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return (result, error);
    }

    /// <summary>把一条文本条目重新复制回系统剪贴板（FR-9「单条重新复制」的 CLI 出口）。返回错误或 null。</summary>
    public static string? CopyText(string content)
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(content);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error;
    }

    // ────────────────────────────────────────────── owner 进程 / 提权判定

    /// <summary>(进程名 or null, 是否提权)。拿不到 owner 不是错误（剪贴板可以没有 owner）。</summary>
    private static (string?, bool) GetOwnerProcess()
    {
        var hwnd = GetClipboardOwner();
        if (hwnd == nint.Zero)
        {
            return (null, false);
        }

        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return (null, false);
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, pid);
        if (handle == nint.Zero)
        {
            // 打不开（如系统进程）——不猜名字，如实返回 null。
            return (null, false);
        }

        try
        {
            var name = new StringBuilder(1024);
            var size = (uint)name.Capacity;
            string? processName = QueryFullProcessImageName(handle, 0, name, ref size)
                ? Path.GetFileName(name.ToString())
                : null;

            return (processName, IsElevated(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsElevated(IntPtr processHandle)
    {
        if (!OpenProcessToken(processHandle, TokenQuery, out var token))
        {
            return false; // 查不到就当不提权——宁可少记占位，不可误记"提权"吓用户
        }

        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevation, sizeof(int), out _)
                && elevation != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ────────────────────────────────────────────── FileDrop

    private static IReadOnlyList<string> ReadFileDropList()
    {
        if (!System.Windows.Clipboard.ContainsFileDropList())
        {
            return Array.Empty<string>();
        }

        var list = System.Windows.Clipboard.GetFileDropList();
        return list.Cast<string>().ToList();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, out int info, int infoLength, out int returnedLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
