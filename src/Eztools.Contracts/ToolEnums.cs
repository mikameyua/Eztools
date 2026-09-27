// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Contracts;

/// <summary>工具重量档位（决定进程怎么起、超时默认值）。见设计方案 §5.1。</summary>
public enum ToolWeight
{
    /// <summary>每次调用起进程，用完即退。默认超时 10s。</summary>
    Script,

    /// <summary>常驻宿主进程（按运行时分组复用）。默认超时 30s。P0 只做这一档。</summary>
    Lite,

    /// <summary>独立宿主进程 + 依赖 + 异步 + 面板 UI。超时按需声明。</summary>
    Full,
}

/// <summary>生命周期（决定起了之后活多久）。见设计方案 §8.1。</summary>
public enum ToolLifecycle
{
    /// <summary>即用即走，进程可回收（挂 kill-on-close Job，宿主崩溃时由内核收割）。</summary>
    Transient,

    /// <summary>宿主保活；必须实现 tool.recover。已实现（P2 尾巴，2026-09-20）：常驻宿主内跨调用复用、崩溃自动重启；不挂 kill-on-close Job。</summary>
    Resident,

    /// <summary>宿主启动时执行一次后回收（P2 简单版；schedule 调度留 P4）。</summary>
    Task,
}

/// <summary>启动延迟要求，仅 resident 可用。</summary>
public enum ToolLatency
{
    None,

    /// <summary>冷启动不可接受，必须预热常驻。</summary>
    Interactive,
}

/// <summary>
/// 菜单项的输入来源。
///
/// **为什么需要它**：`menus` 贡献点声明的是"命令"，但托盘点击时**没有任何上下文**
/// —— 不像 `actions` 有选中的文件。若不声明来源，命令只能拿到 `{}`，
/// 而需要参数的命令会直接在工具侧报"缺少参数"：
/// 菜单能显示、点击有反应、进程真的起来了，却**一次都跑不成**。
/// 见 `docs/P2-托盘-实施方案.md` §1.2。
/// </summary>
public enum MenuInput
{
    /// <summary>无上下文，命令自足（宿主传 <c>{}</c>）。默认，也是未声明时的取值。</summary>
    None,

    /// <summary>以剪贴板文本为输入。由宿主读取并注入到 <c>args["input"]</c>，工具自行映射到自己的参数名。</summary>
    Clipboard,

    /// <summary>
    /// 以**资源管理器当前选中项**为输入。由宿主读取并注入到
    /// <c>args["input"]</c>（string，首项路径；无选中项为 <c>""</c>）与
    /// <c>args["inputPaths"]</c>（string[]，全部选中项，可为空数组）。
    ///
    /// **为什么 <c>input</c> 恒为字符串**：与 <see cref="Clipboard"/> 保持同一类型，
    /// 避免"同一个键在不同声明下类型不同"——那正是 <c>JsonSerializer</c> 严格类型下
    /// 最容易踩的坑。多选信息不丢，在 <c>inputPaths</c> 里。
    ///
    /// 拿不到选中项**不算错误**（前台不是资源管理器 / 宿主已提权都是常态），
    /// 一律注入空值，由工具自己决定怎么提示。
    /// </summary>
    ShellSelection,
}

public static class ToolEnumExtensions
{
    public static string ToWire(this ToolWeight w) => w switch
    {
        ToolWeight.Script => "script",
        ToolWeight.Lite => "lite",
        ToolWeight.Full => "full",
        _ => "lite",
    };

    public static string ToWire(this ToolLifecycle l) => l switch
    {
        ToolLifecycle.Transient => "transient",
        ToolLifecycle.Resident => "resident",
        ToolLifecycle.Task => "task",
        _ => "transient",
    };

    public static string ToWire(this ToolLatency l) => l switch
    {
        ToolLatency.Interactive => "interactive",
        _ => "none",
    };

    public static string ToWire(this MenuInput i) => i switch
    {
        MenuInput.Clipboard => "clipboard",
        MenuInput.ShellSelection => "shellSelection",
        _ => "none",
    };

    public static bool TryParseWeight(string? raw, out ToolWeight weight)
    {
        weight = ToolWeight.Lite;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "script": weight = ToolWeight.Script; return true;
            case "lite": weight = ToolWeight.Lite; return true;
            case "full": weight = ToolWeight.Full; return true;
            default: return false;
        }
    }

    public static bool TryParseLifecycle(string? raw, out ToolLifecycle lifecycle)
    {
        lifecycle = ToolLifecycle.Transient;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "transient": lifecycle = ToolLifecycle.Transient; return true;
            case "resident": lifecycle = ToolLifecycle.Resident; return true;
            case "task": lifecycle = ToolLifecycle.Task; return true;
            default: return false;
        }
    }

    public static bool TryParseLatency(string? raw, out ToolLatency latency)
    {
        latency = ToolLatency.None;
        if (raw is null)
        {
            return true; // 未声明等价于 none
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "":
            case "none": latency = ToolLatency.None; return true;
            case "interactive": latency = ToolLatency.Interactive; return true;
            default: return false;
        }
    }

    /// <summary>
    /// 解析菜单项输入来源。未声明（null / 空）= <see cref="MenuInput.None"/> 且返回 true
    /// —— 与 <see cref="TryParseLatency"/> 同口径：缺省是正常写法，不该产生噪音；
    /// 只有"写了但不认识"才返回 false，由调用方报警告（静默取默认值会让作者以为生效了）。
    /// </summary>
    public static bool TryParseMenuInput(string? raw, out MenuInput input)
    {
        input = MenuInput.None;
        if (raw is null)
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "":
            case "none": input = MenuInput.None; return true;
            case "clipboard": input = MenuInput.Clipboard; return true;
            case "shellselection": input = MenuInput.ShellSelection; return true;
            default: return false;
        }
    }

    /// <summary>weight → 默认调用超时。</summary>
    public static TimeSpan DefaultTimeout(this ToolWeight w) => w switch
    {
        // 依据：实测冷启动仅 126~181ms（spike 第七节），10s 对单次工具执行非常充裕
        ToolWeight.Script => TimeSpan.FromSeconds(10),
        ToolWeight.Lite => TimeSpan.FromSeconds(30),
        ToolWeight.Full => TimeSpan.FromSeconds(120),
        _ => TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// 崩溃熔断阈值：resident 收紧（见设计方案 §6.5 / §8.2）。
    /// 返回 (窗口, 允许崩溃次数)。
    /// </summary>
    public static (TimeSpan Window, int MaxCrashes) CrashBudget(this ToolLifecycle l) => l switch
    {
        ToolLifecycle.Resident => (TimeSpan.FromMinutes(5), 2),
        _ => (TimeSpan.FromMinutes(10), 3),
    };
}

/// <summary>运行时标识。</summary>
public static class ToolRuntimes
{
    public const string Python = "python";
    public const string Node = "node";
    public const string DotNet = "dotnet";
    public const string Exe = "exe";

    public static readonly string[] All = { Python, Node, DotNet, Exe };

    public static bool IsKnown(string? runtime) =>
        runtime is not null && Array.Exists(All, r => string.Equals(r, runtime, StringComparison.OrdinalIgnoreCase));

    /// <summary>运行时是否为"托管型"（由宿主提供解释器），false 表示 entry 本身即可执行文件。</summary>
    public static bool IsInterpreted(string runtime) =>
        !string.Equals(runtime, Exe, StringComparison.OrdinalIgnoreCase);
}
