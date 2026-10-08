# 贡献指南

感谢你愿意为 Eztools 花时间。这份文档说明**怎么构建、怎么验证、提交要满足什么**。

---

## 环境要求

| 依赖 | 版本 | 说明 |
|---|---|---|
| **.NET SDK** | **10.x** | 唯一硬依赖。构建目标是 `net10.0` |
| Python | 3.11+ | **仅开发期需要**（跑验收脚本）；运行Eztools 不需要预装 Python |

> ⚠️ 机器上可能同时存在旧版 `dotnet`（如7.x）。`ezt.exe` 是**框架依赖**应用，
> 且 apphost **不扫 `PATH`** —— 只看 `DOTNET_ROOT` 与注册表默认安装位置。
> 脚本已内置探测与版本校验，但你若装了多个版本，请显式指定：
> ```bash
> DOTNET_ROOT=/path/to/dotnet10 bash scripts/acceptance.sh
> ```

---

## 构建

```bash
dotnet build Eztools.sln
```

产物：`src/Eztools.Cli/bin/Debug/net10.0-windows10.0.19041.0/ezt.exe`
（输出目录带 Windows 平台后缀是正常的 —— Cli 依赖 `net10.0-windows` 的 Win32 API。）

> **构建结果依赖 SDK 版本**：`Directory.Build.props`刻意不固定 `LangVersion`，
> 跟随 TFM（net10.0 → C# 14）。这是必要的 —— SDK 自带的源码生成器
> （如 `System.Text.RegularExpressions.Generator`）产出可能要求更新的语言版本，
> 钉死旧值会让构建在 `obj/` 下报出看起来像"自己代码有问题"的错。
> 换 SDK 版本后请重跑 `ezt selftest`。

---

## 验证：四层，缺一不可

改动落地前按下面顺序跑。**判据一律是「FAIL=0」**，不是"看起来没报错"。

| 层 | 命令 | 覆盖 | 何时必跑 |
|---|---|---|---|
| **1. 自检** | `ezt selftest` | 336 项端到端断言 | 任何改动 |
| **2. 守卫** | `python scripts/review-guards.py` | G1~G8 静态规则 | 任何改动 |
| **3. 文档** | `python scripts/check-doc-refs.py --strict` | 文档交叉引用 | 改了 `docs/` 或 README |
| **4. 验收** | `bash scripts/acceptance.sh` | 541 项功能验收 | 改了 `src/` |
|4b. 桌面 | `python scripts/verify-desktop.py --repo .` | 274 项托盘/面板 | 改了 Desktop 层 |

```bash
# 完整序列
dotnet build Eztools.sln
E="src/Eztools.Cli/bin/Debug/net10.0-windows10.0.19041.0/ezt.exe"
$E selftest
python scripts/review-guards.py
python scripts/check-doc-refs.py --strict
bash scripts/acceptance.sh
python scripts/verify-desktop.py --repo .
```

> 断言总数会随功能增长（见 `CHANGELOG.md`）。**热键被其他程序占用时会自动跳过并计数**，
> 判据是「环境项扣除后 FAIL=0」，不是要求绝对数满额。

---

## 静态守卫（G1~G8）

`scripts/review-guards.py` 挂在验收第 20 步。改动引入下列模式会被拦下：

| 规则 | 拦什么 |
|---|---|
| **G1** | 空 `catch` 块（必须写 `// review-guards:allow-empty-catch :: <理由>`） |
| **G2** | 字符串 `switch` 缺 `default` |
| **G3** | 验收脚本里的禁用模式（如 `cmd \| grep -q`、`A && B \|\| C && D`） |
| **G4** | 幽灵代码（死代码/ 永不可达分支） |
| **G5** | 多TFM 残留目录（死产物） |
| **G6** | 测试被跳过但未计数 |
| **G7** | 分层违规（见下） |
| **G8** | 生产接线缺失（符号定义了但没人调用） |

自测：`python scripts/review-guards.py --selftest`。

### 分层硬约束（G7）

- **纯逻辑 → `Host{net10.0}`**（Cli selftest 可直接测，且编译器强制它碰不到 Win32）
- **Win32 调用 → `Desktop{net10.0-windows}`**（避开 CA1416跨平台分析器误报）

评审任何新代码时先问：**这个依赖类型住哪个 TFM？**

---

## 提交规范

```
<类型>: <一句话概括>

<正文：为什么这样改、有什么影响、怎么验证的>
```

类型用：`feat` / `fix` / `docs` / `chore` / `refactor` / `test`。

**写好正文**。这个项目的历史提交（如 `W11 索引范围与排除规则全波落地（代码/守卫/验收脚本）`）
是往后追溯设计决策的依据 —— 半年后你自己会需要它。

---

## 代码纪律（几条踩过坑的）

1. **新代码"没生效"先查DLL 时间戳**，再 `--no-incremental` 重编。
2. **断言必须落在数字或退出码上**。断言写"看起来在守"，其实恒真，等于没守。
3. **别写"被上游结构保证"的断言** —— 那种断言恒真。
4. **静默类断言必须配正向对照**：单看"某文件没变"是空断言（可能只是没执行），
   必须另有"前置动作确实发生"的独立旁证。
5. **复用前先确认 TFM**。`Host` 不能引用 Win32 类型 —— 编译器会拦，但别去试探。
6. **新增协议字段要两层都测**（协议层 + `ezt <cmd> --json`）。
7. **原生资源用 `try/finally`**（`AllocHGlobal`、句柄）。
8. **源码是 UTF-8 无 BOM**。`Directory.Build.props` 已设 `CodePage=65001`，别删。
9. **别把本机路径写死进脚本**。用 `${VAR:-}` + 探测 + 可覆盖的环境变量。
10. **vendor 第三方代码必须登记 `THIRD-PARTY-NOTICES.md`**（GPL 义务，见该文件）。

---

## 文档约定

- `docs/README.md` 是**全项目文档的唯一索引**，先读它再读别的。
- 设计决策 → `docs/`；日常使用 → `README.md`。
- **改了波次方案要同步更新对应手工验收清单**。
- 文档里的路径引用会被 `check-doc-refs.py` 校验；写"将新建"的路径时
  用 `（新建）`**前缀**标记（脚本按行内任意位置扫路径，不认语义）。

---

## 许可

贡献即表示你同意你的贡献以 **GPL-3.0-or-later** 授权（与仓库一致）。
若你无法接受 copyleft，请先别提交。