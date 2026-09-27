// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eztools.Host.Registry;

/// <summary>单个工具的持久化状态。</summary>
public sealed class ToolState
{
    public bool Enabled { get; set; } = true;

    public string? DisabledReason { get; set; }

    public string? DisabledAt { get; set; }

    /// <summary>用户覆盖的热键绑定（P2 热键仲裁使用）。</summary>
    public Dictionary<string, string> HotkeyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 宿主状态存储（<c>%APPDATA%\Eztools\state.json</c>）。
///
/// 与工具配置分离：这里只放"宿主怎么管这个工具"，不放"工具自己的配置"。
/// 关键语义：**"已启用" 与 "已加载" 是两件事**——PowerToys 的 <c>enabled</c> 只管
/// <c>enable()</c> 调用，DLL 照样载入内存（见设计方案 §10.2），此处严格区分。
/// </summary>
public sealed class ToolStateStore
{
    private readonly EztoolsPaths _paths;
    private readonly HostLog _log;
    private readonly Dictionary<string, ToolState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ToolStateStore(EztoolsPaths paths, HostLog log)
    {
        _paths = paths;
        _log = log;
        Load();
    }

    public IReadOnlyDictionary<string, ToolState> All
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, ToolState>(_states, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public ToolState Get(string toolId)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(toolId, out var state))
            {
                state = new ToolState();
                _states[toolId] = state;
            }

            return state;
        }
    }

    public void SetEnabled(string toolId, bool enabled, string? reason = null)
    {
        var state = Get(toolId);
        state.Enabled = enabled;
        state.DisabledReason = enabled ? null : reason;
        state.DisabledAt = enabled ? null : DateTimeOffset.Now.ToString("O");
        Save();
    }

    public void SetHotkeyOverride(string toolId, string command, string hotkey)
    {
        Get(toolId).HotkeyOverrides[command] = hotkey;
        Save();
    }

    /// <summary>移除覆盖（回退清单默认）。键不存在时静默返回 —— unset 的幂等语义。</summary>
    public void RemoveHotkeyOverride(string toolId, string command)
    {
        var state = Get(toolId);
        if (state.HotkeyOverrides.Remove(command))
        {
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_paths.StateFile))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_paths.StateFile));
            if (node?["tools"] is not JsonObject tools)
            {
                return;
            }

            foreach (var kv in tools)
            {
                if (kv.Value is not JsonObject toolNode)
                {
                    continue;
                }

                var state = new ToolState
                {
                    Enabled = toolNode["enabled"]?.GetValue<bool>() ?? true,
                    DisabledReason = toolNode["disabledReason"]?.GetValue<string>(),
                    DisabledAt = toolNode["disabledAt"]?.GetValue<string>(),
                };

                if (toolNode["hotkeyOverrides"] is JsonObject overrides)
                {
                    foreach (var o in overrides)
                    {
                        if (o.Value is JsonValue v && v.TryGetValue<string>(out var s))
                        {
                            state.HotkeyOverrides[o.Key] = s;
                        }
                    }
                }

                _states[kv.Key] = state;
            }
        }
        catch (Exception ex)
        {
            // 状态文件坏了不该让宿主起不来：记日志、用默认值继续
            _log.Warn($"状态文件解析失败，按默认状态继续: {ex.Message}", "state");
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_paths.StateFile)!);

                var tools = new JsonObject();
                foreach (var (id, state) in _states.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var overrides = new JsonObject();
                    foreach (var (command, hotkey) in state.HotkeyOverrides)
                    {
                        overrides[command] = hotkey;
                    }

                    tools[id] = new JsonObject
                    {
                        ["enabled"] = state.Enabled,
                        ["disabledReason"] = state.DisabledReason,
                        ["disabledAt"] = state.DisabledAt,
                        ["hotkeyOverrides"] = overrides,
                    };
                }

                var root = new JsonObject
                {
                    ["schema"] = 1,
                    ["tools"] = tools,
                };

                File.WriteAllText(
                    _paths.StateFile,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Error($"状态保存失败: {ex.Message}", "state");
            }
        }
    }
}
