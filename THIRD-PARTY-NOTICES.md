# 第三方组件声明（THIRD-PARTY NOTICES）

本仓库整体以 **GNU GPL-3.0-or-later** 分发（见 `LICENSE`）。
下面列出**随仓库分发**的第三方组件及其许可。所有组件均与 GPL-3.0 兼容
（判定依据与许可分级见 `docs/参考来源.md`）。

> **维护规则**：新增任何 vendor 进仓库的第三方代码时，**必须**在此登记
> （名称 / 版本 / 许可 / 位置 / 上游），并保留其许可正文。
> 这是 GPL-3.0 与各许可的**共同义务**，漏登记不是"以后再补"的事 —— 见 `docs/参考来源.md` §三。

---

## 1. pypdf

| 项 | 内容 |
|---|---|
| 版本 | **6.9.2**（wheel：`pypdf-6.9.2-py3-none-any.whl`，sha256 `662cf29bcb419a36a1365232449624ab40b7c2d0cfc28e54f42eeecd1fd7e844`） |
| 许可 | **BSD-3-Clause**（`License-Expression: BSD-3-Clause`，见随包 METADATA） |
| 与 GPL-3.0 的关系 | ✅ **兼容** —— BSD-3-Clause 是宽松许可，可与 GPL-3.0 合并分发；义务 = 保留版权声明与免责声明 |
| 位置 | `tools/preview/Lib/pypdf/`（含 `pypdf-6.9.2.dist-info/`） |
| 上游 | <https://github.com/py-pdf/pypdf> |
| 用途 | 速览工具（`tools/preview`）提取 PDF 的**文字层** |
| 是否含编译代码 | **否** —— 纯 Python，无 `.pyd` / `.so`（因此可跨同平台 Python 小版本直接 vendor） |
| 许可正文 | `tools/preview/Lib/pypdf-6.9.2.dist-info/licenses/LICENSE`（随包保留，原样未改） |

版权声明（照录上游原文）：

```
Copyright (c) 2006-2008, Mathieu Fenniak
Some contributions copyright (c) 2007, Ashish Kulkarni <kulkarni.ashish@gmail.com>
Some contributions copyright (c) 2014, Steve Witham <switham_github@mac-guyver.com>

All rights reserved.
```

---

## 2. 未 vendor、仅作开发期参照的项目

这些**不在随包分发之列**（不进 `dist/` / 便携包），仅作为实现参考，
因此不构成本仓库的分发义务；登记在此是为了让"参考过谁"可追溯。

| 项目 | 许可 | 用途 |
|---|---|---|
| PowerToys | MIT | 托盘 / 速览（Peek）等实现的参照；**未借用代码** |
| GlazeWM / QuickLook | GPL-3.0 | 与本仓库同许可，**可读可抄**（2026-09-21 D7 决议）；目前未借用 |

详细清单与判定见 `docs/参考来源.md`。

## 2b. NuGet 依赖（随 bin / 便携包分发的二进制，非 vendor 源码）

### Microsoft.Data.Sqlite 10.0.0（W5-a 剪贴板历史库，2026-09-26）

| 项 | 内容 |
|---|---|
| 版本 | **10.0.0**（传递依赖：Microsoft.Data.Sqlite.Core 10.0.0、SQLitePCLRaw.bundle_e_sqlite3 / core / lib / provider 2.1.11） |
| 许可 | **MIT**（NuGet 包 license 表达式 MIT，© Microsoft） |
| 与 GPL-3.0 的关系 | ✅ **兼容**（宽松许可，义务 = 保留版权与许可声明，本文件即履行） |
| 引入位置 | `src/Eztools.Clipboard/Eztools.Clipboard.csproj`（PackageReference） |
| 用途 | 剪贴板历史 SQLite 持久化 + FTS5 trigram 中文搜索（W5-剪贴板-设计方案.md §6.1） |
| 形态 | **NuGet 二进制依赖**（非源码 vendor）：`e_sqlite3.dll` 原生库随发布落 bin；SQLite ≥3.4x，满足 FTS5 trigram 的 ≥3.34 要求 |

## 3. 已并入（borrowed）的第三方代码

这些是**实际改编进本仓源码**的部分：随包分发时按本仓 GPL-3.0-or-later 整体分发，
此处保留原作者版权声明与来源追溯（MIT 与 GPL-3.0 兼容，义务为保留声明）。

### 3.1 PowerToys PowerOCR.Core —— MIT

| 项 | 内容 |
|---|---|
| 来源 | `PowerToys/src/modules/PowerOCR/PowerOCR.Core/`（本地克隆，上游 <https://github.com/microsoft/PowerToys>） |
| 许可 | **MIT** © Microsoft Corporation（`PowerToys/LICENSE`） |
| 并入位置 | `src/Eztools.Ocr/BitmapPreprocessor.cs`（改编自 `Imaging/BitmapPreprocessor.cs`）、`src/Eztools.Ocr/WindowsOcrEngine.cs`（改编自 `Ocr/WindowsOcrRecognizer.cs` + `Services/TextExtractorService.cs`） |
| 借用机制 | EnhancedScale 1.5× 三段回退、64px 最小维 + 8px 补边、BMP→SoftwareBitmap 识别通道、word 包围盒命中变换、MaxImageDimension 钳制 |
| 借入日期 | 2026-09-26（W4-a，`docs/W4-屏幕OCR-设计方案.md` §9 借鉴映射表） |
| 修改说明 | 结构适配本项目命名空间与记录类型；注释中文化；每个文件头部保留 MIT 来源标注 |
