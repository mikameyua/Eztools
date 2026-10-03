// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Host.Search;

namespace Eztools.Host.Launcher;

/// <summary>
/// 启动器结果来源类型（W7，设计方案 §10.3）。决定徽标、动作语义与渲染模板。
///
/// <para><b>File 是"默认形态"</b>：它的渲染走既有 <c>SearchWindow.HitText</c>（代码与模板零改动，
/// 设计方案 FR-10）—— 因此 <see cref="LauncherItem.FileHit"/> 对 File 项**必须非空**。</para>
/// </summary>
public enum LauncherKind
{
    /// <summary>文件/目录（来自索引进程，files provider）。</summary>
    File,

    /// <summary>可启动的应用（开始菜单/桌面快捷方式/App Paths，apps provider）。</summary>
    App,

    /// <summary>算术表达式求值结果（calc provider）。</summary>
    Calc,

    /// <summary>单位换算结果（unit provider）。</summary>
    Unit,

    /// <summary>编码转换结果（encode provider）。</summary>
    Encode,
}

/// <summary>
/// 动作种类。**枚举值与"谁产出它"是分开的**：W7-a 只产出 <see cref="Open"/> / <see cref="Reveal"/>；
/// 其余三种由 W7-b（apps）与 W7-c（calc）引入 —— 见设计方案 §4.2 的动作矩阵。
/// </summary>
public enum LauncherActionKind
{
    /// <summary>用 shell 打开（文件 / URL）。同既有 <c>BuildOpenStartInfo</c> 语义。</summary>
    Open,

    /// <summary>资源管理器定位（<c>explorer /select</c>）。同既有 <c>BuildRevealStartInfo</c> 语义。</summary>
    Reveal,

    /// <summary>启动应用（可带工作目录）。W7-b 起产出。</summary>
    Launch,

    /// <summary>定位应用目标（.lnk/exe）。W7-b 起产出。</summary>
    RevealApp,

    /// <summary>复制文本到剪贴板。W7-c 起产出（calc/unit/encode 的 Enter）。</summary>
    CopyText,
}

/// <summary>一个可执行动作。<paramref name="Argument"/> 的语义随 <see cref="LauncherActionKind"/> 而定。</summary>
public sealed record LauncherAction(LauncherActionKind Kind, string Argument);

/// <summary>
/// 一次查询请求（provider 收到的东西）。不可变。
/// <para><b>契约（设计方案 §10.2）</b>：provider 必须把 <see cref="Generation"/> **原样回传**
/// （判断留给 <see cref="QueryRouter"/>），且**不得自己做节流**（节流由 <see cref="QueryPump"/> 统一承担）。</para>
/// </summary>
public readonly record struct LauncherQuery(
    string Text,
    long Generation,
    int Limit,
    bool Substr);

/// <summary>段位错误种类（设计方案 §11.1 的错误分类表）。</summary>
public enum LauncherErrorKind
{
    /// <summary>
    /// 索引未就绪（协议 -32001）。**不算故障** —— 这是正常启动期状态，
    /// 文案是"正在建索引…打字会自动重试"，不是"出错"。
    /// </summary>
    IndexNotReady,

    /// <summary>索引不可用（超时 / 协议破坏 / 进程崩）。算故障。</summary>
    IndexUnavailable,

    /// <summary>provider 自抛异常（已被 router 捕获隔离）。算故障。</summary>
    ProviderFailed,

    /// <summary>
    /// 核心服务未运行（W8·B1）。**与 <see cref="IndexNotReady"/> 的关键区别是它不会自己好** ——
    /// 索引侧自举只在进程启动时跑一次且无重试，核心服务缺席时必然失败 ⇒ 用户等多久都一样。
    /// 所以文案不给"打字会自动重试"，而是给一个可点击的出口（见 <see cref="LauncherError.CanLaunch"/>）。
    /// </summary>
    CoreUnavailable,

    /// <summary>
    /// 核心服务在跑但没有提权（W8·B1）。读 MFT 需要提权 ⇒ 同样**只能由用户动作解决**
    /// （以管理员身份重启核心服务），不是等一等就好。
    /// </summary>
    CoreNotElevated,
}

/// <summary>
/// 段位错误。<paramref name="UserText"/> = **状态行直接显示的那句话**（文案在此单点确定，
/// 渲染层不做字符串拼接 —— 否则"索引未就绪"与"真的坏了"迟早被写成同一句）。
///
/// <para><b><paramref name="CanLaunch"/> 是"这句话有没有出口"</b>（W8·B1）：为真 ⇒ 渲染层把状态行
/// 变成可点击的入口（点了走提权启动核心服务）。**默认 false** —— 既有构造点因此零改动，
/// 且"没出口"是绝大多数错误的正确形态。</para>
///
/// <para>★ 为什么"能不能点"不靠 <see cref="LauncherErrorKind"/> 推：两者会分叉 ——
/// 将来若有"核心服务不可用但本机没有 ezt-core.exe"这种变体，语义仍是 <c>CoreUnavailable</c>，
/// 而**出口不该给**（点了必然失败）。语义与可点性分开，加变体时不用改两处。</para>
/// </summary>
public sealed record LauncherError(
    LauncherErrorKind Kind,
    int Code,
    string UserText,
    string Detail,
    bool CanLaunch = false);

/// <summary>
/// 统一结果项（设计方案 §10.3）。
///
/// <para><b>★ File 项必须携带 <see cref="FileHit"/></b>：渲染层凭它走既有
/// <c>SearchWindow.HitText</c>（零改动）—— 这是 FR-10"文件项渲染零变化"的实现手段，
/// 也是 <see cref="QueryRouter"/> 不变量 I11 的内容。</para>
/// </summary>
public sealed record LauncherItem(
    LauncherKind Kind,
    string Title,
    string Subtitle,
    string IconHint,
    double Score,
    IReadOnlyList<(int Start, int Len)> Highlights,
    LauncherAction? PrimaryAction,
    LauncherAction? SecondaryAction,
    SearchHitDto? FileHit);

/// <summary>
/// 一个 provider 的一轮结果。<see cref="Dropped"/>=true 表示结果被**过期闸**作废
/// （索引侧会话状态已变）⇒ 消费方**沿用上一轮该段内容**，不清空、不覆盖
/// （等价于既有"丢弃回调 ⇒ 界面不动"的语义）。
/// </summary>
public sealed record LauncherResultSet(
    long Generation,
    string ProviderId,
    IReadOnlyList<LauncherItem> Items,
    int Total,
    int ElapsedMs,
    bool Dropped,
    LauncherError? Error)
{
    /// <summary>本轮该段是否被**接受**（既非错误也非过期）。</summary>
    public bool Accepted => Error is null && !Dropped;
}

/// <summary>
/// 渲染模型（router → 窗口的唯一下行通道，设计方案 §10.3）。
///
/// <para><b>为什么 files 的计数单独带</b>：状态行右侧今天显示的是
/// <c>显示 {filesHits} / 共 {filesTotal} 条</c> —— 那是 **files 段**的口径，
/// 与归并后总条数不是一回事（apps 命中不该改这行）。</para>
///
/// <para><b>为什么有 <see cref="FilesAccepted"/></b>：过期丢弃时今天**什么都不变**
/// （<c>OnResults</c> 根本不触发）。消费方据此决定"是否更新状态行的 files 部分"—— 少了这个标志，
/// 丢弃会被渲染成"显示 0 / 共 0 条"（把"没变"显示成"没结果"）。</para>
///
/// <para><b>为什么还要 <see cref="AnyAccepted"/></b>：决定"是否重绘列表"。全部段都未接受时重绘
/// 等于把同样的内容清空重灌 —— 用户看不见差别，但**列表选中态会被重置**，而今天的过期丢弃
/// 连选中态都不动。多 provider 之后（W7-b）"files 过期但 apps 有新鲜结果"仍需重绘，所以两个
/// 标志各管一件事，不能合并。</para>
/// </summary>
public sealed record LauncherRenderModel(
    long Generation,
    string QueryText,
    bool IsEmptyQuery,
    bool AnyAccepted,
    bool FilesAccepted,
    IReadOnlyList<LauncherItem> Items,
    IReadOnlyDictionary<string, LauncherError> Errors,
    int FilesHitCount,
    int FilesTotal,
    int FilesElapsedMs);
