// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

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

    /// <summary>
    /// 输入来源。菜单/热键触发**本身没有上下文**，靠它决定宿主注入什么
    /// （剪贴板文本 / 资源管理器当前选中项）。
    /// </summary>
    public MenuInput Input { get; init; }

    /// <summary>
    /// 调用**成功后**自动带到前台的面板 id（来自 <c>hotkeys[].opensPanel</c>，菜单项恒为 null）。
    ///
    /// 放在托盘条目上而不是让工具自己请求：本项目**没有**"工具操纵宿主 UI"的协议方法，
    /// 也不该为这一点开（那会让任意工具都能弹宿主窗口）。而"这条热键顺带开哪个面板"
    /// 是清单本来就写得下的事，宿主读清单即可。
    ///
    /// 语义是"**成功后**"而非"触发即"：拿不到上下文时用户该先看到那句解释
    /// （如速览的"请先选中文件"），而不是收到一个空面板。
    /// </summary>
    public string? OpensPanel { get; init; }

    /// <summary>
    /// 按声明的输入来源构造调用参数。
    ///
    /// 约定：取到的内容放进 <c>args["input"]</c>，**宿主不必知道各工具把参数叫什么名字**
    /// ——工具侧自己从 <c>args["input"]</c> 映射到 <c>text</c> / <c>path</c> …
    /// 这样避免"宿主硬编码参数名"的耦合（设计方案 §4.5 的"贡献点是加法"）。
    ///
    /// <see cref="MenuInput.ShellSelection"/> 的注入形状：<c>input</c> = 首项路径
    /// （string，无选中项为 <c>""</c>），<c>inputPaths</c> = 全部选中项（string[]，可为空数组）。
    /// **刻意不让 <c>input</c> 在两种声明下类型不同** —— 严格类型下
    /// "同一个键两种类型"正是工具侧最容易踩的坑。
    /// </summary>
    public JsonObject BuildArgs(string? clipboardText, IReadOnlyList<string>? shellPaths = null) => Input switch
    {
        MenuInput.Clipboard => new JsonObject { ["input"] = clipboardText ?? string.Empty },
        MenuInput.ShellSelection => BuildShellSelectionArgs(shellPaths),
        _ => new JsonObject(),
    };

    private static JsonObject BuildShellSelectionArgs(IReadOnlyList<string>? paths)
    {
        var list = paths ?? Array.Empty<string>();
        var array = new JsonArray();
        foreach (var path in list)
        {
            array.Add(path);
        }

        return new JsonObject
        {
            ["input"] = list.Count > 0 ? list[0] : string.Empty,
            ["inputPaths"] = array,
        };
    }

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
