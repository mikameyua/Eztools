using Eztools.Host.Registry;

namespace Eztools.Host.Tray;

/// <summary>
/// 从注册表合成托盘菜单。
///
/// **组织维度是 `group`，不是工具** —— 用的人的视角是"我想对文件做什么"，
/// 而不是"我想用哪个工具"。两个工具都声明 <c>group: "file"</c> 时自然合并到同一组。
///
/// 顺序**完全确定**：组按 group 键序、组内按「工具 id → 命令 id」序。
/// 为什么不按工具的扫描顺序：那会让同一份配置在不同机器上排出不同菜单，
/// 也会让验收断言变得不稳定（"有时候通过"是最难查的一类问题）。
/// </summary>
public static class TrayMenuBuilder
{
    /// <summary>菜单位置标识。其它取值（如 <c>main</c>）属于 P1b 的主界面菜单，这里不处理。</summary>
    public const string TrayLocation = "tray";

    /// <summary>
    /// 已知分组的显示名。**未知分组原样显示，不报错** —— 新增分组不该要求先改宿主，
    /// 那正是"新增工具零 UI 代码"的一部分。
    /// </summary>
    private static readonly Dictionary<string, string> GroupDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file"] = "文件",
        ["text"] = "文本",
        ["system"] = "系统",
        ["clipboard"] = "剪贴板",
    };

    public static TrayMenuModel Build(ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var items = Collect(registry);
        if (items.Count == 0)
        {
            return TrayMenuModel.Empty;
        }

        items.Sort(static (a, b) =>
        {
            var byGroup = string.CompareOrdinal(a.GroupKey, b.GroupKey);
            if (byGroup != 0)
            {
                return byGroup;
            }

            var byTool = string.CompareOrdinal(a.ToolId, b.ToolId);
            return byTool != 0 ? byTool : string.CompareOrdinal(a.CommandId, b.CommandId);
        });

        // 已按 GroupKey 排好序，GroupBy 会保持首次出现的顺序
        var groups = items
            .GroupBy(static i => i.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static g => new TrayMenuGroup
            {
                Title = GroupDisplayName(g.Key),
                Items = g.ToList(),
            })
            .ToList();

        return new TrayMenuModel { Groups = groups, Items = items };
    }

    /// <summary>分组显示名；空键返回空串，表示"直接挂顶层"。</summary>
    public static string GroupDisplayName(string? groupKey)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return string.Empty;
        }

        return GroupDisplayNames.TryGetValue(groupKey, out var display) ? display : groupKey;
    }

    private static List<TrayMenuItem> Collect(ToolRegistry registry)
    {
        var items = new List<TrayMenuItem>();

        foreach (var tool in registry.Tools)
        {
            // 禁用的工具不进托盘 —— 否则点下去只会得到"工具已禁用"
            if (!tool.Enabled)
            {
                continue;
            }

            var commands = tool.Manifest.Contributes.Commands;
            foreach (var menu in tool.Manifest.Contributes.Menus)
            {
                if (!string.Equals(menu.Location, TrayLocation, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 标题取命令的 title；找不到就退回命令 id。
                // 找不到这件事清单校验已经报过 contributes.menu-unknown-command 诊断，这里不重复报。
                var title = commands
                    .FirstOrDefault(c => string.Equals(c.Id, menu.Command, StringComparison.OrdinalIgnoreCase))
                    ?.Title ?? menu.Command;

                items.Add(new TrayMenuItem
                {
                    ToolId = tool.Id,
                    CommandId = menu.Command,
                    Title = title,
                    GroupKey = menu.Group ?? string.Empty,
                    Input = menu.Input,
                });
            }
        }

        return items;
    }
}
