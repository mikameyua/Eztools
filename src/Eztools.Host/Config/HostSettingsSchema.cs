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
    public const string DefaultCaptureHotkey = "Ctrl+Alt+X";
    public const string DefaultPickHotkey = "Ctrl+Alt+C";

    public const string KeySearchHotkey = "search.hotkey";
    public const string KeyOcrHotkey = "ocr.hotkey";
    public const string KeyOcrLanguage = "ocr.language";
    public const string KeyClipEnabled = "clip.enabled";
    public const string KeyClipHotkey = "clip.hotkey";
    public const string KeyClipMaxItems = "clip.max-items";
    public const string KeyClipImageRetentionDays = "clip.image-retention-days";
    public const string KeyClipBlacklist = "clip.blacklist";
    public const string KeyCaptureHotkey = "capture.hotkey";
    public const string KeyPickHotkey = "pick.hotkey";
    public const string KeyColorFormat = "color.format";
    public const string KeyLauncherProviders = "launcher.providers";
    public const string KeyLauncherUsage = "launcher.usage";
    public const string KeyLauncherAlias = "launcher.alias";

    /// <summary>
    /// 启动器结果来源的默认值（W7-b）。
    ///
    /// <para><b>★ 必须与 <c>LauncherProviderRegistry.DefaultEnabled</c> 一致</b>（把当前**已实现**的
    /// provider 全列上）。两处同值是刻意的重复 —— schema 是字符串字面量而注册表是代码常量，
    /// 无法在编译期合一；改成字面量 + **selftest 交叉断言**（`LauncherPrefs.Parse(默认值).Error == null`
    /// 且集合 == `KnownIds`）比"在 schema 里插值"更稳：漏改会在验收当场变红，而不是留下一个
    /// "默认配置本身非法"的活雷。</para>
    /// </summary>
    public const string DefaultLauncherProviders = "files,apps,calc,unit,encode";

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
              "x-order": 1,
              "x-group": "搜索"
            },
            "ocr.hotkey": {
              "type": "string",
              "title": "屏幕取字热键",
              "description": "唤出屏幕取字（OCR 框选）遮罩的全局热键，格式如 Ctrl+Alt+O。保存后立即生效（自动重注册）；被其它程序占用时会气泡提示。",
              "default": "Ctrl+Alt+O",
              "x-order": 2,
              "x-group": "屏幕取字"
            },
            "ocr.language": {
              "type": "string",
              "title": "OCR 识别语言",
              "description": "BCP-47 标签（如 zh-Hans-CN / en-US），留空 = 自动（跟随系统首选语言）。可用清单跑 `ezt ocr langs` 查看；配置了未安装的语言时唤出会明确报错，不会静默回落。",
              "default": "",
              "x-order": 3,
              "x-group": "屏幕取字"
            },
            "clip.enabled": {
              "type": "boolean",
              "title": "剪贴板历史总开关",
              "description": "关闭后：不监听剪贴板、不注册面板热键、托盘菜单隐藏（已有历史保留在库里不删除）。",
              "default": true,
              "x-order": 4,
              "x-group": "剪贴板历史"
            },
            "clip.hotkey": {
              "type": "string",
              "title": "剪贴板历史热键",
              "description": "唤出剪贴板历史面板的全局热键，格式如 Ctrl+Alt+V（刻意避开 Win+V / Win+Shift+V）。保存后立即生效（自动重注册）；被其它程序占用时会气泡提示。",
              "default": "Ctrl+Alt+V",
              "x-order": 5,
              "x-group": "剪贴板历史"
            },
            "clip.max-items": {
              "type": "integer",
              "title": "历史上限（条）",
              "description": "超出后自动删除最旧的未置顶条目（置顶条目永不删除）。范围 100~50000。",
              "default": 1000,
              "x-order": 6,
              "x-group": "剪贴板历史"
            },
            "clip.image-retention-days": {
              "type": "integer",
              "title": "图片保留天数",
              "description": "图片条目超过该天数自动清理（置顶豁免）；文本/文件条目只受条数上限管。",
              "default": 30,
              "x-order": 7,
              "x-group": "剪贴板历史"
            },
            "clip.blacklist": {
              "type": "string",
              "title": "隐私黑名单（进程名）",
              "description": "这些进程里复制的内容不入库。分号分隔进程名（如 1password.exe;Bitwarden;keepassxc.exe），.exe 后缀可省略。",
              "default": "",
              "x-order": 8,
              "x-group": "剪贴板历史"
            },
            "capture.hotkey": {
              "type": "string",
              "title": "区域截图热键",
              "description": "唤出区域截图遮罩的全局热键，格式如 Ctrl+Alt+X。拖拽框选，松开鼠标即把所选区域位图复制进剪贴板。保存后立即生效（自动重注册）；被其它程序占用时会气泡提示。",
              "default": "Ctrl+Alt+X",
              "x-order": 9,
              "x-group": "截图与取色"
            },
            "pick.hotkey": {
              "type": "string",
              "title": "屏幕取色热键",
              "description": "唤出屏幕取色遮罩的全局热键，格式如 Ctrl+Alt+C。移动放大镜预览，左键单击即复制色值。保存后立即生效（自动重注册）；被其它程序占用时会气泡提示。",
              "default": "Ctrl+Alt+C",
              "x-order": 10,
              "x-group": "截图与取色"
            },
            "color.format": {
              "type": "string",
              "enum": ["hex", "rgb", "hsl"],
              "title": "取色色值格式",
              "description": "单击取色后复制进剪贴板的色值格式：hex = #rrggbb，rgb = rgb(r, g, b)，hsl = hsl(h, s%, l%)。下次唤出取色时生效。",
              "default": "hex",
              "x-order": 11,
              "x-group": "截图与取色"
            },
            "launcher.providers": {
              "type": "string",
              "title": "启动器结果来源",
              "description": "逗号分隔的结果来源：files = 文件搜索、apps = 应用启动（开始菜单/桌面快捷键/App Paths）、calc = 计算器（输入算式直接出结果）、unit = 单位换算（如 10km to mi）、encode = 编码转换（前缀 b64: / b64d: / url: / urld: / u: / ud:）。★ 文案只列**已实现**的来源：未实现的取值会被明确拒绝（不静默忽略），在描述里预先许愿会让用户得到「配置里写了、功能却不存在」。保存后下次唤出搜索窗生效。",
              "default": "files,apps,calc,unit,encode",
              "x-order": 12,
              "x-group": "搜索"
            },
            "launcher.usage": {
              "type": "boolean",
              "title": "启动器频次记忆",
              "description": "记住你常启动的应用，把它的排序提前（只作用于应用结果）。关闭后完全不读也不写（已有记录保留在文件里，重新打开即恢复）。",
              "default": true,
              "x-order": 13,
              "x-group": "搜索"
            },
            "launcher.alias": {
              "type": "string",
              "title": "启动器应用别名",
              "description": "给应用起小名：格式「别名=应用标题或路径」，分号分隔多个（如 notepad=记事本）。输入别名即可命中该应用并获得排序加成。写错的条目会被明确拒绝（不静默忽略）。",
              "default": "",
              "x-order": 14,
              "x-group": "搜索"
            }
          }
        }
        """)!.AsObject();
}
