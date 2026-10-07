# PowerToys 模块化改造与插件商店实现方案

> | | |
> |---|---|
> | **类型** | ④ 执行 / 工具类（外部取证） |
> | **状态** | 📁 归档参考（2026-09-17 最后更新）—— 结论已定：**不改造，自建** |
> | **定位** | **上游 PowerToys 源码取证**：它的模块化与插件商店怎么做的、我们能不能照搬。
> ⚠️ **不是本项目规格**，含上游仓库路径。结论已并入 `Eztools-设计方案.md` |
> | **上下游** | 上游 = PowerToys 仓库；下游 = `Eztools-设计方案.md` 的「不改造」决策 |

> 评估目标：将 Microsoft PowerToys 从"整体安装包"改造为"按需下载的插件商店"形态。
> 评估基准：本地源码 checkout `D:\01-项目代码\Eztools\PowerToys`（对应官方 0.100.x 时代）。
> 结论：**技术上可行，现有骨架复用率高；真正成本在进程隔离 / 签名信任 / 依赖管理，不在商店 UI。**

---

## 0. 结论先行

| 问题 | 答案 |
|---|---|
| 能不能做成插件商店？ | 能。模块 ABI 已经是干净的二进制边界，CmdPal 已有一套上线的扩展商店管线可复用 |
| 最大障碍是什么？ | 不是商店页面，是**进程隔离 + 签名信任 + 共享依赖**三件事 |
| 最容易被低估的风险？ | 36 个模块 DLL 目前在同一个管理员进程内裸 `LoadLibrary`。商店化会把它放大成"任意第三方代码进驻管理员进程" |
| 推荐路径？ | MSI Feature 化（拿即时价值）→ 清单驱动 + 进程隔离（打地基）→ 自研包格式 + Gallery（终态） |
| 不能进商店的模块？ | 需要装服务 / 驱动 / 全局提权常驻的模块（LightSwitch、Hosts、EnvironmentVariables、KeyboardManager、MouseWithoutBorders） |

---

## 1. 现状取证

### 1.1 模块加载链

```
PowerToys.exe (runner, 常驻)
   └─ knownModules[36]  硬编码 std::vector<std::wstring_view>
        └─ load_powertoy() → LoadLibraryW + GetProcAddress("powertoy_create")
             └─ ModuleInterface.dll（薄适配层，实现 PowertoyModuleIface）
                  └─ enable() → ShellExecuteExW → 模块业务进程（独立 exe）
```

关键事实：

| 项 | 证据 |
|---|---|
| 硬编码 36 项模块清单 | `src/runner/main.cpp:256-293` |
| 加载循环 | `src/runner/main.cpp:295-320` |
| 加载实现（无签名校验） | `src/runner/powertoy_module.cpp:14-30` |
| 加载失败在 Release 弹 MessageBox | `src/runner/main.cpp:314` |
| 模块 ABI 定义 | `src/modules/interface/powertoy_module_interface.h:181`（`powertoy_create`）、`:191`（函数指针类型） |
| 开关存于 settings.json `enabled` 对象 | `src/runner/general_settings.cpp:96-101` |
| **全部 DLL 先加载，开关只管 enable()** | `main.cpp:295`（LoadLibrary 全部）先于 `:322 start_enabled_powertoys()` |
| Settings 侧另一套硬编码平行清单 | `Settings.UI/OOBE/Enums/PowerToysModules.cs:7-41`；`Settings.UI.Library/Helpers/ModuleHelper.cs:11-176`；`ModuleIconResolver.cs:14-21` |

> **重要语义问题**：`enabled=false` 只是不调 `enable()`，DLL 仍然被载入进程。所以"没安装的模块"在现有架构下等价于"加载失败"，而加载失败在 Release 会弹窗。

### 1.2 打包与分发

| 项 | 证据 | 结论 |
|---|---|---|
| 模块级 ComponentGroup 已存在 | `installer/PowerToysSetupVNext/` 下 30 个模块 wxs（如 `PowerRename.wxs:17`） | 组件粒度已具备 |
| 但只有唯一一个 Feature | `Product.wxs:42-74`，`CoreFeature` + `AllowAbsent="no"` | Feature 粒度缺失，无法按需安装 |
| Bootstrapper 只装单个 MSI | `PowerToysBootstrapperVNext.wixproj:26`；`PowerToys.wxs:56-66` 的 `<Chain>` 仅 1 个 MsiPackage + WebView2 | 无网络附加下载能力 |
| 无"可选/可下载模块"机制 | 全仓搜索 optional / additional / downloadable，唯一命中是 CmdNotFound 安装 PowerShell | 需从零建 |
| 共享依赖组（已是独立组） | `WinAppSDK.wxs`、`DscResources.wxs`、`CliShims.wxs`、`Tools.wxs` | 可作为依赖包基础 |

### 1.3 更新机制

- 源：GitHub Releases API `https://api.github.com/repos/microsoft/PowerToys/releases/latest`（`src/common/updating/updating.cpp:18`）
- 逻辑：`ObtainInstaller`（`src/update/PowerToys.Update.cpp:101-183`）从 assets 里选**单个**安装器
- 安装：`MsiInstallProductW` 或 bootstrapper `/passive /norestart`（`PowerToys.Update.cpp:271-296`）
- 信任：`installer.cpp:281-332` 做 Authenticode + 校验发布者为 Microsoft，**这是唯一可复用的签名校验基础设施**
- 粒度：整体更新，无模块级

### 1.4 已有的第三方扩展生态（最重要的复用资产）

**CmdPal 已经有一套生产中的扩展商店：**

| 组件 | 位置 |
|---|---|
| 扩展 SDK（NuGet 发布） | `src/modules/cmdpal/extensionsdk/`，`Microsoft.CommandPalette.Extensions.SDK.nuspec:4` |
| 扩展发现机制 | MSIX `AppExtension`，Name=`com.microsoft.commandpalette`；`WinRTExtensionService.cs:27,49-51` 经 `PackageCatalog.OpenForCurrentUser()` |
| Gallery feed | `ExtensionGalleryService.cs:16` — `https://aka.ms/CmdPal-ExtensionsJson` |
| 缓存 / TTL / 限流降级 | `ExtensionGalleryService.cs:98-123`（429 处理）、`:240-298`（图标本地化）、`ExtensionGalleryHttpClient`（TTL 缓存 + Prune） |
| Gallery UI（已上线） | `ExtensionGalleryPage.xaml.cs`、`ExtensionGalleryItemPage.xaml.cs`、`ExtensionGalleryViewModel.cs` |
| 安装委托 | `ExtensionGalleryItemViewModel.cs:37-41` — 委托给 Store / WinGet / URL，本身不下载 |
| 检测 | 靠 `PackageFamilyName` |

**PowerToys Run 插件机制：**

- 目录：`<install>\RunPlugins`（预装）+ `%LOCALAPPDATA%\...\PowerToys Run\Plugins`（用户装）— `Constant.cs:35,61-62`
- 发现：扫描每个子目录的 `plugin.json`（`PluginConfig.cs:24,32-39`），仅允许 CSharp（`:61`），按 ID 取最高版本去重（`:62-81`）
- 加载：`PluginLoadContext.cs`
- **安全：全仓无任何插件签名 / 信任校验**；卸载靠 `NeedDelete.txt` 标记（`PluginConfig.cs:46-51`）

> 这套"目录扫描 + 版本仲裁"的加载器思路，可以直接移植到模块层。

---

## 2. 模块权限分级（决定可分发性）

卡点是权限，不是技术偏好。

| 级别 | 模块 | 权限需求 | 商店可行性 |
|---|---|---|---|
| **T1 用户态** | FancyZones、ShortcutGuide、ColorPicker、TextExtractor(PowerOCR)、Awake、AlwaysOnTop、PowerAccent、PowerDisplay、CropAndLock、MeasureTool、ZoomIt、AltWindowCycle、GrabAndMove、FindMyMouse、MouseHighlighter、MouseJump、MousePointerCrosshairs、AutoHideCursor、CursorWrap、Peek、Workspaces、AdvancedPaste、CmdNotFound | 无 | ✅ 免提权直接装，商店首选 |
| **T2 需注册** | PowerRename、ImageResizer、FileLocksmith、NewPlus、FileExplorerPreview(previewpane 全家)、RegistryPreview | shell 扩展 / COM / 预览处理器注册，需 per-machine | ⚠️ 必须提权安装；或 MSIX packaged extension（能力受限） |
| **T3 需服务/提权常驻** | LightSwitch（装后台服务）、Hosts、EnvironmentVariables、KeyboardManager、MouseWithoutBorders | 服务 / 全局钩子 / `runas` 提权 | ❌ 只能随整体升级 |

提权点证据：

| 模块 | 提权位置 |
|---|---|
| Hosts | `HostsModuleInterface/dllmain.cpp:83-99`（`runas`） |
| EnvironmentVariables | `EnvironmentVariablesModuleInterface/dllmain.cpp:89-105`（`runas`） |
| KeyboardManager | `KeyboardEventHandlers.cpp:1650`（`run_elevated`） |
| FileLocksmith | `NativeMethods.cpp:126`（`runas`） |
| MouseWithoutBorders | `ModuleInterface/dllmain.cpp:636`（`runas`） |
| LightSwitch | `LightSwitch.wxs:10-33`（安装服务） |
| FancyZones / AlwaysOnTop / Awake 等 | 仅 `is_process_elevated()` 判断以跳过提权窗口 |

MSI 作用域仅有 per-machine(elevated) / per-user(limited) 两档：`Common.wxi:37-55`。

---

## 3. 目标架构

### 3.1 四层

```
分发层    Gallery Feed（静态 JSON + CDN） ｜ 独立签名模块包
              ↓
宿主层    Runner + Settings —— 清单驱动发现（替换 main.cpp 硬编码数组）
              ↓
运行时层  Manifest 发现 · 版本与依赖解析 · 签名信任校验 · 进程隔离宿主
              ↓
模块层    用户态模块 ｜ 提权模块 ｜ 服务类模块
```

### 3.2 可复用 vs 必须重构

**可直接复用：**

1. `powertoy_create` ABI + `PowertoyModuleIface` —— 干净的二进制边界
2. `load_powertoy()` —— 本身通用，换清单驱动改动小
3. 30 个 per-module ComponentGroup wxs
4. CmdPal 的 feed → install source → detection → Gallery UI 完整管线
5. Run 的目录扫描式插件加载器 + 版本仲裁
6. `verify_installer_trust` —— Authenticode + 发布者校验

**必须重构：**

1. `main.cpp:256` 硬编码 `knownModules` → 清单/目录发现
2. `Product.wxs:42` 单一强制 Feature → Feature 树或独立包
3. 整体更新 → 模块级更新
4. 模块/插件零签名信任 → manifest 签名 + WinVerifyTrust
5. Settings 三处硬编码模块清单 → 清单生成
6. 模块与 runner 同目录 + 单一作用域 → 版本化模块仓库 + 逐模块作用域
7. 共享依赖（WinAppSDK / WebView2 / .NET / DSC）→ 独立前置包

### 3.3 模块包格式（建议）

```
<key>-<version>.ptmod          # zip 容器，必须签名
├── module.json                # 清单（见下）
├── bin/
│   ├── <Key>ModuleInterface.dll
│   └── <Key>.exe
├── assets/
│   └── icon.png
└── META-INF/
    └── signature.p7s          # 整包签名
```

`module.json` 建议字段：

```json
{
  "key": "FancyZones",
  "displayName": "FancyZones",
  "version": "0.100.2",
  "abiVersion": 1,
  "minHostVersion": "0.100.0",
  "maxHostVersion": null,
  "entry": "bin/PowerToys.FancyZonesModuleInterface.dll",
  "publisher": "Microsoft Corporation",
  "tier": "user",
  "capabilities": [],
  "dependencies": {
    "shared": ["WindowsAppSDK>=1.7", "WebView2"],
    "modules": []
  },
  "hotkeys": ["Win+Shift+`"],
  "configKey": "FancyZones"
}
```

安装位置（用户态）：

```
%LOCALAPPDATA%\PowerToys\Modules\<Key>\<Version>\
%LOCALAPPDATA%\PowerToys\Modules\<Key>\current.json   # 指向活跃版本，支持回滚
```

---

## 4. 方案对比

| 方案 | 做法 | 成本 | 收益 | 致命局限 |
|---|---|---|---|---|
| **A. MSI Feature 化** | `CoreFeature`（不可缺）+ 每模块独立 Feature | 低 | 安装时可勾选、控制面板可单独卸载，**立刻拿到"按需安装"** | MSI 不支持从网上下载单个 feature；仍需提权；不支持第三方 |
| **B. 全量 MSIX 化** | 每模块一个 MSIX，复用 CmdPal AppExtension 模式，走 Store/WinGet | 中高 | 免提权、Store 分发、自动更新、签名天然 | T2/T3 模块基本做不了；fork 项目无 Store 渠道 |
| **C. 自研包格式 + 清单驱动运行时** | `.ptmod` + `module.json`，版本化仓库 + 模块运行时 | 高 | 完全可控：进程隔离、逐模块提权、模块级更新、可开第三方生态 | 工作量最大，须自建依赖共享方案 |

**推荐：A → C 渐进。** B 仅用于"纯用户态小工具"子集（比如 T1 里那些无 UI 的小模块），不承载主力模块。

---

## 5. 路线图

### P0 — 地基（清单化）

- [ ] 每模块产出 `module.json`（从现有 `get_name/get_key/get_config/get_hotkeys` 运行时元数据反推）
- [ ] `main.cpp` 的 `knownModules` 改为扫描 `modules/*/module.json`
- [ ] 修掉 `main.cpp:314` Release 弹窗 → 静默降级 + 记录
- [ ] 区分"未安装"与"加载失败"两种状态
- [ ] `ModuleHelper.cs` / `PowerToysModules.cs` / `ModuleIconResolver.cs` 三处硬编码改为清单生成
- **交付**：模块成为数据驱动的可插拔单元（还不带商店）

### P1 — 可卸载

- [ ] 拆 WiX Feature 树：30 个 ComponentGroup 各自独立 Feature
- [ ] `CoreFeature` 保留 `AllowAbsent="no"`，其余允许缺省
- [ ] 控制面板 / 卸载程序支持按模块移除
- [ ] 验证模块间共享文件（WinAppSDK、资源配置）不被误删
- **交付**：用户能只装 FancyZones

### P2 — 可下载

- [ ] 定义 `.ptmod` 包格式 + `module.json` schema
- [ ] 整包签名与校验（复用 `verify_installer_trust`）
- [ ] 版本化模块仓库 `%LOCALAPPDATA%\PowerToys\Modules\<Key>\<Ver>\` + `current.json` 回滚
- [ ] **进程隔离宿主**：每模块独立 host 进程，崩溃隔离 + 最低权限 token
- [ ] 共享依赖独立成前置包，模块声明依赖而非各带一份
- [ ] ABI 版本协商（`abiVersion` + `minHostVersion`）
- **交付**：不重装整个 PowerToys 就能加/删模块

### P3 — 商店

- [ ] 把 `ExtensionGalleryService` 抽成通用 `ModuleGalleryService`（feed 拉取 + TTL 缓存 + 429 降级 + 图标本地化直接复用）
- [ ] 商店 UI（列表 / 详情 / 已安装 / 更新 / 权限提示）
- [ ] 逐模块 UAC 授权（沿用现有 `runas` 模式 + 独立提权 helper exe）
- [ ] 模块级更新通道（与宿主版本解耦）
- [ ] 卸载时保留用户配置，重装可恢复
- **交付**：完整商店闭环

---

## 6. 风险登记册

| # | 风险 | 严重度 | 说明 | 缓解 |
|---|---|---|---|---|
| 1 | **同进程加载第三方代码** | 🔴 致命 | 36 个 DLL 在管理员进程内裸 `LoadLibrary`。商店化 = 任意第三方代码进驻管理员进程，且崩溃即拖垮整个 PowerToys | P2 必须先做进程隔离，再开商店。顺序不可反 |
| 2 | **Release 加载失败弹窗** | 🟠 高 | `main.cpp:314` 缺模块会 `MessageBoxW`，开机必弹 | P0 改为静默降级 + 日志 |
| 3 | **共享依赖地狱** | 🟠 高 | WinAppSDK / WebView2 / .NET / DSC 是共享底座，各模块各带一份会体积与版本双失控 | 依赖声明 + 独立前置包 |
| 4 | **ABI 无版本协商** | 🟠 高 | `PowertoyModuleIface` 无版本号，宿主升级即碎插件 | `abiVersion` + 宿主兼容区间 |
| 5 | **签名信任链缺失** | 🔴 致命 | 模块 DLL 零校验；仅更新器有 WinVerifyTrust | manifest 签名 + 整包 Authenticode |
| 6 | **配置迁移** | 🟡 中 | `enabled` 的 key 即模块名，卸载后配置需保留 | 配置与模块分离存储，重装恢复 |
| 7 | **模块级权限模型缺失** | 🟠 高 | MSI 只有 per-machine / per-user 两档 | 能力声明字段 + 逐模块提权 helper |
| 8 | **shell 扩展无法用户态注册** | 🟡 中 | T2 模块的 COM/shell 扩展天然需要机器级注册 | 接受 T2 必须提权；或用 MSIX packaged extension（能力受限） |
| 9 | **卸载残留** | 🟡 中 | 现有 Run 插件靠 `NeedDelete.txt` 标记，机制粗糙 | 设计正规的卸载/回滚流程 |
| 10 | **品牌与分发合规** | 🟡 中 | 若走官方渠道涉及微软签名与 Store 政策；若是 fork 自用则跳过 | 先明确目标场景 |

---

## 7. 待确认事项

1. **目标场景**：是评估可行性、自用 fork 改造，还是朝可上游合并的方向设计？（决定是否要考虑微软签名/品牌约束）
2. **是否支持第三方开发者**：仅"官方模块按需下载"与"开放第三方生态"的工作量差约一倍（后者必须做沙箱 + 信任分级 + 审核流）。
3. **改造范围**：只做 T1 用户态模块的商店（快速见效），还是必须覆盖 T2 提权模块（复杂度陡增）？
4. **是否接受进程模型变更**：模块从"宿主内 DLL"改为"独立 host 进程"会带来 IPC 开销与启动延迟，需要确认可接受。

---

## 8. 生态成熟度评估：能否达到 Tachiyomi 级别

### 8.1 Tachiyomi / Mihon 的扩展机制（事实梳理）

| 项 | 实现 |
|---|---|
| 包单元 | Android APK，每个扩展是独立应用包（如 `eu.kanade.tachiyomi.extension.all.mangadex`） |
| 接口 | 实现统一的 `Source` 抽象（窄接口，十几个方法） |
| 发现 | 仓库 `index.min.json`（如 `https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.min.json`），含 `name` / `pkg` / `apk` / `lang` / `code` / `version` / `nsfw` / `sources[]` |
| 多仓库 | **支持用户在应用内添加任意第三方仓库 URL**，多仓库并存 |
| 信任 | `Extension.Available` 带 `signatureHash`；状态机为 `Available` → `Installed` / **`Untrusted`**，未信任的扩展需用户显式 `trustExtension()` |
| 安装 | 委托 OS `PackageInstaller`，应用本身不安装；支持手动 sideload |
| 兼容性 | `versionCode`（更新判断）+ `libVersion`（API 兼容） |
| 隔离 | 每个扩展是独立 Android 应用 = 独立进程 + 独立权限沙箱 |

### 8.2 核心结论：Tachiyomi 的成熟靠的是 OS 白送的四个原语

| Android 免费提供 | Windows 对应物 | 缺口 |
|---|---|---|
| 包管理器 + 系统安装器 | MSIX | shell 扩展 / 服务装不进去 |
| 每扩展独立应用沙箱 | AppContainer / Silo | 模块强依赖 OS 集成，容器内做不了 |
| 签名校验原语 | Authenticode | 存在但未被强制（`LoadLibrary` 零校验） |
| 清单内嵌包元数据 | 无 | 须自建 manifest + ABI 版本协商 |

**这四个原语在 Windows 上要么能力受限、要么必须自建、要么无法对等实现。**

### 8.3 更深一层的结构性差异：同质 vs 异质

- **Tachiyomi 扩展是同质的**：每个都只做"实现 `Source` 抓内容"。接口窄 → 可用模板批量生产（Multisrc）→ 上千扩展成为可能。
- **PowerToys 模块是异质的**：30 个模块 = 30 种能力形状（全局热键 / shell 扩展 / 后台服务 / 窗口钩子 / 截屏 OCR / 跨机键鼠）。没有任何窄接口能覆盖，这是能力需求天然发散，不是接口设计缺陷。

**因此对标的正确对象是 VS Code，不是 Tachiyomi：**

| VS Code 解法 | PowerToys 对应改造点 |
|---|---|
| Extension Host 独立进程 | 模块从宿主内 DLL 改为独立 host 进程（**唯一能解决"第三方代码进管理员进程"的方案**） |
| Contribution Points 声明式扩展点 | 模块只声明"提供热键 / 提供右键菜单项"，不随意调 OS |
| `package.json` 能力白名单 | `module.json` 的 `capabilities` 字段 + 装前权限提示 |
| Marketplace 发布者验证 | manifest 签名 + Authenticode 白名单（复用 `installer.cpp:281`） |

### 8.4 各层可达成度

| 层 | 可达成度 | 说明 |
|---|---|---|
| Command Palette 扩展 | ✅ **已具备** | MSIX 包 + 发布者签名 + Gallery 已上线，形态与 Tachiyomi 同构 |
| T1 用户态模块（约 23 个） | 🟡 **可达** | 补上进程隔离 + 包格式 + ABI 版本协商后，体验可接近 Tachiyomi |
| T2 提权模块 | 🟠 半可达 | 逐模块 UAC，体验打折 |
| T3 服务 / 驱动模块 | ❌ **不可达** | shell 扩展注册、服务安装、全局提权无法在用户态完成 |

### 8.5 成熟生态的六个必要条件

| # | 条件 | PowerToys 现状 |
|---|---|---|
| 1 | 窄接口 + 声明式扩展点 | ❌ 模块需直接调 OS 能力，无法收窄 |
| 2 | 进程隔离 + 权限降级 | ❌ 当前同进程裸 `LoadLibrary` |
| 3 | 稳定 ABI + 版本兼容矩阵 | ❌ `PowertoyModuleIface` 无版本号 |
| 4 | 用户可自行添加的多源 feed | ❌ 无 feed 概念，更新走单一 GitHub Release |
| 5 | 低门槛 SDK + 脚手架 | ✅ CmdPal 已做到（`extensionsdk/README.md` 的 "Create a new extension" 命令） |
| 6 | 共享运行时前置包 | ⚠️ WinAppSDK / WebView2 / DSC 已是独立组，未包化 |

### 8.6 三个必须认清的现实

1. **生态成熟度不来自架构，来自供需。** Tachiyomi 上千扩展源于几千个有图源需求且"会写 Kotlin"的贡献者。PowerToys 第三方模块门槛是 C++/C# + Win32/WinRT + 模块 ABI 理解，贡献者基数会少一到两个数量级。
2. **去中心化是生态的续命机制。** 2024 年 Tachiyomi 关停、官方 extensions 仓库被下架，社区重建 `keiyoushi/extensions` 即恢复运行——因为客户端支持用户自行添加仓库 URL。方案若只设计"单一官方 feed"，等于把生态命脉交给单一 gatekeeper。
3. **微软的信任边界不会放开。** 证据：CmdPal 扩展的安装委托给 Store / WinGet（`ExtensionGalleryItemViewModel.cs:37-41`），PowerToys 自身不下载不安装，检测依赖 `PackageFamilyName`。官方生态的形态会像 Tachiyomi，自由度不会。

### 8.7 结论

- **能做到**：形态接近 Tachiyomi 的模块商店（清单驱动 + 进程隔离 + 签名 + 索引 feed + 一键装），覆盖 CmdPal 扩展与 T1 用户态模块。
- **做不到**：覆盖全 30 个模块的免提权一键装生态。
- **做不出**：自发繁荣的第三方开发者生态——这不是技术问题，是号召力与治理问题。自用 fork 场景下第三方贡献者会趋近于零，最终形态是"可控的模块化工具箱"而非"生态"。

---

## 附：关键文件索引

| 用途 | 路径 |
|---|---|
| 模块清单（硬编码，待重构） | `src/runner/main.cpp:256-293` |
| 模块加载实现 | `src/runner/powertoy_module.cpp:14-30` |
| 模块 ABI 定义 | `src/modules/interface/powertoy_module_interface.h:181,191` |
| 开关与启动顺序 | `src/runner/general_settings.cpp:96-101`、`src/runner/main.cpp:295,322` |
| 安装包 Feature 定义 | `installer/PowerToysSetupVNext/Product.wxs:42-74` |
| Bootstrapper Chain | `installer/PowerToysSetupVNext/PowerToys.wxs:56-66` |
| 更新源与安装 | `src/common/updating/updating.cpp:18`、`src/update/PowerToys.Update.cpp:101-183,271-296` |
| 签名校验（可复用） | `src/common/updating/installer.cpp:281-332` |
| 扩展商店管线（可复用） | `src/modules/cmdpal/Microsoft.CmdPal.Common/ExtensionGallery/Services/ExtensionGalleryService.cs` |
| Gallery UI | `src/modules/cmdpal/Microsoft.CmdPal.UI/Settings/ExtensionGalleryPage.xaml.cs` |
| Run 插件加载器 | `src/modules/launcher/*/PluginConfig.cs`、`PluginLoadContext.cs` |
| 扩展 SDK 文档 | `src/modules/cmdpal/extensionsdk/README.md` |
| 模块权限档位 | `installer/Common.wxi:37-55` |
