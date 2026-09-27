// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Eztools.Contracts;

/// <summary>
/// 组合键的规范化表示（如 <c>Ctrl+Alt+H</c>）。
///
/// 为什么放契约层而不是 Desktop：**热键是一种独占资源**（§8.3 的 <c>hotkey.&lt;组合键&gt;</c>），
/// 仲裁发生在宿主层——宿主必须能不依赖 Win32 地比较两个组合键是否相同。
/// 解析规则：修饰键大小写不敏感、顺序不敏感（Alt+Ctrl+H ≡ Ctrl+Alt+H）、必须有修饰键
/// 且必须恰好一个主键（裸 F1~F12 允许无修饰键）。
/// </summary>
public sealed record HotkeyCombo
{
    private HotkeyCombo(string normalized, string modifiers, string key)
    {
        Normalized = normalized;
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>规范化串（修饰键固定顺序 Ctrl+Alt+Shift+Win，主键大写），如 <c>Ctrl+Alt+H</c>。</summary>
    public string Normalized { get; }

    /// <summary>修饰键集合（规范化顺序，"+" 分隔），可空串。</summary>
    public string Modifiers { get; }

    /// <summary>主键（大写），如 <c>H</c> / <c>F5</c> / <c>1</c>。</summary>
    public string Key { get; }

    /// <summary>独占资源 id（§8.3：<c>hotkey.&lt;规范化组合键&gt;</c>）。</summary>
    public string ResourceId => $"hotkey.{Normalized.ToLowerInvariant()}";

    /// <summary>解析失败时返回 null（调用方出诊断），不抛异常 —— 清单解析路径不允许异常传播。</summary>
    public static HotkeyCombo? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(p => p.Trim().ToLowerInvariant())
                       .ToList();

        if (parts.Count == 0)
        {
            return null;
        }

        var mods = new List<string>();
        string? key = null;

        foreach (var part in parts)
        {
            switch (part)
            {
                case "ctrl" or "control":
                    mods.Add("ctrl");
                    break;
                case "alt":
                    mods.Add("alt");
                    break;
                case "shift":
                    mods.Add("shift");
                    break;
                case "win" or "windows":
                    mods.Add("win");
                    break;
                default:
                    // 主键：只允许出现一次
                    if (key is not null)
                    {
                        return null;
                    }

                    key = NormalizeKey(part);
                    if (key is null)
                    {
                        return null;
                    }

                    break;
            }
        }

        if (key is null)
        {
            return null;
        }

        // F1~F12 允许无修饰键（约定俗成的独占用法）；其余必须有修饰键
        var isFunctionKey = key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 12;
        if (mods.Count == 0 && !isFunctionKey)
        {
            return null;
        }

        // 去重（Ctrl+Ctrl+H）
        mods = mods.Distinct().ToList();

        // 固定顺序：Ctrl → Alt → Shift → Win
        var ordered = new[] { "ctrl", "alt", "shift", "win" }.Where(mods.Contains).ToList();
        var modStr = string.Join("+", ordered.Select(m => m switch
        {
            "ctrl" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            _ => "Win",
        }));

        var normalized = modStr.Length == 0 ? key : $"{modStr}+{key}";
        return new HotkeyCombo(normalized, modStr, key);
    }

    private static string? NormalizeKey(string key)
    {
        var upper = key.ToUpperInvariant();

        // F1~F12
        if (upper.Length >= 2 && upper[0] == 'F' && int.TryParse(upper[1..], out var fn) && fn is >= 1 and <= 12)
        {
            return upper;
        }

        // 单个字母 / 数字
        if (upper.Length == 1 && (char.IsAsciiLetterUpper(upper[0]) || char.IsAsciiDigit(upper[0])))
        {
            return upper;
        }

        // 一小组常用命名键（其余按键暂不支持，解析失败出诊断而不是静默不注册）
        return upper switch
        {
            "SPACE" or "SPACEBAR" => "SPACE",
            "TAB" => "TAB",
            "ENTER" or "RETURN" => "ENTER",
            "ESC" or "ESCAPE" => "ESC",
            "INSERT" or "INS" => "INSERT",
            "DELETE" or "DEL" => "DELETE",
            "HOME" => "HOME",
            "END" => "END",
            "PGUP" or "PAGEUP" => "PGUP",
            "PGDN" or "PAGEDOWN" => "PGDN",
            _ => null,
        };
    }
}
