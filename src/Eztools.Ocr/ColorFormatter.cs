// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Drawing;

namespace Eztools.Ocr;

/// <summary>
/// 色值格式化（W6-c FR-4，D4=A）：取色得到的颜色 → 目标格式文本。纯静态无状态。
///
/// 放在 Eztools.Ocr（W6 设计方案 §10）而不是 Desktop：selftest（ezt selftest，Cli 侧）
/// 要对手算精确值断言，而 Cli 引用不到 Desktop、引用得到 Ocr。
///
/// 格式约定：
///   · hex = <c>#rrggbb</c> 小写（W6 设计方案 §10 拍板）；
///   · rgb = CSS 语法 <c>rgb(r, g, b)</c>（逗号 + 空格）；
///   · hsl = CSS 语法 <c>hsl(h, s%, l%)</c>，H ∈ [0,360) 整数度、S/L ∈ [0,100] 整数百分比，
///     **四舍五入用 AwayFromZero**（银行家舍入会把 24.5 舍成 24，用户预期是 25）。
///     RGB→HSL 用标准公式（max/min/ delta），max==min 时 H=0、S=0。
///
/// 未知格式（R10）：返回 <c>null</c> —— **显式失败**，由调用方回落 hex 并记日志；
/// 本类绝不静默替用户做主（S 系红线：降级必须可见）。
/// </summary>
public static class ColorFormatter
{
    /// <summary>
    /// 按配置格式格式化。格式名大小写不敏感、两侧空白忽略；
    /// <paramref name="format"/> 为 null/空白 = 默认 hex；未知格式返回 null（R10）。
    /// </summary>
    public static string? TryFormat(Color color, string? format)
    {
        var key = string.IsNullOrWhiteSpace(format) ? "hex" : format.Trim().ToLowerInvariant();
        return key switch
        {
            "hex" => Hex(color),
            "rgb" => Rgb(color),
            "hsl" => Hsl(color),
            _ => null,
        };
    }

    public static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    public static string Rgb(Color color) => $"rgb({color.R}, {color.G}, {color.B})";

    public static string Hsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var l = (max + min) / 2.0;

        double h = 0;
        double s = 0;
        if (delta > 0)
        {
            s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);
            h = max == r ? 60.0 * Mod6((g - b) / delta)
              : max == g ? 60.0 * (((b - r) / delta) + 2)
              : 60.0 * (((r - g) / delta) + 4);
            if (h < 0)
            {
                h += 360.0;
            }
        }

        return $"hsl({Round(h)}, {Round(s * 100)}%, {Round(l * 100)}%)";
    }

    private static double Mod6(double x) => x % 6.0;

    private static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
