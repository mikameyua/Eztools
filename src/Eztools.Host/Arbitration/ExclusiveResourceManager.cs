// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace Eztools.Host.Arbitration;

/// <summary>
/// 独占资源运行时注册表（§8.3）。
///
/// 三类消费方（现在与将来）：
/// ① 热键 —— Desktop 注册成功的每个 <c>hotkey.*</code> 资源在此 claim，进程内唯一持有者；
/// ② <c>resident</c> 工具启动时 claim 其 <c>exclusiveResources</c>（P2 生命周期部分接入）；
/// ③ 诊断与 CLI（<c>ezt resources</code>）展示当前持有者。
///
/// 边界（§8.3 的注）：**只管 Eztools 自己的工具**。第三方程序（GlazeWM 等）占用的资源
/// 无法在这里阻止，只能由调用方在启用前告知用户。
/// </summary>
public sealed class ExclusiveResourceManager
{
    private readonly ConcurrentDictionary<string, string> _holders = new();

    /// <summary>尝试占用。成功 = true；false = 已被 <paramref name="byToolId"/> 之外的工具持有。</summary>
    public bool TryClaim(string resourceId, string byToolId)
    {
        var holder = _holders.GetOrAdd(resourceId, byToolId);
        return holder == byToolId;
    }

    /// <summary>当前持有者；无人持有时返回 null。</summary>
    public string? HolderOf(string resourceId) =>
        _holders.TryGetValue(resourceId, out var holder) ? holder : null;

    /// <summary>释放某工具持有的全部资源（工具退出/被禁用时调用），返回释放的数量。</summary>
    public int ReleaseBy(string byToolId)
    {
        var released = 0;
        foreach (var (resource, holder) in _holders)
        {
            if (holder == byToolId && _holders.TryRemove(new KeyValuePair<string, string>(resource, holder)))
            {
                released++;
            }
        }

        return released;
    }

    /// <summary>快照：资源 → 持有者。</summary>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        _holders.ToDictionary(kv => kv.Key, kv => kv.Value);
}
