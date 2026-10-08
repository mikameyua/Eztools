# 贡献指南

本文说明怎么构建、怎么验证、提交要满足什么。

## 环境要求

| 依赖 | 版本 | 说明 |
|---|---|---|
| **.NET SDK** | **10.x** | 唯一硬依赖。构建目标 `net10.0` |
| Python | 3.11+ | 仅开发期需要（跑验收脚本），运行 Eztools 不需要预装 Python |

机器上可能同时存在旧版 `dotnet`（如 7.x）。`ezt.exe` 是框架依赖应用，且 apphost **不扫 `PATH`**，只看 `DOTNET_ROOT` 与注册表默认安装位置。脚本已内置探测与版本校验，装了多个版本时请显式指定：

```bash
DOTNET_ROOT=/path/to/dotnet10 bash scripts/acceptance.sh
```

## 构建

```bash
dotnet build Eztools.sln
```

产物：`src/Eztools.Cli/bin/Debug/net10.0-windows10.0.19041.0/ezt.exe`。输出目录带 Windows 平台后缀是正常的 —— Cli 依赖 `net10.0-windows` 的 Win32 API。

构建结果依赖 SDK 版本：`Directory.Build.props` 刻意不固定 `LangVersion`，跟随 TFM（net10.0 → C# 14）。这是必要的 —— SDK 自带的源码生成器（如 `System.Text.RegularExpressions.Generator`）产出可能要求更新的语言版本，钉死旧值会让构建在 `obj/` 下报出看起来像"自己代码有问题"的错。换 SDK 版本后请重跑 `ezt selftest`。

---

## 验证：四层，缺一不可

改动落地前按下面顺序跑。判据一律是「FAIL=0」，不是"看起来没报错"。

| 层 | 命令 | 覆盖 | 何时必跑 |
|---|---|---|---|
| **1. 自检** | `ezt selftest` | 336 项端到端断言 | 任何改动 |
| **2. 守卫** | `python scripts/review-guards.py` | G1~G8 静态规则 | 任何改动 |
| **3. 文档** | `python scripts/check-doc-refs.py --strict` | 文档交叉引用 | 改了 `docs/` 或 README |
| **4. 验收** | `bash scripts/acceptance.sh` | 功能验收 | 改了 `src/` |
| **4b. 桌面** | `python scripts/verify-desktop.py --repo .` | 托盘 / 面板 | 改了 Desktop 层 |

```bash
dotnet build Eztools.sln
E="src/Eztools.Cli/bin/Debug/net10.0-windows10.0.19041.0/ezt.exe"
$E selftest
python scripts/review-guards.py
python scripts/check-doc-refs.py --strict
bash scripts/acceptance.sh
python scripts/verify-desktop.py --repo .
```

断言总数随功能增长，**热键被其他程序占用时会自动跳过并计数** —— 判据是「环境项扣除后 FAIL=0」，不要求绝对数满额。

### 持续集成

`.github/workflows/ci.yml` 在每次 push / PR 自动跑**第 1~3 层**（构建 + selftest + 守卫 + 文档引用）。

**故意不跑第 4 层**（`acceptance.sh` / `verify-desktop.py`）：它们需要真 UAC 提权、托盘窗口与真实热键，CI 环境里关键项会大面积退化为 skip —— 那时判别力很低，只制造"红了不知道为什么"的噪声。这两层由**手工验收**承担，见 `docs/W*-手工验收清单.md`。

runner 实测耗时（首次跑通，共 93 秒）：

| 步骤 | 耗时 |
|---|---|
| 构建（.sln 全解） | 30s |
| 准备 Python 运行时载荷 | 10s |
| `ezt selftest` | 32s |
| 守卫 + 守卫自测 | 3s |
| 文档引用 | 1s |
| 其余（检出 / 装 SDK / 缓存） | 17s |

> `payload/` **不入库**（`.gitignore` 排除），而 selftest 启动时要从它部署
> Python 运行时 ⇒ CI 必须先 `make-payload.sh` 生成一次。跳过这步会让
> selftest 在第一项就 FAIL 并提前返回，后续 300+ 项根本不执行。

`global.json` 用 `rollForward: latestFeature` 钉住 SDK 大版本，避免 runner 默认版本漂移导致的构建差异。

#### CI 环境相关的两项设置

| 变量 | 作用 |
|---|---|
| `PYTHONUTF8=1` | 本仓脚本大量输出中文，而 Python 默认按系统 locale 编码 stdout，英文 locale 的 runner 上会抛 `UnicodeEncodeError`（脚本侧已各自加 `reconfigure` 兜底，此为环境级双保险） |
| `EZTOOLS_PERF_SCALE=20` | selftest 含性能红线（扫描 100 万条 ≤ 200 ms）。Debug 下已按 ×10 放宽，但 **runner 比开发机慢约 2.3 倍**（实测 P50：本机 96.5 ms vs runner 219 ms），200 ms 预算是**临界波动**（同代码一次过、一次红）。×20 ⇒ 400 ms，给 runner 约 1.8 倍余量 |

> ⚠️ 放宽性能预算**不是关掉断言** —— 断言仍在跑、仍能抓"退化到荒谬"，且 selftest 会**显式打印** `[perf]` 放宽声明（不静默）。真性能红线由开发机上的 Release 验收承担（`#if DEBUG` 分支，见 `SelfTestCommand.cs`）。

#### 失败时怎么排查

CI 日志在**无凭证时不可读**（`api/actions/runs/*/logs` 返 403、job 页面是 CSR 壳、`runs/<id>/logs` 返 404）。因此流程末尾有一个 `if: failure()` 步骤，把 `selftest.log` 的 `[FAIL]` 行与末尾 60 行写入一条**固定复用的 issue**（label `ci-digest`）—— issue 内容是匿名可读的，这是唯一无凭证可见的通道。

---

## 静态守卫（G1~G8）

`scripts/review-guards.py` 挂在验收脚本里（`scripts/_step19_review_guards.sh`）。改动引入下列模式会被拦下：

| 规则 | 拦什么 |
|---|---|
| **G1** | 静默吞异常（空 `catch` 块） |
| **G2** | 字符串 `switch` 缺 `default` |
| **G3** | 验收脚本里的禁用模式（如 `cmd \| grep -q`、`A && B \|\| C && D`） |
| **G4** | 幽灵代码（死代码 / 永不可达分支） |
| **G5** | 死产物目录（多 TFM 残留） |
| **G6** | 测试被跳过但未计数 |
| **G7** | 分层违规（见下） |
| **G8** | 生产接线缺失（符号定义了但没人调用） |

空 `catch` 确有必要时，必须显式声明豁免并写明理由，否则一律判FAIL：

```csharp
// review-guards:allow-empty-catch :: <理由>
```

自测：`python scripts/review-guards.py --selftest`。

### 分层硬约束（G7）

- **纯逻辑 → `Host{net10.0}`**：Cli selftest 可直接测，且编译器强制它碰不到 Win32
- **Win32 调用 → `Desktop{net10.0-windows}`**：避开 CA1416 跨平台分析器误报

评审新代码时先问：这个依赖类型住哪个 TFM？

---

## 代码纪律

以下是本项目踩过坑后定下的规则。

1. 新代码"没生效"先查 DLL 时间戳，再 `--no-incremental` 重编。
2. 断言必须落在数字或退出码上。写"看起来在守"却恒真的断言等于没守。
3. 别写"被上游结构保证"的断言 —— 那种断言恒真。
4. 静默类断言必须配正向对照：单看"某文件没变"是空断言（可能只是没执行），必须另有"前置动作确实发生"的独立旁证。
5. 复用前先确认 TFM。`Host` 不能引用 Win32 类型 —— 编译器会拦，但别去试探。
6. 新增协议字段要两层都测（协议层 + `ezt <cmd> --json`）。
7. 原生资源用 `try/finally`（`AllocHGlobal`、句柄）。
8. 源码是 UTF-8 无 BOM。`Directory.Build.props` 已设 `CodePage=65001`，别删。
9. 别把本机路径写死进脚本。用 `${VAR:-}` + 探测 + 可覆盖的环境变量。
10. vendor 第三方代码必须登记 `THIRD-PARTY-NOTICES.md`（GPL 义务）。

---

## 提交规范

```
<类型>: <一句话概括>

<正文：为什么这样改、有什么影响、怎么验证的>
```

类型用：`feat` / `fix` / `docs` / `chore` / `refactor` / `test`。

写好正文。这个项目的历史提交（如 `W11 索引范围与排除规则全波落地（代码/守卫/验收脚本）`）是往后追溯设计决策的依据。

## 文档约定

- `docs/README.md` 是全项目文档的唯一索引，先读它再读别的（其 §0 读者导航给出按目的的切入路径）。
- 设计决策 → `docs/`；日常使用 → `README.md`。
- 改了波次方案要同步更新对应的手工验收清单。
- 文档里的路径引用会被 `check-doc-refs.py` 校验。写"将新建"的路径时用`（新建）` **前缀**标记 —— 脚本按行内任意位置扫路径，不认语义。

## 许可

贡献即表示你同意你的贡献以 GPL-3.0-or-later 授权（与仓库一致）。若你无法接受 copyleft，请先别提交。
