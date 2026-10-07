# W12 · Launcher 第二波（空查询默认视图 / 触发词发现性 / 工具动作接回）设计方案

> | | |
> |---|---|
> | **类型** | ② 阶段方案（功能波次） |
> | **状态** | 📌 **已拍板 D1~D13，可开工**（2026-10-07；决策见 §8） |
> | **波次** | W12 —— W11 收口后的第一个新功能波（与 `src/Eztools.Index/**` 零耦合，可并行） |
> | **上游** | `docs/功能候选路线图.md`（立项闸门）· `docs/未完成项与待决清单.md` 头部第 ③ 项（`actions` 悬空）与 W10-c 第 1 项（触发词发现性） |
> | **底座** | `docs/W7-Launcher-设计方案.md`（provider / 动作 / 频次库 / 探针）· `docs/W10-剪贴板搜索-设计方案.md`（触发词先例 `图片` / `cmd:`） |
> | **配套** | `docs/W12-手工验收清单.md`（真机项） |

## 0. 立项依据（先过闸门）

### 0.1 闸门对账

| 子项 | ① 一天至少用一次 | ② 复用已验证底座 | ③ 无现成免费替代品 | 结论 |
|---|---|---|---|---|
| **a 空查询默认视图** | ✅ 每次唤出搜索窗都先经过空查询态 | ✅ W7 频次库 + app 缓存 + 既有渲染链（零新控件） | ✅ 无 —— 这是本产品自身入口的体验 | 立项 |
| **b 触发词发现性** | ✅ 每次「搜不到」都命中 | ✅ 只改状态行文案与一个常量 | ✅ 无 | 立项 |
| **c 工具动作接回** | ✅ 哈希 / 字数 / 速览都是日常文件操作 | ✅ 工具与调用通路**都已实现**（§2.5、§2.6） | ✅ 无 —— 能力在库里，只是摸不到 | 立项 |

**为什么不单独立项 / 为什么合成一波**：

- c 不是新想法，是**台账已挂的欠债**：`docs/未完成项与待决清单.md` 头部第 ③ 项原文「建议立项为 W12（与 W11 无耦合，可并行）」+「**建议不恢复 `actions` 声明**，只在 Launcher 侧建路由 ⇒ 避开 Shell 上下文菜单扩展这个安全面（代码进 `explorer.exe`，违反『工具代码永不进 Core』）」。本波就是那条「Launcher 侧路由」。
- b 是台账 W10-c 第 1 项，原文「🟡 候选（未立项）… **要动渲染层，需单独立项评估**」⇒ W12 即那次评估与立项。
- 三者同属一个命题 —— **Launcher 是主入口（路线图 §F3 要点：热键稀缺，其他工具尽量从它进入），但能力已有而摸不到** —— 且改动集中在同一批文件（`SearchWindow.cs` / `TrayApplication.cs` / `LauncherUiProbe.cs`），**一次验收**比三次更省。

### 0.2 本波边界（防范围膨胀）

- **做**：a 空查询默认视图「最近使用」· b 状态行触发词与动作键提示 · c 三个孤儿 handler 的 Launcher 动作入口。
- **不做**：**B3 廉价 provider 搭车**（取色历史 / 窗口切换 / 进程结束 / 哈希·UUID·时间戳 provider）。理由：它们在形态上是**新 provider**，要过闸门三条 + 定段位归属（`SegmentOf` 把未知 id 归 `Pin`）+ 排序与加成 + 独立断言面，与「把已有能力接出来」不是一类。W12 收口后另行立项。
- **不做**：新增配置键（D4）· 恢复 `tool.json` 的 `actions` 声明（D11）· 改协议字段与贡献点 · 碰 `src/Eztools.Index/**`（W11 刚收口）与 `src/Eztools.Core/**`。
- **不做**：默认视图的「常用（按次数）」第二视图与聚合混排（D2）。

## 1. 目标

一句话：**让 Launcher 在「什么都没打」和「打错了」这两种状态下都不再是空白**，并把它已经拥有却摸不到的三件文件操作接出来。

三条用户可见结果（＝本波的验收口径）：

1. 唤出搜索窗，**不必先打字**就能看到「最近使用」的应用，Enter 直接打开。
2. `图片` / `>` 这类**触发词**从「内部知识」变成状态行上的可见提示。
3. 选中一个文件行按 **Tab**，能看到并执行「计算哈希并复制」「统计字数」「速览」。

## 2. 事实核查

> 本节每条都决定方案形态，逐条带证据（文件:行）。**先查实、再设计** —— 这是 W11 v1 的教训。

### 2.1 空查询现状 = 「零 provider 往返」，这是**被断言钉住的不变量**，不是缺陷

- `src/Eztools.Host/Launcher/QueryRouter.cs:104-130` `OnEmptyTicket`：空文本 ⇒ **零 provider 调用** + 清段位，`:114` 也推进 `_generation`（让在途批次失效），`:119-129` 直接 `Publish` 一个 `Items` 为空、`IsEmptyQuery: true`、`AnyAccepted/FilesAccepted` 均为 `true` 的 `LauncherRenderModel`。
- 消费方 `src/Eztools.Desktop/SearchWindow.cs:1423-1435`：`IsEmptyQuery` ⇒ `ClearAndFill(model)` + 状态行 = `EmptyQueryHint`（`:856`）。
- 已有断言：`src/Eztools.Cli/SelfTestCommand.cs:2583` / `:2596` —— **25.4 空查询零往返：本地回调空模型，不进管道（Execute 零调用）**；`SelfTestCommand.cs:3032` / `:3039` —— **W10-1 clip 空查询 ⇒ 空结果且零触达来源（不倾倒历史）**。
- 另有 `src/Eztools.Host/Launcher/QueryPump.cs`：`Kick` 里 `text.Length == 0` ⇒ 走 `OnEmptyText` 并 `return`（不进管道、不占单在途位）；`Requery()` 在 `_latestText.Length == 0` 时**什么都不做**。

⇒ **结论（决定 D3）**：默认视图**不能**通过「让某个 provider 处理空串」实现（会同时破 25.4 与 W10-1）；必须在**窗口侧本地合成**，走既有 `ClearAndFill`。且**就绪补发也必须另行处理** —— 空查询下 `Requery()` 是 no-op（§2.7）。

### 2.2 默认视图的数据源只有一份：`usage.json`，且目前只有 apps 段在写

- `src/Eztools.Host/Launcher/LauncherUsageStore.cs`：路径 `<ConfigRoot>/launcher/usage.json`，键 = `providerId|identity`（`:87` `Key`），值 = `{count, lastUsed}`。
- `src/Eztools.Desktop/TrayApplication.cs:1293-1301`：`UsageRecorder` **只记 App 段**（`_usage.Record(LauncherProviderRegistry.Apps, action.Argument)`）。这是 W7-e 的收窄（FR-8：files 排序归索引进程；calc/unit/encode 每轮至多一行，加成是 no-op）⇒ 设计原则「只记录有消费者的段」。
- **`LauncherUsageStore` 没有任何枚举 / Top-N / 最近条目 API**（只有 `EntryCount` / `CountOf` / `BoostFor`）⇒ 本波必须新增一个查询面（`Recent`，§6.1），且**不得破**「只记录有消费者的段」—— 我们**只读**，不新增任何 `Record` 调用点。

### 2.3 `usage.json` 的 `lastUsed` 是本地时区字符串；`BoostFor` 有一处未防护的 `DateTime.Parse`（**真缺陷**）

- `LauncherUsageStore.cs:185`：`LastUsed = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")`（本地时区）；`UsageEntry.LastUsed` 声明为 `string`（`:345-346`）。
- `LauncherUsageStore.cs:210`：`var days = (DateTime.Now - DateTime.Parse(entry.LastUsed)).TotalDays;` —— **未做防护**。手工改坏 / 版本变更导致格式不符的 `lastUsed` ⇒ `DateTime.Parse` 抛 ⇒ 该异常只能靠 `QueryRouter.SafeQueryAsync`（I5）兜住 ⇒ 结果是**整个 apps 段置空 + 状态行显示「应用索引不可用」**，而真实原因只是时间戳格式不对。
- 这与该类自己的契约相矛盾：类注 `:21-22` 「任何 IO 异常 ⇒ 记到 `LastError`，**绝不抛**」。

⇒ **结论（见 D14 追加项）**：本波顺手加固（`DateTime.TryParse` 失败 ⇒ 0 加成且不抛、不置 `LastError` —— 数据格式问题不是 IO 问题），并加断言；新增的 `Recent` 一律用 `TryParse`，**坏数据不参与视图排序**（不显示成「最近使用」以免说谎）。

### 2.4 Tab 键在**消息层**可拦，且不依赖 IME

- `src/Eztools.Desktop/NativeInputBox.cs:234-258`：`private sealed class Edit : WinForms.TextBox` 重写 `WndProc`，对 `WM_KEYDOWN` / `WM_SYSKEYDOWN`（`:72-73` 常量 0x0100 / 0x0104）先 `RawKeySeen?.Invoke(vk)`，再 `if (KeyInterceptor?.Invoke((WinForms.Keys)vk) == true) return;`（吞掉，不调 `base`）。
- 同文件 `:183` `OnCommandKey(WinForms.Keys keyCode)`：按 `WinForms.Control.ModifierKeys` 分派到 `InputCommand` 枚举（`:35-60`：`Enter` / `CtrlEnter` / `Escape` / `Up` / `Down` / `Delete` / `CtrlP` / `CtrlC`），末尾 `return Command?.Invoke(command) == true;`（返回 true = 已处理吞键）。
- `_edit.TabStop = true`（`:94`），而结果列表**不可交互** ⇒ Tab 在原生 EDIT 里的既有作用是「把焦点挪出输入框」，实际无收益。

⇒ **结论（决定 D12）**：加一支 `WinForms.Keys.Tab => InputCommand.ActionMenu` 即可；探针经 `SearchWindow.ProbeCommand(InputCmd)`（`:1609`，直接转 `OnInputCommand`）走同一分派，不需要 Win32 注入。

### 2.5 三个孤儿 handler 的**实况修正**：只有一个是真零入口

| handler | 声明处 | 托盘菜单 | 热键 | Launcher 入口 | 结论 |
|---|---|---|---|---|---|
| `filehash.hashMany` | `tools/filehash/tool.json`（`contributes.commands`）+ `tools/filehash/main.py:116` | ✅ `menus`（tray / group file / input clipboard） | — | ❌ | **零 Launcher 入口** |
| `wordcount.countFile` | `tools/wordcount/tool.json` `:17` + `tools/wordcount/main.py:81` | ❌ | ❌ | ❌ | **真·零 UI 入口**（台账 `:118` 原文「清空 action 后暂无 UI 入口（仍可 `ezt invoke`）」） |
| `preview.show` | `tools/preview/tool.json`（`panels.main` 的 `commands`）+ `tools/preview/main.py:581` | ✅ | ✅ `Ctrl+Alt+P`（input shellSelection，opensPanel main） | ❌ | **零 Launcher 入口** |

★ 更正：台账 W12 候选行里「`filehash.hashMany` / `wordcount.countFile` / `preview.show` 三个已实现的 handler 至今**零 Launcher 入口**」成立；但**不能说成零入口**（前两个各有托盘菜单，`preview.show` 还有热键）。本波的价值正确定位为：**从 Launcher 走到它们的入口**（统一入口，路线图 §F3 的热键策略），而不是「从无到有」。

引用侧证据（`src/**` 全仓 grep）：`hashMany` **0 处**、`countFile` **0 处**、`preview.show` 仅 `src/Eztools.Desktop/TrayApplication.cs:4125` 一句注释 ⇒ Launcher 一侧**确实没有接线**。

### 2.6 工具侧参数契约：**W12 的调用零改动**

- `tools/filehash/main.py:86-107` `_collect_paths(args)` 依次取 `args.paths` / `args.files`（list 非空）→ `args.path` / `args.file`（单值）→ `args.input`（字符串按行拆）⇒ 我们直接给 `{"paths": [<命中文件的绝对路径>]}` 即可，**工具零改动**（只加 `hint`）。
- `tools/wordcount/main.py:81` `count_file(args)`：取 `args.path` → 否则 `paths`/`files` 第一项 → 否则报「缺少参数 path」；`_read_text`（`:50`）**自带尺寸闸**（`DEFAULT_MAX_FILE_MB = 64`，`:59` 超限报「文件过大（… MB > … MB）」）⇒ 从 Launcher 对任意文件挂「统计字数」**没有炸库风险**（工具已自限，失败也有可读文案）。
- `tools/preview/main.py:569-578` `_selected_paths(args)`：**权威来源是 `args.inputPaths`（list）**，回退 `args.input`（单字符串）；给不到时返回 `hint`「未拿到选中项：请在资源管理器中选中文件后，再按 Ctrl+Alt+P（或托盘 → 速览选中的文件）」。⇒ **从 Launcher 进 preview 必须发 `inputPaths`，并且由我们自己打开面板**（工具不会替我们开面板）。
- ⚠️ 不要调 `preview.reload`（`:601` 无副作用，面板按钮只传 `{panelId}`、不带 shellSelection；用它当「刷新选中项」会把上次记录**清空**）。

### 2.7 就绪补发在空查询下断链（本波必须补）

`src/Eztools.Desktop/SearchWindow.cs:1577-1599` `OnProviderBecameReady()`：先判 `_disposed` / `Visibility != Visible` / `_input.Text != _lastRenderedQuery`，然后 `RequeryCount++; _router.Requery();`。
而 `QueryPump.Requery()` 对空文本**什么都不做**（§2.1）⇒ **apps 首扫就绪时，空查询态不会自动补上「最近使用」列表**。这正是 `RunUsage` 探针夹具必须靠 `ProbeStartRenderLog()`（渲染日志）而非「行数 > 0」当就绪信号的同一个坑。

### 2.8 结果摘要（`hint`）已是全项目既有约定

`src/Eztools.Desktop/TrayApplication.cs:4132` `DescribeResult(JsonNode?)`：`null` ⇒ 「（无返回值）」；有 `hint` 字符串且非空白 ⇒ **直接显示 `hint`**；否则回落 `JsonText.Write(result, indented: false)`（配 `Shorten`，`:4152`）。工具侧惯例（`tools/preview`）就是「**想让人看什么就放进 `hint`**」。
⇒ 本波两处新 `hint`（filehash / wordcount）沿用同一约定，不发明第二套展示机制；同时把 `hint` 判定抽成单点（D11），供 Launcher 侧复用。

## 3. W12-a · 空查询默认视图「最近使用」

### 3.1 形态

唤出搜索窗（输入框为空）时，结果列表里直接列出**最近使用过的应用**（上限 8 条）。行形态与 apps 搜索命中**完全一致** —— 它本来就是同一类 `LauncherItem`（`Kind = LauncherKind.App`），走同一套 `LauncherRowTemplateSelector` / `LauncherRowText` / 徽标 `▸`，主行=应用名、副行=目标路径。因此 **Enter 打开 / Ctrl+Enter 定位 / Ctrl+C 复制路径 / Tab 看动作 这些既有按键链一行都不用改**。

### 3.2 数据链（三步，全部在窗口侧）

1. **取最近条目**：`LauncherUsageStore.Recent(LauncherProviderRegistry.Apps, 8)`（新增 API，§6.1）⇒ 若干 `(Identity, Count, LastUsed)`，按 `lastUsed` 倒序。
2. **解析成应用**：`AppsProvider.SnapshotState()`（新增 API，§6.2）拿到 `Items` 快照，用 `TargetPath` 全等（`OrdinalIgnoreCase`）把 identity 映射回 `AppEntry`。**解析不到 ⇒ 跳过**（应用已卸载 / 快捷方式被删，属正常老化，不算错误、不报错、不计入 N）。
3. **合成行**：`LauncherDefaultView.Build(...)` 产出 `IReadOnlyList<LauncherItem>`，每条 `Score = 0`（视图**不参与排序**，按 usage 的最近序原样排）、`PrimaryAction = Open(TargetPath)`、`SecondaryAction = RevealApp(TargetPath)`，与 `AppsProvider.ToItem` 同形。

**副行沿用 `TargetPath`、不显示使用次数**：视图的语义是「最近用过」而不是「用得最多」；显示次数会诱导用户把它读成排行榜（那是 D2 明确不做的第二视图）。

### 3.3 渲染与状态行

`src/Eztools.Desktop/SearchWindow.cs` 的 `RenderResults` 空态分支（`:1423-1435`）改为调用一个新的私有方法 `RenderEmptyQuery(long generation)`；同时把 `ClearAndFill` 的填充逻辑抽成 `FillItems(IReadOnlyList<LauncherItem> items, string queryText, long generation)`，让 `ClearAndFill(LauncherRenderModel)` 与 `RenderEmptyQuery` **共用同一实现**（不许出现第二份"填列表 + 记 `_lastRenderedQuery` + 记渲染日志"的复制品）：

```
private void RenderEmptyQuery(long generation)
{
    var view = BuildDefaultView();                       // 本地合成（含 apps 错误态）
    FillItems(view.Items, queryText: "", generation);    // ★ queryText 必须与路由器的空模型一致（空串）
    if (ApplyStaleNotice())                              // 陈旧态优先级最高（既有行为，原样保留）
    {
        _statusRight.Text = "";
        return;
    }

    SetStatusLaunchable(false);
    _status.Text = EmptyQueryHint;
    _statusRight.Text = view.Items.Count > 0
        ? $"最近使用 {view.Items.Count}"
        : view.Error ?? EmptyQueryEmptyRight;
}
```

- `RenderResults`：`if (model.IsEmptyQuery) { RenderEmptyQuery(model.Generation); return; }`
- `queryText: ""` **不是可选的**：`ClearAndFill` 会把它记进 `_lastRenderedQuery`，而 `OnProviderBecameReady` 的守卫是 `_input.Text == _lastRenderedQuery`（§2.7）⇒ 写错值会让就绪补发永久失效。
- 状态行右侧三选一（**互斥且有优先级**）：`最近使用 N` > `view.Error` > `EmptyQueryEmptyRight`。

### 3.4 降级：视图为空时必须**回到今天的样子**

四种情况都落到「视图空」，不许各自发明文案：

| 情况 | 结果 |
|---|---|
| `launcher.usage=false`（频次库整体关闭） | `Recent` 返回空 ⇒ 视图空 |
| `usage.json` 还没加载完（`EnsureLoaded` 是 fire-and-forget，首帧可能空） | 视图空，**随后由 §3.5 补发**上屏 |
| apps 首扫未就绪（`AppIndexCache.Ready == false`） | 视图空，**随后由 §3.5 补发**上屏 |
| 所有 identity 都解析不到（应用全卸载了） | 视图空 |

视图空 ⇒ 左位仍是 `EmptyQueryHint`、右位写触发词提示（§4）—— 即**与今天完全一致的空态**。**不发明**「暂无最近使用」这类新占位文案（空就是空）。

### 3.5 就绪补发（补 §2.7 的断链）

`OnProviderBecameReady()`（`SearchWindow.cs:1577-1599`）在既有 `_router.Requery()` **之前**补一支：

```
if (_input.Text.Length == 0)
{
    RenderEmptyQuery(_router.Generation);   // 空查询下面 Requery() 是 no-op，必须本地重渲
    return;
}
```

两条既有前置守卫（窗口可见 + `_input.Text == _lastRenderedQuery`）保持不动。

### 3.6 容量

`LauncherDefaultView.Limit = 8`（**常量，零新配置键**）。8 的来源：结果区可视高度约 8~9 行；且「最近使用」的价值集中在前几条，再往后没人看。要关掉默认视图，就把 `launcher.usage` 关掉（一份配置，两个出口：不记频次 + 不显示默认视图 —— 语义自洽）。

### 3.7 已知取舍

默认视图的行与被 apps provider **搜出来**的行长得一模一样（同徽标、同副行）。⇒ 残余风险 R1：用户可能以为「搜出了东西」。缓解只有状态行右位的 `最近使用 N`。**不**在行上加「最近」字样 —— 那要改行渲染契约，破 `LauncherRowText` 的 FR-10 逐字节一致纪律。**也**不做「视图行无高亮」之外的特殊处理（本来就没有 `Highlights`）。

## 4. W12-b · 触发词发现性

### 4.1 两串文案（单点，`SearchWindow.cs:856` 附近）

| 常量 | 值 |
|---|---|
| `EmptyQueryHint`（**改**） | `输入以搜索 · Enter 打开 · Ctrl+Enter 定位 · Ctrl+C 复制 · Tab 更多动作` |
| `EmptyQueryEmptyRight`（**新**） | `「图片」看最近剪贴板图片 · 「>」看系统命令` |

### 4.2 放置逻辑（就是 §3.3 的同一个分支，不新增分支）

| 场景 | 左 `_status` | 右 `_statusRight` |
|---|---|---|
| 空查询 + 有最近使用 | `EmptyQueryHint` | `最近使用 N` |
| 空查询 + 无最近使用 | `EmptyQueryHint` | `EmptyQueryEmptyRight` |
| 空查询 + apps 读不到 | `EmptyQueryHint` | `SnapshotState().Error` |
| 空查询 + 陈旧态（Core 没跑等） | 陈旧态文案（既有 `CoreStaleNotice`） | 空（既有行为） |

理由：**左位讲「怎么用这个框」（恒定），右位讲「现在有什么 / 还能打什么」（随状态变）**。触发词提示出现在右位空闲时，正是用户「什么都没看到、最需要指路」的时刻；而有最近使用列表时，用户此刻更需要知道「这是什么列表」（优先级更高）。

### 4.3 为什么是这两条触发词

- `图片`（`src/Eztools.Host/Launcher/ClipProvider.cs` `ImageTriggers = ["图片", "img"]`，精确匹配）：W10-c 的图片入口。
- `>`（`src/Eztools.Host/Launcher/CommandCatalog.cs` `Prefix`）：系统命令前缀。
- **不提示 `cmd:`**：它是 `>` 的 ASCII 等价（`TryGetBody` 同时认两者），提示一个就够；选输入成本更低的 `>`。
- 文案用「」包触发词（本项目 RPC/文档既有风格），**不用反引号** —— 状态行是 WPF `TextBlock`，反引号会原样显示成字符，像噪声。

## 5. W12-c · 工具动作接回

### 5.1 交互：Tab = 动作 drill-down（D7）

```
[输入框]  结果列表（文件/应用/…）
   选中某行 → Tab → 动作列表（标题=动作名，副行=副作用说明）
                       Enter = 执行选中动作
                       Esc / Tab = 回到结果列表
                       继续打字 = 自动回到结果列表并重新搜索
```

**动作列表复用同一个 `_results` ListBox 与同一套行控件**，不新建窗、不新建控件：

- 新增 `LauncherKind.Action`（徽标 `⚙`）与 `LauncherRowText.BadgeOf` 的一个分支 ⇒ 动作行就是普通的非文件行。
- **动作列表就是一列 `LauncherItem`**，每条把要执行的动作放在 `PrimaryAction` 上 ⇒ **Enter 链一行都不改**（`OpenFirstOrSelected` → `ExecuteAction(item.PrimaryAction)` 原样走）。
- 动作行 `Score` 按定义序递减、不参与任何排序（我们自己填 `_results`，不进 `QueryRouter.Merge`），因此**动作顺序 = 我们给的顺序**。

**状态机**（`SearchWindow` 新增两个字段 `_actionRows` / `_actionSource`）：

| 事件 | 行为 |
|---|---|
| `InputCommand.ActionMenu`，且不在动作模式，且当前选中项非空 | 取 `LauncherActionMenu.For(item)` ⇒ 填 `_actionRows` ⇒ `FillItems(actionRows, _input.Text, gen)` ⇒ handled=`true` |
| `InputCommand.ActionMenu`，已在动作模式 | 退出动作模式（恢复原结果列表）⇒ handled=`true` |
| `InputCommand.Escape`，已在动作模式 | 退出动作模式（**不隐藏窗口**）⇒ handled=`true` |
| 输入框文字变化 | 退出动作模式（在既有输入变更处理里先清 `_actionRows`） |
| `RenderResults` 被调用且 `_actionRows != null` | **早退**（防在途批次把动作列表覆盖掉） |

### 5.2 动作列表的生成（纯函数，D8）

`（新建）src/Eztools.Desktop/Launcher/LauncherActionMenu.cs`：

```
internal static IReadOnlyList<LauncherItem> For(LauncherItem item)
```

| 行类型 | 动作（**按定义序**，不排序、不评分） |
|---|---|
| 任何行 | `PrimaryAction`（如 打开）、`SecondaryAction`（如 定位）—— 非空者、去重 |
| `File` 行（`FileHit != null`） | 追加：**计算哈希并复制**（`filehash.hashMany`，`CopyResult=true`）· **统计字数**（`wordcount.countFile`）· **速览**（`preview.show`，执行后开面板并收窗） |

- 每条的 `Subtitle` = 副作用说明（如「复制到剪贴板，不打开文件」「在速览面板里查看」），让用户在按 Enter 前知道会发生什么。
- ⚠️ **落地核对项**：`File` 行的 `PrimaryAction`/`SecondaryAction` 由 `FilesProvider` 合成，落地时须先确认 `File` 行两者都非空（否则「打开/定位」在动作菜单里会缺条，而 Ctrl+Enter 的回落链 `action = item.SecondaryAction ?? item.PrimaryAction` 会掩盖这个缺口）。

### 5.3 动作模型（契约扩展，D9）

- `LauncherActionKind` **新增 `ToolCommand`**；`LauncherAction` **新增可选 `bool CopyResult = false`**（既有构造点不受影响）。
- `Argument` = 命令 id（如 `filehash.hashMany`）；`Arguments` = JSON 参数文本（既有字段，本波明确它可承载 JSON 载荷）。
- `CopyResult = true` ⇒ 宿主把**摘要原文**（§5.5）写进剪贴板。命名已承诺副作用：动作名就叫「**计算哈希并复制**」，不叫「计算哈希」。

### 5.4 执行（宿主路由，D10）

`SearchWindow.ExecuteAction` 新增 `ToolCommand` 分支（照既有 `OcrCopy` / `PasteBack` 分支的形状），要点：

```
case LauncherActionKind.ToolCommand:
    if (_toolBusy) return;                      // 并发闸（hash 大文件可跑数十秒）
    _ = RunToolActionAsync(action, item);       // 不阻塞 UI 线程（必须 await，不许 .Result / Wait()）
    return;
```

```
private async Task RunToolActionAsync(LauncherAction action, LauncherItem? item)
{
    _toolBusy = true;
    SetStatusLaunchable(false);
    _status.Text = $"正在执行：{item?.Title ?? action.Argument}…";
    try
    {
        var outcome = ToolActionRequested is null
            ? new LauncherToolOutcome("该动作需要宿主支持（宿主未注入执行器）", CloseWindow: false)
            : await ToolActionRequested(action);
        _status.Text = outcome.Message;
        if (outcome.CloseWindow)
        {
            if (item is not null) UsageRecorder?.Invoke(item, action);   // 复用既有频次记录口径（异常只改文案）
            Hide();
        }
    }
    catch (Exception ex)
    {
        _status.Text = $"执行失败：{ex.Message}";                          // 禁静默（审查规范 §3.3）
    }
    finally
    {
        _toolBusy = false;
    }
}
```

**失败面实况**（决定文案怎么写）：`ToolCallResult` 只有 `Result` / `Raw` / `Elapsed`（`src/Eztools.Host/Processes/ToolProcess.cs:36-44`），**没有 Success/Error 字段** —— 失败一律以**异常**形式出现：

| 异常 | 何时 | 文案里已有什么 |
|---|---|---|
| `ToolRpcException`（`ToolProcess.cs:388`） | **工具自己抛异常**（如 wordcount 的「文件过大（… MB > … MB）」「文件不存在」） | `"{工具 id} 的 {方法} 返回错误: {工具的原话}"` ⇒ **工具的原话已经在 `ex.Message` 里**，宿主无需二次拼装 |
| `ToolTimeoutException` `:377` | 工具超时（hash 大文件可能命中，取决于 `Weight.DefaultTimeout()`） | 超时文案 |
| `ToolCrashedException` `:326` | 工具进程崩溃 | 崩溃文案 |
| `ToolProtocolException` `ToolHostManager.cs:184` | 命令 id 不存在（例如用户手工改过配置） | 「未找到命令 '…'」 |
| `ToolDisabledException` `ToolHostManager.cs:366` | 工具被禁用 | 禁用文案 |

⇒ 窗口侧规则只有一条：**成功 ⇒ 显示摘要 + 按 `CloseWindow` 决定是否收窗；任何异常 ⇒ 状态行 `执行失败：{ex.Message}` + 不收窗**（用户必须看到失败原因）。

托盘侧实现（注入 `ToolActionRequested`，`TrayApplication.cs` 一处装配）：

1. `var result = await _host!.Processes.InvokeCommandAsync(action.Argument, args, ct: CancellationToken.None);`（`args` 来自 `action.Arguments` 的 JSON 解析）。
2. 摘要 = `ToolResultText.Hint(result.Result)`（§5.5 单点）。
3. `CopyResult` ⇒ `Clipboard.SetText(摘要)`（**摘要为空则跳过并说明**，不许把空串写进剪贴板 —— 那会清掉用户剪贴板）。
4. `preview.show` ⇒ 追加 `OpenPanelById("preview", "main")`（面板 id 取自 `tools/preview/tool.json`）。**只调 `show`，绝不调 `reload`**（§2.6 的坑）。
5. 返回 `new LauncherToolOutcome(文案, CloseWindow: true)`；异常 ⇒ 文案 = `$"执行失败：{ex.Message}"`、`CloseWindow: false`。

**参数**：`filehash.hashMany` / `wordcount.countFile` 发 `{"paths": ["<绝对路径>"]}`；`preview.show` 发 `{"inputPaths": ["<绝对路径>"]}`（§2.6）。

### 5.5 摘要单点（D11）

新增 `（新建）src/Eztools.Host/Launcher/ToolResultText.cs`（纯函数，零 IO）：

```
public static string? Hint(JsonNode? node)
public static string Text(JsonNode? node)        // Hint 优先，回落 JsonText.Write(indented:false)
```

- `TrayApplication.DescribeResult`（`:4132`）改为 **`ToolResultText.Text` + `Shorten`** ⇒ 全项目只剩一份 `hint` 判定（既有 `LastBalloonBody` 探针断言仍守「宿主真的调了 DescribeResult」）。
- 工具侧最小改动（2 个文件、每个几行）：`tools/filehash/main.py` 的 `hashMany` 返回体加 `hint`（形如 `sha256: <hex>`；单路径时同时是 `CopyResult` 复制的内容）· `tools/wordcount/main.py` 的 `countFile` 返回体加 `hint`（形如 `字符 N · 词 N · 行 N`）。
- `preview.show` **已有 `hint`，零改动**。
- **不恢复 `tool.json` 的 `actions` 声明**（台账决策）。

## 6. 接口定义（落地照此形状写签名）

### 6.1 `LauncherUsageStore`（`src/Eztools.Host/Launcher/LauncherUsageStore.cs`）

```csharp
/// <summary>按最近使用时间倒序返回某 provider 的前 limit 条。坏 lastUsed 不参与（不显示成「最近使用」）。</summary>
public IReadOnlyList<(string Identity, long Count, string LastUsed)> Recent(string providerId, int limit)
```

- **过滤**：只取键以 `providerId + "|"` 开头者（`StringComparison.Ordinal`）；identity = 键剥掉前缀后的剩余部分（**不能 `Split('|')`** —— 路径里可能有竖线）。
- **排序**：`DateTime.TryParse(lastUsed)` 成功者按时间**倒序**；失败者**排除**；同刻并列按 identity `Ordinal` 升序 ⇒ **确定性**（探针可断言）。
- **边界**：`!limit <= 0` 或 `!_enabled` ⇒ 空表（照 `BoostFor` 的早退形状）。
- **只读**：不改 `Record`，不新增任何写路径（守 §2.2 的「只记录有消费者的段」）。
- **加固**（同文件顺手）：`BoostFor` 的 `DateTime.Parse`（`:210`）改 `TryParse`，失败 ⇒ `return 0`（**不抛、不置 `LastError`** —— 数据格式问题不是 IO 问题，类注 `:21-22` 承诺「绝不抛」）。

### 6.2 `AppsProvider`（`src/Eztools.Desktop/Launcher/AppsProvider.cs`）

```csharp
/// <summary>应用快照（默认视图的数据面）：条目 + 就绪态 + 读不到的原因。</summary>
internal (IReadOnlyList<AppEntry> Items, bool Ready, string? Error) SnapshotState()
```

- 只是把私有的 `_cache.Items` / `Ready` / `LastError` 三件一起交出去，**不改缓存行为**。
- ★ 判定口径（写进 XML 注释）：**「有没有内容」看 `Items`，「为什么是空的」看 `Ready` / `Error`**。重扫期间缓存**保留旧快照**（`AppIndexCache` 既有语义）⇒ 旧快照照常显示，不算降级。
- `Error` ⇒ 默认视图右位直接显示（§3.3 的 `view.Error`），守 W7 的「读不到必须可见」纪律（`AppIndexCache` 注释原文：「不能用 `Ready=false` 表达坏了，那会被当静默缺失」）。

### 6.3 `（新建）src/Eztools.Desktop/Launcher/LauncherDefaultView.cs` —— `LauncherDefaultView`

```csharp
internal readonly record struct DefaultView(IReadOnlyList<LauncherItem> Items, string? Error);

internal static class LauncherDefaultView
{
    internal const int Limit = 8;

    /// <summary>纯函数：不读盘、不碰 Dispatcher —— selftest 与探针可直接穷举。</summary>
    internal static DefaultView Build(
        LauncherUsageStore? usage,
        (IReadOnlyList<AppEntry> Items, bool Ready, string? Error) apps);
}
```

### 6.4 `（新建）src/Eztools.Desktop/Launcher/LauncherActionMenu.cs` —— `LauncherActionMenu`

```csharp
internal static class LauncherActionMenu
{
    /// <summary>某一行「可以做哪些事」。纯函数：不进进程、不读盘、不问缓存。</summary>
    internal static IReadOnlyList<LauncherItem> For(LauncherItem item);
}
```

动作条目的字段：`Kind = LauncherKind.Action` · `Title = 动作名` · `Subtitle = 副作用说明` · `PrimaryAction = <要执行的动作>` · `FileHit = null` · `Highlights = []` · `Score` = 递减占位（列表顺序才是语义）。

### 6.5 契约扩展（`src/Eztools.Host/Launcher/LauncherContracts.cs`）

```csharp
public enum LauncherKind { File, App, Calc, Unit, Encode, Clip, Command, Action }                   // 尾部追加
public enum LauncherActionKind { Open, Reveal, Launch, RevealApp, CopyText, PasteBack, OcrCopy, ToolCommand }  // 尾部追加
public sealed record LauncherAction(LauncherActionKind Kind, string Argument, string? Arguments = null, bool CopyResult = false);
```

⚠️ 两个枚举**只许尾部追加**（既有取值不动）；`CopyResult` 是**带默认值的可选参数** ⇒ 既有构造点零改动。

### 6.6 `NativeInputBox`（`src/Eztools.Desktop/NativeInputBox.cs`）

```csharp
public enum InputCommand { Enter, CtrlEnter, Escape, Up, Down, Delete, CtrlP, CtrlC, ActionMenu }
// OnCommandKey 追加一支：
WinForms.Keys.Tab => InputCommand.ActionMenu,
```

**不看修饰键**（给将来 `Shift+Tab`「上一个动作/反向」留路，本波不实现）。

### 6.7 `SearchWindow`（`src/Eztools.Desktop/SearchWindow.cs`）

```csharp
internal Action<LauncherItem, LauncherAction>? UsageRecorder { get; set; }              // 既有（:1398）
internal Func<LauncherAction, Task<LauncherToolOutcome>>? ToolActionRequested { get; set; }  // 新增（托盘注入）

internal sealed record LauncherToolOutcome(string Message, bool CloseWindow);          // 新增（照 LauncherRowSnapshot 的先例，留在本文件）

private void FillItems(IReadOnlyList<LauncherItem> items, string queryText, long generation);  // 从 ClearAndFill 抽出
private void RenderEmptyQuery(long generation);                                                // 新的空态唯一实现
private DefaultView BuildDefaultView();                                                        // RenderEmptyQuery 用
private async Task RunToolActionAsync(LauncherAction action, LauncherItem? item);               // ToolCommand 执行链

private IReadOnlyList<LauncherItem>? _actionRows;   // 非空 ⇒ 正处于动作模式
private LauncherItem? _actionSource;                // 动作列表是从哪一行展开的（退出时回到它）
private bool _toolBusy;                             // 工具动作并发闸

private const string EmptyQueryHint = "输入以搜索 · Enter 打开 · Ctrl+Enter 定位 · Ctrl+C 复制 · Tab 更多动作";
private const string EmptyQueryEmptyRight = "「图片」看最近剪贴板图片 · 「>」看系统命令";
```

### 6.8 `（新建）src/Eztools.Host/Launcher/ToolResultText.cs` —— `ToolResultText`

```csharp
namespace Eztools.Host.Launcher;

public static class ToolResultText
{
    /// <summary>工具想让人看的那一行：result.hint 是字符串且非空白 ⇒ 它；否则 null。</summary>
    public static string? Hint(JsonNode? node);

    /// <summary>Hint 优先，回落 JSON 单行；null ⇒「（无返回值）」（与既有气泡文案逐字一致）。</summary>
    public static string Text(JsonNode? node);
}
```

## 7. 改动面清单

### 新建 5 件（代码 3 + 文档 2）

| # | 文件 | 内容 |
|---|---|---|
| 1 | `docs/W12-Launcher第二波-设计方案.md` | 本文 |
| 2 | `docs/W12-手工验收清单.md` | 真机验收项（§10.3） |
| 3 | `（新建）src/Eztools.Desktop/Launcher/LauncherDefaultView.cs` | 默认视图纯函数（§6.3） |
| 4 | `（新建）src/Eztools.Desktop/Launcher/LauncherActionMenu.cs` | 动作菜单纯函数（§6.4） |
| 5 | `（新建）src/Eztools.Host/Launcher/ToolResultText.cs` | 摘要单点（§6.8） |

### 修改 13 处

| # | 文件 | 改什么 |
|---|---|---|
| 1 | `src/Eztools.Host/Launcher/LauncherUsageStore.cs` | `Recent`（§6.1）+ `BoostFor` 加固（D14） |
| 2 | `src/Eztools.Host/Launcher/LauncherContracts.cs` | `LauncherKind.Action` / `LauncherActionKind.ToolCommand` / `LauncherAction.CopyResult`（§6.5） |
| 3 | `src/Eztools.Desktop/Launcher/AppsProvider.cs` | `SnapshotState()`（§6.2） |
| 4 | `src/Eztools.Desktop/NativeInputBox.cs` | `InputCommand.ActionMenu` + Tab 分派（§6.6） |
| 5 | `src/Eztools.Desktop/SearchWindow.cs` | 空态默认视图（`FillItems` / `RenderEmptyQuery` / `BuildDefaultView`）· 动作模式状态机 · `ToolActionRequested` · 两串文案常量 · `ExecuteAction` 的 `ToolCommand` 分支 · `OnProviderBecameReady` 补发（§3 / §5） |
| 6 | `src/Eztools.Desktop/Launcher/LauncherRowText.cs` | `BadgeOf` 加 `LauncherKind.Action => "⚙"` |
| 7 | `src/Eztools.Desktop/Launcher/LauncherActionRunner.cs` | `Execute` 加 `ToolCommand => "工具动作需要宿主执行链（调用方未接）"`（照既有 `OcrCopy` 分支的形状，**禁止**让它落到 `default` 谎报「暂不支持的动作」） |
| 8 | `src/Eztools.Desktop/Launcher/LauncherUiProbe.cs` | 新探针模式（默认视图 / 动作菜单 / 动作模式 / 工具动作真链路） |
| 9 | `src/Eztools.Desktop/TrayApplication.cs` | 注入 `ToolActionRequested`（§5.4）+ `DescribeResult` 改调 `ToolResultText`（§5.5） |
| 10 | `src/Eztools.Cli/SelfTestCommand.cs` | 25.x 新用例（§11） |
| 11 | `scripts/verify-desktop.py` | 新断言组（§11） |
| 12 | `tools/filehash/main.py` | `hashMany` 返回体加 `hint`（几行） |
| 13 | `tools/wordcount/main.py` | `countFile` 返回体加 `hint`（几行） |

### 零改动 5 处（**明写，防顺手扩大**）

| 范围 | 为什么零改动 |
|---|---|
| `src/Eztools.Index/**` | 本波不碰索引（W11 刚收口） |
| `src/Eztools.Core/**` | 不提权、不加原语 |
| 协议方法 / 贡献点 / `docs/*协议*.md` | 零新 RPC、零新 `tool.json` 字段（不恢复 `actions`） |
| `HostSettingsSchema` 与 `launcher.*` 配置 | **零新配置键**（D4：容量是常量；开关复用 `launcher.usage`） |
| `tools/preview/**` 与 `QueryRouter` / `QueryPump` / `ILauncherProvider` | preview 的 `hint` 已有；默认视图**不进** provider 体系（D3） |

## 8. 决策（已拍板，D1~D14）

| # | 决策 | 内容 | 理由 |
|---|---|---|---|
| D1 | 范围 | W12 = a 空查询默认视图 + b 触发词发现性 + c 工具动作接回（`filehash.hashMany` / `wordcount.countFile` / `preview.show`）。**B3 廉价 provider 搭车不进 W12** | 每个新 provider 都要过闸门 + 定段位/排序/断言，与「补发现性、补入口」不同类；混在一起会让本波的验收面说不清 |
| D2 | 默认视图数据源 | **只用** `usage.json` 的「最近使用」（`lastUsed` 倒序，上限 8）；不做「常用」第二视图、不做混排；`identity → AppEntry` 用 `TargetPath` 全等（`OrdinalIgnoreCase`）；解析不到 ⇒ 跳过；副行沿用 `TargetPath`、**不显示次数** | 视图语义是「最近用过」而非「用得最多」；显示次数会诱导用户读成排行榜 |
| D3 | 默认视图归属 | **不进 provider 体系**：不新增 `ILauncherProvider`、不进 `QueryRouter`、不碰 `QueryPump`；在 `SearchWindow` 空态分支本地合成 `LauncherItem` 后走既有 `ClearAndFill` | 保住两条被断言钉住的不变量: selftest 25.4「空查询零往返（`Execute` 零调用）」与 W10-1「clip 空查询 ⇒ 空结果且零触达来源」 |
| D4 | 容量与配置 | `LauncherDefaultView.Limit = 8`，**零新配置键**（`launcher.usage=false` 天然关掉默认视图） | 不为一屏列表加一个配置面；先看真实使用再谈可调 |
| D5 | 就绪补发 | `OnProviderBecameReady` 补一支：空查询时本地重渲默认视图（因 `QueryPump.Requery` 对空文本是 no-op），仍守两条前置（窗口可见 + `_input.Text == _lastRenderedQuery`） | 首扫就绪前查不到 apps，若不补发则「最近使用」永远空着 |
| D6 | 文案 | 左位 `EmptyQueryHint` = 「输入以搜索 · Enter 打开 · Ctrl+Enter 定位 · Ctrl+C 复制 · Tab 更多动作」；右位视图非空 ⇒ 「最近使用 N」，视图空 ⇒ `EmptyQueryEmptyRight` = 「「图片」看最近剪贴板图片 · 「>」看系统命令」；apps `Error` 非空 ⇒ 右位显示该错误；陈旧态仍由 `ApplyStaleNotice` 优先覆盖 | 一条状态行同时承担「能用什么」与「为什么空」；错误必须可见（W7 纪律） |
| D7 | 动作 UI | **动作 drill-down**（Tab 进入，把结果列表替换为该行的动作行；Enter 执行；Esc/Tab 返回；输入变化自动退出；动作模式期间 `RenderResults` 早退） | 复用既有 `ListBox` + `LauncherRowText` + `SnapshotRows` 探针面（**零新控件**）；Tab 在原生 EDIT 里原本只是把焦点移出输入框，无实际损失 |
| D8 | 动作列表生成 | Desktop 纯函数 `LauncherActionMenu.For(LauncherItem)`：基础两条 = `PrimaryAction`/`SecondaryAction` 去重，再按 Kind 追加；W12-c 只为 File 行追加「计算哈希」「统计字数」「速览」 | 纯函数 ⇒ 探针/selftest 可穷举；动作清单是「这一行能做什么」的知识，集中在单点 |
| D9 | 契约扩展 | `LauncherActionKind += ToolCommand`（`Argument` = 命令 id，`Arguments` = JSON 参数）+ `LauncherAction` 加可选 `bool CopyResult = false` | 只加一个动作类型 + 一个副作用开关；不发明第二套「动作声明」机制 |
| D10 | 宿主路由 | `SearchWindow.ToolActionRequested` 注入（`Func<LauncherAction, Task<LauncherToolOutcome>>`）；托盘实现 = `Processes.InvokeCommandAsync` + 摘要 + 可选写剪贴板 + 可选开面板（preview）⇒ 返回文案与是否收窗；执行期 `_toolBusy` 防并发，必须 `await` 不阻塞 UI | 窗口不认识 `ToolHostManager`，宿主不认识动作细节 —— 各自只做一半 |
| D11 | 摘要单点 | 新增 Host 纯函数 `ToolResultText.Hint/Text`，托盘 `DescribeResult` 改调它 + 回落 JSON；工具侧只加一个 `hint`（filehash / wordcount）；**preview 的 hint 已存在**，直接用 | 「想让人看什么就放进 hint」已是全项目约定；把它抽成单点，避免第二份判定漂移 |
| D12 | 触发键 | Tab（`InputCommand.ActionMenu`），经 `NativeInputBox.WndProc` 消息层拦（`WM_KEYDOWN` 全收）⇒ 不依赖 IME 状态；探针经 `ProbeCommand` 断言同一分派 | 已验证可行（§2.4）；不动既有 Enter/Esc/Up/Down/Ctrl+C 任何一支 |
| D13 | 不恢复 `actions` 声明 | 只在 Launcher 侧建路由，`tool.json` 零改动 | 恢复 `actions` 会引入 Shell 上下文菜单扩展（代码进 `explorer.exe`），违反「工具代码永不进 Core」 |
| D14 | 顺手加固 | `LauncherUsageStore.BoostFor` 的 `DateTime.Parse`（`:210`）改 `TryParse`，失败 ⇒ 0 加成、不抛 | 类注 `:21-22` 承诺「任何 IO/数据异常 ⇒ `LastError`，绝不抛」；现在只靠 `QueryRouter.SafeQueryAsync` 兜住，等于把「应用段整体消失」当错误处理 |

## 9. 风险与缓解

| # | 风险 | 缓解 | 靠哪条断言 |
|---|---|---|---|
| R1 | 用户把默认视图读成「搜索就这几个结果」 | 右位「最近使用 N」+ 左位仍写「输入以搜索」 | W12-11 |
| R2 | 空态改造破「空查询零往返」/「clip 空查询零触达」两条不变量 | 默认视图不进 provider/管道（D3）；本地合成后复用 `FillItems` | W12-1 |
| R3 | 动作模式与在途渲染竞态（旧批次把动作行冲掉） | 动作模式期间 `RenderResults` 早退；`_actionRows != null` 是唯一判据 | W12-7 |
| R4 | 工具动作耗时（哈希大文件）卡 UI | `async` 全链 + `_toolBusy` 并发闸 + 命令自带 `TimeoutMs` | W12-8 |
| R5 | preview 的 KV 桥接被误用（`reload` 无参 ⇒ 清空记录） | 只调 `show` 并显式带 `inputPaths`，执行后主动开面板；**不碰** `reload` | W12-10 |
| R6 | `CopyResult` 把工具结果写进 W5 剪贴板历史 | 用户显式点了「计算哈希并复制」⇒ 名称已承诺副作用，可接受；命名带「并复制」 | W12-8 |
| R7 | Tab 占用了既有行为（焦点切换） | 列表本就不可交互 ⇒ 无实际损失；仍记为行为变更（§12） | W12-5 |
| R8 | apps 读不到时默认视图静默为空「什么都不说」 | 右位显示 `AppIndexCache.LastError`（「读不到必须可见」） | W12-11 |
| R9 | `Recent` 的排序/剥前缀写错，视图顺序漂移 | 坏 `lastUsed` 排除 + identity `Ordinal` 升序兜底 ⇒ 确定性；selftest 穷举 | W12-2 |
| R10 | 动作行引入新 `LauncherKind` 后，既有按 Kind 的分支漏处理（如 `ExecuteAction`、探针行快照） | 徽标 `BadgeOf` 加 `Action`、`LauncherActionRunner.Execute` 加显式分支（禁落 `default`）；`SnapshotRows` 自然覆盖 | W12-6 |
| R11 | 取色/进程结束等**新 provider** 被顺手夹带进本波 | §7「零改动 5 处」明写 + D1 | 评审 |
| R12 | 工具侧新增 `hint` 改变既有面板/CLI 显示 | `hint` 只在宿主摘要路径被读（`ToolResultText`），面板与 `ezt invoke` 走原样 JSON | W12-14 |

## 10. 落地步骤与验收

### 10.1 实施顺序（三段各自可独立验收，任一段不通过不进入下一段）

1. **W12-a 默认视图**：`LauncherUsageStore.Recent` + 加固（D14）→ `AppsProvider.SnapshotState` → `LauncherDefaultView.cs` → `SearchWindow` 空态改造（`FillItems` / `RenderEmptyQuery` / `BuildDefaultView` / 就绪补发）→ selftest 25.x + 探针。
2. **W12-b 触发词发现性**：两串文案常量 + §4.2 四场景放置 → 探针快照断言（**先 a 后 b**：b 的右位要显示「最近使用 N」，依赖 a 已能产出视图）。
3. **W12-c 工具动作**：契约扩展 → `InputCommand.ActionMenu` + Tab → `LauncherActionMenu.cs` → `SearchWindow` 动作模式与 `RunToolActionAsync` → `ToolResultText.cs` + 托盘注入 → `tools/filehash` / `tools/wordcount` 加 `hint` → selftest + 探针 + 守卫。

### 10.2 自动验收面

- `dotnet run --project src/Eztools.Cli -- selftest`：新增 25.x 组（**仅新增用例，不改既有编号与文案**），基线 336/0 之上只增不减。
- `python scripts/verify-desktop.py`：新增 launcher 默认视图 / 动作菜单 / 工具动作断言（基线 274/0）。
- 守卫脚本：`tools/*/tool.json` 仍无 `actions` 段（若既有 G8 守卫能覆盖则复用，不新增守卫条目）。
- `python scripts/check-doc-refs.py --strict` ⇒ rc=0。

### 10.3 手工验收

见配套 `docs/W12-手工验收清单.md`（真机唤出 → 最近使用可见 → Tab 进动作 → 对真实文件算哈希 → 剪贴板拿到摘要 → 空态文案三段可见）。**手工项未过之前，W12 不算闭环**（W11 的教训：自动全绿只证明代码在，不证明用户能用）。

## 11. 断言面（W12-1 ~ W12-14）

| # | 断言 | 落在哪 |
|---|---|---|
| W12-1 | **空查询默认视图零 provider 调用**：空文本 ⇒ `Execute` 计数不变、`clip` 来源零触达；视图由本地合成 | selftest（25.x） |
| W12-2 | `Recent`：倒序正确、坏 `lastUsed` 被排除、identity 含 `|` 时剥前缀正确、`limit=0` 空表 | selftest |
| W12-3 | `BoostFor` 遇坏 `lastUsed` ⇒ 返回 0 且**不抛**（加固回归） | selftest |
| W12-4 | `LauncherDefaultView.Build` 纯函数穷举：usage 关闭 / 空 / 部分解析不到 / 全解析不到 / 超 8 条截断 | selftest |
| W12-5 | Tab ⇒ `handled = true` 且窗口**不隐藏**（空查询与非空查询各一次） | 探针 |
| W12-6 | 动作菜单内容：File 行 5 条（打开/定位/哈希/字数/速览）、App 行 2 条、**定义序稳定** | 探针 |
| W12-7 | 动作模式状态机：Tab 进 / Enter 执行 / Esc 返回 / 打字自动退出；动作模式期间在途渲染**不覆盖**（`SnapshotRows`） | 探针 |
| W12-8 | 工具真链路：临时文件 → 「计算哈希并复制」→ 状态行含摘要 + 剪贴板内容 == 摘要（`CopyResult`） | 探针 |
| W12-9 | 失败面：目标不存在时状态行含「执行失败」且窗口**未隐藏** | 探针 |
| W12-10 | preview 动作：发出的 args 含 `inputPaths`、执行后请求开面板（**不得**调 `reload`） | 探针 |
| W12-11 | 空态文案三态：有视图 ⇒ 右位「最近使用 N」；无视图 ⇒ 提示串；apps `Error` ⇒ 右位显错 | 探针快照 |
| W12-12 | 就绪补发：空查询下 apps 首扫完成 ⇒ 列表自动出现（照 `RunUsage` 夹具，用渲染日志当就绪信号） | 探针 |
| W12-13 | 不恢复 `actions`：`tool.json` 无 `actions` 段 | 守卫 |
| W12-14 | 摘要单点：`ToolResultText.Text` 对 hint/非 hint/null 三种输入 == 既有气泡文案（`LastBalloonBody` 仍守） | selftest + 探针 |

## 12. 已知限制（不装作用户不会遇到）

1. 默认视图**不显示使用次数**、不提供「常用」榜（D2）。
2. 默认视图内部**不可再过滤**（它就是空查询态；打字即退出视图）。
3. 动作菜单**只有一层**，不支持为动作输入参数（如哈希算法选择用工具默认值）。
4. 工具动作**只对单行**生效，不支持多选批量（`hashMany` 目前只会收到一个路径）。
5. `preview` 的面板打开沿用既有「按 id 打开」语义：面板已开则复用，不新开窗口。
6. Tab 从「焦点移出输入框」改为「进入动作菜单」（原生 EDIT 的默认 Tab 行为被吞掉，§D7/§R7）。
7. `BoostFor` 加固后**坏数据仍留在** `usage.json`（不自动清理、不迁移）；修复方式是它不再影响加成，而不是文件被洗干净。
8. 默认视图上限 8 条是**编译期常量**，改它要改代码（D4 明确不加配置键）。

## 13. 变更记录

| 版本 | 日期 | 变更 |
|---|---|---|
| v1 | 2026-10-07 | 首版：立项依据（闸门对账）· 事实核查 2.1~2.8 · W12-a/b/c 设计 · 接口定义 6.1~6.8 · 改动面（新建 5 / 修改 13 / 零改动 5）· 决策 D1~D14 · 风险 R1~R12 · 断言面 W12-1~W12-14 · 已知限制 8 条 |
