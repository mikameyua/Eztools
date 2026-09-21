# Eztools 设计方案

> **产品**：Eztools —— 类似 PowerToys 的一体化 Windows 工具集合
> **状态**：设计定稿 · **P0 已落地**（§14）· **P1a 配置中心已落地**（§16）· **P2 托盘已落地**（§17）
> **本文地位**：**唯一权威设计文档**。历史决策与被否决方案见《附录 B》，不干扰主线。
> **调研依据**：同类成熟项目的 GitHub 实测数据（见《附录 A》）+ 本机 PowerToys 源码取证（见《附录 C》）

---

## 0. 核心决策速查

| 决策项 | 结论 |
|---|---|
| 产品形态 | 一体化工具集合（统一安装 / 统一设置 / 统一托盘与热键） |
| **不是**什么 | **不是插件平台，不是第三方生态**。工具全部由自己编写 |
| 宿主 | **C# / .NET 10**（2026-09-19 由 net7.0 升级：.NET 7 已于 2024-05-14 终止支持） |
| UI | ✅ **WPF + iNKORE.UI.WPF.Modern，与托盘同进程**（2026-09-19 拍板并落地，见 §2.1 的注与 §18）。原定 WinUI 3 因选型依据有误 + 本机无 WindowsAppRuntime 而改；代价 = **放弃 37 MB 裁剪形态**（WPF 与 `PublishTrimmed` 实测不兼容） |
| 工具运行时 | **Python，独立进程**（其余语言可选） |
| 主通信 | **JSON-RPC 2.0 over stdio**（NewLineDelimited 分帧） |
| 工具发现 | **清单驱动**（扫描 `tools/*/tool.json`），非硬编码 |
| 工具隔离 | 每工具一进程，崩溃不影响宿主与其他工具 |
| 设置界面 | **由配置 schema 自动渲染**，新增工具零 UI 代码 |
| 特权处理 | **Core 特权原语层**，工具代码永不进特权进程 |
| Python 分发 | **自带隔离嵌入式 Python**，不依赖用户环境 |
| **本项目许可** | **`GPL-3.0-or-later`**（2026-09-21 定稿；SPDX：`GPL-3.0-or-later`）—— **OSI 开源许可**，**允许商用，但强制下游开源**（copyleft）。⚠️ 别混：copyleft ≠ 禁商用；16:0x 曾选的 PolyForm-NC 已作废。见 `LICENSE` 与 `docs/许可证选型分析.md` |
| 权衡取舍 | 用"进程外 + IPC 开销"换"崩溃隔离 + 可用 Python 生态" |

---

## 1. 产品定位

### 1.1 是什么

一个统一的 Windows 系统增强工具体，用户装一次，获得一批可独立启停的工具。

**核心价值就是"统一"**：
- 统一安装 / 卸载 / 更新
- 统一的设置界面与托盘入口
- 统一的热键注册与冲突仲裁
- 统一的日志与诊断
- 各工具之间互不干扰（一个崩溃不影响其他）

### 1.2 不是什么（边界很重要）

**它不是插件平台，也不是第三方生态。** 由此推出的直接结论：

| 不做 | 理由 |
|---|---|
| 多源 Feed / 商店 / 官方源 | 不从网上下载工具 |
| 包格式（`.ezt` 等）与包签名 | 工具随产品发布 |
| 信任状态机 / 发布者信任 / blocklist | 不区分来源——全由自己编写 |
| API 版本兼容矩阵 | 版本由自己独家控制 |
| 沙箱档位（AppContainer 等） | 不必防自己 |

> 这些设计的价值都在"别人写的工具如何安全进来"，而工具全由自己写时该问题不存在。
> **这是好消息**：工作量大幅下降，能更快到达"可用"。

> ### ⚠️ "工具全部由自己编写"要读准（2026-09-19 澄清）
>
> 这一节取消的是**供应链信任机制**（多源 Feed / 包格式 / 签名 / 信任状态机 / 沙箱档位），
> 理由是"这些设计的价值都在**别人写的工具如何安全进来**"。
> 也就是说，它表达的是"**没有第三方作者与发布者，所以不需要来源校验**"，
> **不是"不许参考别人的东西"** —— 参考别人的实现是**手段问题，与定位无关**。
>
> 而且参考对象要**按许可分类**，差别很大：
>
> | 许可 | 能怎么用 | 本项目相关例子 |
> |---|---|---|
> | **MIT / BSD / Apache** | ✅ 可读、可抄、可改、可融进产品（只需保留版权声明） | **PowerToys 是 MIT** —— 本地克隆里 `FileLocksmith` / `peek` / `powerrename` / `fancyzones` 等全是 MIT 的 **C#** 参考实现，与宿主同语言、同产品形态 |
> | **GPL-3.0** | ✅ **可读、可抄、可改、可移植** —— 本项目自己就是 **`GPL-3.0-or-later`**，同许可下合法（义务 = 保留原作者版权声明） | GlazeWM、QuickLook 都是 **GPL-3.0** ⇒ **2026-09-21 起解锁**，详见 `docs/参考来源.md` §2.2 / §2.3 |
> | **GPL-2.0-only / AGPL-3.0** | ⚠️ **只读文档，不读源码** —— GPL-2.0-only 与 GPL-3.0 **不兼容**；AGPL-3.0 的 §13 网络条款会附着在该部分上 | 默认避开 |
> | **闭源 freeware** | ❌ 没有源码可参考；❌ 不宜当组件打包 | Everything（二进制宽松可再分发、需署名）、WizTree、OpenFilesView |
> | **提供官方 CLI / API 的** | ✅ **调它，别重写** —— 正常调用别人的程序不构成分发，一般不触发许可 | Everything 有 `es.exe`（最稳）、本地 HTTP API、SDK（`Everything64.dll` + IPC，带 C#/Python 示例） |
>
> **别忽略维护成本**：抄进来的代码从此由你维护，上游修 bug 不会自动流过来。
> 几百行、逻辑稳定的（如 File-Locksmith 的"列句柄 + 结束进程"）适合；
> 几千行且要处理各种系统怪癖的（如 FancyZones 的布局引擎）不适合 ——
> 那属于"看着香、最后变负债"。
>
> 📋 **实际借用情况登记在 `docs/参考来源.md`**（许可分级规则 · 已核实条目 · 动手前必问的三句）。
> **规则：每借用一次就补一条；没有记录 = 没有借用的权利。**

### 1.3 刻意保留的扩展点

为了未来若开放第三方时改动最小，以下四点**现在实现成本为零、将来补的成本很高**，故预留：

1. `tool.json` 保留 `publisher` / `apiVersion` 字段（只填不校验）
2. 模块发现支持多目录源（现在只扫内置 `tools/`，将来加"用户目录源"）
3. 特权原语是固定 IPC 契约（将来第三方工具只能使用已有原语，天然有界）
4. 配置使用标准 JSON Schema（将来可支持远程配置下发）

---

## 2. 技术选型

### 2.1 选型结论

| 项 | 结论 | 依据（成熟先例） |
|---|---|---|
| **宿主** | **C# / .NET**（net10.0） | DevToys（32.0k）、Flow Launcher（15.6k）、PowerToys 设置 UI——三个成熟项目一致 |
| **UI** | ✅ **WPF + iNKORE.UI.WPF.Modern**（与托盘同进程）——2026-09-19 拍板并落地，见下表后的注与 §18 | ~~原生观感 + 启动快~~ 原选 WinUI 3 的**选型依据已被证伪**（三个先例里 Flow Launcher/DevToys 实际不是 WinUI 3，见注）；且本机无 WindowsAppRuntime |
| **工具运行时** | **Python 独立进程** | Wox（27.4k）、Flow Launcher（15.6k）已验证 |
| **传输** | **JSON-RPC 2.0 over stdio** | Flow Launcher 的 Python 插件默认方式 |
| **Python 分发** | 自带隔离的嵌入式 Python | Flow Launcher 的做法 |

> ### ✅ 2026-09-19 复核：上表"依据"里的三个先例，**两个是错的**（详见下方取证）
>
> **UI 栈已拍板（2026-09-19，菲比确认）**：**WPF（与托盘同进程）+ iNKORE.UI.WPF.Modern 主题库**。
> 依据就是下面的探针实测：共存成立、裁剪对 WPF 彻底不可用（代价明确接受）、
> 主题库补齐外观短板。WinUI 3 / Avalonia / 独立进程方案否决，理由见探针数据与 §15 风险表 5b。
>
> **2026-09-19 补：WPF 探针实测**（一次性探针，结论留档、产物已删）——
> 托盘已用 WinForms 落地（§17），"WinUI 3 没有托盘"这条反对理由对设置窗口已不再成立，
> 所以这次用探针把「托盘 WinForms + 设置窗口 WPF 同进程」这条路实测了一遍：
>
> | 实验 | 结果 |
> |---|---|
> | `UseWPF` + `UseWindowsForms` 同开 | ⚠️ 能编译，但两个栈的全局 using 大量重名（`Application` / `CheckBox` / `MessageBox` …），**必须显式管理 using**（保留一个栈的全局 using，另一个全部走别名）——这是共存的第一个真实成本 |
> | WPF 消息循环 + WinForms `NotifyIcon` 同进程 | ✅ **能共存**：循环自关 913ms 正常退出 |
> | 真 WPF 窗口显示 + 托盘同进程 | ✅ 窗口真的显示 4 秒后正常退出（5015ms，含等待） |
> | 自包含 + **裁剪** | ❌ **WPF 与裁剪不兼容，且比 WinForms 更严**：SDK 直接报 `NETSDK1168`（WinForms 是可抑制的 1175）；用 `_SuppressWpfTrimError=true` 硬绕过编译后，**创建窗口时运行时崩溃**（退出码 `0xE0434352`，未处理的 CLR 异常；同一代码不裁剪时正常显示）——**别走这条路** |
> | 体积（加 WPF 后，对照托盘 WinForms 版） | 框架依赖 190KB · 自包含 **173MB**（原 118MB，+55MB）· 自包含+单文件压缩 **77MB** |
>
> 🔄 **上表体积一行的 2026-09-21 14:3x 重测更正**（真 WPF 设置窗口已落地）：自包含 **182 MB / 272 文件**、
> 单文件压缩 **80 MB**（旧值 173/77 偏低 9/3 MB）。CLI 侧未变（自包含 78 MB / 单文件压缩 37 MB）。
> 完整对照与新增基线见 §15.4 的注。
>
> → **架构含义**：选 WPF（同进程）= 放弃 37MB 裁剪形态；保持 37MB = 设置窗口要么独立进程
> （跨进程通知配置变更，多一套生命周期问题），要么换 Avalonia（重写已落地的托盘）。
>
> 补充一条会影响排期的实测（2026-09-19）：**"托盘"不需要等这个决策** ——
> `net10.0-windows` + `UseWindowsForms` 下 `System.Windows.Forms.NotifyIcon` 可直接编译运行
> （实测构造成功、可设图标与右键菜单）。**UI 栈只阻塞 P1b 的设置窗口，不阻塞 P2 的托盘。**
> 详见 §12 的 P2 段。
>
> | 项目 | 上表当它是 | 实际 | 取证 |
> |---|---|---|---|
> | PowerToys 设置 UI | WinUI 3 | ✅ 确实是 | `src/settings-ui/Settings.UI/PowerToys.Settings.csproj`：`<UseWinUI>true</UseWinUI>` |
> | Flow Launcher | WinUI 3 先例 | ❌ **是 WPF** | .NET 7 + ModernWpf / iNKORE 换皮 + `System.Windows.Forms.NotifyIcon` 做托盘 + `HotKeyMapper` 做热键 |
> | DevToys | WinUI 3 先例 | ❌ **2.0 已移除 WASDK** | changelog："Use WPF Blazor on Windows, MAUI Blazor on Mac. **Removed WASDK and Uno app**" |
>
> 补充两条：
>
> 1. **PowerToys 自己是混用的** —— 设置 UI 迁了 WinUI 3，但 `PowerLauncher`（启动器）、
>    `FancyZonesEditor`、`WorkspacesEditor` 三个工程仍是 `UseWPF=true`。
> 2. **WinUI 3 没有第一方托盘控件**。微软官方迁移文档原文：
>    *"Windows App SDK doesn't include a first-party tray-icon control."*
>    而本项目定位的第一条就是"**统一托盘入口**"。
>
> **最贴近本项目的先例其实是 Flow Launcher**（.NET + Python 插件独立进程 + JSON-RPC +
> 热键 + 托盘 + 设置窗口，逐项对上），**而它是 WPF**。上表引用它来印证"进程外 + JSON-RPC"
> 这一层是成立的，但不能顺势推出它也印证了 WinUI 3 —— 同一个项目被两头引用是自相矛盾的。
>
> **教训**：查证先例要看它的 `csproj`，不要看传闻。

### 2.2 为什么不用 Electron / Tauri

实测数据（《附录 A》）：常驻系统工具集的宿主**全是原生栈**——PowerToys（C++）、DevToys（C#）、Wox（Go）、Flow Launcher（C#），四个最大的项目无一例外。Electron 只出现在 ueli（4.6k，启动器类，非系统增强工具集）；Tauri 方向的同类项目 stars 为个位数到 45，**无成熟先例**。

原因是对这一类产品，以下四项都是**日常成本**：
- 启动开销与常驻内存（工具集开机常驻）
- Win32 / 提权 / 壳集成的调用代价（Electron 需经 ffi/edge 间接层）
- 安装包体积
- **Electron 叠加管理员权限是尤其糟糕的组合**（Chromium 的攻击面叠加高权限）

### 2.3 为什么工具走进程外（而不是像 DevToys 那样进程内）

DevToys 用 MEF 把扩展作为 .NET 程序集**进程内**加载——简单、启动快、零调用开销，但**只能写 C#**，且扩展崩溃会拖垮整个应用。

Eztools 的工具需要 Python 生态（PDF / 图像 / 音视频 / AI 处理），因此必须走**进程外**：

| | 进程内（DevToys 路线） | **进程外（本方案）** |
|---|---|---|
| 工具语言 | 仅宿主语言 | **任意语言（主要 Python）** |
| 崩溃影响 | 拖垮整个应用 | **仅该工具** |
| 调用开销 | 零 | IPC 往返 |
| 冷启动 | 最快 | 较慢（需起进程） |
| DX | 最简单 | 需处理 IPC 与运行时分发 |

**外部印证**：Flow Launcher 1.18 专门重构插件系统，公开理由正是旧架构"**插件崩溃影响主程序稳定性**"，改为微内核 + 进程隔离 + 统一 JSON-RPC 2.0 + 每语言专用运行时容器。与本节判断一致。

---

## 3. 总体架构

```
┌─ 统一 UI 层 ────────────────────────────────────────┐
│  设置窗口（schema 驱动渲染）· 托盘菜单 · 热键中心      │
└──────────────────────┬──────────────────────────────┘
                       │ 进程内直调
┌─ 宿主层（C# / .NET，单进程）────────────────────────┐
│  模块注册表（清单驱动）· 配置中心 · 热键仲裁器         │
│  日志与诊断 · 进程生命周期管理 · 特权原语客户端        │
└──────────────────────┬──────────────────────────────┘
                       │ JSON-RPC 2.0 over stdio（每工具一条）
┌─ 工具层（每工具独立进程）──────────────────────────┐
│  script（即用即走）｜ lite（常驻宿主）｜ full（独立进程）│
│  Python 宿主 · Node 宿主 · 原生可执行                  │
└──────────────────────┬──────────────────────────────┘
                       │ 窄原语 IPC（仅需特权的工具）
┌─ Core 特权层（按需存在，独立进程）──────────────────┐
│  仅特权原语：句柄枚举 · 原始卷读 · 窗口控制 · 输入钩子 │
│  仅第一方代码。工具代码永不进入。                      │
└─────────────────────────────────────────────────────┘
```

**分层原则**：
- UI 与宿主**同进程**（C# 直调，零 IPC 开销，这是原生栈的主要收益之一）
- 工具**必须独立进程**（隔离是第一原则）
- 特权层**独立进程且仅第一方代码**（唯一权限边界）

---

## 4. 工具契约

### 4.1 新增一个工具的成本

**目标：新增一个工具 = 新建一个目录 + 写 1 个 manifest + 实现 1 个入口。**

```
tools/pdf-merge/
├── tool.json          # 身份 · 运行时 · 贡献点 · 配置 schema · 热键
├── main.py            # 只实现声明过的 handler
├── icon.png
└── Lib/               # 可选：vendor 的第三方依赖（见 §5.4）
```

宿主从 `tool.json` **自动获得**：设置页、命令入口、全局热键、托盘项、进程生命周期管理——**全部零代码**。

### 4.2 `tool.json` 完整规范

```jsonc
{
  // ── 身份 ──
  "id": "pdf-merge",                    // 必填，小写+连字符，全局唯一
  "name": "PDF 合并",                    // 必填，显示名
  "version": "1.0.0",                   // 必填，semver
  "description": "把多个 PDF 合并为一个，支持书签保留",
  "author": "菲比",
  "license": "MIT",
  "icon": "icon.png",
  "homepage": "https://...",

  // ── 预留：自用阶段只填不校验 ──
  "publisher": "Eztools",
  "apiVersion": 1,

  // ── 运行时 ──
  "runtime": "python",                  // python | node | dotnet | exe
  "runtimeVersion": ">=3.11 <4.0",      // 可选；宿主据此选运行时
  "entry": "main.py",                   // 入口文件
  "weight": "lite",                     // script | lite | full   ← 见 §5.1
  "lifecycle": "transient",             // transient | resident | task
  "latency": null,                      // 仅 resident 可用；"interactive" 表示冷启动不可接受，须预热常驻
                                        // ⚠️ 语义标注（2026-09-21）：本字段只有解析 / 校验 / 诊断，
                                        //    **没有独立的运行时行为** —— 它表达的"须预热常驻"已由
                                        //    `lifecycle: "resident"` 的启动钩子如实承担（§8）。
                                        //    两字段表达同一件事，故不另加一套 latency 逻辑；本字段仅作意图声明。

  // ── 能力声明（决定权限档位与授权提示，非运行时仲裁依据）──
  "needs": ["files.userSelected", "notify"],
  "elevatedPrimitives": [],             // 需要 Core 特权层的原语名，如 ["handles.enumerate"]

  // ── 独占资源（工具间冲突仲裁）──
  "exclusiveResources": [],

  // ── 贡献点 ──
  "contributes": {
    "commands": [
      { "id": "pdf-merge.merge", "title": "合并 PDF", "handler": "merge" }
    ],
    "actions": [
      { "id": "pdf-merge.quick", "title": "快速合并选中的 PDF",
        "when": "files.count>1 && files.ext:pdf", "handler": "mergeSelected" }
    ],
    "hotkeys": [
      { "command": "pdf-merge.merge", "default": "Ctrl+Alt+M" }
    ],
    "menus": [
      { "location": "tray", "command": "pdf-merge.merge", "group": "file" }
    ]
  },

  // ── 配置 schema：设置页与设置存储由此自动生成 ──
  "config": {
    "type": "object",
    "properties": {
      "preserveBookmarks": { "type": "boolean", "default": true, "title": "保留书签" },
      "outputDir":         { "type": "string",  "format": "directory", "title": "输出目录" },
      "quality":           { "type": "integer", "minimum": 1, "maximum": 100,
                             "default": 90, "title": "压缩质量" }
    }
  }
}
```

### 4.3 字段说明

| 字段 | 必填 | 说明 |
|---|---|---|
| `id` | ✅ | 唯一标识。配置文件、日志、IPC 均以此为键 |
| `runtime` | ✅ | `python` / `node` / `dotnet` / `exe` |
| `entry` | ✅ | 相对于工具目录的入口 |
| `weight` | ✅ | 决定启动方式与超时策略，见 §5.1 |
| `lifecycle` | ✅ | 决定宿主保活策略与资源配额，见 §8 |
| `needs` | — | 粗粒度能力声明（4~6 项收敛集），见 §4.4 |
| `elevatedPrimitives` | — | 需要特权的原语名，见 §9 |
| `exclusiveResources` | — | 独占资源声明，见 §8.3 |
| `contributes` | — | 贡献点，见 §4.5 |
| `config` | — | JSON Schema 子集，见 §7 |
| `publisher` / `apiVersion` | — | **预留字段，自用阶段不校验** |

### 4.4 `needs` 取值（粗粒度收敛集）

| 值 | 含义 | 授权提示 |
|---|---|---|
| `files.userSelected` | 访问用户显式交给它的文件 | 否 |
| `files.pluginData` | 访问自己的私有数据目录 | 否 |
| `files.anyPath` | 访问任意路径 | 是 |
| `network` | 联网 | 是 |
| `clipboard` | 读写剪贴板 | 部分 |
| `ui.panel` | 提供面板 UI | 否 |
| `notify` | 发通知 | 否 |
| `system.inspect` | 只读系统信息（配合特权原语） | 是 |

> **设计要点**：`needs` 不是运行时仲裁依据（那是伪安全，见《附录 B》），它的作用是**决定权限档位 + 在工具详情页告知用户这是什么类别的工具**。

### 4.5 贡献点

| 贡献点 | 用途 | 需要的宿主能力 | 实现状态 |
|---|---|---|---|
| `commands` | 可被 UI / 热键 / 其他工具调用 | 无 | ✅ P0 |
| `actions` | 文件、选中文本动作（右键 / 拖入） | 无 | ✅ P0 |
| `hotkeys` | 声明全局热键（由宿主统一注册） | 宿主热键仲裁 | ✅ P2 |
| `menus` | 注入托盘 / 主界面菜单 | 无 | ✅ P2（含 `input` 上下文契约） |
| `panels` | 提供一个面板 UI（`weight: full` 才可用） | `ui.panel` | ✅ **P4 Wave 2c**（2026-09-21）：契约层 + `tool.panel.data` 数据通道 + 原生 WPF 渲染（D3）。协议见 `docs/P4-Wave2c-面板协议.md`，落地记录见 §25 |
| `search` | 提供搜索提供者 | 无 | ⬜ **未实现·未排期**：没有搜索框 UI 作消费方，补齐 = 造没人用的抽象（建议删除或标未排期，见同清单 B2） |

**状态列是硬要求**：凡"文档里列了但代码没有"的贡献点必须显式标状态 —— 否则读文档的人会把
"写在表里"当成"已经能用"，这正是 `panels` / `search` 两项此前的问题
（`panels` 已于 2026-09-21 补齐，`search` 仍为 ⬜）。

**设计原则**：贡献点是**加法**——工具只声明自己需要的那几个，只为声明过的贡献点写 handler。

---

## 5. 工具运行时

### 5.1 `weight` 三档（借鉴 Wox 的做法）

| 档位 | 启动方式 | 通信 | 可用能力 | 超时 | 适用 |
|---|---|---|---|---|---|
| **`script`** | 每次调用起进程，用完即退 | stdio 单次往返 | 仅基础 API，无设置界面，无依赖 | 默认 10s | 单文件小工具、快速原型 |
| **`lite`** | 常驻宿主进程（按运行时分组复用） | stdio 持续连接 | 完整 API（配置 / 通知 / 特权原语），**无第三方依赖** | 30s | 绝大多数工具 |
| **`full`** | 独立宿主进程，宿主管理其生命周期 | stdio 持续连接 | 完整 API + 依赖 + 异步 + 面板 UI | 按需声明 | 重型工具、带 UI 的工具 |

> `weight` 与 `lifecycle` 是两个正交维度：`weight` 决定**进程怎么起**，`lifecycle` 决定**起了之后活多久**（§8）。

### 5.2 通信协议

**JSON-RPC 2.0 over stdio，NewLineDelimited 分帧**（一行一个 JSON 对象）。选择 stdio 而非 WebSocket 的原因：

- stdio 是天然点对点的**私有管道**，本机其他进程无法连接
- WebSocket 需动态端口发现 + 本机监听，任何本机进程都可能尝试接入
- 无端口即无端口冲突、无防火墙弹窗

**启动方式**：宿主以子进程方式启动 `entry`，通过 stdin/stdout 交换 JSON-RPC，stderr 重定向到日志。

**协议层的三条硬约束（技术验证实测得出，都是"不踩过就一定会踩"的坑）**：

| 约束 | 症状 | 修法 |
|---|---|---|
| **禁用 BOM** | .NET 的 `Encoding.UTF8` 带 BOM 标识，用作 `StandardInputEncoding` 时会在子进程 stdin 开头写入 `EF BB BF`，工具侧解析**首帧**直接失败 → 表现为"调用超时"（症状与原因距离极远） | 宿主侧用 `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`；工具侧 stdin 用 `utf-8-sig` 容忍。**两侧都要做** |
| **只写 `\n` 分帧** | 混入 `\r\n` 会污染分帧 | 宿主与工具统一 `newline="\n"` |
| **逐帧 flush + 独立读 stderr** | 不 flush 会让对端永久阻塞；stderr 不单独读取会在缓冲区写满时死锁 | 每次写帧后 flush；stderr 用独立任务读取 |

**宿主侧的编码设置**：

```csharp
StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
StandardOutputEncoding = Encoding.UTF8,
StandardErrorEncoding  = Encoding.UTF8,
```

### 5.3 方法集

**宿主 → 工具**

| 方法 | 参数 | 返回 | 说明 |
|---|---|---|---|
| `tool.initialize` | `{ toolId, config, context }` | `{}` | 启动后首个调用 |
| `tool.invoke` | `{ commandId, args }` | `{ result }` | 触发命令 handler |
| `tool.onConfigChanged` | `{ config }` | `{}` | 配置变更推送 |
| `tool.recover` | `{ lastState }` | `{}` | **仅 `resident`**：状态恢复钩子，见 §8.2 |
| `tool.stop` | `{}` | `{}` | 请求优雅退出 |

**工具 → 宿主**

| 方法 | 参数 | 说明 |
|---|---|---|
| `host.log` | `{ level, message }` | 写日志 |
| `host.notify` | `{ title, body }` | 发通知 |
| `host.progress` | `{ taskId, percent, message }` | **仅 `task`**：进度上报 |
| `host.storage.get` / `host.storage.set` | `{ key } / { key, value }` | 工具私有 KV（卸载保留） |
| `host.primitive.call` | `{ name, args }` | **调用特权原语**，见 §9 |
| `host.invokeTool` | `{ toolId, commandId, args }` | 调用其他工具的命令 |

### 5.4 Python 依赖策略 ★（已由技术验证修正）

Flow Launcher 在这件事上踩过坑。**必须在 P0 就定好策略，否则第一个需要 numpy 的工具就会卡住。**

> **本节结论已用实测数据修正。** 实测环境：Windows 11 / Python 3.13.14 / Pillow 12.3.0（含编译代码）/ PyInstaller 6.22.3。

**实测冷启动耗时**：

| 方式 | 中位耗时 | 相对 |
|---|---|---|
| 源码 + 标准库 | 167 ms | 1× |
| **源码 + 标准库 + `-I` 隔离** | **126 ms** | **0.75×（更快）** |
| 源码 + Pillow（含编译代码） | 181 ms | 1.1× |
| `--onefile` 打包 + Pillow | **1023 ms** | **6.1×** |

**修正后的策略优先级**：

| 优先级 | 方案 | 冷启动 | 适用 |
|---|---|---|---|
| **1（首选）** | **嵌入式 Python + 源码运行 + 依赖 vendor 到 `Lib/`** | 126~181 ms | **绝大多数工具**，含 Pillow 这类含编译代码的库 |
| 2 | `--onedir` 打包 | 预期 300~500 ms | 需要完全自包含、不依赖宿主运行时 |
| 3（最后手段） | `--onefile` 打包 | ~1023 ms | 仅当必须单文件分发；**该工具须 `lifecycle: resident`** |

> **原判断修正**：此前认为"含编译代码的库必须用 nuitka/pyinstaller 打包"。
> 实测表明——**只要目标机器是同一 Windows 平台且 Python 版本由宿主固定，含编译代码的 wheel 直接 vendor 到 `Lib/` 就能正常使用**，不需要打包。
> 打包只在需要脱离宿主运行时自包含时才必要，而 `--onefile` 每次启动解压的代价约 900 ms，能不用就不用。

**嵌入式 Python 分发（已实测验证可重定位）**：

实测：把一个独立安装的 CPython 整体拷到别处（**跨盘符 + 中文路径**），5 项检查全通过——
`sys.executable` / `sys.prefix` 指向新位置、`-I` 下 `sys.path` 只含运行时自身路径、vendored 编译依赖正常加载、端到端中文无损、**性能零损失（126ms → 124ms）**。

因此「嵌入式 Python 分发」**退化为"随宿主拷一份运行时"**，无需下载、无需特殊处理。

| 项 | 结论 |
|---|---|
| 位置 | `%LOCALAPPDATA%\Eztools\runtimes\python\<version>` |
| 隔离 | 与用户系统的 Python 完全隔离，**不写入 `PATH`**，用户无需自行安装 Python |
| 部署方式 | **压缩包分发（约 15~20 MB）+ 首次运行解压**（53 MB 直接拷贝需 42 秒，不可接受） |
| **不以"用户可配置 Python 环境"为功能** | 会把环境不一致引回来（实测见下方"启动参数"） |

**运行时来源（重要区分）**：

| 来源 | 布局特征 | 可用性 |
|---|---|---|
| python.org Windows 安装器 | 完整安装布局 | ✅ 可重定位 |
| python.org **embeddable zip** | 带 `._pth`、**无 pip**、`site` 被禁用 | ❌ **不要用**（经典坑） |
| **python-build-standalone（`install_only`）** | 完整安装布局、自带 pip、**专为再分发设计** | ✅ **推荐的生产来源** |

**工具进程的标准启动参数（实测确定）**：

```
<python> -I -X utf8 -u <entry>
```

| 参数 | 作用 | 为什么必须 |
|---|---|---|
| `-I` | 隔离模式（隐含 `-E -s`） | 阻断父进程 `PYTHONPATH` 与用户 site-packages 泄漏（实测：裸启动时 `PYTHONPATH` 会进入工具的 `sys.path[1]`）；且实测比不隔离**快 41 ms** |
| `-X utf8` | 强制 UTF-8 | **关键**：`-I` 隐含 `-E`，会连带忽略 `PYTHONUTF8` / `PYTHONIOENCODING` 环境变量，所以编码**必须靠 CLI 参数** |
| `-u` | 不缓冲 | 不加会让宿主等待首帧时永久阻塞 |

**工具入口的标准前缀**（SDK 模板必须包含）：

```python
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
if hasattr(sys.stdin, "reconfigure"):
    # utf-8-sig：容忍对端写入的 UTF-8 BOM。实测 .NET 的 Encoding.UTF8 默认带 BOM 标识，
    # 会让工具侧解析首帧直接失败，症状表现为"调用超时"，极难定位。
    sys.stdin.reconfigure(encoding="utf-8-sig", newline="\n")
```

> 环境变量层（`PYTHONUTF8` / `PYTHONIOENCODING`）在隔离模式下**失效**，因此不再作为方案依赖项。

**入口文件的标准前缀**（工具模板生成）：

```python
import sys, os
from pathlib import Path

here = Path(__file__).resolve().parent
for p in (here, here / "Lib"):
    sys.path.insert(0, str(p))
```

---

## 6. 宿主核心服务

### 6.1 模块注册表（清单驱动）

**这是对 PowerToys 最重要的修正**：PowerToys 把 36 个模块名硬编码在 `src/runner/main.cpp:256-293` 的数组里，新增模块必须改宿主源码重编译。Eztools 改为扫描清单。

```
启动流程：
  扫描 tools/*/tool.json
    → 逐项校验 schema（失败：记录并跳过，不弹窗、不中断启动）
    → 去重（同 id 取最高 version）
    → 注册贡献点（命令 / 动作 / 热键 / 菜单）
    → 按 lifecycle 决定是否预热启动
```

| 规则 | 说明 |
|---|---|
| 校验失败 | **静默降级 + 日志**，绝不弹错误对话框（PowerToys 在 Release 模式会弹 `MessageBoxW`，见《附录 C》） |
| 目录源 | 现在只扫内置 `tools/`；预留多目录源（§1.3） |
| 状态区分 | **明确区分"已启用"与"已加载"**（PowerToys 的 `enabled` 只管 `enable()`，DLL 照样载入——见《附录 C》） |

### 6.2 配置中心

- 存储位置：`%APPDATA%\Eztools\config\<toolId>.json`
- **与工具目录分离**：禁用或移除工具不丢用户配置，重新启用即恢复
- 写入策略：宿主侧校验 schema 后落盘，再推送给工具（`tool.onConfigChanged`）
- 工具私有数据：`%LOCALAPPDATA%\Eztools\toolsdata\<toolId>\data`，同样与代码分离

**配置（config）与私有数据（storage）刻意不合并**（2026-09-19 决定）：

| | 配置 config | 私有数据 storage |
|---|---|---|
| 路径 | `%APPDATA%\Eztools\config\<toolId>.json` | `%LOCALAPPDATA%\Eztools\toolsdata\<toolId>\storage.json` |
| 谁写 | **用户** | **工具自己** |
| 有没有 schema | 有 —— 设置页据此渲染 | 没有 |
| 用户该不该看见 | 该 | 不该 |

**不合并的理由**：合成一个文件后，"用户点重置配置"会把工具的内部状态一起清掉
（比如某个工具缓存的索引位置），那不是用户的预期。
因此**配置中心只管有 schema 的那部分**；工具自己的数据走 P0 就已可用的 `host.storage.*`。

> 位置分法（配置在 Roaming、数据在 Local）是**有意的**，别改：
> 配置应随用户漫游，缓存类数据不该漫游。

### 6.3 热键仲裁

**PowerToys 用户最常见的抱怨来源，必须由宿主统一管理。**

| 环节 | 做法 |
|---|---|
| 注册 | 工具只**声明**热键，由宿主统一 `RegisterHotKey` |
| 冲突检测 | 启动与新增时全局比对，冲突则保留先注册者并提示 |
| 用户改键 | 在设置 UI 里集中展示与重绑 |
| 失效回退 | 注册失败（被其他程序占用）时，提示并列出占用方 |

### 6.4 日志与诊断

- 统一日志目录 `%LOCALAPPDATA%\Eztools\logs`，按工具分文件 + 轮转
- 工具 stdout/stderr 自动重定向入库
- 提供"诊断包导出"（日志 + 配置 + 工具清单 + 版本），便于排查

### 6.5 进程生命周期与资源治理

| 机制 | 策略 |
|---|---|
| 崩溃熔断 | 10 分钟内崩溃 3 次 → 自动禁用该工具 + 通知（`resident` 工具收紧为 5 分钟 2 次） |
| 调用超时 | 按 `weight` 取默认值，可声明覆盖 |
| 资源限制 | 用 **Windows Job Object** 施加内存上限 / 进程数限制（不自研监控） |
| 崩溃恢复 | `resident` 工具重启后调用 `tool.recover` 恢复现场 |
| 卸载 | 只删工具目录，保留配置与私有数据 |

---

## 7. 配置 schema 驱动设置 UI ★

**这是相对 PowerToys 最大的单点改进。**

PowerToys 需要在 `PowerToysModules.cs`、`ModuleHelper.cs`、`ModuleIconResolver.cs` **三处**维护平行的硬编码模块清单，新增一个模块必须同步改三处 C# 代码，漏一处就出 bug。Eztools 下新增工具**零 UI 代码**。

**渲染映射**：

| JSON Schema | 渲染控件 |
|---|---|
| `boolean` | 开关 |
| `string` | 单行输入框 |
| `string` + `enum` | 下拉选择 |
| `string` + `format: directory` / `file` | 路径选择器 |
| `string` + `format: textarea` | 多行输入框 |
| `integer` / `number` | 数字输入框（有 `minimum`/`maximum` 时可用滑块） |
| `array` | 列表编辑器 |
| `object` | 分组折叠区 |
| `title` / `description` | 标签 / 帮助文本 |
| `default` | 初始值（未配置时生效） |
| `x-order` | 字段排序（扩展键） |

支持标准 JSON Schema 子集，保证将来可复用于远程配置（§1.3）。

---

## 8. 生命周期与资源治理

### 8.1 生命周期三型

| 类型 | 语义 | 宿主行为 | 资源配额 |
|---|---|---|---|
| `transient`（默认） | 即用即走 | 进程按需启动、空闲后回收 | 标准 |
| `resident` | 常驻 | 宿主保活；可声明 `latency: interactive` 表示冷启动不可接受，须预热常驻 | 标准 + 常驻预算 |

> `latency: "interactive"` 的**运行时行为由 `lifecycle: "resident"` 承担**（启动钩子预热 + 崩溃自动重启）。
> 本字段只作意图声明与诊断依据，宿主不据它做额外动作 —— 见 §4.1 清单示例旁的同一标注。
| `task` | 长任务 | 支持进度上报（`host.progress`）、取消、后台继续、**可恢复**（进度持久化） | 可申请上浮（需用户确认） |

### 8.2 `resident` 工具的强制要求

`resident` 工具的崩溃后果比 `transient` 严重得多——窗口管理器崩溃会把窗口留在错乱位置，按键监听崩溃会让按键失效。因此：

1. **必须实现 `tool.recover`**：宿主重启该工具后调用它恢复现场
2. 崩溃阈值收紧为 5 分钟 2 次
3. 连续崩溃时采取"回滚到安全状态"而非直接禁用

### 8.3 独占资源与冲突仲裁

以下资源全局唯一，多个工具同时持有必然互相破坏：

| 独占资源 ID | 典型占用者 | 冲突后果 |
|---|---|---|
| `desktop.windowManager` | 平铺窗口管理器（GlazeWM 类） | 两个窗口管理器互抢窗口，桌面错乱 |
| `input.<键>.explorer` | 空格键预览类（QuickLook / Peek 类） | 按键双重拦截 |
| `find.provider.default` | 文件索引类（Everything 类） | 搜索结果源冲突 |
| `overlay.fullscreen` | 全屏覆盖窗类 | 覆盖窗叠加，无法交互 |
| `hotkey.<组合键>` | 任意 | 热键抢占 |

宿主在启用工具时检测冲突，已占用则提示用户仲裁，**不允许"两个都在跑"的中间态**。

> ### ⚠️ 这张表的边界（2026-09-19 澄清 —— 此前没写清楚）
>
> **仲裁只覆盖 Eztools 自己管辖的工具。** 上表"典型占用者"一列描述的是**资源类别**，
> 不是"宿主能拦住的东西"。如果 GlazeWM 已经作为一个独立程序在跑，Eztools
> **既不会、也不该去接管它** —— 它不在我们的进程生命周期里，硬干预就变成另一个产品了。
>
> | 情形 | 宿主行为 |
> |---|---|
> | 两个 **Eztools 工具**抢同一个独占资源 | ✅ 启用时检测 → 提示仲裁 → 不允许同时跑 |
> | 一个 Eztools 工具 vs 一个**已装的第三方程序** | ⚠️ **不能阻止**。能做的只是**启用前告知**"系统里已有同类占用者" |
> | 两个第三方程序互相冲突 | ❌ 不管辖 |
>
> **为什么非要写清这条**：上表的写法很容易让人以为"Eztools 会帮我管住 GlazeWM 和 QuickLook" ——
> 按现有设计**它不会**。与其留一个会被误解的承诺，不如把边界划明。
> （这条是菲比 2026-09-19 拿出自己的日常工具清单核对时发现的：她列的六个工具 6/6 命中了本表与 §9.2，
> 但其中大多数只能落在"不能阻止"那一行。）
>
> **一个可选的补强（待评估，未排期）**：**只读的「环境冲突诊断」**——
> 扫描系统里已知的抢占型工具（平铺窗口管理器 / 索引服务 / 空格键预览 / 覆盖窗类）是否在运行，
> 在设置页或 `ezt doctor` 里报告"你装了这几个，其中 X 与 Y 属同类，会互抢"。
> 它只读、不接管，成本低，填的是"装了六七个抢占型工具却没人告诉你在抢"这个真实空白。
> 若要做，应在 P2 之前单独评估。
>
> **2026-09-21 更正**：上面的时机建议**已过期**——P2 于 2026-09-19 收官。本项按
> `docs/未完成项与待决清单.md` 排为 **P3 级（建议不做，价值真实但最低）**；
> 若翻案，先修掉本句过期时机，再按 `doctor` 只读检测的路径实施。

---

## 9. 特权层（Core 特权原语）

### 9.1 什么时候需要

**先问：需要特权的到底是哪个动作？** "工具需要管理员权限"通常**不等于**"工具必须以管理员权限常驻"。多数情况只有少数操作需要特权，其余可以不提权。

以磁盘分析类工具为例：读原始卷需要管理员，但 **MFT 解析、目录树构建、聚合统计、UI 渲染全都不需要**。特权面可以从"整个工具"收敛到"1 个原语"。

### 9.2 Core 特权原语集

固定且数量有限，**每个都是明确、可审计的具体操作，而不是"调用任意 Win32 API"**。

| 原语 | 用途 | 风险 |
|---|---|---|
| `process.enumerate` | 进程列表 | 低 |
| `process.terminate(pid)` | 结束进程 | 中 |
| `handles.enumerate(pid?)` | 枚举系统句柄表 | 中 |
| `handles.resolve(handle)` | 句柄 → 路径 | 中 |
| `handles.close(handle)` | 关闭句柄 | 🔴 高（可致数据损坏） |
| `volume.enumerate` | 卷列表 | 低 |
| `volume.readRaw(volume, offset, len)` | 读原始卷扇区 | 中 |
| `volume.readMft(volume)` | 读 MFT | 中 |
| `volume.readUsn(volume, since)` | 读 USN journal | 中 |
| `window.enumerate` / `window.control` | 窗口枚举 / 控制（含提权窗口） | 中 |
| `input.installHook(scope)` | 安装全局输入钩子 | 高 |
| `fs.readPrivileged(path)` | 读受保护路径 | 中 |
| `registry.writeHklm(path, value)` | 写 HKLM | 高 |
| `elevated.run(exe, args)` | 提权执行外部程序 | 高 |

**设计原则**：
1. 固定数量级（十几到几十个），不追求覆盖一切
2. **缺能力就新增一个原语（第一方实现），不开通用后门**
3. 每个原语做：参数校验 + 调用方身份校验 + 审计日志
4. IPC 端点用 ACL 限制连接方，并校验调用方完整性级别（**拒绝低完整性调用者**，防 UIPI 式提权攻击）

### 9.3 为什么这样是可信的

**最关键的一条：工具代码永远不进特权进程。**

| | 错误做法（已被否决，见《附录 B》） | 本方案 |
|---|---|---|
| 特权进程里跑什么 | 工具代码 | **只有第一方原语实现** |
| 中介对象 | 任意 API，试图代理一切 | 有限原语，第一方实现 |
| 缺能力时 | 开通用后门 | **新增一个原语** |
| 校验位置 | 每次 API 调用（高频、无法穷举） | 每次 IPC 消息（低频、可穷举、可审计） |

工具通过 `host.primitive.call` 请求原语，宿主转发给 Core 层执行并做审计。工具拿不到特权句柄。

### 9.4 特权常驻的形态选择

| 形态 | 免每次 UAC | 开机自启 | 可有 GUI | 防篡改 | 部署复杂度 |
|---|---|---|---|---|---|
| **计划任务（最高权限）** | ✅ | ✅ | ✅（用户会话） | 中 | 低 |
| **Windows 服务** | ✅ | ✅ | ❌（Session 0 隔离） | 高（ACL + 失败恢复） | 中 |
| 管理员普通进程 | ❌ 每次确认 | 需配合 | ✅ | 低 | 最低 |

**推荐组合**：Windows 服务承载特权原语 + 计划任务/普通进程承载 UI。
参考范式：杀毒软件与 EDR 的「SYSTEM 服务 + 用户态 UI + 受保护 IPC」。

---

## 10. 与 PowerToys 的对照

### 10.1 抄什么

| PowerToys 的做法 | 说明 |
|---|---|
| 工具实现统一接口（`PowertoyModuleIface`） | 边界干净，实现一个类就能加工具 |
| 单一设置 UI + 统一托盘入口 | 工具集合的形态正确 |
| 统一热键注册 | 方向对，但需加强冲突检测 |
| 单一安装器 / 更新器 | 正确 |
| 工具间共享基础设施（日志、IPC、提权） | 避免每个工具重复造轮子 |

### 10.2 修什么

| PowerToys 的做法 | 问题 | 修法 |
|---|---|---|
| 模块清单硬编码（36 项数组） | 加模块要改宿主源码重编译 | 清单驱动发现（§6.1） |
| 模块 DLL 加载进宿主进程 | 崩溃拖垮全局；第三方代码进特权进程 | 每工具独立进程（§3、§9） |
| `enabled` 只管 `enable()`，DLL 照样载入 | "未安装"无意义，缺模块即加载失败 | 区分"已启用/已加载"（§6.1） |
| 加载失败弹 `MessageBoxW` | 每次开机被弹窗骚扰 | 静默降级 + 日志（§6.1） |
| 三处平行硬编码 UI 清单 | 新增模块要同步改三处 C# | 配置 schema 驱动 UI（§7） |
| "整体以管理员运行与否"的粗粒度开关 | 提权面过大 | 特权原语分离（§9） |
| 模块与宿主共用安装目录 | 无法独立版本化 | 工具目录独立（§4.1） |

### 10.3 优先改进顺序（对用户体验影响最大）

1. **配置 schema 驱动设置 UI** —— 消除三处硬编码，新增工具零 UI 代码
2. **热键冲突检测** —— 直接消灭最常见的一类抱怨
3. **工具独立进程** —— 一个工具崩溃不影响其他工具与 UI
4. **特权原语分离** —— UI 与多数工具无需提权
5. **清单驱动发现** —— 新增工具 = 新增一个目录

---

## 11. 开发者体验

两个成熟项目（Wox、Flow Launcher）都在这上面投入，值得照搬。

| 项 | 做法 |
|---|---|
| **项目模板** | `ezt new tool <name> --runtime python` 生成骨架（含 §5.4 的 `sys.path` 前缀） |
| **热重载** | 开发模式下保存即重启工具进程，无需重启宿主 |
| **本地挂载** | `ezt dev link <path>` 把开发目录注册为工具源 |
| **调试** | `ezt dev logs --follow <toolId>` 实时看日志与 IPC 报文 |
| **打包** | `ezt pack <path>`，按 §5.4 策略处理依赖 |
| **CI 模板** | GitHub Actions 工作流：校验 schema → 打包 → 发布 Release |
| **示例工具** | 至少 3 个：纯标准库 / 带 vendor 依赖 / 带面板 UI |
| **SDK** | Python SDK 提供 handler 装饰器、类型化 API 封装、bootstrap 助手 |

> CmdPal 的经验：**"能在 10 分钟内跑起来第一个工具"比任何文档都重要。**

---

## 12. 路线图

### P0 —— 宿主骨架 + 最小闭环 ✅ 已完成（见 §14）

> 各阶段标注的验收数字（22/41/65/19 项）为**当时快照**，均已是 `scripts/acceptance.sh` **85 项基线**的一部分（2026-09-20）。
- [x] 技术验证：**C# 最小宿主 + Python 工具进程跑通 stdio JSON-RPC**（先探通最难的路）
- [x] `tool.json` schema + 校验器
- [x] 模块注册表：扫描 `tools/*/tool.json`
- [x] 工具主机进程管理 + 崩溃熔断
- [x] 生命周期：先只做 `transient`
- [x] `weight`：先只做 `lite`
- [x] 贡献点：先只做 `commands` + `actions`
- [x] 示例工具端到端（落地 4 个：echo / wordcount / filehash / probe）
- [x] 嵌入式 Python 分发方案落地
- **验收**：新增一个工具目录 → 宿主自动发现并可用，**不改宿主任何代码**
  → 已用「SDK 模板复制成新工具」验证通过，`scripts/acceptance.sh` 可一键复现（22 项全绿）

### P1 —— 配置与设置 UI（2026-09-19 拆成 a / b 两半）

**P1a —— 配置中心** ✅ **已完成（2026-09-19，落地记录见 §16）**
- [x] 配置中心（读写 + schema 校验 + 变更推送通路）
- [x] 配置与工具目录分离
- [x] 工具私有 KV **不并入**配置中心（决定与理由见 §6.2）
- 验收：`scripts/acceptance.sh` **第 8 步 10 项**，全套 **41/41 全绿**

**P1b —— 设置 UI ✅ 已完成（2026-09-19，落地记录见 §18）**
- [x] 配置 schema → 设置页自动渲染（UI 栈 = WPF + iNKORE.UI.WPF.Modern，§2.1 已拍板）
- 验收：`scripts/verify-desktop.py` 的「设置清单」断言 5 项，全套 **65/65 全绿**

> ~~**为什么拆开**：P1a 是纯宿主逻辑、**零 UI 依赖**，现在就能做完；P1b 依赖那个还没定的 UI 栈。~~
> 这个拆分的前提（UI 栈未定）已解除 —— WPF 与 WinForms 托盘共存经探针实测成立。

### P2 —— 常驻、热键与治理 ✅（2026-09-20 全清：提前件 + Job Object + 诊断包 + resident/task 生命周期）

**为什么提前**：P0 之后工具只能命令行调用（`ezt invoke ...`）—— 没有托盘、没有右键菜单、
没有热键。对一个"PowerToys 类工具集"，**"能用"的门槛是托盘 + 一个入口**，而那属于 P2。
按原顺序把它排在 P1 之后，"能用"会被推后两轮。

| 提前做 | 理由 | 是否依赖 UI 栈决策 |
|---|---|---|
| **托盘菜单合成** | "能用"的最短路径。没有它，工具等于不存在 | ✅ **不依赖**（实测：`UseWindowsForms` + `NotifyIcon` 即可，无需选 WPF/WinUI） |
| **热键仲裁器** | 与托盘同批；也是 Eztools 自己工具之间仲裁的基础 | ✅ 纯宿主逻辑（冲突提示走托盘气泡） |
| **独占资源冲突仲裁** | §8.3 的核心价值（**边界务必看该节的注**：只管 Eztools 自己的工具） | ✅ 纯宿主逻辑（提示同上） |

> **这一列的用途**：UI 栈决策只阻塞 **P1b 的设置窗口**，上面三件都不被阻塞 ——
> 所以"先定 UI 栈再开工"并非必需，**先做托盘同样可行，而且它反过来会给 UI 栈决策提供真实输入**
> （右键菜单要装什么、要不要一个大的设置中心，都会影响 WPF vs WinUI 的权衡）。

> **托盘已完成**（2026-09-19，落地记录见 §17）：契约 `menus.input` + 菜单自动合成 +
> `Eztools.Desktop` 托盘宿主 + 19 项验收断言。**热键仲裁与独占资源仲裁已完成**（2026-09-19，落地记录见 §19）——P2 至此全清（尾巴 3 项见 §12 末注，2026-09-20 亦已落地）。
>
> 方案：`docs/P2-托盘-实施方案.md`。
> 写方案前的探针撞出**一个必须一并修的契约缺口**：
> `menus` 贡献点没规定"参数从哪来"，导致**现有两个已声明托盘菜单的命令点了都失败**
> （`ValueError: 缺少参数`）—— 菜单能显示、点击有反应，但一次都跑不成。
> 修法是给 `menus` 加 `input` 字段（`none` / `clipboard`），并让"能零参执行或声明了 input"
> 成为进托盘的前提；这条规则**只能靠验收断言守**（handler 是动态的，静态校验不了）。
> 详见该方案 §1.2。

【2026-09-20 尾巴全部落地】Job Object（`ToolJobObject.cs`，kill-on-close，resident 分流不挂）·
诊断包（`ezt diag`）· `resident`/`task` 生命周期（`RunStartupLifecycleAsync` + 崩溃自动重启 +
`PushRecoverAsync`；验收工具 keepalive/tasktool；selftest 6 项生命周期断言）。落地记录见 §21。

**待评估（未排期）**：只读的「环境冲突诊断」—— 报告已装的第三方抢占型工具之间的潜在冲突。
详见 §8.3 末尾的注。做与不做、放哪个阶段，需单独评估。

### P3 —— 特权层 ✅（2026-09-20 完成，落地记录见 §20）
- [x] Core 特权原语服务（独立进程 + 管道 CurrentUserOnly + 完整性级别校验 + 审计）
- [x] 提权形态落地（计划任务；Windows 服务形态明确推迟，见 §20）
- [x] 首批原语：`handles.enumerate`、`volume.readMft`、`process.terminate`（另有 `process.enumerate` / `volume.enumerate` 两个低风险配套）
- [x] `host.primitive.call` 通路（含清单 `elevatedPrimitives` 声明门槛）

### P4 —— 工具间协作与体验完善（Wave 1 ✅ / **Wave 2 已拍板开工，见 §23**）
- [x] `host.invokeTool` 真实通路（2026-09-20）：含命令归属校验 + **调用链环检测**（成环立刻拒绝，绝不排队等待——等待即死锁）
- [ ] **宿主内部事件总线** —— ✅ **契约已定（2026-09-21，D2）**：**只做「宿主 → 托盘/设置 UI」的内部事件，不开放给工具订阅**。
      复用已有 `event Action<T>` 模式（`ToolQuarantined` / `ResidentSnapshot` / `NotifyRequested` 同款）；
      解决"UI 要刷新但只能手动点刷新"这个**已存在**的痛点。**不做** `host.events.subscribe` 弱类型总线（当前无工具需要，YAGNI）
- [x] `panels` 贡献点（面板 UI）—— ✅ **已落地（2026-09-21，Wave 2c，D3：原生 WPF）**：
      契约层 `ToolPanel` + `tool.panel.data` 数据通道 + 6 种声明式节点 + `PanelWindow` 渲染 + 托盘「面板」子菜单。
      **协议先行**（`docs/P4-Wave2c-面板协议.md`，写码之前完成），落地记录见 §25。
- [x] `weight: script` 补齐（2026-09-20）：按 §5.1「仅基础 API」建立 fail-closed 闸门，样板工具 `tools/oneshot`
- [x] `weight: full` 增量 —— ✅ **已落地（2026-09-21，随 Wave 2c）**：`full` 档获得两条独占能力
      （`panels` 贡献点 + `tool.panel.data` 方法）与 120s 默认超时（vs lite 30s / script 10s）。
- [x] 安装器 / 更新器 —— ✅ **已落地（2026-09-21，Wave 2b，A3：便携自包含 zip + 首次运行铺开）**；
      `ezt update` = 覆盖程序 + 保留 `toolsdata/` 与 `runtimes/`，见 §24
- [x] **决定打包形态** —— ✅ **已拍板（2026-09-21，D1）：自包含 · 非单文件**（整套 182 MB）。

      > ✅ **决策依据**（2026-09-21 14:5x 拍板）：**自包含·非单文件**。三条理由：
      > ① 与"自带 Python 运行时、不依赖用户环境"完全同构；
      > ② **零代码改动、零崩溃风险**——单文件要替掉 `Assembly.Location`（`IL3000`）、裁剪与 WPF 彻底不兼容（`NETSDK1168`），两条路都有实测坑；
      > ③ 目标是自用工具集，**"装起来能用"比"分发体积"重要**。
      > 预算口径已在 §15.4 拆成"CLI 形态 40 MB"与"整套分发 220 MB"两行。

      <details><summary>决策时的四形态实测数据（2026-09-21 14:3x 重测，留档）</summary>

      > 真 WPF 设置窗口落地后，用 `dotnet publish -c Release -r win-x64` 实测：
      > Desktop 自包含 **182 MB / 272 文件**（旧记 173 MB）、单文件压缩 **80 MB**（旧记 77 MB）；
      > CLI 自包含 78 MB / 196 文件、CLI 单文件压缩 37 MB **均与旧值一致**；
      > 安装根总计 **43.4 MB**；8 个工具合计仅 **92 KB**（印证"工具体积可忽略"）。
      > **单文件形态端到端跑通**：单文件 `ezt.exe` 放进安装根 `bin/` → `doctor` 报"已安装形态" →
      > **`core start` 成功拉起 ezt-core（pid=26364）**。`IL3000` 仅 `SelfTestCommand.cs:470` 一处，
      > 且位于候选兜底位置 → 去掉一行即可，不涉及架构。
      > 详见 `docs/未完成项与待决清单.md` §2-A4 与 §3 的顺序建议。

      | 形态 | 体积 | 目标机要装 .NET 吗 | 备注 |
      |---|---|---|---|
      | 框架依赖 | **497 KB**（9 文件） | ✅ **要**（.NET 10 运行时；有 GUI 则要 Desktop Runtime） | 体积最省，但用户多一步前置 |
      | 自包含 | **78 MB**（196 文件，CLI）/ **182 MB**（含 Desktop+WPF） | ❌ 不要 | ✅ **本次选中（整套分发形态）** |
      | 自包含 + 单文件压缩 | **37 MB**（CLI）/ **80 MB**（含 WPF） | ❌ 不要 | ⚠️ 报 `IL3000`：单文件下 `Assembly.Location` 恒为空（`SelfTestCommand.cs:470`） |
      | 自包含 + 裁剪 | **22 MB**（50 文件，CLI） | ❌ 不要 | ❌ **WPF 与裁剪不兼容**（`NETSDK1168`）→ 整套分发形态**不能用这条** |

      </details>

      **验证方式**：把 `DOTNET_ROOT` 指向一个不存在的目录后运行 —— 自包含三种变体照常工作
      （目录里自带 `coreclr.dll` / `hostfxr.dll` / `System.Private.CoreLib.dll`），框架依赖版直接失败。
      进一步做过端到端验证：自包含形态铺进全新安装根，**不设 `DOTNET_ROOT`**，`install` →
      `runtime install` → `selftest` **22/22 全绿**。

      ✅ **最终结论（2026-09-21，D1 拍板）**：**自包含 · 非单文件**。与本项目"自带 Python 运行时、
      不依赖用户环境"（§5.4）的思路一致；**不用裁剪**（WPF 不兼容，`NETSDK1168`）、**不用单文件**
      （要替掉 `Assembly.Location`，且每次启动自解压有开销）——两条路都有实测坑，自包含非单文件是**零风险**那条。
      体积 182 MB 已立新预算（§15.4「整套分发 ≤ 220 MB」）。
      `panels`（D3，原生 WPF）落地后体积增量应在几 MB 内，**不会推翻本决策**（这也是 D3 选原生而非 WebView 的直接原因）。

---

## 13. 反模式清单

每一条都对应 PowerToys 的实证问题（《附录 C》）。

| ❌ 别做 | 后果 |
|---|---|
| 硬编码工具清单 | 加工具要改宿主源码重编译 |
| 把工具加载进宿主进程 | 工具崩溃 = 宿主全崩 |
| 用"启用开关"冒充"安装" | "未安装"无意义，缺工具即加载失败 |
| 加载失败弹错误对话框 | 用户每次开机被弹窗骚扰 |
| 工具与宿主共用安装目录 | 无法独立版本化、无法回滚 |
| 省掉配置 schema，改用手写 UI | 新增工具要同步改宿主多处代码 |
| 只发整体更新包 | 改一个工具要重装整个产品 |
| 配置存在工具目录里 | 卸载即丢用户数据 |
| 自研运行时权限仲裁 | 必然为真实需求开后门，是伪安全（《附录 B》） |
| 让工具代码进特权进程 | 特权层沦为任意代码执行入口 |
| 依赖用户自行安装 Python 与 pip 包 | 第一个带依赖的工具就会卡住 |

---

## 14. P0 落地记录（2026-09-17 完成）

### 14.1 交付物

| 位置 | 内容 |
|---|---|
| `src/Eztools.Contracts/` | 契约层：`tool.json` 模型与校验器、贡献点模型、协议方法名、帧模型、semver 范围。**零第三方依赖** |
| `src/Eztools.Host/` | 宿主框架库：路径布局、日志、运行时部署与解析、清单发现、模块注册表、工具进程管理 |
| `src/Eztools.Cli/` | 命令行宿主 `ezt`（`install` / `doctor` / `dirs` / `list` / `info` / `invoke` / `call` / `enable` / `disable` / `runtime` / `selftest`） |
| `sdk/python/eztools/` | Python SDK：协议、主循环、handler 注册、宿主回调 |
| `sdk/python/template/` | 新工具模板（复制即用） |
| `tools/` | 4 个示例工具：echo（基准探针）、wordcount（四类贡献点 + 配置 schema）、filehash（实用工具）、probe（诊断与自检钩子） |
| `scripts/make-payload.sh` | 把一份独立 CPython 打成可部署的运行时载荷 |
| `scripts/acceptance.sh` | P0 验收（跑在临时安装根里，可重复、可进 CI） |
| `docs/P0-验证手册.md` | **验证入口**：三档验证路径（冒烟 / 一键验收 / 手工）/ 反向验证 / 退出码表 / 实测基准 / 已知边界 |
| `README.md` | 构建、运行、目录结构、新增工具指引 |

### 14.2 真实安装目录（`%LOCALAPPDATA%` / `%APPDATA%`）

```
%LOCALAPPDATA%\Eztools\              程序与运行时，可整体重装而不丢数据
├── bin\                             宿主可执行（ezt / 将来的 WinUI 应用）
├── tools\                           内置工具（清单驱动发现）
├── sdk\                             Python SDK 源码，部署运行时时植入其 site-packages
├── payload\                         运行时压缩包（python-<版本>-<rid>.tar.gz）
├── runtimes\python\<版本>\          解压后的隔离运行时，不写 PATH
├── logs\                            宿主日志 + 按工具分文件
├── toolsdata\<toolId>\data\         工具私有数据（卸载工具不丢）
└── cache\                           临时解压

%APPDATA%\Eztools\                   用户数据，与程序目录分离
├── config\<toolId>.json             工具配置（P1 配置中心）
└── state.json                       启用/禁用、热键覆盖
```

`ezt dirs` 打印这份结构。**配置、私有数据与代码三处分离**（§6.2）已落地。

### 14.3 三个关键落地决策

**① SDK 的投递方式：植入运行时的 `Lib\site-packages`**

不是 `-c` 注入预置代码（Flow Launcher 的做法），也不是每个工具目录复制一份。

实测依据：`-I` 确实会忽略父进程的 `PYTHONPATH`，但**运行时自身的 `Lib\site-packages` 仍在 `sys.path` 上**
（实测 `site-packages on path: True`，同时 `isolated=1`、`utf8_mode=1`）。

| | 结论 |
|---|---|
| 工具侧写法 | `from eztools import Tool`，**零 import hack** |
| 版本一致性 | SDK 随宿主版本走，不会出现"某个工具带着旧 SDK" |
| 依赖隔离 | 工具自己的依赖仍走 `Lib/`（SDK 自动把工具目录与 `Lib/` 加入 `sys.path`） |
| 更新方式 | 按文件指纹比对，SDK 变更时自动重新植入；指纹不变则零开销 |

**② 运行时载荷：格式约定与来源**

| 项 | 约定 |
|---|---|
| 文件名 | `<runtime>-<version>-<os>-<arch>.tar.gz`，如 `python-3.13.14-win-x64.tar.gz` |
| 归档布局 | **归档根即运行时根**（解压后根目录下直接是 `python.exe`）；也容忍套一层同名目录 |
| 来源 | **python-build-standalone 的 `install_only` 产物**（实测该产物带 `.extracted` 标记、完整安装布局、自带 pip、无 `._pth`）——与 §5.4 的推荐一致 |
| 裁剪 | 剔除 `Lib/test`、`idlelib`、`turtledemo`、`tkinter`、`tcl`、`__pycache__`、`*_test*.pyd`；**保留 pip**，让工具作者能用自带运行时把依赖 vendor 到工具的 `Lib/` |
| 部署 | 解压到 `cache` 临时目录 → 校验解释器存在 → 整体 `Move` 落位 → 写安装标记（含 sha256）→ 植入 SDK |

**③ P0 用 CLI 而不是 WinUI 3**

| 理由 | 说明 |
|---|---|
| 验收与 UI 无关 | P0 的标准是"新增工具目录 → 宿主自动发现可用" |
| 不被依赖阻塞 | 本机未安装 WindowsAppRuntime，P0 不该卡在 UI 依赖上 |
| 可脚本化 | CLI 能进 `acceptance.sh` 与 CI，UI 不能 |

关键：**`Eztools.Host` 是类库**。P1b 的设置应用（**UI 栈待定**，见 §2.1 的注）引用同一个门面，
两者只是同一宿主的不同前端，不是两套逻辑。

### 14.4 实测数据（Windows 11 · .NET 7.0.20 · CPython 3.13.14 · win-x64）

> 下表是 **P0 当时（2026-09-17）** 的数据，保留作对照。
> **2026-09-19 已把整个解决方案从 net7.0 升到 net10.0**（SDK 10.0.401，便携装于 `D:\dotnet10`）
> —— 起因是 .NET 7 早已终止支持，而 .NET 8/9 也将于 2026-11-10 终止，唯一合理的 LTS 落点是 .NET 10。
> 升级后重新实测的数据见 `docs/P0-验证手册.md` §7。

| 项 | 实测 | 与 spike 对比 |
|---|---|---|
| 解释器启动（嵌入式 + `-I -X utf8`） | **114 ms** | spike 126 ms，一致 |
| 解释器启动（不隔离） | 240 ms | spike 167 ms；**隔离更快**的结论再次成立 |
| `ezt version`（宿主启动 + 清单扫描） | 161 ms | — |
| `ezt invoke` 端到端（起进程 + 2 次 IPC 往返 + 回收） | **459 ms** | 首次实现是 1128 ms，修掉 SDK 重复植入后降到 459 ms |
| 运行时首次部署（解压 1324 个文件） | 首次 30 s / 同内容再次 1 s / 复用 **241 ms** | — |
| 运行时载荷 | **13.1 MB**（1457 条目，源 53 MB，打包 3 s） | 与 §5.4 估算的 15~20 MB 相当 |
| 端到端自检 | **22 项全通过** | spike 为 9 项 |

**关于首次部署的 30 秒**：同一份载荷内容的第二次解压只要 1 秒，判断是 Windows Defender 对
**首次出现的新文件内容**的一次性扫描成本，不是结构性问题。但**产品首次安装时用户感知到的就是 30 秒**，
所以**安装器必须给进度反馈**，不能静默等待。
（安装器本身排在 **P4**，见 §12 —— 但这条约束从"首次安装会卡 30 秒"这个事实成立那天起就一直有效，
不因为安装器晚做而消失。）

### 14.5 P0 阶段新踩的 12 个坑

| # | 症状 | 原因 | 修法 |
|---|---|---|---|
| 1 | 一个工具都发现不到 | 宿主首次运行会在安装根创建**空的** `tools`，遮蔽了仓库里的真实工具源 | 判据用"目录里有清单"，不是"目录存在" |
| 2 | 每次调用都多花 505 ms | 安装标记的键是 camelCase，`System.Text.Json` 默认大小写严格匹配 → `SdkVersion` 恒为 `null` → 指纹每次判定为变化 → 每次重新植入 SDK | `PropertyNameCaseInsensitive = true` |
| 3 | `list --json` 静默失败、退出码 3、stdout 空 | `ConfigSchema` 已挂在清单解析出的节点上，直接挂到新 JSON 对象会抛 `The node already has a parent.` | 挂接前 `Clone()` |
| 4 | 上一条"静默"的原因 | JSON 模式下错误输出也被抑制 | 错误与失败必须仍可见，且走 **stderr** |
| 5 | emoji 输出成 `\uD83C\uDF61` | `UnsafeRelaxedJsonEscaping` 基于 `UnicodeRanges.All`，只覆盖 BMP | 输出前还原 `\uXXXX`（`>= U+0080`） |
| 6 | `tar: Cannot connect to D: resolve failed` | `tar -f D:/x` 被当作 rsh 的 `host:path` 语法 | 归档用裸文件名 + subshell `cd` 定位 |
| 7 | 载荷校验"根目录没有 python.exe"（实际有） | `tar -tzf \| grep -q` 的 `grep -q` 命中即退出 → tar 收到 SIGPIPE → `set -o pipefail` 把管道判为失败 | 先完整读取再匹配，不用 `grep -q` |
| 8 | 变量里拼进了两份路径 | `A && B \|\| C && D` 中 `D` 在 `B` 成功时也执行 | 用显式 `if/else` |
| 9 | 目录建了、命令退出 0，但文件却建在别处 | Git Bash 的 `pwd` / `$TEMP` 给 POSIX 路径（`/d/...`、`/tmp`），交给 .NET 可执行文件后会被按**进程当前盘符**解析。**实测踩到三次**：`/d/01-…`→`D:\d\01-…`、`/tmp/x`→`D:\tmp\x`、`/tmp/x`→`C:\tmp\x`（cwd 在 C 盘时）。命令退出 0、目录也不报错地建了，只是建在别处 | 脚本里一律 `pwd -W` / `cygpath -m`；并加**硬断言**：拿到非 Windows 形式路径直接中止，不允许静默继续 |
| 10 | `--path docs/xxx.md` 报"文件不存在"，但文件明明在 | 工具进程的工作目录是**工具目录**（不是调用方 cwd），相对路径被相对工具目录解析 | 由最清楚调用方 cwd 的那一层（CLI）绝对化；SDK 与模板文档写明该语义 |
| 12 | `ezt list --tools-dir /d/.../devtools` **静默发现 0 个工具** | 传进来的 POSIX 路径被 .NET 按当前盘符解析成 `C:\d\...`。同一根因**第三次**发作（前两次来自 `pwd` 与 `$TEMP`） | 在**所有路径入口**做归一化（`/d/x` → `D:/x`）：CLI 参数、环境变量、安装根 / 配置根 / 源码根。**靠文档提醒解决不了**——只要能写 POSIX 路径的地方，迟早会有人写 |
| 11 | 把 `ezt.exe` 单独放到别处后**一个工具都发现不到** | 工具源与 SDK 来源的解析顺序是"先安装根、再向上找仓库根"。开发形态靠仓库兜住了，而安装根下的 `tools`、`sdk` 从**没有任何东西去填**——"开发跑得通"被误当成"装起来能用" | 补 `ezt install`：把 `tools/`、`sdk/`、`payload/`、`bin/` 铺进安装根；并让 `doctor` 输出"安装完整度"，缺哪块直接给出该跑的命令 |

> 这张表是对 spike 方法论的正向验证：spike 阶段"9 个用例崩了 2 个"证明了**先崩在验证里比崩在交付后便宜**；
> P0 阶段又崩了 10 次，但**全部崩在写框架的过程中**，没有一个是留到交付之后才发作的。
> 其中 #2 / #3 / #5 / #10 都属于同一类：**不报错、只静默降级到最慢或最错的路径**——这类问题只能靠端到端验收兜住，
> 所以 `scripts/acceptance.sh` 与 `ezt selftest` 是 P0 的必要交付物，不是锦上添花。

### 14.6 落地时与设计的偏差

| 设计 | 落地 | 原因 |
|---|---|---|
| 模块发现"现在只扫内置 `tools/`" | 落地时即支持多目录源（安装根 / 仓库 / 开发挂载） | 开发布局与部署布局**必须都正确**，否则"开发跑得通、发布出去坏了"。§1.3 已预留该扩展点 |
| `host.storage` 属于 P1 | P0 即实现 | 工具 → 宿主的**反向调用必须闭环**：宿主不回响应会让工具侧永久挂起。这是协议正确性问题，不是功能问题 |
| 贡献点先只做 `commands` + `actions` | `hotkeys` / `menus` 也一并解析与展示 | 解析与展示是廉价的；注册与仲裁仍在 P2 |
| 未提及"已启用 / 已加载"的持久化 | 落地为 `%APPDATA%\Eztools\state.json` | 这是 §10.2 对 PowerToys 的核心修正，必须能在进程重启后仍然成立 |
| 未提及"安装动作"本身 | 补 `ezt install`（把 `tools/`、`sdk/`、`payload/`、`bin/` 铺进安装根） | P0 原本只验证了开发形态；实测发现把 exe 单独部署时安装根是空的、向上又找不到仓库根，**一个工具都发现不到**。不补这一步，"装起来能用"根本无从谈起。完整的 MSI/卸载/更新仍属 P4 |

### 14.7 P1 的入口

P0 已经把 P1 需要的所有输入准备好了：

| P1 要做 | 现成的输入 |
|---|---|
| 配置中心 | `manifest.ConfigSchema`（JSON Schema 子集）+ `EztoolsPaths.ToolConfigFile(toolId)` |
| schema 驱动设置 UI | 渲染映射表见 §7；`ezt info <toolId>` 已经在用同一份数据打印配置项表格 |
| 配置变更推送 | 协议方法 `tool.onConfigChanged` 已定义、SDK 已实现接收侧 |
| 工具私有 KV | `host.storage.*` 已端到端可用（P0 实际落地） |

WinUI 3 应用的接入方式：引用 `Eztools.Host`，用 `EztoolsHost.CreateAsync()` 拿到宿主门面，
把 `HostOptions.EchoLogToConsole` 设为 `false`（GUI 宿主日志只落文件）。

---

## 15. 轻量化预算（硬约束）

> **来源**：菲比明确要求"做到轻量化"（2026-09-19）。这一节把它变成**可核对的上限**，
> 而不是一句愿望。下面所有数字都是本机**实测**，不是估算。

### 15.1 现状账本（2026-09-19 实测）

**磁盘**

| 项 | 实测 | 说明 |
|---|---|---|
| 宿主 `ezt.exe`（框架依赖） | **497 KB** | 9 个文件 |
| 宿主（自包含） | **78 MB** | 单文件 37 MB / 裁剪 22 MB |
| Python 运行时载荷 | **13.1 MB** | 已裁剪（剔 test / tkinter / idlelib / tcl），源 53 MB |
| 解压后运行时 | **44 MB** | Lib 21 / DLLs 13 / python313.dll 6 / include 3 / libs 1 |
| 安装根合计 | **44 MB** | 几乎全是运行时，代码本身占比不到 2% |

**内存（工作集）**

| 场景 | 实测 |
|---|---|
| 嵌入式解释器（空闲） | **15.6 MB** |
| 解释器 + 已加载 SDK | **18.7 MB** |
| 宿主 `ezt.exe`（调用工具期间） | **40.2 MB** |
| 工具进程（调用期间） | **19.1 MB** |
| 一次调用的峰值合计 | **≈ 59 MB，随后全部退出** |
| **常驻总量（当前）** | **0 MB** —— 所有工具都是 `transient` |

> 对照：Electron 类常驻应用通常 100~200 MB/实例。

### 15.2 为什么现在不臃肿（这七条要保住）

1. 每工具独立进程 + `transient` → **不用到的工具零内存**。这是最强的轻量保证
2. 空闲回收 + 崩溃熔断 → 进程会真的退出，不会悄悄堆积
3. 清单驱动，**不预加载**任何工具
4. 运行时载荷已裁剪：53 MB → **13.1 MB**
5. SDK 植入**运行时**的 site-packages，而不是每个工具复制一份 —— 这本身就是"共享"思路
6. 宿主是**类库**，CLI 与将来的 GUI 共用同一份逻辑，不是两套
7. `--onefile` 被明确列为**最后手段**（它每次调用都要解压，既慢又占 I/O）—— 这条定得对

### 15.3 会真正让它臃肿的五个做法（要盯住的）

| # | 做法 | 后果 | 对策 |
|---|---|---|---|
| 1 | **每个工具各自 vendor 一份重依赖** | 20 个工具各带一份 numpy ≈ 800 MB | 建**共享依赖池** `%LOCALAPPDATA%\Eztools\pylibs\<包名>-<版本>`，工具在 manifest 里声明所需；纯标准库 / 轻依赖的工具仍各自 vendor |
| 2 | 滥用 `--onefile` | 每次调用解压，启动慢 + 临时目录堆积 | 保持禁用，除非有极强理由 |
| 3 | `resident` 工具开得太多 | 每个常驻 Python ≈ 19 MB，10 个就是 190 MB 常驻 | 只给真正需要的（如全局热键监听）开 `resident` |
| 4 | 面板 UI 用 WebView2 / Chromium | 一个 WebView 进程 50~100 MB | 面板优先用**原生控件**；WebView 只在确需富文本/代码编辑器时用 |
| 5 | WinUI 3 带来的 Windows App Runtime | 多一层依赖 + 体积 | 这也是 UI 选型的一条附加考量（见 §2.1 的注） |
| 5b | **设置窗口若用 WPF（同进程）**（2026-09-19 探针实测新增） | **裁剪对 WPF 完全不可用**：SDK 报 `NETSDK1168`，硬绕过编译后**创建窗口即运行时崩溃**（`0xE0434352`）——这不是"不推荐"而是"真的跑不了"。自包含从托盘版的 118 MB 涨到 **173 MB**，单文件压缩后 **77 MB**，框架依赖仍是 190 KB | 代价 = 放弃 37 MB 裁剪形态（+40 MB 换一个成熟 UI 栈）。若不可接受，替代路径是「设置窗口独立进程」或换 Avalonia（重写托盘），都有各自代价（见 §2.1 的注） |
| 6 | **托盘用 WinForms**（2026-09-19 实测新增） | **裁剪被 SDK 直接禁止**（`NETSDK1175`），自包含从 78 MB 涨到 **118 MB**；强行绕过裁剪后 **37 MB**，其中约 15 MB 是 WinForms 的固定成本 | 属可接受代价（详见 `docs/P2-托盘-实施方案.md` §1.4 / §7）。**但要知道它把"自包含+裁剪 ≤ 25 MB"这条预算打破了** |

> 第 6 条实测细节：`UseWindowsForms=true` 与 `PublishTrimmed=true` **不能共存**，构建会失败；
> 用 `-p:_SuppressWinFormsTrimError=true` 可绕，绕过后再无 IL 警告（.NET 10 的 WinForms 已带 trim 注解），
> 且实测托盘图标、消息循环、通知区域登记全部正常。**"不支持"仍是长期风险，只是风险窗口很窄**
> —— .NET 10 是 LTS（支持到 2028-11），而我们只用了 `NotifyIcon` + `ContextMenuStrip` + 气泡三个控件。

第 1 条是**最大的风险源**，而且它不会在早期暴露 —— 前几个纯标准库工具人畜无害，
等到第一个需要 numpy / Pillow 的工具出现时才会发现没有共享机制。

> ### 📌 第 1 条的触发判据（2026-09-21 写死，清单 B4 / 决策 D6）
>
> **不提前建池**（现在 8 个工具全部只用标准库，无真实用例驱动的池一定会设计错 —— YAGNI）。
> 但把"什么时候**必须**建"变成**可判定的规则**，不靠记性：
>
> | # | 触发条件 | 到点后的动作 |
> |---|---|---|
> | **T1** | **第二个工具需要同一个重依赖**（如 tool-A 与 tool-B 都要 numpy）→ *这是必须建的判据* | 立即改走共享池，形态取下面的**最小版** |
> | T2 | 单个重依赖的体积 **> 20 MB**（即使只有一个工具用） | 同样走共享池（否则单工具目录会突破 §15.4 的 5 MB 上限） |
> | T3 | 同一个工具需要**两个以上**重依赖 | 走共享池，避免该工具目录失控 |
>
> **最小版形态（不做版本池）**：宿主在启动工具时统一把 `%LOCALAPPDATA%\Eztools\pylibs\` 注入
> 子进程的 `sys.path`，池内按 `<包名>-<版本>` 平铺目录；工具在 manifest 里声明所需包。
> **先不做多版本共存**——等真的出现"A 要 numpy 1.x、B 要 numpy 2.x"再谈版本池，
> 那时才知道要按什么维度分目录。
>
> **为什么现在明确不建**：预算表（§15.4）已经留了「共享依赖池总量 ≤ 150 MB」这一行，
> 而 `budget.sh` 对它报 `skip`（未建）——**这是诚实状态，不是欠债**。写死判据是为了让"到点"变得可判定。
> 详见 `docs/未完成项与待决清单.md` B4 / D6。

### 15.4 预算上限（硬约束）

| 层 | 上限 | 现状 | |
|---|---|---|---|
| 宿主可执行（框架依赖） | ≤ 1 MB | 0.5 MB | ✓ |
| 宿主可执行（**自包含 + 裁剪**，仅 CLI 形态） | ≤ **40 MB**（2026-09-19 修订：25→40，含托盘） | 37 MB | ✓ |
| **整套分发（自包含·非单文件 + 托盘 + WPF 设置窗口 + Core）** | **≤ 220 MB**（2026-09-21 新立） | **182 MB** | ✓ |
| Python 运行时（解压后） | ≤ 50 MB | 44 MB | ✓ |
| 单个工具目录（不含重依赖） | ≤ 5 MB | 几十 KB | ✓ |
| 共享依赖池总量 | ≤ 150 MB | 未建 | — |
| 单次调用进程峰值内存 | ≤ 80 MB | 59 MB | ✓ |

> **✅ 口径已澄清（2026-09-21，D1 拍板：自包含·非单文件）**
>
> 此前本表只有「自包含+裁剪 ≤ 40 MB」一行，容易被误读成"整个产品 40 MB"。现已拆成**两行**：
> - **CLI 形态**（框架依赖 / 裁剪自包含）：37 MB，**这条老上限仍然有效**；
> - **整套分发形态**（2026-09-21 新立）：**自包含·非单文件**，含托盘 + WPF 设置窗口 + Core，实测 **182 MB**，上限定 **220 MB**。
>
> **为什么上限是 220 而不是 182**：留约 20% 余量给 Wave 2c 的 `panels`（原生 WPF 控件，体积增量应在几 MB 内）
> 与后续工具，避免每加一点就改预算。**WPF 与裁剪不兼容**（`NETSDK1168`，实测）——所以整体分发形态**不能用裁剪**，
> 这也是它的体积从 37 MB 涨到 182 MB 的根本原因（多出的是 WPF 的运行时要件）。
>
> **为什么接受 182 MB**：与"自带 Python 运行时（13.7 MB）、不依赖用户环境"完全同构；
> 且**零代码改动、零崩溃风险**——单文件（要替掉 `Assembly.Location`，`IL3000`）与裁剪（WPF 不兼容）两条路都已实测有坑。
> 目标是自用工具集，**"装起来能用"比"分发体积"重要**。
>
> 🔄 **2026-09-21 14:3x 重测（真 WPF 设置窗口已落地后）**：上段引用的 173/77 MB **偏低**，实际为
> **Desktop 自包含 182 MB / 272 文件**、**单文件压缩 80 MB**（主 exe 75,169,954 B + 7 个 WPF 原生 DLL）；
> CLI 自包含 78 MB / 196 文件、CLI 单文件压缩 37 MB 均与旧值一致。
> 另新增两项基线实测：**安装根总计 43.4 MB**（runtimes 43.1 + logs 0.3）、**8 个工具合计仅 92 KB**
> （印证"工具体积可忽略"，8 个工具不到运行时的 0.3%）。
> ⇒ **182 MB 超本条上限 4.5 倍**，但根因是"上限不覆盖该形态"，**不是代码超标**。改写随 D1 一起做。
| 常驻总量（`resident` 全开） | ≤ 100 MB | ≈50 MB（托盘） | ✓ |
| `ezt` 冷启动 | ≤ 200 ms | 150 ms | ✓ |
| 热键响应（P2 目标） | ≤ 100 ms | 已实现（§19：注册 + 仲裁 + 触发回调）；响应耗时未专项实测 | — |

**"基础盘"定义**：宿主 + Python 运行时 + SDK。
**工具永远不进基础盘** —— 它们是可增减的业务内容，不该拖着平台一起长。

> **硬规则**：任何让"基础盘"增长超过 **20%** 的改动，必须单独评审并写明理由。

> **2026-09-19 已按 P2 托盘落地结果修订本表**：自包含+裁剪上限 25 → **40 MB**（现状 37 MB，
> 其中约 15 MB 是 WinForms 的固定成本）；常驻总量现状 0 → **≈50 MB**（"P0 实测 0 MB"的记录作废）。
> 宿主（框架依赖）0.5 → 0.7 MB。实测与理由见 §17.4 与 §15.3 第 6 条。

### 15.5 核对脚本 `scripts/budget.sh`（已实现）

预算要能**自动核对**，否则只是一句愿望。`scripts/budget.sh` 把 15.4 的数字做成断言，
**超限即非零退出**，可进 CI。

```bash
bash scripts/budget.sh            # 完整（约 14 秒）
bash scripts/budget.sh --quick    # 跳过计时 / 内存 / 残留（约 5 秒）
```

检查九项：宿主可执行目录 · Python 运行时 · **基础盘** · 单个工具目录 · 共享依赖池 ·
安装根总计 · `ezt` 冷启动中位 · 单次调用峰值内存 · 调用后无进程残留。

首次运行结果（2026-09-19）：**通过 8 / 失败 0 / 跳过 1**（跳过的是"共享依赖池尚未建立"）。
反向验证过：故意放一个 6 MB 的工具目录 → 报 `[FAIL] ... 超出上限 5120 KB`、退出码 1。

**两条实现上的教训**（都写进脚本注释了）：

1. **计时不能用 shell 的 `date`**。本环境是 MSYS，每次 `date` 都是一次进程启动（约 200 ms）；
   用两次 `date` 去量一个 150 ms 的程序会得到 **550 ms** —— 量到的绝大部分是**测量开销**。
   一律交给 Python 的 `perf_counter`。（这个坑 spike 阶段就踩过一次，当时误以为"裸解释器要 430 ms"。）
2. **`tasklist` 的"内存使用"列含千位逗号**（`15,988 K`），按逗号切列会取错值。
   要用 `awk` 取**倒数第二列**并先去逗号。

> **为什么和 `acceptance.sh` 分开**：`acceptance.sh` 跑在**临时安装根**里
> （保证可重复、不动真实目录），而预算必须量**真实安装根**（"装完之后多大"才有意义）。
> 两者关注点不同，刻意不合并。

---

## 16. P1a 配置中心落地记录（2026-09-19 完成）

### 16.1 补上的那个缺口

P0 留下了接口、但**中间那段从没写**：

| 环节 | P0 时 | P1a 后 |
|---|---|---|
| 清单声明 `config` | ✅ 已解析为 `manifest.ConfigSchema` | 同 |
| 配置目录与路径 | ✅ `EztoolsPaths.ToolConfigFile()` 存在 | 同 |
| **读写落盘** | ❌ **零引用** | ✅ `ConfigStore` |
| **schema 校验** | ❌ 只有"是不是 object" | ✅ 类型/枚举/上下限 |
| **注入工具进程** | ⚠️ `ToolProcess.cs` 有 `options.Config` 位置，**但从没人填** | ✅ 每次启动填 `Effective()` |
| 变更推送 | ⚠️ 方法名已定义，宿主从不发送 | ✅ `PushConfigAsync`（通路） |

> **最直观的症状**：P1a 之前，工具拿到的 `tool.config` **恒为 `{}`** ——
> 即使 `tool.json` 里写了 `default` 也拿不到。这一条现在有了专门的验收断言。

### 16.2 交付物

| 位置 | 内容 |
|---|---|
| `Contracts/ConfigSchema.cs` | schema 子集解析（渲染映射表需要什么就支持什么） |
| `Contracts/ConfigValues.cs` | 类型强制 · 校验 · **默认值 ⊕ 已存值** 合并 |
| `Contracts/JsonText.cs` | JSON 序列化统一出口（含非 ASCII 还原，收口了原先 ConsoleUi 里的私有副本） |
| `Host/Config/ConfigStore.cs` | 读写 · 原子写 · **损坏恢复** · 重置 |
| `Host/Processes/ToolHostManager.cs` | 注入 `tool.start` · `PushConfigAsync` |
| `Cli/ConfigCommand.cs` | `ezt config list/get/set/unset/reset/path/schema` |

### 16.3 三条硬要求（都不是可选项）

1. **原子写**：写 `.tmp` 再 `Move(overwrite)`。直接覆盖写会在崩溃时留下半个 JSON。
2. **损坏文件绝不静默覆盖**：解析失败 → 备份为 `.corrupt-<时间戳>` → 用默认值继续 + 记 Error。
   *宁可留一个看不懂的文件，也不能把用户配置抹掉。*
3. **注入面 == schema**：只有 schema 声明过的字段进 `Effective`；schema 已删除的旧键
   保留在文件里（不销毁数据）但不注入工具，由 `ezt config list` 提示。

### 16.4 验收

`scripts/acceptance.sh` 新增第 8 步 **10 项**，全套从 31 项扩到 **41 项**，全绿。
覆盖：默认值注入 · set 后端到端生效 · 独立落盘 · 三种非法值被拒 · 失败不改文件 ·
unset 回落 · 损坏文件不崩且留备份。

### 16.5 期间踩到的两个坑

| # | 症状 | 原因 | 修法 |
|---|---|---|---|
| 1 | `maxFileSizeMb` 设成 99999 **被接受**（超上限没拦住） | `JsonValue.TryGetValue<double>` **只在底层类型正好是 double 时返回 true，不做数值转换**。命令行传进来的是 `JsonValue.Create(99999L)`（long 背书）→ 读不出 → 整个上下限校验被静默跳过。而 schema 里的 `1024` 来自 `JsonNode.Parse`（JsonElement 背书，转换是通的）→ **两种来源行为不一致**，极难发现 | 统一走 `ConfigValues.TryGetNumber`（double → long → decimal 依次尝试） |
| 2 | 自测脚本连续 4 条"未被拒绝" | 我写了 `cmd 2>&1 \| head -2; E=$?` —— `$?` 取的是 **head 的退出码**，不是被测程序的 | 直接 `OUT=$(cmd); E=$?`，不经管道 |

> 坑 #1 与 P0 阶段的 `PropertyNameCaseInsensitive` 属于同一类：
> **不报错、只静默让校验失效**。这类问题只能靠"故意传非法值看它会不会被拒"兜住 ——
> 所以验收里必须有反向断言，不能只测正常路径。

---

## 17. P2 托盘落地记录（2026-09-19）

> **状态**：Wave 1–5 完成 —— 托盘进程可运行，且有 **12 项可重复断言**。
> 方案与探针实测数据见 `docs/P2-托盘-实施方案.md`。

### 17.1 交付物

| 位置 | 内容 |
|---|---|
| `src/Eztools.Desktop/` | **新项目**：WinExe + `net10.0-windows` + WinForms 托盘宿主 + WPF 设置窗口（§18） |
| `src/Eztools.Host/Tray/` | `TrayMenuModel` / `TrayMenuBuilder`：**与 UI 框架无关**的菜单合成 |
| `src/Eztools.Cli/TrayCommand.cs` | `ezt tray`（`--json` / `--check`）—— CLI 侧的预览与静态检查 |
| `src/Eztools.Contracts/` | `MenuInput` 枚举、`ToolMenu.Input`、解析与诊断码 `contributes.menu-unknown-input` |
| `src/Eztools.Desktop/assets/eztools.ico` | 多尺寸图标（16/20/24/32/48），Python 手写生成，无外部依赖 |
| `scripts/verify-desktop.py` | 托盘进程验收：剪贴板（Win32）/ 通知区域注册表 / 单实例 / 无残留 |
| `tools/filehash`、`tools/wordcount` | 托盘项改为可零参执行（见 17.2） |

### 17.2 契约变更：`menus[].input`

**为什么必须加**：`menus` 声明的是"命令"，但**托盘点击没有上下文**（不像 `actions` 有选中文件）。
契约里不规定"参数从哪来"时，菜单能显示、点击有反应、进程真的起来，却报一句"缺少参数"
—— **菜单是"假能用"的**。P0 时就声明的两个托盘项，实测**两个都跑不起来**。

| `input` | 宿主行为 |
|---|---|
| `none`（默认） | 传 `{}` |
| `clipboard` | 读剪贴板，注入 `args["input"]` |

**约定**：宿主把内容放进 `args["input"]`，**宿主不必知道各工具的参数名**
（工具侧自己映射到 `text` / `path` …）。这样避免"宿主硬编码参数名"的耦合。

**规则**：能进托盘的命令，**要么零参可执行，要么声明了 `input`**。
这条**无法静态校验**（handler 是动态的），只能由验收 9.4 / 10 用具体输入真调一次来守。

### 17.3 三个设计决定

| 决定 | 理由 |
|---|---|
| **托盘 = 进程内宿主**（不是"点一次起一个 `ezt` 子进程"） | 托盘要当"治理者"而非"启动器"：`resident` 保活、热键仲裁、独占资源仲裁都需要一个**长期活着的宿主**；顺带每次点击省约 450ms |
| **菜单合成与 UI 框架解耦** | （写作时 UI 栈未定；后定为 WPF，见 P1b/§2.1）但"菜单该怎么排"与它无关。所以 `TrayMenuBuilder` 能被断言、能被 `ezt tray` 打印、也能被将来的设置页复用 |
| **Cli 不启动托盘** | Cli 是控制台 Exe（改 WinExe 会让**所有命令行输出消失**），Desktop 是 GUI 宿主。`OutputType` 一个项目只能取一个值 —— 平台约束，不是偏好 |

菜单组织维度是 **`group` 而非工具**（用的人的视角是"我想对文件做什么"，不是"用哪个工具"）；
顺序**完全确定**（组按 key 序、组内按 工具id→命令id 序），不依赖工具扫描顺序
—— 否则断言会"有时候通过"，那是最难查的一类问题。

### 17.4 实测数据

| 项 | 值 |
|---|---|
| 托盘常驻内存 | **50.7 MB**（验收实测；探针阶段 47.6~50.3 MB） |
| 宿主产物（框架依赖） | **198 KB** |
| 宿主产物（自包含） | **118 MB** |
| 宿主产物（自包含 + 裁剪） | **37 MB**（需绕 `NETSDK1175`，见 §15.3 第 6 条） |
| 解释器启动 / `ezt invoke` 端到端 | 108 ms / 445 ms（与 P0 一致，**无性能回归**） |
| 验收 | **60 项 / 约 36 秒**（原 41 项 + 托盘 19 项） |

### 17.5 本阶段踩的 5 个坑

| # | 症状 | 原因 | 修法 |
|---|---|---|---|
| 1 | `new Icon(stream, size)` 抛 `Win32Exception(0x80004005)`，**堆栈指向 `Icon` 构造函数** | **ICO 里的 DIB 必须自带 40 字节 `BITMAPINFOHEADER`**（`.res` 资源里才不带）。漏了会得到一个"看起来合法"的 ICO —— 目录项齐全、能被解析出头，但 GDI+ 拒绝加载 | 生成时补头，`biHeight` 写 2× |
| 2 | 托盘进程 stdout 中文乱码 | **WinExe 没有控制台**，`Console.Out` 退回系统 ANSI 代码页（中文 Windows 上是 GBK），调用方按 UTF-8 读就乱 | 自检结果写**文件**（UTF-8）并把编码钉死，不靠 stdout |
| 3 | 重载后"新工具在菜单里可见、点下去说找不到" | `LoadRegistry()` 只换 `Registry` 引用，而 `ToolHostManager` 仍持有旧的那个（**半迁移**） | 新增 `ReloadAsync()` 连同进程层一起重建；托盘「刷新菜单」走它 |
| 4 | Cli 与 Desktop 写同一个 `host-<日期>.log` | 日志路径不含宿主名 | `HostLog` 加可选 hostName → `host-ezt-desktop-<日期>.log` |
| 5 | 托盘图标默认落在**溢出区**，用户以为程序没启动 | Win11 默认行为（`NotifyIconSettings` 里 `IsPromoted` 为空） | 首次运行提示一次；**不去改用户的桌面设置**（那是用户的桌面） |

> 坑 1 值得单记：症状指向 `Icon` 构造函数，第一反应会去怀疑"尺寸参数不对"
> （我确实先改了参数，没用）。**真因在输入数据的格式**里，不在调用方。
> 这与 P0 那批"不报错、只静默失效"的坑互补：这类是**报错了、但报错位置误导**。

### 17.6 边界（什么没做）

- **热键仲裁 · 独占资源冲突仲裁**：P2 的另两件，各自独立交付。
- **长任务进度条**：依赖 `weight: task` 生命周期。
- ~~**托盘菜单里的「设置」**：设置窗口是 P1b，UI 栈未定；先放「打开配置目录」占位。~~ → 已由 §18 交付（托盘「设置…」→ WPF 设置窗口）。
- **托盘与 CLI 之间不做 IPC**：Cli 保持完全独立（开发/运维工具，用户日常不用）。
  这条边界写在这里是为了防止将来有人给它加"发消息给托盘" —— 那会引入一整套
  IPC 复杂度，收益很小。两者共享状态靠**原子写 + 文件锁**（§6.2），足够了。

### 17.7 只能手工验的三件事（自动化边界）

托盘是交互式的。以下三项**无法自动断言**，已在 `docs/P0-验证手册.md` 列出手工步骤：

| 项 | 为什么 |
|---|---|
| 鼠标真的点击菜单项 | 自动化只能到"程序化触发 handler"为止（`--click N`），点不到真实菜单 |
| 气泡通知的观感与时长 | 需要人眼看 |
| 图标在不同 DPI 下的清晰度 | 同上 |

**但"图标真的显示了"是可以自动断言的** —— 查 `HKCU\Control Panel\NotifyIconSettings`
里有没有本 exe 的记录（explorer 会为每个出现过的托盘图标建一条）。这比
"构造 `NotifyIcon` 没抛异常"硬得多：那只能证明代码跑过，证明不了图标看得见。

---

## 18. P1b 设置 UI 落地记录（2026-09-19 完成）

**UI 栈（§2.1 已拍板）**：WPF（与托盘同进程）+ **iNKORE.UI.WPF.Modern 0.10.2.1**。

### 18.1 交付物

| 位置 | 内容 |
|---|---|
| `src/Eztools.Desktop/SettingsWindow.cs` | 设置窗口（**零工具专属代码**）：左侧工具列表（有 config schema 的），右侧按 §7 映射表渲染字段 + 保存 / 恢复默认 |
| `Eztools.Desktop.csproj` | `UseWPF` 与 `UseWindowsForms` 并存 + 全局 using 管理（见 18.4 ①） |
| `TrayApplication.cs` | 托盘「设置…」入口（`ShowDialog` 模态泵跑 WPF，两栈互操作最稳路径）；`--selfcheck` 附带**设置清单**输出 |
| `scripts/verify-desktop.py` | 新增 5 项断言：设置清单覆盖 3 个工具、7 个字段、控件类型逐一对上 |

### 18.2 映射表实测（自检输出原样）

```
设置清单 echo:      uppercase=CheckBox
设置清单 wordcount: countWhitespace=CheckBox, language=ComboBox, maxFileSizeMb=TextBox
设置清单 filehash:  algorithm=ComboBox, uppercase=CheckBox, chunkSizeKb=TextBox
```

7 个字段全对：`boolean→CheckBox` · `string+enum→ComboBox` · `integer→TextBox`（带范围提示）。
secret（`x-secret`）→ `PasswordBox`（不回显已存值，留空 = 未改动）。

### 18.3 数据流（全部复用 P1a，窗口不自己写文件）

显示 = `ConfigStore.Load()`（快照含 Effective / OrphanKeys / 损坏恢复标记）
保存 = 逐字段 `ConfigStore.Set()`（校验失败不落盘并提示）· 空值 = `Unset()`（回落默认）
重置 = `ConfigStore.Reset()` → 保存后 `ToolHostManager.PushConfigAsync(toolId)`
（当前全是 transient：推送是通路验证，值在下次调用天然生效）

### 18.4 踩坑（4 个，都有实测证据）

1. **再次踩到「Edit 报成功但文件没变」**：`SettingsWindow.cs` 的三处修正用 Edit 工具报了 success，
   下一轮构建错误原样 —— 文件实际未变。**改用 Python 补丁 + grep 回验**（此坑已是本项目第二次记录，
   是"关键批量修改必须回验"这条规则的直接证据）。
2. **SDK 10 的基座隐式 using 不含 `System.IO`**（生成的 GlobalUsings.g.cs 只有 5 条）——
   凭 .NET 7 时代的印象假设它在全局里，结果 `File`/`Path`/`Directory` 全部 CS0103。
   **教训同 P0：隐式集合要看生成产物，不凭记忆。**
3. **iNKORE 包是两个程序集**：`XamlControlsResources` 在 `iNKORE.UI.WPF.Modern.Controls` 子命名空间
   （主程序集 xml 文档在 net452/net6 两个 lib 下位置还不同——查文档要查**实际选中的 TFM** 那份）。
   `ThemeManager.ApplicationTheme` 文档标成静态（`P:`），实际是**实例**属性（`ThemeManager.Current`），
   编译器 CS0120 纠正了文档。
4. **UIElementCollection 是非泛型集合**：`foreach` 出 object，直接传给 `UIElement` 参数报 CS1503。

### 18.5 验证边界

- **可自动断言**：设置清单（工具覆盖 / 字段数 / 控件类型）已进 `verify-desktop.py`（验收 60 → **65 项**）；
  保存→文件→`ezt config get` 的往返由第 8 步的配置中心断言覆盖（同一 ConfigStore）。
- **只能手工验**（3 项）：窗口观感（Fluent 主题是否生效）、从托盘点「设置…」的交互、
  编辑保存后气泡/提示文案。—— **已由菲比于 2026-09-19 晚手工验证通过**（并额外抓到
  §18.7 的下拉卡死与可读性两个问题，均已修复）。清单见 §17.7 的同款逻辑 —— 交互式 UI 自动化到"程序化触发"为止。

### 18.6 P2 剩余

热键仲裁 · 独占资源冲突仲裁（都不依赖 UI，可随时开工）。

---

### 18.7 补丁：ComboBox 下拉卡死（她手工验证 ⑤ 抓到，2026-09-19 晚修复）

**现象**：设置窗口里点开下拉框（wordcount.language 等）→ 整个窗口卡死。

**定位过程（探针三轮，每轮缩小一圈）**：
1. 复现探针：同构窗口 + 程序化展开下拉，**两种消息循环（WinForms 泵 / WPF 泵）都正常**
   → 排除"两栈互操作"这个第一嫌疑；
2. 挂上 iNKORE 主题（与真实窗口同款）→ **两种泵都崩**（0xE0434352）→ 嫌疑转向库本身；
3. `DispatcherUnhandledException` 抓堆栈：
   `iNKORE.UI.WPF.Modern.Controls.Helpers.ComboBoxHelper.UpdateCornerRadius` 里
   `TryFindResource("OverlayCornerRadius")` 返回 null → **拆箱 null 直接 NRE** ——
   它自己的 ThemeResources 字典里没有这个它模板要用的键（0.10.2.1）。

**修复（两处，探针验证连续通过）**：
- `SettingsWindow.ApplyModernTheme`：主题资源**同时挂 App 级**（官方文档的标准挂法）+
  预播缺失的 `OverlayCornerRadius`（仅缺失时补默认值）；
- `TrayApplication`：WPF 异常兜底 —— 只拦截 **iNKORE 命名空间内的 NRE**（记日志后放行，
  该异常只是圆角没更新，无害），其余异常照旧崩溃。

**两条教训**：
- **"卡死"未必是死锁** —— 这次实际是库内异常 + 无兜底，表象却像挂住。UI 库的崩溃要抓堆栈，
  不能靠猜。
- **heredoc 补丁会执行两次**（本会话第二次实锤）：第一遍应用成功、第二遍断言失败报错 ——
  看到断言失败别急着重打补丁，先 grep 确认文件实际状态。

**回归**：构建 0 错 0 警 · selftest 22/22 · acceptance 65/65 · budget 5/0/1。
探针产物已删，结论留档本节。

---

## 19. P2 收官：热键仲裁 + 独占资源仲裁（2026-09-19 完成）

P2 的最后两件，做完后 Eztools 的"治理"层齐了：**统一托盘入口 + 统一设置 + 统一仲裁**。

### 19.1 交付物

| 位置 | 内容 |
|---|---|
| `Contracts/HotkeyCombo.cs` | 组合键规范化：修饰键大小写/顺序不敏感（`alt+ctrl+x` ≡ `Ctrl+Alt+X`）、主键校验、资源 id（`hotkey.<规范化串>`） |
| `Contracts` 诊断码 | `exclusive-invalid-id` / `exclusive-duplicate`（清单级校验）；`registry.hotkey-conflict` 等两个注册表级码 P0 已预留，本次启用 |
| `Host/Arbitration/HotkeyArbitration.cs` | 仲裁纯逻辑：**保留先注册者**（工具 Id 序 = 注册序，确定性）；同一条规则服务 CLI 展示、Desktop 注册决策、验收断言 |
| `Host/Arbitration/ExclusiveResourceManager.cs` | 独占资源运行时注册表：claim/release/query；热键注册成功即 claim，resident 工具（将来）启动时 claim 其声明 |
| `Desktop/HotkeyHook.cs` | 不可见消息窗口 + `RegisterHotKey` + `WM_HOTKEY` 分发；三种落败（内部仲裁落选 / 被第三方占用 / 解析失败）提示各不相同 |
| `Desktop/TrayApplication` | 启动注册 + 「刷新菜单」重注册（`ReRegisterHotkeys`）+ 退出注销；热键触发与菜单点击共用 `InvokeAsync`（剪贴板注入、气泡、防重入全继承） |
| `Cli/HotkeysCommand.cs` | `ezt hotkeys`（列表 + 归属，--json 可读）· `ezt hotkey set/unset <命令id>`（写 state.json 的 hotkeyOverrides，改键免重启托盘） |
| `ToolRegistry` 检测升级 | 热键冲突从朴素字符串比对升级为 HotkeyCombo 规范化比对 |

### 19.2 独占资源模型的统一

**热键本身就是独占资源**——注册成功的 `hotkey.*` 在资源管理器里 claim，与 §8.3 的
`desktop.windowManager` 等声明资源走同一个注册表。将来 resident 工具启动时 claim 其
`exclusiveResources`，冲突的 API 语义完全一致。

**能力边界再念一遍（§8.3 的注）**：仲裁只管 Eztools 自己的工具；第三方程序占用
`RegisterHotKey` 失败时只能提示"被其他程序占用"（Windows 不告知占用方是谁）。

### 19.3 实测

| 项 | 实测 |
|---|---|
| 真实注册 | `--selfcheck` 报"热键注册 2 个"；日志两条 `RegisterHotKey` 成功（Ctrl+Alt+H / Ctrl+Alt+W） |
| 规范化仲裁 | 临时工具用 `Ctrl+Alt+X` 与 `alt+ctrl+x` 两种写法 → doctor 识别为同一键冲突 |
| 归属确定性 | 同键冲突赢家 = 工具 Id 序在前者（hk-a），落选者明确提示"让给 hk-a" |
| 改键 | `ezt hotkey set` 立即重仲裁；刷新菜单后生效；unset 幂等回默认 |
| 验收 | **acceptance 71 项全绿**（新增第 11 步 5 项）· selftest 22/22 · budget 5/0/1 |

### 19.4 踩坑（验收脚本侧）

1. **`python file.json` 会把 json 当脚本执行** —— 文件里的 `"false"` 变成裸标识符，
   报 `NameError: name 'false' is not defined`，极具迷惑性。给 `sys.argv[1]` 传文件路径
   必须 `python - file.json <<'EOF'`（`-` 占位 stdin 脚本）。
2. **`$PY -c "python 代码"` 的引号炸弹** —— python f-string 的双引号终止 bash 引号串，
   把代码炸成 shell 语法错误。内嵌 python 一律用**单引号 heredoc**（`$PY <<'EOF'`）。
3. `--tools-dir` 是**追加语义**：验收断言要按工具过滤，不能数全局。

### 19.5 P2 至此全部完成

托盘 ✅ · 热键仲裁 ✅ · 独占资源仲裁 ✅。剩余：P3 特权层 · P4 协作与打包。

---

## 20. P3 特权层落地记录（2026-09-20 完成）

### 20.1 交付物

| 位置 | 内容 |
|---|---|
| `src/Eztools.Core/` | 特权服务 `ezt-core.exe`（net10.0-windows，独立进程）：`CoreServer`（命名管道 JSON-RPC，NewLineDelimited 与 stdio 同约定）· `CoreSecurity`（对端 PID→令牌→SID+完整性校验）· `AuditLog`（`logs\audit-*.log` 每调用一行 JSON）· `Primitives/*`（含 Win32 互操作） |
| `Contracts/Primitives.cs` | 原语名 · `core.*` 方法 · 扩展错误码（-32013 ~ -32018）· `CoreEndpoint`（core.json 读写，两侧共用一份模型） |
| `Host/Primitives/PrimitiveClient.cs` | 管道客户端（每调用一连接）+ ezt-core.exe 定位（env → exe 旁 → 仓库回退 → 安装根 bin） |
| `Host/LayoutInstaller.cs` | 安装器新增 4b 步骤：把 Core 构建输出铺进安装根 bin |
| `Cli/CoreCommand.cs` / `PrimitiveCommand.cs` | `ezt core status/start/stop/ping/install-task/uninstall-task` · `ezt primitive <name>`（调试直调） |
| `sdk/python/eztools/tool.py` | `Tool.primitive(name, args)` |
| `tools/pinfo/` | 端到端示例：声明 `process.enumerate` → SDK → Core → 返回进程列表 |
| `scripts/_step12_p3.sh` | 验收第 12 步 13 项 |

**验收**：acceptance **85 项全绿**（新增第 12 步 13 项）· budget 6/0/1（宿主目录 537KB/1024KB 上限）。

### 20.2 关键落地决策（与方案的偏差都写明了理由）

| 决策 | 理由 |
|---|---|
| 闸 1 用 `PipeOptions.CurrentUserOnly` 而非手写 PipeSecurity | 自绘 ACL 在跨父进程场景下 CreateNamedPipe 间歇性 Access Denied（实测）；.NET 内建内核级同用户校验，一条顶一套 |
| 身份校验用 `GetNamedPipeClientProcessId → OpenProcessToken`，**不用模拟**（ImpersonateNamedPipeClient） | 模拟依赖 SeImpersonate 特权，普通用户进程上不可靠（实测失败）；对端 PID 反而因此从"自报"升级为"内核验证"，terminate 守卫与审计都受益 |
| `process.terminate` 不要求提权 | 杀同用户进程本不需要管理员（File Locksmith 同语义）；防线 = 关键进程保护名单（csrss/wininit/winlogon/smss/services/lsass + Core 自身 + 宿主）+ 审计。`volume.readMft` 仍需提权（负向可测：-32015） |
| 提权形态选计划任务（§9.4 三选一） | 免 UAC + 自启 + 用户会话全占；Windows 服务形态推迟（Session 0 复杂度暂无对抗场景） |

### 20.3 踩坑（全部实测踩到）

1. **🔴 Windows 11 24H2（build 26100）改了句柄表条目布局**：`SystemExtendedHandleInformation` 的条目里 ProcessId/Handle 从 8 字节缩成 4 字节（+16/+24，各带 4 字节保留），Object 之后多 8 字节保留区。老布局读新表 = **所有条目 pid 恒为 0 → 枚举永远返回空**，无任何报错。这是 PowerToys FileLocksmith 移植时最深的坑。已按 `OSVersion.Build >= 26100` 双布局自适应；定位手段 = 用 python ctypes 对句柄表逐偏移做非零分布扫描。
2. **父进程退出会杀死重定向了 stdio 的子进程**：Core 的 `Console.WriteLine` 在父进程（ezt core start）退出后因管道断裂抛 IOException 直接崩。所有控制台输出必须容错（`CoreConsole` 吞 IO 异常）。
3. **陈旧 core.json + ConnectAsync = 30 秒假死**：Core 被杀后端点文件残留，`NamedPipeClientStream.ConnectAsync` 会把超时轮询满。连前先查登记 PID 是否存活，死了立即报错并清理端点。
4. **`JsonNode already has a parent` 又踩一次**（§14 坑 5）：Core 响应解析出的 result 节点直接回给工具帧 → 调用挂死。跨帧传递一律 `DeepClone()`。
5. **`test -f` 对混合分隔符路径会误判**：`C:\a\bin/ezt-core.exe`（反斜杠+正斜杠混排）可能判否。给 test 前先 `${path//\\//}`。
6. **MSYS `/tmp` 会解析成 `D:\tmp`**（cwd 在 D 盘时）—— 再次印证"给原生程序传路径必须 Windows 形式"。
7. `dotnet build` 增量构建可能**不把依赖项目的最新 DLL 拷到 exe 输出目录**（Cli bin 里的 Host.dll 停留在旧版，症状 = "改了代码但行为没变"）。排查手段：对 DLL 内 UTF-16 字符串做 `in` 检查；修复用 `--no-incremental`。
8. **🔴 USN 是有符号 LONGLONG——`HighUsn = ulong.MaxValue` = -1（2026-09-20 三轮排查终审）**：readMft 恒空（`count=0/done=true/cursor=0`，不报错）的真凶**不是控制码**——`FSCTL_ENUM_USN_DATA = CTL_CODE(9, 44, METHOD_NEITHER, ANY) = 0x0009_00B3` 从第一天就是对的（MSYS2 winioctl.h L1504 与 MS Learn 一致，METHOD 就是 NEITHER，勿改 BUFFERED）。排查中两次凭记忆"修码"（179/BUFFERED、44/BUFFERED）全错，实测 err=1；python 提权矩阵实验（她跑 UAC 脚本）终审：`HighUsn=LLONG_MAX` 单次返回 65400 字节真实记录、`HighUsn=-1` 复现 EOF(38)、20B/V1 结构 err=87、BUFFERED 变体 err=1——全部变量一一对账。**教训：Win32 带符号类型（USN/LARGE_INTEGER/HRESULT）填"最大值"必须用 `long.MaxValue` 系，`ulong.MaxValue` 会变成 -1**；以及"凭一个记忆去修另一个记忆"不如写 15 行脚本穷举实测。

### 20.6 手测补录（2026-09-20，她跑 M1~M3 抓到第一个真 bug）

- **发现**：提权 Core 下 `volume.readMft` 返回 0 条记录（见 §20.3 坑 8）——自动化 13 项全是非提权环境，readMft 正向数据路径**从未被真实执行过**，手测第一次跑就命中。这验证了"自动化测不到的 4 项必须手测"的分层不是摆设。
- **顺带修复**（读代码发现，同属游标语义）：旧实现在 `maxRecords` 截断时把游标推进到**整块 64KB 缓冲区的边界**（一次 ~700 条），两次调用之间会**静默跳过中间几百条记录**。修为"游标 = 上一批最后一条已返回记录的 FRN + 下一批重放消化首条"（§5 语义），另加游标零推进防死循环守卫、USN_RECORD_V2 头部 60 字节边界（原 64 会漏缓冲区末尾差 2~4 字节的记录）。
- **第二轮（她重跑 M3 抓到误修）**：第一轮把控制码改成 0x0009_02CC（凭错误记忆认定功能码=179），她重跑报 `Win32 错误 1`——比静默空好（至少显式失败），也坐实 179 不是真身。第二轮又凭记忆改"44/BUFFERED=0x900B0"，她再跑仍 err=1——**两次凭记忆修码全部失败**。
- **第三轮（终审，python 提权矩阵实验一锤定音）**：写 15 行 ctypes 脚本（她跑一次 UAC），在提权 GENERIC_READ 句柄上穷举 码×结构×HighUsn：**`0x900B3 + 24B + HighUsn=LLONG_MAX` → ok、returned=65400、游标=680**；对照 HighUsn=-1 复现 EOF(38)；20B/V1 → err=87；0x902CC/0x900B0 → err=1。根因 = `HighUsn=ulong.MaxValue` 在有符号 USN 语义下是 **-1**。修复 = 一行：`(ulong)long.MaxValue`；控制码回到 0x0009_00B3。顺带：脚本 recs 变量误初始化为 0（int）导致的 traceback 也一并修掉。
- **回归**：三轮修复后 `acceptance.sh` 均 85/85 全绿。另验日常循环：Core 运行中构建被锁（MSB3027）→ `ezt core stop` → build → 重启。
- **P3 手测收官（2026-09-20 晚，她全程亲手跑）**：M1~M8 全部完成，过程中抓到 readMft 两个真 bug（HighUsn 符号位、解析起点）均已修复+85 项回归全绿。M4 终审 = Core 自查 6 条真实句柄；全量超时与 pid:4 空均为预期（§20.4）。**手测对自动化的增益实打实**：4 项管理员手测里 2 项（readMft 正向、M4 判据）在自动化里是盲区。
- **三项待办当天落地（2026-09-20 20:5x，未拖到 P4）**：① `ezt primitive` 加 `--timeout <秒>`（默认 30、clamp 5~600），超时映射退出码 2 + `-32011` 结构化错误（原先是未捕获 OCE → 退出码 3，小瑕疵顺手修掉）——全量句柄扫描现在可跑（`--timeout 300`），实测 5 秒档精确生效；② `_step12_p3.sh` 12.6 断言从"≥0 结构合法"升级为 **Core 自查非空**（`count≥1` + 逐条 pid/type/path 校验）——不需要提权（Core 开自己），弱断言放过的布局错误从此在自动化里现形；③ M7 对抗测试是跑一条命令的事（手册 M7 现成），非实现项，留给她随手演练。

### 20.4 已知限制

- ~~句柄全量扫描（无 pid 过滤）约需 1~2 分钟（10 万+ 条目逐个 OpenProcess/DuplicateHandle）……后续可按 pid 分桶并行。~~
  **→ 已修复（2026-09-21，清单 C2）**：改为**按下标区间并行**（`HandlePrimitives.Enumerate`），实测全表扫描 **8306 ms**（约 14× 加速，本机 12 核 → 8 worker）。*详见 §20.7 的"并行方式选择"记录——原计划的"按 pid 分桶"被否决，原因见该节。*
- **`pid:4`（System）恒空是安全模型内的预期**：枚举 System 句柄需要 `SeDebugPrivilege`，Core 刻意不启用该特权（最小权限）。手测终审（2026-09-20）：链路正确性以 **Core 自查**（`pid=Core 自己`）验证——返回 6 条真实 File 句柄（dotnet-diagnostic/ezt.core 管道等），全链路正常。
- `NtQueryObject` 挂起放弃策略会遗留后台线程（PowerToys 同款取舍，进程退出回收）。
- `process.terminate` 保护名单是静态最小集；审计日志是兜底。
- **Windows 服务形态未做**（`ezt core install-task` 的计划任务形态已满足需求）。
  **翻案条件（2026-09-21 写死，清单 B6 / 决策 D8）**：仅当出现「需要**开机时尚未登录**就跑、
  且**不需要碰用户桌面**」的任务时才重开此案。理由：服务跑在 Session 0，与用户桌面隔离，
  要做跨会话交互得再写一套 IPC——成本高、收益零。**在此之前保持不做。**

### 20.5 P3 至此全部完成

剩余：**P4** —— 宿主事件总线 / `host.invokeTool` · `panels` 贡献点 · `weight: full`/`script` 补齐 · 安装器与更新器 · 打包形态决策。

> **✅ 上述 P4 五项已于 2026-09-21 全部收官**（Wave 1 / 2a / 2b / 2c，见 §22~§25）。

### 20.7 句柄全表扫描并行化 —— 为什么最终没按"pid 分桶"做（2026-09-21，清单 C2）

**结论先说**：清单 §3-C2 原文推荐「按 **pid 分桶**并行」，实际落地改为「按 **下标区间**并行」。
这不是偏好问题——**"按 pid 分桶"与既有的 `NtQueryObject` 挂起对策在机制上不能共存**，实测两次挂满超时才定位到。

**背景约束（`C3` 那条，别动）**：`NtQueryObject` 对命名管道/等待中的句柄会**永久挂起**，且无带超时的替代 API。
它挂住的是**执行调用的那个线程**，该线程**无法自救**——唯一出路是**另一个线程**观察到它的游标停滞，
用 `SkipOne()` 把游标推过挂起点，再**开一个新线程**从新位置继续。原单线程实现就是这么做的（见 `HandlePrimitives` 里的看门狗注释）。

**两次失败（都是实测，不是推演）**：

| 版本 | 做法 | 结果 | 根因 |
|---|---|---|---|
| v1 | pid 分桶 + 每桶 worker + 主线程 `thread.Join(30s)` | `rc=2 elapsed_ms=600294`（挂满 `--timeout 600`） | **`Join(timeout)` 不能替代看门狗**：主线程睡在 Join 上时，没有任何线程能通知被挂住的 worker"跳过"。8 个 worker 各自被挂住 → 累计等满 30s×N |
| v2 | pid 分桶 + `AbandonCurrent` 标志 + 主线程看门狗 | 仍 600s | ① `Tick()` 同时写 `_progress` 与 `_lastSeen`，使 `Stalled()` 判据永远为假；② **"整桶放弃"会丢掉该进程的全部句柄**——比原实现更差（损失面从 1 个句柄放大到一个进程的所有句柄） |

**最终方案（v3）**：**放弃 pid 分桶，改按下标区间切分**——把原单线程算法**逐行复制**到 N 个互不重叠的
`[start, end)` 下标区间上，每个区间配一个**监督线程**（`StartRangeSupervisor`）：

```
Enumerate:  workerCount = Clamp(ProcessorCount, 2, 8)      // 本机 12 核 → 8
            segment = (count + workerCount - 1) / workerCount
            对每个区间: 起 supervisor 线程 → supervisor.Join()

Supervisor: while (state.Cursor < end) {
              Sleep(WatchdogIntervalMs);              // 200ms 轮询该区间是否在动
              if (!state.MadeProgress()) { state.SkipOne(); worker = StartRangeWorker(...); }
            }

Worker:     与原单线程版逐行等价（processHandles 字典缓存 OpenProcess、
            DuplicateHandle 失败即 continue、InspectHandle 后 CloseHandle）
```

**为什么这样是对的**：① 挂起影响的粒度仍是 **1 个句柄**（与原实现完全一致），只是"跳过"改由区间自己的监督线程发起；
② 区间之间无共享游标，不需要额外同步；③ `results` 是唯一的共享写入点，用 `lock (results)` 保护（`InspectHandle` 内）。
**环环相扣的一点**：worker 是可重启的——监督线程发现停滞就 `SkipOne()` + 起新 worker，**不复用被挂住的线程**（那线程永远回不来）。

**实测**：`rc=0 elapsed_ms=8306`（约 **14×**）；结果正确性对账 `count=488 / returned=488 / distinct pids=47 / malformed=0`；
pid 过滤路径（走不同分支）336 ms 正常；`_step12_p3.sh` 12.6（Core 自查结构断言）与完整验收 **104 项全绿**。

**教训（可迁移）**：**"让 worker 超时退出"与"看门狗换线程接续"是两种互斥的挂起重对策**——
前者要求线程可被外力终结（`NtQueryObject` 做不到），后者要求有**独立线程**持续观察。
凡是要把"逐项处理 + 挂起跳过"的算法并行化，**必须把看门狗一起复制进每个分片**，不能收归主线程。

---

## 附录 A：同类项目调研数据

数据来源：GitHub API 实测（2026-09-17）。

| 项目 | Stars | 宿主栈 | 扩展机制 | 进程隔离 | 多语言 |
|---|---|---|---|---|---|
| PowerToys | ~125k | C++（UI: C#） | 模块接口 DLL，进程内，硬编码清单 | ❌ | ❌ |
| **DevToys** | **32.0k** | **C# (.NET 8)** | **MEF**，进程内程序集，NuGet 分发 | ❌ | ❌ 仅 C# |
| **Wox** | **27.4k** | **Go** | `plugin.json` + 语言宿主进程（Py/Node），JSON-RPC | ✅ | ✅ |
| **Flow Launcher** | **15.6k** | **C#** | `plugin.json`（含 `language`）+ **JSON-RPC over stdio** | ✅（1.18 起） | ✅ Py/Node/exe |
| ueli | 4.6k | TypeScript | JS 扩展，进程内（Electron） | ❌ | ❌ |
| Tauri 系工具箱 | 45 / 2 / 1 / 1 | TS / Rust | — | — | — |

- devtoys-app/DevToys · flow-launcher/Flow.Launcher · Wox-launcher/Wox · microsoft/PowerToys

**关键借鉴**：
- **DevToys**：MEF + NuGet 分发的进程内扩展模型（说明"进程内"路线的完整形态）
- **Flow Launcher**：多语言插件的完整工程实践——`plugin.json` 的 `language` 字段、JSON-RPC over stdio 的三种分帧（Python 默认 NewLineDelimited）、`python -c` 注入预置代码、**嵌入式隔离 Python**、依赖 vendor 到 `Lib/`、`dotnet new` 项目模板、GitHub Actions 发布模板、1.18 因"插件崩溃影响主程序稳定性"而重构为进程隔离
- **Wox**：语言宿主进程模型、按重量分级的插件体系（script / 单文件 SDK / full）、公开 SDK
- **PowerToys**：`PowertoyModuleIface` 的统一接口设计值得抄；其余多为教训

---

## 附录 B：决策演进（已否决的方案与原因）

保留此节以防将来重新引入已被否决的设计。

| # | 曾提出 | 否决原因 |
|---|---|---|
| 1 | **能力网关 + 运行时逐调用仲裁** | 试图在用户态自造安全内核，必须为原生 API 开 `native.api` 后门。**需要开洞的权限模型是伪安全**。核实发现：成熟项目**没有任何一家**实现逐调用仲裁——VS Code 官方文档原文是"扩展主机拥有与 VS Code 本身相同的权限"，它靠进程隔离（为稳定性）+ 发布者信任 + 市场审核 + 企业 allow-list |
| 2 | **双轨能力模型（Track A 受控 / Track B 原生）** | 同上：轨道 B 就是那个后门 |
| 3 | **S0–S3 进程权限档位** | 前提变为"工具需管理员权限"后不再适用，改为四层架构 + 特权原语 |
| 4 | **插件 / 伴生应用（Plugin / Companion）二分** | 随"自用工具集合"定位一并砍掉——工具都是自己写的，不需要按来源分类 |
| 5 | **多源 Feed / 商店 / 包格式 / 签名 / 信任状态机 / S0 沙箱** | 面向第三方生态，对自用工具集合价值为零 |
| 6 | **Electron + TypeScript 作宿主** | 从个人历史项目外推技术选型，方法错误。实测：常驻系统工具集宿主全为原生栈，Electron 无成熟先例 |
| 7 | **`apiVersion` 兼容矩阵** | 版本由自己控制，无需协商 |

**对 PowerToys 评价的自我修正**：曾把"36 个模块加载进管理员进程"记为纯失误，需修正——**它提权的决定是对的**（模块确实需要特权操作）。真正的失误是 ① **特权面没收窄**（连完全不需要特权的模块如 ColorPicker、MouseHighlighter 也被塞进同一管理员进程）② **允许第三方代码进特权层**（DLL 无签名校验、裸 `LoadLibrary`）。即：**不是"不该提权"，而是"提权面太大 + 没有信任边界"**。

---

## 附录 C：PowerToys 源码取证索引

依据本机 `D:\01-项目代码\Eztools\PowerToys`（checkout HEAD 对应上游 main）。

| 用途 | 路径 |
|---|---|
| 模块清单（硬编码，待修正项） | `src/runner/main.cpp:256-293` |
| 加载循环 / 启动顺序 | `src/runner/main.cpp:295-320`、`:322` |
| 模块加载实现（无签名校验） | `src/runner/powertoy_module.cpp:14-30` |
| 加载失败弹窗（Release） | `src/runner/main.cpp:314` |
| 模块 ABI 定义 | `src/modules/interface/powertoy_module_interface.h:181,191` |
| 开关与默认值 | `src/runner/general_settings.cpp:96-101` |
| 三处平行硬编码 UI 清单 | `Settings.UI/OOBE/Enums/PowerToysModules.cs`、`Settings.UI.Library/Helpers/ModuleHelper.cs`、`ModuleIconResolver.cs` |
| 安装包单一 Feature | `installer/PowerToysSetupVNext/Product.wxs:42-74` |
| 更新机制（整体更新） | `src/common/updating/updating.cpp:18`、`src/update/PowerToys.Update.cpp:101-183,271-296` |
| 签名校验基础设施（可复用） | `src/common/updating/installer.cpp:281-332` |
| 扩展商店管线（已砍，仅作参考） | `src/modules/cmdpal/Microsoft.CmdPal.Common/ExtensionGallery/Services/ExtensionGalleryService.cs` |

**PowerToys 各模块的提权点**（说明"提权面收窄"的可行性）：

| 模块 | 提权位置 |
|---|---|
| Hosts | `HostsModuleInterface/dllmain.cpp:83-99`（`runas`） |
| EnvironmentVariables | `EnvironmentVariablesModuleInterface/dllmain.cpp:89-105`（`runas`） |
| KeyboardManager | `KeyboardEventHandlers.cpp:1650`（`run_elevated`） |
| FileLocksmith | `NativeMethods.cpp:126`（`runas`） |
| MouseWithoutBorders | `ModuleInterface/dllmain.cpp:636`（`runas`） |
| LightSwitch | `LightSwitch.wxs:10-33`（安装服务） |
| 其余多数 | 仅 `is_process_elevated()` 判断以跳过提权窗口 |

---

## 附录 D：平台事实与已知坑位

### Windows 平台事实（已核实）

- **Windows 11 现代资源管理器右键菜单必须通过 MSIX sparse package 注册**，单纯写 `HKCR` 已失效
- sparse package 的 COM 注册落在 `HKCU\Software\Classes\CLSID` → **per-user，无需管理员权限**
- 安装/卸载用 `Add-AppxPackage -ExternalLocation` / `Remove-AppxPackage`，干净可逆
- 前提是 **CA 可信签名**（自签仅本机有效）

### 工具开发环境的坑位

- **Bash 工具需先补 PATH**：`export PATH="/usr/bin:/bin:/c/Windows/System32:$PATH"`，否则 `ls`/`head`/`dirname` 全部 command not found（PortableGit 无 coreutils）
- 读写文件优先用 Read / Write / Edit / Glob / Grep 工具，可完全绕开 Bash
- WorkBuddy 沙箱会静默拦截对真实磁盘（D 盘、`%LOCALAPPDATA%`）的写/删：**exit 0 但文件毫发未动**，
  必要时 Bash 调用加 `dangerouslyDisableSandbox: true`
- **Git Bash 的 `pwd` 给 POSIX 路径，不要直接交给 Windows 可执行文件**：`/d/01-...` 会被 .NET/原生程序
  解析成"当前盘符根下的相对路径"，实际写到 `D://d//01-...`。脚本里一律用 `pwd -W`
  （注意 `tar -f` 例外：它会把 `D:/x` 当 rsh 的 `host:path`，所以归档名要用裸文件名 + subshell `cd`）
- 脚本里不要用 `cmd \| grep -q`：`grep -q` 命中即退出会让上游收到 SIGPIPE，在 `set -o pipefail` 下
  整条管道被判为失败，判定结果与事实相反。同理 `A && B \|\| C && D` 中 `D` 在 `B` 成功时也会执行
- **pip 装包必须走国内镜像**：`-i https://pypi.tuna.tsinghua.edu.cn/simple --timeout 60`，否则走默认源会卡死

---

## 21. P2 尾巴落地记录（2026-09-20 完成）

### 21.1 交付物

| 位置 | 内容 |
|---|---|
| `src/Eztools.Host/Processes/ToolJobObject.cs` | kill-on-close Job：transient/task 工具挂入（宿主崩溃→内核收割，无孤儿）；**resident 分流不挂**（宿主崩溃/强杀时常驻必须存活） |
| `src/Eztools.Cli/DiagCommand.cs` | `ezt diag [--out <zip>]`：打包 manifest（版本/OS/工具清单）+ logs/ + config/；**默认不脱敏但打印敏感内容清单，`--redact` 全量脱敏（2026-09-21 补，见 §21.5）** |
| `ToolHostManager.RunStartupLifecycleAsync` | 宿主启动钩子：task 工具执行 commands[0] 一次（决策 1 简单版，schedule 留 P4）+ resident 拉起保活 |
| `ToolHostManager.PushRecoverAsync` | `tool.recover` 宿主发送侧（决策 3 的托盘入口调它） |
| `ToolHostManager.HandleCrashAsync` | resident 崩溃自动重启（决策 2，异步防死锁；熔断预算 resident=5 分钟 2 次兜底防风暴） |
| `ReleaseAsync` | resident 跳过即用即走回收（进程保活，同宿主跨调用复用） |
| `tools/keepalive`、`tools/tasktool` | 验收工具：ping 返回 pid+计数、crash 故意崩溃、startup 写宿主日志凭证 |
| `SelfTestCommand.RunLifecycleCasesAsync` | 6 项断言：启动钩子 / 同 pid 复用 / 计数递增 / 崩溃检测 / 自动重启（新 pid）/ task 日志凭证 |

### 21.2 关键决策

1. **resident 的常驻载体 = 常驻宿主（托盘）**：CLI invoke 是一次性宿主，跨调用复用只在常驻宿主内成立；CLI 场景 resident 行为等同 transient（调用完回收由宿主 Dispose 兜底）。
2. **task 语义简化**：宿主启动时执行 commands[0] 一次；schedule 调度留 P4。
3. **优雅退出 vs 崩溃**：宿主优雅退出回收全部（含 resident）；崩溃/强杀时 Job 分流——transient 死、resident 活。崩溃场景的 resident 孤儿由下次宿主启动重起新实例接管（旧实例保留，第一版已知限制）。

### 21.3 验收

- `ezt selftest` **28/28**（原 22 + 生命周期 6 项：同 pid 复用、计数递增、崩溃检测、自动重启新 pid、task 日志凭证、启动钩子）；
- `acceptance.sh` **85/85**（新工具补空 config schema 与既有口径一致）；
- 进程回收断言语义修正：`pythonAfter ≤ pythonBefore + resident 常驻数`（resident 在宿主存活期就该活着）。

### 21.4 遗留 / 注意

- ~~托盘菜单的 resident 状态项与 recover 入口~~ **✅ 已做（2026-09-20 晚）**：`ToolHostManager.GetResidentSnapshot` 只读快照 + 托盘常驻服务菜单区段（状态项 + recover 入口绑 `PushRecoverAsync`，气泡回执）；selftest 增快照断言（**29/29**）；
- ~~崩溃场景 resident 孤儿进程第一版不回收~~ **经查不需要**：SDK 主循环在 stdin EOF（宿主死亡 → 管道断开）时 break 退出（`tool.py` 205 行），resident 孤儿只会存活 handler 执行中的极短窗口，天然自愈；
- **顺手修**：Desktop `--selfcheck` 短路退出不回收宿主的泄漏（resident 进程存活并持有继承句柄，曾把调用方管道拖到不关闭）——SelfCheck/AutoClick 分支补 `Dispose()`；另注意参数名是 `--selfcheck`（无连字符），传 `--self-check` 会被静默忽略进消息循环。
- `schedule` 调度、Windows 服务形态：P4/推迟（同 P3 决策）
  —— **`schedule` 路径已定（2026-09-21，清单 A5 / 决策 D4）：走 Windows 计划任务，宿主侧 0 代码。**
  P3 已把这条机制走通并验证（`ezt core install-task` → `schtasks /Create /SC ONLOGON /RL HIGHEST`，含免 UAC 提权）。
  将来需要时照同一形态加一条 `ezt task install <toolId> --schedule ...` 即可，**白拿"开机前运行""无人登录也运行"**；
  不写宿主内置调度器（后者只在宿主进程活着时有效，语义更弱却要写更多代码）。
  **前置条件**：需先定义粒度（间隔 / 日历 / 错过补偿 / 并发策略——这四个问题不回答，写出来的调度器一定是错的那一个）；
  **Windows 服务形态**保持不做，翻案条件见 §20.4。

### 21.5 `ezt diag` 脱敏（2026-09-21 补，清单 C4）

**改动前的问题**：手册 §9 明写「打包 manifest + logs/ + config/，**未脱敏**」——
而诊断包**天生是要发给别人看的**，未脱敏等于埋了个"哪天手一滑就漏内部路径"的雷。

**改动（`src/Eztools.Cli/DiagCommand.cs` + `Program.cs` 帮助文本）**：

| 路径 | 行为 |
|---|---|
| `ezt diag`（默认） | **不脱敏，但先把"将包含哪些敏感内容"打印出来**（当前用户名 / 机器名 / 安装根 / 配置根 / 工具私有数据目录）—— 让打包的人自己看见 |
| `ezt diag --redact` | 上述五类**全量替换为占位符**：`<user>` / `<machine>` / `<installRoot>` / `<configRoot>` / `<toolsdata>`，另加 `<home>` / `<tmp>` |

**三条设计约束**（都是为了"包还能用来排查"）：
1. **只处理「身份 + 绝对路径」，不动业务内容** —— 脱敏的目的是让包能安全发给别人，不是让它没法排查。
2. **替换规则按 Needle 长度降序**：长而具体的路径（`...\AppData\Local\Eztools\install`）必须**先**换，
   否则用户名先被替换掉后，含用户名的路径就再也匹配不到了。
3. **路径尾必须保留**：`C:\Users\ishe\AppData\Local\Eztools\install\logs` 要变成
   `<home>\AppData\Local\Eztools\install\logs`（仍能看出是哪个目录），而不是整条变成 `<installRoot>`。

**验证**（`scripts/_step13_p4.sh` 13.13 / 13.14）：
- 13.13：造一个含**真实身份三要素**（用户名/机器名/主目录）的测试日志 → 跑 `--redact` →
  断言 zip **正文不含**这三者、**且仍含**业务内容标记（反向对照：不许把业务内容也误删）。
  ⚠️ 测试样本**刻意用 Python 写而非 bash `printf`**——本机 MSYS 的 `printf` 会吃掉 `\E`/`\U` 转义，
  写出 ` ztools` 这样的残字，会把自己骗成"脱敏有 bug"。
- 13.14：跑未脱敏 `diag` → 断言输出含「未脱敏」与「机器名」（清单没退化）
- 完整验收 **104 / 0**（102 → 104，+2 条 C4 断言）。

---

## 22. P4 落地记录 —— Wave 1：工具间协作与 `weight` 档位闸门（2026-09-20）

> 方案文档：`docs/P4-实施方案.md`。P4 分三波推进，本波只做**规格完备**的两项——
> 事件总线在文档里是空白（无方法名 / 订阅语义 / 载荷），`panels` 与安装器依赖外部决策，均未动。

### 22.1 交付物

| 位置 | 内容 |
|---|---|
| `src/Eztools.Contracts/ProtocolMethods.cs` | 新错误码 `-32004 InvokeCycle`（成环）· `-32005 WeightNotPermitted`（档位不支持） |
| `src/Eztools.Host/Processes/ToolHostManager.cs` | `InvokeToolAsync`（嵌套调用路径）· `Session.Chain`（调用链）· `_nestedGate` · `EnsureApiAllowed` / `EnsureProgressAllowed` |
| `sdk/python/eztools/tool.py` | `Tool.invoke_tool(tool_id, command_id, args)` |
| `sdk/python/eztools/protocol.py` | 两个新错误码常量 |
| `tools/probe/` | 新增 `probe.delegate`（跨工具转调）· `probe.selfCall`（自调用成环）· `probe.progress`（进度档位） |
| `tools/oneshot/` | **新工具**：`weight: script` 档参考实现（三个命令里两个故意要被拒绝） |
| `scripts/_step13_p4.sh` | P4 验收 12 项，由 `acceptance.sh` source |

### 22.2 核心设计：嵌套调用为什么不能复用 `InvokeCommandAsync`

`InvokeCommandAsync` 全程持有全局闸门 `_perToolGate`。工具在 handler 里发 `host.invokeTool(B)` 时，
宿主正持闸等该工具返回 —— 若嵌套走同一条路就是**三方死锁**（宿主等工具 → 工具等宿主 → 宿主等闸门）。
故 `InvokeToolAsync` **刻意不取该闸门**，改用独立的 `_nestedGate`。

死锁安全的依据是**调用链无环**：顶层调用被 `_perToolGate` 全局串行 ⇒ 任意时刻只有一条链 ⇒
链外工具必然空闲（它在等宿主，不是宿主在等它）⇒ **只要链上无重复工具，等待图必然无环**。
因此成环直接拒绝（`-32004`）而不是排队等待。

**深度上限刻意不做**：链上工具两两不同 ⇒ 链长天然被"已发现工具数"约束 ⇒ `MaxDepth` 是永不可达的分支（戒幽灵代码）。

### 22.3 验收

`acceptance.sh` **97/97 全绿**（85 基线 + P4 12 项）。逐项见 `docs/P4-实施方案.md` §4。
其中两条是这套验收的关键：**13.6** 用墙钟耗时区分"拒绝"与"挂到超时才报错"（后者说明实现方式错），
**13.10** 用 lite 档同 API 成功做对照，区分"闸门按档位生效"与"闸门把 API 全关"。

### 22.4 踩坑与已知限制

1. **`JsonNode` 父节点跨帧传递（第 4 条硬约束的又一次复现）**：`host.invokeTool` 的 `args` 挂在工具请求的
   params 树上，直接塞进新 envelope 会抛 `The node already has a parent.`，工具侧表现为 `-32603`。
   修法 `prms?["args"]?.Clone()`（与 `PrimitiveClient:96` 同源写法）。**出方向与入方向都要 Clone** ——
   第一版只做了出方向，正是被 13.1 这条断言抓出来的。
2. **`host.progress` 收紧为仅 `lifecycle: task`**（§5.3 明文，原先任何工具都能报，属文档与实现漂移）。
   ⚠️ 正向路径（task 工具上报进度）当前**无调用方**，本波只验了负向拒绝。
3. **`weight: script` 的"用完即退"在 `IdleRecycle > 0` 时会退化**：`ReleaseAsync` 的即用即走判据是
   `IdleRecycle <= 0`，与档位无关。当前产品无该配置路径（CLI 默认 0，Desktop 也走 `ForCli`），
   故**未改代码**（改了也验证不了），留待常驻宿主形态落地时一并处理。
4. ~~**事件总线仍是空白**：P4 五项里唯一没有规格的一项，未自行发明协议（见 §12 P4 与方案 §5 D1）。~~
   **✅ 已于 2026-09-21 拍板并落地**（D2：只做内部事件），见 §23。
5. ~~`panels` / `weight: full` 增量 / 安装器**未做**，属 Wave 2b/2c（打包形态已拍板，见 §23）。~~
   **✅ 均已落地（2026-09-21）**：安装器/更新器见 §24，`panels` + `weight: full` 增量见 §25。

> **章节号说明（2026-09-21 更正）**：本节原误编为 §21，与「§21 P2 尾巴落地记录」撞号，已改为 §22 并移到文末。

---

## 23. P4 落地记录 —— Wave 2a：宿主内部事件总线（2026-09-21）

> **决策依据（D2，2026-09-21 14:5x 拍板）**：**只做「宿主 → 托盘/设置 UI」的内部事件，不开放给工具订阅**。
> 理由：设计方案 §12 的 P4 清单只有"宿主事件总线"五个字——无方法名、无订阅语义、无载荷 schema。
> 当前**没有任何工具**需要订阅宿主事件，造一套 `host.events.subscribe` + 弱类型载荷总线
> = 造一个人人都可能用错的抽象（YAGNI、幽灵代码）；而"UI 要刷新但只能手动点刷新"是**已存在**的真实痛点
> （托盘菜单里的「刷新菜单」项），本波只解决后者。

### 23.1 交付物

| 位置 | 内容 |
|---|---|
| `Host/HostEvents.cs`（新增） | `HostEventKind`（6 种）· `HostEvent`（统一载荷 record）· `HostEventBus`（订阅/退订/发布） |
| `EztoolsHost.Events` | 门面暴露的事件总线实例；`CreateAsync` 里 `AttachLog`；`SetEnabled` / `ReloadAsync` 发事件 |
| `ToolHostManager` 构造函数 | 新增可空 `HostEventBus? events` 参数 + 私有 `Publish` 助手；在**5 个状态变更点**发事件 |
| `TrayApplication` | 订阅总线（存 `IDisposable` 并在 `Dispose` 里退订）+ `OnHostEvent` 反应（熔断弹气泡，其余落日志） |
| `SelfTestCommand.RunEventBusCases` | 9 条断言（新增「宿主内部事件总线」小节），selftest **29 → 38** |
| `scripts/acceptance.sh` 5.1 | 新增"用例数下限 + 事件总线小节在场"断言（防整节被删），验收 **104 → 105** |

### 23.2 三条设计决策（都是取舍，不是默认写法）

1. **统一载荷 `HostEvent(Kind, ToolId?, Detail?, At?)`，而不是每种事件一个 record**：
   订阅方（托盘/设置窗口）关心的是"有东西变了，该刷新哪一块"，**不需要**解析每种事件的细节字段；
   细节走 `Detail`（可读字符串）。这是"不做弱类型总线"与"订阅方要能拿到信息"之间的中间点。
   —— 同时**保留**了原有的强类型事件（`ToolQuarantined` / `NotifyRequested` / `ResidentSnapshot`）：
   它们面向的消费方不同（前者要窗口/次数等细节），合并会让任一方被迫解析不需要的 schema。

2. **投递模型 = 同步、按订阅顺序、逐个 try/catch**。三条理由：
   ① 事件源（进程管理/状态存储）都在**后台线程**，同步投递不阻塞 UI；
   ② 订阅方自己决定要不要 marshal —— 强制异步投递会引入"事件乱序"这个新问题；
   ③ **一个订阅者坏了不该让事件源跟着坏** —— 逐订阅者隔离异常，只记日志（`HostLog` 可空，
   早期构造阶段静默吞掉），**绝不向上抛**。这条有专门的断言守（"坏订阅者不把异常抛回事件源"）。

3. **`Subscribe` 返回 `IDisposable` 而非裸 `+=`/`-=`**：
   UI 重建时漏退订是最容易发生的泄漏（已死的菜单对象继续收事件）。用 `using`/存字段+`Dispose` 强制成对。
   ⚠️ **退订必须在 Dispose 宿主之前**：宿主 Dispose 会发 `ToolStopped`，那时图标已销毁，
   `_icon` 调用会抛（虽被 catch 吞掉，但日志会刷噪音）。

### 23.3 挂在哪 5 个状态变更点

| 位置 | 事件 | 为什么这里 |
|---|---|---|
| `SetEnabled`（`EztoolsHost`） | `ToolEnabledChanged` | 用户操作与熔断自动禁用都经这里 |
| `HandleCrashAsync` 熔断分支 | `ToolQuarantined` | 工具静默失效是用户会困惑的 → **唯一主动弹气泡的** |
| `HandleCrashAsync` 崩溃分支 | `ToolStopped`（含 exit code） | 崩溃也是"停了"，UI 状态得跟着变 |
| `AcquireAsync` 启动成功 | `ToolStarted`（含 pid） | resident 预热完成、调用拉起进程 |
| `ReleaseAsync` 回收 / `HandleTimeoutAsync` | `ToolStopped` | 空闲回收与超时都算停止 |
| `ReloadAsync` | `RegistryReloaded` | 清单重扫（新增/移除工具） |

### 23.4 一个值得记的坑：**新断言写进去了，但它可能是恒真的**

新增的"用例数下限 + 事件总线小节在场"断言，动机正是本波自己暴露的问题：
原来的"全部自检用例通过"断言**删掉一整节用例照样绿**——因为 `fail=0` 恒成立。
**这类"防退化"断言本身也要能被证伪**：我把下限设为 38（当轮真实值），但 38 会随用例增长而过期，
所以断言的语义是"**不低于 38**"（少了才失败，多了不失败）—— 这样将来加用例不需要改它，
而删用例会立刻红。**验收 105 项全绿 / selftest 38/38。**

---

## 24. P4 落地记录 —— Wave 2b：便携分发（更新器 / 卸载器 / 打包脚本）（2026-09-21）

**决策依据**：D1 = 自包含·非单文件·非裁剪；A3 = 便携自包含 zip + 首次运行铺开。（§12 路线图）

### 24.1 交付物

| 交付物 | 文件 | 说明 |
|---|---|---|
| 更新器 / 卸载器 | `src/Eztools.Host/LayoutUpdater.cs`（新建） | `Update()` + `Uninstall()` 两个公开方法，配套 `UpdateResult` / `UninstallResult` |
| CLI 命令面 | `src/Eztools.Cli/Program.cs` | 新增 `update --from <包\|zip>` 与 `uninstall [--yes] [--purge-data]` |
| 打包脚本 | `scripts/make-portable.sh`（新建） | 产出 `Eztools-<版本>-<rid>.zip`（自包含 publish ×3 → 组装 → zip → 校验） |
| 验收片段 | `scripts/_step14_w2b.sh`（新建） | 19 条端到端断言 |
| 安装器修正 | `src/Eztools.Host/LayoutInstaller.cs` | 4b 步骤改为"先查源 bin/ 再找构建输出"（见 24.4） |

**实测产物**：`Eztools-0.1.0-win-x64.zip` —— **87 MB / 544 条目 / 8 个工具 / 1 个载荷**，
sha256 `d7e370c4…d6c206`。解压后 `bin/ezt.exe version` 在 **`DOTNET_ROOT` 未设置**的情况下正常输出
（`ezt 0.1.0` / `.NET 10.0.12`）—— 这是"自包含"的可证伪判据，不是"命令退出 0"。

### 24.2 三条设计决策

1. **更新 = 备份程序 → 替换 → 保留数据与运行时，而不是清空重铺。**
   安装根里混着寿命完全不同的三类东西：**程序**（`bin/ tools/ sdk/ payload/`，整体替换）、
   **用户数据**（`toolsdata/` + `%APPDATA%\Eztools`，绝不碰）、**运行时**（`runtimes/`，保留 ——
   几十 MB 的解压不该每次更新重做）。
   替换名单刻意写成**白名单**（`ProgramDirs`）而非"删除除 X 外的一切"：后者漏一个目录就误删用户数据。

2. **卸载默认保留用户数据，`--purge-data` 才连数据一起清；非试运行必须 `--yes`。**
   这与 §13 反模式清单里"配置存在工具目录里 → 卸载即丢用户数据"是同一件事的两面。
   破坏性操作在脚本里手一滑的代价不可逆，所以"不给 `--yes` 返回 **rc=64** 且**什么都不删**"。

3. **`backup/` 归入"程序目录"一起删（实现取舍，方案文档未细化）。**
   理由：更新前的备份在**卸载**后毫无意义 —— 用户要回滚就不会卸载。所以 `uninstall` 的
   删除名单是 `{ bin, tools, sdk, payload, runtimes, cache, logs, backup }`。

### 24.3 验收口径（为什么不能只断言退出码）

更新与卸载的失败模式是**静默删多了 / 删少了**，而这两类**命令都退出 0**。所以断言落在具体路径上：

| 断言 | 判据 |
|---|---|
| 更新保留用户数据 | `toolsdata/echo/data/state.json` 内容仍是 `USER-DATA`，配置根 `echo.json` 仍是 `{"theme":"dark"}` |
| 更新报告如实 | 结果 JSON 的 `preserved` 含 `toolsdata` 与 `runtimes` |
| 更新有回滚备份 | `backup/` 下文件数 ≥ 1 |
| 无降级残留 | 旧版已删文件 `bin/removed-in-new-version.dll` **必须消失**（先删后拷的判据） |
| 试运行真不落盘 | `--dry-run` 后 `bin/ezt.exe` 仍是旧内容 `old-bin` |
| 卸载被拒绝时无破坏 | 缺 `--yes` → rc=64 **且安装根完好** |
| 默认卸载 | 程序目录全消失 **且** 用户数据与配置仍在 |
| `--purge-data` | 安装根与配置根**彻底消失**（含 `toolsdata/`） |

### 24.4 三个实现坑（都是实测，报错位置极具误导性）

1. **`tar -a` 是按扩展名选格式的 —— `.zip.partial` 打包出来是 tar。**
   产物大小 195 MB（没压缩），`unzip` 报 `start of central directory not found / zipfile corrupt`。
   修复：临时文件名也必须以 `.zip` 结尾（`<名字>.tmp-<pid>.zip`）。
   *附带*：本机 Git Bash **不带 `zip.exe`**（只有 `unzip.exe`/`zipinfo.exe`），
   所以脚本优先用 Windows 自带的 `tar.exe`（bsdtar，`-a` 认得出 `.zip`）。

2. **POSIX 路径不能交给 MSBuild。** `-o /d/01-...` 会被当成未知开关（`MSB1001`），
   且报错文本是整条 MSBuild 命令行，看不出真正原因。修复：脚本内加 `winpath()`，
   凡传给 `dotnet` 的路径一律 `盘符:/正斜杠`。

3. **便携包里没有 `src/` 目录树，`LayoutInstaller` 第 4b 步会误报"未找到 ezt-core.exe"。**
   实际第 4 步拷整个 `bin/` 时已把它带过去了（便携包的 `bin/` 里三件套齐全）。
   修复：4b 改为**先查源 `bin/ezt-core.exe`，再去找构建输出**，并按三种情形给不同措辞 ——
   避免"装成功了却提示缺件"。**这类"提示与实际不符"是比功能缺失更难排查的问题。**

### 24.5 打包产物与 §15.4 预算对照

| 形态 | 实测 | 预算口径 |
|---|---|---|
| CLI 单文件（`ezt.exe` only） | 37 MB | ≤ 40 MB ✅ |
| **整套分发（本脚本产物，zip）** | **87 MB** | ≤ 220 MB ✅ |
| 解压后（未压缩） | 182 MB | —— |

**验收 124 项全绿 / selftest 38/38。**


---

## 25. P4 落地记录 —— Wave 2c：面板协议（`panels` + `weight: full` 增量）（2026-09-21）

**决策依据**：D3 = **原生 WPF 面板**（否决 WebView2，理由见 §15.3：WebView 加 50~100 MB，会推翻 D1 的 220 MB 预算）。

**协议先行**：`docs/P4-Wave2c-面板协议.md`（12 节，**写码之前**完成）。
该文档是 Wave 2c 的唯一权威口径 —— 起因是 `panels` 此前在 `src/` 与 `sdk/` 里 **grep 零命中**，
而 §4.5 的表格里却写着它。这类"文档有、代码无"的条目会让人误判进度，所以先钉协议再动代码。

### 25.1 交付物

| 交付物 | 文件 | 说明 |
|---|---|---|
| 协议契约 | `docs/P4-Wave2c-面板协议.md`（新建） | 12 节：范围界定 / 字段表 / 数据通道 / 6 种节点 / 动作通道 / 渲染 / 档位闸门 / 入口 / SDK / 验收口径 / 轻量化 / 已知限制 |
| 契约层模型 | `src/Eztools.Contracts/ToolManifest.cs` | `ToolPanel` record + `ToolPanelLimits`（尺寸与刷新区间的唯一来源）+ `ToolContributions.Panels` |
| 契约层解析 | `src/Eztools.Contracts/ManifestParser.cs` | `ParsePanels()` + 两条跨字段约束（`PanelsRequireFull` / `PanelWithoutUiPanelNeed`） |
| 诊断码 | `src/Eztools.Contracts/Diagnostics.cs` | 7 个新码 + `needs.panel-without-ui-panel` |
| 协议方法 | `src/Eztools.Contracts/ProtocolMethods.cs` | `ToolPanelData = "tool.panel.data"`（**宿主 → 工具**方向） |
| SDK | `sdk/python/eztools/protocol.py` + `tool.py` | `TOOL_PANEL_DATA` + `_dispatch` 分支 + `_panel_data()` |
| 宿主数据通道 | `src/Eztools.Host/Processes/ToolHostManager.cs` | `PanelDataResult` + `PanelDataAsync()`（含两道闸门） |
| CLI | `src/Eztools.Cli/PanelCommand.cs`（新建） | `ezt panel` 三形态（列出 / 列某工具 / 拉数据，`--json` / `--text`） |
| **WPF 渲染器** | `src/Eztools.Desktop/PanelWindow.cs`（新建） | 6 种节点 → 原生控件；DispatcherTimer 定时刷新；订阅宿主事件总线 |
| 托盘入口 | `src/Eztools.Desktop/TrayApplication.cs` | 「面板」子菜单 + 同面板复用窗口 + 退出时关闭面板 |
| 桌面选项 | `src/Eztools.Desktop/DesktopOptions.cs` | 新增 `--tools-dir`（验收要能指向隔离工具根） |
| 验收工具 | `tools/paneltool/`（新建） | 4 个面板：`main`（6 种节点齐全）/ `empty` / `weird`（含未知 type）/ `broken`（返回非对象） |
| 验收片段 | `scripts/_step15_w2c.sh`（新建） | **24 条**端到端断言 |
| 托盘验收扩展 | `scripts/verify-desktop.py` §1c | **10 条**渲染断言（控件树，不开窗口） |

### 25.2 五条设计决策

1. **工具只返回数据，宿主负责渲染 —— 这是进程隔离的直接推论，不是风格选择。**
   允许工具带 HTML/WPF 代码等于让它往宿主进程注入 UI。V1 的面板 =
   **一个由工具数据驱动、由宿主渲染的只读数据视图 + 按钮**。

2. **整体重绘（每次返回完整 `nodes[]`，宿主清空重画），换来工具侧零状态。**
   无 diff、无节点 id ⇒ 工具只需回答"现在该显示什么"，不必记住上次画了什么。
   代价是滚动位置在刷新后重置（已列入 §11 已知限制）。

3. **按钮复用 `tool.invoke`，不新增 `tool.panel.action`。**
   命令已是一等公民（有 id / handler / 超时 / weight 闸门 / 环检测）；新增专用动作方法
   = 把这五样各复制一份并重新测一遍。复用后工具侧零新概念：**面板按钮就是带个
   `panelId` 参数的普通命令**，SDK 不用加 API。

4. **`refreshMs` 越界归 0，而不是钳制到 500。**
   钳制会把笔误（写 `10`）放大成**比作者预期还勤**的性能问题（每 500ms 拉一次）。
   安全默认值必须往"更少打扰"倒。

5. **`weight != full` 声明 `panels` 是 Error，不是自动升档。**
   `weight` 是作者对进程模型的**显式选择**（含超时与依赖策略）。宿主静默改写它 =
   让工具以作者没预期的形态运行（`script` 档"用完即退"语义会消失）。
   与"拼错的 weight 回退 + Warning"不同：那是笔误，这是**能力组合矛盾**。

### 25.3 验收口径

**24 条（`_step15_w2c.sh`）** 覆盖数据链路，判据全部落在**具体值**上：

| 断言 | 判据 |
|---|---|
| 面板清单 | `ezt panel` 输出的 `qualified` 含 `paneltool.main/empty/weird/broken`；**非法声明（lite 档 / 缺 id）不出现在列表** |
| 数据通道 | `nodes` 长度 > 0；`ok=true`；`panelId` 回带正确 |
| 节点类型 | `types == [heading, text, kv, list, separator, buttons]` **顺序也一致**（宿主不得重排） |
| 按钮引用 | `buttons[].commandId` 全部能解析到已声明命令 |
| 档位闸门 | badlite → Error `contributes.panels-require-full`；**对照**：full 档同结构无 Error |
| **缺 id** | Error `contributes.panel-missing-id`（结构性缺陷，非 Warning） |
| 尺寸钳制 | `99999→1600` / `10→240`，且三条 Warning 码齐全 |
| `refreshMs` | 越界 `10→0`（**不是 500**）；对照：合法 `1500` 原样保留 |
| 未知 type | `weird` 返回 3 个节点（含 `__future_widget__`），整体仍 `ok` |
| 非对象载荷 | 退出码 0 + `dataShape="array"` + `nodeCount=0`（不崩） |
| 按钮真实可用 | `paneltool.ping` 返回 `pong="paneltool"` 且 `fromPanel="main"`（panelId 上下文真的到了工具） |
| 反向闸门 | lite 档工具拉面板被拒（rc≠0） |

**10 条（`verify-desktop.py` §1c）** 覆盖渲染层 —— 数据侧全绿而渲染层吃掉 `type`
或按钮没绑回调，上面 24 条一个都发现不了：

`PanelWindow.InventoryForSelfCheck()` 真拉一次数据、装配控件树（**不显示窗口**）后断言：
`Button=2` / `Separator=1` / `Grid=1`（kv）/ `WrapPanel=1` / 尺寸 **720×520 取自清单**（非默认 640×480）/
空面板有提示 / 未知节点跳过但保住其余（`TextBlock=3`）/ 非对象载荷渲染成提示。

### 25.4 四个实现坑

1. **`panels[].id` 缺失的严重级别被静默降级（协议说 Error，代码发 Warning）。**
   根因：`ParsePanels` 的签名只接收 `Action<string, string> warn` —— **没有 Error 出口**。
   于是凡写 `warn(DiagnosticCodes.PanelMissingId, ...)` 的地方都自然退化成 Warning，
   而"级别"恰恰决定 `doctor` 的 `healthy` 判定与退出码。**没有任何编译期或测试期信号。**
   修复：`ParseContributions` 内补 `Error` 委托并传入；非对象条目、缺 id、非法 id 三处改走 `error`。
   **教训**：解析器只暴露一半诊断通道时，协议里写"Error"的地方会一致地退化成 Warning。

2. **自检控件计数走 `VisualTreeHelper` → 数字全错（2 个按钮数成 8 个）。**
   两个原因：模板展开的内部零件（`Border`/`ContentPresenter`/`ScrollViewer` 子孙）也进了计数；
   且**未 `Show()` 的窗口根本没有可视化树**，而这正是自检的前提。
   修复：改走**逻辑树**（`Panel`/`Decorator`/`ContentControl` 三类覆盖本窗口全部容器）——
   数出来的才是"我放了什么"，断言数字才有意义。

3. **`--tools-dir` 在托盘侧是个隐式的空开关。**
   `DesktopOptions.Parse` 不认识它 ⇒ 静默忽略；自检"碰巧能跑"只是因为**当前 cwd 恰好能找到
   `tools/`**。验收脚本一旦想指向隔离根（放负向夹具）就会失效。修复：补 `--tools-dir` 并透传
   `HostOptions.ToolsDir`。**"静默忽略未知参数"在这里不是宽容，是把一个真实依赖藏了起来。**

4. **`acceptance.sh` 里"每个工具都得有 config schema"是按排除法写的，被新工具撞红。**
   原判据 `configSchema is None and weight != "script" and id != "probe"` ——
   每新增一个**合法地没有** config 的工具就要加一个 `and id != xxx`，清单会烂掉，
   且看不出哪个是"故意没有"。修复：改成显式白名单 `NO_SCHEMA_TOOLS = {oneshot, probe, paneltool}`。
   **这类"排除法白名单"是维护陷阱 —— 它把"新增"变成了"改判据"。**

### 25.5 轻量化核算（§15.3 硬约束）

| 项 | 增量 | 说明 |
|---|---|---|
| `PanelWindow.cs` | ~19 KB 源码 | 纯 WPF，**零新依赖**（`PresentationFramework` 等已在） |
| 契约层 `ToolPanel` + 解析 | ~4 KB | record + 解析分支 |
| 二进制 | **≈ 0** | 不引入任何新程序集 |
| 托盘常驻工作集 | **67.8 MB**（实测） | §15 预算上限 100 MB ✅ **未因面板上升** |
| 运行时内存 | 一个面板窗口 ≈ 8~15 MB，**仅打开时** | 关闭即释放 |

**对照 D1 预算**：§15.4 口径上限 220 MB（自包含 182 MB + ~20% 余量），本增量在 **1 MB 以内**。
这是 D3 选"原生 WPF"而非"WebView"的直接兑现 —— 后者会加 50~100 MB。

### 25.6 已知限制（V1，诚实清单）

1. **只读 + 按钮**：不支持输入框 / 下拉框 / 复选框（工具想要输入 → 用命令 + 配置 schema）。
2. **整体重绘**：无 diff、无局部更新、刷新后滚动位置重置。
3. **无双向推送**：工具不能主动让面板刷新（靠定时 `refreshMs` 或用户点刷新 + 宿主事件）。
4. **无面板状态持久化**：窗口尺寸/位置不记。
5. **`weight: full` 仍无"独立进程组"语义**：`full` 与 `lite` 共用同一套进程管理路径，
   差异只在闸门与超时。若将来 `full` 需要"独立宿主进程 + 异步"，需单独一期。
6. **`ezt panel --text` 是简化渲染**：只做缩进树，不与 WPF 视觉一致。

### 25.7 结果

**验收 158 项全绿（124 → 158：`_step15_w2c.sh` +24 · `verify-desktop.py` +10）**，
exit 0。P4 全部 Wave（1 / 2a / 2b / 2c）收官 —— 设计方案 §12 路线图里
除 `search` 贡献点（仍标"未实现·未排期"）与项目自有工具的持续增补外，**P0~P4 无遗留**。

