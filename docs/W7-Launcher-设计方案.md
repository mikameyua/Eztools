# W7 · 通用启动器 Launcher 设计方案

> **文档类型**：③ 功能设计（架构权威：选型 / 模块 / 接口 / 流程 / 边界 / 风险）
> **状态**：✅ **W7 全波完成（W7-a~e，2026-10-01）**——v2 细化 + §8 D1~D11 拍板 + 五个子波全部落地。W7-e 三件套全绿：构建 0 错/7 警告=基线 · selftest 281/0 · verify-desktop 233/0（连跑 3 次）· acceptance 满额 rc=0 · 守卫 FAIL=0/WARN=0 · `check-doc-refs --strict` exit 0。落地记录见 §6；手工项归 `docs/W7-手工验收清单.md`（新建，M1~M6 归菲比）
> **创建**：2026-10-01 · 权威链：`Eztools-设计方案.md` > 本文档
> **上游依据**：`docs/功能候选路线图.md`（F3；计算器/单位换算/编码转换并入 provider，不独立立项）
> **实现纪律**：动手前先读 §7 风险表 + §11 异常与边界 + `验收断言审视清单.md`；键盘链红线（踩坑全集 §2.25~§2.30）适用于全程
> **编号冻结**：§1~§9 的编号与语义**不得重排**（`docs/README.md` §3.5b 与 `docs/功能候选路线图.md` 都按"§7 风险 + §8 拍板"引用）；新增内容一律追加为 §10+。

---

## 0. 阅读导航

| 我要… | 看哪节 |
|---|---|
| 一句话搞清这个功能是什么、为什么值得做 | §1 · §2 |
| 知道代码落在哪、谁依赖谁、分层怎么切 | §3.2 · §10.1 · §12 |
| 写代码前找**接口签名与数据结构** | §10.2 · §10.3 |
| 搞清一次按键从输入到出结果的完整流程与竞态 | §3.3 · §10.4 |
| 想知道某个 provider 具体怎么实现 | §10.5（files）· §10.6（apps）· §10.7（calc）· §10.8（unit/encode） |
| 对照"哪些既有行为一个字节都不能变" | §2.1 FR-10 · §10.5 映射表 · §10.9 对照表 |
| 处理异常 / 想清边界情况 | §11（全节） |
| 评估改动会波及谁 | §12.3 下游消费者 |
| 拍板未决项 | §8 |
| 知道怎么验收、判据是什么 | §9 · §10.12 |
| 抄现成实现（复用什么、从哪、注意什么） | §14 |

---

## 1. 功能概述

**一句话**：W3 搜索窗进化为统一入口——一个热键、一个输入框，**输入即出结果，结果可执行**：搜文件（现状）、启动应用、算表达式、换单位、转编码。

- **产品定位**：对标 PowerToys Run / Listary 的入口型功能，Eztools 工具箱的"门面"。
- **为什么是 Wave 级**：provider/action 抽象是平台工程；但 files provider 天然是第一个插件，抽象不为假想需求设计。
- **复用底座**：W3 搜索窗壳（Summon/Toggle、NativeInputBox 键盘链、结果渲染、防抖查询）、`SearchIndexClient` → `SearchService` 既有管道、`HostSettingsSchema` 配置、`SearchUiProbe`/`SearchSummonProbe` 探针。
- **已裁剪非目标**：插件市场/第三方 provider、剪贴板搜索（W5 已有）、网页搜索跳转（v2）、拼音首字母匹配（v1 只做名称模糊匹配，中文输入法路径已被 §2.30 验证可用）。
- **三层价值**（决定后面的取舍口径）：
  1. **入口收敛**（产品层）：一切从热键进，缓解 `RegisterHotKey` 稀缺（已占 S/O/V/X/C 五键）。
  2. **能力复用**（工程层）：五个 provider 共享同一套会话/渲染/动作/探针基座。
  3. **主设计承诺的兑现**（权威层）：主设计 §4.5「`actions` 不做派发，入口归 W7」——**"对文件/选中内容快速操作"的入口就是本波**，本波不交付该承诺则 §4.5 那条留痕会变成空头支票。

## 2. 需求说明

### 2.1 功能需求（FR）

| # | 需求 | 验收口径 |
|---|------|----------|
| FR-1 | 热键唤起行为与现状**完全一致**（`search.hotkey`，不新增热键）；输入即出结果 | 既有探针零回归（`--probe-search-summon` 断言不改） |
| FR-2 | **files provider**：与现搜索行为逐字节一致（结果、排序、Enter 打开、Ctrl+Enter 揭示） | W7-a 回归门槛：acceptance 满额 0 FAIL |
| FR-3 | **apps provider**：模糊匹配应用名 → Enter 启动；Ctrl+Enter 打开所在目录；结果带图标 | 手测 + 探针（§10.6 / §9） |
| FR-4 | **calc provider**：输入算术表达式（如 `128*3/4`）出结果行（类型徽标 `=`），Enter 复制结果值 | selftest 解析器精确断言（含除零/溢出/非法输入取值缺失要"响亮失败"） |
| FR-5 | **unit / encode provider**（第二批）：单位换算、base64/URL/Unicode 转换 | W7-d 细化（§10.8 已给规格） |
| FR-6 | 结果行显示**类型徽标**（文件/应用/计算/换算），一眼可辨来源 | 手测（**口径见 §10.9 注 ①**：徽标只加在非文件来源上） |
| FR-7 | provider 可配置启停（`launcher.providers`，白名单式——反向断言：未知值拒绝，不静默吞，S2 教训） | `--probe-host-settings` 扩展 + selftest 纯函数断言 |
| FR-8 | 使用频次记忆：同一输入多次选择后，被选项排序提前 | W7-e，手测。**口径收窄（待确认）**：v1 只作用于 **apps 段** —— files 的排序由索引进程给出，重排它直接违反 FR-2/FR-10（见 §10.11 注 ②） |
| **FR-9** | **provider 级故障隔离且故障可见**：任一 provider 抛异常，其余 provider 结果照常渲染，出故障的段位在状态行给出**可读原因**（不弹窗、不只写日志） | 探针：注入必抛的假 provider ⇒ 断言其余段仍在 + 状态行含原因（§11.2） |
| **FR-10** | **文件项渲染零变化**（W7-a 硬门槛的机器化）：文件命中项的渲染结构与文本与今天**逐字节一致** | `--probe-search-ui` 的 `firstSegments`/`firstName`/`firstPath`/`statusRight` **断言一行不改**且全绿 |
| **FR-11** | **键盘链零介入**：`NativeInputBox` 及其供字/命令上报路径一行不改；provider 层只消费文本、不拦按键 | `git diff` 对 `src/Eztools.Desktop/NativeInputBox.cs` 为空 + W3 手工清单 M2 抽查 |

### 2.2 非功能需求（NFR）

- **NFR-1 零新 NuGet**：计算器自写解析器、单位表纯函数、图标走 Win32 Shell——不加包（D3 论证）。
  ⚠️ **口径澄清（W7-e 前置）**：本条的判据是"**不引入新的第三方库**"。`Microsoft.Data.Sqlite` 已在解决方案内（`Eztools.Clipboard` 在用），若 D6 复议采纳 JSON 方案则连"新增依赖边"都不需要（§8 D6 复议）。
- **NFR-2 零特权**：不碰 Core；应用启动走 `Process.Start` 普通权限。
- **NFR-3 不碰键盘链**：NativeInputBox 及其供字路径**一行不改**；provider 层只消费文本、不拦截按键（命令键仍由输入框上报，W5 面板同款分工）。
- **NFR-4 性能**：唤起与首屏延迟不劣于现状；apps/calc 为本地即时计算（≤10ms 量级）；files 仍走既有防抖异步管道。
- **NFR-5 可探针化**：每个新行为都必须能找到"不改断言的机器观测面"（沿 W6-d「自动化优先」口径：能自动验的不进手工清单）。
- **NFR-6 线程纪律**：**绝不在 UI 线程做 IO**。apps 扫描、图标提取、频次读写全部在线程池/后台线程；UI 侧只接受已 marshal 的结果（`SearchWindow` 类头的既有纪律）。

### 2.3 非目标（v1 不做）

| 不做 | 理由 |
|---|---|
| 新窗口 / 新热键 | D1：进化 SearchWindow，热键预算不扩（已占 S/O/V/X/C 五键） |
| 通用查询引擎假抽象 | USN/MFT 引擎是 files 专属，不"通用化"；各 provider 自带查询逻辑 |
| 第三方 provider 插件机制 | 抽象仅服务内置五源；接口不导出、不做发现协议 |
| 自然语言/网页搜索 | 违反产品边界，且含网络 |
| 文件内容全文搜索 | USN 只索引路径；内容搜索另行立项 |
| **.lnk 快捷方式解析**（IShellLink / WScript.Shell） | D7：启动与取图标都不需要解析（见 §10.6）；解析只为"同目标去重/别名/更准标题"，不值 v1 的 COM 成本 |
| **时间单位换算**（s/min/h/d） | 与长度单位 `m` 命名冲突，且换算关系是非十进制；v1 只做长度/重量/数据量/温度（§10.8） |
| **provider 运行时热插拔**（改配置即换集合，不重启） | 配置改动需重开窗口才生效（与 `color.format` 同款时机口径）；热插拔引入并发装配问题。★ **2026-10-02 更新**：「需重开窗口才生效」这一半**已由 W8 · B2 修复并落地**（改为"下次唤出即生效"，手段是**重建窗口**而非热插拔 —— 本文件 §2.3 的取舍仍然成立）⇒ 见 `docs/W8-可用性回填-设计方案.md` §4.4 / §7.2 |
| 拼音首字母匹配 / 中文分词 | v1 用"子序列模糊匹配 + 别名配置"覆盖；中文输入法直通已由 §2.30 验证 |
| 文件图标（给 files 段加图标） | 会把 files 项的渲染结构改掉 ⇒ 直接违反 FR-10；且 200 条命中的图标提取是性能与句柄风险 |

## 3. 整体架构

### 3.1 核心决策：SearchWindow 进化，不新建窗（D1 推荐 A）

```
SearchWindow（现有，壳不动：Summon/输入框/结果列表/防抖）
 └─ QueryRouter（新增：文本 → provider 分发 + 代次 token 防竞态）
      ├─ FilesProvider   ← 包装现有 SearchIndexClient 流（W7-a 零行为回归）
      ├─ AppsProvider    ← 开始菜单(.lnk 用户+公共)+桌面快捷方式+注册表 App Paths；启动缓存
      ├─ CalcProvider    ← 自写递归下降解析器（+-*/^%() 一元负号，double 语义，显式错误）
      ├─ UnitProvider    ← 纯函数换算表（长度/重量/温度/数据量…，W7-d）
      └─ EncodeProvider  ← base64/url/unicode 三向（W7-d）

 LauncherItem（新增结果统一模型）：Title / Subtitle / Kind 徽标 / Icon? /
   PrimaryAction / SecondaryAction / MatchScore
```

- **插入点**：现有"输入变化 → SearchIndexClient 查询 → SearchHitDto → HitText 渲染"链路中，把"查询"一步换成 QueryRouter 聚合各 provider 结果；渲染层扩展类型徽标（**文件项不变**，见 FR-10），`SearchHitDto` 本身不动（files 保持五元组）。
- **竞态**：沿用既有防抖 + 查询代次（旧代次结果丢弃）；provider 结果必须带代次 token，防慢 provider 回填旧数据（W3-d 已有同型机制）。
- **为什么不是独立新窗**：热键预算紧张（已注册 5 键）；用户要的是"一个入口"；SearchWindow 壳的键盘链/焦点链已用血验证，复制第二份是负资产。代价是动稳定代码——用 W7-a"零行为 commit + 全量回归门槛"对冲（同 W6-a 基类抽取打法，已验证有效）。

### 3.2 模块划分与依赖方向（v2 新增）

**分层（箭头 = 依赖方向，绝不反向）**：

```
┌─ 表现层（Desktop，net10.0-windows + WPF/WinForms）
│    SearchWindow ─ LauncherRowText ─ LauncherRowTemplateSelector ─ IconCache
│    TrayApplication（装配）· AppsProvider（Shell/注册表）· LauncherActionRunner · LauncherUiProbe
└───────────────┬──────────────────────────────────────────────────────────
                │ 调用（单向）
┌─ 编排/纯逻辑层（Host，net10.0，无 Windows/WPF 依赖）
│    QueryRouter ─ QueryPump ─ LauncherPrefs ─ FuzzyMatcher
│    FilesProvider ─ CalcProvider ─ UnitProvider ─ EncodeProvider ─ LauncherUsageStore
│    LauncherContracts（纯数据）· ILauncherProvider（接口 + 注册表）
└───────────────┬──────────────────────────────────────────────────────────
                │ 复用（不改）
        SearchIndexClient / ISearchIndexTransport / SearchHitDto
        ConfigStore / HostSettingsSchema / EztoolsPaths / InputThrottle
```

| 决策 | 结论 | 理由 |
|---|---|---|
| 纯逻辑 provider 放 Host | `QueryRouter`/`QueryPump`/`CalcProvider`/`UnitProvider`/`EncodeProvider`/`FuzzyMatcher`/`LauncherPrefs`/`LauncherUsageStore` 全部落 **Eztools.Host**（`（新建）src/Eztools.Host/Launcher/`） | ① `Eztools.Cli` 与 `Eztools.Desktop` **都引用 Host** ⇒ selftest（Cli 侧）能直接测纯函数；② Host 是 net10.0 平台中立 ⇒ **编译器强制**"纯逻辑不碰 Win32/WPF"（不是靠自觉） |
| Win32 相关 provider 放 Desktop | `AppsProvider`（Shell 扫描 / `SHGetFileInfo` / 注册表 `App Paths`）与 `IconCache` 落 **Eztools.Desktop**（`（新建）src/Eztools.Desktop/Launcher/`） | Host 是 net10.0，用 `Microsoft.Win32.Registry`/Shell32 会产生 CA1416 平台兼容告警 ⇒ 破坏"警告 7 = 基线持平"的验收判据（§9） |
| **接口不得出现 UI 类型** | `ILauncherProvider` 的签名里**不允许出现 `BitmapSource`/`ImageSource`/`Icon`**；图标以 `string IconHint`（可解析的路径）表达，由 Desktop 侧 `IconCache` 解析 | 否则 Host 被迫依赖 WPF ⇒ 分层断裂 + selftest 不可测。这条是**可机器化检查**的：`grep -n 'System.Windows' src/Eztools.Host/Launcher/` 必须为空（§12.5） |
| 不新增项目 / 不新增进程 | 全部落既有两个项目 | 与 W6 §10 同款结论；新增项目要付构建/打包/发布矩阵成本 |

**命名与落位（文件清单见 §10.1）**：Desktop 现为扁平结构（`SearchWindow.cs` 同级），本波新增 6 个文件 ⇒ 收进 `Launcher/` 子目录（扁平再塞 6 个文件会明显伤可导航性）；Host 侧同样收进 `Launcher/` 子目录，与既有 `Search/` `Config/` 风格一致。

### 3.3 数据流与竞态模型（v2 新增）

**一次按键的完整生命周期**（7 步，★ = 既有代码，不改）：

```
① 用户按键
   └─ ★ NativeInputBox（原生 EDIT，IMM32）── 字符由系统输入法经 WM_CHAR 直送
        └─ TextChanged 事件（只报"变了"，不带字符内容 —— 宿主读 .Text）

② SearchWindow.TextChanged → router.Submit(_input.Text)          ← W7-a 唯一替换点
        └─ ★ 由 InputThrottle 决定何时真发（防抖 30ms / 最短间隔 80ms）

③ QueryPump 到点 ⇒ Dispatch(Generation++, Text)
        ├─ 代次：每次**派发**递增（不是每次 Submit 递增 —— 见 §10.4 注 1）
        └─ 单在途：_inFlight 为真 ⇒ 只置 _trailing = true，不派发（收敛"打字快于响应"）

④ QueryRouter 扇出：对**启用且就绪**的 provider 并行 QueryAsync
        ├─ calc/unit/encode：同步纯函数（微秒级，无 IO）
        ├─ apps：内存索引匹配（首扫未就绪 ⇒ IsReady=false ⇒ 静默不参与）
        └─ files：★ SearchIndexClient.QueryAsync（stdio 往返，60s 硬超时）
              └─ 过期闸：client.IsStale(epoch) ⇒ 该段 DROPPED

⑤ 归并（§10.4 排序规则）：pin 段 → apps 段 → files 段（files 段**原序不重排**）

⑥ 代次闸：ticket.Generation == router.Generation ? 渲染 : 整体丢弃

⑦ UI 线程渲染（Dispatcher.BeginInvoke）
        ├─ ★ files 项 → HitText（**模板与代码零改动**，FR-10）
        └─ 非 files 项 → LauncherRowText（徽标 + 标题 + 副标题 + 可选图标）
```

**三层收敛 + 双闸（必须理解，否则改坏）**：

| 层 | 收敛什么乱源 | 机制 | 落点 |
|---|---|---|---|
| 节流 | 物理按键风暴 | `InputThrottle`（防抖 30ms / 最短 80ms） | QueryPump（★ 常量与 `SearchSession` 同值） |
| 单在途 + trailing | 打字快于响应 | `_inFlight` / `_trailing` / 只补发最末次 | QueryPump |
| **代次闸** | 慢 provider 回填旧结果 | `Generation` 随派发递增；渲染前比对，不等即整体丢弃 | QueryRouter（v2 新增，R3） |
| **过期闸**（既有，保留） | 在途期间索引侧状态变了 | `SearchIndexClient.IsStale(chainEpoch)`（`StatusAsync`/暂停也会签发 epoch） | FilesProvider（R1：**不替换、不删除**） |

> **为什么两道闸都要留**：代次闸管"provider 结果属于哪一次派发"，过期闸管"索引进程的会话级作废"（例如：查询在途时用户点了暂停/卷清单刷新 ⇒ epoch 前进 ⇒ 该结果按既有语义作废）。把过期闸换成代次闸＝**行为变化**，W7-a 不允许（§10.5 注 ③）。

### 3.4 AppsProvider 细节

- **扫描源**：`%AppData%\Microsoft\Windows\Start Menu`、`%ProgramData%\Microsoft\Windows\Start Menu`（递归 .lnk）、桌面 .lnx/.url、注册表 `App Paths`；启动后异步扫描，落缓存（改动了才重扫：按目录时间戳/文件清单哈希）。
- **图标**：`SHGetFileInfo`/`IShellItemImageFactory` 提取一次、按路径缓存位图（**句柄生命周期 try/finally**——安全专项 RI-4 教训，验收加"连续 N 次看斜率"句柄自测）。
- **匹配**：不区分大小写子序列模糊分 + 别名（W7-e 配置 `launcher.alias`）。
- **展开规格见 §10.6**（扫描源清单 / 失效指纹 / 匹配打分 / 图标句柄 / 启动动作 / 失败文案）。

### 3.5 CalcProvider 细节

- 触发：**自动嗅探**——输入满足"安全表达式语法"才出结果行，解析失败返回空（不弹错误、不干扰文件搜索）；结果行固定带 `=` 徽标。
- 语法：四则 + 幂 + 取模 + 括号 + 一元负号；double 语义；除零/NaN/Infinity 显式错误文本（禁 `:-0` 式静默）。
- **排序位置**：calc 命中时置顶为首行（PowerToys Run 同款），files 结果不受影响。
- **展开规格见 §10.7**（词法/语法 EBNF / 两类失败的**不同**处置 / 求值错误码 / 数值格式化）。

### 3.6 装配与生命周期（v2 新增）

| 环节 | 载体 | 时机 | 释放 |
|---|---|---|---|
| Provider 集合装配 | `TrayApplication` 构造 provider 列表（依赖 `LauncherPrefs` + `SearchIndexClient`），注入 `new SearchWindow(prefs, providers)` | 首次唤出懒创建（照抄 `EnsureSearchWindow`） | 随窗口 |
| apps 索引首扫 | `AppsProvider` 内部 `Task.Run`，**懒触发**（首个非空查询时启动），完成后置 `IsReady=true` | 后台线程，不阻塞唤出（R6） | 无（进程级缓存） |
| 会话 | `QueryRouter`（内含 `QueryPump`） | 随窗口构造 | `SearchWindow.Closed` → `Dispose`（今日 `DisposeSession` 同款；探针收尾走 `DisposeForProbe` 兜底，因为未 `Show()` 的窗口 `Close()` 不触发 `Closed`） |
| 图标缓存 | `IconCache`（Desktop，静态单例） | 首次渲染 apps 项 | 进程退出（不显式释放；BitmapSource 已 `Freeze`，HICON 已 `DestroyIcon`） |
| 频次记忆 | `LauncherUsageStore` | 首次查询时懒加载 | 宿主退出前 flush（`TrayApplication` 收尾链） |
| 索引进程 | `SearchIndexProcess`（**不动**） | 既有 | 既有 |

> ⚠️ **窗口"隐藏"不释放任何 provider 资源**（与今日"隐藏而非关闭"的既有语义一致）——只有真关闭（托盘退出 / 探针收尾）才释放会话。所以 dispose 路径必须**幂等**且**两条都覆盖**（今日 `DisposeSession` 有 `_sessionDisposed` 标志；新代码照抄）。

## 4. 交互契约

### 4.1 输入 → 行为

| 输入 | 行为 |
|---|---|
| 普通文本 | files + apps 模糊结果混合（apps 命中置顶段，files 随后，各自内部按现有排序/频次） |
| 算术表达式 | calc 结果行置顶 + files/apps 若有命中照常列出 |
| Enter | 首行主动作（打开文件 / 启动应用 / 复制计算结果） |
| Ctrl+Enter | 次动作（揭示所在目录 / 打开应用目录） |
| Esc | 现状不变（收窗） |

### 4.2 完整键位 × 结果类型矩阵（v2 新增）

| 键 | 文件项（File） | 应用项（App） | 计算项（Calc） | 换算项（Unit） | 编码项（Encode） |
|---|---|---|---|---|---|
| `Enter` | 打开（`UseShellExecute`）→ **收窗** | 启动 → **收窗** | 复制**结果值** → **收窗** | 复制结果值 → 收窗 | 复制转换结果 → 收窗 |
| `Ctrl+Enter` | 资源管理器定位 → 收窗 | 定位目标所在目录 → 收窗 | 复制 `表达式 = 结果` 整串 → 收窗 | 同左（复制 `原式 = 结果`） | 同左 |
| `Ctrl+C` | 已选中 ⇒ 复制**路径**；未选中 ⇒ 放行给输入框 | 复制**可执行文件/快捷方式全路径** | 复制结果值 | 复制结果值 | 复制转换结果 |
| `↑` / `↓` | 移动列表选中（**焦点始终留在输入框** —— Everything 模式，既有语义） | 同左 | 同左 | 同左 | 同左 |
| `Esc` | 收窗（`Hide`，不销毁） | 同左 | 同左 | 同左 | 同左 |
| `Del` / `Ctrl+P` | **放行**（返回 false，不吞 —— 既有契约：静默吞键＝用户以为快捷键坏了） | 放行 | 放行 | 放行 | 放行 |

**规则（写死，避免实现期各写各的）**：
1. **动作执行成功 ⇒ `Hide()`**（收窗）；**动作执行失败 ⇒ 留窗 + 状态行给可读原因**（既有 `OpenFile`/`RevealInExplorer` 同款）。
2. **复制类动作（`CopyText`）执行成功也收窗**——与"Enter = 完成一次操作"的整体手感一致（PowerToys Run 同款）。
   ⚠️ 与既有 `Ctrl+C`（复制路径）**不收窗**的行为不同：那是"查档时的顺手动作"，不是"完成操作"。两者语义不同，不做统一（统一会改变既有 `Ctrl+C` 行为 ⇒ 违反 FR-10 家族纪律）。
3. `Ctrl+Enter` 在**无次动作**的项上（理论上不该存在，防御性）⇒ 退化为主动作。
4. 列表为空时 Enter/Ctrl+Enter/↑/↓ **一律不动作且不吞键**（既有 `case ... when _results.Items.Count > 0` 形态，`default:` 返回 false）。

### 4.3 结果行视觉契约

| 类型 | 徽标 | 主行 | 副行 | 图标 |
|---|---|---|---|---|
| File | **无**（见 §10.9 注 ①） | 文件名（含索引回传高亮分段） | 全路径（灰、省略号） | 无 |
| App | `▸`（可执行/启动语义） | 应用名 | 目标全路径 | ✅ 真图标（16×16，D5=A） |
| Calc | `=` | 结果值 | `= 原表达式`（去首尾空白） | 无 |
| Unit | `⇄` | 换算结果 | `原式 = 结果` | 无 |
| Encode | `{}` | 编码/解码结果 | 方向说明（如 `base64 解码`） | 无 |

> **徽标为何用单字符记号的说明**：单字符可用**纯文本**表示 ⇒ 探针能整段读回断言（`LauncherRowText.RenderedBadge`），不需要截图比对。颜色只做弱区分（不进入断言）。

## 5. 设置与接线

| 项 | 值 | 说明 |
|---|---|---|
| 配置键 | `launcher.providers`（默认 **`files,apps,calc,unit,encode`** —— 取"当前**已实现**的 id 全集"） | schema 在 **Eztools.Host** `HostSettingsSchema`；解析白名单式，未知值报错不静默 |
| 配置键（可选） | `launcher.usage`（boolean，默认 `true`，W7-e ✅ 已落地） | 频次记忆总开关（**不做降级**：关就是彻底不读写，selftest 36.6 + acceptance 8.5c 双层钉住） |
| 配置键（可选） | `launcher.alias`（string，默认空，W7-e ✅ 已落地） | **格式「别名=目标」分号/换行分隔**；目标 = 应用标题或路径里的**连续一段**（不区分大小写**子串包含**——10-02 实机改判：条目是文件名 `leigod.exe` 而用户写俗称"雷声加速器"，全等必失配）；缺等号 ⇒ 报错回落空表；别名命中 ⇒ +300 分（§10.6 C） |
| 热键 | **不新增**，沿用 `search.hotkey` | D1 前提 |
| 托盘 | 菜单文案「搜索文件」→「搜索 / 启动器」（不改热键语义） | 文案级改动。**影响面已核查**：`--probe-tray-items` 不断言该文案（只断言截图/取色两项），改文案不破断言；但 `TrayApplication.cs` 内的**三处气泡/selfcheck 兜底文案**（`RegisterSearchHotkey` 失败提示、`ReRegisterHotkeys` 日志、selfcheck 行）同步改口径，避免"托盘菜单里没有叫『搜索文件』的项"这种指路错误 |
| 探针 | `--probe-launcher`（§10.12）**独立观测面** —— 不改 `SearchUiProbe` / `SearchSummonProbe` 的任何断言 | ★ 理由见 §10.12 开头 |
| 新探针（v2 定名） | `--probe-launcher`（rows/actions/isolation/router/apps，§10.12） | `DesktopOptions` 加开关 |
| **探针装配纪律** | `--probe-search-ui` / `--probe-search-summon` 的 `SearchWindow` 构造**必须注入"仅 files"的 provider 集合** | ★ 否则真机上 apps 可能命中（开始菜单里真有中文名应用）⇒ `itemsCount == 200` 断言被打破 = **环境依赖的假红**（与 §2.17 墙钟同族）。零行为证据 = **断言一行不改**，只换装配参数 |

**`launcher.providers` 解析规格**（写死；selftest 直测）：

| 情形 | 行为 |
|---|---|
| `"files,apps,calc,unit,encode"` | ✅ 全部五来源（W7-d 起已实现齐） |
| 含空白 `"files, apps"` | ✅ trim 后接受 |
| 重复 `"files,files"` | ✅ 去重（保留首次出现位置） |
| 大小写 `"FILES,Apps"` | ✅ 归一为小写 |
| 未知值 `"files,foo"` | ❌ 报错：`未知 provider "foo"（已知：files、apps、calc）` —— **白名单 = 当前已实现的 id**，故未知 id 一律走这条（文案自我解释，胜过"配置合法但功能不存在"的静默缺席）。★ **CLI 对该键不做取值校验**（自由字符串），取值合法性在应用层（`LauncherPrefs` 白名单）—— 这是刻意的分工，由两层闭环测试钉住 |
| 空串 / 全空白 | ❌ 报错：`至少启用一个 provider` |
| 键缺失 | ✅ 回落默认（= `LauncherProviderRegistry.DefaultEnabled`） |
| 类型不是字符串 | ❌ 报错（`ConfigStore.TryGetString` 返回 null ⇒ 与"键缺失"**不可混同**，需用 `Effective(...)` 原始树判类型，见 §11.6） |
| 非法值的**出口** | ① 设置窗口保存即拒绝（主路径）；② 手改文件 ⇒ 下次唤出时**状态行显示一次告警** + 日志 `WARN`，并按默认集合继续服务（禁静默，审查规范 §3.3「降级信号必须有可达出口」） |

## 6. 分期

| 阶段 | 内容 | 验收门槛 |
|---|---|---|
| **W7-a** | `ILauncherProvider` 抽象 + QueryRouter + files provider 包装（**零行为变化**） | acceptance 满额 0 FAIL · verify-desktop 全绿（**search 段断言零改动**）· selftest 全绿（25.x 由 6 → 10 条，见 §6 落地偏差①）· W3 手工清单抽查 M2（键盘链） |
| **W7-b** ✅ | apps provider（`AppIndexCache` + `AppsProvider` + `IconCache`）· 行渲染分流（`LauncherRowText` + 模板选择器）· `LauncherActionRunner` · `FuzzyMatcher` · `LauncherPrefs` + `launcher.providers` 键 · `--probe-launcher` 五模式 | acceptance 满额 0 FAIL（**473**，通过 472 / 跳过 1）· verify-desktop 全绿（**search 段仍一行未改**）· selftest **236/0**（W7-1~W7-13）· 图标句柄斜率 = 0 · 代次闸突变验证（摘闸即变红） |
| **W7-c** ✅ | calc provider（`CalcExpression` 词法/语法/求值/格式化 + `CalcProvider` 门槛与组行）· `calc` 进白名单与 schema 默认值 · `--probe-launcher calc` 真链路 | selftest **34.x ×17**（精确值表 ≥35 例 + 错误码表 + 格式化边界 + 两层门槛 + 长度/深度上限）· verify-desktop calc 段 ×8 · acceptance 满额 **0 FAIL** |
| **W7-d** ✅ | `UnitTable`（单位表 + 分词 + 仿射温度）· `UnitProvider` · `EncodeProvider` · 白名单/默认值 → 五来源 · 探针 `unit` / `encode` / `config` 三模式 · calc 次动作回填 · 两层闭环（CLI 写入 ↔ 应用解析/告警） | selftest **271/0**（35.x ×18）· verify-desktop **229/0**（连跑 3 次）· acceptance **498/0/1 满额 499**（下限 218 → **236** + 35.x 守卫）· 两层闭环四态全绿 |
| **W7-e** ✅ | 频次记忆 + 别名 + 手工清单 `W7-手工验收清单.md`（新建） | §D 手测归菲比 |

**每阶段交付物（写死，防"做完才想起缺东西"）**：

| 阶段 | 代码产出 | 验收产出 | 文档产出 |
|---|---|---|---|
| W7-a | `LauncherContracts.cs` · `ILauncherProvider.cs` · `QueryPump.cs` · `QueryRouter.cs` · `FilesProvider.cs`；改 `SearchWindow.cs`（装配 + 模板分流）/`TrayApplication.cs`（装配）/`SelfTestCommand.cs`；**删 `SearchSession.cs`**（见 §6 落地偏差①） | verify-desktop：**search 段断言零新增零删改**（唯一门槛是"原来的全绿"）；selftest：25.x 由 6 → 10 条 | 本节下方落地记录 + `docs/README.md` 状态列 |
| W7-b ✅ | `AppsProvider.cs` · `AppIndexCache.cs` · `IconCache.cs` · `LauncherPrefs.cs` · `FuzzyMatcher.cs` · `LauncherRowText.cs` · `LauncherRowTemplateSelector.cs` · `LauncherActionRunner.cs` · `Launcher/LauncherUiProbe.cs`（**全落 `Launcher/` 子目录**）· `HostSettingsSchema` 加 `launcher.providers` · `IReadOnlyList<ILauncherProvider>` 装配出口 `LauncherProviderSet.Build` | verify-desktop：`--probe-launcher` 五模式（rows/actions/isolation/router/apps）+ 设置清单 desktop **12 字段** | 本节下方落地记录 |
| W7-c ✅ | `（新建）src/Eztools.Host/Launcher/CalcExpression.cs`（纯函数：两层门槛 / 递归下降 / 求值错误码 / 格式化）· `（新建）src/Eztools.Host/Launcher/CalcProvider.cs` · `ILauncherProvider` 白名单 + `HostSettingsSchema` 默认值 → `files,apps,calc` · `TrayApplication` 加 calc 工厂 · `LauncherUiProbe` 加 `calc` 模式 · `SearchWindow.ProbeLeadingKinds`（"置顶"读数） | selftest **34.x ×17**；verify-desktop calc 段 **8 条**；acceptance 下限 201 → **218** + 34.x 守卫 | 本节下方落地记录 |
| W7-d ✅ | `（新建）src/Eztools.Host/Launcher/UnitTable.cs`（单位表 + 分词 + 仿射温度 + 常用单位集）· `（新建）src/Eztools.Host/Launcher/UnitProvider.cs` · `（新建）src/Eztools.Host/Launcher/EncodeProvider.cs`（六前缀 + 编解码纯函数）· 白名单/schema 默认值 → **五来源** · `TrayApplication` 加 unit/encode 工厂 · 探针加 `unit` / `encode` / `config` 模式 · **回填**：calc 次动作（§4.2）· **新增** `SearchWindow.StartupWarningFor`（告警文案单点） | selftest **35.x ×18**；verify-desktop **+14**；acceptance 步骤 8 新增**两层闭环**（合法/未知/空/unset 四态）；acceptance 下限 218 → **236** + 35.x 守卫 | 本节下方落地记录 |
| W7-d | `UnitProvider.cs` · `UnitTable.cs` · `EncodeProvider.cs` + schema 键值域扩展 | selftest：换算表 + 编解码双向；CLI `ezt config` 闭环 | 落地记录 |
| W7-e ✅ | `（新建）src/Eztools.Host/Launcher/LauncherUsageStore.cs`（存储 + Boost 纯函数 + 原子写/去抖/有界 flush）· `LauncherPrefs` 加 `UsageEnabled` + `ParseUsage` + `（新建）LauncherAliases`（同文件）· `AppsProvider` 接入频次/别名加成 · `SearchWindow.UsageRecorder` 出口 · `TrayApplication` 装配 + Dispose 有界 flush · `HostSettingsSchema` 加 `launcher.usage` / `launcher.alias` · `EztoolsPaths.LauncherDataDir` · 探针 `usage` 模式 + `config` 扩展观测 | selftest **36.x ×10**；verify-desktop usage 段 **4 条** + 设置清单 desktop **14 字段**；acceptance 8.5c **两层闭环四态**；手工清单 `（新建）docs/W7-手工验收清单.md` | 本节下方落地记录 |

> **落地偏差登记位置**：本表下方（每阶段完成后追加 `### ✅ W7-x 落地记录`），格式照抄 `docs/W6-视觉小波-设计方案.md` §6 —— 记录**产出 / 实现期方案微调 / 回归实测数字 / 发现并修复的问题**，数字必须来自当轮命令输出。

> ### ✅ W7-a 落地记录（2026-10-01 · **完成，三件套全绿**）
>
> **产出（代码）**
> - 新增 `（新建）src/Eztools.Host/Launcher/`：`LauncherContracts.cs`（枚举/查询/结果/渲染模型）· `ILauncherProvider.cs`（接口 + `LauncherProviderRegistry` + `LauncherProviderSet`）· `QueryPump.cs` · `QueryRouter.cs` · `FilesProvider.cs`
> - 删除 `src/Eztools.Host/Search/SearchSession.cs` —— 已删除（见落地偏差①）
> - `src/Eztools.Desktop/SearchWindow.cs`：构造改收 provider 集合 · `TextChanged → QueryRouter.Submit` · 数据模板绑定改指 `LauncherItem.FileHit`（**`HitText` 自身零改动**）· 动作按 `LauncherActionKind` 分派 · `RenderResults(LauncherRenderModel)`
> - `src/Eztools.Desktop/TrayApplication.cs`：装配 `LauncherProviderSet.FilesOnly(client)`（**仅此一行 + 一个 using**）
> - 探针两处构造改传"仅 files"集合（`SearchUiProbe` / `SearchSummonProbe`）—— **断言一行未改**（§5 探针装配纪律兑现）
> - `src/Eztools.Cli/SelfTestCommand.cs`：25.x **6 → 10 条**（5 条语义平移 + 4 条新增 + 原有 FindIndexExe）
> - `scripts/review-guards.py`：新增 **G7 分层守卫**（`src/*/Launcher/` 内的 UI 依赖 / `Eztools.Core` 引用）+ 2 条双向突变（正/反）
> - `scripts/acceptance.sh`：selftest 用例数下限 **184 → 188** + 记账注释
> - 注释同步：`InputThrottle.cs` / `SearchIndexProcess.cs` 里指向 `SearchSession` 的类引用改指 `QueryPump`
>
> **回归实测（2026-10-01）**
>
> | 件 | 结果 | 对比 |
> |---|---|---|
> | `--no-incremental` 全量构建 | **0 error · 警告 7** | = 基线持平（首轮 8，新代码 1 条 CS8602 当轮修除） |
> | `ezt selftest` | **223 / 0** | 219 → 223（+4）；25.x 全绿 |
> | 静态守卫 `--selftest` | **23 / 0** | 21 → 23（+2，G7 双向突变） |
> | 静态守卫扫本仓 | **FAIL=0 · WARN=3 · EXEMPT=48** · **G7 = 0 命中** | WARN 3 条为**既有**（见落地偏差④） |
> | `verify-desktop` | **181 / 0**（rc=0） | 总断言数 181 **不变**；**本次未改 `verify-desktop.py` 一行** ⇒ search 段断言与今天逐字相同且全绿 |
> | `acceptance` | **通过 446 / 失败 0 / 跳过 1（满额 447）** · rc=0 | 与基线**逐项一致**（447 总断言 / 446 通过 / 1 条环境依赖 SKIP） |
> | `check-doc-refs --strict` | **exit 0**（悬空 0 / 0） | 删类后文档里的旧路径引用已按「—— 已删除」标记修正 |
>
> **★ 一次环境阻塞的完整定位过程（值得留档：它几乎被误判成代码回归）**
> - **现象**：首轮 `verify-desktop` **170/10/1**、`acceptance` **432/13/2** —— 10~12 条红全部集中在热键**注册**：`--probe-host-settings` 的 unset 恢复断言（`registered: False`）、OCR / 截图 / 取色真按键探针（`热键未注册`）、以及一条 `统计文本` 的 `code=6`（= `RunProbeHotkey` 的"该命令没有已注册热键项"）。
> - **第一步（归因）**：跑项目自带的 `scripts/probe-hotkey-free.py` ⇒ `Ctrl+Alt+A/C/F/L/O/R/T/W/X/Z` **10 键被占用**（`GetLastError=1409`）；且 `verify-desktop` 自己的三态判定也报「注册 6/8…已被别的进程占用」。**结论：环境冲突，与 W7-a 无关**（热键注册/仲裁路径一字未改；`SearchWindow` 的改动不可能让 `RegisterHotKey` 失败）。
> - **第二步（定位）**：先怀疑并临时退出 `Microsoft.CmdPal.UI.exe`（Windows 命令面板）+ `PowerToys.exe` —— 占用从 **10 键降到 3 键**（A/F/L/R/T/W/Z 释放，O/X/C 仍占）。**注意：释放不是立刻的**（进程退出后头一次复探仍报占用，约 1~2 分钟后才释放）—— 差点因此把 CmdPal 判成"无关"。
> - **第三步（闭环）**：待 `Ctrl+Alt+O/X/C` 释放后复跑 ⇒ `verify-desktop` **181/0**、`acceptance` **446/0/1** 全绿，**门槛达成且与基线数字逐项一致**。
> - **判据纪律（写在这里防下次误判）**：热键类验收失败**先跑 `probe-hotkey-free.py`**，别从代码侧找原因 —— 该脚本自己就会打印"这是**环境冲突**，不是代码回归 —— 别去改 `RegisterHotKey` 调用"。
> - **W7-a 相关断言（全程全绿，与环境无关）**：`--probe-search-ui`（虚拟化/高亮/计数/卷清单/暂停徽标/漂移提示）· `--probe-search-summon`（唤出循环 4 轮 + 焦点 + X 拦截）· live 探针（未就绪文案 + **lei/LEI 大小写折叠**）· 渲染与首屏耗时 —— **search 段断言一行未改且全 PASS**，这就是"文件项零行为"（FR-10）的机器证据。
> - 🔵 **顺带发现的验证设计缺口（建议后续修，不在本波）**：热键占用在**聚合断言**上正确走"跳过并落计数"，但**逐条探针断言**（OCR/取色/截图真按键）仍是硬失败 —— 同一环境原因在两处得到不同处置（本次 10 条红里 8 条属此类）。修法 = 让逐条探针也走三态判定（占用 ⇒ SKIP 落数字）**并补一条突变用例证明"非占用时的失败仍判红"**。同 W6-c 的教训：**断言必须区分"链路坏了"与"环境在动"**。
>
> **实施期偏差登记（4 条，均按授权自主决策）**
>
> ① **`SearchSession` 删除，而非保留委托壳**（原计划：保留壳 + 8 例 selftest 一行不改）。落地时发现：窗口改走 router 后该壳在**生产路径上零调用者** ⇒ 保留它 = 幽灵代码（审查规范 §3.9 G4 家族），且会留下两份"看起来都该用"的会话对象。**改判**：三层收敛**平移**进 `QueryPump`、单次查询 + epoch 过期闸 + 错误分层**平移**进 `FilesProvider`；原 5 条用例**语义平移**进 25.x（同一判据、同一断言内容，仅被测类型变），另新增 4 条把 §10.4 的 I2/I5/I7/I8 与 §10.5 的映射表变成机器判定。**行为等价的证据链** = 平移式抽取 + 同用例换类型全绿 + search 探针断言零迁移全 PASS + 构建警告持平 —— 比"保留一个没人用的壳"更强。
>
> ② **`LauncherRenderModel` 增两个标志 + 空查询也推进代次**。①模型原本只有 `FilesAccepted`，落地时发现"过期丢弃 ⇒ 今天连**列表选中态**都不动"，统一重绘会把选中态重置 ⇒ 拆成 `AnyAccepted`（是否重绘列表）与 `FilesAccepted`（是否更新状态行 files 部分）。②**空查询也推进代次** —— 今天"清空输入框后又被在途结果铺满"是个 bug（输入框空着却显示上一个词的文件），W7-a 顺带修掉。这是 W7-a **唯一一处有意的行为变化**，已记入 §10.4 注 2b。
>
> ③ **`LauncherPrefs` + `launcher.providers` schema 键推迟到 W7-b**。W7-a 只有 files 一个 provider ⇒ 该键此刻**真的没有作用**（配成 `apps`/`calc` 只会得到一个空启动器）；此时加键 = 名存实亡 flag（踩坑全集 §2.23），未被消费的解析类 = 幽灵代码。推迟到 apps 落地（W7-b）后与 §10.10 的解析用例、`--probe-host-settings` 扩展一并交付。
>
> ④ **未顺手修既有 WARN=3**：`src/Eztools.Desktop/CaptureOverlayProbe.cs` / `PickOverlayProbe.cs` 的注释-only catch（与 HEAD 逐字节一致 ⇒ **非本次引入**，属 W6 遗留）。改法是每处加一行 `// review-guards:allow-empty-catch :: <理由>`（把"刻意沉默"变成机器可见的声明）。**不在本波范围内顺手改**（会动 W6 交付物与 EXEMPT 计数），登记为待办。

> ### ✅ W7-b 落地记录（2026-10-01 · **完成，三件套全绿**）
>
> **产出（代码）**
> - **Host 纯逻辑**（`（新建）src/Eztools.Host/Launcher/`）：`FuzzyMatcher.cs`（完全相等/前缀/词边界/子串/子序列五档 + 分词 AND + 高亮区间）· `LauncherPrefs.cs`（`Parse`（字符串）/ `ParseNode`（JSON 节点，区分"键缺失"与"类型错"）/ `FromConfig`）
> - **Desktop 平台层**（`（新建）src/Eztools.Desktop/Launcher/`）：`AppIndexCache.cs`（**根可注入**、后台扫描、指纹失效、10 分钟兜底重扫、三态：扫描中/就绪/读不到）· `AppsProvider.cs`（S1~S5 源 + 去噪 + 本地模糊匹配 + Top-N）· `IconCache.cs`（`SHGetFileInfo` → `BitmapSource` + `DestroyIcon` try/finally + LRU 512 + 缓存命中计数）· `LauncherRowText.cs`（徽标 + 高亮主行 + 副行 + 图标，含探针读数）· `LauncherRowTemplateSelector.cs`（`Kind==File → HitText` 原样复用 / 其余 → 新控件）· `LauncherActionRunner.cs`（五种动作 + 失败文案三分支）· `LauncherUiProbe.cs`（`--probe-launcher` 五模式）
> - `src/Eztools.Host/Launcher/ILauncherProvider.cs`：白名单随实现增长（`KnownIds`/`DefaultEnabled` = `files,apps`）；新增 `LauncherProviderSet.Build(prefs, factories)`（**按配置装配** —— 未启用者根本不进集合）
> - `src/Eztools.Host/Launcher/QueryPump.cs`：新增 `Requery()`（延迟就绪来源的补发入口，走同一套节流/单在途，不新造路径）
> - `src/Eztools.Host/Config/HostSettingsSchema.cs`：`launcher.providers` 键（默认 `files,apps`，`x-order 12`）
> - `src/Eztools.Desktop/SearchWindow.cs`：模板改 `LauncherRowTemplateSelector`；动作交给 `LauncherActionRunner`；`ILauncherReadyNotifier` → `Requery()`；**新增 `startupWarning` 构造参数 + 首次显示时状态行报一次**；新增渲染日志探针钩子
> - `src/Eztools.Desktop/TrayApplication.cs`：`LauncherPrefs.FromConfig` → `LauncherProviderSet.Build`；配置非法时**日志 + 状态行**双出口
> - `src/Eztools.Desktop/DesktopOptions.cs` / `TrayApplication.cs`：`--probe-launcher <mode>` 五模式转发
> - `src/Eztools.Cli/SelfTestCommand.cs`：新增 **W7-1~W7-13**（providers 解析 8 + 模糊匹配 4 + Requery 1）
> - `scripts/verify-desktop.py`：launcher 段 **26 条**断言（首轮 22 + 代次闸/配置告警 4）（含代次闸上屏级证据、配置告警出口）；**search 段一行未改**
> - `scripts/acceptance.sh`：selftest 下限 188 → **201**；W7-x 下限 12 → **13**
> - `src/Eztools.Desktop/CaptureOverlayProbe.cs` / `PickOverlayProbe.cs`：W7-a 登记的 3 条注释-only catch WARN 补 `// review-guards:allow-empty-catch :: <理由>`（把"刻意沉默"变成机器可见的声明）
>
> **回归实测（2026-10-01）**
>
> | 件 | 结果 | 对比 |
> |---|---|---|
> | `--no-incremental` 全量构建 | **0 error · 警告 7** | = 基线持平 |
> | `ezt selftest` | **236 / 0** | 223 → 236（+13：W7-1~W7-13） |
> | 静态守卫 `--selftest` | **23 / 0** | 持平（G7 双向突变不变） |
> | 静态守卫扫本仓 | **FAIL=0 · WARN=0 · EXEMPT=53** | WARN 3 → **0**（W6 遗留清零），EXEMPT 48 → 53（+5 显式豁免声明） |
> | `verify-desktop` | **203 / 0** → 最终 **207 / 0**（rc=0） | 181 → 207（+26）；**search 段断言一行未改** |
> | `acceptance` | **通过 472 / 失败 0 / 跳过 1（满额 473）· rc=0** | 满额 447 → 473（+26）；与 search 段"逐字未改"并行不悖（新增全在 launcher 段） |
> | `check-doc-refs --strict` | **exit 0** | — |
>
> **★ 一条值得留档的假失败：验收期间改源码**
> - 现象：`acceptance` red 1 条 —— `宿主源码目录未被本次新增工具改动（期望 0，实际 1）`。
> - 归因（**不是回归**）：该断言是 `find "$REPO/src" -newer "$WORK/list.json" -name '*.cs'`，
>   而我在验收**运行期间**改了 `AppIndexCache.cs`（一处溢出修正）⇒ 正好命中 1 个文件。
> - **纪律**：`acceptance.sh` 的这条探针把"跑测期间不该动 src"变成了机器约束 —— 验收开跑后**不要再改 `src/`**；
>   `docs/`/`.workbuddy/` 不在其扫描面内，可以照常改。
>
> **实施期偏差登记（5 条，均按授权自主决策）**
>
> ① **`--apps` 改成 `--probe-launcher <mode>` 五模式 + `LauncherUiProbe` 落 `Launcher/` 子目录**。原 §10.12 写的是 `--probe-launcher --apps` 形态。落地改为**位置参数 `<mode>`**（与既有 `--probe-search-ui` 等开关同风格：`--xxx` 不接值的都是布尔开关，接值的一律 `--xxx <v>`）并拆五个模式：`rows`/`actions`/`isolation`/`router`/`apps`（+ `all`）。多出一个 **`actions`** 模式是把原计划的 calc 动作断言（Enter/Ctrl+C 复制与收窗语义）提前用**假来源**钉住 —— 这些语义与 provider 无关，属 W7-b 可测范围，故不留到 W7-c。
>
> ② **`--router` 的用例重新设计为"清空输入框推进代次"**（原计划：注入慢 300ms/快 0ms 两个 provider，连打两次）。复核后原方案**有写出恒真断言的风险**：泵是**严格单在途**的，在途批次之间不会交叠，"连打两次"只会得到先后两个完整批次 ⇒ 断言"最终代次 = 第 2 次"被上游结构直接保证（审查规范③「判据不得与上游保证冲突」）。改为攻**唯一可达**的路径：`Kick("")` 走 `OnEmptyText` 零往返支路，**它也推进代次** ⇒ 在途的 gen=1 带着过期代次回来必须整体丢弃。判据记在**"真正写进列表"的渲染日志**（非"router 发布了什么"）上，且用 `staleEverRendered`（**从不少于任何一次渲染**）而非"最后一次渲染里没有"。
> - **突变验证（已做）**：临时摘掉 `QueryRouter` 的代次闸 ⇒ 探针渲染日志多出 `1|201|陈旧结果`、`itemsOnScreen=201` ⇒ 断言变红。**不是恒真断言。**
>
> ③ **新增"启动期配置告警"的可见出口**。落地时发现 `TrayApplication` 的注释声称"唤出搜索窗时状态行还会再报一次"，但**代码里没有这条路**（`SearchWindow` 压根拿不到 `configError`）——注释在描述一个不存在的出口，正是审查规范 §3.3⑤ 反模式（"我加了 Console.WriteLine"当修复证据）。**补齐**：`SearchWindow` 增可选 `startupWarning` 参数，首次显示（`Summon` 与 `ShowForProbe` 同路径）在状态行报一次；探针传 `null` ⇒ 与 W7 之前逐字一致（FR-10 不受影响）。对应 verify-desktop 两条断言（可见 + 结果可覆盖，不形成常驻噪音）。
>
> ④ **`KnownIds` 不预埋未实现的 id**（白名单 = **当前已实现**）。配 `calc`/`unit`/`encode` 一律走"未知 provider"报错（文案自我解释：`已知：files、apps`），而不是"配置合法但功能不存在"的静默缺席（S2 家族）。schema 的 `description` 同步**只列已实现来源** —— 在描述里预先许愿会让用户得到"配置里写了、功能却不存在"。
>
> ⑤ **`AppIndexCache` 的根可注入 + 兜底间隔的比较写法**。①根注入是探针确定性的**前提**（§10.6 A 明写"探针可注入替换全部根"）；②`due` 判据里 `_lastScanMs == long.MinValue` 写成**显式分支**而非常量参与减法 —— `now - long.MinValue` 在 unchecked 下溢出成负数，会让兜底重扫被静默跳过（首扫前那一段窗口正好命中）。
>
> **★ 顺带闭环：W7-a 遗留的 3 条 WARN 与"逐条热键三态"**
> - WARN 3 → **0**：三处注释-only catch 补 `review-guards:allow-empty-catch :: <理由>`（不删 catch —— 它们确实无出口可去，缺的是**机器可见的声明**）。
> - 逐条热键探针改用三态判定：被占用 ⇒ **SKIP 并落计数**（与聚合断言同口径），并补 `hotkey_selftest` 5 例双向突变（含"探针挂掉 ⇒ 按失败处理，不许静默放过"）。**修的是 W6-c 同族验证缺口**：断言必须区分"链路坏了"与"环境在动"。

> ### ✅ W7-c 落地记录（2026-10-01 · **完成，三件套全绿**）
>
> **产出（代码）**
> - **`（新建）src/Eztools.Host/Launcher/CalcExpression.cs`**（纯函数，~430 行）：两层门槛（`TryGate` 字符层 / 解析层）· 递归下降解析器（EBNF 逐条落地，**显式错误状态短路、不用异常做控制流**）· 求值错误码（除零/溢出/未定义，逐算子检查）· 数值格式化（整数/科学记数/10 位去零，恒 `InvariantCulture`）· **长度上限 512 + 深度上限 64**
> - **`（新建）src/Eztools.Host/Launcher/CalcProvider.cs`**：`IsReady` 恒真 · 静默 ⇒ 空段且无错误 · 出行 ⇒ 一行（成功=复制格式化值 / 求值错=动作禁用）
> - `src/Eztools.Host/Launcher/ILauncherProvider.cs`：`KnownIds`/`DefaultEnabled` → `[files, apps, calc]`
> - `src/Eztools.Host/Config/HostSettingsSchema.cs`：默认值 `files,apps,calc` + 描述补回 `calc`（**已实现才写进文案**）
> - `src/Eztools.Desktop/TrayApplication.cs`：加 calc 工厂（装配表 +1 行）
> - `src/Eztools.Desktop/Launcher/LauncherUiProbe.cs`：加 `calc` 模式（真 provider + 真窗口 + 真动作分派）
> - `src/Eztools.Desktop/SearchWindow.cs`：加 `ProbeLeadingKinds`（"置顶"读数 —— 只读 `SnapshotRows` 拿不到"第几条"）
> - `src/Eztools.Desktop/DesktopOptions.cs`：探针模式文档 +1 条
> - `src/Eztools.Cli/SelfTestCommand.cs`：新增 **34.1~34.17**（精确值 ≥35 例 + 错误码表 + 格式化边界 + 两层门槛 + 长度/深度上限 + 结果行构成 + 注册表 + provider 行为）
> - `scripts/verify-desktop.py`：calc 段 **+8 条**；`scripts/acceptance.sh`：selftest 下限 201 → **218** + 新增 **34.x 计数守卫**（≥17）
>
> **回归实测（2026-10-01）**
>
> | 件 | 结果 | 对比 |
> |---|---|---|
> | `--no-incremental` 全量构建 | **0 error · 警告 7** | = 基线持平 |
> | `ezt selftest` | **253 / 0**（34.x ×17 全绿） | 236 → 253（+17） |
> | 静态守卫 `--selftest` | **23 / 0** | 持平 |
> | 静态守卫扫本仓 | **FAIL=0 · WARN=0 · EXEMPT=53** | 持平 |
> | `verify-desktop` | **215 / 0**（rc=0） | 207 → 215（+8，**search 段仍一行未改**） |
> | `acceptance` | **通过 480 / 失败 0 / 跳过 1（满额 481）· rc=0** | 满额 473 → 481（+8） |
> | `check-doc-refs --strict` | **exit 0** | — |
>
> **实施期方案调整（4 条，均按授权自主决策）**
>
> ① **EBNF 放宽一处：允许前导小数点的 `.5`**（设计稿原文 `digit+ ('.' digit*)?`，即必须数字开头）。理由：`.5+1` 是自然输入；语法上**无歧义**（没有别的构造以 `.` 开头）；静默吞掉会被当成 bug。已同步更新 §10.7 B 的 EBNF（`(digit+ ('.' digit*)? | '.' digit+)`）。`1.2.3` 依旧语法错。
>
> ② **明确了"L0 允许科学记数的 `e`/`E`"与"指数里的符号不算运算符"两条口径**（§10.7 A′ ①②）。设计稿的 L0 字符集只列了 `0-9 . + - * / % ^ ( )`，但 §11.3 C-4 要求 `1e5` 合法 —— 不放行 `e` 就自相矛盾；而不排除指数符号，`1e+5` 会突然开始出结果、与 C-2"纯数字不出结果"自相矛盾（一条口径被开后门）。
>
> ③ **`1e5` 的 UI 行为写死为"不出行"**：它是**纯数字** ⇒ 与 `2026` 同判。§11.3 C-4 的"合法"指**解析层**不把它当非法字符（selftest 用 `ParseAndEvaluate` 直测 `1e5 == 100000`）。两层各说各的真话 —— 已在 §10.7 A′ 注与 §11.3 C-4 明写，避免以后被当成 bug 反复"修"。
>
> ④ **错误行的主行/副行分工写死**：主行 = 去空白的原表达式、副行 = 错误文案（成功行是主行 = 值、副行 = `= 表达式`）。设计稿 C 表把错误文案列在"结果行**副标题**"，此处按字面落地；两种行结构一致（主行 = "这东西是什么"，副行 = "细节"）。
>
> **★ 一次"弱断言"自查（写在断言里，防止以后退化）**
> - 静默类用例（`2026` / `report-2026.txt` / `1+`）如果只断"没有 calc 行"，那**"整轮查询压根没跑"也满足它** —— 探针因此同时回传"文件段照常 200 条"，并在 verify-desktop 里两条一起断言。同 §10.12 的"定长泵 + 正向对照"纪律。
> - selftest 34.12 初版把 `1+` 断言成 `NotAnExpression` ⇒ **当场变红**（实际是 `Syntax`）。这恰好证明了两层静默**必须分开断言**：L0 未过与语法不成立对用户都是静默，但语义不同。已改成三组分别断言（放行 / 门槛静默 / 语法静默），并把 `1.2.3` 归到"门槛静默"（它没有运算符，字符层就挡掉了；解析器层的 `Syntax` 由 34.10 单独钉住）。

> ### ✅ W7-d 落地记录（2026-10-01 · **完成，三件套全绿**）
>
> **产出（代码）**
> - **`（新建）src/Eztools.Host/Launcher/UnitTable.cs`**（纯函数）：四类单位表（长度/重量/数据量/温度，`Data` 带**两制式**标记）· `TryParse`（数字 + 单位 + 可选触发词，**整串必须被消费**）· `Convert`（线性乘除 / **温度走仿射单独路径**）· `CommonOf`（无触发词的常用单位集）
> - **`（新建）src/Eztools.Host/Launcher/UnitProvider.cs`**：带触发词 ⇒ 一行精确结果；无触发词 ⇒ 列常用单位（**不含源单位**，Score 递减）；副行带数据量**制式说明**
> - **`（新建）src/Eztools.Host/Launcher/EncodeProvider.cs`**：六前缀（`b64:`/`b64d:`/`url:`/`urld:`/`u:`/`ud:`）+ 编解码纯函数；**非法输入 ⇒ 显式错误行 + 动作禁用**；残缺 `%XX` 自查（`Uri.UnescapeDataString` 自己不会报）；Unicode **按码点走**（emoji 不给半截码位）
> - `ILauncherProvider` 白名单 + `HostSettingsSchema` 默认值 → **`files,apps,calc,unit,encode`**（描述同步补齐）
> - `TrayApplication`：装配表 +2 行（unit/encode 工厂）
> - 探针加 **`unit` / `encode` / `config`** 三模式（`config` 读**真实**配置中心，是两层闭环的应用层观测面）
> - **回填**：calc 次动作按 §4.2 复制「表达式 = 结果」整串（W7-c 当时错写成回落主动作，见落地偏差③）
> - **新增** `SearchWindow.StartupWarningFor`（告警文案**单点出处**，托盘与探针共用）
> - selftest **35.1~35.18**；`verify-desktop` +14 条；acceptance 下限 218 → **236** + **35.x 计数守卫** + 步骤 8 新增**两层闭环四态**
>
> **回归实测（2026-10-01）**
>
> | 件 | 结果 | 对比 |
> |---|---|---|
> | `--no-incremental` 全量构建 | **0 error · 警告 7** | = 基线持平 |
> | `ezt selftest` | **271 / 0**（35.x ×18 全绿） | 253 → 271（+18） |
> | 静态守卫 `--selftest` / 扫本仓 | **23/0** · **FAIL=0 · WARN=0 · EXEMPT=53** | 持平 |
> | `verify-desktop` | **229 / 0**（rc=0，**连跑 3 次全绿**） | 215 → 229（+14，**search 段一行未改**） |
> | `acceptance` | **通过 498 / 失败 0 / 跳过 1（满额 499）· rc=0**；步骤 8 两层闭环四态全绿 | 481 → 499（+18） |
> | `check-doc-refs --strict` | **exit 0** | — |
>
> **实施期方案调整（4 条 + 2 处探针修正，均按授权自主决策）**
>
> ① **无触发词的常用单位列表不含源单位**（设计稿列表含它）。源单位那一行是恒等变换（`10 km = 10 km`），零信息纯噪声。**重量类别随之扩到 4 个**（kg/lb/oz/g，设计稿只写 kg/lb）—— 排除源单位后只剩 1 行太薄。已写进 §10.8 B′。
>
> ② **calc 次动作回填（§4.2 的字面要求）**：`Ctrl+Enter` 应复制「表达式 = 结果」整串，W7-c 当时写成 `SecondaryAction = null`（回落主动作）—— 那是**规格偏差**，本轮修正（`1+2` ⇒ 复制 `1+2 = 3`），并加 35.18 + 探针断言钉住。
>
> ③ **告警文案抽成单点**：`SearchWindow.StartupWarningFor(configError)`。托盘装配与探针共用一份 —— 两处各写一句"配置无效"，早晚漂移成两种说法。顺带把"有错 ⇒ 有告警、无错 ⇒ 无告警"变成机器可断言。
>
> ④ **encode 错误行主行 = 错误文案、副行 = 方向说明**（与 calc 错误行的"副行 = 错误文案"**不对称**）。这不是疏忽：§4.3 规定 Encode 副行恒为方向说明、§10.7 C 表规定 calc 副行是错误文案 —— 两份字面规格各自成立。已写进 §10.8 B′，若要统一是 5 分钟改动，需菲比一句话。
>
> **★ 两处探针修正（都在 `LauncherUiProbe`，都是本轮实测抓到的真 bug）**
>
> - **就绪信号不能用"行数 > 0"**（W7-c 遗留）：上一轮的行还在列表里，"行数 > 0"在新一轮渲染落地**之前**就已成立 ⇒ 读到**陈旧行**。实测两形态：`1GB to MiB` 读回上一条 `-40 °F`；calc `1+2` 选中 `1/0` 的禁用动作行 ⇒ 复制为空、窗没收 ⇒ 假红。修法 = 一律用**渲染日志**当就绪信号（`ProbeStartRenderLog` 清空后"涨了"即本轮落地）。
> - **剪贴板断言必须清空 + 有限重试**（`ProbeCopy`）：OLE 剪贴板在自动化环境里偶发被别的进程短暂占住（`OpenClipboard` 失败），与"复制了**错的**内容"是两种失败 —— 前者重试即可，后者重试也没用。**清空是前置哨兵**：复制失败时读到空串（响亮失败），而不是上一轮的残留值（假绿）。修完 verify-desktop **连跑 3 次全绿**（此前 4 次里红了 2 次，且两次红的是**不同**断言 —— 典型的环境噪声指纹）。


> ### ✅ W7-e 落地记录（2026-10-01 · **完成，三件套全绿，W7 全波收口**）
>
> **代码**：`（新建）src/Eztools.Host/Launcher/LauncherUsageStore.cs`（存储 + `Boost` 纯函数 +
> 临时文件原子写 + 2s 去抖后台落盘 + **有界 flush ≤500ms**）；`LauncherPrefs` 加 `UsageEnabled`
> 字段（W7-d 当初去参数的理由——"名存实亡"——随 `launcher.usage` 键落地自然解除）+ `ParseUsage`
> + `（新建）LauncherAliases`（同文件）；`AppsProvider` 接入频次/别名加成；`SearchWindow.UsageRecorder`
> 出口（**探针默认 null ⇒ 零写入**，FR-10 装配纪律）；`TrayApplication` 装配 + Dispose 有界 flush；
> `HostSettingsSchema` 加 `launcher.usage` / `launcher.alias`；`EztoolsPaths.LauncherDataDir`；
> 探针加 `usage` 模式（**十模式**）+ `config` 模式扩展 usage/alias 观测。
>
> **回归实测（最终）**：构建 **0 error / 7 警告 = 基线**（中途一度 8 警告 —— 修 `_loaded` 死字段后回基线）·
> selftest **281/0**（271→281，+36.1~36.10）· 守卫 **FAIL=0/WARN=0/EXEMPT=56** ·
> verify-desktop **233/0**（229→233，**search 段一行未改**，连跑 3 次）·
> acceptance 满额 rc=0（下限 236→281 + 36.x 守卫 + 8.5c 两层闭环四态）·
> `check-doc-refs --strict` **exit 0**。
>
> **落地偏差（4 条）**
>
> 1. **频次只记 apps 段**：identity 规格表里有 File/Calc/Unit/Encode，但它们的加成**没有消费者**
>    （files 归索引进程；calc/unit/encode 每轮至多一行）⇒ 记了没人读 = 垃圾数据 + usage.json 无界增长。
>    `LauncherUsageStore` API 按 `providerId` 泛化，将来加消费者时按 §10.11 identity 规格扩展即可。
>    注②已从"待确认"改判为落地口径。
> 2. **`launcher.alias` 格式与匹配语义补齐**（设计方案只写了"命中 ⇒ +300"）：
>    格式 = 「别名=目标」分号/换行分隔；目标 = **应用标题或全路径、不区分大小写全等**
>    （"包含"会误伤 VSCodium 一族）；缺等号 ⇒ 报错回落空表（告警文案独立成句 ——
>    不能套"已回落默认来源"前缀，那话说的是 providers）。
> 3. **别名与频次取 max 不叠加**：叠加会把 400+300 顶穿成"必第一"。别名命中且标题未命中也算命中，
>    但**高亮为空**（高亮区间是标题里的字符位，别名匹配的区间对不上标题，硬套会加粗错位）。
> 4. **动作失败不记频次**：只有 `LauncherActionRunner.Execute` 成功（null 失败文案）才 count+1 ——
>    启动失败的目标不该被记住"常用"。
>
> 5. **（10-02 实机补丁）别名目标：全等 → 子串包含**。实机两条别名（`note=记事本`、`lei=雷声加速器`）
>    全部不命中：系统条目是文件名（`notepad.exe` / `leigod.exe.lnk`），与用户写的俗称无全等关系。
>    匹配放宽为 `title/path.Contains(target)`（OrdinalIgnoreCase）；selftest 36.9 同步改判。
>    ★ 同日另记**已知缺口（W8 候选）**：`launcher.alias` / `launcher.usage` 改完**需重启托盘才生效**
>    （搜索窗只在首次创建时读一次配置，运行中不重读）—— 用户实测踩到：usage=false 落盘后
>    usage.json 仍在增长。配置热生效待立项。
>
> **★ 一处真 bug（探针 usage 真链路当场抓到）**：`BoostFor` 原实现里 `!_loaded ⇒ return 0`，
> 而 `Record` 是先写内存、懒加载在后台追 ⇒ **"记完立刻查"这一轮自己看不到自己的分**
> （OneApp 压过 TwoApp ⇒ 断言红）。修法 = BoostFor 不看 `_loaded`（缺键本来就返回 0）；
> `_loaded` 字段随之成为死字段（CS0414 警告）⇒ 删除。**这正是 §2.35①"就绪信号"教训的变体：
> "还没加载完"与"没有数据"必须区分，而"内存已有但加载标志未翻"是第三种状态。**

> ### ✅ W7-e 补遗（2026-10-02 · 实机修复：usage store 脏闸，M4 假阳性根因）
>
> **实机现象**：M4 验收（关闭频次 ⇒ usage.json 修改时间不变）实机必失败——用户记完 T1 →
> 关闭开关 + 重启托盘 → 关闭期间零记录 → mtime ≠ T1。
>
> **根因**：`LauncherUsageStore` 没有 dirty 标志，`TakeSnapshot()` 只判 `Count == 0` ⇒
> **每次退出托盘，Dispose 的有界 flush 都无条件重写整个文件**（哪怕零新记录），
> mtime 被推到"托盘退出时刻"。详见 `docs/踩坑全集.md` §2.39。
>
> **修法**：`_dirty` 脏标志（Record 置位 / 快照取走清零 / 写盘失败置回）。selftest 新增 **36.11**
> （预置旧 mtime 判据），突变验证：恢复旧"无条件写"行为 ⇒ 36.11 恰好一红，其余全绿。
> 回归：构建 0 错/**7 警告 = 基线**（`--no-incremental`）· selftest **282/0** ·
> verify-desktop **233/0** · acceptance 满额（数字见 §6 尾注/当日日志）。

## 7. 风险表

| # | 风险 | 缓解 |
|---|---|---|
| R1 | 动稳定 SearchWindow | W7-a 独立 commit、零行为、全量回归门槛；不顺手重构 |
| R2 | 键盘链回归（最高危） | NFR-3 红线：输入框零改动；W3 手工清单 M2 抽查；探针 `ProbeLastVk`/`ProbeQueryFocused` 断言保留 |
| R3 | 异步回填竞态（慢 provider 旧结果覆盖新输入） | 查询代次 token 一票否决；provider 接口强制携带 |
| R4 | 图标提取句柄/位图泄漏 | try/finally + GetProcessHandleCount 斜率自测（RI-6 同款方法） |
| R5 | calc 嗅探误判（把文件名当表达式） | 严格语法门槛：纯运算符/数字/括号/空白才解析；含字母除科学记数外直接放弃；失败返回空 |
| R6 | apps 扫描卡 UI 线程 | 后台线程扫描 + 缓存；首扫未就绪时 provider 静默缺席（不发空结果也不阻塞） |
| R7 | 多 TFM 残留死产物（§2.31） | 每阶段验收核对二进制时间戳 + `$TFM` |
| R8 | 沙箱内进程枚举恒 0（§D3 坑） | 探针判据不依赖进程列表；用窗口存在性 + 日志 |
| **R9** | **三层收敛平移后行为漂移**（W7-a 最大隐患：`QueryPump` 抽走后节流/trailing/代次语义微变 ⇒ files 行为不再逐字节一致） | ① `QueryPump` 由**平移**（不是重写）得到 —— 逐行照抄原 `SearchSession` 的三层收敛；② 原 5 条用例**语义平移**进 25.x（同一判据、同一断言内容，仅被测类型变）+ 4 条新增覆盖 §10.4 不变量；③ 门槛 = 构建警告持平 + selftest 全绿 + search 探针断言零迁移且全 PASS（§6 落地偏差①记录了"保留委托壳"方案被否的原因） |
| **R10** | **apps 首扫与首次查询竞态**：首扫未完成时用户已打字 ⇒ 结果里没有应用，用户以为"搜不到" | `IsReady=false` 期间**静默缺席**（R6 口径），但**首扫完成后若窗口仍可见且文本未变**，主动补一次派发（否则用户要再敲一个字符才看到应用）—— 判据：`TextUnchangedSinceDispatch && Visible` ⇒ 补发；探针断言这条（§11.3 E-7） |
| **R11** | **图标缓存无上限**：开始菜单条目多（大型软件环境可达数千）⇒ 位图内存累积 | 缓存按路径去重 + **上限 512 条 LRU**；上限内命中率已足够（用户实际只翻前几十条），超限淘汰最旧；断言"连续渲染 2000 条不同路径后缓存条目数 ≤ 512"（纯函数/内部计数探针） |
| **R12** | **`launcher.providers` 手改非法值**静默降级 | §5 解析表 + §11.6：报错 + 状态行一次性告警 + 日志（禁 `:-0` 式静默） |
| **R13** | **探针环境依赖假红**：apps 命中打破 `itemsCount == 200` | §5「探针装配纪律」：探针注入"仅 files"集合 |
| **R14** | **files 段被重排**（归并排序顺手把 files 也按 Score 排了）⇒ FR-2/FR-10 破 | 归并规则写死"files 段原序透传"（§10.4 规则 3）；探针断言 `firstPath` 与索引回传首条一致（既有断言即覆盖，**不许改**） |

## 8. 决策与拍板记录（D1~D11）

> ### ✅ 拍板结论（2026-10-01 · 菲比：**全部按推荐采用**）
>
> | # | 拍板结果 | 落地阶段 | 备注 |
> |---|---|---|---|
> | D1 | **A** SearchWindow 进化（同窗同热键） | W7-a | |
> | D2 | **A** files/apps/calc 先行，unit/encode 第二批 | W7-a→W7-d | |
> | D3 | **A** 自写递归下降解析器（零 NuGet） | W7-c | |
> | D4 | **A** calc 自动嗅探 + 严格语法门槛 | W7-c | |
> | D5 | **A** Shell 提取真图标（句柄纪律按 RI-6 方法） | W7-b | |
> | D6 | **B JSON**（原推荐 A 已复议改判，理由见下「D6 复议」） | W7-e | 复议理由经复核成立，菲比采纳 |
> | D7 | **A** v1 不解析 .lnk | W7-b | 复活条件：同目标重复出现 / 需按目标名匹配 |
> | D8 | **A** apps 缓存 = 内存 + 目录指纹 + 10 分钟兜底 | W7-b | |
> | D9 | **A** encode 只认显式前缀（`b64:`/`b64d:`/`url:`/`urld:`/`u:`/`ud:`） | W7-d | |
> | D10 | **A** unit 触发词 `to`/`->`/`转` + 无触发词列同类常用单位 | W7-d | |
> | D11 | **A** 文件项**不加**类型徽标（保 W7-a 零行为证据） | W7-a | 文件来源靠"全路径副行"辨识 |
>
> **同时确认（写作期发现的三项口径）**：
> 1. **FR-8 收窄** —— 频次记忆只作用于 apps（及 W7-d 的 unit/encode）段；files 排序归索引进程，UI 侧不重排。
> 2. **calc 纯数字不出结果**（`2026` 不当表达式 → 静默），与 PowerToys 有意不同（本项目主职是搜文件）。
> 3. **`launcher.usage` 键不在 W7-a 加** —— 有消费者（W7-e 的 `LauncherUsageStore`）时才加 schema 键，避免"名存实亡 flag"（踩坑全集 §2.23）。
>
> **额外采纳的两项更优方案（本拍板一并执行）**：
> - **① G7 分层守卫机器化**：把 §12.5 的分层守卫落进 `scripts/review-guards.py`（Host/Launcher 不得引用 `System.Windows*`；W7 新代码不得引用 `Eztools.Core`），而不是只写在文档里靠自觉 —— 依据代码审查规范 §3.9.1「能机器化的检查必须机器化」。
> - **② `QueryPump` 抽取共用而非复制**：三层收敛（节流 / 单在途+trailing / 代次）**只有一份实现**，由 `SearchSession` 平移抽出为 `QueryPump`，router 与 files 链路共用（省掉一次"两份拷贝迟早漂移"，同 W6-a 抽基类的打法）。★ **实施期偏差**：原计划保留 `SearchSession` 作委托壳，落地时发现窗口改走 router 后它在生产路径上零调用者 ⇒ 会变成幽灵代码，故**改判为删除、逻辑平移**（详见 §6 落地偏差①）。
>
> **下方 D1~D11 选项表保留原样**（选项与推荐理由是可追溯的决策依据，不因拍板而删）。

| # | 决策点 | 选项 | 推荐 |
|---|---|---|---|
| D1 | 落位方式 | A. SearchWindow 进化（同窗同热键，provider 路由内嵌）/ B. 独立 LauncherWindow + 新热键 | **A**（一个入口 + 热键预算；风险用 W7-a 零行为 commit 对冲） |
| D2 | v1 provider 范围 | A. files/apps/calc 先行，unit/encode 第二批 / B. 五个一次做完 | **A**（每阶段过门禁；unit/encode 是纯函数随时能加） |
| D3 | 计算器实现 | A. 自写递归下降（零依赖，selftest 全覆盖）/ B. NCalc 等 NuGet | **A**（NFR-1 零新依赖；表达式语法小，自写 ~200 行，断言可控） |
| D4 | calc 触发方式 | A. 自动嗅探（命中即置顶结果行）/ B. `=` 前缀显式触发 | **A**（`128*3` 还要先打等号就输了；误判由 R5 语法门槛兜住） |
| D5 | apps 图标 | A. Shell 提取真图标（观感即启动器核心感知）/ B. v1 纯文字 + 类型徽标 | **A**（无图标的应用列表像 DOS；句柄纪律按 RI-6 方法管住） |
| D6 | 频次记忆存储 | A. SQLite（复用 W5-c 基建经验，新表）/ B. JSON 文件 | **A（原结论）**，但**建议复议为 B** —— 见下方「D6 复议」 |
| **D7** | **.lnk 是否解析目标** | A. v1 **不解析**：标题取文件名、启动靠 shell（`UseShellExecute`）、图标对 .lnk 直接 `SHGetFileInfo` / B. COM `IShellLinkW` 解析目标（同目标去重、别名更准、标题更准） | **A**（解析的价值全在"去重/别名/标题"，而 v1 的去重按**全路径**已够用；代价是 60 行 `[ComImport]` 声明 + 每个 .lnk 一次 COM 调用 + 一堆"目标不存在/指向 UWP"的边界；**复活条件**：用户反馈"同一应用出现多条"或需要按目标名匹配） |
| **D8** | **apps 缓存形态** | A. **内存缓存 + 目录指纹失效**（每次唤出后台比对指纹，变了才重扫；进程退出即丢）/ B. 落盘缓存（首次唤出秒开，代价 = 缓存版本管理 + 失效判断 + 磁盘残留） | **A**（一次全扫的成本在真机是**数十毫秒级**且发生在后台线程，用户感知不到；落盘缓存要付"缓存与真实不一致"这一类最难查的 bug，收益不成比例） |
| **D9** | **encode 触发方式** | A. **只认显式前缀**（`b64:` / `b64d:` / `url:` / `urld:` / `u:`）/ B. 自动嗅探（检测 base64 字符集 / `%XX` 序列） | **A**（算术语法是**强**特征所以 calc 可以自动嗅探；base64 是**弱**特征——`abcd`、`test`、任何 4 的倍数纯字母串都合法，自动嗅探必然误报，而误报的代价是**污染文件搜索结果**。前缀只多打 4 个字符） |
| **D10** | **unit 触发词** | A. `to` / `->` / `转` 三者等价；**不带触发词时列出该类别的 4~5 个常用单位**（如 `10km` → 显示 km/mi/m/ft/in 五行）/ B. 只认 `to`，不带就什么都不出 | **A**（"10km" 单独输入时用户十有八九想看别的单位；这行的成本是零，收益是把单位换算从"要记住语法"变成"随手可用"） |
| **D11** | **文件项是否加类型徽标** | A. **不加**（文件是默认形态；徽标只加非默认来源）/ B. 加（FR-6 字面全覆盖，代价 = files 渲染结构变化 ⇒ `--probe-search-ui` 断言必须迁移，W7-a 的"断言零改动"证据链断掉） | **A**（FR-6 的**目的**是"一眼可辨来源"，而 files 项有**全路径副行**这个更强的来源信号；B 的代价是拿 W7-a 最硬的回归证据去换一个视觉冗余项） |

### D6 复议（✅ 已采纳：改判 B JSON）

> 原推荐 A（SQLite）的理由是"并发写与规模都在 SQLite 侧已被验证过"。**v2 复核后认为该理由在本场景不成立**：

| 维度 | SQLite（A） | JSON（B） | 结论 |
|---|---|---|---|
| 并发写 | 需要（WAL） | **不需要** —— 桌面宿主是**单实例**（`src/Eztools.Desktop/SingleInstance.cs`），写入者唯一 | B 无劣势 |
| 规模 | 万级舒适 | 用户实际使用面 ≈ 几个 provider × 上百条 ≈ **千级以内**；全量读写 < 1ms | B 无劣势 |
| 原子写 | 内建 | 复用既有 **配置中心的原子写**模式（写临时文件 + `File.Replace`） | 打平 |
| 依赖 | 需给 `Eztools.Host` 新增 `Microsoft.Data.Sqlite` 包引用（包已在解决方案内，但**新增一条边**） | 零新依赖（`System.Text.Json` 已在用） | **B 优** |
| 复杂度 | schema 版本闸 + WAL + 迁移出口（W5 `HistoryStore` 那套完整机制） | 一个 `Dictionary<string,UsageEntry>` 的序列化 | **B 优** |
| 未来扩展 | 若要做"按查询词分桶 + 时间衰减 + 统计面板"，SQL 更顺 | 到那一步再迁（数据是纯增量、可丢弃） | A 略优（但属未立项需求） |

**结论（✅ 2026-10-01 拍板采纳）**：**B（JSON，`（新建）` 落 `<ConfigRoot>/launcher/usage.json`，写盘走原子写 + 2s 去抖 + 退出 flush）**。原「若坚持 A 则须登记新依赖边」的分支**不再需要**（不新增依赖边，NFR-1 口径零偏离）。

（注：D6 与其余 D 项的差异在于——**它是唯一一个"原推荐的理由经复核不成立"**的项，故单独列出复议过程，而不是悄悄改推荐。）

## 9. 验收策略

- **selftest**：calc 解析器（双向突变：合法表达式精确值 + 非法/边界响亮失败）；`launcher.providers` 白名单解析（合法/未知值/空值）；计数增量进总表。
- **verify-desktop**：`--probe-launcher` **新观测面**（§10.12 五模式）；`--probe-search-ui` / `--probe-search-summon` 断言**一行不改**（FR-10 机器证据）。
- **手工清单**：`W7-手工验收清单.md`（W7-e 建）——中文输入法输入、真机应用启动、图标显示、Esc/焦点、热键唤起（真按键）。
- **门禁**：W7-a 回归门槛不绿不开 W7-b；每阶段三件套跑满再进下一阶段；判据一律 FAIL=0。

### 9.1 判据口径（v2 补齐，防"引用旧数字对账"）

| 件 | 判据 | 数字口径 |
|---|---|---|
| `scripts/acceptance.sh` | **FAIL = 0** | 满额随新增断言变动 —— **引用数字前先看最近一次输出**，不拿旧数字对账（W7-b 满额 **473**） |
| `scripts/verify-desktop.py` | **FAIL = 0** | W7-a 阶段**断言数不变**（这是"零行为"的机器证据）；W7-b 起按块递增（181 → 207） |
| `ezt selftest` | **FAIL = 0** | W7-a：25.x 6 → 10；W7-b：+W7-1~W7-13 ⇒ 206 → 236 → 236；acceptance 用例数下限 184 → 188 → **201** |
| 编译 | **警告数 = 基线**（当前基线 7） | 必须 `--no-incremental` 量（增量会跳过未变更项目 ⇒ 假绿，坑位 S1） |
| `scripts/review-guards.py` | **FAIL = 0 / WARN = 0**（EXEMPT 计数允许变动） | 挂在 acceptance 第 20 步；W7-b 起 WARN 已清零，新写的"刻意沉默"必须带 `allow-empty-catch :: <理由>` |
| `scripts/check-doc-refs.py --strict` | **0 悬空** | 新文件路径必须带 `（新建）` 前缀，否则红 |
| W7-a 专属 | **`--probe-search-ui` / `--probe-search-summon` 的断言集合与今天逐字相同** | 用 `git diff scripts/verify-desktop.py` 只应看到"新增 launcher 段"，**看不到 search 段的改动**（这是 FR-10 的机器证据） |
| 验收期间纪律 | **开跑后不再改 `src/`** | `acceptance.sh` 有 `find src -newer <marker>` 探针，会判"验收期间改源码"为失败（W7-b 实测踩到，见 §6 落地记录） |

### 9.2 新增断言清单（v2 定名，实现期按此落地）

| 落点 | 断言（要点） | 阶段 | 状态 |
|---|---|---|---|
| selftest | `LauncherPrefs` 8 例（§5 解析表逐行，含 schema 默认值 × 白名单交叉断言） | W7-b | ✅ W7-1~W7-8 |
| selftest | `FuzzyMatcher` 4 例（分档序 / 分词 AND / 高亮区间落位 / 中文子序列） | W7-b | ✅ W7-9~W7-12 |
| selftest | `QueryPump.Requery`（空文本零动作 / 有文本用最新文本再派发） | W7-b | ✅ W7-13 |
| selftest | `QueryPump`：节流计数 / 单在途 trailing / 代次递增 / Reset 清待发（由 `SearchSession` 用例平移） | W7-a | ✅ 25.x |
| verify-desktop | `--probe-launcher router`：**代次闸上屏级证据** —— 清空输入框推进代次（唯一可达路径）⇒ 在途批次放行后**渲染日志条数不变**、陈旧标题**从不少于任何一次渲染** | W7-b | ✅ +突变验证 |
| verify-desktop | `--probe-launcher isolation`：必抛 provider ⇒ 其余段仍在 + 状态行含原因（FR-9）；**配置告警出口可见且被结果覆盖** | W7-b | ✅ |
| verify-desktop | `--probe-launcher apps`：临时扫描根 ⇒ 命中 / 递归 / `.url` / 去噪 / 启动动作构造（不真起进程）/ **句柄斜率 = 0** / 缓存上限 / 指纹重扫 / 首扫补发 | W7-b | ✅ |
| verify-desktop | `--probe-launcher rows`：五种 Kind 的徽标与主副行；**`LauncherRowText` 里一条 `File` 都没有**（FR-10 反向断言） | W7-b | ✅ |
| verify-desktop | `--probe-launcher actions`：Ctrl+C 不收窗 / Enter 收窗 / 文件行复制全路径 / 参数形状 / 失败文案三分支 | W7-b | ✅ |
| selftest | calc：合法表达式精确值表（≥20 例）+ 语法错/求值错错误码表 + 格式化边界（§10.7） | W7-c | ✅ 34.x ×17（内含 ≥35 例精确值） |
| verify-desktop | `--probe-launcher calc`：真链路（输入 → 行 → Enter 复制值 ⇒ 剪贴板 == 手算值） | W7-c | ✅ +8 条 |
| selftest | unit 换算表（每类别 ≥3 例双向）+ encode 双向（含非法输入响亮失败） | W7-d | ✅ 35.x ×18 |
| verify-desktop | `--probe-host-settings` 扩展：`launcher.providers` 合法/未知/空三态（协议层）+ CLI `ezt config get/set/unset` 闭环 | W7-d | ✅ 改为 `--probe-launcher config`（读真实配置中心）+ acceptance 步骤 8 两层闭环四态 |

## 10. 模块详解（接口 / 数据结构 / 流程）

### 10.1 文件清单与落位（v2 新增）

| # | 文件 | 落位 | 职责 | 新建/改 |
|---|---|---|---|---|
| 1 | `（新建）src/Eztools.Host/Launcher/LauncherContracts.cs` | Host | 纯数据契约：`LauncherKind` / `LauncherQuery` / `LauncherItem` / `LauncherAction` / `LauncherResultSet` / `LauncherError` | 新建 |
| 2 | `（新建）src/Eztools.Host/Launcher/ILauncherProvider.cs` | Host | provider 接口 + `LauncherProviderRegistry`（已知 id 集合 = 白名单唯一出处） | 新建 |
| 3 | `（新建）src/Eztools.Host/Launcher/QueryPump.cs` | Host | 三层收敛：节流 / 单在途+trailing / 代次。**从 `SearchSession` 平移抽出** | 新建 |
| 4 | `（新建）src/Eztools.Host/Launcher/QueryRouter.cs` | Host | 扇出 + 归并 + 排序 + 代次闸 + 故障隔离 + 渲染派发 | 新建 |
| 5 | `（新建）src/Eztools.Host/Launcher/FilesProvider.cs` | Host | 包装 `SearchIndexClient`（保 epoch 过期闸）→ `LauncherItem` | 新建 |
| 6 | `（新建）src/Eztools.Host/Launcher/CalcProvider.cs` | Host | 嗅探门槛 + 调解析器 + 组结果行 | 新建 |
| 7 | `（新建）src/Eztools.Host/Launcher/CalcExpression.cs` | Host | 词法 + 递归下降语法 + 求值 + 格式化 + 错误码（纯函数） | 新建 |
| 8 | `（新建）src/Eztools.Host/Launcher/FuzzyMatcher.cs` | Host | 子序列模糊匹配 + 打分 + 高亮区间（纯函数，apps/别名用；**files 不用**） | 新建 |
| 9 | `（新建）src/Eztools.Host/Launcher/LauncherPrefs.cs` | Host | `launcher.providers` / `launcher.usage` 解析（白名单 + 错误） | 新建 |
| 10 | `（新建）src/Eztools.Host/Launcher/UnitProvider.cs` + `（新建）src/Eztools.Host/Launcher/UnitTable.cs` | Host | 单位换算（W7-d） | 新建 |
| 11 | `（新建）src/Eztools.Host/Launcher/EncodeProvider.cs` | Host | 编码转换（W7-d） | 新建 |
| 12 | `（新建）src/Eztools.Host/Launcher/LauncherUsageStore.cs` | Host | 频次记忆读写（W7-e） | 新建 |
| 13 | `（新建）src/Eztools.Desktop/Launcher/AppsProvider.cs` | Desktop | 扫描 + 缓存 + 指纹失效 + 匹配（Shell/注册表） | 新建 |
| 13b | `（新建）src/Eztools.Desktop/Launcher/AppIndexCache.cs` | Desktop | 应用清单缓存：**根可注入**（探针确定性）、后台扫描、指纹失效、10 分钟兜底重扫、就绪通知（`ILauncherReadyNotifier`） | 新建 |
| 14 | `（新建）src/Eztools.Desktop/Launcher/IconCache.cs` | Desktop | `SHGetFileInfo` → `BitmapSource`，`DestroyIcon` try/finally，LRU 512 | 新建 |
| 15 | `（新建）src/Eztools.Desktop/Launcher/LauncherRowText.cs` | Desktop | 非文件项的渲染控件（徽标 + 标题 + 副标题 + 图标 + 探针读数） | 新建 |
| 16 | `（新建）src/Eztools.Desktop/Launcher/LauncherRowTemplateSelector.cs` | Desktop | `Kind==File → HitText`（**原样复用**）/ 其余 → `LauncherRowText` | 新建 |
| 17 | `（新建）src/Eztools.Desktop/Launcher/LauncherActionRunner.cs` | Desktop | 动作执行 + 失败文案（`Process.Start` / `explorer /select` 复用） | 新建 |
| 18 | `（新建）src/Eztools.Desktop/Launcher/LauncherUiProbe.cs` | Desktop | `--probe-launcher` 探针（与 `SearchUiProbe.cs` 同风格；落 `Launcher/` 以受 G7 之外的分层约定约束） | 新建 |
| 19 | 原名 `src/Eztools.Host/Search/SearchSession.cs` —— 已删除（W7-a 落地偏差①：逻辑**平移**进 `（新建）src/Eztools.Host/Launcher/QueryPump.cs` + `（新建）src/Eztools.Host/Launcher/FilesProvider.cs`；保留委托壳会变成生产路径零调用者的幽灵代码） | Host | 三层收敛 → `QueryPump`；单次查询+epoch 过期闸+错误分层 → `FilesProvider` | **删** |
| 20 | `src/Eztools.Desktop/SearchWindow.cs` | Desktop | 会话→router 接线；模板分流；动作分派；选中项读法（`FileHit`） | **改** |
| 21 | `src/Eztools.Desktop/TrayApplication.cs` | Desktop | provider 装配 + 托盘/气泡/selfcheck 文案 + 探针开关转发 | **改** |
| 22 | `src/Eztools.Desktop/DesktopOptions.cs` | Desktop | `--probe-launcher`（+ 子开关）| **改** |
| 23 | `src/Eztools.Host/Config/HostSettingsSchema.cs` | Host | 加 `launcher.providers`（+ `launcher.usage`） | **改** |
| 24 | `src/Eztools.Cli/SelfTestCommand.cs` | Cli | 新增断言段 | **改** |
| 25 | `scripts/verify-desktop.py` | 脚本 | 新增 launcher 段（**search 段不动**） | **改** |
| 26 | `src/Eztools.Desktop/NativeInputBox.cs` | Desktop | **一行不改**（NFR-3 / FR-11 红线） | **禁改** |
| 27 | `src/Eztools.Index/**`、`src/Eztools.Core/**` | — | 本波不碰（NFR-2；files 匹配排序仍是索引进程的职责） | **禁改** |

### 10.2 `ILauncherProvider`（接口定义）

```csharp
// （新建）src/Eztools.Host/Launcher/ILauncherProvider.cs

/// <summary>一个结果来源。契约条款见设计方案 §10.2 表。</summary>
public interface ILauncherProvider
{
    /// <summary>稳定 id（= 配置键 <c>launcher.providers</c> 里的取值）。必须属于 <see cref="LauncherProviderRegistry.KnownIds"/>。</summary>
    string Id { get; }

    /// <summary>用户可见的短名（段位错误文案用："应用索引暂不可用"）。★ 实施期新增 —— 错误文案要显示名，不是开发者 id。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 是否可参与本次查询。false ⇒ 本次**静默缺席**（不调用、不占段位、不进错误面）。
    /// 语义 = "还没准备好"（如 apps 首扫进行中），**不等于**"坏了"（坏了走 QueryAsync 抛异常 ⇒ 进错误面）。
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// 查询。<b>必须带 <paramref name="query"/>.Generation 回传</b>（由 router 校验，见 §10.4 不变量 I2）。
    /// 允许抛异常（router 负责隔离转成段位错误）；实现内部**不得**做 UI 侧工作。
    /// 线程纪律：可能在线程池线程被调用；实现内不得碰 Dispatcher/控件。
    /// </summary>
    Task<LauncherResultSet> QueryAsync(LauncherQuery query, CancellationToken ct);
}
```

**接口契约条款（实现者必须满足）**：

| # | 条款 | 违反后果 |
|---|---|---|
| C1 | **不做节流**：provider 被调用即应当真算（节流由 `QueryPump` 统一承担） | 双重防抖 ⇒ 结果比今天更慢（files 尤其致命） |
| C2 | **不做代次判断**：原样回传 `query.Generation`（判断在 router） | 判断散落多处 ⇒ 必然漂移 |
| C3 | **不 marshal UI**：只返回值，不碰 `Dispatcher` | 线程纪律破坏 + 探针不可测 |
| C4 | **不吞异常**：自己拿不准就让它抛（router 隔离并转成段位错误） | 静默失败（S2 家族） |
| C5 | **`IsReady=false` 必须是"暂不可用"而非"无结果"**：不得用它表达"这次没命中" | "没有结果"与"还没准备好"混同（S9 家族：显示的数字语义是错的） |
| C6 | **段内顺序由 provider 自己负责**，router 只做**段间**排序（files 段原序透传）；要参与段内加权（如频次）由 provider 自己做 | "谁排序"职责不清 ⇒ FR-10 破 |
| C7 | **不返回超过 `query.Limit` 条**；超限部分用 `Total` 表达 | 渲染 200+ 条无意义数据 |

**注册表（白名单唯一出处）**：

```csharp
public static class LauncherProviderRegistry
{
    public const string Files  = "files";
    public const string Apps   = "apps";
    public const string Calc   = "calc";
    public const string Unit   = "unit";     // W7-d 起进 KnownIds
    public const string Encode = "encode";   // W7-d 起进 KnownIds

    /// <summary>当前**已实现**的 provider id（= 配置白名单）。随阶段增长；错误文案从这里取"已知集合"。</summary>
    public static IReadOnlyList<string> KnownIds { get; } = [Files, Apps, Calc];

    /// <summary>默认启用集合（= schema default，两处必须一致 —— selftest 断言）。</summary>
    public static IReadOnlyList<string> DefaultEnabled { get; } = [Files, Apps, Calc];
}
```

> ★ **白名单 = `KnownIds`（随阶段增长）而不是"五个常量全列"**：`unit`/`encode` 在 W7-d 前被配置时，用户得到的是 `未知 provider "unit"（已知：files、apps、calc）`——**可读且自我解释**；若白名单预先含 `unit` 却无实现，用户会得到"配置合法但功能不存在"的静默缺席（S2 家族）。

### 10.3 数据结构（字段级）

```csharp
// （新建）src/Eztools.Host/Launcher/LauncherContracts.cs

/// <summary>结果来源类型（决定徽标、动作语义、渲染模板）。</summary>
public enum LauncherKind { File, App, Calc, Unit, Encode }

/// <summary>动作种类。Argument 的语义随 Kind 而定（见下表）。</summary>
public enum LauncherActionKind
{
    Open,        // 用 shell 打开（文件/URL）
    Reveal,      // 资源管理器定位（/select）
    Launch,      // 启动应用（可带工作目录）
    RevealApp,   // 定位应用目标（.lnk/exe）
    CopyText,    // 复制文本到剪贴板
}

/// <summary>一次查询请求（provider 收到的东西）。不可变。</summary>
public readonly record struct LauncherQuery(
    string Text,        // 归一后的查询文本（provider 自己决定是否 trim/lowercase）
    long Generation,    // 派发代次（必须原样回传，契约 C2）
    int Limit,          // 本 provider 的条数上限（由 router 按共享预算分配）
    bool Substr);       // 子串匹配开关（files 透传给 search.query；本地 provider 可忽略）

/// <summary>动作描述（可执行性由 Desktop 侧 Runner 解释）。</summary>
public sealed record LauncherAction(LauncherActionKind Kind, string Argument);

/// <summary>统一结果项。**File 项必须携带 FileHit**（FR-10：渲染走既有 HitText）。</summary>
public sealed record LauncherItem(
    LauncherKind Kind,
    string Title,
    string Subtitle,
    string IconHint,                                  // 供 IconCache 用；File/Calc/Unit/Encode 为 ""
    double Score,                                     // 段内排序分（files 段忽略）
    IReadOnlyList<(int Start, int Len)> Highlights,   // 仅 File 非空；FindHitText 探针依赖
    LauncherAction PrimaryAction,
    LauncherAction SecondaryAction,
    SearchHitDto? FileHit);                           // ★ 仅 Kind==File：原样携带索引回传命中

/// <summary>段位错误（FR-9：段位级故障必须可见）。</summary>
public sealed record LauncherError(LauncherErrorKind Kind, int Code, string UserText, string Detail);

public enum LauncherErrorKind
{
    IndexNotReady,     // -32001：显示"正在建索引"（**不是**错误，见 §11.1）
    IndexUnavailable,  // 索引进程不可用/超时/协议破坏
    ProviderFailed,    // provider 自抛异常（已捕获，附类型与消息）
    ConfigInvalid,     // 配置非法（回落默认 + 告警一次）
}

/// <summary>一个 provider 的一轮结果（含"被丢弃"这一态）。</summary>
public sealed record LauncherResultSet(
    long Generation,
    string ProviderId,
    IReadOnlyList<LauncherItem> Items,
    int Total,             // 全库命中总数（files 显示"共 N 条"用）
    int ElapsedMs,         // provider 自报耗时（files = 索引回传值）
    bool Dropped,          // true = 该结果被过期闸作废（router **不得**覆盖上一轮该段内容）
    LauncherError? Error);

/// <summary>渲染模型（router → 窗口的唯一下行通道）。</summary>
public sealed record LauncherRenderModel(
    long Generation,
    string QueryText,
    bool IsEmptyQuery,                            // 清空输入框：等价于今天的 Epoch==0 本地回调
    bool AnyAccepted,                             // ★ 实施期新增：本轮至少有一段被接受 ⇒ 才重绘列表
    bool FilesAccepted,                           // ★ 实施期新增：files 段被接受 ⇒ 才更新状态行的 files 部分
    IReadOnlyList<LauncherItem> Items,            // 已归并排序（files 段原序透传）
    IReadOnlyDictionary<string, LauncherError> Errors,   // 段位错误（FR-9）
    int FilesHitCount,                             // files 段的条数（= 状态行的"显示 N"）
    int FilesTotal,                                // files 段的 total（"共 M 条"）
    int FilesElapsedMs);                           // files 段耗时（"{n} ms"）
```

> ★ **实施期新增两个标志的理由（W7-a 落地偏差 ②）**：过期丢弃时今天**连列表选中态都不动**
> （`OnResults` 根本不触发）。若统一重绘，同样的内容会被清空重灌 —— 用户看不见差别，但
> **选中态被重置**。"是否重绘列表"与"是否更新状态行的 files 部分"是两件事，不能合并成一个标志。

**`LauncherAction.Argument` 语义表（写死，Runner 按此解释）**：

| Kind | Argument | 主动作 | 次动作 |
|---|---|---|---|
| File | 目标**全路径** | `Open` | `Reveal` |
| App | 目标全路径（.lnk/.exe） | `Launch` | `RevealApp` |
| Calc | 结果值字符串 | `CopyText` | `CopyText`（Argument = `表达式 = 结果`） |
| Unit | 结果值字符串 | `CopyText` | `CopyText`（`原式 = 结果`） |
| Encode | 转换结果字符串 | `CopyText` | `CopyText`（含方向说明） |

> **`Score` 的语义边界**：只用于**段内**排序（apps/unit/encode）。files 段的 `Score` 恒为 0 且被忽略（§7 R14）。

### 10.4 `QueryRouter`（流程与不变量）

**状态**：`_generation`（long，派发计数）· `_inFlight`（bool）· `_trailing`（bool）· `_latestText`（string）· `_providers`（`IReadOnlyList<ILauncherProvider>`，构造期定死）· `_lastSegments`（`Dictionary<string, LauncherResultSet>`，用于"段位保持"）。

**流程（伪代码，实现照此）**：

```
Submit(text, nowMs):                        // 输入变化（每次按键）
    _latestText = text
    if pump.Report("q", text) is delivered: Dispatch(delivered.Text)

Fire():                                     // 节流到点（DispatcherTimer）
    if pump.Fire() is delivered: Dispatch(delivered.Text)

Dispatch(text):
    gen = ++_generation                     // ★ 代次在**派发**时递增（注 1）
    if _inFlight: _trailing = true; return  // 单在途（收敛"打字快于响应"）
    _inFlight = true
    _ = Task.Run(() => RunBatchAsync(gen, text))

RunBatchAsync(gen, text):                    // 线程池线程
    if text == "":                          // 空查询：零 provider 调用（R4/既有语义）
        Render(gen, EmptyModel(text)); return
    active = _providers.Where(p => p.Enabled && p.IsReady).ToList()
    results = await Task.WhenAll(active.Select(p => SafeQuery(p, gen, text)))   // 故障隔离在此
    if gen != _generation:                  // 代次闸：迟到批次整体丢弃
        return
    Merge(results) → model                  // 段位保持（Dropped 段沿用上一轮）
    Render(gen, model)                      // UI 线程 marshal

SafeQuery(p, gen, text):                    // FR-9：单 provider 失败不影响其余
    try:  return await p.QueryAsync(q, ct)
    catch(ex): return LauncherResultSet(gen, p.Id, [], 0, 0, false, ProviderFailed(ex))
```

**不变量（改代码前必须全部成立）**：

| # | 不变量 | 依据 |
|---|---|---|
| I1 | 同一时刻**最多一个批次在途** | 收敛"打字快于响应"；也是 stdio 管道"不并发写 stdin"的前提 |
| I2 | 渲染**只接受** `model.Generation == _generation` 的批次；其余**整体丢弃且不产生任何渲染** | R3 一票否决 |
| I3 | `_trailing` 为真时，批次结束**必以"最新文本 + 当前代次"补发一次**（只补最末次，中间值没有观众） | 与 `SearchSession` 同语义 |
| I4 | 空文本 ⇒ **零 provider 调用** + 渲染 `IsEmptyQuery=true`（窗口据此显示既有占位文案，零往返） | 既有行为 |
| I5 | 任一 provider 抛异常 ⇒ 该段置空 + 该段进 `Errors`；**其余段照常渲染** | FR-9 |
| I6 | `Enabled=false` 与 `IsReady=false` **都不调用**，且都**不进 `Errors`**（"关掉"与"还没准备好"都不是故障） | 契约 C5 |
| I7 | 排序：段序 `pin(calc/unit/encode)` → `apps` → `files`；段内按 `Score` 降序，`Score` 相等按 `Title` 序（ordinal）保证**确定性** | 可断言 |
| I8 | files 段**原序透传、绝不重排** | R14 / FR-10 |
| I9 | 总条数 ≤ `Limit`（50）：pin 段先占（最多 3 条），剩余预算按段序分配 | 既有 `DefaultLimit` |
| I10 | `Dropped=true` 的段 ⇒ **沿用 `_lastSegments` 中该段的上一轮内容**（不清空、不覆盖） | 等价于今天"丢弃回调 ⇒ 界面不动"（§10.5 注 ③） |
| I11 | 归并结果中 `Kind==File` 的项**必须** `FileHit != null`（否则渲染层无法走既有模板） | FR-10 |
| I12 | 派发与渲染**都不在 UI 线程**做 IO；渲染 marshal 用 `Dispatcher.BeginInvoke`（非阻塞，不成环） | NFR-6 |

> **注 1：为什么代次在"派发"时递增而不是"Submit"时递增。**
> 若在 `Submit` 时递增：用户在批次在途期间打字（只置 `_trailing`，未派发）⇒ 在途批次返回时代次已不等 ⇒ 被丢弃 ⇒ **界面白等一轮**（要等 trailing 那一轮才出结果）。那正是今天 `SearchSession` 用 `IsStale(epoch)` 而不"在 Submit 时作废"的同一个道理（epoch 是**请求**签发的，不是按键签发的）。★ 这条是本模块最容易写错的一行。

> **注 2：`_lastSegments` 的清理时机。** 空查询（`IsEmptyQuery`）与文本变化时必须**清空**所有段位（否则"清空输入框后仍残留上一轮的应用列表"，与新文本的结果混搭）。规则：`text != _lastQueryText` ⇒ 先清 `_lastSegments`，再归并；`Dropped` 段的保持只在**同一文本**内有效。
>
> **注 2b：空查询也推进代次（★ 实施期确认的有意行为变化）。** 今天的行为是"清空输入框 → 立刻显示空态 → 但在途批次回来后又把结果铺上去"（`SearchSession` 的过期闸对空查询不生效，因为空查询不签发 epoch）—— 那是**一个 bug**：输入框是空的，却显示着上一个词的文件。W7-a 起空查询同样推进代次 ⇒ 在途批次被丢弃、空态稳定。这是 W7-a **唯一一处有意的行为变化**，已登记（§6 落地偏差②）。

> **注 3：段位保持只对 files 有意义。** 目前只有 files 会产出 `Dropped`（过期闸只存在于索引侧）；apps/calc 是本地即时计算，不存在"过期"。但规则**不特判**：任何 provider 报 `Dropped` 都走同一条"沿用上一轮"路径（避免为"现在只有一个"写死）。

### 10.5 `FilesProvider`（零行为包装）

**映射表（files 唯一的数据变换，逐字段对着写）**：

| 源（`SearchHitDto`，★ 不改） | 目标（`LauncherItem`） | 说明 |
|---|---|---|
| `Name` | `Title` | 原值 |
| `Path` | `Subtitle` | 原值（**UI 不拼路径** —— 既有红线） |
| `Highlights` | `Highlights` | 原值（UTF-16 code unit 口径，渲染层不重算） |
| `Dir` | （并入 `FileHit`） | `HitText` 自己读 `Dir` 渲染 `[目录]` 尾注 |
| — | `Kind = File` | — |
| — | `IconHint = ""` | files 无图标（§2.3） |
| — | `Score = 0` | 段内不重排（R14） |
| `Path` | `PrimaryAction = Open(Path)` | 与既有 `BuildOpenStartInfo` 同义 |
| `Path` | `SecondaryAction = Reveal(Path)` | 与既有 `BuildRevealStartInfo` 同义（反斜杠归一） |
| 整个 `SearchHitDto` | `FileHit` | ★ 渲染层凭它走**既有 HitText 模板**（FR-10 的实现手段） |

**其余映射**：`SearchQueryResponse.Total → LauncherResultSet.Total`；`ElapsedMs → ElapsedMs`；`Hits → Items`；`Epoch` 仅用于 `client.IsStale` 判断，**不外泄**。

> **注 ③（关键，别"优化"掉）**：`SearchIndexClient.IsStale` 这道闸**必须留着**。它管的是"**在途查询期间索引侧会话状态变了**"（`StatusAsync()` 与暂停/恢复**都会签发 epoch**）⇒ 结果按既有语义作废、界面不动。若用 router 的代次闸替代它，则在"查询在途时用户点了托盘暂停"这个场景下**行为会变**（新行为：照常渲染该结果；旧行为：丢弃）⇒ 违反 W7-a 零行为。两道闸并存且各自成立（§3.3 表）。

**错误映射（逐条对照既有 `RenderError`）**：

| 索引侧 | 今天窗口显示 | W7 模型 | 要求 |
|---|---|---|---|
| `Code == SearchNotReady (-32001)` | `正在建索引（首次全量约 10 秒级，取决于文件数）—— 打字会自动重试` | `LauncherError(IndexNotReady, -32001, UserText=同上)` | ★ **文案逐字相同**，且**不算"故障"**（不触发 FR-9 的段位错误样式） |
| 其它 `SearchIndexException` | `搜索出错（{code}）：{message}` | `LauncherError(IndexUnavailable, code, ...)` | 逐字相同 |
| 超时（60s 无配对响应） | `搜索出错（-32603）：索引进程响应超时（…）` | 同上（由 `SearchIndexClient` 抛出，异常类型不变） | 逐字相同 |

### 10.6 `AppsProvider`（扫描 / 缓存 / 匹配 / 图标 / 启动）

**A. 扫描源（v1，逐条写死；探针可注入替换全部根以做确定性断言）**

| # | 源 | 路径 | 收录 | 标题 |
|---|---|---|---|---|
| S1 | 用户开始菜单 | `Environment.SpecialFolder.StartMenu` + `\Programs` | `*.lnk`（递归） | 文件名去 `.lnk` |
| S2 | 公共开始菜单 | `Environment.SpecialFolder.CommonStartMenu` + `\Programs` | `*.lnk`（递归） | 同上 |
| S3 | 用户桌面 | `SpecialFolder.Desktop` | `*.lnk` `*.url` | 同上 |
| S4 | 公共桌面 | `SpecialFolder.CommonDesktopDirectory` | `*.lnk` `*.url` | 同上 |
| S5 | 注册表 App Paths | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\*` + `HKCU\...` | 子键默认值 = exe 全路径 | 子键名去 `.exe` |

- **去重键 = 全路径（`OrdinalIgnoreCase`）**：S1/S2 存在大量同名复制（同一应用装到用户级+机器级），按路径去重保留**两个不同路径**（它们确实都能启动）；若两路径**同名同目标**（无法判定，因为不解析 .lnk）⇒ 保留先扫到的（S1 优先用户级，符合"用户改过的优先"直觉）。
- **排序去噪**：跳过文件名以 `Uninstall`/`卸载` 开头、以及 `desktop.ini`、`Thumbs.db`（真机上开始菜单里确实存在）。
- `*.url` ⇒ `Title` = 文件名，`PrimaryAction = Open(url 文件全路径)`（shell 按关联打开），`SecondaryAction = Reveal`。
- 单个根不存在/无权限 ⇒ **跳过该根 + 记录**（不抛、不整体失败）；全部根都失败 ⇒ `IsReady=false` + 首次查询报 `ProviderFailed`（可见）。

**B. 缓存与失效（D8=A）**

```csharp
// （新建）src/Eztools.Desktop/Launcher/AppIndexCache.cs
internal sealed class AppIndexCache          // ★ 实例类（不是静态）—— 根可注入是探针确定性的前提
{
    internal const int RescanIntervalMs = 10 * 60 * 1000;   // 兜底重扫间隔

    internal AppIndexCache(IReadOnlyList<AppScanRoot> roots, bool includeAppPaths = true, Func<long>? nowMs = null);

    internal bool Ready { get; }        // 扫描完成（含失败）
    internal IReadOnlyList<AppEntry> Items { get; }   // 重扫期间 = 旧快照
    internal string? LastError { get; } // 非空 ⇒ 必须对用户可见（§11.1）
    internal int ScanCount { get; }     // 探针断言"重扫真的发生了"
    internal event Action? BecameReady;              // → AppsProvider 转发（R10 补发）

    internal void EnsureScanStarted();               // 幂等；**构造后由装配方调用**
    internal void InvalidateIfChanged();             // 唤出时调用：指纹变了或超兜底间隔 ⇒ 后台重扫
    internal static IReadOnlyList<AppScanRoot> DefaultRoots();   // S1~S4（S5 由 includeAppPaths 控制）
}
```

- **★ 三态必须分清（否则用户看到的是"没装应用"）**：
  `Ready=false` = 扫描中（**静默缺席**，"还没好"不是"没有"）· `Ready=true && LastError==null` = 正常 ·
  `Ready=true && LastError!=null` = **读不到 ⇒ 必须可见**。★ 第三态**不能**用 `Ready=false` 表达 ——
  那样 router 会按"静默缺席"处理（契约 C5），故障就永远没人看见。
- **★ 扫描必须在构造后立即启动**（`EnsureScanStarted`），**不能等 `QueryAsync` 触发**：未就绪时
  router 按 `IsReady` 闸**根本不会调 provider**（契约 I6）⇒ 等它触发就永远不会触发（死锁）。
- **首次**：本次查询按 `IsReady=false` 静默缺席；扫描完成后置 `Ready=true` 并按 R10 规则补发（`Requery`）。
- **失效检测**：`Summon()` 时后台比一次指纹（各根 `LastWriteTimeUtc.Ticks` + 文件数拼接，成本 ~ms）；不一致 ⇒ 重扫。
  **App Paths 无轻量变更通知** ⇒ 纳入"每 10 分钟兜底重扫一次"（常量，不配置）。
  ★ 兜底判据写成 `_lastScanMs == long.MinValue || now - _lastScanMs >= RescanIntervalMs`：**显式分支而非常量参与减法**
   —— `now - long.MinValue` 在 unchecked 下溢出成负数，会让兜底重扫被静默跳过。
- 扫描期间的 `QueryAsync` 一律走旧快照（**永不用空列表冒充"没装应用"**）。
- **全部根都失败** ⇒ `LastError` 非空且 `Ready=true`（可见的失败态），不是"没有应用"。

**C. 匹配与打分（`FuzzyMatcher`，纯函数，selftest 直测）**

| 匹配形态 | 分数 | 例（查询 `code`） |
|---|---|---|
| 完全相等（忽略大小写） | 1000 | `code` |
| 前缀 | 800 − 长度惩罚 | `Code.exe` |
| 词边界前缀（空格/`-`/`_`/`.` 后的前缀） | 700 | `Visual Studio **Code**` |
| 子串 | 500 − 位置惩罚 | `VS**Code**` |
| 子序列（两者保序不连续） | 200 − 间隔惩罚 | `V**S**tud**i**o`… |
| 无匹配 | — | 排除 |

- 查询词**按空白分词**：每段都必须命中（AND），分数取各段平均（`"vs code"` ⇒ `vs` 与 `code` 都要命中）。
- **中文**：走同一套子序列逻辑（`报` 命中 `报表工具`）；**不做拼音**（§2.3）。
- **别名加成（W7-e）**：`launcher.alias` 命中 ⇒ 加 300 分。目标与应用标题/路径做**不区分大小写子串包含**（10-02 实机改判，原稿全等 —— 用户写俗称、系统条目是文件名，全等必失配）；误伤约束 = "目标尽量写长一点"。
- **频次加成（W7-e）**：`+min(400, 100 * log2(1 + 命中次数))` 并乘 `lastUsed` 衰减（§10.11）。
- 命中的字符区间同时产出 `Highlights`（供 `LauncherRowText` 主行加粗）——**apps 项的高亮由本地匹配给出**（与 files 由索引进程给出并列，互不干扰）。

**D. 图标（D5=A，Desktop `IconCache`）**

```
提取：SHGetFileInfo(path, 0, out SHFILEINFO, sizeof, SHGFI_ICON | SHGFI_SMALLICON)
      → Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, ...) → Freeze()
      → DestroyIcon(shfi.hIcon)   ★ 必须在 try/finally
缓存：Dictionary<string /*路径 OrdinalIgnoreCase*/, BitmapSource>，LRU 上限 512（R11）
失败：返回 null（行内不显示图标）—— ★ 只有"图标失败"静默，因为它是**纯装饰**；
      绝不能让图标失败影响"能不能搜到/能不能启动"
自测：连续 N（≥200）次提取后 GetProcessHandleCount 斜率必须为 0（R4 / RI-6 同款方法）
```

**E. 启动与失败文案（`LauncherActionRunner`）**

| 动作 | 实现 | 失败文案 |
|---|---|---|
| `Launch(app)` | `Process.Start(new ProcessStartInfo(TargetPath){ UseShellExecute = true, WorkingDirectory = 目录 })` | 目标不存在 ⇒ `启动失败：目标不存在（快捷方式可能已失效）—— {path}`；其它 ⇒ `启动失败：{ex.Message}` |
| `RevealApp(app)` | 复用既有 `explorer /select,"{反斜杠归一}"` 模式（★ 正斜杠是静默 no-op，踩坑 R 系） | `定位失败：{ex.Message}` |
| `Open(file)` / `Reveal(file)` | 复用既有构造器（见 §14） | 复用既有 `DescribeOpenFailure`（区分"文件不存在"） |

### 10.7 `CalcProvider`（词法 / 语法 / 求值 / 格式化）

**A. 触发门槛（两级，语义不同 —— 这是本 provider 最容易被写错的地方）**

| 级别 | 判据 | 处置 | 为什么 |
|---|---|---|---|
| **L0 字符门槛** | 文本（trim 后）非空，且**只含** `0-9` `.` `+` `-` `*` `/` `%` `^` `(` `)` 与空白，且**至少含一个运算符/括号**，且**至少含一个数字** | 不通过 ⇒ **完全静默**（不产生任何行，连错误行都没有） | 含字母/汉字（`report-1.txt`）⇒ 用户明显在搜文件；纯数字（`2026`）⇒ 更可能是在搜文件，**不出结果行**（与 PowerToys 不同，理由见 §11.3 C-2） |
| **L1 语法/求值** | L0 通过后交给解析器 | 语法错 ⇒ **静默**（半成品输入高频：`1+`、`(1+2`）；求值错（除零/溢出/NaN）⇒ **出结果行并显示显式错误文本** | 见下方"两类失败的不同处置" |

> ★ **两类失败的不同处置（写死）**：
> - **语法错 = 静默**：用户打到一半（`128*`）就出"语法错误"是噪声。
> - **求值错 = 显式行**：输入已是完整合法表达式（`1/0`），用户**期望**一个结果；静默会让人以为计算器坏了。这正是 §3.5「禁 `:-0` 式静默」的落点。
> - 但**语法错的诊断信息必须可达**（审查规范 §3.3「降级/告警信号必须有可达出口」）：出口 = **selftest 直接调解析器断言 `CalcError.Syntax` + 位置**，并把错误码落进探针 JSON。**"UI 不显示"≠"错误不存在"**，两者必须能区分（否则"我静默了"与"解析器坏了"同形 —— S2 家族）。

**A′. 落地补充（W7-c 实施期明确下来的五条口径，都是"写错就会静默退化"的地方）**

| # | 口径 | 为什么 |
|---|---|---|
| ① | L0 的允许字符集**还包括科学记数的 `e`/`E`**（且必须紧跟在数字后、后面是 `数字` 或 `符号+数字`） | 设计稿只列了 `0-9 . + - * / % ^ ( )`，但 §11.3 C-4 要求 `1e5` 合法 ⇒ 不放行 `e` 就自相矛盾；而"紧跟数字"这条约束保证 `report-1.txt` 里的 `e` 仍被挡（R5） |
| ② | **指数里的符号不算运算符**：`1e+5` 与 `1e5` 同判 | 两者都是**纯数字**；不排除的话 `1e+5` 会突然开始出结果，与 C-2「纯数字不出结果」自相矛盾（一条口径被开了一个后门） |
| ③ | **"不出行"有两个来源，必须分开断言**：L0 未过 ⇒ `NotAnExpression`；L0 过了但语法不成立 ⇒ `Syntax` | 两者对用户同样是静默，但语义完全不同（"我没打算算" vs "你还没打完"）。只断言"不出行"会把这两件事混成一件，将来把 L0 写坏（例如放行 `1+`）也照样绿 |
| ④ | **一元号本身算"运算符"** ⇒ `-5` / `+5` 会出结果行 | 门槛口径是"至少一个运算符/括号"；"纯数字"只挡**不带任何运算符**的输入（`2026` / `1e5` / `1e+5`）。若嫌 `-5` 是噪声，正确做法是改门槛口径（需重跑 §11.3 C 全表），而不是在解析器里特判 |
| ⑤ | **错误行的主行 = 去掉空白的原表达式，副行 = 错误文案** | 设计稿 C 表把错误文案列在"结果行**副标题**"，此处按字面落地；成功行副行是 `= 表达式`、错误行副行是原因，两者结构一致（主行 = "这东西是什么"，副行 = "细节") |

> ★ **`1e5` 的 UI 行为（易被误读，写死）**：`1e5` 单独输入**不出结果行**（它是纯数字 ⇒ 与 `2026` 同判）。
> §11.3 C-4 说的"合法"指的是**解析层**不把它当非法字符 —— selftest 用 `ParseAndEvaluate` 直接断言
> `1e5 == 100000`。`1e5*2` 这类**带运算符**的输入照常出结果。两层各说各的真话，不矛盾。

**B. 词法 / 语法（EBNF，实现照此；`^` 右结合，一元号弱于幂）**

```
expr    := term (('+' | '-') term)*
term    := unary (('*' | '/' | '%') unary)*
unary   := ('+' | '-')* power
power   := primary ('^' unary)?                 // 右结合 ⇒ 2^3^2 = 2^(3^2) = 512
primary := number | '(' expr ')'
number  := (digit+ ('.' digit*)? | '.' digit+) ([eE] [+-]? digit+)?   // 科学记数允许（R5 的唯一字母例外）
```

> **★ 实现期放宽一处（W7-c 落地）**：`number` 增加 `.digit+` 分支 —— 允许 `.5` 这种**前导小数点**写法
> （设计稿原文是 `digit+ ('.' digit*)?`，即必须数字开头）。理由：`.5+1` 是自然输入，语法上**无歧义**
> （没有别的构造以 `.` 开头），静默吞掉会被当成 bug。`1.2.3` 依旧判语法错（`1.2` 之后剩 `.3` 不接运算符）。

- `%` = 取模，语义取 `Math.IEEERemainder`? **否** —— 取 **C 语言 `fmod`**（符号跟随被除数）并写死；`Math.IEEERemainder` 在除数为 0 时返回 NaN 而不抛，语义与直觉不符。
- `-2^2` = **−4**（一元号弱于幂，标准数学约定）；`2^-1` = 0.5（指数的操作数是 `unary`）。
- 非法字符在词法阶段即报 `CalcError.InvalidChar(position)`。
- **不做**：隐式乘法（`2(3+4)`）、`!`、`sin/cos`、变量、十六进制（`0x`）—— 一律语法错（静默）。列为复活条件。

**C. 求值错误码（`CalcErrorKind`）**

| 错误 | 触发例 | 结果行副标题文案 | 主动作 |
|---|---|---|---|
| `Syntax` | `1+`、`(1+2`、`1.2.3` | **不出行**（selftest 断言错误码可达） | — |
| `InvalidChar` | 含汉字/字母（L0 已挡） | — | — |
| `DivideByZero` | `1/0`、`5%0` | `除零`（值域未定义） | **禁用**（`PrimaryAction = null` ⇒ Enter 无动作，行仍显示） |
| `Overflow` | `1e308*10` | `溢出（超出双精度范围）` | 禁用 |
| `Undefined` | `(-8)^0.5`（复数结果）、`0^-1` | `结果未定义` | 禁用 |
| `NotAnExpression` | L0 未通过 | —（不出行） | — |

> **"禁用动作"的表达方式**：`LauncherItem` 的 action 字段允许为 `null`；Runner 见 `null` ⇒ 不动作 + 状态行提示"该结果不可复制"（**不静默吞键** —— 既有契约：返回 false 让键放行）。

**D. 数值格式化（纯函数，selftest 手算精确断言，禁"算完跟自己比"）**

| 条件 | 格式 | 例 |
|---|---|---|
| 整数且 \|v\| < 1e15 | 无小数、无千分位 | `128*3/4` → `96` |
| \|v\| ≥ 1e15 或（v ≠ 0 且 \|v\| < 1e-4） | 科学记数（`G6` 后归一大小写与 `+`） | `2^60` → `1.15292E+18` |
| 其它 | 最多 10 位小数，**去尾随零** | `1/3` → `0.3333333333`；`1/8` → `0.125` |
| 恒用 `CultureInfo.InvariantCulture` | 小数点恒为 `.` | 避免中文区域出现 `96,0` |

**E. 结果行构成**

`Title = 格式化结果值` · `Subtitle = "= " + 去空白的原表达式` · `Kind = Calc` · 徽标 `=` · `Score = 1e9`（pin 段固定首位）· `IconHint = ""`。

### 10.8 `UnitProvider` / `EncodeProvider`（W7-d 规格）

**A. `UnitProvider`（D10=A）**

| 类别 | 单位（可识别写法） | 换算基 |
|---|---|---|
| 长度 | `mm` `cm` `m` `km` `in` `ft` `yd` `mi` | 米（十进制）+ 英制精确系数 |
| 重量 | `mg` `g` `kg` `t` `oz` `lb` | 千克 |
| 数据量 | `B` `KB` `MB` `GB` `TB` `KiB` `MiB` `GiB` `TiB` | 字节（**两制式并存**：`KB`=1000、`KiB`=1024，**显示时必须带制式说明**，见下） |
| 温度 | `C` `℃` `F` `℉` `K` | **仿射**（`F = C×9/5+32`），**单独代码路径**（不是乘系数） |

- **输入形态**：`<数值><单位> [to|->|转] <单位>`；`to`/`->`/`转` **三者等价**（D10）。
- **无触发词**（`10km`）⇒ 出该类别**常用单位 4~5 行**（长度：km/mi/m/ft/in；重量：kg/lb；数据量：MB/MiB/GB/GiB；温度：C/F/K），每行一个结果，`Score` 递减。
- **歧义与边界**：`m` 唯一解释为米（不做时间单位，§2.3）；`10 m` 与 `10m` 等价（空格可选，但 **L0 门槛含字母 ⇒ 与 calc 无冲突**）；`100°C to F` 的 `°` 在字符集外 ⇒ **必须显式接受 `°`/`℃`/`℉`**（否则真机常用输入法打出的温度符号全部失灵）；负数温度 `-40C to F` = `-40`（写死，selftest 断言这条经典恒等式）。
- **数据量制式说明**：结果副标题带 `(1000 进制)` / `(1024 进制)` 标注 —— 否则 `1GB` → `953.674MiB` 会被当成 bug（这是最常被误解的一处，必须可见）。
- **不做的换算**（写死）：单位混算（`10km + 5mi`）、货币（需网络）、时间（命名冲突）、`kn`/`bar` 等工程单位（低频）。

**B. `EncodeProvider`（D9=A：只认显式前缀）**

| 前缀 | 方向 | 输入例 | 输出行 |
|---|---|---|---|
| `b64:` | 编码 | `b64:你好` | `5L2g5aW9` |
| `b64d:` | 解码 | `b64d:5L2g5aW9` | `你好` |
| `url:` | 编码 | `url:a b&c` | `a%20b%26c` |
| `urld:` | 解码 | `urld:a%20b` | `a b` |
| `u:` | 字符 → 码位 | `u:中A` | `U+4E2D U+0041` |
| `ud:` | 码位 → 字符 | `ud:U+4E2D` | `中` |

- **无前缀 ⇒ 完全静默**（理由见 D9；弱特征自动嗅探必然污染文件搜索结果）。
- 前缀**大小写不敏感**，前缀后允许一个空格；**载荷为空（`b64:`）⇒ 静默**（半成品输入，与 calc 语法错同处置）。
- **非法输入 = 显式错误行**（不是静默）：`b64d:!!!!` ⇒ 行显示 `无效的 base64 输入`；理由与 calc 求值错同款（用户已明确表达意图）。★ 错误行主行 = 错误文案、副行 = 方向说明（§4.3 规定 Encode 副行恒为方向说明，与 calc 的"副行 = 错误文案"是**两份字面规格**，各自成立）；动作禁用。
- URL 解码：`+` 是否视作空格 ⇒ **不视作**（`Uri.UnescapeDataString` 语义，写死并断言），避免破坏 `b64` 结果的 `+`。★ **残缺的 `%XX`（如 `urld:a%ZZ`）必须自查并显式报错** —— `Uri.UnescapeDataString` 对它们不抛也不报，原样返回，否则"非法输入"会静默成"转换成功但结果等于输入"。
- Unicode：BMP 内的 `U+XXXX` 与 `\uXXXX` 两种写法；代理对（`U+1F600`）⇒ 显式支持（`char.ConvertFromUtf32`），单测覆盖。★ **必须按码点而非码元走** —— 逐 `char` 会把 emoji 吐成 `U+D83D U+DE00` 一对无意义数字。

**B′. 落地补充（W7-d 实施期明确下来的口径）**

| # | 口径 | 为什么 |
|---|---|---|
| ① | **无触发词的常用单位列表不含源单位** | 源单位那一行是恒等变换（`10 km = 10 km`），对用户零信息、纯噪声；设计稿的列表是"该类别的常用单位集"，去掉源单位后行数仍是 4~5（长度 5→4、重量 4→3、数据量 4→3、温度 3→2） |
| ② | **重量类别扩充到 4 个**（kg/lb/oz/g，设计稿只写了 kg/lb） | 排除源单位后只剩 1 行太薄；oz/g 是日常最常换算的两个 |
| ③ | **整串必须被消费**，否则不解析 | `10km + 5mi` 若按"尽力匹配前缀"会被当成"无触发词"去列常用单位 —— **错的输入给出看似合理的结果**比静默更糟（§10.8 A"不做单位混算"的落地） |
| ④ | 单位记号的字符集**显式列**（ASCII 字母 + `°℃℉`），不用 `char.IsLetter` | `转` 与 `℃` 在 .NET 里都是"字母"类字符；用 IsLetter 会把 `10km转mi` 的单位记号吃成 `km转mi`。显式列字符集比"通用判定再打补丁"难写错得多 |
| ⑤ | 数值格式化**复用 `CalcExpression.Format`** | 同一套"整数 / 10 位去零 / 科学记数"口径；单位行与计算行贴进剪贴板的数字格式不一致会被当成 bug |
| ⑥ | **`+` 不会成为 calc 的入口**：`10km` 含字母 ⇒ calc 的 L0 必挡；`10km+5mi` 被本 provider 的"整串消费"挡掉 | 与 calc 的"两层静默"互斥性证明（§10.7 A′ ③ 同族） |


### 10.9 `SearchWindow` 改造点（渲染分流 + 动作分派）

**A. 模板分流（FR-10 的实现关键）**

```
_results.ItemTemplate 改为 LauncherRowTemplateSelector：
    item is LauncherItem { Kind: File, FileHit: not null } → 既有 HitText 模板
                                                              （★ 代码与模板行**零改动**）
    item is LauncherItem（其它 Kind）                      → LauncherRowText
```

- **`HitText` 的内部实现、`HitProperty`、`RenderedName`/`RenderedPath`/`RenderedSegments` 全部不动** ⇒ `SearchUiProbe.FindHitText` + `firstSegments` 断言**零迁移**（这是 FR-10「断言零改动」的落地手段）。
- 唯一改动是**数据模板的绑定路径**：`SetBinding(HitText.HitProperty, new Binding())` → `new Binding(nameof(LauncherItem.FileHit))`。
- `_results.Items` 的元素类型统一为 `LauncherItem`（files 项携带 `FileHit`）。
- `SelectedHit()` → `SelectedItem() is LauncherItem`；`Ctrl+C` 复制路径的取值从 `hit.Path` 改为按 Kind 取（File ⇒ `FileHit.Path`；App ⇒ `Subtitle`；其它 ⇒ `Title`）。

**B. 零行为对照表（W7-a 逐个核对，缺一即不可交付）**

| 观测点 | 今天 | W7-a 必须 | 机器证据 |
|---|---|---|---|
| 200 条渲染 / 虚拟化 | 200 条 + 容器数 < 60 | 不变 | `--probe-search-ui` 断言不变 |
| 计数显示 | `显示 200 / 共 12345 条` | 逐字相同 | 同上 |
| 高亮分段 | `季报` + `年度` 两段加粗 | 逐字相同 | 同上（`firstSegments`） |
| 首项路径 | 索引回传全路径 | 逐字相同 | 同上（`firstPath`） |
| 空查询占位文案 | `输入以搜索（Enter 打开 · Ctrl+Enter 定位 · Ctrl+C 复制路径）` | 逐字相同 | 探针 `statusText` + 手工 |
| 挂起/暂停徽标 | 行首 `索引已暂停…` | 逐字相同 | `--probe-search-ui` 的 `volumesLinePaused` |
| 卷漂移提示 | `检测到未索引的卷 E:…` | 逐字相同 | `driftProbe` 四例 |
| 打开/定位构造器 | `BuildOpenStartInfo` / `BuildRevealStartInfo` | **保留原签名**（新增 path 重载，旧重载委托新重载） | `actions` 断言块不变 |
| 唤起链 | Summon/Toggle/ForceForeground/IME 修复 | 不变 | `--probe-search-summon` |
| 键位语义 | Enter/↑↓/Esc/Ctrl+C/Del 放行 | 不变 | `--probe-search-ui` + W3 手工 M2 |

> **注 ①（FR-6 的口径）**：文件项**不加徽标**。原因：`HitText` 的渲染结构是 `Children[0] = 名字 TextBlock`，探针的 `RenderedSegments()` 读的正是 `Children[0]`；插入徽标会让 `firstSegments` 断言变红，从而**毁掉 FR-10 的机器证据**。文件项的"来源可辨"由**全路径副行**承担（比徽标更强的信号）。若菲比要求字面满足 FR-6，则选 D11=B，并接受 `--probe-search-ui` 断言集迁移。

**C. 动作分派**

```
OnInputCommand:
    Enter when items>0     → runner.Execute(SelectedItem().PrimaryAction)
    CtrlEnter when items>0 → runner.Execute(SelectedItem().SecondaryAction ?? PrimaryAction)
    CtrlC when items>0     → runner.CopyToClipboard(按 Kind 取值)   // ★ 不 Hide（既有语义）
    Esc / ↑ / ↓            → 既有逻辑不变
    default                → false（放行）
动作结果：
    成功 → Hide()（复制类也 Hide，§4.2 规则 2）
    失败 → 留窗 + _status.Text = 失败文案（可读原因）
    action 为 null → 不动作 + _status.Text = "该结果不可执行"
```

### 10.10 `LauncherPrefs`（配置解析）—— ★ 落地记录：W7-b 已交付

> **为什么 W7-a 不做**（落地偏差③）：W7-a 只有 files 一个 provider ⇒ `launcher.providers` 此刻
> **真的没有作用**（配成 `apps`/`calc` 会得到一个空启动器），此时加 schema 键 = "名存实亡 flag"
> （踩坑全集 §2.23），而未被生产消费的解析类 = 幽灵代码（审查规范 §3.9 G4 家族）。
> **推迟到 W7-b**（apps 落地后该键才有意义）一并交付，连同 §5 的解析规格表与 8 条 selftest。

```csharp
// （新建）src/Eztools.Host/Launcher/LauncherPrefs.cs
public sealed record LauncherPrefs(IReadOnlyList<string> Providers)   // 已归一、已去重、顺序即段序优先级
{
    public static IReadOnlyList<string> DefaultProviders => LauncherProviderRegistry.DefaultEnabled;
    public static LauncherPrefs Default { get; }                      // 键缺失时的形态

    public bool Includes(string providerId);                          // 大小写不敏感

    /// 纯函数，selftest 直测。raw == null = 键缺失 ⇒ 默认集合、无错误。
    public static (LauncherPrefs Out, string? Error) Parse(string? raw);

    /// JSON 节点版：**键存在但类型不是字符串 ⇒ 报错**（与"键缺失"必须可分，§11.6 G-2）。
    public static (LauncherPrefs Out, string? Error) ParseNode(JsonNode? node);

    /// 生产入口：读 desktop 保留节的 launcher.providers。
    public static (LauncherPrefs Out, string? Error) FromConfig(ConfigStore configs);
}
```

- **相对 v2 设计稿的两处落地调整**：① 去掉 `UsageEnabled` 参数 —— `launcher.usage` 是 W7-e 的键，
  现在就把它塞进签名 = 又一个"名存实亡"参数（同一根理由）；W7-e 加键时再加字段。
  ② 增 `ParseNode`/`FromConfig` 两层：`ConfigStore.TryGetString` 对"键缺失"与"类型错"**都返回 null**，
  只靠它判会把手改的 `"launcher.providers": []` 当"没配"静默走默认（§11.6 G-2 的原型）。
- 解析规则见 §5 表（逐行即断言）。
- **`Error` 的三个消费者**（出口必须可达，§3.3⑤）：设置窗口（保存时拒绝）+ **窗口状态行（首次唤出报一次）**
  + 日志 `WARN`。★ 落地时发现注释曾声称"状态行会再报一次"但代码里没有这条路 ⇒ 已补齐
  （`SearchWindow.startupWarning`，见 §6 W7-b 落地偏差③）。

### 10.11 `LauncherUsageStore`（频次记忆，W7-e）

**存储**（D6 复议后按 JSON 计）：

```
路径：<ConfigRoot>/launcher/usage.json
结构：{ "version": 1, "entries": { "<providerId>|<identity>": { "count": 3, "lastUsed": "2026-10-01T10:11:12" } } }
identity：App ⇒ 目标全路径（OrdinalIgnoreCase）；File ⇒ 全路径；Calc/Unit/Encode ⇒ 规范化后的表达式
写入：仅用户**执行了动作**（Enter/Ctrl+Enter）时 `count+1`；2s 去抖落盘 + 宿主退出 flush；原子写
读入：首次查询时懒加载；文件缺失/损坏 ⇒ 空字典 + 日志 WARN（不阻塞）
开关：`launcher.usage`（默认 true）；false ⇒ **完全不读写**（不是"只写不读"，避免磁盘上留下用户以为没有的数据）
失败：任何 IO 异常 ⇒ 仅日志（**绝不能因为记频次失败而影响启动应用**）
```

**加成函数（纯函数，selftest 断言单调性 + 上限）**：

```
boost(count, lastUsedDays) = min(400, 100 * log2(1 + count)) * decay(lastUsedDays)
decay(d) = d <= 7 ? 1.0 : max(0.25, 1.0 - (d - 7) / 90.0)     // 90 天后衰减到 0.25 地板
```

> **注 ②（FR-8 口径收窄，✅ W7-e 落地确认）**：频次加成**只作用于 apps 段**——files 排序归索引进程（FR-2/FR-10）；calc/unit/encode 每轮至多一行，加成是 no-op ⇒ **只记录 apps、identity = 目标全路径**。记了没人读的数据 = 垃圾数据 + usage.json 无界增长，故 v1 不记其余段（将来加消费者时按 identity 规格扩展，`LauncherUsageStore` API 已按 providerId 泛化）。

### 10.12 探针设计（`--probe-launcher <mode>`）

> **★ 为什么新开一个探针而不是扩展 `SearchUiProbe`**：`--probe-search-ui` / `--probe-search-summon` 的
> **每一条**断言都是"文件项零变化"（FR-10）的机器证据，一行都不许动。把新来源的断言挂进去，早晚会
> 顺手改到文件项的断言上 —— 那条证据链一断，之后就再也说不清"文件项到底有没有变"。所以新来源
> 必须有**自己的观测面**（对应 §5「探针装配纪律」的另一半）。

**形态 = 位置参数**（`--probe-launcher <mode>`，与既有 `--probe-search-ui` 等布尔开关同风格：
`--xxx` 不接值 = 开关，`--xxx <v>` = 带值），`--out` 落盘 JSON，`rc` 判据。

| 模式 | 做什么 | 关键断言 |
|---|---|---|
| `rows` | 五种来源各一条（files 真 + calc/unit/encode/apps 假），`Submit("a")` | 段序 `pin→apps→files`、徽标逐项（`=`/`⇄`/`{}`/`▸`）、主副行落到控件、**`LauncherRowText` 里一条 `File` 都没有**（FR-10 反向断言）、`statusRight` 仍是"显示 200 / 共 12345 条"、`HitText` 仍读出首项名、204 条仍只生成少量容器（虚拟化未被新模板破坏） |
| `actions` | 真 `SearchWindow` + 假来源：`Ctrl+C`（首行 calc）/ `Enter` | Ctrl+C 复制**结果值**且**不收窗**；Enter 复制值 + **收窗**；文件行 Ctrl+C 复制**索引回传全路径**；动作构造器参数形状（`Launch` 工作目录 = 目标所在目录、`RevealApp` 反斜杠归一 + 引号只包路径）；失败文案三分支（目标不存在 / 其它） |
| `isolation` | ① 必抛 provider；② `startupWarning` 非空 | ①其余段照常 + 状态行含"暂不可用"（FR-9）；②配置告警**首次显示可见**且被后续结果覆盖（出口可达 + 不形成常驻噪音） |
| `router` | 慢来源（阻塞 gate）+ **清空输入框推进代次** ⇒ 放行慢批次 | ★ **代次闸的上屏级证据**：`staleEverRendered == false`、渲染日志条数在放行前后**不变**、`itemsOnScreen == 0`（详见 §6 W7-b 落地偏差② —— 为什么不用"慢/快连打两次"、以及突变验证结果） |
| `calc` | 真 `CalcProvider` + 真窗口（只喂假文件源）：`128 * 3/4` → 行 → Enter；再逐个试静默类与求值错类 | ★ 四条：①结果行**置顶**（`leadingKinds == [Calc, File]`）+ 值/副行/徽标逐字；②Enter 复制**格式化值**并收窗；③静默类（`2026` / `report-2026.txt` / `1+`）**0 条 calc 行且文件段照常 200 条**（只断前者是弱断言 —— "整轮没跑"也满足它）；④`1/0` 出行且 Enter **不复制、不收窗**、状态行"该结果不可执行"；⑤Ctrl+Enter 复制「表达式 = 结果」整串（§4.2，W7-d 回填） |
| `unit` | 真 `UnitProvider` + 真窗口：`10 km to mi` → 行 → Enter；`-40C to F`；`1GB to MiB`；`10km`（无触发词）；混算/未知单位 | ★ ①结果行置顶 + 值/副行/徽标逐字；②Enter 复制结果值（含单位）并收窗；③温度仿射恒等式 `-40C = -40 °F`；④数据量副行带「1000 进制 → 1024 进制」；⑤无触发词列 4 行且**不含源单位**、首行与显式换算一致；⑥混算/未知单位 0 行且文件段照常 |
| `encode` | 真 `EncodeProvider` + 真窗口：`b64:你好` → 行 → Enter；其余前缀；`b64d:!!!!`；无前缀 | ★ ①结果行置顶 + 值/副行/徽标逐字；②Enter 复制结果并收窗；③六前缀全覆盖（按码点，emoji 不给半截码位）；④非法输入**显式错误行** + 动作禁用（Enter 不复制不收窗）；⑤残缺 `%XX` 同样显式报错；⑥无前缀 0 行且文件段照常 |
| `config` | 读**真实**配置中心落出解析结果/告警文案（两层测试的应用层观测面；CLI 写入由 acceptance 步骤 8 配对断言） | 键缺失 ⇒ 五来源 + 无错误 + 无告警（其余三态在 acceptance 的两层闭环里）；**含 usage/alias 解析观测**（usageEnabled / aliasCount / aliasError） |
| `usage` | 真 `AppsProvider` + 真 `LauncherUsageStore`（文件落探针临时目录） | ①记录两次 ⇒ **同分对手排序提前**；②Flush ⇒ 新实例回读 count/加成一致（持久化真发生）；③开关关 ⇒ 不落盘 + BoostFor 恒 0；④损坏文件 ⇒ 空表 + LastError |
| `apps` | 真 `AppsProvider`（探针自建临时目录注入扫描根） | 命中 / 递归收录 `.lnk` / 收录 `.url` / 过滤"卸载"项 / `Launch` 参数（**不真起进程**）/ **图标句柄斜率 = 0**（200 次提取）/ 缓存上限 ≤ 512 且真命中过 / 指纹失效重扫 / 首扫就绪补发（`RequeryCount`）/ 行渲染（图标 / 高亮 / 副行） |
| `all` | 以上全跑（默认） | — |

- 探针纪律：**不改 `SearchUiProbe`/`SearchSummonProbe` 的任何断言**（§5「探针装配纪律」）。
- `--out` 落盘 JSON + `rc` 判据（禁 `cmd | grep -q`，断言审视清单 S 系）。
- 探针自行收尾（`CloseForProbe` + `DisposeForProbe`），保证**遮罩/窗口绝不过夜**。
- 阻塞类用例**一律有限等待**（gate 3 s 超时 + `PumpUntil` 超时）：用例失败时探针也必须能退出，绝不挂死。
- **静默类用例用定长泵**（`PumpFor(250)`）：静默是"什么都不发生"，无从 `PumpUntil` ——
  但必须**配一条正向断言**（文件段照常 200 条）证明那轮查询真的跑了（否则"没出行"可能只是"没查询"）。
- ★ **就绪信号必须用渲染日志，不能用"行数 > 0"**（W7-d 实测踩到）：上一轮的行还在列表里，"行数 > 0"
  在新一轮渲染落地之前就已成立 ⇒ 读到**陈旧行**（`1GB to MiB` 读回了上一条 `-40 °F`）。渲染日志按
  "真正写进列表"计数，清空后再涨即本轮落地；静默类同时回传"本轮确实渲染过"这个布尔。

## 11. 异常与边界

### 11.1 错误分类与文案（通用表）

| 类别 | 触发 | 段位处置 | 用户可见文案 | 是否算"故障" |
|---|---|---|---|---|
| 索引未就绪 | files 回 `-32001` | 段置空 | 既有文案 `正在建索引…打字会自动重试` | ❌ 否（正常启动期状态） |
| 索引不可用 | 超时 / 协议破坏 / 进程崩 | 段置空 | `搜索出错（{code}）：{message}` | ✅ 是 |
| provider 失败 | provider 自抛异常 | 段置空 | `{provider 显示名}暂不可用：{ex.Message}` | ✅ 是 |
| provider 未就绪 | `IsReady == false` | 静默缺席 | 无 | ❌ 否（R6 口径） |
| provider 已关闭 | 不在 `prefs.Providers` | 静默缺席 | 无 | ❌ 否 |
| 配置非法 | `launcher.providers` 非法 | 用默认集合 | 状态行一次性：`启动器配置无效（{err}），已用默认：files、apps、calc` | ✅ 是（一次性，不重复打扰） |
| 动作失败 | `Process.Start` 抛 | 段不变 | `启动失败：…` / `定位失败：…`（区分"不存在"） | ✅ 是（留窗，不静默） |

> **原则**：**"没结果"、"还没好"、"坏了"三者必须用不同的话术**（§30.6：只断"出了错"不够）。这张表是实现期文案的唯一出处。

### 11.2 provider 级故障隔离（FR-9）

- 规则：`SafeQuery` 捕获**所有**异常（含 `OutOfMemoryException`? **不** —— 只捕 `Exception` 非致命子集，致命异常（`StackOverflowException`/`OutOfMemoryException`）让它冒泡，避免把不可恢复状态伪装成"某个 provider 坏了"）。
- **主设计依据**：Flow Launcher 1.18 重构的公开理由正是"插件崩溃影响主程序稳定性"（主设计 §2 外印证）。本波的内置 provider 是进程内的，所以隔离只能靠 `try/catch` + 段位置空 —— **这是 in-proc 插件模型必须付的税**，写清以免后来者以为"既然隔离了就可以随便写"。
- 反向要求：**隔离不得掩盖 bug** ⇒ 每次段位失败都必须 ① 进 `LauncherRenderModel.Errors` ② 落 `logs/host-*.log` WARN ③ 探针 `--isolation` 断言可达。三者缺一即为 S2 家族问题。

### 11.3 各 provider 的边界清单（实现期照此逐条测）

**A. files（全部沿用既有行为，不新增）**：索引未就绪 / 空查询 / 查询期间暂停 / 卷热插拔（漂移提示）/ 超时（60s）/ 文件已被删除（打开失败文案）/ `explorer /select` 正斜杠 / 路径含空格与中文。

**B. apps**

| # | 边界 | 期望 |
|---|---|---|
| B-1 | 首扫未完成时查询 | 静默缺席；扫描完成后若窗口可见且文本未变 ⇒ **补发一次**（R10） |
| B-2 | 开始菜单目录不存在 / 无权限 | 跳过该根 + 日志；其它根照常；全失败 ⇒ 首次查询报 `ProviderFailed` |
| B-3 | 同一 `.lnk` 出现在用户级与机器级 | 按全路径去重（两条都留，因为都能启动）；`Title` 相同时副标题（路径）区分 |
| B-4 | `.lnk` 目标已失效（软件已卸载） | **仍列出**（不解析就无法预判）；`Launch` 失败 ⇒ `启动失败：目标不存在（快捷方式可能已失效）` |
| B-5 | 开始菜单里的"卸载 X"项 | 被 `Uninstall`/`卸载` 前缀过滤掉（§10.6 A 排序去噪） |
| B-6 | `App Paths` 子键指向不存在的 exe | 列出；启动失败走 B-4 文案 |
| B-7 | 名称含中文 / 含空格 / 含 emoji | 子序列匹配正常工作（emoji 按 UTF-16 code unit 处理，高亮区间不切坏代理对 —— ★ 渲染时若区间落在 surrogate 中间，**钳制到字符边界**，否则会渲染出乱码） |
| B-8 | 查询 2000 次不同路径（图标缓存压力） | 缓存条目 ≤ 512（R11）；句柄斜率 = 0 |
| B-9 | 多屏 / 高 DPI | 图标为 16×16 逻辑尺寸，WPF 自动按 DPI 缩放（不手工算像素） |

**C. calc**

| # | 边界 | 期望 |
|---|---|---|
| C-1 | `1+`（半成品） | 静默；selftest 断言解析器返回 `Syntax` |
| C-2 | `2026`（纯数字） | **静默**（不当表达式）—— 与 PowerToys 的差异是**有意的**：本项目的主职是搜文件 |
| C-3 | `report-2026.txt` | L0 未过（含字母）⇒ 静默 |
| C-4 | `1e5` / `1E-5` | 合法（科学记数）—— ★ 指**解析层**接受（selftest 直测 `ParseAndEvaluate`）；单独输入时 UI **不出行**（纯数字，与 C-2 同判），见 §10.7 A′ 注 |
| C-5 | `1/0` / `5%0` | 出错误行 `除零`；动作禁用 |
| C-6 | `1e308*10` | 出错误行 `溢出（超出双精度范围）` |
| C-7 | `(-8)^0.5` | 出错误行 `结果未定义` |
| C-8 | `2^3^2` | `512`（右结合） |
| C-9 | `-2^2` | `-4`（一元号弱于幂） |
| C-10 | `1/3` | `0.3333333333`（10 位去零） |
| C-11 | 超长输入（1 万字符） | 解析器必须**有长度上限**（如 512 字符，超出按 L0 未过静默）⇒ 防 UI 输入卡住 |
| C-12 | 极深括号（如 1000 层 `((((…`） | 递归下降需**深度上限**（如 64 层）⇒ 超出返回 `Syntax`（**防栈溢出**：`StackOverflowException` 在本进程内不可捕获） |

**D. unit / encode（W7-d）**：单位大小写（`KM`/`km` 等价）/ 温度负值与恒等式 `-40C == -40F` / `°` `℃` `℉` 字符 / 数据量两制式标注 / 前缀大小写 / 非法 base64 ⇒ 显式错误行 / `+` 不视作空格 / 代理对码位。

### 11.4 线程 / 生命周期边界

| # | 边界 | 规则 |
|---|---|---|
| T-1 | 查询回调线程 | 线程池（`SearchSession` 不 marshal）⇒ router 渲染前必须 `Dispatcher.BeginInvoke`（**非阻塞**，不成死锁环 —— 类头既有结论） |
| T-2 | 窗口已隐藏时结果到达 | **仍然渲染**（进 `_results.Items`，下次唤出即最新）—— 与今天行为一致（今天也不判可见性） |
| T-3 | 窗口已真关闭（`RealClose`）后会话仍在途 | `Dispose` 置 `_disposed`，`Submit/Fire` 抛 `ObjectDisposedException`（既有语义）；router 的 `Render` 必须先查 `_disposed`（否则 marshal 到已销毁的 Dispatcher ⇒ 异常） |
| T-4 | 探针收尾（未 `Show()`）| `Close()` 不触发 `Closed` ⇒ 必须显式 `DisposeForProbe()`（既有兜底，照抄） |
| T-5 | apps 后台扫描与进程退出竞争 | `ScanAsync` 内不得触碰已释放资源；扫描结果写静态缓存（无依赖）⇒ 天然安全 |
| T-6 | 图标提取线程 | 在渲染（UI）线程按需提取（`SHGetFileInfo` 是毫秒级、单次），**不在后台预取全部**（预取是 2000 次调用 = 句柄压力） |
| T-7 | 频次落盘 | 后台线程 + 2s 去抖；宿主退出时 `flush` 必须**有界等待**（≤500ms），超时则放弃（**不得让退出被磁盘 IO 拖住**） |
| T-8 | 单实例约束 | 桌面宿主单实例（既有 `SingleInstance.cs`）⇒ 频次文件的写入者唯一，不需要跨进程锁；若将来放开多实例，D6 复议结论需重评 |
| T-9 | 键盘链观测面 | 任何新增观测都必须读 `NativeInputBox.LastVk` / `IsInputFocused`（**跟控件走**），**禁止**在 WPF 主窗挂钩子 —— 挂上去在原生 EDIT 架构下恒为空（踩坑全集 §2.27④ 死观测面） |

### 11.5 资源边界（句柄 / 位图 / 进程）

| 资源 | 风险 | 规则 | 自测 |
|---|---|---|---|
| `HICON`（`SHGetFileInfo` 出参） | 每个未 `DestroyIcon` 的图标 = 1 个 GDI 句柄泄漏 | `DestroyIcon` 走 **try/finally**；转换封装成 `IconCache.GetIcon()` 唯一入口 | `--probe-launcher --apps`：连续 200 次后 `GetProcessHandleCount` 斜率 = 0 |
| `BitmapSource`（`CreateBitmapSourceFromHIcon`） | 句柄表 `+0/+8` 恒为 0（新版 Windows）⇒ **按对象作键不可行**（RI-6 教训） | 只按**路径**作缓存键；`Freeze()` 后跨线程安全 | 缓存条目数 ≤ 512（R11） |
| 启动的应用进程 | `Process.Start` 返回的 `Process` 对象若不 `Dispose` ⇒ 句柄累积 | `using var _ = Process.Start(...)`（**照抄既有 `OpenFile`**） | 复用既有断言面（`actions` 块） |
| 频次文件 | 原子写失败留下临时文件 | 写临时文件 + `File.Replace`；失败清临时文件 + 日志 | 手测（权限受限目录） |
| 临时探针目录（`--apps` 自建） | 残留 | 探针 `finally` 删除临时目录 | 探针 rc + 目录不存在断言 |

### 11.6 配置边界

| # | 边界 | 规则 |
|---|---|---|
| G-1 | `launcher.providers` 键缺失 | 回落默认（**不报错**：这是正常状态） |
| G-2 | 键存在但类型不是 string（手改为 `[]`/`123`） | **必须与"键缺失"区分**：`HostSettingsSchema.TryGetString` 对两者都返回 `null` ⇒ 解析器必须自己看 `Effective(...)` 的原始节点判类型，否则"类型写错"会被静默当成"没配"（S2 家族）。类型错 ⇒ 报错 + 回落默认 |
| G-3 | 值含未知 provider | 报错（含已知集合），回落默认 |
| G-4 | 值空串 / 全空白 | 报错 `至少启用一个 provider`，回落默认 |
| G-5 | 值里含 `unit`/`encode`（W7-d 前） | 报错 `未知 provider "unit"（已知：files、apps、calc）` —— 白名单 = `KnownIds`（§10.2 注） |
| G-6 | 保存路径 | 设置窗口（schema 驱动渲染 ⇒ 新键自动出现，**W6-b 已核实结论**）+ CLI `ezt config get/set/unset desktop launcher.providers` 双向闭环（RI-3 教训：**CLI 白名单式构造会吞字段** ⇒ 必须两层都测） |
| G-7 | 改动生效时机 | **下次唤出**（与 `color.format` 同款口径）；不做运行时热插拔（§2.3）。★ **2026-10-02 更新**：W7 收口时为"需重启托盘"（实际行为，`EnsureSearchWindow` 一次性消费配置）⇒ 该缺陷**已由 W8 · B2 修复并落地**，现在是**真正的"下次唤出"**（重建窗口）—— 见 `docs/W8-可用性回填-设计方案.md` §4.4 / §7。**本条的口径（不做热插拔）不变** |
| G-8 | 告警只出一次 | 同一非法配置在**一次宿主生命周期内只告警一次**（用 `_configWarned` 标志），否则每次唤出都刷状态行 = 噪声 |

### 11.7 环境与工程边界

| # | 边界 | 规则 / 依据 |
|---|---|---|
| E-1 | 多 TFM 残留死产物 | 每阶段验收前核对二进制时间戳 + 验收脚本 `$TFM`；**两个观测面矛盾时先怀疑"看的不是同一个东西"**（踩坑全集 §2.31，已踩 3 次） |
| E-2 | 沙箱内进程枚举恒 0 | 探针判据不依赖进程列表（R8）：apps 启动动作**只断言 `ProcessStartInfo` 构造**，不真起进程 |
| E-3 | 热键占用漂移 | 本波**不新增热键**，天然规避；但验收跑热键相关项前仍按既有纪律重探（`scripts/probe-hotkey-free.py`） |
| E-4 | 键盘链"环境级吞键"旧结论已作废 | 若真机出现吞键（`DiagPath` 里 `KEY` 行缺失可判），**按"单入口 + 去重闸"补定向兜底，而不是把兜底堆回去**（踩坑全集 §2.29） |
| E-5 | 结构断言须剥注释再匹配 | 若为 W7 加"源码结构断言"（如"`NativeInputBox.cs` 不得出现 `Keyboard`"），必须先剥注释/字符串再匹配（§2.30 教训） |
| E-6 | 编译警告基线 | Host/Desktop 新增代码不得引入 CA1416（平台兼容）等新告警 ⇒ 这正是不把 Win32 provider 放 Host 的原因（§3.2） |
| E-7 | apps 首扫竞态补发 | 见 R10：`TextUnchangedSinceDispatch && Visible` ⇒ 补发一次；探针可断言（注入一个"延迟就绪"的假 provider） |

## 12. 依赖关系

### 12.1 程序集依赖（新增边 / 禁止边）

| 边 | 类型 | 说明 |
|---|---|---|
| `Eztools.Host` → `Eztools.Contracts` | 既有 | `InputThrottle`/`ProtocolMethods`/`RpcErrorCodes`/`JsonNodeExtensions` |
| `Eztools.Desktop` → `Eztools.Host` | 既有 | 新代码沿用它拿 `Launcher/*` 与 `Search/*` |
| `Eztools.Cli` → `Eztools.Host` | 既有 | **selftest 能测 `Launcher/*` 纯函数的前提**（这就是纯逻辑放 Host 的原因） |
| `Eztools.Host/Launcher` → `Eztools.Host/Search` | **新增（内部）** | `FilesProvider` 用 `SearchIndexClient`/`SearchHitDto` |
| `Eztools.Host` → `Microsoft.Data.Sqlite`（仅 D6=A 时） | **可能新增** | D6 复议建议避免（§8 D6） |
| `Eztools.Host` → `System.Windows.*` | 🔴 **禁止** | 破坏分层 + selftest 不可测 ⇒ §12.5 机器化守卫 |
| `Eztools.Host` → `System.Windows.Forms.*` | 🔴 **禁止** | 同上 |
| `Eztools.Index` / `Eztools.Core` ← 任何 W7 代码 | 🔴 **禁止（反向）** | NFR-2 零特权；files 能力的唯一入口是 `SearchIndexClient` |
| `Eztools.Desktop/NativeInputBox.cs` 被任何 W7 代码修改 | 🔴 **禁止** | NFR-3 / FR-11 |

### 12.2 上游契约依赖（逐条：用什么、怎么用、稳定性承诺）

| 上游 | 具体成员 | W7 怎么用 | 变更影响 |
|---|---|---|---|
| `docs/W3-搜索协议.md` §3 | `search.query`（`q`/`substr`/`limit`/`epoch`） | 经 `SearchIndexClient` 透传，**不直接拼 JSON** | 协议冻结；改动需双端同步 |
| `SearchIndexClient` | `QueryAsync` / `IsStale` / `LatestEpoch` / `StatusAsync` / `PauseIndexingAsync` | files 的唯一查询入口；`IsStale` 保留为过期闸 | ★ 公共 API 冻结（W3 已收口） |
| `SearchHitDto` / `SearchQueryResponse` | 五元组 / `Epoch`/`Total`/`ElapsedMs`/`Hits` | 映射进 `LauncherItem.FileHit` | ★ 冻结（新增字段需两侧同步） |
| `InputThrottle` | `Report`/`Fire`/`Reset` + `DebounceMs`/`MinIntervalMs` | `QueryPump` 复用其机制；**常量按链路重新论证**（`SearchSession` 注释已有先例：30/80 ≠ 150/80） | 通用类，改动影响面板 |
| `NativeInputBox.InputCommand` | `Enter`/`CtrlEnter`/`Escape`/`Up`/`Down`/`Delete`/`CtrlP`/`CtrlC` | 命令语义表新增"按 Kind 分派"，**枚举本身不改** | ★ 冻结（§2.30 换实现刚收口） |
| `HostSettingsSchema` | schema JSON + `SectionId` + `TryGet*` | 新增 `launcher.*` 两键（schema 驱动 ⇒ 设置窗口自动渲染） | 加法改动 |
| `ConfigStore.Effective/Set` | 生效值合并 + 原子写 | `LauncherPrefs` 读；`LauncherUsageStore` 不复用它（数据不是配置） | 冻结 |
| `EztoolsPaths` | `ConfigRoot` / `LogsDir` | 频次文件与日志路径 | 新增属性可选（D6=B 时需要 `<ConfigRoot>/launcher/`） |
| `scripts/verify-desktop.py` | `run()` / `ck()` / 探针 JSON 约定 | 新增 launcher 段 | 自身是 W7 的交付物之一 |

### 12.3 下游消费者（改动的波及面）

| 消费者 | 会被 W7 影响到什么 | 必须做的事 |
|---|---|---|
| `SearchWindow` | 会话→router、模板分流、动作分派、`SelectedHit` 读法 | W7-a 逐个对照 §10.9 B 零行为表 |
| `TrayApplication` | provider 装配 + 托盘/气泡/selfcheck 文案 + 退出 flush 频次 | 文案三处同步（§5 托盘行） |
| `SearchUiProbe` / `SearchSummonProbe` | **零改动**（唯一变化 = 构造时注入"仅 files"集合） | 断言集合不变（FR-10 证据） |
| `SelfTestCommand` | 新增断言段（25.x 6 → 10 条） | 阶段门禁 |
| `scripts/verify-desktop.py` / `scripts/acceptance.sh` | 新增探针条目 ⇒ 满额递增 | 引用数字前看最近输出 |
| `SettingsWindow` | **零改动**（schema 驱动，W6-b 已核实） | W7-d 落地时抽查一次新键渲染 |
| 托盘 `--probe-tray-items` | 文案变更不影响其断言（只断言截图/取色） | 已核查（§5） |
| W3 手工验收清单 | M2 键盘链需在 W7-a/W7-c 各抽查一次 | 手工项 |

### 12.4 跨进程依赖

```
Eztools.Desktop ── stdio（JSON-RPC 2.0，单在途，不并发写 stdin）──> ezt-index.exe
                                                                    （★ W7 一行不改）
Eztools.Desktop ── CLI 调用 ──> ezt.exe（selftest / config）（仅验收期）
```

- files 的**匹配与排序始终在索引进程**（UI 侧只渲染）—— W7 不改变这条职责边界（`SearchWindow` 类头既有纪律）。
- 索引进程崩溃/未启动 ⇒ 走 §11.1 的"索引不可用"分支，**apps/calc 段不受影响**（这正是 FR-9 的价值）。

### 12.5 反向依赖守卫（怎么机器化）

| 守卫 | 判据 | 落点 |
|---|---|---|
| 分层守卫 | `grep -n 'System\.Windows\|System\.Windows\.Forms' src/Eztools.Host/Launcher/` ⇒ **0 命中** | `scripts/review-guards.py` 新增 G7（或 selftest 一条结构断言） |
| 键盘链守卫 | `git diff --exit-code src/Eztools.Desktop/NativeInputBox.cs`（W7 全程） | 阶段门禁（人工执行 + 落地记录留痕） |
| 探针零迁移守卫 | `git diff scripts/verify-desktop.py` 的 search 段**无删改行** | W7-a 门禁（§9.1 末行） |
| 死产物守卫 | 既有 G5（TFM 死产物） | 既有 |
| Core 无引用守卫 | `grep -rn 'Eztools.Core' src/Eztools.Host/Launcher/ src/Eztools.Desktop/Launcher/` ⇒ 0 命中 | 阶段门禁 |

## 13. 技术选型（对齐 `docs/W6-视觉小波-设计方案.md` §10 的颗粒度）

| 项 | 选型 | 理由 | 备选 / 红线 |
|---|---|---|---|
| provider 抽象 | `ILauncherProvider` + `LauncherItem` 统一模型（§10.2/§10.3） | 五源共享会话/渲染/动作/探针；接口小（3 成员） | 备选：每源自带窗口（= 复制 5 份壳，禁） |
| 结果渲染分流 | `LauncherRowTemplateSelector`：File → 既有 `HitText`（零改动）/ 其他 → 新 `LauncherRowText` | FR-10 的**实现手段**：保住探针断言，就保住了"零行为"证据 | 🔴 禁"统一改成一个新模板"（断言全迁移，证据链断） |
| calc | 自写递归下降（D3=A，~200 行） | 零依赖 + 断言可控（错误码/位置能直接断言，第三方库的错误模型不受我们控制） | 🔴 红线：**必须有长度上限与递归深度上限**（C-11/C-12，`StackOverflowException` 不可捕获） |
| apps 扫描 | `Directory.EnumerateFiles` + `SpecialFolder` + `Microsoft.Win32.Registry`（Desktop 侧） | 零依赖；`App Paths` 只有注册表一条路 | 🔴 禁 `WScript.Shell` 动态 COM（`Type.GetTypeFromProgID` 属**动态解析面**，安全专项 红线 1 的成因）；禁 `IShellLinkW`（D7=A） |
| apps 匹配 | 自写 `FuzzyMatcher` 纯函数（§10.6 C） | 需要"打分 + 高亮区间"两个输出；索引进程的匹配器是文件路径域专用，**不复用也不改它** | 备选：抄索引进程的匹配算法 ⇒ 跨进程/跨域耦合，禁 |
| 图标 | `SHGetFileInfo` + `CreateBitmapSourceFromHIcon` + `DestroyIcon`（try/finally） | WPF 栈内建零新依赖；16×16 是 Shell 标准小图标 | 🔴 `DestroyIcon` 必须 try/finally（R4）；禁 `ExtractIconEx`（同样要句柄纪律但无必要） |
| 图标缓存 | 进程级字典 + LRU 512 | 用户实际只翻前几十条；上限防"大型软件环境内存累积" | 备选：不缓存（每次提取 ⇒ 每次渲染都付 Shell 调用成本，200 条时卡） |
| apps 缓存 | 内存 + 目录指纹 + 10 分钟兜底（D8=A） | 全扫成本数十毫秒且在后台；落盘缓存要付一致性 bug | 备选：落盘（D8=B，复活条件 = 冷启动感知到慢） |
| 频次记忆 | JSON + 原子写 + 2s 去抖（D6 复议推荐 B） | 单实例 ⇒ 无并发；规模 ≤ 千级；零新依赖 | 备选：SQLite（§8 D6 复议表） |
| 配置 | `HostSettingsSchema` 加键 + `LauncherPrefs.Parse` 纯函数（§5 表） | schema 驱动 ⇒ 设置窗口零改动；解析规则可 selftest | 🔴 禁"未知值静默忽略"（S2） |
| 探针 | 新 `--probe-launcher`（子开关）**叠加**在 `--probe-search-*` 之上 | 老探针不动 = 回归证据；新探针独立演进 | 🔴 探针判据用 rc + 落盘 JSON，禁 `cmd \| grep -q` |
| 断言（selftest） | calc/unit/encode/prefs/usage 的**纯函数精确值表** | 期望值人工手算，禁"算完跟自己比"（`验收断言审视清单.md` §2.17③） | 🔴 非法输入必须"响亮失败"（不许 `:-0`） |

## 14. 复用清单（文件 / 成员级映射）

> 动手前按图索骥。格式对齐 `docs/W6-视觉小波-设计方案.md` §11。

| 复用项 | 出处 | W7 用法与注意点 |
|---|---|---|
| 原 `SearchSession` 的三层收敛（节流 / 单在途+trailing / 空查询本地回调）+ 过期闸 + 错误分层 | 原名 `src/Eztools.Host/Search/SearchSession.cs` —— 已删除；逻辑现居 `（新建）src/Eztools.Host/Launcher/QueryPump.cs` 与 `（新建）src/Eztools.Host/Launcher/FilesProvider.cs` | **W7-a 平移（不重写）**；原 5 条用例语义平移进 25.x（同一判据、同一断言内容），行为等价证据见 §6 落地偏差① |
| `ISearchIndexTransport` / `SearchIndexClient` / `SearchHitDto` | `src/Eztools.Host/Search/SearchIndexClient.cs` | `FilesProvider` 只用它们；**不改** |
| `SearchIndexProcess`（懒启动 / 单在途写 stdin / 60s 往返超时 / 优雅停） | `src/Eztools.Host/Search/SearchIndexProcess.cs` | 不动；超时行为属上游保证（§11.1） |
| 结果列表虚拟化三件套（显式 ControlTemplate + `VirtualizingStackPanel` + `CanContentScroll`） | `src/Eztools.Desktop/SearchWindow.cs` | **一行不改**（改了就破坏 W3-d-2 的虚拟化断言） |
| `HitText`（高亮分段 / `[目录]` 尾注 / 路径省略） | `src/Eztools.Desktop/SearchWindow.cs`（内部类） | **零改动**；只改数据模板的绑定路径为 `FileHit`（§10.9 A） |
| `BuildOpenStartInfo` / `BuildRevealStartInfo` / `DescribeOpenFailure` | 同上（`internal static`） | 新增 **path 重载**并让旧的重载委托新的（探针 `actions` 断言面**不变**）；★ `explorer /select` **只认反斜杠**，归一逻辑必须带过去 |
| `ForceForeground`（四级降级）/ `RestoreImeAssociation` / `Summon` / `Toggle` | 同上 | **一行不改**（键盘链相关，最高危） |
| `NativeInputBox`（原生 EDIT / `Command` 回调 / `IsInputFocused` / `InputHwnd` / `LastVk`） | `src/Eztools.Desktop/NativeInputBox.cs` | **一行不改**（NFR-3）；命令语义表由窗口侧扩展 |
| 会话释放纪律（`_sessionDisposed` / `DisposeSession` / `DisposeForProbe`） | `src/Eztools.Desktop/SearchWindow.cs` | router 沿用同款（T-3/T-4） |
| 探针纪律（`ShowForProbe` 屏幕外不抢焦点 / `PumpUntil` 满泵 / `CloseForProbe`） | `src/Eztools.Desktop/SearchUiProbe.cs` | `LauncherUiProbe` 照抄；★ 探针必须注入"仅 files"集合（R13） |
| 假传输夹具（`ProbeSearchUiTransport` 的 200 条 / 中文多段高亮 / `total ≠ hits`） | 同上 | W7-a 直接复用（files 段证据不变） |
| 热键注册 / 仲裁 / 气泡 / `ReRegisterHotkeys` | `src/Eztools.Desktop/TrayApplication.cs` | 不新增热键；但**装配点**在此（provider 列表构造） |
| 托盘菜单构建 + `--probe-tray-items` | 同上 | 菜单文案改「搜索 / 启动器」；探针断言不受影响 |
| 设置 schema 与 schema 驱动渲染 | `src/Eztools.Host/Config/HostSettingsSchema.cs` + `SettingsWindow` | 加键（W7-b 加 `launcher.providers`；W7-e 加 `launcher.usage`）；设置窗口零改动（W6-b 已核实：schema 驱动自动渲染） |
| 配置读写 CLI 闭环 | `src/Eztools.Cli/ConfigCommand.cs` | 验收用 `ezt config get/set/unset desktop launcher.providers`；**注意 RI-3：CLI 白名单式构造会吞字段** ⇒ 两层都测 |
| `GetProcessHandleCount` P/Invoke（泄漏类自测量） | `NativeInterop`（已有） | apps 图标句柄斜率断言直接用 |
| 抽屉式原子写（写临时文件 + `File.Replace`） | 剪贴板/配置中心的既有模式 | 频次文件写盘照抄 |
| 探针热键空闲探测 | `scripts/probe-hotkey-free.py` | 本波不新增热键，仅在验收相关项前按纪律跑 |
| 断言方法论 | `验收断言审视清单.md`（M 系 / S 系） | calc/unit/encode 的精确值断言、非法输入"响亮失败"、跳过必须计数 |

## 15. 命名与术语

| 术语 | 含义 | 不要混用 |
|---|---|---|
| **provider** | 一个结果来源（`ILauncherProvider` 实现） | 不叫 plugin / 插件（§2.3 明确不做第三方插件机制） |
| **段（segment）** | 归并结果里来自同一个 provider 的连续区间 | 不叫"分组"（分组会与 UI 分组概念混） |
| **代次（Generation）** | router 层"第几次派发"的单调计数（R3 防竞态） | ★ 不等于协议层的 **`epoch`**（索引侧请求序号，配对闸用）；两者语义独立，代码里必须用不同名字 |
| **过期闸 / 配对闸** | 过期闸 = `IsStale`（UI 消费层丢弃旧响应）；配对闸 = 响应 `epoch` 必须等于请求 `epoch`（`SearchIndexClient` 内） | 见 `SearchIndexClient` 类头 |
| **pin 段** | 确定性命中段（calc/unit/encode），固定置顶 | 不叫"置顶段"（与 W5 的"置顶条目"混） |
| **静态缺席 / 故障** | 静态缺席 = 未启用或未就绪（无 UI）；故障 = provider 抛异常（进 `Errors`，可见） | 二者**必须可区分**（I6 / C5） |
| **零行为** | W7-a 的专有验收口径：既有可观测行为**逐字节不变**，且**断言集合不变** | 不是"大概一样"——判据见 §9.1 |

## 变更记录

| 日期 | 变更 |
|---|---|
| 2026-10-01 | 初版（§1~§9，待拍板 D1~D6） |
| 2026-10-01 | **v2 细化（本轮）**：新增 §0 阅读导航 · §3.2 模块划分与依赖方向 · §3.3 数据流与竞态模型（三层收敛 + **双闸**）· §3.6 装配与生命周期 · §2.1 增 FR-9/FR-10/FR-11 · §2.2 增 NFR-5/NFR-6 并澄清 NFR-1 口径 · §4.2 键位×类型全矩阵 · §4.3 结果行视觉契约 · §5 配置解析规格表 + 探针装配纪律 + 托盘文案影响面核查 · §6 每阶段交付物表 + 落地偏差登记位置 · §7 增 R9~R14 · §8 增 D7~D11 并对 **D6 提出复议（建议改判 JSON）** · §9.1 判据口径表 + §9.2 新增断言清单 · **新增 §10 模块详解**（文件清单 / 接口定义 / 数据结构{字段级} / QueryRouter 12 条不变量 / 五 provider 规格 / 窗口改造对照表 / 配置与频次 / 探针设计）· **新增 §11 异常与边界**（错误分类表 / 故障隔离 / 四个 provider 的边界清单 / 线程·资源·配置·环境边界）· **新增 §12 依赖关系**（程序集依赖表 + 上游契约 + 下游波及面 + 跨进程 + 反向依赖守卫）· **新增 §13 技术选型** · **新增 §14 复用清单** · **新增 §15 命名与术语** |
| 2026-10-01 | v2 待确认项（写作期发现，需菲比拍板）：① **FR-8 口径收窄**（频次只作用 apps 段，files 排序归索引进程）② **FR-6/D11** 文件项是否加徽标（推荐不加，理由 = 保 W7-a 零行为证据）③ **D6 复议**（SQLite → JSON）④ §10.7 C-2 **纯数字不出 calc 行**（与 PowerToys 有意不同）|
| 2026-10-01 | **§8 拍板（菲比：全部按推荐采用）**：D1=A · D2=A · D3=A · D4=A · D5=A · **D6=B（JSON，复议改判采纳）** · D7=A · D8=A · D9=A · D10=A · D11=A；三项口径确认（FR-8 收窄 / calc 纯数字静默 / `launcher.usage` 键延到 W7-e，避"名存实亡 flag"）；**额外采纳两项更优方案**（① 分层守卫落 `scripts/review-guards.py` 新增 **G7**；② `QueryPump` **抽取共用**而非复制）。文档状态 → 🚧 已拍板 · W7-a 实施中 |
| 2026-10-01 | **W7-a 完成（三件套全绿）**：新增 `Eztools.Host/Launcher/` 五件（Contracts / ILauncherProvider / QueryPump / QueryRouter / FilesProvider）；**删 `SearchSession`**（逻辑平移，见 §6 落地偏差①）；`SearchWindow` 接线（`HitText` 零改动，模板改绑 `FileHit`）；两探针只改装配参数、**断言零迁移**；selftest 25.x 6→10；守卫新增 **G7**（+2 突变）。回归：构建 **0 错/7 警告=基线** · selftest **223/0** · 守卫 **23/0 · FAIL=0** · **verify-desktop 181/0** · **acceptance 446/0/1（满额 447）** · `check-doc-refs --strict` **exit 0**。首轮曾因**外部热键占用**（CmdPal/PowerToys 占 `Ctrl+Alt+O/X/C` 等）红 10~12 条，定位与闭环过程见 §6 |
| 2026-10-01 | **W7-b 完成（三件套全绿）**：`Eztools.Host/Launcher/` 增 `FuzzyMatcher` / `LauncherPrefs`（+`ParseNode`/`FromConfig`）· `QueryPump.Requery`；`Eztools.Desktop/Launcher/` 新建 **7 件**（`AppIndexCache` / `AppsProvider` / `IconCache` / `LauncherRowText` / `LauncherRowTemplateSelector` / `LauncherActionRunner` / `LauncherUiProbe`）；`HostSettingsSchema` 加 `launcher.providers`（默认 `files,apps`）；`LauncherProviderSet.Build` 按配置装配；`SearchWindow` 增 `startupWarning` 可见出口 + 渲染日志探针钩子；selftest 增 **W7-1~W7-13**（223→236）；`verify-desktop` 增 launcher 段（181→207，**search 段一行未改**）；W6 遗留 **3 条 WARN 清零** + 逐条热键探针改三态。回归见 §6 落地记录。**5 条落地偏差**（探针形态改五模式 / `--router` 用例重设计 + 突变验证 / 补配置告警出口 / 白名单不预埋未实现 id / `AppIndexCache` 根可注入与溢出判据）逐条登记在 §6。★ 另记一条**假失败**教训：验收运行期间改 `src/` 会触发 `find src -newer` 探针 ⇒ red 1 条（非回归）；**验收开跑后不再改 `src/`** |
| 2026-10-01 | §5/§9/§10.6/§10.10/§10.12 与实际落地对齐：`launcher.providers` 默认 `files,apps`（白名单 = **当前已实现**的 id，不预埋 calc/unit/encode）；探针定名 `--probe-launcher <mode>` 五模式；`AppIndexCache` 接口与三态语义；`LauncherPrefs` 去 `UsageEnabled` 参数 |
| 2026-10-01 | **W7-c 完成（三件套全绿）**：新增 `Eztools.Host/Launcher/CalcExpression.cs`（两层门槛 / 递归下降 / 求值错误码 / 格式化 / 长度+深度双上限，纯函数）与 `CalcProvider.cs`（静默 vs 出行两类失败处置）；`KnownIds` 与 schema 默认值 → `files,apps,calc`；`TrayApplication` 加 calc 工厂；探针加 `calc` 真链路模式 + `SearchWindow.ProbeLeadingKinds`；selftest 增 **34.1~34.17**（236→253）；`verify-desktop` calc 段 +8（207→215，**search 段一行未改**）；acceptance 满额 **481**（下限 201→218 + 34.x 计数守卫）；探针模式由五 → **六**。**4 条实施期调整**（`.5` 前导小数点放宽 / L0 放行科学记数 `e` 且"指数符号不算运算符" / `1e5` 的 UI 静默口径 / 错误行主副行分工）逐条登记在 §6。★ 另记一条断言自查：静默类用例必须**配正向对照**（文件段照常 200 条），否则"整轮没跑"也满足"没有 calc 行" |
| 2026-10-01 | §5/§9.2/§10.7/§11.3/§14 与 W7-c 落地对齐：默认值 `files,apps,calc`；EBNF 增 `.digit+` 分支；新增 §10.7 A′「落地补充五条口径」；C-4 注明"合法指解析层，UI 单独输入不出行"；§10.12 探针表加 `calc` 行 |
| 2026-10-01 | **W7-d 完成（三件套全绿）**：新增 `Eztools.Host/Launcher/UnitTable.cs`（四类单位表 + 分词"整串必须被消费" + **温度仿射单独路径** + 常用单位集）· `UnitProvider.cs`（触发词 `to`/`->`/`转` 三态等价；无触发词列常用单位且**不含源单位**）· `EncodeProvider.cs`（六前缀双向；**非法输入 ⇒ 显式错误行 + 动作禁用**；残缺 `%XX` 自查；Unicode 按码点走，emoji 不给半截码位）；白名单/schema 默认值 → **五来源**；探针加 `unit` / `encode` / `config` 三模式；**回填** calc 次动作（§4.2：Ctrl+Enter 复制「表达式 = 结果」整串，W7-c 曾错写）；新增 `SearchWindow.StartupWarningFor`（告警文案单点）；acceptance 步骤 8 新增**两层闭环四态**（合法/未知/空/unset）。回归：构建 **0 错/7 警告=基线** · selftest **271/0**（253→271，+35.x×18）· verify-desktop **229/0**（215→229，**search 段一行未改**，连跑 3 次全绿）· acceptance **498/0/1 满额 499 · rc=0** · 守卫 FAIL=0/WARN=0 · `check-doc-refs --strict` exit 0。**4 条实施期调整 + 2 处探针修正**（无触发词不含源单位 / calc 次动作回填 / 告警文案单点 / encode 错误行主副行分工 + 探针就绪信号改渲染日志 / 剪贴板清空+有限重试）逐条登记在 §6 |
| 2026-10-01 | §4.2/§5/§9.2/§10.8/§10.12 与 W7-d 落地对齐：默认值五来源；§10.8 增 **B′ 落地补充六条口径**（不含源单位 / 重量扩 4 / 整串消费 / 显式字符集 / 复用 Format / 与 calc 互斥证明）；§10.12 探针表补 `unit`/`encode`/`config` 行与"就绪信号必须用渲染日志"纪律 
| 2026-10-01 | **W7-e 完成（三件套全绿，W7 全波收口）**：新增 `LauncherUsageStore`（JSON 原子写 + 2s 去抖 + 有界 flush；Boost 纯函数 = min(400, 100·log2(1+count))×衰减(7 天内 1.0 → 90 天后 0.25 地板)）；`LauncherPrefs` 加 `UsageEnabled`/`ParseUsage` + `LauncherAliases`（「别名=目标」全等匹配）；`AppsProvider` 接入频次/别名加成（**取 max 不叠加**）；`SearchWindow.UsageRecorder` 出口（动作成功才 count+1，探针默认零写入）；schema 加 `launcher.usage`/`launcher.alias` 两键（设置清单 desktop 12→14 字段）；探针 `usage` 模式（十模式）+ `config` 扩展；acceptance 8.5c 两层闭环四态；**手工清单 `docs/W7-手工验收清单.md`（新建，M1~M6）**。回归：构建 0 错/7 警告=基线 · selftest **281/0**（+36.x ×10）· verify-desktop **233/0**（连跑 3 次，search 段一行未改）· acceptance 满额 rc=0 · 守卫 FAIL=0/WARN=0 · `check-doc-refs --strict` exit 0。**4 条落地偏差 + 1 条真 bug**（频次只记 apps 段 / alias 格式补齐 / max 不叠加 / 失败不记频次；BoostFor 的 `_loaded` 闸让"记完立刻查"拿不到自己的分 —— 探针真链路当场抓到）登记在 §6 ||