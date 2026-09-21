using System.Text.Json.Nodes;

namespace Eztools.Contracts;

/// <summary>一条命令贡献点。</summary>
public sealed class ToolCommand
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>工具侧 handler 名（工具只需要实现声明过的 handler）。</summary>
    public required string Handler { get; init; }

    public string? Description { get; init; }

    /// <summary>默认超时覆盖（毫秒）。null 表示按 weight 取默认值。</summary>
    public int? TimeoutMs { get; init; }
}

/// <summary>一条动作贡献点（文件 / 选中文本动作）。</summary>
public sealed class ToolAction
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Handler { get; init; }

    /// <summary>启用条件表达式，例如 <c>files.ext:pdf</c>。P0 只做承载与展示，不做求值。</summary>
    public string? When { get; init; }
}

/// <summary>一条热键声明。工具只声明，由宿主统一注册（P2 实现仲裁）。</summary>
public sealed class ToolHotkey
{
    public required string Command { get; init; }

    /// <summary>清单里的原始串（用户可读）。</summary>
    public required string Default { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// 触发时宿主注入的上下文来源。**缺省 = <see cref="MenuInput.Clipboard"/>** ——
    /// 这是**兼容性默认值**，不是随手取的：热键命令可能根本没有 menus 项可继承
    /// <c>input</c> 声明（<c>filehash.hash</c> 正是这种），而热键路径历史上就一律注入剪贴板。
    /// 缺省定成 Clipboard，"未声明"才与历史行为严格等价；要换来源须**显式**声明
    /// （如 <c>"input": "shellSelection"</c>）。
    /// </summary>
    public MenuInput Input { get; init; } = MenuInput.Clipboard;

    /// <summary>
    /// 规范化后的组合键；null = 原始串无法解析（已有 HotkeyInvalid 诊断，此处保留原样不崩溃）。
    /// 仲裁与独占资源声明都基于它。
    /// </summary>
    public HotkeyCombo? Resolved { get; init; }

    /// <summary>该热键对应的独占资源 id（规范化后；未解析成功时为 null）。</summary>
    public string? ResourceId => Resolved?.ResourceId;
}

/// <summary>一条菜单注入声明。</summary>
public sealed class ToolMenu
{
    public required string Location { get; init; }

    public required string Command { get; init; }

    public string? Group { get; init; }

    /// <summary>
    /// 输入来源。未声明等价于 <see cref="MenuInput.None"/>。
    /// 进托盘的命令必须"**能零参执行 或 声明了 input**"——否则菜单点了只会报"缺少参数"。
    /// 这条规则**无法静态校验**（handler 是动态的），由 `scripts/acceptance.sh` 的
    /// "每个托盘项零参可跑"断言守门。见 `docs/P2-托盘-实施方案.md` §1.2。
    /// </summary>
    public MenuInput Input { get; init; } = MenuInput.None;
}

/// <summary>
/// 一条面板声明（P4 Wave 2c，贡献点 <c>panels</c>）。
///
/// **语义**：工具声明"我有一个面板"，面板的**内容**由宿主在打开时通过
/// <c>tool.panel.data</c> 向工具拉取（声明式数据 → 宿主原生 WPF 渲染）。
/// 工具**不提供 UI 代码**——那是进程隔离架构的硬前提，见
/// <c>docs/P4-Wave2c-面板协议.md</c> §1.2。
///
/// **前置约束**：仅 <see cref="ToolWeight.Full"/> 档可用（解析期 Error，见同文档 §6）。
/// </summary>
public sealed class ToolPanel
{
    /// <summary>面板 id（工具内唯一）。宿主用它路由 <c>tool.panel.data</c> 的 <c>panelId</c>。</summary>
    public required string Id { get; init; }

    /// <summary>窗口标题 / 托盘菜单项文字。缺省时取 <see cref="Id"/>。</summary>
    public required string Title { get; init; }

    /// <summary>可选的副标题说明。</summary>
    public string? Description { get; init; }

    /// <summary>初始宽度（已钳制到 [320, 1600]）。</summary>
    public int Width { get; init; } = ToolPanelLimits.DefaultWidth;

    /// <summary>初始高度（已钳制到 [240, 1200]）。</summary>
    public int Height { get; init; } = ToolPanelLimits.DefaultHeight;

    /// <summary>
    /// 自动刷新周期（毫秒）。<c>0</c> = 不自动刷新；
    /// 非 0 时已钳制到 [500, 60000]。
    /// </summary>
    public int RefreshMs { get; init; }
}

/// <summary>
/// 面板字段的合法区间（**单一来源**：解析期钳制与验收断言都引用这里，
/// 避免"文档写 1600、代码写 1600、断言写 2000"三处漂移）。
/// </summary>
public static class ToolPanelLimits
{
    public const int MinWidth = 320;
    public const int MaxWidth = 1600;
    public const int DefaultWidth = 640;

    public const int MinHeight = 240;
    public const int MaxHeight = 1200;
    public const int DefaultHeight = 480;

    public const int MinRefreshMs = 500;
    public const int MaxRefreshMs = 60000;

    public static int ClampWidth(int raw) => Math.Clamp(raw, MinWidth, MaxWidth);

    public static int ClampHeight(int raw) => Math.Clamp(raw, MinHeight, MaxHeight);

    /// <summary>
    /// 刷新周期归一化：<c>0</c> 原样保留（= 不自动刷新）；合法区间内原样；
    /// **其余（包括 0 &lt; v &lt; 500 与 &gt; 60000）一律归 0**。
    ///
    /// **为什么不钳制到 500**：`Clamp` 会把"每 10 毫秒拉一次"这种笔误变成
    /// "每 500 毫秒拉一次" —— **比作者写的还勤**，等于把笔误放大成性能问题，
    /// 而作者完全看不出发生了什么。归 0（不自动刷新）是**保守降级**：
    /// 面板仍能手动刷新，只是少了自动行为，作者一看"怎么不自动刷新了"就会去查清单。
    /// 安全默认值必须往"更少打扰"的方向倒，不能往"更频繁"的方向倒。
    /// </summary>
    public static int NormalizeRefreshMs(int raw) =>
        raw == 0 || raw is >= MinRefreshMs and <= MaxRefreshMs ? raw : 0;
}

/// <summary>贡献点集合。贡献点是加法——工具只声明自己需要的那几个。</summary>
public sealed class ToolContributions
{
    public static readonly ToolContributions Empty = new();

    public IReadOnlyList<ToolCommand> Commands { get; init; } = Array.Empty<ToolCommand>();

    public IReadOnlyList<ToolAction> Actions { get; init; } = Array.Empty<ToolAction>();

    public IReadOnlyList<ToolHotkey> Hotkeys { get; init; } = Array.Empty<ToolHotkey>();

    public IReadOnlyList<ToolMenu> Menus { get; init; } = Array.Empty<ToolMenu>();

    /// <summary>面板声明（P4 Wave 2c）。仅 <c>weight: full</c> 可用。</summary>
    public IReadOnlyList<ToolPanel> Panels { get; init; } = Array.Empty<ToolPanel>();

    public bool IsEmpty =>
        Commands.Count == 0 && Actions.Count == 0 && Hotkeys.Count == 0 && Menus.Count == 0
        && Panels.Count == 0;}

/// <summary>
/// `tool.json` 的内存模型。
/// 这是宿主与工具之间唯一的契约载体：宿主从这里**自动获得**设置页、命令入口、全局热键、托盘项、
/// 进程生命周期管理——新增工具零 UI 代码（设计方案 §4.1、§7）。
/// </summary>
public sealed class ToolManifest
{
    // ── 身份 ──
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    public string? License { get; init; }

    public string? Icon { get; init; }

    public string? Homepage { get; init; }

    // ── 预留字段：自用阶段只填不校验（设计方案 §1.3）──
    public string? Publisher { get; init; }

    public int? ApiVersion { get; init; }

    // ── 运行时 ──
    public required string Runtime { get; init; }

    public string? RuntimeVersion { get; init; }

    public required string Entry { get; init; }

    public ToolWeight Weight { get; init; } = ToolWeight.Lite;

    public ToolLifecycle Lifecycle { get; init; } = ToolLifecycle.Transient;

    public ToolLatency Latency { get; init; } = ToolLatency.None;

    // ── 能力与资源声明 ──
    public IReadOnlyList<string> Needs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ElevatedPrimitives { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExclusiveResources { get; init; } = Array.Empty<string>();

    // ── 贡献点 ──
    public ToolContributions Contributes { get; init; } = ToolContributions.Empty;

    // ── 配置 schema（P1 将据此渲染设置页）──
    public JsonObject? ConfigSchema { get; init; }

    // ── 以下字段不是清单内容，而是发现过程填写的定位信息 ──

    /// <summary>工具目录绝对路径。</summary>
    public string ToolDirectory { get; init; } = string.Empty;

    /// <summary>清单文件绝对路径。</summary>
    public string ManifestPath { get; init; } = string.Empty;

    /// <summary>来自哪个目录源（builtin / user / dev）。</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>入口文件绝对路径。</summary>
    public string EntryPath => Path.GetFullPath(Path.Combine(ToolDirectory, Entry));

    /// <summary>图标绝对路径；未声明图标或文件不存在时为 null。</summary>
    public string? IconPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Icon))
            {
                return null;
            }

            var path = Path.Combine(ToolDirectory, Icon);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>是否有配置界面可言（决定 P1 是否渲染设置页）。</summary>
    public bool HasConfigSchema =>
        ConfigSchema is not null &&
        ConfigSchema.Count > 0 &&
        ConfigSchema["properties"] is JsonObject props &&
        props.Count > 0;

    public override string ToString() => $"{Id}@{Version} ({Runtime}, {Weight.ToWire()}, {Lifecycle.ToWire()})";
}
