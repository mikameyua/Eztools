// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;

namespace Eztools.Host.Config;

/// <summary>
/// 桌面宿主设置的 schema 与保留 id（W4-c，FR-9）。
///
/// 宿主级功能（搜索热键 / OCR 热键 / OCR 语言）没有 tool.json 可声明 config，
/// 落法是占用一个**保留 toolId**（<see cref="SectionId"/>，配置文件 = <c>config/desktop.json</c>）
/// 挂进 P1a 配置中心 —— 校验、原子写、损坏恢复、CLI 与设置窗口双入口全部复用既有链路。
///
/// <b>为什么放在 Eztools.Host 而不是 Eztools.Desktop</b>：schema 的消费者有两个 ——
/// Desktop 的设置窗口与托盘、**Cli 的 <c>ezt config</c>**（get/set/schema 对保留 id 也要能工作，
/// 否则"CLI 与窗口改同一个文件"的闭环就缺了 CLI 那一半）。Desktop 引用 Host，
/// 反向不成立，所以本体必须住在 Host。
/// </summary>
public static class HostSettingsSchema
{
    /// <summary>宿主设置在 ConfigStore 里的保留 toolId（对应 config/desktop.json）。</summary>
    public const string SectionId = "desktop";

    public const string DefaultSearchHotkey = "Ctrl+Alt+S";
    public const string DefaultOcrHotkey = "Ctrl+Alt+O";
    public const string DefaultClipHotkey = "Ctrl+Alt+V";

    public const string KeySearchHotkey = "search.hotkey";
    public const string KeyOcrHotkey = "ocr.hotkey";
    public const string KeyOcrLanguage = "ocr.language";
    public const string KeyClipEnabled = "clip.enabled";
    public const string KeyClipHotkey = "clip.hotkey";
    public const string KeyClipMaxItems = "clip.max-items";
    public const string KeyClipImageRetentionDays = "clip.image-retention-days";
    public const string KeyClipBlacklist = "clip.blacklist";

    /// <summary>剪贴板历史上限默认值（FR-4；可配 100~50000，schema 校验）。</summary>
    public const int DefaultClipMaxItems = 1000;

    /// <summary>图片默认保留天数（FR-4；pinned 豁免）。</summary>
    public const int DefaultClipImageRetentionDays = 30;

    /// <summary>读一个宿主设置的整型有效值（clip.max-items 等用）。键缺失/不是数字返回 null。</summary>
    public static int? TryGetInt(ConfigStore configs, string key)
    {
        var effective = configs.Effective(SectionId, SchemaJson());
        return effective[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    }

    /// <summary>读一个宿主设置的布尔有效值（clip.enabled 用）。键缺失/不是布尔返回 null。</summary>
    public static bool? TryGetBool(ConfigStore configs, string key)
    {
        var effective = configs.Effective(SectionId, SchemaJson());
        return effective[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    /// <summary>是否宿主设置节的保留 id（大小写不敏感 —— 与工具 id 的匹配口径一致）。</summary>
    public static bool IsSectionId(string toolId) =>
        string.Equals(toolId, SectionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读一个宿主设置的有效值（文件值合并 schema 默认值后的结果）。
    /// 键不存在 / 值不是字符串返回 <c>null</c> —— 调用方决定回落语义。
    /// </summary>
    public static string? TryGetString(ConfigStore configs, string key)
    {
        var effective = configs.Effective(SectionId, SchemaJson());
        return effective[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>
    /// 宿主设置 schema（设置窗口按它渲染控件、ConfigStore 按它校验、CLI 按它回报）。
    /// **每次调用返回新实例** —— JsonNode 挂过父不能复用，调用方可能把同一份树
    /// 传进多处（Load / Set），工厂语义避免共享子树的再入问题（§四.4）。
    /// </summary>
    public static JsonObject SchemaJson() => JsonNode.Parse("""
        {
          "properties": {
            "search.hotkey": {
              "type": "string",
              "title": "搜索文件热键",
              "description": "唤出搜索窗的全局热键，格式如 Ctrl+Alt+S。保存后立即生效（自动重注册）；被其它程序占用时会气泡提示。",
              "default": "Ctrl+Alt+S",
              "x-order": 1
            },
            "ocr.hotkey": {
              "type": "string",
              "title": "屏幕取字热键",
              "description": "唤出屏幕取字（OCR 框选）遮罩的全局热键，格式如 Ctrl+Alt+O。保存后立即生效（自动重注册）；被其它程序占用时会气泡提示。",
              "default": "Ctrl+Alt+O",
              "x-order": 2
            },
            "ocr.language": {
              "type": "string",
              "title": "OCR 识别语言",
              "description": "BCP-47 标签（如 zh-Hans-CN / en-US），留空 = 自动（跟随系统首选语言）。可用清单跑 `ezt ocr langs` 查看；配置了未安装的语言时唤出会明确报错，不会静默回落。",
              "default": "",
              "x-order": 3
            },
            "clip.enabled": {
              "type": "boolean",
              "title": "剪贴板历史总开关",
              "description": "关闭后：不监听剪贴板、不注册面板热键、托盘菜单隐藏（已有历史保留在库里不删除）。",
              "default": true,
              "x-order": 4
            },
            "clip.hotkey": {
              "type": "string",
              "title": "剪贴板历史热键",
              "description": "唤出剪贴板历史面板的全局热键，格式如 Ctrl+Alt+V（刻意避开 Win+V / Win+Shift+V）。保存后立即生效（自动重注册）；被其它程序占用时会气泡提示。",
              "default": "Ctrl+Alt+V",
              "x-order": 5
            },
            "clip.max-items": {
              "type": "integer",
              "title": "历史上限（条）",
              "description": "超出后自动删除最旧的未置顶条目（置顶条目永不删除）。范围 100~50000。",
              "default": 1000,
              "x-order": 6
            },
            "clip.image-retention-days": {
              "type": "integer",
              "title": "图片保留天数",
              "description": "图片条目超过该天数自动清理（置顶豁免）；文本/文件条目只受条数上限管。",
              "default": 30,
              "x-order": 7
            },
            "clip.blacklist": {
              "type": "string",
              "title": "隐私黑名单（进程名）",
              "description": "这些进程里复制的内容不入库。分号分隔进程名（如 1password.exe;Bitwarden;keepassxc.exe），.exe 后缀可省略。",
              "default": "",
              "x-order": 8
            }
          }
        }
        """)!.AsObject();
}
