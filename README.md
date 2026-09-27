# Eztools

类似 PowerToys 的一体化 Windows 工具集合：统一安装、统一设置界面、统一托盘入口。

**不是插件平台，不是第三方生态** —— 工具全部由自己编写。这条定位决定了大量设计取舍，
详见 [`docs/Eztools-设计方案.md`](docs/Eztools-设计方案.md)（唯一权威设计文档）。

> 📚 **找文档先看 [`docs/README.md`](docs/README.md)** —— `docs/` 的目录、权威层级与引用规则。
> 四类文档（主设计 / 阶段方案 / 协议契约 / 执行台账）职责互斥，**别混用**。

---

## Wave 3 · 极速文件搜索（**W3-a~e 基本收官**）

Everything 类的毫秒级文件名搜索。**索引 / 查询 / 同步 / 搜索窗 / 边界五层已落地**，
`acceptance.sh` 全量验收 + `ezt selftest`（**184 条**）全绿：

| 阶段 | 状态 |
|---|---|
| W3-a 索引核心（MFT 全量 + `.ezidx` 落盘 + 热启动） | ✅ |
| W3-b 查询核心（前缀/子串/模糊 + Top-K + 管道缓存） | ✅ |
| W3-c 同步层（USN 增量 + journal 对账 + 有界化） | ✅ |
| W3-d 搜索窗（原生 WPF + 全局热键 + 高亮 + 三动作 + 虚拟化） | ✅（d-4 VS Code 适配器为可选支线，未排期） |
| W3-e 边界（后台 IO 优先级 / 暂停 / 多卷分类 / 边界用例） | ✅ |

| 文档 | 内容 |
|---|---|
| [`docs/W3-极速文件搜索-设计方案.md`](docs/W3-极速文件搜索-设计方案.md) | **架构权威**：索引 / 存储 / 查询 / 同步 / 交互 / 边界六层 + 坑位清单 + 落地偏差登记 |
| [`docs/W3-极速文件搜索-分步实施计划.md`](docs/W3-极速文件搜索-分步实施计划.md) | **执行权威**：阶段顺序 · 验收断言 · 风险表 · 里程碑 · 三个停手点 |

> ✅ **决策已全部收敛，无阻塞项。**
> - **G1 交互通道** → 方案 ①：面板 **`input` 节点**（协议 V1.2 → V1.3）。
> - **N2** → 补齐 `weight: full` 的"独立进程组"语义（崩溃隔离）。
> - **G2 通道延迟** ✅ **已实测并关闭**（2026-09-25）：搜索窗走**原生 WPF 直连 `ezt-index`（stdio）**，
>   判据**三段拆**；传输段热态 P95 实测 **前缀 1 ms / 子串 4 ms**（208 万条）。回退方案（新窗型号）**无需启用**。
>
> 收口与遗留见 [`docs/未完成项与待决清单.md`](docs/未完成项与待决清单.md) 与
> [`docs/W3-手工验收清单.md`](docs/W3-手工验收清单.md)（剩余手工项：热键实测 G1 / 提权 Core 首建索引路径）。

---

## 当前状态：P0 ~ P4 全部落地（P4 收官）

宿主骨架、清单驱动发现、工具进程管理、嵌入式运行时部署四件事已端到端跑通；
**配置中心**（P1a）可读写、可校验、可从损坏中恢复；
**托盘入口**（P2）已可运行 —— 菜单由 `tool.json` 自动合成，新增工具零 UI 代码；
**设置窗口**（P1b，WPF + iNKORE.UI.WPF.Modern）schema 驱动渲染，新增工具零 UI 代码。
**特权层**（P3，`ezt-core.exe`）命名管道 + 对端令牌校验，首批 5 个原语。
**工具间协作**（P4 Wave 1）`host.invokeTool` + `weight` 档位闸门；
**内部事件总线**（Wave 2a）· **便携分发 / 更新 / 卸载**（Wave 2b，`scripts/make-portable.sh`）·
**面板贡献点 `panels`**（Wave 2c）：工具只返回 `nodes[]` 数据、宿主用**原生 WPF** 渲染
（6 种声明式节点 + 按钮复用 `tool.invoke`，`weight: full` 才可用，协议见
[`docs/P4-Wave2c-面板协议.md`](docs/P4-Wave2c-面板协议.md)）。
**两种形态都验证过**：从仓库运行（开发形态）与装到安装根后运行（已安装形态）。
`scripts/acceptance.sh` 一键复现，**169 项全绿**
（85 基线 + P4 12 + 工程尾巴 7 + Wave 2a 1 + Wave 2b 19 + Wave 2c 24 + 托盘渲染 10
+ 热键注入 4 + 速览 1；速览选中项另有 2 条**环境依赖**断言，桌面被占用时自动跳过并打印现场）。

> 想自己验一遍？看 **[`docs/P0-验证手册.md`](docs/P0-验证手册.md)**：
> 三档验证路径（10 秒冒烟 / 25 秒一键 / 手工分步）、反向验证（故意弄坏看宿主如何报错）、
> 退出码表、实测基准数字、以及**尚未落地的边界清单**。

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
bash scripts/acceptance.sh        # 功能验收（18 步）：满额 225，实跑 222~225（速览"选中项"3 条依赖空闲桌面，跳过会显式打出）
bash scripts/budget.sh            # 轻量化预算：9 项断言（体积 / 内存 / 启动耗时），超限即非零退出
python scripts/verify-desktop.py --repo .   # 托盘/面板：53 项（含热键直达面板与气泡正文真值；3 条依赖空闲桌面）
python scripts/verify-preview.py --repo .   # 速览内容判定：33 项（编码 / PDF·Office 文字层 / 图片节点）
bash scripts/audit-tool-sources.sh          # 审计：工具源是否显式钉住（防"靠环境恰好如此"，S11）
python scripts/check-doc-refs.py            # 文档交叉引用：§N.M 与文件路径是否有悬空
$E doctor              # 体检：安装与来源 + 运行时 + 清单诊断

# 配置中心（P1a）
$E config list         # 看各工具有哪些配置项、哪些已落盘
$E config list echo    # 看某个工具的有效值（标注 默认值 / 已设置）
$E config set echo uppercase true   # 设置（会校验类型/枚举/上下限）
$E config schema echo  # 打印 schema —— 将来设置页的输入就是它

# 托盘（P2）
$E tray                # 菜单会长什么样（不必启动 GUI）
$E tray --json         # 结构化输出（验收脚本用它断言）
$E tray --check        # 静态检查：托盘项引用的命令能否解析
```

### 托盘（日常使用入口）

双击 `Eztools.Desktop.exe`，图标出现在任务栏通知区域（**可能在溢出区**，点小三角可看到；
首次运行会提示一次）。右键就是工具菜单 —— 条目由 `contributes.menus` 自动合成，
**新增工具零 UI 代码**。

```bash
DESKTOP="src/Eztools.Desktop/bin/Debug/net10.0-windows/Eztools.Desktop.exe"
$DESKTOP                # 常驻：图标进通知区域，右键出菜单
$DESKTOP --selfcheck    # 只自检（建宿主 + 建菜单 + 读图标）后退出
$DESKTOP --click 0      # 程序化触发第 0 个菜单项（自动化验证链路用）
```

> 菜单项**点击后能不能跑通**由契约保证：能进托盘的命令必须"零参可执行 **或** 声明了
> `input: clipboard`"（宿主读剪贴板注入 `args["input"]`，工具自己映射到 `text` / `path`）。
> 这条规则静态校验不了，由 `acceptance.sh` 第 9/10 步用具体输入真调一次来守。

---

## 工程结构

```
Eztools.sln
├── src/
│   ├── Eztools.Contracts/     契约层：清单模型 · 校验器 · 协议方法名 · 帧模型（零第三方依赖）
│   ├── Eztools.Host/          宿主框架库：路径 · 日志 · 运行时部署 · 清单发现 · 注册表 · 进程管理
│   └── Eztools.Cli/           命令行宿主 ezt（P1 的设置应用将引用同一个 Eztools.Host）
├── sdk/python/
│   ├── eztools/               Python SDK（协议、主循环、宿主回调）
│   └── template/              新工具模板（复制即用）
├── tools/                     内置工具（清单驱动发现，新增工具 = 新增一个目录）
│   ├── echo/                  回显：协议/编码/大 payload 的基准探针
│   ├── wordcount/             文本统计：四类贡献点 + 配置 schema 示范
│   ├── filehash/              文件校验：校验下载文件哈希（纯标准库）
│   └── probe/                 诊断探针：环境可见性 + 崩溃/挂起自检钩子
├── payload/                   运行时载荷（python-<版本>-<rid>.tar.gz，不入库，由脚本生成）
├── scripts/
│   ├── make-payload.sh        把一份独立 CPython 打成可部署载荷
│   └── acceptance.sh          P0 验收（可重复、可进 CI）
├── spike/                     技术验证（已完成使命，保留作历史依据与实测数据出处）
├── docs/                      设计文档
└── PowerToys/                 参照克隆（看它怎么做，以及哪里不该那么做）
```

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
├── config\<toolId>.json             工具配置（P1 配置中心）
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
以及配置 schema（P1 将据此渲染设置页）。详见 [`sdk/python/template/README.md`](sdk/python/template/README.md)。

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
- **在 WorkBuddy 里跑命令要注意**：写真实磁盘（`%LOCALAPPDATA%` 等）会被沙箱静默拦截
  （exit 0 但文件没动），需要显式关闭沙箱；Git Bash 的 `pwd` 给 POSIX 路径，
  传给 .NET 可执行文件会被解析成当前盘符根下的 `D:\d\...`，脚本里一律用 `pwd -W`。
- 遇到诡异问题时先跑 `ezt doctor`（看清单诊断与资源冲突）和 `ezt selftest`（看链路是否完好）。

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
