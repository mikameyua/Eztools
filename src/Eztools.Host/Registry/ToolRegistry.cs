// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Contracts;
using Eztools.Host.Discovery;

namespace Eztools.Host.Registry;

/// <summary>工具的加载状态（运行时信息，不持久化）。</summary>
public enum ToolLoadState
{
    /// <summary>清单已注册但进程未起。</summary>
    NotLoaded,

    /// <summary>进程运行中。</summary>
    Running,

    /// <summary>因崩溃熔断被宿主自动禁用。</summary>
    Quarantined,
}

/// <summary>已注册的工具：清单 + 配置状态 + 运行时状态。</summary>
public sealed class RegisteredTool
{
    public required ToolManifest Manifest { get; init; }

    /// <summary>"已启用"——宿主是否允许启动它。</summary>
    public bool Enabled { get; set; } = true;

    public string? DisabledReason { get; set; }

    /// <summary>"已加载"——进程当前是否活着。与 Enabled 严格区分。</summary>
    public ToolLoadState LoadState { get; set; } = ToolLoadState.NotLoaded;

    public int? ProcessId { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public int CrashCount { get; set; }

    public string Id => Manifest.Id;

    public string Name => Manifest.Name;

    public override string ToString() => $"{Id} v{Manifest.Version}";
}

/// <summary>命令索引项。</summary>
public sealed record CommandBinding(RegisteredTool Tool, ToolCommand Command)
{
    public string Id => Command.Id;
}

/// <summary>
/// 模块注册表（清单驱动）。
///
/// 职责：把发现结果变成"宿主可以直接用的索引"——命令表、动作表、热键表、菜单表，
/// 外加"谁能被调用"。宿主后续（P1 设置 UI、P2 热键与托盘）都从这里取数据，
/// 而不是各自再维护一份硬编码清单。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, RegisteredTool> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CommandBinding> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToolDiagnostic> _diagnostics = new();

    private ToolRegistry(IReadOnlyList<RegisteredTool> tools, IReadOnlyList<ToolSource> sources)
    {
        Tools = tools;
        Sources = sources;

        foreach (var tool in tools)
        {
            _byId[tool.Id] = tool;
            foreach (var command in tool.Manifest.Contributes.Commands)
            {
                if (_commands.TryGetValue(command.Id, out var existing))
                {
                    _diagnostics.Add(new ToolDiagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.CommandConflict,
                        $"命令 id '{command.Id}' 同时被 {existing.Tool.Id} 与 {tool.Id} 声明，" +
                        $"调用将只命中 {existing.Tool.Id}"));
                    continue;
                }

                _commands[command.Id] = new CommandBinding(tool, command);
            }
        }
    }

    public IReadOnlyList<RegisteredTool> Tools { get; }

    public IReadOnlyList<ToolSource> Sources { get; }

    public IReadOnlyList<ToolDiagnostic> Diagnostics => _diagnostics;

    public int EnabledCount => Tools.Count(t => t.Enabled);

    /// <summary>全部命令（跨工具）。</summary>
    public IEnumerable<CommandBinding> Commands => _commands.Values;

    public IEnumerable<CommandBinding> EnabledCommands =>
        _commands.Values.Where(c => c.Tool.Enabled);

    public static ToolRegistry Build(
        ToolDiscoveryResult discovery,
        ToolStateStore stateStore,
        HostLog log)
    {
        var tools = new List<RegisteredTool>();
        var diagnostics = new List<ToolDiagnostic>(discovery.Diagnostics);

        foreach (var manifest in discovery.Manifests)
        {
            var state = stateStore.Get(manifest.Id);
            tools.Add(new RegisteredTool
            {
                Manifest = manifest,
                Enabled = state.Enabled,
                DisabledReason = state.DisabledReason,
            });
        }

        tools.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        var registry = new ToolRegistry(tools, discovery.Sources);
        diagnostics.AddRange(registry.Diagnostics);

        // 独占资源与热键的冲突**检测**（P0 只报告，仲裁在 P2）
        diagnostics.AddRange(DetectExclusiveResourceConflicts(tools));
        diagnostics.AddRange(DetectHotkeyConflicts(tools));

        // 让 registry.Diagnostics 反映全部诊断
        registry._diagnostics.Clear();
        registry._diagnostics.AddRange(diagnostics);

        foreach (var diagnostic in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            log.Error(diagnostic.ToString(), "registry");
        }

        return registry;
    }

    public bool TryGetTool(string toolId, out RegisteredTool tool) => _byId.TryGetValue(toolId, out tool!);

    public bool TryResolveCommand(string commandId, out CommandBinding binding) =>
        _commands.TryGetValue(commandId, out binding!);

    /// <summary>解析热键声明的实际绑定（用户覆盖优先）。</summary>
    public IEnumerable<(RegisteredTool Tool, ToolHotkey Hotkey, string Effective)> Hotkeys(ToolStateStore stateStore)
    {
        foreach (var tool in Tools)
        {
            foreach (var hotkey in tool.Manifest.Contributes.Hotkeys)
            {
                var overrides = stateStore.Get(tool.Id).HotkeyOverrides;
                var effective = overrides.TryGetValue(hotkey.Command, out var custom) && !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : hotkey.Default;
                yield return (tool, hotkey, effective);
            }
        }
    }

    private static IEnumerable<ToolDiagnostic> DetectExclusiveResourceConflicts(IReadOnlyList<RegisteredTool> tools)
    {
        var owners = new Dictionary<string, List<RegisteredTool>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools.Where(t => t.Enabled))
        {
            foreach (var resource in tool.Manifest.ExclusiveResources)
            {
                if (!owners.TryGetValue(resource, out var list))
                {
                    list = new List<RegisteredTool>();
                    owners[resource] = list;
                }

                list.Add(tool);
            }
        }

        foreach (var (resource, list) in owners.Where(kv => kv.Value.Count > 1))
        {
            var names = string.Join("、", list.Select(t => t.Id));
            yield return new ToolDiagnostic(
                DiagnosticSeverity.Warning,
                DiagnosticCodes.ExclusiveResourceConflict,
                $"独占资源 '{resource}' 被多个已启用工具声明：{names}。同时启用会互相破坏，需二选一（运行时由 ExclusiveResourceManager 仲裁）");
        }
    }

    private static IEnumerable<ToolDiagnostic> DetectHotkeyConflicts(IReadOnlyList<RegisteredTool> tools)
    {
        var owners = new Dictionary<string, List<(RegisteredTool Tool, string Command)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools.Where(t => t.Enabled))
        {
            foreach (var hotkey in tool.Manifest.Contributes.Hotkeys)
            {
                // P2 升级：用 HotkeyCombo 规范化比较 —— 修饰键顺序不敏感（Alt+Ctrl+H ≡ Ctrl+Alt+H）、
                // 大小写不敏感。P0 的朴素字符串比对会把这两个等价写法当成两个键。
                if (hotkey.Resolved is null)
                {
                    continue; // 解析失败的场景已有 HotkeyInvalid 诊断
                }

                var key = hotkey.Resolved.ResourceId;
                if (!owners.TryGetValue(key, out var list))
                {
                    list = new List<(RegisteredTool, string)>();
                    owners[key] = list;
                }

                list.Add((tool, hotkey.Command));
            }
        }

        foreach (var (key, list) in owners.Where(kv => kv.Value.Count > 1))
        {
            var names = string.Join("、", list.Select(x => $"{x.Tool.Id}:{x.Command}"));
            yield return new ToolDiagnostic(
                DiagnosticSeverity.Warning,
                DiagnosticCodes.HotkeyConflict,
                $"热键 '{key}' 被多个工具声明：{names}。由宿主统一注册时保留先注册者（HotkeyArbitration）");
        }
    }
}
