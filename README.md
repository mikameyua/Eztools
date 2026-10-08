# Eztools

> | | |
> |---|---|
> | **本文档写什么** | **面向使用者**：有什么功能、怎么装、怎么跑、工程结构在哪、怎么加一个自己的工具。 |
> | **本文档不写什么** | **设计与实现决策一律不进这里** —— 那是 `docs/` 的职责。 |
> | **开发者入口** | [`docs/README.md`](docs/README.md) —— **全项目文档的唯一索引**（分类、权威层级、引用规则）。 |

类似 PowerToys 的一体化 Windows 工具集合：统一安装、统一设置界面、统一托盘入口。

**不是插件平台，不是第三方生态** —— 工具全部由自己编写。这条定位决定了大量设计取舍，
详见 [`docs/Eztools-设计方案.md`](docs/Eztools-设计方案.md)（唯一权威设计文档）。

> 📚 **找文档先看 [`docs/README.md`](docs/README.md)** —— `docs/` 的目录、权威层级与引用规则。
> 四类文档（主设计 / 阶段方案 / 协议契约 / 执行台账）职责互斥，**别混用**。

---

## 功能一览（已交付）

所有功能共用同一套底座：全局热键唤起、托盘入口、清单驱动发现（新增工具零 UI 代码）、
面板 `nodes[]` 原生 WPF 渲染、特权层 `ezt-core`。

| 波次 | 功能 | 说明 | 对标 |
|---|---|---|---|
| W3 | **极速文件搜索** | Everything 类毫秒级文件名搜索：MFT 全量索引 + USN 增量同步 + 全局热键唤起 + 前缀/子串/模糊 + Top-K | Everything / PowerToys 速览 |
| W4 | **屏幕 OCR** | 区域文字识别（`Windows.Media.Ocr`），多屏混合缩放坐标对齐 | PowerOCR / Text-Grab |
| W5 | **剪贴板历史** | SQLite + FTS5 持久化、唤出面板、Enter 直贴闭环、图片捕获 + OCR 提字、隐私黑名单 + 暂停 | CopyQ / Ditto |
| W6 | **取色 + 区域截图** | 共用屏幕遮罩窗：屏幕取色（多格式复制）、区域截图 | Snipaste / 取色器 |
| W7 | **统一启动器 Launcher** | 文件 / 应用 / 计算 / 单位换算 / 编码 / 剪贴板 / 系统命令 合一入口；空查询零往返、最近使用排序 | PowerToys Run / Flow.Launcher |
| W8 | **可用性回填** | 修复索引状态误报（Core 未跑却显示"正在建索引"）、启动器配置热生效 | — |
| W9 | **陈旧索引提示** | Core 缺席时提示索引陈旧并给启动入口（修复"沉默不可用"） | — |
| W10 | **剪贴板搜索 + 命令** | 启动器内搜剪贴板历史（`图片`/`img` 触发图片提字）；`>` / `cmd:` 前缀触发系统命令（锁屏 / 休眠 / 清空回收站） | — |
| W11 | **索引范围与排除** | 目录排除规则（整棵子树不进索引）、`pathFilter` 查询期包含闸、状态/重建可见出口 | — |

> 设计细节、协议字段、未决项台账一律进 `docs/`（见 [`docs/未完成项与待决清单.md`](docs/未完成项与待决清单.md)）。
> 每个波次都有配套的「设计方案 + 手工验收清单」，**全部验收脚本 + 真机手工项均通过**后才算收官。

---

## 当前状态

**W3 ~ W11 全部收官**（2026-10-07 W11 全波闭环）。宿主骨架、配置中心、托盘入口、设置窗口、
特权层、工具间协作、内部事件总线、便携分发、面板贡献点均已端到端跑通。

验收基线（最新）：

| 套件 | 结果 |
|---|---|
| `ezt selftest` | **336/0** |
| `scripts/acceptance.sh` | **541/0**（满额，环境占用的热键自动跳过并计数） |
| `python scripts/verify-desktop.py` | **274/0** |

**规划中（W12，设计已拍板 · 待实施）**：启动器空查询「最近使用」视图、触发词发现性提示、
工具动作接回（`filehash` / `wordcount` / `preview`）。详见 [`docs/W12-Launcher第二波-设计方案.md`](docs/W12-Launcher第二波-设计方案.md)。

**已知限制**（使用者关心的）：

- **分发形态**：当前以「源码 + 便携脚本」形态运行（`scripts/make-portable.sh` 齐备）；
  官方可分发包版本号尚停在 `0.1.0`（W3~W11 尚未进打包流程），对外装包待补「重打 + 版本推进」流程。自用无碍。
- **索引范围**：隐藏 / 系统文件当前不进索引（排除机制不基于文件属性位，系设计取舍）。
- **触发词发现性**：启动器部分触发词（如 `图片` / `>`）UI 暂无提示，正在 W12 补充。

---

## 快速开始

```bash
# 本机 .NET 10 SDK 是**便携安装**（C:\Program Files\dotnet 不可写，没并入系统），
# 所以下面用全路径调用它。若把 D:\dotnet10 加进 PATH，可以直接写 dotnet。
DOTNET="D:/dotnet10/dotnet.exe"
E="src/Eztools.Cli/bin/Debug/net10.0/ezt.exe"

$DOTNET build Eztools.sln         # 构建
# 首次部署：建目录 + 铺布局（tools/sdk/payload/bin）+ 部署运行时
$E install
$E runtime install
$E list                # 看有哪些工具
$E invoke echo.echo --text 你好
$E selftest            # 端到端自检
bash scripts/acceptance.sh        # 功能验收（18 步，满额 541，环境占用项自动跳过并计数）
bash scripts/budget.sh            # 轻量化预算：9 项断言（体积 / 内存 / 启动耗时），超限即非零退出
python scripts/verify-desktop.py --repo .   # 托盘/面板：274 项
python scripts/verify-preview.py --repo .   # 速览内容判定：33 项（编码 / PDF·Office 文字层 / 图片节点）
bash scripts/audit-tool-sources.sh          # 审计：工具源是否显式钉住（防"靠环境恰好如此"）
python scripts/check-doc-refs.py            # 文档交叉引用：§N.M 与文件路径是否有悬空
$E doctor              # 体检：安装与来源 + 运行时 + 清单诊断

# 配置中心
$E config list         # 看各工具有哪些配置项、哪些已落盘
$E config list echo    # 看某个工具的有效值（标注 默认值 / 已设置）
$E config set echo uppercase true   # 设置（会校验类型/枚举/上下限）
$E config schema echo  # 打印 schema —— 设置页的输入就是它

# 托盘（日常使用入口）
$DESKTOP                # 常驻：图标进通知区域，右键出菜单
$DESKTOP --selfcheck    # 只自检（建宿主 + 建菜单 + 读图标）后退出
$DESKTOP --click 0      # 程序化触发第 0 个菜单项（自动化验证链路用）
```

> 菜单项**点击后能不能跑通**由契约保证：能进托盘的命令必须"零参可执行 **或** 声明了
> `input: clipboard`（宿主读剪贴板注入 `args["input"]`，工具自己映射到 `text` / `path`）。
> 这条规则静态校验不了，由 `acceptance.sh` 第 9/10 步用具体输入真调一次来守。

---

## 工程结构

```
Eztools.sln
├── src/
│   ├── Eztools.Contracts/     契约层：清单模型 · 校验器 · 协议方法名 · 帧模型（零第三方依赖）
│   ├── Eztools.Host/          宿主框架库：路径 · 日志 · 运行时部署 · 清单发现 · 注册表 · 进程管理 · Launcher
│   ├── Eztools.Index/         极速搜索索引进程（MFT/USN · 查询 · 排除规则）
│   ├── Eztools.Core/          特权层 ezt-core.exe：命名管道 + 对端令牌校验的原语（MFT 枚举等）
│   ├── Eztools.Ocr/           屏幕 OCR 遮罩窗 + 取色/截图
│   ├── Eztools.Clipboard/     剪贴板历史监听 + 持久化
│   └── Eztools.Cli/           命令行宿主 ezt
├── sdk/python/
│   ├── eztools/               Python SDK（协议、主循环、宿主回调）
│   └── template/              新工具模板（复制即用）
├── tools/                     内置工具（清单驱动发现，新增工具 = 新增一个目录）
│   ├── echo/                  回显：协议/编码/大 payload 的基准探针
│   ├── wordcount/             文本统计：四类贡献点 + 配置 schema 示范
│   ├── filehash/              文件校验：校验下载文件哈希（纯标准库）
│   ├── preview/               速览：PDF/Office/图片 内容判定（vendored pypdf，BSD）
│   └── probe/                 诊断探针：环境可见性 + 崩溃/挂起自检钩子
├── payload/                   运行时载荷（python-<版本>-<rid>.tar.gz，不入库，由脚本生成）
├── scripts/                   验收 / 预算 / 便携打包 / 文档引用校验
├── spike/                     技术验证（已完成使命，保留作历史依据与实测数据出处）
├── docs/                      设计文档（唯一索引 = docs/README.md）
└── PowerToys/                 参照克隆（看它怎么做，以及哪里不该那么做）
```

> 注：W3~W11 的功能以「宿主 / 索引 / 剪贴板 / OCR 进程」形式集成，并非都落在 `tools/` 目录；
> `tools/` 下是可独立编写、清单驱动发现的内置工具。

## 真实安装目录（用户机器上）

```
%LOCALAPPDATA%\Eztools\              程序与运行时，可整体重装而不丢数据
├── bin\                             宿主可执行
├── tools\                           内置工具
├── sdk\                             Python SDK 源码（部署运行时时植入其 site-packages）
├── payload\                         运行时压缩包
├── runtimes\python\<版本>\           解压后的隔离运行时（不写 PATH，用户无需装 Python）
├── logs\                            宿主日志 + 按工具分文件
├── toolsdata\<toolId>\data\         工具私有数据
└── cache\                           临时解压

%APPDATA%\Eztools\                   用户数据，与程序目录分离
├── config\<toolId>.json             工具配置
└── state.json                       启用/禁用、热键覆盖
```

`ezt dirs` 会打印这份结构。

---

## 新增一个工具

**目标：新增工具 = 新建目录 + 写 2 个文件，宿主一行代码都不用改。**

```bash
cp -r sdk/python/template tools/my-tool
# 改 tool.json 的 id / contributes / config，改 main.py 的 handler
ezt list                # 自动出现
ezt invoke my-tool.run --text hello
```

宿主从 `tool.json` 自动获得：命令入口、动作、全局热键、托盘菜单项、进程生命周期、
以及配置 schema（将据此渲染设置页）。详见 [`sdk/python/template/README.md`](sdk/python/template/README.md)。

需要第三方依赖时不要 `pip install` 到全局，也不建议打包：

```bash
pip install --target "tools/my-tool/Lib" --only-binary=:all: pillow
```

---

## 开发须知

- **目标框架 net10.0**（2026-09-19 由 net7.0 升上来；.NET 7 已于 2024-05-14 终止支持）。
  SDK 在 `D:\dotnet10`（便携安装）。升级只需改 `Directory.Build.props` 一处；
  脚本里的 TFM 抽成了 `EZTOOLS_TFM` 变量。`LangVersion` 仍固定 11，放开到 C# 14 请单独决定。
- **源码为 UTF-8 无 BOM**，`Directory.Build.props` 里显式设了 `CodePage=65001`，
  否则中文注释在非英文区域设置下可能被按 ANSI 解出乱码。
- 遇到诡异问题时先跑 `ezt doctor`（看清单诊断与资源冲突）和 `ezt selftest`（看链路是否完好）。
- 文档引用有机器校验：`python scripts/check-doc-refs.py` 必须 0 悬空（CI 门禁之一）。

---

## 许可

本项目采用 **[GNU General Public License v3.0 or later](LICENSE)**（SPDX：`GPL-3.0-or-later`）。

**它是什么**：**自由软件 / 开源软件** —— 你可以自由使用、修改、再分发本项目，**包括商业使用**。

**它要求什么**：**copyleft**。你若分发本项目的衍生作品，必须：

1. **整体以 GPL-3.0（或更新版本）授权**；
2. **提供完整对应的源代码**；
3. **保留原有的版权与许可声明**（含 [`docs/参考来源.md`](docs/参考来源.md) 里登记的外部实现）。

> ⚠️ 常见误解：**GPL 不禁止商业使用**。别人完全可以拿本项目开公司、收费分发 ——
> 只要同样开源。GPL 管的是"下游必须继续开放"，不是"不许赚钱"。

**参考的外部实现**（如 PowerToys，MIT）**其许可独立且仍须遵守** ——
相关版权声明与借用记录见 [`docs/参考来源.md`](docs/参考来源.md)，
许可选型分析见 [`docs/许可证选型分析.md`](docs/许可证选型分析.md)。
