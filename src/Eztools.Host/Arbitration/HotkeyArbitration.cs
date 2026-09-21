using Eztools.Contracts;

namespace Eztools.Host.Arbitration;

/// <summary>一个热键仲裁参与者：谁的哪个命令、想注册哪个组合键、按什么顺序。</summary>
public sealed record HotkeyClaim(
    string ToolId,
    string Command,
    HotkeyCombo Combo,
    /// <summary>归属顺序（先者胜）。用注册表的确定性顺序（按工具 Id 排序）。</summary>
    int Order);

/// <summary>仲裁结果：每个组合键的赢家与落选者。</summary>
public sealed class HotkeyArbitrationResult
{
    /// <summary>资源 id（规范化组合键）→ 赢家。赢家才有资格被 RegisterHotKey。</summary>
    public IReadOnlyDictionary<string, HotkeyClaim> Winners { get; init; } =
        new Dictionary<string, HotkeyClaim>();

    /// <summary>落选者（含落选原因：资源被哪个工具占用）。</summary>
    public IReadOnlyList<(HotkeyClaim Claim, string HeldBy)> Losers { get; init; } =
        Array.Empty<(HotkeyClaim, string)>();
}

/// <summary>
/// 热键仲裁（§6.3）：**保留先注册者**。
///
/// 纯逻辑、无 Win32 依赖 —— 同一条规则同时服务三处：
/// CLI 的冲突展示、Desktop 的注册决策、验收断言。
/// 规则的确定性来自 <see cref="HotkeyClaim.Order"/>：调用方传入注册表的稳定顺序
/// （工具按 Id 排序），同样的输入永远得到同样的赢家 —— 验收断言因此可能。
/// </summary>
public static class HotkeyArbitration
{
    public static HotkeyArbitrationResult Arbitrate(IEnumerable<HotkeyClaim> claims)
    {
        var winners = new Dictionary<string, HotkeyClaim>();
        var losers = new List<(HotkeyClaim, string)>();

        foreach (var claim in claims.OrderBy(c => c.Order))
        {
            var resourceId = claim.Combo.ResourceId;
            if (winners.TryGetValue(resourceId, out var incumbent))
            {
                losers.Add((claim, incumbent.ToolId));
            }
            else
            {
                winners[resourceId] = claim;
            }
        }

        return new HotkeyArbitrationResult { Winners = winners, Losers = losers };
    }
}
