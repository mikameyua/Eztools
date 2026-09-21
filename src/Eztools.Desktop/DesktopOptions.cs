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
            }
        }

        return new DesktopOptions
        {
            NoPrompt = noPrompt,
            SelfCheck = selfCheck,
            ProbeNotify = probeNotify,
            ProbeHotkey = probeHotkey,
            Verbose = verbose,
            OutFile = outFile,
            ToolsDir = toolsDir,
            AutoClick = autoClick,
        };
    }
}
