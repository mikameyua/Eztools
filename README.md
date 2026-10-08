# Eztools

类似 PowerToys 的一体化 Windows 工具集合：统一安装、统一设置界面、统一托盘入口。

**不是插件平台，不是第三方生态** —— 工具全部由自己编写。这条定位决定了大量设计取舍，详见 [`docs/Eztools-设计方案.md`](docs/Eztools-设计方案.md)（项目唯一权威设计文档）。

本文档面向使用者：有什么功能、怎么装、怎么跑、工程结构在哪、怎么加一个自己的工具。设计与实现决策不进本文，一律见 [`docs/`](docs/README.md)。

---

## 功能一览

所有功能共用同一套底座：全局热键唤起、托盘入口、清单驱动发现（新增工具零 UI 代码）、面板 `nodes[]` 原生 WPF 渲染、特权层 `ezt-core`。

| 波次  | 功能                 | 说明                                                                     | 对标                            |
| --- | ------------------ | ---------------------------------------------------------------------- | ----------------------------- |
| W3  | **极速文件搜索**         | Everything 类毫秒级文件名搜索：MFT 全量索引 + USN 增量同步 + 全局热键唤起 + 前缀/子串/模糊 + Top-K   | Everything / PowerToys 速览     |
| W4  | **屏幕 OCR**         | 区域文字识别（`Windows.Media.Ocr`），多屏混合缩放坐标对齐                                 | PowerOCR / Text-Grab          |
| W5  | **剪贴板历史**          | SQLite + FTS5 持久化、唤出面板、Enter 直贴闭环、图片捕获 + OCR 提字、隐私黑名单 + 暂停             | CopyQ / Ditto                 |
| W6  | **取色 + 区域截图**      | 共用屏幕遮罩窗：屏幕取色（多格式复制）、区域截图                                               | Snipaste / 取色器                |
| W7  | **统一启动器 Launcher** | 文件 / 应用 / 计算 / 单位换算 / 编码 / 剪贴板 / 系统命令合一入口；空查询零往返、最近使用排序                | PowerToys Run / Flow.Launcher |
| W8  | **可用性回填**          | 修复索引状态误报（Core 未跑却显示"正在建索引"）、启动器配置热生效                                   | —                             |
| W9  | **陈旧索引提示**         | Core 缺席时提示索引陈旧并给启动入口（修复"沉默不可用"）                                        | —                             |
| W10 | **剪贴板搜索 + 命令**     | 启动器内搜剪贴板历史（`图片` / `img` 触发图片提字）；`>` / `cmd:` 前缀触发系统命令（锁屏 / 休眠 / 清空回收站） | —                             |
| W11 | **索引范围与排除**        | 目录排除规则（整棵子树不进索引）、`pathFilter` 查询期包含闸、状态/重建可见出口                         | —                             |

每个波次都有配套的「设计方案 + 手工验收清单」，全部验收脚本与真机手工项均通过后才算收官。未决项见 [`docs/未完成项与待决清单.md`](docs/未完成项与待决清单.md)。

---

## 当前状态

**版本 `0.11.0`**，对应 W11 全波闭环（2026-10-07）。版本号语义为 `主版本.次版本`，次版本号即功能波次序号（W11 → `0.11.0`）。

验收基线：`ezt selftest` 336 项通过 · `scripts/acceptance.sh` 满额 541 项 · `verify-desktop.py` 274 项。后两项为运行时动态计数，受环境影响会浮动。

**规划中（W12，设计已拍板，待实施）**：启动器空查询「最近使用」视图、触发词发现性提示、工具动作接回（`filehash` / `wordcount` / `preview`）。详见 [`docs/W12-Launcher第二波-设计方案.md`](docs/W12-Launcher第二波-设计方案.md)。

**已知限制**：

- **仅支持 Windows**：依赖 MFT / USN Journal / `Windows.Media.Ocr` / WPF，无跨平台路径。
- **分发形态**：提供**自包含便携包** —— 解压即用，目标机器**无需安装 .NET**。已发布 [`v0.11.0` Release](https://github.com/mikameyua/Eztools/releases/tag/v0.11.0)（附 `Eztools-0.11.0-win-x64.zip`，约 90 MB，含 .NET 与 Python 两套运行时）。用户流程：解压 → `bin\ezt.exe install --from .` → `bin\ezt.exe runtime install` → `bin\Eztools.Desktop.exe`。自行打包用 `bash scripts/make-portable.sh`（版本号手改、非 CI 推进）。
- **索引范围**：隐藏 / 系统文件当前不进索引（排除机制不基于文件属性位，系设计取舍）。
- **触发词发现性**：启动器部分触发词（如 `图片` / `>`）UI 暂无提示，正在 W12 补充。
- **索引重建**：改排除规则需重启托盘才带新值，重建才回收磁盘。

---

## 快速开始

### 1. 前置要求

| 依赖           | 版本       | 说明                   |
| ------------ | -------- | -------------------- |
| **.NET SDK** | **10.x** | 唯一硬依赖。构建目标 `net10.0` |
| **Git**      | 任意近期版本   | 克隆本仓库                |
| **Python**   | —        | **无需预装**，内置隔离运行时     |

.NET 10 支持到 2028-11-14，8 / 9 已进入收尾。若机器上只有旧版 SDK，请从 <https://dotnet.microsoft.com/download/dotnet/10.0> 安装，或用便携版并把它的目录加入 `PATH`。

### 2. 构建

```bash
git clone https://github.com/mikameyua/Eztools.git
cd Eztools
dotnet build Eztools.sln
```

产物：`src/Eztools.Cli/bin/Debug/net10.0-windows10.0.19041.0/ezt.exe`。输出目录带 Windows 平台后缀是正常的 —— Cli 依赖 `net10.0-windows` 的 Win32 API。

### 3. 生成运行时载荷（必做，否则下步会失败）

`payload/` 不在仓库里（体积大，且每台机器的运行时可能不同），需自行生成一次：

```bash
bash scripts/make-payload.sh /path/to/python-<版本>+<rid> payload   # 尖括号换成实际路径
ezt runtime payloads    # 校验产物是否被宿主识别
```

产物形如 `payload/python-<版本>-<rid>.tar.gz`（约 13~20 MB），`<版本>` 由源 Python 自动读取 —— 脚本不挑版本。

源 Python 的选择：

- **推荐**：[python-build-standalone](https://github.com/astral-sh/python-build-standalone) 的 `install_only` 产物（完整安装布局、自带 pip、为再分发设计、可重定位）
- **可用**：python.org Windows 安装器指向的安装目录
- **不可用**：python.org embeddable zip（带 `._pth`、无 pip、site 被禁用）与 venv（不可重定位）—— `make-payload.sh` 会直接拒绝并说明原因

### 4. 部署与使用

```bash
E="src/Eztools.Cli/bin/Debug/net10.0-windows10.0.19041.0/ezt.exe"

$E install              # 建目录 + 铺布局（tools/sdk/payload/bin）
$E runtime install      # 从载荷部署隔离运行时
$E list                 # 看有哪些工具
$E invoke echo.echo --text 你好
$E selftest             # 端到端自检
$E doctor               # 体检：安装与来源 + 运行时 + 清单诊断
```

托盘常驻：运行 `src/Eztools.Desktop/bin/Debug/net10.0-windows10.0.19041.0/Eztools.Desktop.exe`，图标进通知区域，右键出菜单。

### 5. 验证与打包

```bash
bash scripts/acceptance.sh        # 功能验收
bash scripts/budget.sh            # 轻量化预算：体积 / 内存 / 启动耗时
python scripts/verify-desktop.py --repo .   # 托盘/面板
python scripts/verify-preview.py --repo .   # 速览内容判定
bash scripts/audit-tool-sources.sh          # 工具源是否显式钉住
python scripts/check-doc-refs.py            # 文档交叉引用：0 悬空
```

验收脚本默认使用 `dotnet`（或 `PATH` 中的 `DOTNET_ROOT`）。用便携 SDK 时显式指定：

```bash
DOTNET_ROOT=/d/dotnet10 bash scripts/acceptance.sh
```

贡献代码请参考 [`CONTRIBUTING.md`](CONTRIBUTING.md)（四层验证流程、静态守卫规则、代码纪律）。

### 便携包分发（给不想装 SDK 的人）

若你不想在本机装 .NET SDK，直接下载已发布的
[`v0.11.0` Release](https://github.com/mikameyua/Eztools/releases/tag/v0.11.0) —— 附
`Eztools-0.11.0-win-x64.zip`（约 90 MB，已 sha256 校验并实测可在未装 .NET 的环境启动）。

自行打包或验证：

```bash
# 维护者侧：产出 dist/Eztools-<版本>-win-x64.zip（约 90 MB）
bash scripts/make-portable.sh

# 校验产物确实能在未装 .NET 的环境启动（解包 + 隔离环境实启四个 Exe）
python scripts/verify-runtimes.py --zip dist/Eztools-<版本>-win-x64.zip
```

包内布局与安装根同构，用户拿到后：

```bash
bin\ezt.exe install --from .      # 铺到 %LOCALAPPDATA%\Eztools
bin\ezt.exe runtime install      # 部署包内自带的 Python 运行时
bin\ezt.exe selftest             # 验证
bin\Eztools.Desktop.exe          # 启动托盘
```

包内已含 .NET 与 Python 两套运行时，目标机器无需预装任何东西。

### 6. 配置中心

```bash
ezt config list         # 看各工具有哪些配置项、哪些已落盘
ezt config list echo    # 看某个工具的有效值（标注默认值 / 已设置）
ezt config set echo uppercase true   # 设置（会校验类型/枚举/上下限）
ezt config schema echo  # 打印 schema —— 设置页的输入就是它
```

菜单项点击后能不能跑通由契约保证：能进托盘的命令必须「零参可执行」**或**声明了 `input: clipboard`（宿主读剪贴板注入 `args["input"]`，工具自己映射到 `text` / `path`）。该规则静态校验不了，由 `acceptance.sh` 第 9/10 步用具体输入真调一次来守。

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
│   ├── preview/               速览：PDF/Office/图片内容判定（vendored pypdf，BSD）
│   └── probe/                 诊断探针：环境可见性 + 崩溃/挂起自检钩子
├── scripts/                   验收 / 预算 / 便携打包 / 载荷生成 / 文档引用校验
└── docs/                      设计文档（唯一索引 = docs/README.md）
```

W3~W11 的功能以「宿主 / 索引 / 剪贴板 / OCR 进程」形式集成，并非都落在 `tools/` 目录；`tools/` 下是可独立编写、清单驱动发现的内置工具。

以下目录本机开发用，不随仓库分发：`spike/`（早期技术验证）、`PowerToys/`（上游参照克隆，仅供对照阅读）、`_scratch/`（本机临时产物）、`payload/`、`runtimes/`、`dist/`（便携包）。其中 `payload/` 是运行必需的，见「3. 生成运行时载荷」。

## 真实安装目录

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

目标：新增工具 = 新建目录 + 写 2 个文件，宿主一行代码都不用改。

```bash
cp -r sdk/python/template tools/my-tool
# 改 tool.json 的 id / contributes / config，改 main.py 的 handler
ezt list                          # 自动出现
ezt invoke my-tool.run --text hello
```

宿主从 `tool.json` 自动获得：命令入口、动作、全局热键、托盘菜单项、进程生命周期，以及配置 schema（将据此渲染设置页）。详见 [`sdk/python/template/README.md`](sdk/python/template/README.md)。

需要第三方依赖时不要 `pip install` 到全局，也不建议打包：

```bash
pip install --target "tools/my-tool/Lib" --only-binary=:all: pillow
```

---

## 开发须知

- **目标框架 net10.0**（2026-09-19 由 net7.0 升上来；.NET 7 已于 2024-05-14 终止支持）。升级只需改 `Directory.Build.props` 一处，脚本里的 TFM 抽成了 `EZTOOLS_TFM` 变量。
- **`LangVersion` 刻意不固定**，跟随 TFM（net10.0 → C# 14）。钉死旧值会让 SDK 自带的源码生成器（如 `System.Text.RegularExpressions.Generator`）产出无法编译的代码，且报错发生在 `obj/` 下、看起来像自己的代码有问题。**这意味着构建结果依赖 SDK 版本** —— 换 SDK 版本后请重跑 `ezt selftest`。
- **源码为 UTF-8 无 BOM**，`Directory.Build.props` 里显式设了 `CodePage=65001`，否则中文注释在非英文区域设置下可能被按 ANSI 解出乱码。
- 遇到诡异问题时先跑 `ezt doctor`（看清单诊断与资源冲突）和 `ezt selftest`（看链路是否完好）。
- 文档引用有机器校验：`python scripts/check-doc-refs.py` 必须 0 悬空。

---

## 参与贡献

欢迎参与。开始之前：

- [`CONTRIBUTING.md`](CONTRIBUTING.md) —— 四层验证流程、静态守卫 G1~G8、代码纪律
- [`docs/README.md`](docs/README.md) —— 全项目文档索引，§0 读者导航按目的给出切入路径
- 提交会触发 CI（构建 + 自检 + 守卫 + 文档引用，约 40 秒）；涉及 UAC / 托盘 / 热键的验收需手工跑

## 许可

本项目采用 **[GNU General Public License v3.0 or later](LICENSE)**（SPDX：`GPL-3.0-or-later`）。



分发衍生作品时必须：

1. 整体以 GPL-3.0（或更新版本）授权；
2. 提供完整对应的源代码；
3. 保留原有的版权与许可声明。

参考的外部实现（如 PowerToys，MIT）其许可独立且仍须遵守 —— 相关版权声明与借用记录见 [`docs/参考来源.md`](docs/参考来源.md)，许可选型分析见 [`docs/许可证选型分析.md`](docs/许可证选型分析.md)。
