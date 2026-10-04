// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Host.Search;

namespace Eztools.Host.Launcher;

/// <summary>
/// 一个结果来源（W7，设计方案 §10.2）。实现必须满足下列契约 —— 每条都是"写错会静默退化"的地方：
///
/// <list type="number">
/// <item><b>不做节流</b>：被调用即应当真算（节流由 <see cref="QueryPump"/> 统一承担）。
///   自己再节流 = 双重防抖 ⇒ 结果比今天更慢（files 尤其致命）。</item>
/// <item><b>不做代次判断</b>：<see cref="LauncherQuery.Generation"/> 原样回传到
///   <see cref="LauncherResultSet.Generation"/>（判断在 <see cref="QueryRouter"/>）——
///   判断散落多处必然漂移。</item>
/// <item><b>不 marshal UI</b>：只返回值，绝不碰 Dispatcher/控件（否则线程纪律破坏且探针不可测）。</item>
/// <item><b>不吞异常</b>：拿不准就让它抛，由 <see cref="QueryRouter"/> 隔离成段位错误
///   —— 自己吞掉 = 静默失败（静默失败登记册 S2 家族）。</item>
/// <item><b><see cref="IsReady"/>=false 必须是"暂不可用"而非"无结果"</b>：
///   不得用它表达"这次没命中"（"没准备好"与"没有结果"混同 = S9 家族，"显示的数字语义是错的"）。</item>
/// <item><b>段内顺序由 provider 自己负责</b>：router 只做**段间**排序；
///   files 段原序透传、**绝不重排**（重排直接破坏"与今天逐字节一致"，设计方案 R14）。</item>
/// <item><b>不返回超过 <see cref="LauncherQuery.Limit"/> 条</b>；超限部分用
///   <see cref="LauncherResultSet.Total"/> 表达。</item>
/// </list>
/// </summary>
public interface ILauncherProvider
{
    /// <summary>稳定 id（= 配置键 <c>launcher.providers</c> 里的取值），必须属于 <see cref="LauncherProviderRegistry.KnownIds"/>。</summary>
    string Id { get; }

    /// <summary>用户可见的短名（段位错误文案用："应用索引暂不可用"）。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 是否可参与本次查询。false ⇒ 本次**静默缺席**（不调用、不占段位、不进错误面）。
    /// 语义 = "还没准备好"（如 apps 首扫进行中），**不等于**"坏了"（坏了走 <see cref="QueryAsync"/> 抛异常）。
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// 查询。线程纪律：可能在线程池线程被调用，实现内不得碰 Dispatcher/控件。
    /// </summary>
    Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct);
}

/// <summary>
/// "延迟就绪"的 provider 可选实现的接口（W7-b，设计方案 R10）。
///
/// <para><b>为什么需要它</b>：apps 首次全扫要几十毫秒 —— 在它就绪前用户已经打完了字，
/// 那一轮查询里 apps 静默缺席。等它就绪时若不补发一次，用户得**再敲一个字符**才能看见应用结果
/// （"搜不到刚装的应用"这类反馈的根因往往就是这个）。</para>
///
/// <para>本接口刻意只有一个事件、没有方法：就绪与否仍由 <see cref="ILauncherProvider.IsReady"/> 表达，
/// 这里只负责"通知"。事件可能在线程池线程上触发 —— 订阅方自己 marshal。</para>
/// </summary>
public interface ILauncherReadyNotifier
{
    /// <summary>扫描完成（就绪或失败都对）时触发。</summary>
    event Action? BecameReady;
}

/// <summary>归并时的段位（决定**段间**顺序：pin → apps → files）。</summary>
public enum LauncherSegment{
    /// <summary>确定性命中段（calc/unit/encode），固定置顶。</summary>
    Pin,

    /// <summary>应用段。</summary>
    Apps,

    /// <summary>文件段（原序透传）。</summary>
    Files,

    /// <summary>
    /// 剪贴板历史段（W10-a，**原序透传**）。独立成段而不并入 Files 或 Pin，两条硬理由：
    /// ① 合并时 files 段要原序透传且携带 files 专属计数（"显示 N / 共 M 条"），clip 混进去会让
    ///    状态行的数字变成"剪贴板条数"（S9：显示的数字语义是错的）；
    /// ② pin 段按 Score/Title 重排，而 D5 要求 clip 保持 store 返回序。
    /// </summary>
    Clip,
}

/// <summary>
/// provider 注册表：**白名单的唯一出处**（设计方案 §10.2 注）。
///
/// <para><b>★ 白名单 = 当前已实现的 id（<see cref="KnownIds"/>），不是"五个常量全列"</b>：
/// 尚未实现的 provider 被配置时，用户得到的是 <c>未知 provider "unit"（已知：files）</c>
/// —— 可读且自我解释；若白名单预先含 unit 却无实现，用户会得到"配置合法但功能不存在"的
/// <b>静默缺席</b>（静默失败登记册 S2 家族）。<see cref="KnownIds"/> 随阶段增长。</para>
/// </summary>
public static class LauncherProviderRegistry
{
    public const string Files = "files";
    public const string Apps = "apps";
    public const string Calc = "calc";

    /// <summary>unit（单位换算）与 encode（编码转换）的 id 常量。</summary>
    public const string Unit = "unit";

    /// <inheritdoc cref="Unit"/>
    public const string Encode = "encode";

    /// <summary>剪贴板历史（W10-a）。</summary>
    public const string Clip = "clip";

    /// <summary>系统命令（W10-a）。</summary>
    public const string Command = "cmd";

    /// <summary>**当前已实现**的 provider id（= 配置白名单）。随阶段增长；错误文案从这里取"已知集合"。</summary>
    public static IReadOnlyList<string> KnownIds { get; } =
        [Files, Apps, Calc, Unit, Encode, Clip, Command];

    /// <summary>
    /// 默认启用集合 = **全部已实现**（与 <see cref="KnownIds"/> 同源 ⇒ 杜绝"默认值里有未实现的 id"）。
    /// ★ 这条不是风格问题：schema 的 <c>default</c> 文本必须能被 <c>LauncherPrefs.Parse</c> 解析通过，
    /// 否则"默认配置本身就是非法配置"（selftest 有一条交叉断言钉住它）。
    /// </summary>
    public static IReadOnlyList<string> DefaultEnabled => KnownIds;

    /// <summary>id 是否已知（大小写不敏感 —— 与工具 id 的匹配口径一致）。</summary>
    public static bool IsKnown(string? id) =>
        id is not null && KnownIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>"已知：a、b、c"形态的文案片段（配置报错用，单点生成避免各处各写一份）。</summary>
    public static string KnownListText() => string.Join("、", KnownIds);

    /// <summary>段位归属（**段间顺序策略的唯一出处**）。未知 id 归入 pin 段（不静默丢弃）。</summary>
    public static LauncherSegment SegmentOf(string providerId)
    {
        if (string.Equals(providerId, Files, StringComparison.OrdinalIgnoreCase))
        {
            return LauncherSegment.Files;
        }

        return string.Equals(providerId, Apps, StringComparison.OrdinalIgnoreCase)
            ? LauncherSegment.Apps
            : string.Equals(providerId, Clip, StringComparison.OrdinalIgnoreCase)
                ? LauncherSegment.Clip
                : LauncherSegment.Pin;
    }}

/// <summary>
/// provider 集合装配入口。**生产与探针走同一个工厂** —— 探针显式传
/// "仅 files"集合（设计方案 §5「探针装配纪律」）：真机上开始菜单可能含中文名应用，
/// 若搜索窗渲染探针放进 apps provider，`itemsCount == 200` 这类断言会被环境命中打破（假红）。
/// </summary>
public static class LauncherProviderSet
{
    /// <summary>仅 files（搜索窗渲染/唤出探针的装配形态）。</summary>
    public static IReadOnlyList<ILauncherProvider> FilesOnly(SearchIndexClient client) =>
        [new FilesProvider(client)];

    /// <summary>
    /// 按"来源开关"过滤 provider id（W10-a）：<c>clip.enabled=false</c> ⇒ 移除 <c>clip</c>。
    ///
    /// <para><b>为什么单独抽成纯函数</b>：这是"段位静默缺席"的开关 —— 失效的表现是
    /// "关了还在"或"没关却没了"，**两者都不报错**（静默失败登记册 S2 家族）。放在 UI 装配里
    /// 就没人能钉住它；抽到平台中立层后 <c>ezt selftest</c> 可直接穷举。</para>
    ///
    /// <para>语义 = "从**已启用集合**里剔除"（用户本来就没配 clip 时，过滤是空操作）。</para>
    /// </summary>
    /// <param name="providers">已启用的 provider id 列表（<c>launcher.providers</c> 解析结果）。</param>
    /// <param name="clipEnabled"><c>clip.enabled</c> 的生效值。</param>
    public static IReadOnlyList<string> ApplySwitches(IReadOnlyList<string> providers, bool clipEnabled)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (clipEnabled)
        {
            return providers;
        }

        var list = new List<string>(providers.Count);
        foreach (var id in providers)
        {
            if (!string.Equals(id, LauncherProviderRegistry.Clip, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(id);
            }
        }

        return list;
    }

    /// <summary>
    /// 按配置装配（生产路径，W7-b）。**"未启用"与"未就绪"是两件事**：
    /// 前者在这里被筛掉（根本不进集合），后者由 <see cref="ILauncherProvider.IsReady"/> 表达。
    /// </summary>
    /// <param name="prefs">已解析的配置。</param>
    /// <param name="factories">各 provider 的构造器（由 Desktop 侧提供 —— 需要 Win32 的那些只能在那里造）。</param>
    public static IReadOnlyList<ILauncherProvider> Build(
        LauncherPrefs prefs,
        IReadOnlyDictionary<string, Func<ILauncherProvider>> factories)
    {
        var list = new List<ILauncherProvider>(prefs.Providers.Count);
        foreach (var id in prefs.Providers)
        {
            if (factories.TryGetValue(id, out var factory))
            {
                list.Add(factory());
            }
        }

        return list;
    }
}
