// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Desktop;

/// <summary>
/// 桌面宿主的启动开关。都只影响**启动行为**，不改变宿主逻辑
/// —— 所以它们不经过 <c>Eztools.Host</c>，也不需要出现在 Cli 的参数表里。
/// </summary>
internal sealed class DesktopOptions
{
    /// <summary>
    /// 已有实例在跑时**不弹提示框**。
    ///
    /// 为什么要有这个开关：双击第二次时给一句"已在运行、图标在哪"是有用的反馈
    /// （否则"毫无反应"很容易被当成程序坏了），但模态框会**阻塞自动化**
    /// —— 验收脚本调第二次实例时会挂住等用户点确定。
    /// </summary>
    public bool NoPrompt { get; private init; }

    /// <summary>启动后程序化触发第 N 个托盘项然后退出（验证"能起 → 能建菜单 → 能调工具"整条链）。</summary>
    public int? AutoClick { get; private init; }

    /// <summary>只做启动自检（建宿主、建菜单、读图标）后退出，不进消息循环。</summary>
    public bool SelfCheck { get; private init; }

    /// <summary>
    /// 直接调一次 <c>probe.notify</c> 并把它回报的 <c>delivered</c> 写进自检文件后退出。
    ///
    /// **为什么不能靠 <c>--click</c> 覆盖这条**：<c>--click</c> 只触发**托盘菜单项**
    /// （`contributes.tray` 声明的），而 <c>host.notify</c> 是"工具 → 宿主"方向的通知通道，
    /// 两者不是同一条路。没有这个入口，"托盘宿主会不会真弹气泡"在自动化里就是盲区
    /// —— 而那正是 B1（通知通道长期恒回 <c>delivered:false</c>）藏在缝里的原因。
    /// </summary>
    public bool ProbeNotify { get; private init; }

    /// <summary>
    /// 触发一次**已注册热键**对应的命令，用退出码回报成败，然后退出。
    ///
    /// **为什么 <c>--click</c> 覆盖不了这条**：<c>--click</c> 走的是**托盘项**
    /// （<c>TrayMenuBuilder.Build</c> 合成的那些），而热键项是在
    /// <c>RegisterHotkeysFromRegistry</c> 里**单独构造**的另一批对象 ——
    /// 两者的 <c>Input</c> 不同源：托盘项取自清单的 <c>menus[].input</c>，
    /// 热键项取自 <c>contributes.hotkeys[]</c>（历史上是**硬编码 Clipboard**，
    /// 因为热键命令可能根本没有 menus 项可继承 —— <c>filehash.hash</c> 正是这种）。
    /// 于是"热键触发时到底往 <c>args</c> 里注入了什么"在自动化里是盲区，
    /// 症状是某个热键**静默**不再拿到剪贴板，手点发现不了。
    ///
    /// 传**命令 id**（而非 <c>--click</c> 那样的序号）：序号会随菜单项增减漂移，
    /// 而这里要钉住的正是"某一个具体命令"。
    ///
    /// 仍未覆盖的：**真按下按键**（键盘钩子→事件）。那部分已在方案里列为不可自动化项，
    /// 与 <c>--selfcheck</c> 报的"热键注册 = N（RegisterHotKey 真实成功）"合起来，
    /// 剩下没验的只有"用户真的按了键"这一环。
    /// </summary>
    public string? ProbeHotkey { get; private init; }

    /// <summary>
    /// 搜索窗全局热键（W3-d-1）。**null = 命令行未显式传** —— 生效值由托盘按
    /// 「命令行 &gt; config/desktop.json 的 search.hotkey &gt; 默认 Ctrl+Alt+S」合成（W4-c 接 P1a）。
    ///
    /// **为什么是宿主级开关而不是工具清单声明**：搜索窗是**宿主功能**（结果来自宿主持有的
    /// <c>SearchIndexClient</c> → ezt-index 管道，没有任何 Python 工具参与），
    /// 而 <c>contributes.hotkeys[]</c> 的语义是"命令成功后回调"—— 载体不存在，
    /// 硬塞进清单就是造一个没人实现的锚点（B2/D5 的同族教训：文档有、实现无 = 负资产）。
    /// 宿主级热键在**工具热键仲裁之后**注册：工具清单声明优先，宿主给工具让路。
    /// </summary>
    public string? SearchHotkey { get; private init; }

    /// <summary>
    /// 屏幕取字全局热键（W4-c）。**null = 命令行未显式传** —— 生效值由托盘按
    /// 「命令行 &gt; config/desktop.json 的 ocr.hotkey &gt; 默认 Ctrl+Alt+O」合成。
    /// 与搜索热键同款"宿主级开关"定位：遮罩窗是宿主功能，没有工具清单可声明。
    /// </summary>
    public string? OcrHotkey { get; private init; }

    /// <summary>
    /// 区域截图全局热键（W6-b）。**null = 命令行未显式传** —— 生效值由托盘按
    /// 「命令行 &gt; config/desktop.json 的 capture.hotkey &gt; 默认 Ctrl+Alt+X」合成。
    /// 与 OCR 热键同款"宿主级开关"定位（D2 拍板：X，2026-09-30 复探可注册）。
    /// </summary>
    public string? CaptureHotkey { get; private init; }

    /// <summary>
    /// 屏幕取色全局热键（W6-c）。**null = 命令行未显式传** —— 生效值由托盘按
    /// 「命令行 &gt; config/desktop.json 的 pick.hotkey &gt; 默认 Ctrl+Alt+C」合成
    /// （D2 拍板：C，2026-09-30 复探可注册）。
    /// </summary>
    public string? PickHotkey { get; private init; }

    /// <summary>
    /// 走一次真实的 <see cref="Search.SearchIndexProcess"/> 通路（懒启动 ezt-index → stdio
    /// → search.query），把结构化结局写进 <c>--out</c> 文件后退出（W3-d-1 验收断言面）。
    /// </summary>
    public bool ProbeSearch { get; private init; }

    /// <summary>
    /// <c>--probe-search</c> 在 query 之前**最多等 ready 多少毫秒**（W3-c 有界化后新增，2026-09-25）。
    ///
    /// **为什么需要**：宿主托管的 ezt-index 是**后台自举**（先回 ping、再建索引），探针原来
    /// ping 一回来就 query ⇒ 必然撞上 <c>-32001</c>。实测整个探针只花 478 ms，而自举连
    /// "自举完成"都没走完 —— 那不是"索引没就绪"，是"我们没等"。真机 A3 之后想看到**真命中**，
    /// 就必须显式等落地。
    ///
    /// **为什么默认 0（不等）**：无提权 Core 的环境里自举会逐卷失败，ready 永远为 false；
    /// 默认等待会把"零提权冒烟"从 ~0.5 s 拖成等满超时。要真命中的路线显式传
    /// <c>--wait-ready 30000</c> 即可 —— 两种环境的期望值因此都保得住。
    /// </summary>
    public int WaitReadyMs { get; private init; }

    /// <summary>
    /// <c>--probe-search</c> 的**采样能力**（G2 方案 A，2026-09-25）：预热几次 + 正式采样几次，
    /// 落 P50/P95 而不是"单次一发"。
    ///
    /// **为什么必须加**：原来探针只发**一次** query，而那一次是**冷态**（首次要 page-in
    /// `.ezidx`）—— 实测 `elapsedMs=510`。拿一个冷态单样本去对"P95 ≤ N ms"的闸，
    /// 第一天就是红的，而且红得没有意义（冷/热、单样本/P95 是两个不同的量）。
    /// 有了预热 + 多点采样，`elapsedMs` 才是一个可比较的分布，判据才谈得上"守得住"。
    /// </summary>
    public int ProbeWarmup { get; private init; }

    /// <summary><c>--probe-search</c> 正式采样次数（不含预热）。默认 1 = 与旧行为一致。</summary>
    public int ProbeRepeat { get; private init; } = 1;

    /// <summary>
    /// 命令行里**不认识**的参数（原样保留）。
    /// 保留成结构化字段而不是只打一行 stderr：调用方/自检想断言"某个 flag 到底有没有生效"时，
    /// 需要能机读地看到它 —— 只写日志的话，"被忽略"和"生效了但没声音"在外部完全同形。
    /// </summary>
    public IReadOnlyList<string> UnknownArgs { get; private init; } = [];

    /// <summary>
    /// 走一次搜索窗的**渲染**断言面（W3-d-2/3）：假传输灌 200 条合成命中 → 对内容根
    /// Measure/Arrange → 把虚拟化数字 / 计数显示 / 首项高亮分段 / 动作进程参数落进 <c>--out</c>。
    ///
    /// **为什么不 Show()**：布局与虚拟化只要求控件有有限尺寸，不要求窗口可见；Show() 会抢
    /// 焦点打扰用户且需要消息循环。真按键/真滚动/真失焦仍在自动化之外（与 <c>--probe-search</c> 同口径）。
    /// </summary>
    public bool ProbeSearchUi { get; private init; }

    /// <summary>
    /// 搜索窗<b>唤出生命周期</b>探针（C2/G1 手工三 bug 的自动化面）：
    /// 假传输 + 真 Toggle/Summon/Hide 循环（抓"唤出即被藏"与"窗口在但打不进字"）；
    /// 加 <c>--probe-search-live</c> 时再跑真链路 lei/LEI 大小写对照（抓"输入 lei 无结果"）。
    /// </summary>
    public bool ProbeSearchSummon { get; private init; }

    /// <summary>配合 <see cref="ProbeSearchSummon"/>：Phase B 用真 ezt-index 查 lei/LEI。</summary>
    public bool ProbeSearchLive { get; private init; }

    /// <summary>
    /// OCR 遮罩<b>唤出生命周期</b>探针（--probe-ocr-overlay，W4-b）：
    /// 真鼠标拖拽（遮罩全屏置顶 ⇒ 注入必然落在遮罩上）+ Esc 真键。
    /// ⚠️ 副作用：真实移动鼠标、可能改写剪贴板 —— 只能跑在无人交互的会话。
    /// </summary>
    public bool ProbeOcrOverlay { get; private init; }

    /// <summary>
    /// 人工模式（--ocr-show，W4-b 手工清单专用）：唤出全部遮罩后**交给真人操作**，
    /// 遮罩完成（复制/Esc）后进程退出。<c>W4-手工验收清单.md</c> M1~M6 的唤出入口 ——
    /// 探针是自动化面（自跑自收），这个才是给人用的。
    /// </summary>
    public bool OcrShow { get; private init; }

    /// <summary>
    /// 宿主设置保存链探针（--probe-host-settings，W4-c 真机项自动化）：
    /// 程序化走真实设置窗口链路（改热键 → 保存 → 落盘 → 回调重注册 → 恢复默认），
    /// **无鼠标/键盘副作用**，可进 verify-desktop。
    /// </summary>
    public bool ProbeHostSettings { get; private init; }

    /// <summary>
    /// OCR 热键真按键探针（--probe-ocr-hotkey，W4-c 真机项自动化）：
    /// keybd_event 注入真实组合键 → WM_HOTKEY → 遮罩出现 → Esc 收窗。
    /// ⚠️ 副作用：真实按键、遮罩全屏抢焦点 1~2 秒 —— 只能跑在无人交互的会话。
    /// </summary>
    public bool ProbeOcrHotkey { get; private init; }

    /// <summary>
    /// 剪贴板面板探针（--probe-clip-panel，W5-b 验收面）：临时库预置条目 →
    /// 唤出生命周期循环（Toggle/Summon/Hide + X 契约）→ 真键 Enter 直贴
    /// （断言：钩子命中 + 面板隐藏 + 剪贴板逐字等于条目内容）。
    /// ⚠️ 副作用：Summon 真抢焦点 1~2 秒；Ctrl+V 真注入被探针抑制（归手工 M 项）。
    /// </summary>
    public bool ProbeClipPanel { get; private init; }

    /// <summary>
    /// 剪贴板监听探针（--probe-clip-monitor，W5-c 验收面）：
    /// 真 AddClipboardFormatListener → 程序化写剪贴板 → 裸泵喂 WM_CLIPBOARDUPDATE →
    /// 断言事件触发且内容入库。副作用：改写一次系统剪贴板；不抢焦点。
    /// </summary>
    public bool ProbeClipMonitor { get; private init; }

    /// <summary>
    /// 图片 OCR 提字探针（--probe-clip-ocr，W5-d FR-15 验收面）：
    /// 渲染已知文字样图入库 → 面板唤出 → **真实菜单点击链** → 断言剪贴板拿到该文字；
    /// 同时反向断言"非图片条目的菜单项不可用"。语言包缺失时如实落盘并跳过（不伪装成功）。
    /// 副作用：Summon 抢焦点 1~2 秒 + 改写一次系统剪贴板。
    /// </summary>
    public bool ProbeClipOcr { get; private init; }

    /// <summary>
    /// 剪贴板热键真按键探针（--probe-clip-hotkey，W5-c 真机项自动化，--probe-ocr-hotkey 同款）：
    /// keybd_event 注入真实 Ctrl+Alt+V → WM_HOTKEY → 面板唤出 → Esc 真键收窗。
    /// ⚠️ 副作用：真实按键、面板抢焦点 1~2 秒 —— 只能跑在无人交互的会话。
    /// </summary>
    public bool ProbeClipHotkey { get; private init; }

    /// <summary>
    /// 截图热键真按键探针（--probe-capture-hotkey，W6-b 真机项自动化，--probe-ocr-hotkey 同款）：
    /// keybd_event 注入真实 Ctrl+Alt+X → WM_HOTKEY → 截图遮罩唤出 → Esc 真键收窗。
    /// ⚠️ 副作用：真实按键、遮罩全屏抢焦点 1~2 秒 —— 只能跑在无人交互的会话。
    /// </summary>
    public bool ProbeCaptureHotkey { get; private init; }

    /// <summary>
    /// 截图遮罩端到端探针（--probe-capture-overlay，W6-b 验收面，OcrOverlayProbe 同款纪律）：
    /// 真鼠标拖拽 → 断言剪贴板真拿到位图且尺寸与拖拽矩形一致（±1 圆整容差）→
    /// 空拖拽（单击）= 取消出窗契约。
    /// ⚠️ 副作用：真实移动鼠标、**改写系统剪贴板** —— 只能跑在无人交互的会话。
    /// </summary>
    public bool ProbeCaptureOverlay { get; private init; }

    /// <summary>
    /// 取色热键真按键探针（--probe-pick-hotkey，W6-c 真机项自动化，--probe-ocr-hotkey 同款）：
    /// keybd_event 注入真实 Ctrl+Alt+C → WM_HOTKEY → 取色遮罩唤出 → Esc 真键收窗。
    /// ⚠️ 副作用：真实按键、遮罩全屏抢焦点 1~2 秒 —— 只能跑在无人交互的会话。
    /// </summary>
    public bool ProbePickHotkey { get; private init; }

    /// <summary>
    /// 取色端到端探针（--probe-pick-overlay，W6-c 验收面）：确定性面（已知纯色 → 三格式
    /// 手算精确断言）+ 真链路面（真移动+单击 → 剪贴板 == 自采样同点格式化值）。
    /// ⚠️ 副作用：真实移动鼠标、**改写系统剪贴板** —— 只能跑在无人交互的会话。
    /// </summary>
    public bool ProbePickOverlay { get; private init; }

    /// <summary>
    /// 托盘菜单入口探针（--probe-tray-items，W6-d FR-9 自动化）：真实构建托盘菜单并断言
    /// 「区域截图…」「屏幕取色…」存在。无副作用。
    /// </summary>
    public bool ProbeTrayItems { get; private init; }

    /// <summary>
    /// W6 自产内容入库探针（--probe-w6-clip-integration，W6-d D6=A 自动化闭环）：
    /// 显式启动真监听 → 真实取色 → 断言 W5 库内出现色值条目。
    /// ⚠️ 副作用：改写剪贴板 + 真实移动鼠标（探针库新增一条记录）。
    /// </summary>
    public bool ProbeW6ClipIntegration { get; private init; }

    /// <summary>截图遮罩人工模式（--capture-show，W6-d 手工清单）：唤出后交给真人，完成退出。</summary>
    public bool CaptureShow { get; private init; }

    /// <summary>取色遮罩人工模式（--pick-show，W6-d 手工清单）：唤出后交给真人，完成退出。</summary>
    public bool PickShow { get; private init; }

    /// <summary>
    /// 启动器探针（<c>--probe-launcher &lt;mode&gt;</c>，W7-b 验收面）。取值：
    /// <list type="bullet">
    /// <item><c>rows</c> —— 五种来源的行渲染（徽标 / 主副行 / 高亮 / 图标）+ 文件项无徽标（FR-10 反向断言）；</item>
    /// <item><c>actions</c> —— Enter / Ctrl+Enter / Ctrl+C 的动作分派与失败文案；</item>
    /// <item><c>apps</c> —— 真 AppsProvider（临时扫描根）：命中 / 排序 / 图标句柄斜率 / 缓存上限 / 首扫补发；</item>
    /// <item><c>isolation</c> —— 段位故障必须在状态行可见（FR-9）；</item>
    /// <item><c>router</c> —— 代次闸的上屏级证据（R3）：慢来源在途 → 清空输入框推进代次 → 放行
    ///   慢批次 ⇒ 渲染日志条数不得增加；</item>
    /// <item><c>calc</c> —— 计算器真链路：该静默的真静默（纯数字/含字母/半成品）· 出结果行且置顶 ·
    ///   Enter 复制格式化值并收窗 · 求值错行禁用动作（不收窗、不复制）· Ctrl+Enter 复制「表达式 = 结果」；</item>
    /// <item><c>unit</c> —— 单位换算真链路：触发词 `to`/`->`/`转` · 温度仿射恒等式 `-40C == -40F` ·
    ///   数据量两制式标注 · 无触发词列常用单位（不含源单位）· 混算/未知单位静默；</item>
    /// <item><c>encode</c> —— 编码转换真链路：六前缀双向 · 非法输入 ⇒ 显式错误行且动作禁用 ·
    ///   无前缀完全静默；</item>
    /// <item><c>config</c> —— 读**真实**配置中心并落出解析结果/告警文案（两层测试的应用层观测面，
    ///   CLI 写入由 acceptance 的步骤 8 配对断言；含 usage/alias 的解析观测）；</item>
    /// <item><c>usage</c> —— 频次记忆真链路（W7-e）：记录两次 ⇒ 排序提前 · 落盘回读 ·
    ///   开关关 ⇒ 彻底不读写 · 损坏文件 ⇒ 空表 + LastError；</item>
    /// <item><c>all</c> —— 以上全跑（无真应用启动，无副作用）。</item>
    /// </list>
    /// </summary>
    public string? ProbeLauncher { get; private init; }

    /// <summary>
    /// WinForms 打字判决探针（--probe-winforms-typing，2026-09-27 换方案判决）：
    /// 纯 WinForms TextBox 注入真键 "hi" → 断言 Text=="hi"。无副作用。
    /// </summary>
    public bool ProbeWinFormsTyping { get; private init; }

    public bool Verbose { get; private init; }

    /// <summary>
    /// 自检结果写到这个文件（UTF-8），供自动化读取。
    ///
    /// **为什么不用 stdout**：WinExe 没有控制台，<c>Console.Out</c> 会退回系统 ANSI 代码页
    /// （中文 Windows 上是 GBK），调用方按 UTF-8 读会得到乱码（实测确认）。
    /// 写文件能把编码钉死，不用让每个调用方去猜当前进程用的是哪个代码页。
    /// </summary>
    public string? OutFile { get; private init; }

    /// <summary>
    /// 额外的工具目录源（分号分隔），透传给 <c>HostOptions.ToolsDir</c>。
    ///
    /// **为什么必须有**：验收脚本要能指向一个**隔离的工具根**（含故意写坏的清单夹具），
    /// 否则它只能测仓库自带的 <c>tools/</c>——那就测不了"weight: lite 声明 panels 会被拒"
    /// 这类负向用例（不能把坏夹具放进仓库 tools/，那会污染其它步骤的发现数量断言）。
    /// 缺了它，托盘侧的面板断言就只能依赖"当前 cwd 恰好能找到 tools/"这个隐式前提。
    /// </summary>
    public string? ToolsDir { get; private init; }

    public static DesktopOptions Parse(string[] args)
    {
        var noPrompt = false;
        var selfCheck = false;
        var probeNotify = false;
        var verbose = false;
        string? outFile = null;
        string? toolsDir = null;
        string? probeHotkey = null;
        string? searchHotkey = null;
        string? ocrHotkey = null;
        string? captureHotkey = null;
        string? pickHotkey = null;
        var probeSearch = false;
        var probeSearchUi = false;
        var probeSearchSummon = false;
        var probeSearchLive = false;
        var probeOcrOverlay = false;
        var ocrShow = false;
        var probeHostSettings = false;
        var probeOcrHotkey = false;
        var probeClipPanel = false;
        var probeClipMonitor = false;
        var probeClipOcr = false;
        var probeClipHotkey = false;
        var probeCaptureHotkey = false;
        var probeCaptureOverlay = false;
        var probePickHotkey = false;
        var probePickOverlay = false;
        var probeTrayItems = false;
        var probeW6ClipIntegration = false;
        string? probeLauncher = null;
        var captureShow = false;
        var pickShow = false;
        var probeWinformsTyping = false;
        var waitReadyMs = 0;
        var probeWarmup = 0;
        var probeRepeat = 1;
        var unknown = new List<string>();
        int? autoClick = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-prompt":
                    noPrompt = true;
                    break;
                case "--selfcheck":
                    selfCheck = true;
                    break;
                case "--probe-notify":
                    probeNotify = true;
                    break;
                case "--probe-hotkey" when i + 1 < args.Length:
                    probeHotkey = args[++i];
                    break;
                case "--search-hotkey" when i + 1 < args.Length:
                    searchHotkey = args[++i];
                    break;
                case "--ocr-hotkey" when i + 1 < args.Length:
                    ocrHotkey = args[++i];
                    break;
                case "--capture-hotkey" when i + 1 < args.Length:
                    captureHotkey = args[++i];
                    break;
                case "--pick-hotkey" when i + 1 < args.Length:
                    pickHotkey = args[++i];
                    break;
                case "--probe-search":
                    probeSearch = true;
                    break;
                case "--probe-search-ui":
                    probeSearchUi = true;
                    break;
                case "--probe-search-summon":
                    probeSearchSummon = true;
                    break;                case "--probe-search-live":
                    probeSearchLive = true;
                    break;
                case "--probe-ocr-overlay":
                    probeOcrOverlay = true;
                    break;
                case "--ocr-show":
                    ocrShow = true;
                    break;
                case "--probe-host-settings":
                    probeHostSettings = true;
                    break;
                case "--probe-ocr-hotkey":
                    probeOcrHotkey = true;
                    break;
                case "--probe-clip-panel":
                    probeClipPanel = true;
                    break;
                case "--probe-clip-monitor":
                    probeClipMonitor = true;
                    break;
                case "--probe-clip-ocr":
                    probeClipOcr = true;
                    break;
                case "--probe-clip-hotkey":
                    probeClipHotkey = true;
                    break;
                case "--probe-capture-hotkey":
                    probeCaptureHotkey = true;
                    break;
                case "--probe-capture-overlay":
                    probeCaptureOverlay = true;
                    break;
                case "--probe-pick-hotkey":
                    probePickHotkey = true;
                    break;
                case "--probe-pick-overlay":
                    probePickOverlay = true;
                    break;
                case "--probe-tray-items":
                    probeTrayItems = true;
                    break;
                case "--probe-launcher" when i + 1 < args.Length:
                    probeLauncher = args[++i];
                    break;
                case "--probe-w6-clip-integration":
                    probeW6ClipIntegration = true;
                    break;
                case "--capture-show":
                    captureShow = true;
                    break;
                case "--pick-show":
                    pickShow = true;
                    break;
                case "--probe-winforms-typing":
                    probeWinformsTyping = true;
                    break;
                case "--wait-ready" when i + 1 < args.Length && int.TryParse(args[i + 1], out var waitMs):
                    waitReadyMs = waitMs;
                    i++;
                    break;
                case "--probe-warmup" when i + 1 < args.Length && int.TryParse(args[i + 1], out var warmup):
                    probeWarmup = Math.Max(warmup, 0);
                    i++;
                    break;
                case "--probe-repeat" when i + 1 < args.Length && int.TryParse(args[i + 1], out var repeat):
                    probeRepeat = Math.Clamp(repeat, 1, 500);
                    i++;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    outFile = args[++i];
                    break;
                case "--tools-dir" when i + 1 < args.Length:
                    toolsDir = args[++i];
                    break;
                case "--click" when i + 1 < args.Length && int.TryParse(args[i + 1], out var index):
                    autoClick = index;
                    i++;
                    break;

                default:
                    // ★ 未知参数**必须可见**（2026-09-25 实测教训）：原来这里没有 default 分支，
                    //   未知 flag 被**静默忽略**。踩到的形态：探针传了 `--install-root X`（这个 flag
                    //   在 Desktop 上**不存在**，真装根由环境变量 `EZTOOLS_INSTALL_ROOT` 控制）
                    //   ⇒ 参数无声丢弃 ⇒ 探针改用默认数据根 ⇒ 输出**看起来完全正常**，
                    //   但测的根本不是你以为的那个索引（实测白跑一轮，且第一次的结论是错的）。
                    //   与 `ezt-index` 对未知参数的处理保持一致：**说出来**，不假装没看见。
                    //   刻意只告警不退出：`verify-desktop.py` 一直在传这两个（历史上就被忽略）
                    //   的名存实亡 flag，硬失败会平白打断验收；把它记下来比替它做主更稳妥。
                    unknown.Add(args[i]);
                    break;
            }
        }

        if (unknown.Count > 0)
        {
            Console.Error.WriteLine(
                $"[Eztools.Desktop] 未知参数被忽略：{string.Join(" ", unknown)}"
                + "（安装根/配置根请用环境变量 EZTOOLS_INSTALL_ROOT / EZTOOLS_CONFIG_ROOT）");
        }

        return new DesktopOptions
        {
            NoPrompt = noPrompt,
            SelfCheck = selfCheck,
            ProbeNotify = probeNotify,
            ProbeHotkey = probeHotkey,
            SearchHotkey = searchHotkey,
            OcrHotkey = ocrHotkey,
            CaptureHotkey = captureHotkey,
            PickHotkey = pickHotkey,
            ProbeSearch = probeSearch,
            ProbeSearchUi = probeSearchUi,
            ProbeSearchSummon = probeSearchSummon,
            ProbeSearchLive = probeSearchLive,
            ProbeOcrOverlay = probeOcrOverlay,
            OcrShow = ocrShow,
            ProbeHostSettings = probeHostSettings,
            ProbeOcrHotkey = probeOcrHotkey,
            ProbeClipPanel = probeClipPanel,
            ProbeClipMonitor = probeClipMonitor,
            ProbeClipOcr = probeClipOcr,
            ProbeClipHotkey = probeClipHotkey,
            ProbeCaptureHotkey = probeCaptureHotkey,
            ProbeCaptureOverlay = probeCaptureOverlay,
            ProbePickHotkey = probePickHotkey,
            ProbePickOverlay = probePickOverlay,
            ProbeTrayItems = probeTrayItems,
            ProbeW6ClipIntegration = probeW6ClipIntegration,
            ProbeLauncher = probeLauncher,
            CaptureShow = captureShow,
            PickShow = pickShow,
            ProbeWinFormsTyping = probeWinformsTyping,
            WaitReadyMs = waitReadyMs,
            ProbeWarmup = probeWarmup,
            ProbeRepeat = probeRepeat,
            UnknownArgs = unknown,
            Verbose = verbose,
            OutFile = outFile,
            ToolsDir = toolsDir,
            AutoClick = autoClick,
        };
    }
}
