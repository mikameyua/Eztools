# W4 · 屏幕 OCR（框选取字）设计方案

> **文档类型**：③ 功能设计（架构权威：选型 / 模块 / 流程 / 风险）
> **状态**：✅ 已实施并收口（W4-a / W4-b / W4-c 全部落地，2026-09-26；落地记录见 §7）
> **创建**：2026-09-25 · 权威链：`Eztools-设计方案.md` > 本文档
> **上游依据**：屏幕 OCR 选型调研（2026-09-25，GitHub API 实测）+ MIT 项目横向比较结论（见工作日志 2026-09-25）
> **实现纪律**：分步验收细节以本文 §7 为执行权威；开始前先读 §8 风险表与 `验收断言审视清单.md`

---

## 1. 功能概述

**一句话**：全局热键唤出全屏遮罩 → 拖拽框选屏幕任意区域（图片 / 视频 / PDF 截图，对引擎而言都是 bitmap）→ 系统**本地离线** OCR → 文字进剪贴板。

- **产品定位**：对标 PowerToys Text Extractor，是 Eztools「唤出式工具」家族第二员（快搜窗之后）。
- **设计哲学对齐**：单职责窗口、快进快出、零特权（不碰 Core）、全离线（无网络、无模型文件、无第三方运行时）。
- **外部参考收敛为两条**（2026-09-25 已拍板裁剪）：
  1. **PowerToys PowerOCR 源码**（MIT，工作区自带 `PowerToys/src/modules/PowerOCR/`）——只抄三个机制点：per-monitor DPI 坐标映射、EnhancedScale 小字放大、遮罩窗交互骨架。**不整模块照搬**。
  2. **Text-Grab 交互契约**（MIT，C#/WPF/.NET 10）——框选窗只做「选区→OCR→剪贴板」一件事；空选区窗口保持活动可重试；Esc 退出；单击 word 包围盒直接复制单词。
- **已裁剪的非目标**（决策记录，勿在实施时复活）：后处理编辑窗、排版解析（降为已知局限）、热键冲突自愈 UI、批量/PDF/二维码识别、翻译。

## 2. 需求说明

### 2.1 功能需求（FR）

| # | 需求 | 验收口径 |
|---|------|----------|
| FR-1 | 全局热键唤出 / Esc 退出遮罩；**每个显示器一扇遮罩窗**（多屏全覆盖） | 手工清单 §W4 条目 |
| FR-2 | 拖拽框选，实时高亮选区并显示像素尺寸 | F6 手测 |
| FR-3 | 松开鼠标即截图 → OCR → 文字进剪贴板，气泡轻提示"已复制 N 字符" | 手测 + 探针 |
| FR-4 | 空选区 / 选区内无文字：窗口保持活动可重试，不闪退 | Text-Grab 契约，手测 |
| FR-5 | 单击（非拖拽）命中单词：按 word 包围盒复制该词 | word box 由引擎免费提供 |
| FR-6 | 多显示器 + 混合缩放（100%/150%/200% 混搭）坐标正确 | 手工清单双屏缩放条目 |
| FR-7 | 语言包缺失时给出明确引导（`Add-WindowsCapability`），不静默失败 | 探针输出 `languages=0` 触发引导文案 |
| FR-8 | CLI：`ezt ocr langs`（列可用语言）/ `ezt ocr file <图片路径>`（对图 OCR） | 断言落退出码 + stdout 数字 |
| FR-9 | 热键与默认语言可在设置中配置 | 复用 P1a 配置中心 |

### 2.2 非功能需求（NFR）

- **NFR-1 全离线**：任何路径不发起网络请求。
- **NFR-2 零特权**：不申请提权，代码永不进入 Eztools.Core。
- **NFR-3 零依赖**：不引入模型文件、Python、ONNX 等第三方运行时；新增 NuGet 包需在 §6 论证。
- **NFR-4 性能**：唤出到遮罩可框选 ≤300ms；常规屏幕区域（<1/4 屏）OCR ≤500ms 量级（W4-a 实测校准后固化数字）。

### 2.3 非目标（Non-goals，v1 明确不做）

| 不做 | 理由 | 复活条件 |
|------|------|----------|
| 排版解析 / 多栏重排 | 系统引擎只给行级 box，多栏会乱序——**文档级已知局限**，标注于结果提示 | PDF 长文档截图成为高频需求 |
| 后处理编辑窗 | scope creep；剪贴板即终点 | 独立工具另行立项 |
| 批量图片 / PDF 原生 / 二维码 | 与"框选屏幕"场景无关 | — |
| 远程 OCR / 翻译 | 违反 NFR-1 | 永久排除除非推翻哲学 |

## 3. 整体架构

### 3.1 进程与项目落位

```
Eztools.Desktop（现有宿主：托盘 + 快搜窗，同进程模型不变）
 ├─ HotkeyHook（现有：RegisterHotKey + WM_HOTKEY 分发）      ← 复用，OCR 热键注册进来
 ├─ HotkeyArbitration（现有：占用仲裁 + 失败气泡提示）        ← 复用，kiri 式处理已内建
 ├─ OcrOverlayWindow（新增：每显示器一扇遮罩窗，与 SearchWindow 同层）
 └─ 引用 Eztools.Ocr

Eztools.Ocr（新增类库：TFM net10.0-windows10.0.19041.0）
 ├─ CaptureService（多屏枚举 / DPI / GDI 截图，物理像素）
 ├─ WindowsOcrEngine（引擎生命周期 / 语言 / EnhancedScale）
 └─ ResultTextBuilder（行拼装 / word 命中）

Eztools.Cli（现有）
 └─ `ezt ocr langs|file` 子命令，引用 Eztools.Ocr
```

**落位原则**：UI 归 Desktop、原语归 Ocr 类库、脚本入口归 Cli；不新建进程，不加 Python 边界，不动 Eztools.Core。

### 3.2 数据流（全链路）

```
热键 → OverlayManager（按 MonitorInfo 逐屏开窗）→ 拖拽得选区(物理像素 Rect)
  → CaptureService.GrabRegion(rect) → Bitmap(物理像素)
  → WindowsOcrEngine.Recognize(bitmap, lang)（必要时 EnhancedScale 放大）
  → OcrResult { Lines[] { Text, WordBoxes[] } }
  → ResultTextBuilder → string
  → Clipboard.SetText → 气泡反馈 → 遮罩窗全部退出
```

**坐标纪律（一条红线）**：全程**物理像素**，逻辑像素只允许存在于 WPF 窗口事件的最里层转换点。混合缩放错位是本功能头号 bug 源，坐标体系唯一化是防线。

## 4. 模块划分

| 模块 | 职责 | 借鉴来源（文件级映射见 §9） |
|------|------|------------------------------|
| `CaptureService` | 显示器枚举（含 DPI）、区域截图、光标裁剪 | PowerOCR `ScreenCaptureService` / `DisplayCapture` / `CursorClipper` |
| `WindowsOcrEngine` | 引擎创建与复用、语言选择与回退、EnhancedScale（上限 `OcrEngine.MaxImageDimension`）、语言包缺失检测 | PowerOCR `WindowsOcrRecognizer` / `TextExtractorService` |
| `OcrOverlayWindow` ×N | 遮罩渲染、拖拽选区、Esc/右键取消、空选区重试、单击取词 | PowerOCR overlay **交互逻辑** + Text-Grab 契约；**XAML 不抄**（见 §6 勘误） |
| `ResultTextBuilder` | 行序拼装、换行策略、单击命中判定 | 自研（量小） |
| `ClipboardOutput` | 文本写入 + 失败重试 + 字符计数反馈 | 自研 |
| 接线层 | 热键注册、托盘菜单项、设置项、气泡 | 全部复用现有基础设施 |

## 5. 核心业务流程

### 5.1 主流程（成功路径）

1. 用户按热键 → `HotkeyHook` 分发 → `OverlayManager` 为每台显示器创建全屏置顶遮罩窗（`ShowActivated`，抢焦点仅主屏）。
2. 用户在任一屏拖拽 → 遮罩实时显示选区高亮 + 像素尺寸 → 松开。
3. `CaptureService.GrabRegion` 截取物理像素位图（裁掉光标）→ 交给引擎。
4. 引擎识别 → `ResultTextBuilder` 拼装 → 写剪贴板 → 气泡"已复制 N 字符" → 所有遮罩窗退出。
5. 用户在目标处 Ctrl+V，链路结束。

### 5.2 异常路径

| 场景 | 行为 |
|------|------|
| 选区为空 / 无文字 | 窗口保持活动，状态提示"未识别到文字，重新框选"，Esc 才退出（FR-4） |
| Esc / 右键 / Alt+F4 | 全部遮罩窗退出，无任何剪贴板写入 |
| `AvailableRecognizerLanguages` 为空 | 唤出时即拦截：气泡给出"安装语言包"引导文案，日志落 Error（禁静默，S 系红线） |
| 识别抛异常 | 气泡提示失败原因，窗口保持活动；异常必须带 Error 出口，禁止吞掉 |
| 剪贴板写入失败 | 重试 ≤3 次（内部剪贴板竞争），仍失败则气泡报错 |

## 6. 技术栈选型

| 项 | 选型 | 理由 | 适配性 / 备选 |
|----|------|------|----------------|
| OCR 引擎 | **`Windows.Media.Ocr`（WinRT，系统内置）** | 零模型零依赖、全离线、屏幕 UI 文字精度好；被 Text-Grab（5.0k★）与 PowerToys PowerOCR **双实现长期验证**；调研报告结论：Windows 专属产品 = 最优路径 | 备选 RapidOcrNet（Apache-2.0, ONNX）仅在实测精度不达标时启用，触发条件写入 §8 |
| WinRT 投影 | **独立项目 TFM `net10.0-windows10.0.19041.0`**，SDK 内置投影 | SDK 对带 Windows 版本号的 TFM 自动注入 WinRT 投影，**无需 Microsoft.Windows.CsWinRT 包**（PowerToys 用 CsWinRT 是其 AOT + 中央 TFM 约束，本项目无此需求） | ★ **落地修订（2026-09-26，W4-a）**：引用链要求 **Eztools.Cli 也同步升级到同款 TFM**（net10.0 引用不了 net10.0-windows10.0.19041.0），`acceptance.sh` / `budget.sh` / `probe-readmft-continuation.py` 的 TFM 默认值随之更新；Desktop 保持 `net10.0-windows` 不动 |
| UI | **WPF 遮罩窗**（Desktop 现栈） | 与 SearchWindow 同进程同栈，复用全部唤出窗经验 | ⚠️ **勘误**：PowerToys PowerOCR 主程序实为 **WinUI3**（`UseWinUI=true`），其 XAML/遮罩代码不可直接照搬——只借鉴其**交互逻辑与坐标机制**；WPF 遮罩的真正同栈参考是 Text-Grab（MIT，真 WPF）。其 `PowerOCR.Core` 库（截图/引擎）与 UI 栈无关，可整段借鉴 |
| 截图 | **GDI `Graphics.CopyFromScreen`（物理像素）** | 静态单帧截图的最短路径；`Windows.Graphics.Capture` 引入的 WinRT 会话复杂度对"截一次静态图"是过度设计 | PowerOCR 同款思路 |
| 剪贴板 | `System.Windows.Clipboard` | WPF 栈内建，STA 天然满足 | — |
| 热键 / 设置 / 托盘 | 复用 `HotkeyHook` + `HotkeyArbitration` + P1a 配置中心 | "热键被占用→气泡提示"已实现，无需新增 | — |

**协议合规**：PowerOCR / Text-Grab 均 MIT，代码借鉴须在 `THIRD-PARTY-NOTICES.md` 登记 attribution。NormCap / ShareX / eSearch 为 GPL，**一个字不抄**。

## 7. 实现步骤（Wave 4）

> 门禁纪律：上阶段断言全绿才进下阶段；每阶段产出直接进验收体系（acceptance.sh / verify-desktop / 手工清单）。

### W4-a 引擎原语 + CLI 探针（先行，无 UI）

| 项 | 内容 |
|----|------|
| 任务 | 新建 `Eztools.Ocr`（TFM `net10.0-windows10.0.19041.0`）；`WindowsOcrEngine`（创建/语言/EnhancedScale/MaxImageDimension）；`CaptureService` 截图原语；语言包缺失检测；Cli 注册 `ezt ocr langs` / `ezt ocr file`；探针 `--probe-ocr`（引擎自检：语言数、对内置样图识别字数、耗时） |
| 依赖 | 无（不依赖 Desktop，不受单实例锁影响——§G2-a 前科规避） |
| 产出 | ① CLI 断言全绿（退出码 + stdout 数字）② 本机 OCR 精度与耗时基线记录（中文/英文/小字三样张）③ 语言包环境结论 |
| 风险关卡 | **若中文精度基线不达标（误字率明显劣于预期）→ 此处即触发 RapidOcrNet 备选评估点**，UI 不开工 |

> ### ✅ W4-a 落地记录（2026-09-26）
>
> **产出**：`src/Eztools.Ocr/`（csproj + OcrModels + BitmapPreprocessor + WindowsOcrEngine + ScreenCapture + SampleImage）、`src/Eztools.Cli/OcrCommand.cs`、Program 接线 + 帮助、`Eztools.sln` 注册、三个脚本的 TFM 默认值、`THIRD-PARTY-NOTICES.md` §3.1 attribution。
>
> **本机实测基线**（语言包 **已装 2 个：en-US / zh-Hans-CN**，R1 未触发）：
>
> | 指标 | 实测值 |
> |---|---|
> | 引擎创建 | 2 ms |
> | 样图识别（"EZTOOLS OCR 2026"，880×220 @56px） | **16/16 字符全对**，92~119 ms |
> | EnhancedScale | 1.5× 生效；MaxImageDimension = 10000 |
> | 退出码契约 | langs=0 / probe=0 / file=0（回环 PNG）、缺失文件=4、未知子命令=64 全部命中 |
>
> **发现并修复的既有问题**（非本任务引入，全量验证挖出）：`QueryEngine.cs` 16:37 引入 RootFrn=5 硬判停（§2.24③）后 **selftest 28.5 夹具未同步**（夹具链穿越 FRN=5 → deep 被截短），且该改动从未被回归（上次 184/184 跑在 14:43 的旧二进制上）。已按生产不变量修订夹具（根 FRN=5 不入库、链从 FRN=6 起），selftest **184/184 全绿（rc=0）**。
>
> **设计文档修订**：§6 WinRT 投影行（Cli TFM 同步升级，见上表）。
>
> **已知偏差**：无。`--save-sample` 首版只在人类可读分支生效（--json 下被静默跳过），实现当日已修复并回归。

### W4-b 遮罩框选窗

| 项 | 内容 |
|----|------|
| 任务 | `OcrOverlayWindow`（多屏、拖拽高亮、Esc/右键、空选区重试、单击取词）+ `CaptureService.GrabRegion` 接线 + 剪贴板输出与反馈；探针 `--probe-ocr-overlay`（唤出→模拟框选→剪贴板断言，类比 `--probe-search-summon` 模式，含真键注入端到端） |
| 依赖 | W4-a 全绿 |
| 产出 | 探针断言 + `W4-手工验收清单.md` 新建（含双屏混合缩放条目）+ F6 手测记录 |

> ### ✅ W4-b 落地记录（2026-09-26 · 全部完成）
>
> **产出**：`src/Eztools.Desktop/OcrOverlayWindow.cs`（单屏遮罩窗：拖拽高亮/Esc·右键·Alt+F4 取消/空选区重试/单击取词/剪贴板重试）、`OcrOverlayManager.cs`（每显示器一扇、完成语义三态 copied·cancelled·retry）、`OcrOverlayProbe.cs` + `--probe-ocr-overlay`（**真鼠标注入**端到端）、`DesktopOptions`/`TrayApplication` 接线、`Eztools.Desktop.csproj` TFM 升级（修订②：引用链推翻"Desktop 不动"）+ `acceptance.sh`/`verify-desktop.py` 路径同步、`W4-手工验收清单.md`。
>
> **探针覆盖面（最终形态）**：① 唤出可见（monitors=t0Visible）② 真鼠标拖拽→copied（剪贴板=屏幕真实文字）③ 空选区→Esc 收窗 ④ 复现性（再唤出→Esc）⑤ **单击取词双覆盖**：确定性面（FindWordAt 变换链自洽：词框中心反查必命中）+ 真链路面（多候选点真击，copied 分支剪贴板=屏幕真实文件名）⑥ 每窗 GetDpiForWindow 缩放比落盘（R2 机制证据）。
>
> **最终实测（含手工项自动化替身）**：`ok=true`，drag.branch=copied、clickWord.deterministic.matched=true、realClick 多候选（copied="OcrOverlayProbe.cs"）、escClosed/reshowEscClosed=true、**m2EmptyResultHint=true**（空白位图喂链 → 提示+保持活动）、**m5RightClickClosed=true**、**m5AltF4Closed=true**、**m6ClipboardContention=true**（持锁 4s → 重试成功收窗）。副作用声明：探针真实移动鼠标、改写剪贴板。
>
> **手工项清点**：唯一真机项 = M1 混合缩放坐标（需物理双屏，当前开发机单屏 N/A；`dpiScales` 机制证据已自动化）；另补 `--ocr-show` 人工模式（唤出后交给真人，手工复核 M2~M6 手感用）。
>
> **实现期发现并修复的探针 bug**（均为测试侧，生产代码未动）：① 自洽测试双重变换（词框中心已是预处理坐标，再 ×Scale 必 miss）② 候选点改绝对坐标后循环残留比率乘法（鼠标被注入到屏幕外）③ 取词 miss 与"区域内没字"不可区分 → 补 `ProbeLastWordDebug` 诊断面。
>
> **★ 探针注入根治 + 外接屏端到端全绿（2026-09-26 02:50）**：双屏手测的低效根源找到 —— 探针 `mouse_event` 绝对坐标缺 `MOUSEEVENTF_VIRTUALDESK`，OS 按主屏映射（实测注入 (996,347) 落到 (569,346) = X÷主缩放 1.75）。补上标志后注入精确落点，探针升级为**双屏全自动化**：每屏分别自校准（词级物理坐标）→ 主屏拖拽/取词 → **外接屏真拖拽（剪贴板=外接屏真实文字「助 理」）+ 真点击取词（抄出终端真实路径）** → 连续模式两次复制 → M2/M5/M6 全绿，`ok=true`。生产"副屏坐标原点"修复被双屏端到端证实。**M1 人工复核降级为可选**（机器判据已全覆盖）。
> **★ M1 双屏混合缩放实测（2026-09-26 01:55，笔记本 175% + 外接 100% 扩展模式）**：探针曾 `ok=true`（monitors=2、t0Visible=2、`dpiScales=[1.75,1]`、placementAllMatch=true），过程中抓出并根治两个问题：① **WPF Window.Left/Top 跨 DPI 多屏 DIP 语义歧义**（副屏窗宽被摆成 2560+1920=4480 物理像素）→ 定位改用 Win32 `SetWindowPos` 直接吃物理像素，绕开 DIP 歧义（R2 根治方案落地）；② 探针对账读数 bug——`GetWindowRect` 出参 RECT{L,T,R,B} 直接 marshal 成 Rectangle{X,Y,W,H}，Width 字段读到的是 Right（主屏 X=0 时侥幸相等掩盖）。
> **★ 双屏实测第二弹（2026-09-26 02:30）—— 抓出并修复生产真 bug**：`CopyRegionAsync`/`CopyWordAtAsync` 把**窗口内部**物理坐标直接当**虚拟桌面**坐标传给 `CopyFromScreen` —— 主屏在 (0,0) 时侥幸相等（单屏全绿从未暴露），窗口原点非零（副屏/双屏）时截图区域整体偏移到别的屏 ⇒ 用户实测"外接屏截不到"。修复：**窗口内部坐标 + 窗口 Win32 原点 = 虚拟桌面坐标**（两处同步修）。
> **探针侧已知限制（不阻塞）**：`mouse_event` 绝对坐标在混合 DPI 下的落点存在 X 方向 ÷主缩放比的系统偏移（多轮数据一致），属注入层映射陷阱 —— 双屏下探针的拖拽/取词注入目标会偏，`ok` 判据在双屏+混合缩放时可能误报 false；生产链路（真鼠标事件坐标）不受影响。单屏探针仍全绿；双屏正确性以 M1 人工对照为准。另：连续模式重唤必须走 Dispatcher 延迟（同步 ShowAll 在旧窗 Close 栈内会卡死消息泵，实测进程挂起 rc=3）。

### W4-c 接线打磨与收口

| 项 | 内容 |
|----|------|
| 任务 | 热键注册进 `HotkeyHook`（仲裁失败→气泡）；托盘菜单项；设置项（热键/默认语言）接 P1a；已知局限标注（多栏乱序提示）；`THIRD-PARTY-NOTICES.md` attribution；acceptance.sh / verify-desktop 扩展；docs/README 索引状态更新；设计方案落地偏差回填 |
| 依赖 | W4-b 全绿 |
| 产出 | acceptance 新增 PASS 条目、verify-desktop 断言、文档收口 |

> ### ✅ W4-c 落地记录（2026-09-26 · 全部完成，W4 就此收口）
>
> **产出**：
> · `src/Eztools.Host/Config/HostSettingsSchema.cs`（宿主设置 schema：desktop 保留节，住在 Host 是因为 **Cli 的 `ezt config` 也要认它** —— "CLI 与设置窗口改同一个文件"的闭环缺一半就是名存实亡）；
> · `DesktopOptions` 新增 `--ocr-hotkey`，`SearchHotkey` 改为可空（null = 命令行未显式传）；
> · `TrayApplication`：`RegisterOcrHotkey`（失败 = 日志 + **气泡**，搜索热键失败也补了气泡）、`StartOcrCapture`（语言校验 → 每屏遮罩 → 异常收敛为气泡+日志）、`ReloadHostSettings`（合成优先级 = 命令行 > config/desktop.json > 默认）、托盘菜单「屏幕取字…」、Dispose 回收遮罩、selfcheck 新增 OCR 热键/语言两行；
> · `SettingsWindow` 接入 `HostSettingsSection`（桌面宿主节与工具配置走**同一条** Load/渲染/Save 链路，渲染共用 `RenderSnapshot`；保存成功回调托盘重注册热键 = 改键立即生效）；
> · `OcrOverlayManager.ShowAll(string? bcp47Language)` + `CurrentLanguageTag`（配置语言变更 → manager 重建）；
> · `ConfigCommand`：`TryResolveSchema` 对 desktop 保留节放行（get/set/unset/reset/path/schema/list 全入口），list 汇总含宿主行；
> · 验收扩展：`verify-desktop.py` 热键注册断言 4→**5**、OCR 热键行 + OCR 语言行 + 设置清单 desktop 节（净增 8 条断言 → **128/128**）；`_step17_ocr.sh` 新建（langs/probe/config get desktop 五条 PASS，语言包缺失走 R1 环境分支计 SKIP；$EZ 调用全部显式钉 install-root/config-root —— 工具源审计 S11 纪律）→ acceptance **334 PASS / 0 FAIL / 1 SKIP（满额 335）**；selftest **184/184** 回归通过。
>
> **FR-9 落法（与设计原意的对齐说明）**：「热键与默认语言可在设置中配置 | 复用 P1a 配置中心」
> —— OCR 是宿主功能没有 tool.json，落法是占用保留 toolId `desktop`（配置文件 `config/desktop.json`）
> 挂进 ConfigStore，校验/原子写/损坏恢复/双入口全部复用，**没有新造存储**。
> 热键优先级：命令行显式开关 > desktop.json > 代码默认（命令行是一次性显式覆盖，落盘配置是长期值）。
>
> **已知局限标注**（§2.3）：复制成功气泡在行数 > 1 时附「多栏排版可能乱序（已知局限）」；
> 单行文本没有乱序问题，不提示以免噪音。
>
> **顺手修复的既有 bug**：`ReRegisterHotkeys`（「刷新菜单」路径）只重注册**工具热键**，
> 而 `UnregisterAll` 注销的是**全部** —— 刷新一次搜索热键就静默失效一次（W3-d-1 落地以来
> 一直存在；无人触发过该路径所以没暴露）。现在宿主级热键统一重注册，这条链同时是
> "设置窗口改热键 → 立即生效"的通路。
>
> **实现期教训（check 函数语义）**：acceptance 的 `check <描述> <期望> <实际>` 是**字符串相等
> 比较**，不是条件表达式 —— 传 `"[[ 2 -ge 1 ]]"` 进去会拿它当期望值字面量比对，
> 出现"期望 [[ 2 -ge 1 ]]、实际 count=2"的假红（判据成立却 FAIL）。≥/≤ 判断必须手写 if。
>
> **验收实测（2026-09-26）**：acceptance `334/0/1（满额 335，rc=0）` · verify-desktop `128/0` ·
> selftest `184/184` · `ezt config set desktop ocr.hotkey … → get → unset` 闭环回读全对
> （写入值生效、删除后回落默认 Ctrl+Alt+O）。

> **真机项自动化收口（2026-09-26 晚，W4-c 追加）**：两个新探针把"改热键生效 / 热键唤出"
> 两条手工项钉进自动化 ——
> · `--probe-host-settings`（**无副作用**，进 verify-desktop）：程序化装配真实 `SettingsWindow`
>   （selfcheck 先例：WPF 控件未显示可构建）→ 选中宿主节 → 改 OCR 热键 Ctrl+Alt+K → 保存 →
>   断言 desktop.json 落盘 / 生效值重合成 / 热键真重注册 → unset 恢复默认断言回落；
> · `--probe-ocr-hotkey`（**有副作用**，遮罩抢焦点 1~2 秒，verify-desktop 已接受该量级
>   —— `--probe-search-summon` 同款先例）：`keybd_event` 注入真实 Ctrl+Alt+O → RegisterHotKey
>   sink 收 WM_HOTKEY → 遮罩全屏出现 → 注入 Esc → 断言收窗。两次连跑稳定全绿。
> verify-desktop **128 → 135**（2 条探针退出码 + 5 条 ★ 行为断言）。
>
> **★ 裸泵教训（DoEvents 吞键盘消息，2026-09-26 实测定案）**：Esc 收窗轮询最初用
> `WinForms.Application.DoEvents()`，注入的 Esc **永远到不了**遮罩 WndProc（焦点三层
> Win32 前台/Win32 焦点/WPF FocusedElement 全部正确落位、消息也确实进了线程队列
> QS_KEY=1，但 hook 计数恒 0、PreviewKeyDown 恒不触发）—— 换自建裸泵
> `PeekMessage→TranslateMessage→DispatchMessage` 后一次全绿。根因：**在"无主窗体的
> WinForms 线程 + 后台启动"上下文里，DoEvents 取出键盘消息后不 dispatch 给 WPF 窗口**
> （消息被泵内 PreTranslate 链吞掉）。正式运行（Application.Run 主泵）无此问题，
> 用户手工 Esc 收窗一直正常 —— 纯探针上下文特有，勿据此改产品代码。
> 排查路径上的两个副产品教训：① `nint`（IntPtr）不能直接进 JsonNode（序列化抛
> NotSupportedException），必须 `.ToInt64()`；② 诊断 hook 挂在窗口类 `SourceInitialized`
> 里从未生效（连必发的 WM_ACTIVATE 都计数 0），探针侧 `HwndSource.FromHwnd` 挂载同样
> 全 0 —— **计数恒 0 的探针面是名存实亡的探针面（§2.23② 变体）**，已全部删除，探针
> 只保留裸泵日志 `diagPumpLog`（仅失败时输出，记录队列里关键消息的 msg/hwnd 归属）。

## 8. 注意事项与风险

| # | 风险 / 坑位 | 等级 | 对策 |
|---|-------------|------|------|
| R1 | **OCR 语言包缺失**：未装 `Language.OCR~` Feature 时连英文都不可用 | 高（新机器大概率命中） | W4-a 首日即探；`languages=0` → 明确引导 `Add-WindowsCapability -Online -Name Language.OCR~~~zh-CN~0.0.1`；禁静默 |
| R2 | **per-monitor DPI / 混合缩放坐标错位** | 高 | §3.2 坐标纪律：全程物理像素；借鉴 PowerOCR `DisplayCapture` 的映射实现；手工清单设双屏缩放专项 |
| R3 | 遮罩窗**焦点与键盘链**：后台进程唤出窗被拒焦点、Alt 残留、X 键关闭语义 | 中 | 直接适用踩坑全集 §2.24~§2.26 结论：AttachThreadInput 组合、`_summoning` 屏蔽瞬态失焦、Closing→Hide 拦截、直通字符合成层照搬 |
| R4 | 小字号识别差；超大位图超 `OcrEngine.MaxImageDimension` 直接失败 | 中 | EnhancedScale 放大 + 维度上限钳制（PowerOCR `TextExtractorService` 已示范边界处理） |
| R5 | Desktop 单实例锁 vs 探针：托盘在跑时探针 rc=3 零输出（§G2-a 前科） | 中 | CLI 原语探针（W4-a）不依赖 Desktop 进程；overlay 探针（W4-b）写明前置条件"托盘须停止" |
| R6 | WinRT 线程模型：`OcrEngine` 创建/复用的 apartment 约束、异步续体线程 | 中 | 引擎封装为显式工厂 + 文档化线程约定；异常必须 Error 出口（禁静默退化，S2） |
| R7 | 剪贴板竞争写入失败 | 低 | 重试 ≤3 次 + 失败气泡（§5.2） |
| R8 | 新 DLL 未生效（增量构建坑） | 低 | 常规纪律：怀疑"没生效"先查 DLL 时间戳 → `--no-incremental`（S1） |

**断言纪律**（全阶段）：验收断言只落数字或退出码；禁 `cmd | grep -q` 与 `A && B || C && D`；探针输出对齐既有 `--probe-*` JSON 惯例。

## 9. 借鉴映射表（实施时按图索骥）

| 本项目目标 | 参考文件（`PowerToys/src/modules/PowerOCR/PowerOCR/`） | 抄什么 |
|------------|----------------------------------------------------------|--------|
| 多屏枚举 + DPI 坐标 | `Models/DisplayCapture.cs` | 显示器枚举与物理/逻辑像素映射 |
| 区域截图 | `Services/ScreenCaptureService.cs` | CopyFromScreen 用法、区域裁剪 |
| 光标裁剪 | `Helpers/CursorClipper.cs` | 光标剔除 |
| 引擎调用与放大 | `PowerOCR.Core/Ocr/WindowsOcrRecognizer.cs`、`PowerOCR.Core/Services/TextExtractorService.cs` | OcrEngine 生命周期、EnhancedScale、MaxImageDimension 钳制 |
| 遮罩交互逻辑（非 XAML） | `PowerOCRXAML/Views/OverlayPage*.cs`、`Helpers/NativeSelectionWindow.cs` | 选区/取消/重试的状态机思路 |
| WPF 遮罩窗实现 | Text-Grab 仓库（MIT，github.com/TheJoeFin/Text-Grab，WPF 同栈） | 全屏遮罩窗的 WPF 写法 |
| word 包围盒取词 | 引擎 `OcrResult.Lines[].Words`（系统免费赠品） | 单击命中判定 |

## 10. 状态登记

- [x] 本文档在 `docs/README.md` §3.5 登记（W4 段）
- [x] **W4-a 已落地（2026-09-26）**：落地记录见 §7；本机语言包已就绪（en-US / zh-Hans-CN），精度基线 16/16 全对；selftest 184/184 回归通过
- [x] W4-b 开工时：新建 `W4-手工验收清单.md` 并登记索引（2026-09-26 已建，M1 双屏混合缩放实测 + 外接屏端到端探针全绿）
- [x] attribution 进 `THIRD-PARTY-NOTICES.md`（提前于 W4-c 完成——MIT 派生代码已实际入库）
- [x] **W4-b 已落地（2026-09-26）**：遮罩窗三件套 + 真鼠标注入探针 + 双屏混合缩放实测（外接屏端到端全绿，生产"副屏坐标原点"bug 抓出并修复）
- [x] **W4-c 已落地（2026-09-26，W4 全部收口）**：热键/托盘菜单/设置项（desktop.json 接 P1a）/局限标注/验收扩展全绿（acceptance 334/0/1 · verify-desktop 128/0 · selftest 184/184）
- [x] **W4-c 真机项自动化（2026-09-26 晚）**：`--probe-host-settings` + `--probe-ocr-hotkey` 进 verify-desktop（**135/0**，含 5 条 ★ 行为断言）——"改热键生效 / 热键唤出→Esc 收窗"两条手工项钉进自动化；裸泵教训见 §7 W4-c 落地记录
