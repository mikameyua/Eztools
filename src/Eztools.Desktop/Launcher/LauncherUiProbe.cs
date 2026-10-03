// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Eztools.Contracts;
using Eztools.Host.Launcher;
using Eztools.Host.Search;
using InputCmd = Eztools.Desktop.NativeInputBox.InputCommand;

namespace Eztools.Desktop;

/// <summary>
/// 启动器探针（W7-b 起）：把"非文件来源"的行为落成结构化 JSON。各模式见
/// <see cref="DesktopOptions.ProbeLauncher"/>（`rows` / `actions` / `isolation` / `router` / `calc` / `unit` / `encode` / `apps`）。
///
/// <para><b>为什么单独一个探针而不是塞进 <c>SearchUiProbe</c></b>：那个探针的**全部断言**都是
/// "文件项渲染零变化"的机器证据（FR-10），一行都不许动。新来源（应用/计算/换算/编码）的行为
/// 必须有自己的观测面，否则"给新来源加断言"就会顺手改到文件项的断言上 —— 那条证据链一断，
/// 之后就再也说不清"文件项到底有没有变"。</para>
///
/// <para><b>不真起进程</b>：应用动作（Launch / RevealApp）只断言**构造出来的参数**，
/// 不真的启动应用、不真的弹 Explorer（自动化环境里那既慢又不稳定，而且会打扰正干活的人）。
/// 这一条与 W3-d-3 的 `actions` 断言块同款。</para>
/// </summary>
internal static class LauncherUiProbe
{
    private const int PumpTimeoutMs = 1500;

    /// <summary>
    /// 运行指定模式（`rows` / `actions` / `isolation` / `router` / `calc` / `unit` / `encode` / `config` / `apps` / `all`）。
    /// <paramref name="configs"/> 只有 `config` 模式用（读**真实**配置中心 —— 两层测试的应用层观测面）。
    /// </summary>
    internal static JsonObject Run(string mode, Eztools.Host.Config.ConfigStore? configs = null)
    {
        var json = new JsonObject { ["mode"] = mode };
        var all = string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase);

        if (all || Eq(mode, "rows"))
        {
            json["rows"] = RunRows();
        }

        if (all || Eq(mode, "actions"))
        {
            json["actions"] = RunActions();
        }

        if (all || Eq(mode, "isolation"))
        {
            json["isolation"] = RunIsolation();
        }

        if (all || Eq(mode, "router"))
        {
            json["router"] = RunRouter();
        }

        if (all || Eq(mode, "calc"))
        {
            json["calc"] = RunCalc();
        }

        if (all || Eq(mode, "unit"))
        {
            json["unit"] = RunUnit();
        }

        if (all || Eq(mode, "encode"))
        {
            json["encode"] = RunEncode();
        }

        if (all || Eq(mode, "config"))
        {
            json["config"] = RunConfig(configs);
        }

        if (all || Eq(mode, "apps"))
        {
            json["apps"] = RunApps();
        }

        if (all || Eq(mode, "usage"))
        {
            json["usage"] = RunUsage();
        }

        if (all || Eq(mode, "corestatus"))
        {
            json["corestatus"] = RunCoreStatus();
        }

        return json;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── rows：五种来源的行渲染 ───────────────────────────────────────────────

    private static JsonObject RunRows()
    {
        var transport = new ProbeSearchUiTransport();
        var client = new SearchIndexClient(transport);
        var providers = new ILauncherProvider[]
        {
            new FilesProvider(client),
            FakeOf(LauncherProviderRegistry.Calc, [Item(LauncherKind.Calc, "96", "= 128*3/4", 5, [(0, 2)])]),
            FakeOf(LauncherProviderRegistry.Unit, [Item(LauncherKind.Unit, "6.2137 mi", "10 km = 6.2137 mi", 4, [])]),
            FakeOf(LauncherProviderRegistry.Encode, [Item(LauncherKind.Encode, "aGk=", "base64 编码", 3, [])]),
            FakeOf(LauncherProviderRegistry.Apps, [Item(LauncherKind.App, "记事本", RealExe, 10, [(0, 3)], RealExe)]),
        };

        var window = new SearchWindow(client, providers) { ProbeSuppressDrift = true };
        try
        {
            window.ShowForProbe();
            window.SubmitForProbe("a");
            PumpUntil(() => window.ProbeItemCount >= 204, PumpTimeoutMs);

            var rows = window.SnapshotRows();
            var snap = window.SnapshotUi();

            var kinds = new JsonArray();
            var badges = new JsonArray();
            var titles = new JsonArray();
            var subtitles = new JsonArray();
            var icons = new JsonArray();
            var segmentCounts = new JsonArray();
            foreach (var row in rows)
            {
                kinds.Add(row.Kind);
                badges.Add(row.Badge);
                titles.Add(row.Title);
                subtitles.Add(row.Subtitle);
                icons.Add(row.IconShown);
                segmentCounts.Add(row.Segments.Count);
            }

            return new JsonObject
            {
                ["itemsCount"] = window.ProbeItemCount,
                ["rowCount"] = rows.Count,
                ["kinds"] = kinds,
                ["badges"] = badges,
                ["titles"] = titles,
                ["subtitles"] = subtitles,
                ["icons"] = icons,
                ["segmentCounts"] = segmentCounts,
                // ★ 反向断言素材：文件行由 HitText 渲染 ⇒ 这里**一条 File 都不该出现**
                ["fileRowLeaked"] = rows.Any(r => Eq(r.Kind, nameof(LauncherKind.File))),
                // files 段的计数不受新来源影响（"显示 200 / 共 12345 条"）
                ["statusRight"] = snap.StatusRight,
                // 文件行仍然由 HitText 渲染并读出名字（新模板没把文件行抢走）
                ["firstNameFromHitText"] = snap.FirstName,
                // 204 条仍然只生成少量容器（虚拟化没被新模板破坏）
                ["realizedContainers"] = snap.RealizedContainers,
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    // ── actions：动作分派与副作用 ───────────────────────────────────────────

    private static JsonObject RunActions()
    {
        var transport = new ProbeSearchUiTransport();
        var client = new SearchIndexClient(transport);
        var providers = new ILauncherProvider[]
        {
            new FilesProvider(client),
            FakeOf(LauncherProviderRegistry.Apps, [Item(LauncherKind.App, "记事本", RealExe, 100, [], RealExe)]),
            FakeOf(LauncherProviderRegistry.Calc, [Item(LauncherKind.Calc, "96", "= 128*3/4", 200, [])]),
        };

        var window = new SearchWindow(client, providers) { ProbeSuppressDrift = true };
        try
        {
            window.ShowForProbe();
            window.ProbeStartRenderLog();   // ★ 就绪信号 = 渲染日志（见 SubmitAndWaitRender 的说明）
            window.SubmitForProbe("a");
            PumpUntil(() => window.ProbeRenderLog.Count > 0, PumpTimeoutMs);

            // Ctrl+C 在**未选中**时复制首行（calc 项，pin 段置顶）—— 且**不**收窗（既有语义）
            window.ProbeSelectIndex(0);
            var (ctrlCText, ctrlHidden, ctrlCHandled) = ProbeCopy(window, InputCmd.CtrlC);
            var stillVisibleAfterCtrlC = !ctrlHidden;

            // Enter on calc（首行）= 复制结果值 + 收窗
            var (enterText, enterHidden, enterHandled) = ProbeCopy(window, InputCmd.Enter);
            var hiddenAfterEnter = enterHidden;

            // 选中文件项（非文件段之后的第一条）→ 复制的是**全路径**（与 W7 之前逐字一致）
            window.ProbeSelectIndex(window.ProbeItemCount - 1);
            var (lastCopyText, _, _) = ProbeCopy(window, InputCmd.CtrlC);

            return new JsonObject
            {
                ["ctrlCHandled"] = ctrlCHandled,
                ["ctrlCText"] = ctrlCText,
                ["visibleAfterCtrlC"] = stillVisibleAfterCtrlC,
                ["enterHandled"] = enterHandled,
                ["enterText"] = enterText,
                ["hiddenAfterEnter"] = hiddenAfterEnter,
                ["fileCopyText"] = lastCopyText,
                ["fileCopyLooksLikePath"] = lastCopyText.Contains(Path.DirectorySeparatorChar)
                    || lastCopyText.Contains(':'),
                // 无动作的结果（Ctrl+Enter 在 calc 项上：次动作也是复制 —— 断言它不静默失败）
                ["itemsCount"] = window.ProbeItemCount,

                // ── 动作构造器（不真起进程 —— 只断言参数形状）──
                // ★ 这些字段必须落在 actions 模式：它们断言的是"动作参数长什么样"，
                //   与 rows 模式（行渲染）是两条独立的观测面。放错模式 = 断言恒读 None
                //   （2026-10-01 实测踩到：verify-desktop 从 actions 读，探针写进了 rows ⇒ 三条假红）。
                ["launchFileName"] = LauncherActionRunner.BuildLaunchStartInfo(RealExe).FileName,
                ["launchWorkingDir"] = LauncherActionRunner.BuildLaunchStartInfo(RealExe).WorkingDirectory,
                ["revealArgs"] = LauncherActionRunner.BuildRevealStartInfo(RealExe).Arguments,
                ["revealPathHasForwardSlash"] = LauncherActionRunner.BuildRevealStartInfo("C:/a/b.txt").Arguments
                    .Split('"') is { Length: > 1 } parts && parts[1].Contains('/'),

                // 打开失败的两种文案（区分"目标不存在"与其它异常）
                ["failureMissing"] = LauncherActionRunner.DescribeFailure(
                    new LauncherAction(LauncherActionKind.Launch, @"C:\no\such\app.exe"),
                    new System.ComponentModel.Win32Exception(2, "系统找不到指定的文件。")),
                ["openFailureLegacy"] = SearchWindow.DescribeOpenFailure(
                    new SearchHitDto("gone.txt", false, @"C:\no\such\gone.txt", 2, []),
                    new FileNotFoundException("模拟：文件已被删除")),
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    // ── router：代次闸的**上屏级**证据（R3）─────────────────────────────────

    /// <summary>
    /// 验证"过期批次不得铺上屏"（设计方案 §10.4 I2 / 风险 R3）。
    ///
    /// <para><b>为什么这条断言不是恒真</b>：泵是**严格单在途**的，所以在途批次之间不会交叠 ——
    /// 唯一能让"在途批次的代次变旧"的路径是 **清空输入框**：<c>Kick("")</c> 走
    /// <c>OnEmptyText</c> 那条零往返支路，它**也推进代次**（正是 §10.4 注 2b 的语义）。
    /// 于是"在途的 gen=1 迟到 → 必须整体丢弃"是一个真实可达、且只有代次闸能兜住的状态：
    /// 拆掉代次闸，下面这条断言立刻变红（陈旧的应用条目会被铺到用户眼前）。</para>
    ///
    /// <para><b>判据记在"真正写进列表"的渲染日志上</b>（<see cref="SearchWindow.ProbeStartRenderLog"/>），
    /// 不是"router 发布了什么" —— 丢弃的批次根本不会触发渲染，所以日志条数就是"铺屏次数"。</para>
    /// </summary>
    private static JsonObject RunRouter()
    {
        var transport = new ProbeSearchUiTransport();
        var client = new SearchIndexClient(transport);

        using var gate = new ManualResetEventSlim(false);
        var slow = new FakeProvider
        {
            Id = LauncherProviderRegistry.Apps,
            Items = [Item(LauncherKind.App, "陈旧结果", RealExe, 999, [], RealExe)],
            Gate = gate,
        };

        var window = new SearchWindow(client, [new FilesProvider(client), slow]) { ProbeSuppressDrift = true };
        try
        {
            window.ShowForProbe();
            window.ProbeStartRenderLog();

            // ① 首发立即派发（gen=1）；慢来源卡住 ⇒ 该批次不可能完成
            window.SubmitForProbe("报");
            var dispatched = WaitFor(() => slow.Calls >= 1, PumpTimeoutMs);

            // ② 清空输入框 ⇒ 本地空结果（gen=2）先一步铺上屏（零往返，不等任何 provider）
            window.SubmitForProbe("");
            var emptied = PumpUntil(() => window.ProbeRenderLog.Count >= 1, PumpTimeoutMs);
            var logAfterEmpty = window.ProbeRenderLog.ToArray();

            // ③ 放行 gen=1 的慢批次：它带着**已过期的代次**回来，必须整体丢弃
            gate.Set();
            PumpFor(300);                    // 若没被丢弃，这条路径足够把陈旧结果铺上屏

            var finalLog = window.ProbeRenderLog.ToArray();
            return new JsonObject
            {
                ["dispatchedFirstBatch"] = dispatched,
                ["slowCalls"] = slow.Calls,
                ["emptiedToPlaceholder"] = emptied,
                ["rendersAfterEmpty"] = logAfterEmpty.Length,
                ["logAfterEmpty"] = new JsonArray([.. logAfterEmpty.Select(l => (JsonNode)l)]),
                ["rendersAfterRelease"] = finalLog.Length,
                ["renderLog"] = new JsonArray([.. finalLog.Select(l => (JsonNode)l)]),
                // ★ 陈旧条目的标题**从不少于任何一次渲染**（不是"最后一次渲染里没有"——
                //   那种写法在"渲染了 3 次、陈旧在第 2 次"时仍然假绿）
                ["staleEverRendered"] = finalLog.Any(l => l.Contains("陈旧结果", StringComparison.Ordinal)),
                ["itemsOnScreen"] = window.ProbeItemCount,
                ["statusText"] = window.ProbeStatusText,
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    // ── calc：真链路（门槛静默 / 出结果行 / Enter 复制 / 求值错禁动作）──────────

    /// <summary>
    /// 计算器的真链路探针：**真 <see cref="CalcProvider"/> + 真窗口 + 真动作分派**（只喂假文件源）。
    ///
    /// <para>断言两组容易被写错、又各自静默的语义：
    /// <list type="number">
    /// <item><b>该静默的真静默</b>（纯数字 / 含字母 / 半成品）—— 判据是"列表里一条 calc 行都没有"，
    ///   不是"没报错"（"没报错"在什么都没发生的实现下也成立）；</item>
    /// <item><b>该出行的真出行且动作正确</b> —— 成功行 Enter 复制**格式化值**并收窗；
    ///   求值错行 Enter **不收窗、不复制**，只把"该结果不可执行"写进状态行
    ///   （动作被禁用必须与"复制了个空串"可区分）。</item>
    /// </list></para>
    /// </summary>
    private static JsonObject RunCalc()
    {
        var client = new SearchIndexClient(new ProbeSearchUiTransport());
        var window = new SearchWindow(client, [new FilesProvider(client), new CalcProvider()])
        {
            ProbeSuppressDrift = true,
        };

        try
        {
            window.ShowForProbe();
            window.ProbeStartRenderLog();

            // ① 正常表达式 ⇒ 置顶一行、值/副行正确
            window.ProbeStartRenderLog();   // ★ 就绪信号 = 渲染日志（不能用"行数>0"，见 SubmitAndWaitRender）
            window.SubmitForProbe("128 * 3/4");
            var okShown = PumpUntil(() => window.ProbeRenderLog.Count > 0, PumpTimeoutMs);
            var okRows = window.SnapshotRows();
            var okRow = okRows.Count > 0 ? okRows[0] : null;
            var okLeading = window.ProbeLeadingKinds(2);
            var okItems = window.ProbeItemCount;
            var okRenderLog = string.Join(" ; ", window.ProbeRenderLog);

            // ② Enter ⇒ 复制格式化值 + 收窗（§4.2 规则 2）
            var (okClipboard, okHidden, enterHandled) = ProbeCopy(window, InputCmd.Enter);
            var okWindowHidden = okHidden;

            // ③ 纯数字 ⇒ 完全静默（C-2：本项目主职是搜文件）
            var plain = SubmitAndCountRows(window, "2026", LauncherKind.Calc);

            // ④ 含字母 ⇒ 完全静默（C-3：文件名形态）
            var withLetter = SubmitAndCountRows(window, "report-2026.txt", LauncherKind.Calc);

            // ⑤ 半成品 ⇒ 语法错静默（C-1）
            var halfDone = SubmitAndCountRows(window, "1+", LauncherKind.Calc);

            // ⑥ 除零 ⇒ **出行**且副行是显式文案、动作被禁用（C-5）
            var divRows = SubmitAndSnapshot(window, "1/0");
            var errRow = divRows.Rows.Count > 0 ? divRows.Rows[0] : null;
            var clipboardBeforeEnter = ReadClipboardText();
            _ = window.ProbeCommand(InputCmd.Enter);
            var errorStatus = window.ProbeStatusText;
            var errorStillVisible = window.ProbeIsVisible;
            var clipboardAfterEnter = ReadClipboardText();

            // ⑦ Ctrl+Enter ⇒ 复制「表达式 = 结果」整串（§4.2；W7-d 回填 —— W7-c 当时错写成"回落主动作"）
            //    ★ 就绪信号必须用渲染日志：上一轮的行还在列表里，"行数>0"立刻成立 ⇒ 会选中**陈旧行**
            //     （2026-10-01 实测：读到 `1/0` 的禁用动作行 ⇒ 复制为空、窗没收 ⇒ 假红）。
            if (!window.ProbeIsVisible)
            {
                window.ShowForProbe();
            }

            window.ProbeStartRenderLog();
            window.SubmitForProbe("1+2");
            PumpUntil(() => window.ProbeRenderLog.Count > 0, PumpTimeoutMs);
            window.ProbeSelectIndex(0);
            var (ctrlEnterClipboard, ctrlEnterHidden, _) = ProbeCopy(window, InputCmd.CtrlEnter);

            return new JsonObject
            {
                // ①
                ["okRowShown"] = okShown,
                ["okRowCount"] = okRows.Count,
                ["okKind"] = okRow?.Kind ?? "(none)",
                ["okTitle"] = okRow?.Title ?? "(none)",
                ["okSubtitle"] = okRow?.Subtitle ?? "(none)",
                ["okBadge"] = okRow?.Badge ?? "(none)",
                ["okLeadingKinds"] = new JsonArray([.. okLeading.Select(k => (JsonNode)k)]),
                ["okItemsCount"] = okItems,
                ["okRenderLog"] = okRenderLog,

                // ②
                ["enterHandled"] = enterHandled,
                ["okClipboard"] = okClipboard,
                ["okWindowHidden"] = okHidden,

                // ③④⑤
                ["plainNumber"] = new JsonObject
                {
                    ["calcRows"] = plain.Rows,
                    ["fileRows"] = plain.Files,
                    ["rendered"] = plain.Rendered,
                },
                ["letterText"] = new JsonObject
                {
                    ["calcRows"] = withLetter.Rows,
                    ["fileRows"] = withLetter.Files,
                    ["rendered"] = withLetter.Rendered,
                },
                ["halfDone"] = new JsonObject
                {
                    ["calcRows"] = halfDone.Rows,
                    ["fileRows"] = halfDone.Files,
                    ["rendered"] = halfDone.Rendered,
                },

                // ⑥
                ["divRows"] = divRows.Rows.Count,
                ["divTitle"] = errRow?.Title ?? "(none)",
                ["divSubtitle"] = errRow?.Subtitle ?? "(none)",
                ["clipboardBeforeEnter"] = clipboardBeforeEnter,
                ["clipboardAfterEnter"] = clipboardAfterEnter,
                ["errorStatusText"] = errorStatus,
                ["errorWindowStillVisible"] = errorStillVisible,

                // ⑦
                ["ctrlEnterClipboard"] = ctrlEnterClipboard,
                ["ctrlEnterWindowHidden"] = ctrlEnterHidden,
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    /// <summary>
    /// 提交一段文本，**等到"这一轮真的写进了列表"**，再回读该来源的行数与文件段条数。
    ///
    /// <para><b>★ 就绪信号必须用渲染日志，不能用"行数 &gt; 0"</b>：上一轮的行还在列表里，
    /// "行数 &gt; 0" 在新一轮渲染落地**之前**就已经成立 ⇒ 会读到**陈旧行**
    /// （2026-10-01 实测：`1GB to MiB` 读回了上一条 `-40 °F`）。
    /// 渲染日志（<see cref="SearchWindow.ProbeStartRenderLog"/>）按"真正写进列表"计数，清空后再涨即本轮落地。</para>
    ///
    /// <para><b>★ 为什么必须同时回传 <c>Files</c> 与 <c>Rendered</c></b>：只断言"没有该来源的行"是
    /// **弱断言** —— "整轮查询压根没发生"也满足它。<c>Files == 200</c> 说明文件段照常渲染，
    /// <c>Rendered</c> 说明这一轮**确实渲染过**（否则拿到的可能只是上一轮的残留）。</para>
    /// </summary>
    private static (int Rows, int Files, bool Rendered) SubmitAndCountRows(
        SearchWindow window, string text, LauncherKind kind)
    {
        var (_, items, rendered) = SubmitAndWaitRender(window, text);
        return (window.SnapshotRows().Count(r => Eq(r.Kind, kind.ToString())), items, rendered);
    }

    /// <summary>提交并等本轮渲染落地（见 <see cref="SubmitAndCountRows"/> 的就绪信号说明）。</summary>
    private static (IReadOnlyList<LauncherRowSnapshot> Rows, int Items, bool Rendered) SubmitAndSnapshot(
        SearchWindow window, string text) => SubmitAndWaitRender(window, text);

    private static (IReadOnlyList<LauncherRowSnapshot> Rows, int Items, bool Rendered) SubmitAndWaitRender(
        SearchWindow window, string text)
    {
        if (!window.ProbeIsVisible)
        {
            window.ShowForProbe();   // 上一轮 Enter 收过窗；重开不改变被测语义
        }

        window.ProbeStartRenderLog();                                   // 清空 ⇒ "涨了" = 本轮落地
        window.SubmitForProbe(text);
        var rendered = PumpUntil(() => window.ProbeRenderLog.Count > 0, PumpTimeoutMs);
        return (window.SnapshotRows(), window.ProbeItemCount, rendered);
    }

    // ── unit：真链路（触发词三态 / 仿射温度 / 两制式标注 / 无触发词列常用单位）────

    /// <summary>单位换算的真链路探针（真 <see cref="UnitProvider"/> + 真窗口 + 真动作分派）。</summary>
    private static JsonObject RunUnit()
    {
        var client = new SearchIndexClient(new ProbeSearchUiTransport());
        var window = new SearchWindow(client, [new FilesProvider(client), new UnitProvider()])
        {
            ProbeSuppressDrift = true,
        };

        try
        {
            window.ShowForProbe();

            // ① 带触发词 ⇒ 一行精确结果且置顶
            window.SubmitForProbe("10 km to mi");
            var shown = PumpUntil(() => window.SnapshotRows().Count > 0, PumpTimeoutMs);
            var rows = window.SnapshotRows();
            var row = rows.Count > 0 ? rows[0] : null;
            var leading = window.ProbeLeadingKinds(2);
            var items = window.ProbeItemCount;

            // ② Enter ⇒ 复制结果值 + 收窗
            var (clipboard, hidden, enterHandled) = ProbeCopy(window, InputCmd.Enter);

            // ③ 温度仿射恒等式 `-40C == -40F`（经典自证点）
            var negative = SubmitAndSnapshot(window, "-40C to F");

            // ④ 数据量两制式 ⇒ 副行必须带制式说明
            var data = SubmitAndSnapshot(window, "1GB to MiB");

            // ⑤ 无触发词 ⇒ 列该类别常用单位（且**不含源单位**）
            var listed = SubmitAndSnapshot(window, "10km");

            // ⑥ 不做的换算（单位混算 / 未知单位）⇒ 静默，但文件段照常
            var mixed = SubmitAndCountRows(window, "10km + 5mi", LauncherKind.Unit);
            var unknown = SubmitAndCountRows(window, "10xyz to m", LauncherKind.Unit);

            return new JsonObject
            {
                ["okShown"] = shown,
                ["okRowCount"] = rows.Count,
                ["okKind"] = row?.Kind ?? "(none)",
                ["okTitle"] = row?.Title ?? "(none)",
                ["okSubtitle"] = row?.Subtitle ?? "(none)",
                ["okBadge"] = row?.Badge ?? "(none)",
                ["okSecondary"] = row?.Subtitle ?? "(none)",
                ["okLeadingKinds"] = new JsonArray([.. leading.Select(k => (JsonNode)k)]),
                ["okItemsCount"] = items,
                ["enterHandled"] = enterHandled,
                ["okClipboard"] = clipboard,
                ["okWindowHidden"] = hidden,

                ["negTitle"] = negative.Rows.Count > 0 ? negative.Rows[0].Title : "(none)",
                ["negSubtitle"] = negative.Rows.Count > 0 ? negative.Rows[0].Subtitle : "(none)",
                ["dataTitle"] = data.Rows.Count > 0 ? data.Rows[0].Title : "(none)",
                ["dataSubtitle"] = data.Rows.Count > 0 ? data.Rows[0].Subtitle : "(none)",

                ["listRowCount"] = listed.Rows.Count,
                ["listTitles"] = new JsonArray([.. listed.Rows.Select(r => (JsonNode)r.Title)]),

                ["mixedRows"] = mixed.Rows,
                ["mixedFileRows"] = mixed.Files,
                ["mixedRendered"] = mixed.Rendered,
                ["unknownRows"] = unknown.Rows,
                ["unknownFileRows"] = unknown.Files,
                ["unknownRendered"] = unknown.Rendered,
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    // ── encode：真链路（六前缀 / 非法输入显式错误行 / 无前缀静默）────────────────

    /// <summary>编码转换的真链路探针（真 <see cref="EncodeProvider"/> + 真窗口 + 真动作分派）。</summary>
    private static JsonObject RunEncode()
    {
        var client = new SearchIndexClient(new ProbeSearchUiTransport());
        var window = new SearchWindow(client, [new FilesProvider(client), new EncodeProvider()])
        {
            ProbeSuppressDrift = true,
        };

        try
        {
            window.ShowForProbe();

            // ① base64 编码 ⇒ 一行结果且置顶
            window.SubmitForProbe("b64:你好");
            var shown = PumpUntil(() => window.SnapshotRows().Count > 0, PumpTimeoutMs);
            var rows = window.SnapshotRows();
            var row = rows.Count > 0 ? rows[0] : null;
            var leading = window.ProbeLeadingKinds(2);
            var items = window.ProbeItemCount;

            // ② Enter ⇒ 复制转换结果 + 收窗
            var (clipboard, hidden, enterHandled) = ProbeCopy(window, InputCmd.Enter);

            // ③ 其余方向
            var decode = SubmitAndSnapshot(window, "b64d:5L2g5aW9");
            var codePoints = SubmitAndSnapshot(window, "u:中A");
            var fromCodePoints = SubmitAndSnapshot(window, "ud:U+4E2D U+0041");
            var url = SubmitAndSnapshot(window, "url:a b&c");

            // ④ 非法输入 ⇒ **显式错误行**（不是静默）+ 动作禁用 ⇒ Enter 不复制、不收窗
            var bad = SubmitAndSnapshot(window, "b64d:!!!!");
            var badRow = bad.Rows.Count > 0 ? bad.Rows[0] : null;
            var clipBefore = ReadClipboardText();
            _ = window.ProbeCommand(InputCmd.Enter);
            var badStatus = window.ProbeStatusText;
            var badVisible = window.ProbeIsVisible;
            var clipAfter = ReadClipboardText();

            // ⑤ 残缺 URL 转义同样是"显式错误行"（`Uri.UnescapeDataString` 自己不会报）
            var badUrl = SubmitAndSnapshot(window, "urld:a%ZZ");

            // ⑥ 无前缀 ⇒ 完全静默（D9=A：base64 是弱特征，自动嗅探必然污染文件搜索结果）
            var bare = SubmitAndCountRows(window, "test", LauncherKind.Encode);

            return new JsonObject
            {
                ["okShown"] = shown,
                ["okRowCount"] = rows.Count,
                ["okKind"] = row?.Kind ?? "(none)",
                ["okTitle"] = row?.Title ?? "(none)",
                ["okSubtitle"] = row?.Subtitle ?? "(none)",
                ["okBadge"] = row?.Badge ?? "(none)",
                ["okLeadingKinds"] = new JsonArray([.. leading.Select(k => (JsonNode)k)]),
                ["okItemsCount"] = items,
                ["enterHandled"] = enterHandled,
                ["okClipboard"] = clipboard,
                ["okWindowHidden"] = hidden,

                ["decodeTitle"] = decode.Rows.Count > 0 ? decode.Rows[0].Title : "(none)",
                ["codePointTitle"] = codePoints.Rows.Count > 0 ? codePoints.Rows[0].Title : "(none)",
                ["fromCodePointTitle"] = fromCodePoints.Rows.Count > 0 ? fromCodePoints.Rows[0].Title : "(none)",
                ["urlTitle"] = url.Rows.Count > 0 ? url.Rows[0].Title : "(none)",

                ["badRows"] = bad.Rows.Count,
                ["badTitle"] = badRow?.Title ?? "(none)",
                ["badSubtitle"] = badRow?.Subtitle ?? "(none)",
                ["badClipboardBefore"] = clipBefore,
                ["badClipboardAfter"] = clipAfter,
                ["badStatusText"] = badStatus,
                ["badWindowStillVisible"] = badVisible,

                ["badUrlTitle"] = badUrl.Rows.Count > 0 ? badUrl.Rows[0].Title : "(none)",

                ["bareRows"] = bare.Rows,
                ["bareFileRows"] = bare.Files,
                ["bareRendered"] = bare.Rendered,
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    // ── config：**真配置文件**的两层测试（CLI 写入 ↔ 应用解析/告警）────────────

    /// <summary>
    /// 读真实配置中心并落出"解析结果 + 告警文案"。这是 <c>launcher.providers</c> 的
    /// **协议层↔应用层**闭环观测面：CLI 层负责"写进去读得回来"，应用层负责
    /// "未知值被拒 + 回落默认 + 告警可见"。两层的判据在 acceptance 里合在一起断。
    /// </summary>
    private static JsonObject RunConfig(Eztools.Host.Config.ConfigStore? configs)
    {
        if (configs is null)
        {
            // 走到这说明调用方没把配置中心传进来 —— 显式落盘而不是静默给个空对象
            return new JsonObject
            {
                ["hasError"] = true,
                ["error"] = "探针缺少配置中心（代码接线缺失）",
                ["hasWarning"] = false,
            };
        }

        var (prefs, error) = Eztools.Host.Launcher.LauncherPrefs.FromConfig(configs);
        var warning = SearchWindow.StartupWarningFor(error);
        var (aliases, aliasError) = Eztools.Host.Launcher.LauncherAliases.FromConfig(configs);

        return new JsonObject
        {
            ["providers"] = new JsonArray([.. prefs.Providers.Select(p => (JsonNode)p)]),
            ["usageEnabled"] = prefs.UsageEnabled,
            ["aliasCount"] = aliases.Entries.Count,
            ["aliasFirst"] = aliases.Entries.Count > 0 ? $"{aliases.Entries[0].Alias}=>{aliases.Entries[0].Target}" : "(无)",
            ["hasError"] = error is not null,
            ["error"] = error ?? "(无)",
            ["hasAliasError"] = aliasError is not null,
            ["aliasError"] = aliasError ?? "(无)",
            ["hasWarning"] = warning is not null,
            ["warning"] = warning ?? "(无)",
        };
    }

    // ── isolation：段位故障必须在状态行可见（FR-9） ─────────────────────────

    private static JsonObject RunIsolation()
    {
        var transport = new ProbeSearchUiTransport();
        var client = new SearchIndexClient(transport);
        var providers = new ILauncherProvider[]
        {
            new FilesProvider(client),
            new FakeProvider { Id = LauncherProviderRegistry.Apps, Throw = true },
        };

        var window = new SearchWindow(client, providers) { ProbeSuppressDrift = true };
        JsonObject result;
        try
        {
            window.ShowForProbe();
            window.SubmitForProbe("报");
            PumpUntil(() => window.ProbeItemCount > 0, PumpTimeoutMs);

            result = new JsonObject
            {
                ["itemsCount"] = window.ProbeItemCount,          // files 段照常渲染
                ["statusText"] = window.ProbeStatusText,         // 段位故障原因必须在这里
                ["hasReason"] = window.ProbeStatusText.Contains("暂不可用", StringComparison.Ordinal),
            };
        }
        finally
        {
            window.CloseForProbe();
        }

        // ② 启动期配置告警的**可见出口**（设计方案 §5：非法 launcher.providers ⇒ 状态行报一次）。
        //    与上面的"段位故障"同族：都是"降级信号必须有可达出口"，不能只写日志。
        const string warning = "启动器配置无效（已回落默认来源）：未知 provider \"foo\"（已知：files、apps）";
        var client2 = new SearchIndexClient(new ProbeSearchUiTransport());
        var w2 = new SearchWindow(client2, [new FilesProvider(client2)], startupWarning: warning)
        {
            ProbeSuppressDrift = true,
        };
        try
        {
            w2.ShowForProbe();
            var shown = w2.ProbeStatusText.Contains("启动器配置无效", StringComparison.Ordinal);

            // 查询结果到达后必须能**覆盖**它（告警只报一次，不形成常驻噪音）
            w2.SubmitForProbe("报");
            PumpUntil(() => w2.ProbeItemCount > 0, PumpTimeoutMs);
            var overwritten = !w2.ProbeStatusText.Contains("启动器配置无效", StringComparison.Ordinal);

            result["configWarningShown"] = shown;
            result["configWarningOverwrittenByResults"] = overwritten;
            result["configWarningText"] = warning;
        }
        finally
        {
            w2.CloseForProbe();
        }

        return result;
    }

    // ── apps：真 AppsProvider（临时扫描根）──────────────────────────────────

    // ── usage：频次记忆真链路（W7-e，§10.11） ─────────────────────────────

    /// <summary>
    /// 频次记忆的模式（真 <see cref="AppsProvider"/> + 真 <see cref="LauncherUsageStore"/>，
    /// 文件落到探针临时目录）：
    /// ① 记录两次后重查 ⇒ 被记录的条目**排序提前**（同分前提下的确定性断言）；
    /// ② 落盘回读：Record ⇒ Flush ⇒ 新实例读同一文件 ⇒ count/加成一致（持久化真的发生了）；
    /// ③ 开关关 ⇒ Record 不写文件、BoostFor 恒 0（"彻底不读写"）；
    /// ④ 文件损坏 ⇒ 空表 + LastError 非空（响亮但不崩、不阻塞查询）。
    /// </summary>
    private static JsonObject RunUsage()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezt-usage-probe-" + Guid.NewGuid().ToString("N")[..8]);
        _ = Directory.CreateDirectory(root);
        try
        {
            // 夹具：两条**同分**应用（查询 "app" 对两个标题都是同位置子串）
            var alphaPath = Path.Combine(root, "OneApp.lnk");
            var betaPath = Path.Combine(root, "TwoApp.lnk");
            File.WriteAllText(alphaPath, "stub");
            File.WriteAllText(betaPath, "stub");

            var usageFile = Path.Combine(root, "usage.json");
            var store = new LauncherUsageStore(usageFile, enabled: true);
            store.Record(LauncherProviderRegistry.Apps, betaPath);
            store.Record(LauncherProviderRegistry.Apps, betaPath);

            var cache = new AppIndexCache([new AppScanRoot(root, Recursive: false, AppSource.UserStartMenu)],
                includeAppPaths: false);
            var apps = new AppsProvider(cache, store);
            _ = WaitFor(() => apps.IsReady, 5000);

            var transport = new ProbeSearchUiTransport();
            var client = new SearchIndexClient(transport);
            var window = new SearchWindow(client, [new FilesProvider(client), apps])
            {
                ProbeSuppressDrift = true,
            };

            string firstTitle;
            string secondTitle;
            try
            {
                window.ShowForProbe();
                window.ProbeStartRenderLog();   // ★ 就绪信号 = 渲染日志（不能用"行数>0"）
                window.SubmitForProbe("app");
                PumpUntil(() => window.ProbeRenderLog.Count > 0, PumpTimeoutMs);

                var appTitles = window.SnapshotRows()
                    .Where(r => Eq(r.Kind, nameof(LauncherKind.App)))
                    .Select(r => r.Title)
                    .ToArray();
                firstTitle = appTitles.Length > 0 ? appTitles[0] : "(none)";
                secondTitle = appTitles.Length > 1 ? appTitles[1] : "(none)";
            }
            finally
            {
                window.CloseForProbe();
            }

            // ② 落盘回读
            var flushed = store.Flush(TimeSpan.FromSeconds(3));
            var reloaded = new LauncherUsageStore(usageFile, enabled: true);
            reloaded.LoadNow();
            var roundtripCount = reloaded.CountOf(LauncherProviderRegistry.Apps, betaPath);
            var roundtripBoost = reloaded.BoostFor(LauncherProviderRegistry.Apps, betaPath);

            // ③ 开关关 ⇒ 彻底不读写
            var disabledFile = Path.Combine(root, "disabled.json");
            var disabled = new LauncherUsageStore(disabledFile, enabled: false);
            disabled.Record(LauncherProviderRegistry.Apps, betaPath);
            var disabledWroteFile = File.Exists(disabledFile);
            var disabledBoost = disabled.BoostFor(LauncherProviderRegistry.Apps, betaPath);

            // ④ 损坏文件 ⇒ 空表 + LastError（不抛、不阻塞）
            var corruptFile = Path.Combine(root, "corrupt.json");
            File.WriteAllText(corruptFile, "{ 这不是 JSON");
            var corrupt = new LauncherUsageStore(corruptFile, enabled: true);
            corrupt.LoadNow();

            return new JsonObject
            {
                ["firstAppTitle"] = firstTitle,
                ["secondAppTitle"] = secondTitle,
                ["recordedFirst"] = Eq(firstTitle, "TwoApp"),
                ["flushed"] = flushed,
                ["roundtripCount"] = (int)roundtripCount,
                ["roundtripBoost"] = roundtripBoost,
                ["usageFileExists"] = File.Exists(usageFile),
                ["disabledWroteFile"] = disabledWroteFile,
                ["disabledBoost"] = disabledBoost,
                ["corruptEntryCount"] = corrupt.EntryCount,
                ["corruptHasError"] = corrupt.LastError is not null,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // review-guards:allow-empty-catch :: 探针临时目录清理失败（句柄未释放等）属
                // 环境噪声，不影响断言；为它写日志只会刷屏
            }
        }
    }

    private static JsonObject RunApps()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezt-launcher-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var sub = Path.Combine(root, "Sub");
        var iconDir = Path.Combine(root, "icons");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(iconDir);
        try
        {
            // 夹具：递归子目录里的 .lnk、根上的 .url、以及必须被过滤的"卸载"项
            var notepad = Path.Combine(root, "记事本.lnk");
            var code = Path.Combine(sub, "Visual Studio Code.lnk");
            var site = Path.Combine(root, "Example.url");
            var uninstall = Path.Combine(root, "Uninstall 记事本.lnk");
            foreach (var f in new[] { notepad, code, site, uninstall })
            {
                File.WriteAllText(f, "stub");
            }

            var cache = new AppIndexCache([new AppScanRoot(root, Recursive: true, AppSource.UserStartMenu)],
                includeAppPaths: false);
            var apps = new AppsProvider(cache);

            var ready = WaitFor(() => apps.IsReady, 5000);
            var entries = cache.Items.Select(e => e.Title).OrderBy(t => t, StringComparer.Ordinal).ToArray();

            // 真链路：把 apps 装进窗口，搜 "code" ⇒ 命中 `Visual Studio Code`
            var transport = new ProbeSearchUiTransport();
            var client = new SearchIndexClient(transport);
            var window = new SearchWindow(client, [new FilesProvider(client), apps]) { ProbeSuppressDrift = true };
            JsonObject launchArgs;
            int itemCount;
            try
            {
                window.ShowForProbe();
                window.SubmitForProbe("code");
                PumpUntil(() => window.SnapshotRows().Count > 0, PumpTimeoutMs);
                var rows = window.SnapshotRows();
                var appRow = rows.FirstOrDefault(r => Eq(r.Kind, nameof(LauncherKind.App)));
                itemCount = window.ProbeItemCount;
                launchArgs = new JsonObject
                {
                    ["title"] = appRow?.Title ?? "(none)",
                    ["badge"] = appRow?.Badge ?? "(none)",
                    ["subtitle"] = appRow?.Subtitle ?? "(none)",
                    ["iconShown"] = appRow?.IconShown ?? false,
                    ["segmentCount"] = appRow?.Segments.Count ?? -1,
                    ["highlightText"] = appRow is null
                        ? "(none)"
                        : string.Concat(appRow.Segments.Where(s => s.Bold).Select(s => s.Text)),
                };
            }
            finally
            {
                window.CloseForProbe();
            }

            // 图标句柄斜率（R4 / §11.5）：200 次提取后进程句柄数不得增长
            IconCache.Reset();
            var before = IconCache.ProcessHandleCount();
            for (var i = 0; i < 200; i++)
            {
                _ = IconCache.GetIcon(i % 2 == 0 ? notepad : code);
            }

            var after = IconCache.ProcessHandleCount();

            // 缓存上限（R11）：条数超过上限时不得无限增长
            for (var i = 0; i < 520; i++)
            {
                var f = Path.Combine(iconDir, $"f{i}.txt");
                File.WriteAllText(f, "x");
            }

            for (var i = 0; i < 520; i++)
            {
                _ = IconCache.GetIcon(Path.Combine(iconDir, $"f{i}.txt"));
            }

            var cached = IconCache.CachedCount;

            // 指纹失效重扫：新增一个条目 ⇒ InvalidateIfChanged 必须重扫并看到它
            var beforeRescan = cache.ScanCount;
            File.WriteAllText(Path.Combine(root, "新增应用.lnk"), "stub");
            cache.InvalidateIfChanged();
            var rescanned = WaitFor(() => cache.ScanCount > beforeRescan, 5000);
            var seesNewEntry = cache.Items.Any(e => Eq(e.Title, "新增应用"));

            // 首扫就绪补发（R10）：用"延迟就绪"的假来源做确定性断言
            var delayed = new FakeProvider { Id = LauncherProviderRegistry.Apps, IsReady = false };
            var transport2 = new ProbeSearchUiTransport();
            var client2 = new SearchIndexClient(transport2);
            var window2 = new SearchWindow(client2, [new FilesProvider(client2), delayed]) { ProbeSuppressDrift = true };
            int requery;
            int itemsBefore;
            int itemsAfter;
            try
            {
                window2.ShowForProbe();
                window2.SubmitForProbe("a");
                PumpUntil(() => window2.ProbeItemCount > 0, PumpTimeoutMs);
                itemsBefore = window2.ProbeItemCount;

                // 就绪 + 有结果 ⇒ 补发一次 ⇒ 应用结果必须自己冒出来（不需要再敲字符）
                delayed.Items = [Item(LauncherKind.App, "记事本", RealExe, 10, [], RealExe)];
                delayed.IsReady = true;
                delayed.NotifyReady();
                PumpUntil(() => window2.SnapshotRows().Count > 0, PumpTimeoutMs);
                requery = window2.RequeryCount;
                itemsAfter = window2.ProbeItemCount;
            }
            finally
            {
                window2.CloseForProbe();
            }

            return new JsonObject
            {
                ["ready"] = ready,
                ["entryCount"] = entries.Length,
                ["entries"] = new JsonArray([.. entries.Select(e => (JsonNode)e)]),
                ["uninstallFiltered"] = !entries.Any(e => e.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)),
                ["urlIncluded"] = entries.Any(e => Eq(e, "Example")),
                ["recursiveIncluded"] = entries.Any(e => Eq(e, "Visual Studio Code")),
                ["launch"] = launchArgs,
                ["itemsCount"] = itemCount,
                ["iconHandlesBefore"] = before,
                ["iconHandlesAfter"] = after,
                ["iconHandleDelta"] = after - before,
                ["iconCached"] = cached,
                ["iconCacheHits"] = IconCache.Hits,
                ["iconCacheMisses"] = IconCache.Misses,
                ["rescanTriggered"] = rescanned,
                ["seesNewEntry"] = seesNewEntry,
                ["requeryCount"] = requery,
                ["itemsBeforeReady"] = itemsBefore,
                ["itemsAfterReady"] = itemsAfter,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // review-guards:allow-empty-catch :: 临时目录清理失败不影响任何结论
                // （每轮用新的随机目录名，残留由系统临时目录策略回收）
            }
        }
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>本进程自己的 exe（一定存在 ⇒ 图标提取必然有结果，供 rows/apps 断言用）。</summary>
    private static string RealExe => Environment.ProcessPath ?? "";

    private static LauncherItem Item(
        LauncherKind kind,
        string title,
        string subtitle,
        double score,
        IReadOnlyList<(int Start, int Len)> highlights,
        string iconHint = "")
    {
        // 动作按 Kind 给 —— 应用是"启动/定位"，其余是"复制结果值"（与生产 provider 同款）
        var isApp = kind == LauncherKind.App;
        return new LauncherItem(
            kind, title, subtitle, iconHint, score, highlights,
            isApp
                ? new LauncherAction(LauncherActionKind.Launch, iconHint)
                : new LauncherAction(LauncherActionKind.CopyText, title),
            isApp ? new LauncherAction(LauncherActionKind.RevealApp, iconHint) : null,
            null);
    }

    private static ILauncherProvider FakeOf(string id, IReadOnlyList<LauncherItem> items) =>
        new FakeProvider { Id = id, Items = items };

    private sealed class FakeProvider : ILauncherProvider, ILauncherReadyNotifier
    {
        public string Id { get; init; } = LauncherProviderRegistry.Calc;

        public string DisplayName => Id;

        public bool IsReady { get; set; } = true;

        public bool Throw { get; init; }

        /// <summary>非空 ⇒ <c>QueryAsync</c> 阻塞到它被 Set（模拟"慢来源"，用于代次闸用例）。</summary>
        public ManualResetEventSlim? Gate { get; init; }

        public IReadOnlyList<LauncherItem> Items { get; set; } = Array.Empty<LauncherItem>();

        /// <summary>被调用次数（断言"确实问过它" —— 0 次说明用例根本没跑到被测路径）。</summary>
        public int Calls => Volatile.Read(ref _calls);

        private int _calls;

        public event Action? BecameReady;

        public void NotifyReady() => BecameReady?.Invoke();

        public async Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);

            if (Throw)
            {
                throw new InvalidOperationException("模拟 provider 故障");
            }

            if (Gate is { } gate)
            {
                // 有限等待：用例失败时探针也要能退出（绝不把探针挂死）
                await Task.Run(() => gate.Wait(3000), CancellationToken.None).ConfigureAwait(false);
            }

            return new LauncherResultSet(
                query.Generation, Id, Items, Items.Count, 0, Dropped: false, Error: null);
        }
    }

    private static string ReadClipboardText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        }
        catch (Exception)
        {
            return "(unavailable)";
        }
    }

    /// <summary>清空剪贴板（复制类断言的前置哨兵：清不掉时下面的重试会读到残留值，断言响亮失败）。</summary>
    private static void ClearClipboard()
    {
        try
        {
            System.Windows.Clipboard.Clear();
        }
        catch (Exception)
        {
            // review-guards:allow-empty-catch :: 剪贴板被别的进程短暂占住时清不掉是**环境噪声**；
            // 此时后续读到的是残留值 ⇒ 断言照样变红（响亮失败），不吞任何结论
        }
    }

    /// <summary>
    /// 清空剪贴板 → 执行复制类命令 → 读回。**有限重试**。
    ///
    /// <para><b>为什么要重试</b>：OLE 剪贴板在自动化环境里偶发被别的进程短暂占住
    /// （<c>OpenClipboard</c> 失败 / 读取到空），这与"复制了**错的**内容"是两种失败 ——
    /// 前者重试即可（环境噪声），后者重试也没用（照样被抓到）。
    /// 清空是前置哨兵：复制失败时读到的是空串（响亮失败），而不是上一轮的残留值（假绿）。</para>
    /// </summary>
    private static (string Text, bool Hidden, bool Handled) ProbeCopy(SearchWindow window, InputCmd cmd)
    {
        var text = "";
        var hidden = false;
        var handled = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ClearClipboard();
            if (!window.ProbeIsVisible)
            {
                window.ShowForProbe();   // 上一轮 Enter 收过窗；重开不改变被测语义
            }

            handled = window.ProbeCommand(cmd);
            text = ReadClipboardText();
            hidden = !window.ProbeIsVisible;
            if (text.Length > 0)
            {
                break;   // 复制到了内容 ⇒ 停止（内容错了也照常断红）
            }
        }

        return (text, hidden, handled);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(15);
        }

        return condition();
    }

    /// <summary>
    /// 泵 Dispatcher 直到条件成立或超时（与 <c>SearchUiProbe.PumpUntil</c> 同款：
    /// 每轮 <c>Invoke(ApplicationIdle)</c> 会先处理所有更高优先级的工作，再 Sleep 让 DispatcherTimer 到点）。
    /// </summary>
    /// <returns>条件是否成立（超时返回 false —— 调用方据此断言"确实到了那一步"）。</returns>
    private static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var dispatcher = Dispatcher.CurrentDispatcher;
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(15);
        }

        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return condition();
    }

    /// <summary>固定泵一段时间（用于"给一条**不该发生**的渲染留出发生的机会"）。</summary>
    private static void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        var dispatcher = Dispatcher.CurrentDispatcher;
        while (sw.ElapsedMilliseconds < ms)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(15);
        }
    }

    // ── corestatus：核心服务不可达时的两个显示面（W8·B1）──────────────────────

    /// <summary>
    /// 四种可达性各跑一次窗口，断言 <b>两个显示面都分流</b>且只有"未运行 / 未提权"给出可点出口。
    ///
    /// <para><b>为什么不真启动核心服务</b>：注入的宿主动作是探针自己的计数器 ——
    /// 探针绝不弹 UAC（NFR-5）。本模式验三段：① 状态行文案分流 ② 卷清单行文案分流
    /// ③ 出口**真的接到了宿主动作**（点一下计数 +1，而不是只长得像可点）。</para>
    /// </summary>
    private static JsonObject RunCoreStatus()
    {
        var cases = new JsonArray();
        foreach (var availability in new[]
                 {
                     CoreAvailability.CoreOk,
                     CoreAvailability.CoreNotRunning,
                     CoreAvailability.CoreNotElevated,
                     CoreAvailability.Unknown,
                 })
        {
            cases.Add(ProbeCoreStatus(availability));
        }

        return new JsonObject
        {
            ["cases"] = cases,
            ["launch"] = RunCoreLaunchChecks(),
            ["echo"] = RunCoreLaunchEchoChecks(),
        };
    }

    /// <summary>
    /// 启动动作的**可判定面**（W8·B1）。真起一个提权进程没法自动化，但"启动参数对不对、
    /// 取消怎么识别、等到什么算成功、回显什么话"全都能穷举 ——
    /// 而其中"**是不是真的走了 UAC 提权**"是 NFR-1 红线，绝不该只靠人眼看弹窗。
    /// </summary>
    private static JsonObject RunCoreLaunchChecks()
    {
        // ① 提权启动参数（红线：少了 UseShellExecute/runas 就不是提权）
        var psi = CoreLauncher.BuildStartInfo(@"C:\fake\ezt-core.exe", @"C:\fake\root");

        // ② 异常映射：1223 = 用户取消（不是失败）；其它码/异常 = 失败
        var cancelled = CoreLauncher.MapLaunchFailure(new Win32Exception(CoreLauncher.ErrorCancelled));
        var denied = CoreLauncher.MapLaunchFailure(new Win32Exception(5));
        var generic = CoreLauncher.MapLaunchFailure(new InvalidOperationException("boom"));

        // ③ 就绪判定的单轮决策（穷举四种组合）
        var steps = new JsonArray();
        foreach (var (name, exited, code, registered) in new (string, bool, int, bool)[]
                 {
                     ("进程活着 + 端点已登记", false, 0, true),
                     ("进程活着 + 端点未登记", false, 0, false),
                     ("进程退出 exit=3（已有实例）", true, 3, false),
                     ("进程退出 exit=1（异常退出）", true, 1, false),
                 })
        {
            steps.Add(new JsonObject
            {
                ["case"] = name,
                ["decision"] = CoreLauncher.DecideWaitStep(exited, code, registered)?.ToString() ?? "(继续等)",
            });
        }

        // ④ 结局文案（用户看到的那句话，单点确定）
        var messages = new JsonObject();
        foreach (var outcome in Enum.GetValues<CoreLaunchOutcome>())
        {
            messages[outcome.ToString()] = outcome == CoreLaunchOutcome.Failed
                ? CoreLauncher.FailedMessage("(带原因)")
                : CoreLauncher.MessageFor(outcome);
        }

        return new JsonObject
        {
            ["useShellExecute"] = psi.UseShellExecute,
            ["verb"] = psi.Verb ?? "",
            ["createNoWindow"] = psi.CreateNoWindow,
            ["windowStyle"] = psi.WindowStyle.ToString(),
            ["redirected"] = psi.RedirectStandardOutput || psi.RedirectStandardError
                             || psi.RedirectStandardInput,
            ["arguments"] = psi.Arguments,
            ["cancelOutcome"] = cancelled.Outcome.ToString(),
            ["cancelMessage"] = cancelled.Message,
            ["deniedOutcome"] = denied.Outcome.ToString(),
            ["genericOutcome"] = generic.Outcome.ToString(),
            ["waitSteps"] = steps,
            ["messages"] = messages,
        };
    }

    /// <summary>
    /// 启动结果**回显策略**（W8·B1）：三种结局下状态行说什么、出口留不留。
    ///
    /// <para>★ 关键判据是"失败/取消后**出口必须还在**" —— 用户取消一次 UAC 按钮就消失的话，
    /// 他只能去命令行解决，而那正是这个修复想替他省掉的。</para>
    /// </summary>
    private static JsonArray RunCoreLaunchEchoChecks()
    {
        var echo = new JsonArray();
        foreach (var outcome in new[]
                 {
                     CoreLaunchOutcome.Cancelled,
                     CoreLaunchOutcome.Failed,
                     CoreLaunchOutcome.Launched,
                 })
        {
            echo.Add(ProbeLaunchEcho(outcome));
        }

        return echo;
    }

    private static JsonObject ProbeLaunchEcho(CoreLaunchOutcome outcome)
    {
        var transport = new CoreStatusTransport();
        var client = new SearchIndexClient(transport);
        var providers = new ILauncherProvider[]
        {
            new FilesProvider(client, () => CoreAvailability.CoreNotRunning),
        };

        var message = outcome == CoreLaunchOutcome.Failed
            ? CoreLauncher.FailedMessage("探针造的失败原因")
            : CoreLauncher.MessageFor(outcome);

        var clicks = 0;
        var window = new SearchWindow(client, providers,
            coreAvailability: () => CoreAvailability.CoreNotRunning)
        {
            ProbeSuppressDrift = true,
            CoreLaunchRequested = () =>
            {
                clicks++;
                return Task.FromResult((outcome, message));
            },
        };

        try
        {
            window.ShowForProbe();
            window.SubmitForProbe("a");
            PumpUntil(() => window.ProbeStatusLaunchable, PumpTimeoutMs);

            var beforeClick = window.ProbeStatusLaunchable;
            window.ProbeInvokeStatusClick();
            PumpUntil(() => clicks > 0, PumpTimeoutMs);
            PumpFor(150);   // 让 LaunchCoreAsync 的续体把回显落地

            return new JsonObject
            {
                ["outcome"] = outcome.ToString(),
                ["launchableBeforeClick"] = beforeClick,
                ["clicks"] = clicks,
                ["statusTextAfterClick"] = window.ProbeStatusText,
                ["launchableAfterClick"] = window.ProbeStatusLaunchable,
                ["expectedMessage"] = message,
                ["messageMatched"] = window.ProbeStatusText == message,
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    private static JsonObject ProbeCoreStatus(CoreAvailability availability)
    {
        var transport = new CoreStatusTransport();
        var client = new SearchIndexClient(transport);
        var providers = new ILauncherProvider[] { new FilesProvider(client, () => availability) };

        var launchClicks = 0;
        var window = new SearchWindow(client, providers, coreAvailability: () => availability)
        {
            ProbeSuppressDrift = true,
            // 假的宿主动作：只记一次，不启动任何进程（探针零副作用）
            CoreLaunchRequested = () =>
            {
                launchClicks++;
                return Task.FromResult((CoreLaunchOutcome.Cancelled, "（探针）未真正启动"));
            },
        };

        try
        {
            window.ShowForProbe();

            // ① 结果状态行：发一次查询（假传输一律回 -32001）
            window.SubmitForProbe("a");
            PumpUntil(() => window.ProbeStatusText.Contains('索', StringComparison.Ordinal)
                            || window.ProbeStatusText.Contains('核', StringComparison.Ordinal),
                PumpTimeoutMs);

            var statusText = window.ProbeStatusText;
            var statusLaunchable = window.ProbeStatusLaunchable;

            // ② 出口接线：真点一下（宿主动作是假的）
            if (statusLaunchable)
            {
                window.ProbeInvokeStatusClick();
                PumpUntil(() => launchClicks > 0, PumpTimeoutMs);
            }

            // ③ 卷清单行：status 一律回 ready=false
            window.RefreshVolumeSummary();
            PumpUntil(() => !string.IsNullOrEmpty(window.ProbeVolumesLine), PumpTimeoutMs);

            return new JsonObject
            {
                ["availability"] = availability.ToString(),
                ["statusText"] = statusText,
                ["statusLaunchable"] = statusLaunchable,
                ["statusTextAfterClick"] = window.ProbeStatusText,
                ["launchClicks"] = launchClicks,
                ["volumesLine"] = window.ProbeVolumesLine,
                ["queries"] = transport.Queries,
                ["statuses"] = transport.Statuses,
            };
        }
        finally
        {
            window.CloseForProbe();
        }
    }

    /// <summary>
    /// corestatus 的假传输：<c>search.query</c> 一律回 <c>-32001</c>、<c>search.status</c> 一律回
    /// <c>ready=false</c>。只造这两个面 —— 本模式要验的是"窗口拿到这些之后说什么话"，
    /// 而不是协议本身（那是 25.x / W3-c 的地盘）。
    /// </summary>
    private sealed class CoreStatusTransport : ISearchIndexTransport
    {
        public int Queries { get; private set; }

        public int Statuses { get; private set; }

        public Task<JsonObject> RoundTripAsync(JsonObject request, CancellationToken ct = default)
        {
            var method = request["method"]?.GetValue<string>();
            var id = request["id"]!.DeepClone();

            if (method == "search.status")
            {
                Statuses++;
                return Task.FromResult(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new JsonObject
                    {
                        ["ready"] = false,
                        ["totalFiles"] = 0,
                        ["indexing"] = new JsonObject
                        {
                            ["active"] = true,
                            ["phase"] = "enumerate",
                            ["filesDone"] = 0,
                            ["filesTotal"] = 0,
                            ["paused"] = false,
                        },
                        ["volumes"] = new JsonArray(),
                        ["skippedVolumes"] = new JsonArray(),
                        ["failedVolumes"] = new JsonArray(),
                        ["detectedVolumes"] = 0,
                        ["ownScopes"] = new JsonArray(),
                        ["lastError"] = "",
                    },
                });
            }

            Queries++;
            return Task.FromResult(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject
                {
                    ["code"] = RpcErrorCodes.SearchNotReady,
                    ["message"] = "索引准备中（ready=false）",
                },
            });
        }
    }
}
