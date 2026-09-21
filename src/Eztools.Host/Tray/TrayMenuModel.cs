using System.Text.Json.Nodes;
using Eztools.Contracts;

namespace Eztools.Host.Tray;

/// <summary>
/// 托盘里的一个可点条目，由 <c>tool.json</c> 的 <c>contributes.menus</c> 合成
/// —— 新增工具**零 UI 代码**，与设置页走的是同一条"清单驱动"的路子（设计方案 §4.5、§7）。
/// </summary>
public sealed class TrayMenuItem
{
    public required string ToolId { get; init; }

    public required string CommandId { get; init; }

    /// <summary>显示文本，取自命令的 <c>title</c>（不是工具名）——用户关心动作，不关心谁实现的。</summary>
    public required string Title { get; init; }

    /// <summary>分组原始键（<c>menus[].group</c>）。空 = 直接挂在托盘顶层。</summary>
    public required string GroupKey { get; init; }

    /// <summary>输入来源。菜单点击**没有上下文**，靠它决定要不要注入剪贴板内容。</summary>
    public MenuInput Input { get; init; }

    /// <summary>
    /// 按声明的输入来源构造调用参数。
    ///
    /// 约定：取到的内容放进 <c>args["input"]</c>，**宿主不必知道各工具把参数叫什么名字**
    /// ——工具侧自己从 <c>args["input"]</c> 映射到 <c>text</c> / <c>path</c> …
    /// 这样避免"宿主硬编码参数名"的耦合（设计方案 §4.5 的"贡献点是加法"）。
    /// </summary>
    public JsonObject BuildArgs(string? clipboardText) => Input switch
    {
        MenuInput.Clipboard => new JsonObject { ["input"] = clipboardText ?? string.Empty },
        _ => new JsonObject(),
    };

    public override string ToString() => $"{Title} [{CommandId}] input={Input.ToWire()}";
}

/// <summary>托盘菜单里的一个分组（对应 <c>menus[].group</c>）。</summary>
public sealed class TrayMenuGroup
{
    /// <summary>分组显示名。**空字符串表示该条目直接挂在托盘顶层**，不建子菜单。</summary>
    public required string Title { get; init; }

    public required IReadOnlyList<TrayMenuItem> Items { get; init; }
}

/// <summary>
/// 合成后的托盘菜单。**与 UI 框架无关** —— 所以它能被断言、能被 <c>ezt tray</c> 打印、
/// 也能被将来的设置页复用；真正画菜单的只有 <c>Eztools.Desktop</c> 那一小段。
/// 这条边界是刻意的：UI 栈还没定，但菜单**该怎么排**与 UI 栈无关。
/// </summary>
public sealed class TrayMenuModel
{
    public static readonly TrayMenuModel Empty = new()
    {
        Groups = Array.Empty<TrayMenuGroup>(),
        Items = Array.Empty<TrayMenuItem>(),
    };

    public required IReadOnlyList<TrayMenuGroup> Groups { get; init; }

    /// <summary>扁平化的全部条目（分组之前），便于断言与遍历。</summary>
    public required IReadOnlyList<TrayMenuItem> Items { get; init; }

    public int Count => Items.Count;
}
