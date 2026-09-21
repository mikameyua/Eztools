# P4 Wave 2c 面板协议规范（`panels` + `weight: full` 增量）

> **地位**：Wave 2c 的**协议契约**。按项目常驻规则——**先定协议再写码**——
> 本文是 `panels` 实现的唯一权威口径，代码必须与本文一致。
>
> **起笔**：2026-09-21。**决策依据**：D3（2026-09-21 拍板）= **原生 WPF 面板**。
> **状态**：✅ **已实施完成（2026-09-21）** —— 契约层 / SDK / 宿主 / CLI / WPF / 托盘入口全部落地，
> `_step15_w2c.sh` 24 条 + `verify-desktop.py` §1c 10 条断言全绿。落地记录见设计方案 §25。
> **上游**：`docs/Eztools-设计方案.md` §4.5（贡献点表）· §5.1（weight 三档）· §15.3（轻量化）
> **下游**：落地记录进设计方案 §25；验收进 `scripts/acceptance.sh` §15。

---

## 0. 为什么必须先写这份文档

`panels` 是 P4 里**最后一个"文档有、代码零命中"**的贡献点。此前的教训（见 §4.5 的"状态列是硬要求"）：

> 读文档的人会把"写在表里"当成"已经能用"。

所以本协议的目标不是描述一个理想设计，而是**把每个字段、每条拒绝理由、每个边界都钉死**，
让实现无自由发挥空间。**凡是本文没写的，一律不做。**

---

## 1. 范围界定（先说清做什么、不做什么）

### 1.1 做什么

| # | 交付物 | 说明 |
|---|---|---|
| 1 | `contributes.panels[]` 清单字段 | 工具声明"我有一个面板"，含 id / title / size / commands |
| 2 | 契约层模型 + 解析 + 校验 | `ToolPanel` record + `ManifestParser` 分支 + 诊断码 |
| 3 | 宿主 → 工具 的**面板数据通道** | `tool.panel.data` 请求；工具返回 `{nodes:[...]}` |
| 4 | 工具的**面板动作**通道 | 面板上的控件点击 → 复用**已有的** `tool.invoke`（不新增方法） |
| 5 | 宿主侧 WPF 渲染器 | `PanelWindow`：把 `nodes[]` 渲染成 WPF 控件树 |
| 6 | 入口 | 托盘菜单「面板」子菜单 + `ezt panel <toolId> [panelId]` 命令行 |
| 7 | `weight: full` 一致性闸门 | `panels` 仅在 `weight: full` 下可用 |

### 1.2 **明确不做**（写在这里是为了防止范围蔓延）

| 不做 | 理由 |
|---|---|
| ❌ 工具自带 HTML/WPF 代码作为面板 | 那等于让工具进程往宿主进程注入 UI —— 与"工具进程隔离"的架构前提冲突。**声明式 + 宿主渲染**是唯一路径 |
| ❌ WebView2 / 内嵌浏览器 | D3 已否决：§15.3 实测 WebView 会带来 50~100 MB，**会推翻 D1 的 220 MB 预算** |
| ❌ 面板 ↔ 工具的**实时双向**推送 | Wave 2c 只做**拉取式刷新**（打开时拉 + 用户点刷新 + 宿主事件触发）。推送需要工具侧长连接语义，属未来增量 |
| ❌ 工具 → 宿主 主动要求"重绘我的面板" | 同上。V1 靠"面板窗口每 N 秒/每次聚焦时重新拉"解决 |
| ❌ 面板自带的样式/主题/布局自由 | V1 用宿主统一间距与主题；工具只能选"控件种类"，不能控制像素 |
| ❌ `weight: lite` 的工具开面板 | 只有 `full` 档可声明 `panels`（见 §6） |
| ❌ `search` 贡献点 | 与本协议无关，仍标"未实现·未排期" |

> **一句话**：V1 的面板 = **一个由工具数据驱动、由宿主渲染的只读数据视图 + 按钮**。
> 不是"工具把 UI 塞进宿主"，也不是"宿主给工具一块画布"。

---

## 2. 清单声明（`contributes.panels`）

### 2.1 形状

```jsonc
{
  "id": "mytool",
  "weight": "full",                    // ★ 前置：非 full 一律不可用（§6）
  "needs": ["ui.panel"],               // ★ 声明面板能力（§4.4 收敛集内）
  "contributes": {
    "commands": [
      { "id": "mytool.refresh", "title": "刷新", "handler": "refresh" },
      { "id": "mytool.clear",   "title": "清空", "handler": "clear" }
    ],
    "panels": [
      {
        "id": "main",                  // 面板 id，工具内唯一，[a-z0-9-]
        "title": "我的面板",            // 窗口标题（也用于托盘菜单项文字）
        "description": "查看当前状态",   // 可选，展示在窗口副标题
        "width": 640,                  // 可选，默认 640，钳制 [320, 1600]
        "height": 480,                 // 可选，默认 480，钳制 [240, 1200]
        "refreshMs": 0                 // 可选，0 = 不自动刷新；>0 时每 N ms 重拉一次，钳制 [500, 60000]
      }
    ]
  }
}
```

### 2.2 字段表（**全部字段，无遗漏**）

| 字段 | 类型 | 必填 | 默认 | 校验规则 | 违反时的诊断 |
|---|---|---|---|---|---|
| `panels` | array | 否 | 无面板 | 必须为数组；非数组 → 忽略整个字段 | `contributes.panels-not-array`（Warning） |
| `panels[].id` | string | **是** | — | `^[a-z0-9][a-z0-9-]*$`，≤64，面板内唯一 | 缺失 → `contributes.panel-missing-id`（**Error**） |
| `panels[].title` | string | 否 | 取 `id` | 非空字符串，≤128 | — |
| `panels[].description` | string | 否 | null | 自由文本 | — |
| `panels[].width` | int | 否 | 640 | 钳制到 `[320, 1600]`，越界 → 钳制并 Warning | `contributes.panel-size-out-of-range`（Warning） |
| `panels[].height` | int | 否 | 480 | 钳制到 `[240, 1200]` | 同上 |
| `panels[].refreshMs` | int | 否 | 0 | 0 或 `[500, 60000]`；其他值 → 归 0 并 Warning | `contributes.panel-refresh-out-of-range`（Warning） |

**为什么 `id` 缺失是 Error 而其他是 Warning**：`id` 缺失意味着宿主无法路由数据请求
（`tool.panel.data` 的 `panelId` 指谁？）——这是**结构性缺陷**，该面板根本无法工作。
其余字段都有合理默认值，降级可用。

### 2.3 重复 id

同一工具内 `panels[].id` 重复 → 后者被忽略 + Warning（码 `contributes.panel-duplicate`）。
**与 `commands` 的重复处理同口径**（`contributes.command-duplicate`）。

### 2.4 命令引用校验

`panels[].commands[]` 若声明了，则其中每个 id 必须在本工具的 `contributes.commands` 里存在，
否则 Warning（码 `contributes.panel-unknown-command`）——**与 `menus` / `hotkeys` 同口径**
（见 `ManifestParser` 现有的 `MenuUnknownCommand` / `HotkeyUnknownCommand`）。

> **设计理由**：面板上的按钮点击后走的就是 `tool.invoke`。若引用的命令不存在，
> 用户会看到"按钮能点、点了报未知命令"——这是最差的失败模式（能显示但不可用），
> 必须在加载期就警告。

---

## 3. 数据通道：`tool.panel.data`（**新增协议方法**）

### 3.1 方向与方法名

| 项 | 值 |
|---|---|
| 方向 | **宿主 → 工具**（请求，工具必须回复） |
| 方法名 | `tool.panel.data` |
| 常量 | C# `ProtocolMethods.ToolPanelData`；Python `protocol.TOOL_PANEL_DATA` |

> ⚠️ 契约层**两侧必须只有一份定义**（`ProtocolMethods.cs` 与 `protocol.py`）——
> 写错方法名会导致"面板一直是空的"这类难查症状。

### 3.2 请求参数

```json
{
  "toolId": "mytool",
  "panelId": "main",
  "context": {
    "reason": "open"          // open | refresh | host-event
  }
}
```

| 字段 | 说明 |
|---|---|
| `panelId` | 要哪个面板的数据（对应 §2 的 id） |
| `context.reason` | 为什么拉：`open`（窗口刚打开）/ `refresh`（用户点刷新或定时）/ `host-event`（宿主状态变了，如工具被启用） |

### 3.3 返回载荷（**核心 schema**）

```json
{
  "title": "我的面板",                    // 可选：覆盖清单里的 title（动态标题）
  "nodes": [                             // ★ 必填：节点数组，可为空数组
    { "type": "heading", "text": "当前状态" },
    { "type": "text",    "text": "共 3 个文件待处理" },
    { "type": "kv",      "items": [["路径", "D:\\tmp"], ["大小", "12 MB"]] },
    { "type": "list",    "items": ["a.txt", "b.txt", "c.txt"] },
    { "type": "buttons", "items": [
        { "label": "刷新", "commandId": "mytool.refresh" },
        { "label": "清空", "commandId": "mytool.clear", "style": "danger" }
    ]},
    { "type": "separator" }
  ]
}
```

### 3.4 节点类型表（**穷举，V1 只有这 6 种**）

| `type` | 必需字段 | 可选字段 | 渲染为 |
|---|---|---|---|
| `heading` | `text` | — | 加粗大字（FontSize 16，SemiBold） |
| `text` | `text` | `wrap`（默认 true） | 普通文本段落 |
| `kv` | `items`（`[[k, v], ...]`） | — | 两列键值表（左列灰、右列深） |
| `list` | `items`（`[...]`） | — | 项目符号列表 |
| `buttons` | `items`（`[{label, commandId}]`） | `style`：`default` \| `danger` | 横排按钮，点击 → `tool.invoke` |
| `separator` | — | — | 一条水平分隔线 |

**未知 `type` 的处理**：**跳过该节点并渲染为一条灰色提示**（"⚠ 未知节点类型 xxx（宿主版本过旧？）"）。
**不报错、不白屏** —— 与"清单未知字段一律忽略"的前向兼容原则一致。

> **为什么不给节点加 id / 事件绑定**：V1 是**整体重绘**模型——工具每次返回完整 `nodes[]`，
> 宿主清空重画。局部更新需要 diff 语义与节点标识，那是 V2 的事。
> 这个取舍换来的是**工具侧零状态**：工具每次只需回答"当前该显示什么"，不需要记住上次画了什么。

### 3.5 校验与容错（工具侧返回不规范时）

| 情形 | 宿主行为 |
|---|---|
| 返回非对象 / `nodes` 非数组 | 显示错误面板：「工具返回格式不正确」+ 原始返回的前 500 字符（截断） |
| `nodes` 为空数组 | 渲染「（此面板当前无内容）」 |
| 单个节点字段缺失（如 `heading` 无 `text`） | 跳过该节点 |
| 单个节点字段类型不符 | 跳过该节点 |
| 返回超时 | 显示超时提示 + 重新拉取按钮（超时沿用 `weight: full` 的 120s 默认，可用 `timeoutMs` 覆盖） |
| 工具崩溃 / 已禁用 | 显示对应原因（复用现有异常文案） |

> **总原则**：**面板错误不弹对话框、不中断宿主**（与清单诊断同一条设计约束）。

---

## 4. 动作通道：面板按钮 → `tool.invoke`（**复用，不新增方法**）

### 4.1 语义

`buttons` 节点的 `commandId` 指向**本工具已声明的命令**。点击时宿主：

1. 调 `InvokeCommandAsync(commandId, args)` —— **走的是与托盘菜单完全相同的通路**；
2. `args` 传 `{ "panelId": "<当前面板 id>" }`（让工具知道自己是从哪个面板点过来的）；
3. **等返回**（按钮进入禁用态）；
4. 完成后**自动重拉面板数据**（`reason: "refresh"`）—— 这是 V1 的"局部刷新替代品"。

### 4.2 为什么复用 `tool.invoke` 而不是新增 `tool.panel.action`

- **命令已经是一等公民**：有 id、有 handler、有超时、有 `weight` 闸门、有环检测。
  新增一套 panal 专用的动作方法 = 把这五样各复制一份（且都要再测一遍）。
- **工具侧零新概念**：面板按钮就是"带个 panelId 参数的普通命令"，SDK 不用加 API。
- **环检测天然成立**：`tool.invoke` 已在既有链里（见 P4 §2.2），新通道会被同一套逻辑覆盖。

### 4.3 危险操作

`style: "danger"` 只影响**按钮配色**（红），**不加二次确认**。
确认语义属工具自己的 handler 职责（工具可以返回"需要确认"的节点再让用户点第二次）。
**宿主不为工具的业务判断兜底。**

---

## 5. 宿主侧渲染（原生 WPF）

### 5.1 窗口

| 项 | 结论 |
|---|---|
| 载体 | 新增 `src/Eztools.Desktop/PanelWindow.cs`（`Window`，与 `SettingsWindow` 同构） |
| 尺寸 | 取清单 `width`/`height`，用户可拖拽调整（不持久化，V1） |
| 标题 | `"{工具名} — {面板 title}"` |
| 位置 | `WindowStartupLocation.CenterScreen` |
| 主题 | 复用 `SettingsWindow.ApplyModernTheme()`（同样的 iNKORE 主题初始化） |
| 刷新按钮 | 窗口右上角一个「刷新」按钮（手动 `reason: "refresh"`） |
| 状态栏 | 底部一行：`上次刷新 HH:mm:ss · 耗时 Nms` |

### 5.2 多面板

一个工具有多个面板 → 托盘「面板」子菜单里每一项对应一个窗口。
**同一面板重复打开**：复用已开窗口（激活到前台 + 立即重拉），**不新开**。

### 5.3 线程与阻塞（**本项目踩过两次的区域，特别说明**）

- 拉数据是 `await` 异步（`weight: full` 超时可达 120s），**绝不在 UI 线程同步等**（见 P2 托盘 `InvokeAsync` 的注释）。
- 定时刷新用 `DispatcherTimer`（WPF，跑在 UI 线程），回调里 `await` 拉数据。
- 面板窗口关闭时**必须停掉定时器并退订宿主事件**（Wave 2a 的 `Subscribe` 返回 `IDisposable`）。

### 5.4 与事件总线的关系（Wave 2a 的消费方）

面板窗口订阅 `HostEventBus`，收到 `ToolEnabledChanged` / `ToolStopped` / `RegistryReloaded` 时：
- 若事件 `ToolId` == 本面板的工具 → 用 `reason: "host-event"` 重拉；
- 否则忽略。

> 这是 Wave 2a 事件总线的**第二个真实消费方**（第一个是托盘的气泡与 Resident 区段刷新）。
> Wave 2a 当初的"不做开放总线"决策在这里得到验证：**内部事件刚好够用**。

---

## 6. `weight: full` 的一致性闸门

### 6.1 规则

| 规则 | 违反时 |
|---|---|
| 声明了 `panels` 但 `weight != full` | **Error**：`contributes.panels-require-full`。理由：`weight` 决定进程模型，而面板需要常驻可复用的进程 + 长超时 |
| `weight: full` 但未声明 `needs: ["ui.panel"]` | **Warning**：`needs.panel-without-ui-panel`（漏了声明的能力提示，不阻塞） |
| `weight: full` 的工具未实现 `tool.panel.data` handler | **运行时**错误（非加载期）：打开面板时报"工具未实现 panel 数据接口"，错误码 `MethodNotFound` |

> **为什么是 Error 不是自动升档**：`weight` 是**作者对进程模型的显式选择**（含超时、依赖策略），
> 宿主静默改写它 = 让工具以作者没预期的形态运行（`script` 档的"用完即退"语义会消失）。
> 与"写了但不认识的 weight 回退 + Warning"不同——那是**拼写错误**，这是**能力组合矛盾**。

### 6.2 `weight: full` 的其余增量（本次一并落地）

`full` 档此前只有枚举值与超时，无任何独占特性。本次给它**两条**真实差异：

| 能力 | script | lite | full |
|---|---|---|---|
| `host.log` / `host.notify` | ✅ | ✅ | ✅ |
| `host.storage.*` / `host.primitive.call` / `host.invokeTool` / `host.progress` | ❌ | ✅ | ✅ |
| `panels` 贡献点 | ❌ | ❌ | ✅ |
| `tool.panel.data` 方法 | ❌ | ❌ | ✅ |
| 默认超时 | 10s | 30s | 120s |

> **注**：`tool.panel.data` 是**宿主 → 工具**方向，不是"工具调用的宿主 API"，
> 所以它不走 `EnsureApiAllowed` 白名单（那条闸门管的是工具→宿主）。
> 但宿主在**发起** `tool.panel.data` 前要检查档位——**闸门在发起侧，不在处理侧**。

---

## 7. 入口

### 7.1 托盘

`TrayMenuBuilder` 在工具分组内追加「面板」子菜单（仅当该工具有 panels）：

```
工具分组
├── 命令 A
├── 命令 B
└── 面板            ← 新增（仅当 panels 非空）
    ├── 我的面板
    └── 状态面板
```

工具**只有面板没有菜单**时，也该出现（否则面板无处可达）。

### 7.2 命令行

```bash
ezt panel                      # 列出所有工具的面板（toolId.panelId + title）
ezt panel mytool               # 列出该工具的面板
ezt panel mytool main          # 拉取并打印面板数据（JSON，便于脚本断言）
ezt panel mytool main --text   # 人类可读渲染（缩进树）
```

**为什么 CLI 要有**：验收脚本需要一个**不依赖 GUI** 的断言入口。
`ezt panel <toolId> <panelId>` 直接走 `tool.panel.data`，与 WPF 窗口读的是同一条通道 ——
这样"面板数据链路"可以进自动化，只有"WPF 真的画出来了"需要 GUI 验证（用 `--selfcheck` 模式）。

---

## 8. 工具侧 SDK

### 8.1 handler 注册

```python
from eztools import Tool

tool = Tool()

@tool.handler("panel_data")          # 名字固定为 panel_data（宿主按 handler 名路由）
def panel_data(args):
    panel_id = args.get("panelId")
    if panel_id == "main":
        return {"nodes": [
            {"type": "heading", "text": "当前状态"},
            {"type": "text", "text": f"共 {count()} 个文件待处理"},
            {"type": "buttons", "items": [
                {"label": "刷新", "commandId": "mytool.refresh"},
            ]},
        ]}
    return {"nodes": []}
```

**handler 名固定为 `panel_data`**（不是每个面板一个 handler）。
理由：面板 id 已在 `args.panelId` 里，一个 handler 用 `if/switch` 分派即可；
每个面板一个 handler 会让 `tool.json` 复杂化，而收益只是省一次分支。

### 8.2 SDK 改动

- `protocol.py`：加 `TOOL_PANEL_DATA = "tool.panel.data"`。
- `tool.py`：`_dispatch` 加一个分支 → `self._handlers.get("panel_data", 默认实现)`。
  默认实现返回 `{"nodes": []}`（**不报错**）——与 `tool.recover` 的"明确告知未实现"不同，
  因为空面板是合法状态（工具确实没东西可显示）。

---

## 9. 验收口径（将落地为 `scripts/_step15_w2c.sh`）

**判据设计要求**（依据 `docs/验收断言审视清单.md`：判据 = "改成假值测试还会不会过"）：

| # | 断言 | 反例（改成假值必须失败） |
|---|---|---|
| 15.1 | `ezt panel` 列出面板，且**输出里含具体 panelId** | 若实现返回空列表 → 失败 |
| 15.2 | `ezt panel probe main` 返回的 JSON `nodes` **长度 > 0** | 若返回空数组 → 失败 |
| 15.3 | 返回的 `nodes[0].type` **等于期望的具体值**（如 `heading`） | 若渲染层把 type 丢掉 → 失败 |
| 15.4 | 含 `buttons` 节点时，其 `commandId` **能解析到已声明命令** | 若引用了不存在的命令 → 失败 |
| 15.5 | `weight: lite` 的工具声明 `panels` → **加载期 Error** + 断言诊断码字符串 | 若静默接受 → 失败 |
| 15.6 | `weight: full` 且声明 panels 的工具 → **无 Error 诊断** | 若被误判 → 失败 |
| 15.7 | 未知 `type` 的节点 → **被跳过但面板整体仍渲染**（返回 ok，节点数少 1） | 若整包失败 → 失败 |
| 15.8 | `panels[].id` 缺失 → 诊断码 `contributes.panel-missing-id` | — |
| 15.9 | `width` 越界（如 99999）→ **被钳制到 1600** 且出 Warning | 若原样透传 → 失败 |
| 15.10 | 工具返回非对象 → 宿主报告格式错误但**不崩溃**（退出码仍 0） | 若抛异常 → 失败 |
| 15.11 | `refreshMs` 越界（如 10）→ 归 0 且出 Warning | — |
| 15.12 | 面板按钮走 `tool.invoke`：同 `commandId` 的命令**能被真实调用并返回结果** | 若只冒泡不调用 → 失败 |

> **不写"面板窗口能打开"这类断言**：它需要 GUI 会话，进不了 `acceptance.sh`。
> 改由 `--selfcheck` 覆盖：构造窗口**不显示**、检查控件树节点数与预期一致后退出。
>
> **落地情况（2026-09-21）**：15.1~15.12 全部落地在 `scripts/_step15_w2c.sh`（**24 条断言**，
> 因为部分条目拆成了"正例 + 对照"两半，例如 15.5/15.6 的档位闸门、15.9/15.11 的钳制与归一化）。
> GUI 侧由 `scripts/verify-desktop.py` 的 §1c 覆盖（**10 条**）：`PanelWindow.InventoryForSelfCheck`
> 真拉一次 `tool.panel.data` 并装配控件树，断言 Button=2 / Separator=1 / Grid=1 / WrapPanel=1 /
> 尺寸取自清单（720×520 而非默认 640×480）/ 空面板有提示 / 未知节点跳过但保住其余 /
> 非对象载荷渲染成提示而非崩溃。
>
> **踩到的两个坑（值得记下来）**：
> ① `panels[].id` 缺失在实现里曾被路由到 `Warn` 委托而非 `Error` —— 诊断码对、**严重级别错**，
>   而级别决定 `doctor` 的 `healthy` 判定。`ParsePanels` 原先只接收 `Action<string,string> warn`，
>   没有 Error 出口 ⇒ 协议 §2.2 的"必须 Error"被静默降级。**教训**：解析器只暴露一半的诊断通道时，
>   协议里写"Error"的地方会自然退化成 Warning，且没有任何编译期或测试期信号。
> ② 自检的控件计数最初走 `VisualTreeHelper`，把模板展开的内部零件也数进去（2 个按钮数成 8 个），
>   且未 `Show()` 的窗口根本没有可视化树。改走**逻辑树**后计数才等于"我放了什么"。

---

## 10. 轻量化核算（§15.3 硬约束）

| 项 | 增量 | 说明 |
|---|---|---|
| `PanelWindow.cs` | ~1.5 KB 源码 | 纯 WPF，**零新依赖** |
| 契约层 `ToolPanel` | ~1 KB | record + 解析分支 |
| 二进制 | **≈ 0** | 只用已引用的 WPF 程序集（`PresentationFramework` 等已在） |
| 运行时内存 | 一个面板窗口 ≈ 8~15 MB | **仅打开时**，关闭即释放 |

**对照 D1 预算**：§15.4 的口径上限是 **220 MB**（自包含 182 MB + ~20% 余量），
本增量在 **1 MB 以内**，**不触碰预算**。
这也是 D3 选"原生 WPF"而非"WebView"的直接兑现 —— WebView 会加 50~100 MB。

---

## 11. 已知限制（诚实清单）

1. **只读 + 按钮**：V1 不支持输入框、下拉框、复选框等交互控件（工具想要输入 → 用命令 + 配置 schema）。
2. **整体重绘**：无 diff、无局部更新、滚动位置在刷新后会重置。
3. **无双向推送**：工具不能主动让面板刷新（靠定时 `refreshMs` 或用户点刷新）。
4. **无面板状态持久化**：窗口尺寸/位置不记（V1）。
5. **`weight: full` 仍无"独立进程组"语义**：目前 `full` 与 `lite` 共用同一套进程管理路径，
   差异只在闸门与超时。若将来 `full` 需要"独立宿主进程 + 异步"（§5.1 原文），需单独一期。
6. **`ezt panel --text` 的渲染是简化版**：只做缩进树，不追求与 WPF 视觉一致。

---

## 12. 实现顺序（写码时的建议次序）

1. 契约层：`ToolPanel` record + `Panels` 进 `ToolContributions` + 解析 + 诊断码（可独立测试）
2. `ProtocolMethods.ToolPanelData` + 档位闸门（发起侧）
3. SDK：`TOOL_PANEL_DATA` + `_dispatch` 分支
4. 宿主：`PanelsAsync` 数据拉取入口（不经 `EnsureApiAllowed`，走独立路径）
5. CLI：`ezt panel` 三个子形态
6. **验收脚本 `_step15_w2c.sh`**（先让 15.1~15.12 全绿，再动 WPF）
7. WPF：`PanelWindow.cs` + 托盘「面板」子菜单
8. 落盘：设计方案 §25 + 更新 §4.5 状态列 + 本文状态标记

> **关键**：第 6 步在第 7 步之前 —— **先把数据链路验穿，再做 GUI**。
> 这样 WPF 层出问题时不可能是"协议没通"，排查面小一个数量级。
