using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eztools.Contracts;

/// <summary>清单解析结果。</summary>
public sealed class ManifestParseResult
{
    public ToolManifest? Manifest { get; init; }

    public IReadOnlyList<ToolDiagnostic> Diagnostics { get; init; } = Array.Empty<ToolDiagnostic>();

    /// <summary>无 Error 级诊断即为可用。</summary>
    public bool Ok => Manifest is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public bool HasWarning => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);
}

/// <summary>
/// `tool.json` 解析 + 校验器。
///
/// 设计原则（来自 PowerToys 的教训）：
/// <list type="bullet">
/// <item>校验失败<b>静默降级 + 记录诊断</b>，绝不弹错误对话框、绝不中断宿主启动。</item>
/// <item>可回退的枚举字段（weight / lifecycle）取默认值并记 Warning，而不是拒绝整个工具——
/// 一个拼错的 weight 不该让工具彻底不可用。</item>
/// <item>未知字段一律忽略（前向兼容：新版本加入的字段不应让旧宿主拒绝工具）。</item>
/// </list>
/// </summary>
public static class ManifestParser
{
    public const string FileName = "tool.json";

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>`needs` 的收敛集（设计方案 §4.4）。</summary>
    public static readonly string[] KnownNeeds =
    {
        "files.userSelected",
        "files.pluginData",
        "files.anyPath",
        "network",
        "clipboard",
        "ui.panel",
        "notify",
        "system.inspect",
    };

    public static ManifestParseResult Load(string manifestPath, string sourceName)
    {
        string text;
        try
        {
            text = File.ReadAllText(manifestPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return Fail(manifestPath, sourceName, DiagnosticCodes.ManifestUnreadable,
                $"读取清单失败: {ex.Message}", DiagnosticSeverity.Error);
        }

        return Parse(text, manifestPath, sourceName);
    }

    public static ManifestParseResult Parse(string json, string manifestPath, string sourceName)
    {
        var diagnostics = new List<ToolDiagnostic>();
        void Error(string code, string message) =>
            diagnostics.Add(new ToolDiagnostic(DiagnosticSeverity.Error, code, message, manifestPath));
        void Warn(string code, string message) =>
            diagnostics.Add(new ToolDiagnostic(DiagnosticSeverity.Warning, code, message, manifestPath));

        // ── exclusiveResources 规范化与校验（§8.3）──
        // 小写统一 + [a-z0-9._-] 字符校验 + 清单内去重；不合法项出诊断并跳过（不静默收编）
        List<string> ParseExclusiveResources(JsonObject o)
        {
            var result = new List<string>();
            foreach (var raw in StrList(o, "exclusiveResources"))
            {
                var id = raw.Trim().ToLowerInvariant();
                if (id.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-z0-9][a-z0-9._-]*$"))
                {
                    Warn(DiagnosticCodes.ExclusiveInvalidId,
                        $"独占资源 id '{raw}' 不合法（允许 [a-z0-9._-]，小写）");
                    continue;
                }

                if (result.Contains(id))
                {
                    Warn(DiagnosticCodes.ExclusiveDuplicate,
                        $"独占资源 '{id}' 在本清单内重复声明");
                    continue;
                }

                result.Add(id);
            }

            return result;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, // 容忍 tool.jsonc 风格的注释
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            Error(DiagnosticCodes.ManifestInvalidJson, $"JSON 解析失败: {ex.Message}");
            return new ManifestParseResult { Diagnostics = diagnostics };
        }

        if (root is not JsonObject obj)
        {
            Error(DiagnosticCodes.ManifestNotObject, "清单根节点必须是 JSON 对象");
            return new ManifestParseResult { Diagnostics = diagnostics };
        }

        // ── 必填：id ──
        var id = Str(obj, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            Error(DiagnosticCodes.MissingId, "缺少必填字段 id");
        }
        else if (!IdPattern.IsMatch(id) || id.Length > 64)
        {
            Error(DiagnosticCodes.InvalidId,
                $"id '{id}' 非法：只允许小写字母、数字与连字符，且以字母或数字开头，长度 ≤64");
        }

        // ── 必填：name / version ──
        var name = Str(obj, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Error(DiagnosticCodes.MissingName, "缺少必填字段 name");
        }

        var version = Str(obj, "version");
        if (string.IsNullOrWhiteSpace(version))
        {
            Error(DiagnosticCodes.MissingVersion, "缺少必填字段 version（semver，如 1.0.0）");
        }
        else if (!SemVer.TryParse(version, out _))
        {
            Error(DiagnosticCodes.InvalidVersion, $"version '{version}' 不是合法 semver");
        }

        // ── 必填：runtime ──
        var runtime = Str(obj, "runtime");
        if (string.IsNullOrWhiteSpace(runtime))
        {
            Error(DiagnosticCodes.MissingRuntime, "缺少必填字段 runtime");
        }
        else if (!ToolRuntimes.IsKnown(runtime))
        {
            Error(DiagnosticCodes.UnknownRuntime,
                $"runtime '{runtime}' 未支持，可选: {string.Join(" / ", ToolRuntimes.All)}");
        }

        // ── 必填：entry（并做目录逃逸校验）──
        var entry = Str(obj, "entry");
        var toolDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        if (string.IsNullOrWhiteSpace(entry))
        {
            Error(DiagnosticCodes.MissingEntry, "缺少必填字段 entry");
        }
        else if (Path.IsPathRooted(entry))
        {
            Error(DiagnosticCodes.EntryEscape, "entry 必须是相对工具目录的路径");
        }
        else
        {
            var entryFull = Path.GetFullPath(Path.Combine(toolDir, entry));
            var rootWithSep = toolDir.EndsWith(Path.DirectorySeparatorChar)
                ? toolDir
                : toolDir + Path.DirectorySeparatorChar;
            if (!entryFull.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                Error(DiagnosticCodes.EntryEscape, $"entry '{entry}' 逃出了工具目录");
            }
            else if (!File.Exists(entryFull))
            {
                Error(DiagnosticCodes.EntryMissing, $"入口文件不存在: {entry}");
            }
        }

        // ── 枚举字段：可回退 ──
        // 枚举字段的两种失败要分开对待：
        //   · 字段**缺省** → 取默认值，这是正常写法，不该产生噪音
        //   · 字段**写了但不认识** → 作者的意图没被满足，必须警告。多半是打错字，
        //     而静默取默认值会让工具以"看起来正常、其实是默认行为"的方式运行，极难发现。
        var weightRaw = Str(obj, "weight");
        if (!ToolEnumExtensions.TryParseWeight(weightRaw, out var weight))
        {
            weight = ToolWeight.Lite;
            if (!string.IsNullOrWhiteSpace(weightRaw))
            {
                Warn(DiagnosticCodes.UnknownWeight,
                    $"weight '{weightRaw}' 无法识别，回退为 lite（可选 script / lite / full）");
            }
        }

        var lifecycleRaw = Str(obj, "lifecycle");
        if (!ToolEnumExtensions.TryParseLifecycle(lifecycleRaw, out var lifecycle))
        {
            lifecycle = ToolLifecycle.Transient;
            if (!string.IsNullOrWhiteSpace(lifecycleRaw))
            {
                Warn(DiagnosticCodes.UnknownLifecycle,
                    $"lifecycle '{lifecycleRaw}' 无法识别，回退为 transient（可选 transient / resident / task）");
            }
        }

        var latencyRaw = Str(obj, "latency");
        if (!ToolEnumExtensions.TryParseLatency(latencyRaw, out var latency))
        {
            latency = ToolLatency.None;
            if (!string.IsNullOrWhiteSpace(latencyRaw))
            {
                Warn(DiagnosticCodes.UnknownLatency,
                    $"latency '{latencyRaw}' 无法识别，回退为 none（可选 interactive）");
            }
        }

        if (latency == ToolLatency.Interactive && lifecycle != ToolLifecycle.Resident)
        {
            Warn(DiagnosticCodes.LatencyOnNonResident,
                "latency=interactive 仅对 lifecycle=resident 有意义，当前将被忽略");
        }

        // ── needs 收敛集校验 ──
        var needs = StrList(obj, "needs");
        foreach (var need in needs)
        {
            if (Array.IndexOf(KnownNeeds, need) < 0)
            {
                Warn(DiagnosticCodes.UnknownNeed,
                    $"needs 中的 '{need}' 不在收敛集内: {string.Join(" / ", KnownNeeds)}");
            }
        }

        // ── 图标 ──
        var icon = Str(obj, "icon");
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var iconFull = Path.Combine(toolDir, icon);
            if (!File.Exists(iconFull))
            {
                Warn(DiagnosticCodes.IconMissing, $"声明的图标文件不存在: {icon}");
            }
        }

        // ── 贡献点 ──
        var contributes = ParseContributions(obj["contributes"] as JsonObject, id, diagnostics, manifestPath);

        // ── panels 的两个跨字段约束（P4 Wave 2c，协议 §6）──
        // 位置在这里而不是 ParsePanels 内部：ParsePanels 看不到 weight / needs（它们在同层解析）。
        if (contributes.Panels.Count > 0)
        {
            // ① weight 必须是 full —— Error 而非"自动升档"：
            //    weight 是作者对进程模型的显式选择（含超时、依赖策略），宿主静默改写它
            //    = 让工具以作者没预期的形态运行（script 档"用完即退"语义会消失）。
            //    与"拼错的 weight 回退 + Warning"不同：那是笔误，这是能力组合矛盾。
            if (weight != ToolWeight.Full)
            {
                Error(DiagnosticCodes.PanelsRequireFull,
                    $"声明了 {contributes.Panels.Count} 个面板但 weight 是 '{weight.ToWire()}' —— "
                    + "面板仅对 weight: full 可用（需要常驻可复用进程与长超时）。"
                    + $"请把 tool.json 的 weight 改成 \"full\"（当前档位默认超时 {weight.DefaultTimeout().TotalSeconds:F0}s，"
                    + $"full 档为 {ToolWeight.Full.DefaultTimeout().TotalSeconds:F0}s）。");
            }

            // ② needs 应包含 ui.panel —— 仅 Warning（漏声明的能力提示，不阻塞）
            if (!needs.Contains("ui.panel", StringComparer.OrdinalIgnoreCase))
            {
                Warn(DiagnosticCodes.PanelWithoutUiPanelNeed,
                    "声明了 panels 但 needs 里没有 'ui.panel' —— 建议补上，"
                    + "否则工具详情页不会显示「需要面板能力」这条提示");
            }
        }

        // ── 配置 schema ──
        JsonObject? configSchema = null;
        if (obj["config"] is JsonNode configNode)
        {
            if (configNode is JsonObject configObj)
            {
                if (configObj["type"]?.GetValue<string>() is not "object")
                {
                    Warn("config.not-object",
                        "config 的 type 应为 \"object\"（宿主按对象属性渲染设置页）");
                }

                configSchema = configObj;
            }
            else
            {
                Warn("config.invalid", "config 必须是 JSON Schema 对象，已忽略");
            }
        }

        var manifest = new ToolManifest
        {
            Id = id ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? (id ?? "(未命名)") : name!,
            Version = version ?? "0.0.0",
            Description = Str(obj, "description"),
            Author = Str(obj, "author"),
            License = Str(obj, "license"),
            Icon = icon,
            Homepage = Str(obj, "homepage"),
            Publisher = Str(obj, "publisher"),
            ApiVersion = Int(obj, "apiVersion"),
            Runtime = runtime ?? ToolRuntimes.Python,
            RuntimeVersion = Str(obj, "runtimeVersion"),
            Entry = entry ?? string.Empty,
            Weight = weight,
            Lifecycle = lifecycle,
            Latency = latency,
            Needs = needs,
            ElevatedPrimitives = StrList(obj, "elevatedPrimitives"),
            ExclusiveResources = ParseExclusiveResources(obj),
            Contributes = contributes,
            ConfigSchema = configSchema,
            ToolDirectory = toolDir,
            ManifestPath = Path.GetFullPath(manifestPath),
            SourceName = sourceName,
        };

        // 把工具 id 补进诊断，便于汇总展示
        var tagged = diagnostics
            .Select(d => d.ToolId is null ? d with { ToolId = manifest.Id } : d)
            .ToList();

        return new ManifestParseResult
        {
            Manifest = manifest,
            Diagnostics = tagged,
        };
    }

    private static ToolContributions ParseContributions(
        JsonObject? node,
        string? toolId,
        List<ToolDiagnostic> diagnostics,
        string manifestPath)
    {
        if (node is null)
        {
            return ToolContributions.Empty;
        }

        void Warn(string code, string message) =>
            diagnostics.Add(new ToolDiagnostic(
                DiagnosticSeverity.Warning, code, message, manifestPath, toolId));

        // panels 里有一类诊断必须是 Error（缺 id = 结构性缺陷，该面板无法被路由）。
        // 只把 Warn 传下去会让协议 §2.2 的严重级别**静默降级**——诊断码对了、级别错了，
        // 而"级别"恰恰决定 doctor 的退出码与 healthy 判定（Error 才算清单不健康）。
        void Error(string code, string message) =>
            diagnostics.Add(new ToolDiagnostic(
                DiagnosticSeverity.Error, code, message, manifestPath, toolId));

        var commands = new List<ToolCommand>();
        var seenCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (node["commands"] is JsonArray commandArray)
        {
            foreach (var item in commandArray)
            {
                if (item is not JsonObject commandObj)
                {
                    Warn(DiagnosticCodes.CommandMissingHandler, "commands 中的条目不是对象，已跳过");
                    continue;
                }

                var commandId = Str(commandObj, "id");
                var handler = Str(commandObj, "handler");
                var title = Str(commandObj, "title") ?? commandId ?? "(未命名命令)";

                if (string.IsNullOrWhiteSpace(commandId) || string.IsNullOrWhiteSpace(handler))
                {
                    Warn(DiagnosticCodes.CommandMissingHandler,
                        $"命令 '{commandId ?? "?"}' 缺少 id 或 handler，已跳过");
                    continue;
                }

                if (!seenCommands.Add(commandId))
                {
                    Warn(DiagnosticCodes.CommandDuplicate, $"命令 id '{commandId}' 重复，后者被忽略");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(toolId) &&
                    !commandId.StartsWith(toolId + ".", StringComparison.OrdinalIgnoreCase))
                {
                    Warn(DiagnosticCodes.CommandForeignNamespace,
                        $"命令 id '{commandId}' 未使用工具 id 前缀 '{toolId}.xxx'，可能导致全局命令冲突");
                }

                commands.Add(new ToolCommand
                {
                    Id = commandId,
                    Title = title,
                    Handler = handler,
                    Description = Str(commandObj, "description"),
                    TimeoutMs = Int(commandObj, "timeoutMs"),
                });
            }
        }

        var actions = new List<ToolAction>();
        if (node["actions"] is JsonArray actionArray)
        {
            foreach (var item in actionArray)
            {
                if (item is not JsonObject actionObj)
                {
                    continue;
                }

                var actionId = Str(actionObj, "id");
                var handler = Str(actionObj, "handler");
                if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(handler))
                {
                    Warn(DiagnosticCodes.ActionMissingHandler, "action 缺少 id 或 handler，已跳过");
                    continue;
                }

                actions.Add(new ToolAction
                {
                    Id = actionId,
                    Title = Str(actionObj, "title") ?? actionId,
                    Handler = handler,
                    When = Str(actionObj, "when"),
                });
            }
        }

        var hotkeys = new List<ToolHotkey>();
        if (node["hotkeys"] is JsonArray hotkeyArray)
        {
            foreach (var item in hotkeyArray)
            {
                if (item is not JsonObject hotkeyObj)
                {
                    continue;
                }

                var command = Str(hotkeyObj, "command");
                var def = Str(hotkeyObj, "default");
                if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(def))
                {
                    Warn(DiagnosticCodes.HotkeyInvalid, "hotkey 缺少 command 或 default，已跳过");
                    continue;
                }

                if (commands.Count > 0 &&
                    !commands.Exists(c => string.Equals(c.Id, command, StringComparison.OrdinalIgnoreCase)))
                {
                    Warn(DiagnosticCodes.HotkeyUnknownCommand,
                        $"热键引用了本工具未声明的命令 '{command}'");
                }

                // input：**未声明（null/空）= Clipboard** —— 兼容默认，不是随手取的：
                // 热键路径历史上就一律注入剪贴板，且热键命令可能没有 menus 项可继承声明
                //（filehash.hash 正是这种）。
                // 🔴 不能把 TryParseMenuInput 的"缺省 = None"语义直接落到字段上 ——
                //    那会把"未声明"静默变成"不注入"，工具侧表现为"缺少参数"。
                //    （verify-desktop.py §2b 的两条热键断言守的就是这个语义。）
                // 只有"写了但不认识"才警告 —— 与 menus / weight / lifecycle 同口径。
                var hkInputRaw = Str(hotkeyObj, "input");
                MenuInput hkInput;
                if (string.IsNullOrWhiteSpace(hkInputRaw))
                {
                    hkInput = MenuInput.Clipboard;
                }
                else if (!ToolEnumExtensions.TryParseMenuInput(hkInputRaw, out hkInput))
                {
                    hkInput = MenuInput.Clipboard;
                    Warn(DiagnosticCodes.MenuUnknownInput,
                        $"热键 '{command}' 的 input '{hkInputRaw}' 无法识别，按 clipboard 处理（可选 none / clipboard / shellSelection）");
                }

                hotkeys.Add(new ToolHotkey
                {
                    Command = command,
                    Default = def,
                    Title = Str(hotkeyObj, "title"),
                    Input = hkInput,
                    Resolved = HotkeyCombo.TryParse(def),
                });
            }
        }

        var menus = new List<ToolMenu>();
        if (node["menus"] is JsonArray menuArray)
        {
            foreach (var item in menuArray)
            {
                if (item is not JsonObject menuObj)
                {
                    continue;
                }

                var location = Str(menuObj, "location");
                var command = Str(menuObj, "command");
                if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                if (commands.Count > 0 &&
                    !commands.Exists(c => string.Equals(c.Id, command, StringComparison.OrdinalIgnoreCase)))
                {
                    Warn(DiagnosticCodes.MenuUnknownCommand,
                        $"菜单项引用了本工具未声明的命令 '{command}'");
                }

                // input：缺省（null/空）= none，属正常写法不报警告；只有"写了但不认识"才警告
                // —— 与 weight / lifecycle / latency 同口径。
                var inputRaw = Str(menuObj, "input");
                if (!ToolEnumExtensions.TryParseMenuInput(inputRaw, out var input))
                {
                    input = MenuInput.None;
                    if (!string.IsNullOrWhiteSpace(inputRaw))
                    {
                        Warn(DiagnosticCodes.MenuUnknownInput,
                            $"菜单项 '{command}' 的 input '{inputRaw}' 无法识别，按 none 处理（可选 none / clipboard / shellSelection）");
                    }
                }

                menus.Add(new ToolMenu
                {
                    Location = location,
                    Command = command,
                    Group = Str(menuObj, "group"),
                    Input = input,
                });
            }
        }

        // ── panels（P4 Wave 2c，见 docs/P4-Wave2c-面板协议.md §2）──
        // Error 级只在"结构性缺陷"上用（缺 id）；其余降级可用，一律 Warning。
        var panels = ParsePanels(node["panels"], commands, Warn, Error);

        return new ToolContributions
        {
            Commands = commands,
            Actions = actions,
            Hotkeys = hotkeys,
            Menus = menus,
            Panels = panels,
        };
    }

    /// <summary>
    /// 解析 <c>contributes.panels</c>（协议 §2.2 字段表）。
    /// 与 commands / menus 同口径：illegal 项出诊断并跳过，绝不让整份清单失效。
    /// </summary>
    private static List<ToolPanel> ParsePanels(
        JsonNode? panelsNode,
        List<ToolCommand> commands,
        Action<string, string> warn,
        Action<string, string> error)
    {
        var panels = new List<ToolPanel>();

        if (panelsNode is null)
        {
            return panels;
        }

        if (panelsNode is not JsonArray panelArray)
        {
            warn(DiagnosticCodes.PanelsNotArray, "contributes.panels 必须是数组，已忽略该字段");
            return panels;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in panelArray)
        {
            if (item is not JsonObject panelObj)
            {
                error(DiagnosticCodes.PanelMissingId, "panels 中的条目不是对象，已跳过");
                continue;
            }

            var panelId = Str(panelObj, "id");
            if (string.IsNullOrWhiteSpace(panelId))
            {
                // Error：没有 id 就没有 tool.panel.data 的路由目标，该面板根本无法工作。
                // 与尺寸/刷新越界（有默认值、降级可用）不同 —— 这是**结构性缺陷**。
                error(DiagnosticCodes.PanelMissingId, "面板缺少必填字段 id，已跳过");
                continue;
            }

            if (panelId.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(
                    panelId.Trim().ToLowerInvariant(), @"^[a-z0-9][a-z0-9-]*$"))
            {
                // 非法 id 同样是结构性缺陷：即便写了个 id，宿主也路由不到它
                error(DiagnosticCodes.PanelMissingId,
                    $"面板 id '{panelId}' 非法：只允许小写字母、数字与连字符，且以字母或数字开头，长度 ≤64");
                continue;
            }

            panelId = panelId.Trim().ToLowerInvariant();

            if (!seen.Add(panelId))
            {
                warn(DiagnosticCodes.PanelDuplicate, $"面板 id '{panelId}' 重复，后者被忽略");
                continue;
            }

            // ── 尺寸：越界则钳制 + 警告（不拒绝，因为有合理默认语义）──
            var width = Int(panelObj, "width") ?? ToolPanelLimits.DefaultWidth;
            var clampedWidth = ToolPanelLimits.ClampWidth(width);
            if (clampedWidth != width)
            {
                warn(DiagnosticCodes.PanelSizeOutOfRange,
                    $"面板 '{panelId}' 的 width {width} 越界，已钳制为 {clampedWidth}"
                    + $"（合法区间 {ToolPanelLimits.MinWidth}~{ToolPanelLimits.MaxWidth}）");
            }

            var height = Int(panelObj, "height") ?? ToolPanelLimits.DefaultHeight;
            var clampedHeight = ToolPanelLimits.ClampHeight(height);
            if (clampedHeight != height)
            {
                warn(DiagnosticCodes.PanelSizeOutOfRange,
                    $"面板 '{panelId}' 的 height {height} 越界，已钳制为 {clampedHeight}"
                    + $"（合法区间 {ToolPanelLimits.MinHeight}~{ToolPanelLimits.MaxHeight}）");
            }

            // ── refreshMs：越界归 0（不自动刷新）──
            var refreshMs = Int(panelObj, "refreshMs") ?? 0;
            var normalizedRefresh = ToolPanelLimits.NormalizeRefreshMs(refreshMs);
            if (normalizedRefresh != refreshMs)
            {
                warn(DiagnosticCodes.PanelRefreshOutOfRange,
                    $"面板 '{panelId}' 的 refreshMs {refreshMs} 非法，已归 0（不自动刷新）"
                    + $"（合法值 0 或 {ToolPanelLimits.MinRefreshMs}~{ToolPanelLimits.MaxRefreshMs}）");
            }

            // ── 按钮引用的命令必须存在（否则"能显示、点了报未知命令"）──
            // 注意：只在校验命令清单非空时检查 —— 与 menus/hotkeys 同口径（空清单时无从判断）。
            if (panelObj["commands"] is JsonArray refArray)
            {
                foreach (var refItem in refArray)
                {
                    if (refItem is not JsonValue refValue ||
                        !refValue.TryGetValue<string>(out var refCommand) ||
                        string.IsNullOrWhiteSpace(refCommand))
                    {
                        continue;
                    }

                    if (commands.Count > 0 &&
                        !commands.Exists(c => string.Equals(c.Id, refCommand, StringComparison.OrdinalIgnoreCase)))
                    {
                        warn(DiagnosticCodes.PanelUnknownCommand,
                            $"面板 '{panelId}' 引用了本工具未声明的命令 '{refCommand}'");
                    }
                }
            }

            panels.Add(new ToolPanel
            {
                Id = panelId,
                Title = Str(panelObj, "title") is { Length: > 0 } t ? t : panelId,
                Description = Str(panelObj, "description"),
                Width = clampedWidth,
                Height = clampedHeight,
                RefreshMs = normalizedRefresh,
            });
        }

        return panels;
    }

    private static ManifestParseResult Fail(
        string path, string sourceName, string code, string message, DiagnosticSeverity severity) =>
        new()
        {
            Diagnostics = new[]
            {
                new ToolDiagnostic(severity, code, message, path),
            },
        };

    // ── JSON 取值助手：类型不符时静默取默认值，不抛异常（校验逻辑单独负责报错）──

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static IReadOnlyList<string> StrList(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray arr)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
        }

        return list;
    }
}
