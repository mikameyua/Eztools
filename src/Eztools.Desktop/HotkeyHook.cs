// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows.Forms;
using Eztools.Contracts;

namespace Eztools.Desktop;

/// <summary>
/// 全局热键钩子：不可见消息窗口 + <c>RegisterHotKey</c> + <c>WM_HOTKEY</c> 分发。
///
/// 设计要点：
/// - sink 用不可见的 <see cref="Control"/>（有句柄、无界面），它只收热键消息；
/// - 注册 id 与组合键的映射由本类维护，<see cref="HotkeyFired"/> 把 <c>WM_HOTKEY</c> 还原成 id，
///   调用方据此找到要触发的命令；
/// - Win32 失败（组合键被**第三方程序**占用）返回 null —— 这与"输给 Eztools 内部仲裁"是两种
///   不同的落败，提示文案不同（§6.3：失效回退要列出占用方；第三方的占用方拿不到名字，只能提示）。
/// </summary>
internal sealed class HotkeyHook : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [Flags]
    private enum Mods : uint
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Win = 8,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    /// <summary>消息 sink：只负责把 WM_HOTKEY 转成事件。不可见，不进任务栏。</summary>
    private sealed class Sink : Control
    {
        // 用字段而不是属性：WinForms 分析器对 Control 派生类的可写属性要求序列化标注（WFO1000）
        public Action<int>? OnHotkey;

        public Sink() => SetStyle(ControlStyles.AllPaintingInWmPaint, true);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                OnHotkey?.Invoke(m.WParam.ToInt32());
            }

            base.WndProc(ref m);
        }
    }

    private readonly Sink _sink = new();
    private readonly Dictionary<int, HotkeyCombo> _byId = new();
    private int _nextId = 1;

    /// <summary>热键触发（WM_HOTKEY）。参数 = <see cref="Register"/> 返回的 id。</summary>
    public event Action<int>? HotkeyFired;

    public HotkeyHook()
    {
        if (!_sink.IsHandleCreated)
        {
            _ = _sink.Handle; // 强制创建句柄（在 UI 线程上构造本类，句柄随之归 UI 线程）
        }

        _sink.OnHotkey = id => HotkeyFired?.Invoke(id);
    }

    /// <summary>注册一个组合键。成功返回 id（触发时原样回调）；失败（被第三方占用等）返回 null。</summary>
    public int? Register(HotkeyCombo combo)
    {
        if (!TryToWin32(combo, out var mods, out var vk))
        {
            return null;
        }

        var id = _nextId++;
        if (!RegisterHotKey(_sink.Handle, id, (uint)mods, vk))
        {
            _nextId--;
            return null;
        }

        _byId[id] = combo;
        return id;
    }

    /// <summary>注销全部（退出/刷新重注册时调用）。</summary>
    public void UnregisterAll()
    {
        foreach (var id in _byId.Keys)
        {
            UnregisterHotKey(_sink.Handle, id);
        }

        _byId.Clear();
        _nextId = 1;
    }

    public int RegisteredCount => _byId.Count;

    /// <summary>把规范化组合键翻成 Win32 修饰位与虚拟键码。</summary>
    private static bool TryToWin32(HotkeyCombo combo, out Mods mods, out uint vk)
    {
        mods = Mods.None;
        foreach (var m in combo.Modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            mods |= m.ToLowerInvariant() switch
            {
                "ctrl" => Mods.Control,
                "alt" => Mods.Alt,
                "shift" => Mods.Shift,
                "win" => Mods.Win,
                _ => Mods.None,
            };
        }

        var key = combo.Key;
        vk = key switch
        {
            var k when k.Length == 1 && char.IsAsciiLetterUpper(k[0]) => (uint)k[0],  // 'A'→0x41
            var k when k.Length == 1 && char.IsAsciiDigit(k[0]) => (uint)k[0],        // '0'→0x30
            var k when k.Length >= 2 && k[0] == 'F' && int.TryParse(k[1..], out var fn)
                      && fn is >= 1 and <= 12 => (uint)(0x70 + fn - 1),               // F1→0x70
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "ESC" => 0x1B,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PGUP" => 0x21,
            "PGDN" => 0x22,
            _ => 0,
        };

        return vk != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _sink.Dispose();
    }
}
