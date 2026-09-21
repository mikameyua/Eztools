namespace Eztools.Contracts;

/// <summary>诊断级别。Error 会导致清单被拒绝注册，Warning 只记录。</summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 一条诊断记录。
/// 设计约束：清单问题**只记录不弹窗**（PowerToys 在 Release 模式会弹 MessageBoxW，见设计方案 §6.1）。
/// </summary>
public sealed record ToolDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Path = null,
    string? ToolId = null)
{
    /// <summary>
    /// 只输出「代码: 原因」+ 定位信息，**不含严重度前缀**。
    /// 严重度由消费方呈现（CLI 按级别上色加前缀，日志文件用 ERR/WRN 列），
    /// 这里再带一次就会出现"错误 错误 field.xxx: ..."这类重复。
    /// </summary>
    public override string ToString()
    {
        var who = ToolId is null ? "" : $" ({ToolId})";
        var where = Path is null ? "" : $"  [{Path}]";
        return $"{Code}: {Message}{who}{where}";
    }
}

/// <summary>诊断代码常量，便于脚本与测试断言。</summary>
public static class DiagnosticCodes
{
    // ── 清单解析类 ──
    public const string ManifestUnreadable = "manifest.unreadable";
    public const string ManifestInvalidJson = "manifest.invalid-json";
    public const string ManifestNotObject = "manifest.not-object";

    // ── 必填字段 ──
    public const string MissingId = "field.missing-id";
    public const string InvalidId = "field.invalid-id";
    public const string MissingName = "field.missing-name";
    public const string MissingVersion = "field.missing-version";
    public const string InvalidVersion = "field.invalid-version";
    public const string MissingRuntime = "field.missing-runtime";
    public const string UnknownRuntime = "field.unknown-runtime";
    public const string MissingEntry = "field.missing-entry";
    public const string EntryEscape = "field.entry-escape";
    public const string EntryMissing = "field.entry-missing";

    // ── 枚举字段（可回退到默认值，故为 Warning）──
    public const string UnknownWeight = "field.unknown-weight";
    public const string UnknownLifecycle = "field.unknown-lifecycle";
    public const string UnknownLatency = "field.unknown-latency";
    public const string LatencyOnNonResident = "field.latency-without-resident";

    // ── 贡献点 ──
    public const string CommandMissingHandler = "contributes.command-missing-handler";
    public const string CommandDuplicate = "contributes.command-duplicate";
    public const string CommandForeignNamespace = "contributes.command-foreign-namespace";
    public const string ActionMissingHandler = "contributes.action-missing-handler";
    public const string HotkeyUnknownCommand = "contributes.hotkey-unknown-command";
    public const string HotkeyInvalid = "contributes.hotkey-invalid";
    public const string MenuUnknownCommand = "contributes.menu-unknown-command";
    public const string ExclusiveDuplicate = "contributes.exclusive-duplicate";
    public const string ExclusiveInvalidId = "contributes.exclusive-invalid-id";
    public const string MenuUnknownInput = "contributes.menu-unknown-input";

    // ── 面板贡献点（P4 Wave 2c，见 docs/P4-Wave2c-面板协议.md §2）──
    /// <summary>panels 字段不是数组（整个字段被忽略）。</summary>
    public const string PanelsNotArray = "contributes.panels-not-array";

    /// <summary>面板缺少 id —— **Error**：宿主无法路由 tool.panel.data，该面板根本无法工作。</summary>
    public const string PanelMissingId = "contributes.panel-missing-id";

    /// <summary>面板 id 在本工具内重复（后者被忽略）。</summary>
    public const string PanelDuplicate = "contributes.panel-duplicate";

    /// <summary>面板引用了本工具未声明的命令（按钮点了会报未知命令 —— 最差的失败模式）。</summary>
    public const string PanelUnknownCommand = "contributes.panel-unknown-command";

    /// <summary>width/height 越界（已钳制到合法区间）。</summary>
    public const string PanelSizeOutOfRange = "contributes.panel-size-out-of-range";

    /// <summary>refreshMs 越界（已归 0 = 不自动刷新）。</summary>
    public const string PanelRefreshOutOfRange = "contributes.panel-refresh-out-of-range";

    /// <summary>声明了 panels 但 weight 不是 full —— **Error**：能力组合矛盾（见协议 §6.1）。</summary>
    public const string PanelsRequireFull = "contributes.panels-require-full";

    // ── 资源 ──
    public const string UnknownNeed = "needs.unknown-value";
    public const string IconMissing = "icon.missing";

    /// <summary>weight: full 但未声明 needs: ui.panel（漏了声明，不阻塞）。</summary>
    public const string PanelWithoutUiPanelNeed = "needs.panel-without-ui-panel";

    // ── 注册表级 ──
    public const string DuplicateId = "registry.duplicate-id";
    public const string ExclusiveResourceConflict = "registry.exclusive-resource-conflict";
    public const string HotkeyConflict = "registry.hotkey-conflict";
    public const string CommandConflict = "registry.command-conflict";
    public const string ToolDirectoryMissing = "source.directory-missing";
}
