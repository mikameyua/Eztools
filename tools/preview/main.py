# Copyright (c) 2026 Eztools contributors
# SPDX-License-Identifier: GPL-3.0-or-later

"""速览：资源管理器选中项的元信息 + 纯文本内容预览。

为什么是现在这个形态（三条硬约束的推论，见 docs/P4-Wave2c-面板协议.md）：
- 面板节点集已从 6 种扩到 **7 种**（2026-09-23 新增 `image`，协议 §3.6）——
  WebView2 仍被 D3 否决（50~100 MB，推翻 220 MB 预算），且**没有** `code` / `table` 节点，
  所以能表达的仍是"元信息 + 纯文本 + 图片"；
- 触发（热键/托盘，input: shellSelection）与面板打开是**两条通路**，
  靠宿主的私有 KV（storage_set / storage_get）桥接：命令记录选中项，面板读取；
- 面板开着时按"刷新"按钮更新 —— 按钮走 tool.invoke，宿主完成后**自动重拉面板**
  （协议 §4.1 的既有机制），不需要轮询（refreshMs=0，避免每 2 秒拉起一次工具进程）。

边界处理（速览类功能最容易踩的部分）：
- 文本硬截断：面板数据走 JSON-RPC 单帧，无上限会把帧撑爆；
- 二进制探测：前 N 字节出现 NUL 即按二进制处理，只给元信息。
  **例外一**：带 BOM 的 UTF-16 / UTF-32 文本会被这个启发式误伤（其 ASCII 字符自带 NUL），
  所以 BOM 判定**先于**二进制判定 —— 但要求"按该编码真能解成文本"，防 `FF FE` 开头的二进制漏网；
  **例外二**：PDF 同样**先于**二进制判定（真 PDF 基本都含 NUL），走 `_pdf_extract` 尽力提取文字。
  两条例外的判定都用 **magic / BOM 而非扩展名**，与"只按内容判定"这个前提保持一致；
- 编码：**认 BOM 声明的编码**（UTF-8 / UTF-16 / UTF-32），无 BOM 时按 UTF-8 试，
  解码失败用替换字符**并在面板上明确告警** —— **不做编码检测**
  （唯一常见的编码检测库 UTF.Unknown 是 MPL-1.1，与本项目 GPL-3.0 不兼容）。
  告警是**必须**的：GBK 中文会被解成一串 U+FFFD，没有告警时用户只会以为文件坏了，
  而唯一的线索（乱码本身）恰恰长得像"文件损坏"；
- PDF：**只提取文字层**，不还原版式、不渲染图片（面板节点集见 §3.6 —— 2026-09-23 起已有
  `image` 节点，但 PDF **通路本身**仍不做版式还原 / 页面渲染，那是独立一期）。
  加密 / 扫描件 / 缺库各有专门文案 —— 它们的下一步动作完全不同，混成"读取失败"会让用户无从下手；
- Office（`.docx` / `.xlsx` / `.pptx`）：同样是**只提取文字层**。
  它们本质是 **ZIP 容器 + XML**，所以只用**标准库**（`zipfile` + `xml.etree`）——
  **零第三方依赖**，这是实测结论（见设计方案 §29.2）。
  与 PDF 一样，判定排在二进制门之前（ZIP 必含 NUL）；
  判定是**两步**（魔数"是 ZIP"→ 内部条目"是哪种 OOXML"），只看魔数会把普通压缩包误当文档。
  ⚠️ **额外风险面：ZIP 炸弹**（ZIP 压缩比极高，小文件可解出数 GB）⇒
  设单条目 + 累计解压上限，越过则降级。PDF 通路没有这个问题，不能照抄了事；
- 图片（PNG / JPEG / GIF / BMP / WebP）：走 `image` 节点（协议 §3.6）。
  判定同样排在二进制门之前（图片字节含 NUL 是常态），且**按 magic 而非扩展名**。
  工具**不做像素级操作**，只给路径 + 格式 + 大小 —— 缩放/解码由宿主渲染层做。
  `path` 由宿主自行校验（宿主只接受本地路径，拒绝 `http(s)://`，见协议 §3.6.2 S2）；
- 任何一步失败都变成面板里的一个 text 节点，绝不抛 —— 面板错误不弹对话框
  （与清单诊断同一条设计约束）。
"""

import os
import time
import unicodedata

from eztools import Tool

tool = Tool()

_BINARY_NOTE = "二进制文件（前段含 NUL 字节），不预览内容 → 请点下方『用系统程序打开』"

# 解码出非法字节时的告警。为什么值得单独一个节点（而不是省略）：
# GBK/GB18030 的 csv / txt 在中文 Windows 上极常见，它们会被解成一串 U+FFFD，
# 而那个样子**长得就像文件损坏** —— 不告警的话用户只会以为文件坏了，
# 不会想到"是编码问题、用系统程序打开就好了"。
_ENCODING_NOTE = (
    "⚠ 含非 UTF-8 字节（可能是 GBK/GB18030 等本地编码），下方内容可能有误"
    " → 完整、正确的请点下方『用系统程序打开』"
)


def _human_size(n: int) -> str:
    size = float(n)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if size < 1024 or unit == "TB":
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{n} B"


def _mtime(ts: float) -> str:
    return time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts))


def _is_binary(head: bytes) -> bool:
    """前段出现 NUL 字节即判二进制 —— 与 git 对二进制的启发式同源，简单且够用。

    ⚠️ **这个启发式会被 UTF-16 / UTF-32 文本骗到**：它们的 ASCII 字符自带 NUL 字节
    （`"A"` 在 UTF-16LE 里是 `41 00`），于是一个**纯文本**文件会被判成二进制、
    显示"不预览内容"。所以调用之前必须先用 `_bom_encoding()` 排除带 BOM 的宽字符编码
    （BOM 是作者**明确声明**的编码，比 NUL 猜测可信）。
    """
    return b"\x00" in head


# BOM 表。🔴 **顺序是契约**：UTF-32LE 的 BOM（`FF FE 00 00`）以 UTF-16LE 的（`FF FE`）开头，
# 先匹配短的会把 UTF-32 当 UTF-16 解 —— 得到满屏乱码。所以长 BOM 必须排在前面。
# 值用 Python 的宽字符编解码器名：`"utf-16"` / `"utf-32"` 都会**按 BOM 自动选字节序**，
# 不需要自己区分 LE / BE。
_BOMS = (
    (b"\xff\xfe\x00\x00", "utf-32"),
    (b"\x00\x00\xfe\xff", "utf-32"),
    (b"\xff\xfe", "utf-16"),
    (b"\xfe\xff", "utf-16"),
    (b"\xef\xbb\xbf", "utf-8-sig"),
)


def _bom_encoding(head: bytes):
    """返回 `(编码名, BOM 长度)`；无 BOM 返回 `(None, 0)`。"""
    for bom, enc in _BOMS:
        if head.startswith(bom):
            return enc, len(bom)
    return None, 0


# "不像真实文本"的 Unicode 通用类别：未分配 / 私用区 / 代理项。
# 真实文本**基本不会**出现这三类码点，而把随机二进制按宽字符解码时命中率很高 ——
# 所以"这类码点的占比"是区分二者最省事又可靠的判据。
_IMPLAUSIBLE_CATEGORIES = frozenset({"Cn", "Co", "Cs"})


def _unit_of(enc: str) -> int:
    """该编码的**定长单元**大小；变长编码（UTF-8）与无 BOM 返回 1。

    单独抽出来是为了让"长度必须是单元整数倍"这条判据**只有一处实现** ——
    最初它同时写在 `_looks_like_text` 和调用方两处，突变验证发现删掉其中一处
    **没有任何断言变红**（两处等价、行为不变）⇒ 有一份是死代码。
    """
    return 2 if enc == "utf-16" else (4 if enc == "utf-32" else 1)


def _looks_like_text(head: bytes, enc: str) -> bool:
    """按 BOM 声明的编码解出来，**码点**看着像不像真实文本。

    判据 = 未分配 / 私用 / 代理码点（`Cn` / `Co` / `Cs`）与控制字符的占比 < 2%。
    真实文本几乎不会出现这些码点，而把随机二进制按宽字符解码时命中率很高。

    ⚠️ 这里**不做**长度对齐检查 —— 那件事由调用方按 `_unit_of()` 统一负责，
    别在两处各写一遍（会造出永远不会被断言覆盖的死代码）。

    试过的错路（留着免得后人再走）：最初只用"控制字符（<U+0020）占比"——
    对 UTF-16 **几乎无效**，因为随机两字节极少解出控制字符：实测一个伪文本
    （`ff fe 00 01 02 03 ff 7f 00 ff fe 00 aa bb`）解出 `Ā̂翿＀þ뮪`，
    控制字符 0 个，照样被放行。未分配码点才是有效的判据。
    """
    try:
        sample = head.decode(enc)
    except (UnicodeDecodeError, LookupError):
        return False
    if not sample:
        return False

    bad = sum(1 for ch in sample
              if ch == "\ufffd"
              or (ord(ch) < 32 and ch not in "\t\r\n")
              or unicodedata.category(ch) in _IMPLAUSIBLE_CATEGORIES)
    return bad / len(sample) < 0.02


_PDF_MAGIC = b"%PDF-"

# 一份 PDF 可能几百页，全量提取会拖垮面板（数据要过 JSON-RPC 单帧）。
# 预览只需要"够看一眼"，所以设页数上限；越过时照实说"已截断"。
_PDF_MAX_PAGES = 50

_PDF_LIB_MISSING_NOTE = (
    "⚠ 缺少 PDF 解析库（tools/preview/Lib/pypdf 未随包）→ 请点下方『用系统程序打开』"
)
_PDF_ENCRYPTED_NOTE = (
    "⚠ PDF 已加密，无法提取文字 → 请点下方『用系统程序打开』"
)
_PDF_NO_TEXT_NOTE = (
    "该 PDF 没有可提取的文字层（多为扫描件 / 图片版）→ 请点下方『用系统程序打开』"
)

# ── 图片（image 节点，协议 §3.6）─────────────────────────────────────────
# 判定一律走 **magic 而非扩展名**（与 PDF/OOXML 同一条纪律）。
# 为什么这件事在这里尤其重要：`.png` 这个扩展名太容易骗人 ——
# 改了扩展名的文本、被中间环节改了名的文件、甚至某些工具的临时产物，
# 都顶着 `.png` 却不是图。反过来，一张真图被叫成 `.bin` 也该能看。
# ⇒ 按内容判，不按名字判。
#
# 次序表：**先长后短**，避免短前缀误命中（如 `BM` 只有 2 字节，必须放在
# 其它更长的魔数之后判定；否则一个以 `BM` 开头的二进制会被错认成 BMP）。
_IMAGE_MAGIC = (
    (b"\x89PNG\r\n\x1a\n", "PNG"),
    (b"\xff\xd8\xff", "JPEG"),
    (b"GIF87a", "GIF"),
    (b"GIF89a", "GIF"),
    (b"RIFF", "WebP"),        # RIFF....WEBP —— 需要再看第 8~12 字节，见 _image_kind
    (b"BM", "BMP"),
)

# 图片节点只给"H 上限"（协议 §3.6.1）。240 比文本块高、又不会把面板撑到要滚动很久。
_IMAGE_MAX_HEIGHT = 240


def _image_kind(head: bytes):
    """按 magic 判是不是图片；是则返回格式名，否则 `None`。

    ⚠️ `RIFF` 是**容器**不是格式：`.wav` / `.avi` 也是 RIFF 开头。
    所以 WebP 需要**第二步** —— 看第 8~12 字节是不是 `WEBP`。
    与 OOXML 的"魔数只说是不是 ZIP，内部条目才说是什么"完全同构；
    只按第一层魔数放行的话，一个 `.wav` 音频会被当成图片塞进 `<Image>`。
    """
    for magic, label in _IMAGE_MAGIC:
        if not head.startswith(magic):
            continue
        if label == "WebP":
            # RIFF + size(4B) + "WEBP"
            return "WebP" if head[8:12] == b"WEBP" else None
        return label
    return None


# `.docx` / `.xlsx` / `.pptx` 本质是 **ZIP 容器 + XML**，而 zipfile 与
# xml.etree 都是**标准库** ⇒ 提取文字**零第三方依赖**。
# 这是实测结论（三个合成样本全部成功），不是推断 —— 也正是它让"再加一个依赖"
# 这个先前存在的顾虑不再成立（见设计方案 §29.2）。
_ZIP_MAGIC = b"PK\x03\x04"

# ZIP 是**容器**不是格式：`.zip`/`.jar`/`.apk` 都是 PK 开头。
# 所以判定必须两步 —— 魔数只回答"是不是 ZIP"，内部条目才回答"是什么"。
# 只按魔数放行的话，一个普通压缩包会被当成 Office 文档去解析。
_OOXML_MARKERS = (
    ("word/document.xml", "Word"),
    ("xl/workbook.xml", "Excel"),
    ("ppt/presentation.xml", "PowerPoint"),
)

# ZIP 炸弹防线：ZIP 有极高压缩比，42 KB 的包可以解出数 GB。
# 没有上限的话，一次"速览"就能把面板数据撑爆或把工具进程拖死。
# ⚠️ PDF 通路**没有**这个问题（pypdf 内部有页数上限），所以这条不能照抄了事。
_OOXML_MAX_ENTRY_BYTES = 8 * 1024 * 1024    # 单个 XML 条目解压上限
_OOXML_MAX_TOTAL_BYTES = 32 * 1024 * 1024   # 全部条目累计解压上限
_OOXML_MAX_SHEETS = 10                      # xlsx 最多列几个工作表

_OOXML_EMPTY_NOTE = (
    "该文档没有可提取的文字（内容以图片 / 图表为主）→ 请点下方『用系统程序打开』"
)
_OOXML_BROKEN_NOTE = (
    "⚠ 该文档结构不完整或无法解析（可能已损坏、或非标准生成器产出）"
    " → 请点下方『用系统程序打开』"
)
_OOXML_TOO_LARGE_NOTE = (
    "⚠ 文档内部数据过大，出于安全考虑不展开 → 请点下方『用系统程序打开』"
)

# OOXML 的命名空间。用 Clark 记法（`{uri}local`）拼标签名 ——
# ElementTree 不接受前缀写法，必须展开成完整 URI。
_NS_W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
_NS_S = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
_NS_A = "{http://schemas.openxmlformats.org/drawingml/2006/main}"


def _ooxml_kind(names: list):
    """按**内部条目**判定是哪种 OOXML；判不出返回 `(None, None)`。

    为什么不能只看扩展名或 ZIP 魔数：两者都是"容器"级别的信息，
    改了扩展名的文件、或恰好也叫 `document.xml` 的普通 zip，都会骗过它们。
    只按魔数放行的话，一个普通压缩包会被当成 Office 文档去解析。
    """
    present = set(names)
    for marker, label in _OOXML_MARKERS:
        if marker in present:
            return label, marker
    return None, None


def _read_xml(zf, name: str, budget: list):
    """读一个 XML 条目并限流。`budget` 是**可变的累计用量**（单元素 list，便于回传）。

    返回 `(xml_bytes | None, too_large: bool)`。
    限流分两层：单条目上限挡"一个巨大 XML"，累计上限挡"很多个中等 XML"。
    只有单条目上限的话，一个含上千张小表的 xlsx 仍能把内存吃光。
    """
    info = zf.getinfo(name)
    if info.file_size > _OOXML_MAX_ENTRY_BYTES:
        return None, True
    if budget[0] + info.file_size > _OOXML_MAX_TOTAL_BYTES:
        return None, True
    budget[0] += info.file_size
    return zf.read(name), False


def _docx_text(zf, budget: list):
    """Word：段落级提取。返回 `(text, too_large)`。

    `w:t` 是文本片段，`w:p` 是段落 —— 必须**按段落聚合**再换行，
    否则会把一个段落拆成好几行（Word 会把一个句子切成多个 run）。
    """
    raw, too_large = _read_xml(zf, "word/document.xml", budget)
    if too_large:
        return None, True
    if raw is None:
        return None, False

    import xml.etree.ElementTree as ET

    root = ET.fromstring(raw)
    paras = []
    for para in root.iter(_NS_W + "p"):
        text = "".join(node.text or "" for node in para.iter(_NS_W + "t"))
        if text.strip():
            paras.append(text)
    return "\n".join(paras), False


def _xlsx_text(zf, budget: list):
    """Excel：逐工作表提取，输出 TSV 风格的表格。返回 `(text, too_large)`。

    两处容易写错的点：
    ① `t="s"` 的单元格存的是 **sharedStrings 的下标**，不是文字本身 ——
       不回填的话面板上会显示一串数字下标（`0`、`1`、`2`…），看起来像"读到了乱码"；
    ② 空单元格在 XML 里**根本不存在**（稀疏存储），按列号补位才对得齐。
    """
    import xml.etree.ElementTree as ET

    shared = []
    if "xl/sharedStrings.xml" in zf.namelist():
        raw, too_large = _read_xml(zf, "xl/sharedStrings.xml", budget)
        if too_large:
            return None, True
        if raw is not None:
            for si in ET.fromstring(raw).iter(_NS_S + "si"):
                shared.append("".join(t.text or "" for t in si.iter(_NS_S + "t")))

    # 工作表清单：按 workbook.xml 的顺序，找不到就退回目录里排序后的 sheet*.xml。
    sheet_names = [n for n in zf.namelist()
                   if n.startswith("xl/worksheets/") and n.endswith(".xml")]
    sheet_names.sort()

    blocks = []
    for sheet in sheet_names[:_OOXML_MAX_SHEETS]:
        raw, too_large = _read_xml(zf, sheet, budget)
        if too_large:
            return None, True
        if raw is None:
            continue

        rows = []
        for row in ET.fromstring(raw).iter(_NS_S + "row"):
            cells = []
            for cell in row.iter(_NS_S + "c"):
                value = cell.find(_NS_S + "v")
                text = value.text if value is not None else ""
                # 公式单元格另有 <f>，其**计算结果的缓存值**仍在 <v> 里，所以不用特殊处理。
                if cell.get("t") == "s" and (text or "").isdigit():
                    index = int(text)
                    text = shared[index] if 0 <= index < len(shared) else ""
                cells.append(text or "")
            if any(c.strip() for c in cells):
                rows.append("\t".join(cells))

        if rows:
            label = sheet.rsplit("/", 1)[-1].removesuffix(".xml")
            blocks.append(f"【{label}】\n" + "\n".join(rows))

    return "\n\n".join(blocks), False


def _pptx_text(zf, budget: list):
    """PowerPoint：按页收文字。返回 `(text, too_large)`。

    ⚠️ **页序必须按数字排，不能按字符串排**：条目名是 `slide1.xml` / `slide2.xml` /
    `slide10.xml`，纯字符串排序会把 `slide10` 排在 `slide2` **之前** ——
    于是 10 页以上的演示文稿在面板里**页序错乱**（用户很难看出是排序问题，
    会以为"内容串了"）。用 `slide` 后的数字做键才正确。
    """
    import re
    import xml.etree.ElementTree as ET

    def _slide_no(name: str) -> int:
        m = re.search(r"slide(\d+)\.xml$", name)
        return int(m.group(1)) if m else 0

    slides = sorted((n for n in zf.namelist()
                     if n.startswith("ppt/slides/slide") and n.endswith(".xml")),
                    key=_slide_no)
    chunks = []
    for slide in slides:
        raw, too_large = _read_xml(zf, slide, budget)
        if too_large:
            return None, True
        if raw is None:
            continue
        lines = ["".join(t.text or "" for t in p.iter(_NS_A + "t"))
                 for p in ET.fromstring(raw).iter(_NS_A + "p")]
        lines = [ln for ln in lines if ln.strip()]
        if lines:
            label = slide.rsplit("/", 1)[-1].removesuffix(".xml")
            chunks.append(f"【{label}】\n" + "\n".join(lines))
    return "\n\n".join(chunks), False


def _ooxml_extract(path: str, kind: str) -> dict:
    """尽力从 OOXML 提取文字。返回 `{"text", "note"}`。

    **绝不抛** —— 与模块其余部分同一条契约（面板错误不弹对话框）。
    `note` 非空 ⇒ 没提取到可用文字，调用方照原样展示说明。
    """
    import zipfile

    extractors = {"Word": _docx_text, "Excel": _xlsx_text, "PowerPoint": _pptx_text}
    extract = extractors.get(kind)

    budget = [0]
    try:
        with zipfile.ZipFile(path) as zf:
            text, too_large = extract(zf, budget)
    except zipfile.BadZipFile:
        return {"text": "", "note": _OOXML_BROKEN_NOTE}
    except Exception as exc:
        # XML 解析失败 / 编码异常 / 权限问题都落这里 —— 统一降级，绝不抛。
        # OSError 也在此覆盖（BadZipFile 是它的子类，故先单独拦）。
        return {"text": "", "note": f"⚠ 解析文档失败：{exc} → 请点下方『用系统程序打开』"}

    if too_large:
        return {"text": "", "note": _OOXML_TOO_LARGE_NOTE}
    if not text or not text.strip():
        return {"text": "", "note": _OOXML_EMPTY_NOTE}
    return {"text": text, "note": None}


def _ooxml_nodes(path: str, st, kind: str, max_bytes: int, max_lines: int) -> list:
    """OOXML 的预览节点：尽力提取文字，任何失败都降级成"说明 + 仍可打开"。"""
    info = _ooxml_extract(path, kind)
    if info["note"]:
        # 提取失败时**仍标出真实类型**（Word / Excel / PowerPoint）——
        # 标成"二进制文件"会让用户以为"这不是 Office 文档"，与事实不符，
        # 也会把"为什么没内容"（图片版？损坏？）这条最重要的信息盖掉。
        return [
            {"type": "kv", "items": [["类型", kind],
                                     ["大小", _human_size(st.st_size)],
                                     ["修改时间", _mtime(st.st_mtime)]]},
            {"type": "text", "text": info["note"]},
        ]
    return _text_preview_nodes(text=info["text"], st=st, kind=kind,
                               max_bytes=max_bytes, max_lines=max_lines,
                               truncated_by_bytes=False)


def _peek_names(path: str):
    """只读 ZIP 的条目清单（不解压内容）—— 用于类型判定，代价极小。

    这是"魔数 + 内部条目"两步判定的第二步所需的输入。
    任何异常都返回空列表（坏 zip / 加密 / 权限）⇒ 调用方当作"不是 OOXML"，
    退回二进制分支 —— 那类文件本来就该走到那里，不是错误。
    """
    import zipfile

    try:
        with zipfile.ZipFile(path) as zf:
            return zf.namelist()
    except Exception:
        return []



def _pdf_extract(path: str, max_bytes: int) -> dict:
    """尽力从 PDF 提取文字。返回 `{"text", "pages", "note", "truncated"}`。

    **绝不抛** —— 与模块其余部分同一条契约（面板错误不弹对话框）。

    `note` 非空 ⇒ 没提取到可用文字，调用方照原样展示说明。
    三种必然会遇到的降级各有专门文案：缺库 / 加密 / 无文字层 ——
    它们的**下一步动作完全不同**（装库 / 输口令 / 用别的工具），
    混成一句"读取失败"会让用户无从下手。
    """
    try:
        # 延迟导入：只有真要读 PDF 才付出 import 代价（pypdf 约 1.5 MB），
        # 也让其它文件类型的通路不依赖 Lib/ 是否存在。
        from pypdf import PdfReader
    except ImportError:
        return {"text": "", "pages": 0, "note": _PDF_LIB_MISSING_NOTE, "truncated": False}

    try:
        reader = PdfReader(path)
        if reader.is_encrypted:
            # 先拿空口令试一次：很多 PDF 只设了"权限口令"（禁打印 / 禁复制），
            # 内容其实读得出来；真正的打开口令会在这里失败，落到下面的降级。
            try:
                reader.decrypt("")
            except Exception:
                return {"text": "", "pages": 0, "note": _PDF_ENCRYPTED_NOTE,
                        "truncated": False}

        pages = len(reader.pages)
        chunks: list = []
        used = 0
        read_pages = 0
        for index in range(min(pages, _PDF_MAX_PAGES)):
            read_pages = index + 1
            try:
                chunk = reader.pages[index].extract_text() or ""
            except Exception:
                # 单页坏掉不该毁掉整份文档：跳过它，其余照常给。
                chunk = ""
            if chunk:
                chunks.append(chunk)
                used += len(chunk.encode("utf-8", errors="replace"))
            if used >= max_bytes:
                break

        text = "\n".join(chunks)
        if not text.strip():
            return {"text": "", "pages": pages, "note": _PDF_NO_TEXT_NOTE,
                    "truncated": False}
        return {"text": text, "pages": pages, "note": None,
                "truncated": used >= max_bytes or read_pages < pages}
    except Exception as exc:
        return {"text": "", "pages": 0,
                "note": f"⚠ 读取 PDF 失败：{exc} → 请点下方『用系统程序打开』",
                "truncated": False}


def _text_preview_nodes(*, text: str, st, kind: str, max_bytes: int, max_lines: int,
                        truncated_by_bytes: bool, decode_failed: bool = False,
                        extra_rows=()) -> list:
    """把**已解出**的文本渲染成面板节点 —— 文本通路与 PDF 通路**共用这一份**。

    抽成函数不只是省几行：截断判定与"告警必须在正文之前"的顺序只该有**一处**实现。
    同一判据写两遍 ⇒ 必有一份永远测不到（本项目在别处踩过，见
    `docs/验收断言审视清单.md` §10.4）。新增"能出文本"的文件类型时复用它。
    """
    lines = text.splitlines()
    truncated_by_lines = len(lines) > max_lines
    shown = lines[:max_lines]

    nodes = [{"type": "kv", "items": [["类型", kind], ["大小", _human_size(st.st_size)]]
                                     + list(extra_rows)
                                     + [["行数", f"{len(lines)}"
                                                + ("（预览部分）" if truncated_by_bytes else "")],
                                        ["修改时间", _mtime(st.st_mtime)]]}]
    nodes.append({"type": "separator"})
    if decode_failed:
        # 放在正文**之前**：告警必须在用户读到乱码之前出现 ——
        # 否则他先看到的是一堆 ���，先入为主地以为文件坏了，再看告警也已经形成判断。
        nodes.append({"type": "text", "text": _ENCODING_NOTE})
        nodes.append({"type": "separator"})
    nodes.append({"type": "text", "text": "\n".join(shown) if shown else "（空文件）"})
    if truncated_by_bytes or truncated_by_lines:
        reason = []
        if truncated_by_bytes:
            reason.append(f"字节超过 {max_bytes}")
        if truncated_by_lines:
            reason.append(f"行数超过 {max_lines}")
        nodes.append({"type": "text", "text":
                      f"⚠ 已截断（{'、'.join(reason)}）。完整内容请用系统程序打开。"})
    return nodes


def _pdf_nodes(path: str, st, max_bytes: int, max_lines: int) -> list:
    """PDF 的预览节点：尽力提取文字，任何失败都降级成"说明 + 仍可打开"。"""
    info = _pdf_extract(path, max_bytes)
    extra = [["页数", str(info["pages"])]] if info["pages"] else []

    if info["note"]:
        # 提取失败 / 无文字层时**仍标「类型=PDF」** —— 它确实是个 PDF，
        # 标成"二进制文件"会让用户以为"这不是 PDF"，与事实不符，
        # 也会把"为什么没内容"（加密？扫描件？）这条最重要的信息盖掉。
        return [
            {"type": "kv", "items": [["类型", "PDF"], ["大小", _human_size(st.st_size)]]
                                     + extra
                                     + [["修改时间", _mtime(st.st_mtime)]]},
            {"type": "text", "text": info["note"]},
        ]

    return _text_preview_nodes(text=info["text"], st=st, kind="PDF",
                               truncated_by_bytes=info["truncated"],
                               max_bytes=max_bytes, max_lines=max_lines,
                               extra_rows=extra)


def _selected_paths(args) -> list:
    """取本次触发要记录的选中项。inputPaths 是权威来源；CLI 直调时回退 input 单值。"""
    paths = args.get("inputPaths")
    if isinstance(paths, list) and paths:
        return [str(p) for p in paths if str(p).strip()]

    single = args.get("input")
    if isinstance(single, str) and single.strip():
        return [single.strip()]
    return []


@tool.handler("show")
def show(args):
    """记录当前选中项到私有 KV（面板与命令是两条通路，靠 KV 桥接）。"""
    paths = _selected_paths(args)
    tool.storage_set("lastSelection", paths)

    if not paths:
        return {
            "recorded": 0,
            "hint": "未拿到选中项：请在资源管理器中选中文件后，再按 Ctrl+Alt+P（或托盘 → 速览选中的文件）",
        }

    return {
        "recorded": len(paths),
        "paths": paths,
        "hint": "已记录。打开 托盘 → 面板 → 速览 查看；面板开着时按『刷新』换下一个文件",
    }


@tool.handler("reload")
def reload(args):
    """面板"刷新"按钮的落点：不做任何事，靠宿主的"命令完成后自动重拉面板"生效。

    **为什么不是把 show 放进面板按钮**：面板按钮只传 {panelId}（协议 §4.1），
    不带 shellSelection 上下文 —— 调 show 会把已记录的选中项**清空**。
    所以按钮必须指向这个无副作用的命令。
    """
    return {"ok": True}


@tool.handler("openExternal")
def open_external(args):
    """用系统默认程序打开当前记录的文件。"""
    paths = tool.storage_get("lastSelection", [])
    if not paths:
        raise ValueError("还没有记录任何文件：先按 Ctrl+Alt+P 记录选中的文件")

    target = paths[0]
    if not os.path.exists(target):
        raise FileNotFoundError(f"文件不存在（可能已被移动或删除）：{target}")

    os.startfile(target)  # noqa: S606 - Windows 专用，交给系统默认程序
    return {"opened": target, "hint": f"已用默认程序打开：{os.path.basename(target)}"}


def _head_nodes(paths: list) -> list:
    """多选时的头部说明：只预览第一个，其余列出来（协议 §11：V1 是只读 + 按钮）。"""
    nodes = []
    if len(paths) > 1:
        nodes.append({
            "type": "text",
            "text": f"共 {len(paths)} 个选中项，本次预览第 1 个；其余：",
        })
        nodes.append({"type": "list", "items": [p for p in paths[1:21]]})
        if len(paths) > 21:
            nodes.append({"type": "text", "text": f"… 其余 {len(paths) - 21} 个未列出"})
    return nodes


def _image_nodes(path: str, st, kind: str) -> list:
    """图片的预览节点（协议 §3.6）：元信息 + `image` 节点。

    为什么图片值得单开一条通路（而不是继续走"二进制文件"那条）：
    图片是"二进制文件只看元信息"这条限制里**最常撞上、也最不该撞上**的一类 ——
    用户选了一张图按速览，想要的显然是**看到它**，而不是被告知"这是二进制文件"。

    ⚠️ 这里**不做任何像素级操作**（不裁、不缩、不转码）—— 那些交给宿主渲染层，
    它在 WPF 里天然能等比缩放。工具只回答"这是哪张图、多大、什么格式"。
    这也是"工具侧零状态 + 声明式"的一贯做法：**数据在工具，表现在宿主**。
    """
    return [
        {"type": "kv", "items": [
            ["类型", f"{kind} 图片"],
            ["大小", _human_size(st.st_size)],
            ["修改时间", _mtime(st.st_mtime)],
        ]},
        {"type": "separator"},
        # 宿主侧会用 FileStream 读这个路径（协议 §3.6.2 S5）——
        # 所以这里给的必须是**宿主的文件系统能直接打开的路径**（本机同进程族，成立）。
        {"type": "image", "path": path, "alt": os.path.basename(path) or path,
         "maxHeight": _IMAGE_MAX_HEIGHT},
    ]


def _content_nodes(path: str) -> list:
    """单个目标的预览节点。任何失败都变成 text 节点返回，不抛。"""
    nodes = []
    try:
        st = os.stat(path)
    except OSError as exc:
        return [{"type": "text", "text": f"读取失败：{exc}"}]

    if os.path.isdir(path):
        try:
            with os.scandir(path) as it:
                entries = list(it)
            nodes.append({"type": "kv", "items": [
                ["类型", "目录"],
                ["大小", f"{len(entries)} 个子项"],
                ["修改时间", _mtime(st.st_mtime)],
            ]})
            names = [e.name for e in entries[:20]]
            if len(entries) > 20:
                names.append(f"… 其余 {len(entries) - 20} 个未列出")
            nodes.append({"type": "list", "items": names})
            return nodes
        except OSError as exc:
            return [{"type": "text", "text": f"列目录失败：{exc}"}]

    max_bytes = int(tool.config.get("maxTextBytes", 65536))
    max_lines = int(tool.config.get("maxLines", 200))
    probe_bytes = int(tool.config.get("binaryProbeBytes", 4096))

    try:
        with open(path, "rb") as fh:
            head = fh.read(min(probe_bytes, max_bytes + 1))
            # PDF 走独立通路（尽力提取文字）。判定必须放在**二进制门之前**：
            # 真实 PDF 基本都含 NUL（FlateDecode 压缩流），会被那道门拦成"不预览内容"。
            # 用 **magic 而非扩展名** —— 与"只按内容判定"这个前提保持一致。
            if head.startswith(_PDF_MAGIC):
                return _pdf_nodes(path, st, max_bytes, max_lines)

            # Office（OOXML）同理，也必须排在二进制门之前：三个格式都是 ZIP 容器，
            # 前 4096 字节必含 NUL（ZIP 中央目录带 \x00），否则会被拦成"二进制文件"。
            # 判定是**两步**：魔数只说明"是 ZIP 容器"，内部条目才说明"是哪种 OOXML"——
            # 只看魔数的话，一个普通压缩包会被当成 Office 文档去解析。
            if head.startswith(_ZIP_MAGIC):
                kind, _ = _ooxml_kind(_peek_names(path))
                if kind:
                    return _ooxml_nodes(path, st, kind, max_bytes, max_lines)

            # 图片同样必须排在二进制门**之前** —— 图片字节里含 NUL 是常态
            #（PNG 的 IDAT、JPEG 的量化表都可能有 0x00），不先判就会被拦成"二进制文件"。
            # 判定用 magic 而非扩展名：`.png` 骗人的情况比想象中多（见 _image_kind 注释）。
            img_kind = _image_kind(head)
            if img_kind:
                return _image_nodes(path, st, img_kind)

            bom_enc, _ = _bom_encoding(head)
            unit = _unit_of(bom_enc or "")
            # 宽字符编码的文件长度**必须**是单元长度的整数倍 —— 不是就不是那种编码的文本。
            # 这条是**算术事实**，不依赖统计，所以单列（变长编码 `unit == 1` 时恒成立）。
            aligned = st.st_size % unit == 0
            wide_text = (bom_enc is not None and aligned
                         and _looks_like_text(head, bom_enc))
            # BOM 声明了宽字符编码、却在算术上不成立 ⇒ 它**不可能**是文本。
            # 为什么必须单独拦：放它走文本通路会显示乱码**外加一条误导性告警**
            #（"含非 UTF-8 字节"）—— 把"这压根不是文本"说成"编码选错了"，
            # 而用户手里只有那条告警可看，会朝错误的方向排查。
            bom_impossible = bom_enc is not None and unit > 1 and not aligned

            if bom_impossible or (not wide_text and _is_binary(head[:probe_bytes])):
                nodes.append({"type": "kv", "items": [
                    ["类型", "二进制文件"],
                    ["大小", _human_size(st.st_size)],
                    ["修改时间", _mtime(st.st_mtime)],
                ]})
                nodes.append({"type": "text", "text": _BINARY_NOTE})
                return nodes

            fh.seek(0)
            raw = fh.read(max_bytes)
    except OSError as exc:
        return [{"type": "text", "text": f"读取失败：{exc}"}]

    truncated_by_bytes = st.st_size > len(raw)
    # 解码用的编码**只由 BOM 决定**，与 wide_text 无关 —— 这两件事必须解耦：
    # wide_text 管的是"要不要跳过二进制门"；BOM 一旦存在，它就是作者声明的编码，
    # 不管有没有跳过门都该按它解。若把两者绑在一起，一个含少量怪码点的**真** UTF-16
    # 文件会退回去按 UTF-8 解 ⇒ 满屏乱码 + 一条**假**编码告警。
    codec = bom_enc or "utf-8-sig"

    # 先按**严格模式**试解一次，失败才用替换字符兜底，并把"这次解出来的是坏数据"记下来。
    # 为什么不直接 errors="replace"：那样非法字节会被**静默**变成 U+FFFD，
    # 面板上只剩一串 ��� 而没有任何解释 —— 注释里早写了"要如实标注"，代码却一直没做。
    try:
        text = raw.decode(codec)
        decode_failed = False
    except UnicodeDecodeError:
        # 截断可能把一个字符切成两半（UTF-16 / UTF-32 是**定长单元**）⇒
        # 先按单元对齐裁掉尾部残字节，再试一次严格解码。
        # 不做这一步的话，"文件太大被截断"会被误报成"编码有问题"—— 用户看到的是**假告警**。
        unit = 2 if codec == "utf-16" else (4 if codec == "utf-32" else 1)
        if truncated_by_bytes and unit > 1 and len(raw) % unit:
            raw = raw[: len(raw) - (len(raw) % unit)]
            truncated_by_bytes = st.st_size > len(raw)
        try:
            text = raw.decode(codec)
            decode_failed = False
        except UnicodeDecodeError:
            text = raw.decode(codec, errors="replace")
            decode_failed = True

    return _text_preview_nodes(text=text, st=st, kind="文本",
                               truncated_by_bytes=truncated_by_bytes,
                               decode_failed=decode_failed,
                               max_bytes=max_bytes, max_lines=max_lines)


@tool.handler("panel_data")
def panel_data(args):
    """面板数据（协议 §8.1：一个 handler 用 args.panelId 分派）。"""
    if args.get("panelId") != "main":
        return {"nodes": []}

    paths = tool.storage_get("lastSelection", [])
    if not isinstance(paths, list) or not paths:
        return {"nodes": [
            {"type": "heading", "text": "速览"},
            {"type": "text", "text":
             "还没有记录任何文件。在资源管理器里选中文件后按 Ctrl+Alt+P"
             "（或托盘 → 速览选中的文件），然后回到这里按『刷新』。"},
        ]}

    target = str(paths[0])
    nodes = [{"type": "heading", "text": os.path.basename(target) or target}]
    nodes.extend(_head_nodes(paths))
    nodes.extend(_content_nodes(target))
    nodes.append({"type": "separator"})
    nodes.append({"type": "buttons", "items": [
        {"label": "刷新", "commandId": "preview.reload"},
        {"label": "用系统程序打开", "commandId": "preview.openExternal"},
    ]})
    return {"nodes": nodes}


if __name__ == "__main__":
    raise SystemExit(tool.run())
