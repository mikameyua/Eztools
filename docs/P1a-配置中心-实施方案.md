# P1a 配置中心 · 实施方案（待批）

> **状态**：✅ **已实施完成**（2026-09-19）。落地记录见 `docs/Eztools-设计方案.md` §16。
> 下面保留原始方案，作为"当初打算怎么做"的记录 —— 与实现的差异见 §16.5（两个坑）。
> **范围**：只做配置中心（宿主逻辑 + CLI），**不含任何 UI 代码**（UI 栈待定，见 §7）。
> **依据**：设计方案 §6.2（配置中心）、§7（schema 驱动 UI 的渲染映射表）、§14.7（P1 入口）。

---

## 0. 一句话

把 `%APPDATA%\Eztools\config\<toolId>.json` 这条链补全：
**清单声明 → 默认值合并 → 校验 → 原子落盘 → 注入工具进程 → 变更推送**。

---

## 1. 现状盘点：接口都留好了，中间那段从没写

| 环节 | 现状 | 位置 |
|---|---|---|
| 清单声明 `config` | ✅ 已解析为 `manifest.ConfigSchema`，并校验 `type: "object"` | `ManifestParser.cs:234-249` |
| 配置目录 | ✅ `%APPDATA%\Eztools\config\`，已纳入 `EnsureDirectories` | `EztoolsPaths.cs:63,127` |
| 配置文件路径 | ✅ `ToolConfigFile(toolId)` | `EztoolsPaths.cs:89` |
| 注入工具进程 | ⚠️ **有位置但没人填**：`["config"] = options.Config?.Clone() ?? new JsonObject()` | `ToolProcess.cs:161` |
| 工具侧接收 | ✅ SDK `self.config` + `__on_config_changed__` 钩子 | `tool.py:246,267-270` |
| 变更推送协议 | ⚠️ 方法名已定义（`tool.onConfigChanged`），**宿主侧从不发送** | `ProtocolMethods.cs:11` |
| **读写落盘** | ❌ 完全缺失 —— `ToolConfigFile` 全代码零引用 | — |
| **schema 校验** | ❌ 缺失（只有"是不是 object"这一条） | — |
| **CLI 命令** | ❌ 缺失 | — |

**结论**：`options.Config` 永远是空的，所以工具现在拿到的 `tool.config` 恒为 `{}`
——即使 `tool.json` 里写了 `default`。这一条是 P1a 要修的核心。

---

## 2. 交付物（四块）

### 2.1 `Eztools.Contracts`：schema 子集校验器

新增 `ConfigSchemaValidator.cs`、`ConfigValueCoercion.cs`。

| 支持的 `type` | 对应设置控件（§7） |
|---|---|
| `boolean` | 开关 |
| `string` | 单行输入框 |
| `string` + `enum` | 下拉选择 |
| `string` + `format: directory` / `file` | 路径选择器（P1a 只校验存在性，不做选择器） |
| `string` + `format: textarea` | 多行输入框 |
| `integer` / `number` | 数字输入框 |
| `array` | 列表编辑器 |
| `object` | 分组折叠区 |

支持的约束键：`type` `enum` `default` `minimum` `maximum` `title` `description`
`x-order` `required`。

**明确不支持**（超出设置页渲染所需，不要顺手实现）：`$ref` / `oneOf` / `anyOf` /
`allOf` / `patternProperties` / `if-then-else` / 远程 `$schema` / 正则 `pattern`。

> **校验失败不是"拒绝整个清单"**，而是**该项退回默认值 + 记一条诊断**。
> 理由：用户在设置页填错一个数字，不该导致工具整个打不开。这与 §6.1
> 「清单校验失败不弹窗、只记日志」是同一条原则。

### 2.2 `Eztools.Host`：`ConfigStore`

新增 `src/Eztools.Host/Config/ConfigStore.cs`。

| 方法 | 语义 |
|---|---|
| `Load(toolId)` | 读文件；不存在返回空对象（**不自动创建文件**） |
| `Effective(toolId, schema)` | **默认值 ⊕ 已存值** —— 这才是要注入工具进程的东西 |
| `Set(toolId, key, value)` | 校验 → 合并 → 原子写 |
| `Unset(toolId, key)` | 删键 → 回落默认值 |
| `Reset(toolId)` | 删文件 |
| `Path(toolId)` | 配置文件的绝对路径 |

三条硬要求：

1. **原子写**：写临时文件 → `File.Move(overwrite: true)`。禁止直接覆盖写
   —— 断电/崩溃会留下半个 JSON。
2. **损坏文件绝不静默覆盖**：JSON 解析失败时，把原文件改名成
   `<toolId>.json.corrupt-<yyyyMMddHHmmss>`，用默认值启动，记 Warning。
   **宁可留一份看不懂的文件，也不能把用户配置抹掉。**
3. **并发**：按 toolId 加进程内锁；跨进程（CLI 与将来的 UI 同时改）用文件锁。
   P1a 先做进程内锁 + 写前重读合并，跨进程锁记 TODO。

### 2.3 `Eztools.Host`：注入与推送

- `ToolHostManager.AcquireAsync` 时把 `Effective()` 填进 `ToolProcessOptions.Config`
  —— 补上 `ToolProcess.cs:161` 那个一直空着的位置。
- 新增 `PushConfigAsync(toolId)`：工具进程**活着**才发 `tool.onConfigChanged`。

| 生命周期 | 变更后是否需要推送 |
|---|---|
| `transient`（现在全部） | **不需要** —— 每次调用重起进程，天然拿到新值 |
| `resident`（P2） | **必须** —— 进程一直活着，不推就永远用旧值 |

**这个规则要写进文档和日志**，否则将来会出现"我改了配置怎么不生效"的困惑。
P1a 交付通路，端到端验证要等 P2 有 `resident` 工具才能真正跑到。

### 2.4 `Eztools.Cli`：`ezt config` 命令组

| 命令 | 作用 |
|---|---|
| `ezt config list [toolId]` | 列出有效配置，**并标注每项是"已设置"还是"取自默认值"** |
| `ezt config get <toolId> [key]` | 取有效值 |
| `ezt config set <toolId> <key> <value>` | 校验后写入 |
| `ezt config unset <toolId> <key>` | 删除，回落到默认值 |
| `ezt config reset <toolId>` | 重置该工具全部配置 |
| `ezt config path <toolId>` | 打印配置文件路径（用于手工编辑） |
| `ezt config schema <toolId>` | 打印 schema —— **它就是将来设置页的输入** |

`set` 要按 schema 的 `type` 把字符串转成 `boolean` / `integer` / `number`：
**这是"schema 驱动"的第一次落地**，做对了 P1b 渲染设置页就是复用同一套逻辑。

---

## 3. 验收标准（照 P0 的习惯，先写出来才开工）

沿用 `scripts/acceptance.sh` 的路子，新增一组用例：

| # | 验什么 |
|---|---|
| 1 | `set` 合法值 → 落盘，`get` 读回一致 |
| 2 | `set` 非法值（超 `minimum` / 不在 `enum` / 类型不符）→ **拒绝 + 可读原因**，且**文件未被改动** |
| 3 | 不设任何值 → 工具拿到的是 schema 里的 `default`（**这条直接修掉 §1 那个"恒为空对象"的缺口**） |
| 4 | 改配置 → 重新调用工具 → 工具侧 `tool.config` 是新值（**端到端，含进程重启**） |
| 5 | 手工把配置文件改坏 → 宿主**不崩、不覆盖**，备份为 `.corrupt-*`，用默认值继续 |
| 6 | 删掉工具目录，配置仍在；工具放回，配置生效（验证"配置与代码分离"） |
| 7 | 中文 / emoji 值往返无损（**用 `byteLength` 一类数字对账，不靠肉眼看**） |
| 8 | `config path` 打印的路径真实存在且可写 |

---

## 4. 明确不做（划清边界）

| 不做 | 原因 |
|---|---|
| 设置 UI 渲染 | 那是 P1b，且 **UI 栈待定**（见 §7） |
| 配置的版本迁移（schema 变了怎么办） | 现在没有使用者，先记 TODO；等真的改过 schema 再设计 |
| 敏感值加密 | 见 §5 决策 ② |
| 远程 / 云配置（§1.3 预留扩展点） | 无需求 |
| 正则 `pattern` 校验 | 设置页不需要；引入正则就得处理超时与 ReDoS |

---

## 5. 要拍板的三个点

### ① 工具私有 KV（`host.storage.*`）要不要并进配置中心？

现在两套并存：

| | 用户配置 | 工具私有 KV |
|---|---|---|
| 路径 | `%APPDATA%\Eztools\config\<id>.json` | `%LOCALAPPDATA%\Eztools\toolsdata\<id>\storage.json` |
| 谁写 | **用户**（将来的设置页） | **工具自己** |
| 有没有 schema | 有 | 没有 |
| 用户该不该看见 | 该 | 不该 |

设计方案 §7 把"工具私有 KV 收归配置中心"列在 P1。

**我的建议：不并。** 理由：合成一个文件后，"用户点重置" 会把工具的内部状态一并清掉
（比如某个工具缓存的索引位置），这不是用户预期。

顺带说明：**现在的位置分法是合理的，别改** —— 配置放 `%APPDATA%`（随用户漫游，
域环境下换机器带着走），工具数据放 `%LOCALAPPDATA%`（缓存类，不该漫游）。
这个分法是对的。

| 选项 | 说明 |
|---|---|
| **A（建议）** | 保持两套，各自独立；配置中心只管有 schema 的那部分 |
| B | 合并成一个文件（两个顶层分组 `config` / `state` 隔离） |
| C | 先不动，P1a 完全不碰 KV |

### ② 配置里能不能放敏感值（API key）？

AI 处理类工具迟早需要。

| 选项 | 说明 |
|---|---|
| A 明文放 Roaming | 最简单；域漫游会同步走，有泄露面 |
| B 明文放 Local | 不漫游；但和"配置随用户走"的定位冲突 |
| **C（建议）** | P1a 只明文落盘 **+ 在 schema 上加 `"x-secret": true` 标记（先只标记不实现加密）**；加密留给 P1b 和 UI 一起做 |

选 C 的理由：**加密了就必须有 UI 才能改**（命令行里敲明文会进历史记录），
而 P1a 阶段还没有 UI。先加标记，等 UI 到位再补 DPAPI（`ProtectedData`，Windows 原生、零依赖）。

### ③ 配置变更要不要立即生效？

已在 §2.3 给出规则（transient 不用推、resident 必须推）。
**P1a 只交付通路并按此规则接上**，真实验证等 P2。

---

## 6. 实施顺序（按依赖，不是按时间）

| 步 | 内容 | 依赖 |
|---|---|---|
| 1 | `Contracts`：schema 校验器 + 类型归一化 | 无 |
| 2 | `Host`：`ConfigStore`（读/合并/原子写/损坏恢复） | 1 |
| 3 | `Host`：注入 `tool.start` + `PushConfigAsync` | 2 |
| 4 | `Cli`：`ezt config` 命令组 | 2、3 |
| 5 | 验收用例 + `acceptance.sh` 扩项 + 验证手册加章节 | 4 |
| 6 | 文档同步（设计方案 §6.2 / §14 追加、README） | 5 |

---

## 7. 与 UI 的关系（说清楚边界，免得踩到待定的决策）

P1a **不写任何 UI 代码**，但它的产物正是 P1b 的输入：

| P1a 交付 | P1b 怎么用 |
|---|---|
| `ezt config schema <toolId>` | 设置页读的就是这份 schema |
| §7 的 type → 控件映射表 | 原样复用，**框架无关** |
| `Set/Unset/Reset` 的校验与落盘 | 设置页的"确定/应用"按钮后面调的就是这几个方法 |
| `x-order` / `title` / `description` | 字段排序、标签、帮助文本 |

也就是说：**UI 栈不管最后定 WinUI 还是 WPF，P1a 都一个字不用改。**
这就是把 P1a 从 P1 里拆出来的价值。

---

## 8. 风险

| 风险 | 说明 | 应对 |
|---|---|---|
| schema 子集划得太大 | 顺手实现 `oneOf`/`$ref` 会让校验器变成一个大工程 | §2.1 已列明不支持的键，**评审时请对照这份清单砍** |
| "改了不生效" | transient 不推送是设计如此，但用户会觉得是 bug | §2.3 的规则要写进 `ezt config set` 的输出提示 |
| 配置文件被用户手改坏 | 手工编辑是 CLI 阶段的正常用法 | §2.2 的 `.corrupt-*` 备份机制 |
| 与 P0 的 `host.storage` 语义混淆 | 两个"存储"容易混 | 文档统一措辞：**配置（config，用户改）** vs **私有数据（storage，工具改）** |
