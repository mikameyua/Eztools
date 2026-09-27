#!/usr/bin/env python
"""速览（preview）内容判定验收：文件类型识别 / 编码告警 / 提示语指路。

## 为什么必须有这个脚本（血的教训）

`tools/preview/main.py` 的 `_content_nodes()` 是速览的**核心逻辑**，此前
**零自动化覆盖** —— preview 的既有断言只覆盖四件外围的事：
"清单声明了 input: shellSelection" · "面板出现在列表里" · "热键绑定" · "config 映射"。
**文件类型判定一条断言都没有。**

于是实测（`_scratch/pv_matrix.py`，24 个合成样本）暴露的三个缺陷一直没人发现：

| # | 现象 | 性质 |
|---|---|---|
| ① | GBK/GB18030 中文 → 乱码，且**无任何提示** | 注释声称有告警，代码根本没实现 |
| ② | UTF-16 文本 → **误判为二进制**，显示"不预览内容" | 假阴性 |
| ③ | 未压缩 PDF → **误判为文本**，把 PDF 源码当正文显示 | 假阳性 |

本脚本覆盖 ① 的修复（编码告警）与二进制提示语指路。
② / ③ 已修（BOM 先于 NUL 判定 / PDF magic 通路），断言见 16.8~16.15。
④ Office（docx/xlsx）文字层：OOXML = ZIP 容器 + XML，标准库即可提取，
   **零第三方依赖**；断言见 17.1~17.7。ZIP 炸弹是这条通路独有的新风险面。

## 走的是真实通路

`ezt invoke preview.show --json {inputPaths}` 记录选中项 → `ezt panel preview main --json`
读回面板数据。两条命令是**两个进程**，中间靠宿主的 per-tool storage 落盘桥接 ——
这正是用户按 Ctrl+Alt+P 后打开面板的实际路径，不是直接 import 函数（那样测不到桥接）。
"""

import argparse
import io
import json
import os
import subprocess
import sys
import zipfile

# 独立运行时防 GBK 控制台（⚠ 字符在 GBK 下 print 直接崩，2026-09-24 实测）。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

INFO = []
FAILS = 0

# OOXML 命名空间（Clark 记法）。夹具里必须显式带上 —— ElementTree 按完整 URI 匹配，
# 缺命名空间的 `<w:p>` 会被 `root.iter(_NS_W+"p")` **静默漏掉**（提取结果为空，
# 表现为"函数没生效"，实际是夹具本身没意义）。这是造 OOXML 夹具最容易踩的坑。
_NS_W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
_NS_S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
_NS_A = "http://schemas.openxmlformats.org/drawingml/2006/main"


def info(msg):
    print(f"  {msg}")


def ck(name, cond, detail=""):
    global FAILS
    if cond:
        print(f"  [PASS] {name}")
    else:
        FAILS += 1
        print(f"  [FAIL] {name}" + (f"   {detail}" if detail else ""))


def write_bytes(path, data):
    with io.open(path, "wb") as fh:
        fh.write(data if isinstance(data, bytes) else data.encode("utf-8"))
    return path


def make_pdf(content_stream: bytes) -> bytes:
    """生成一份单页、**结构合法**（xref / startxref 正确）的最小 PDF。

    为什么要自己拼而不是"写个像 PDF 的字节"：pypdf 会严格校验 `startxref`，
    随手拼的会直接报 `startxref not found` —— 那是**正确**行为，
    但会让夹具失去意义（测的是夹具不是代码）。实测踩到过。
    """
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        b"<< /Length " + str(len(content_stream)).encode() + b" >>\nstream\n"
        + content_stream + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for index, body in enumerate(objs, start=1):
        offsets.append(len(out))
        out += f"{index} 0 obj\n".encode() + body + b"\nendobj\n"
    xref_off = len(out)
    out += f"xref\n0 {len(objs) + 1}\n".encode() + b"0000000000 65535 f \n"
    for off in offsets:
        out += f"{off:010d} 00000 n \n".encode()
    out += (f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\n"
            f"startxref\n{xref_off}\n%%EOF\n").encode()
    return bytes(out)


PDF_SAMPLE_TEXT = "Hello PDF Preview"

# 提取结果的**期望值**——夹具与断言共用一个常量，避免"改了夹具忘了断言"。
DOCX_SAMPLE_TEXT = "第一段中文标题"


def make_docx(path, paras):
    """最小但**结构合法**的 .docx：ZIP + `word/document.xml`（段落用 w:p / w:t）。"""
    body = "".join(f"<w:p><w:r><w:t>{t}</w:t></w:r></w:p>" for t in paras)
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("[Content_Types].xml", "<Types/>")
        z.writestr("word/document.xml",
                   '<?xml version="1.0"?><w:document xmlns:w="%s">'
                   "<w:body>%s</w:body></w:document>" % (_NS_W, body))
    return path


def make_xlsx(path, strings, sheets):
    """最小 .xlsx：共享字符串表 + 工作表。

    `cells` 里 int 表示 `t="s"`（共享字符串下标）、str 表示字面值 —— 这样夹具本身
    就覆盖了"下标回填"这条最容易写错的逻辑。
    """
    sst = "".join(f"<si><t>{s}</t></si>" for s in strings)
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("[Content_Types].xml", "<Types/>")
        z.writestr("xl/workbook.xml", "<workbook/>")
        z.writestr("xl/sharedStrings.xml",
                   '<?xml version="1.0"?><sst xmlns="%s">%s</sst>' % (_NS_S, sst))
        for idx, rows in enumerate(sheets, 1):
            xml = [f'<?xml version="1.0"?><worksheet xmlns="{_NS_S}"><sheetData>']
            for r, cells in enumerate(rows, 1):
                xml.append(f'<row r="{r}">')
                for c, cell in enumerate(cells, 1):
                    ref = "%s%d" % (chr(64 + c), r)
                    if isinstance(cell, int):
                        xml.append(f'<c r="{ref}" t="s"><v>{cell}</v></c>')
                    else:
                        xml.append(f'<c r="{ref}"><v>{cell}</v></c>')
                xml.append("</row>")
            xml.append("</sheetData></worksheet>")
            z.writestr("xl/worksheets/sheet%d.xml" % idx, "".join(xml))
    return path


def make_badxml_docx(path):
    """ZIP 结构**可读**，但 `word/document.xml` 是半截 XML。

    这才是"损坏文档"的**可达路径**：ZIP 能打开 ⇒ 走到 OOXML 通路 ⇒ XML 解析抛异常
    ⇒ 必须被降级成说明节点。**不要**用"ZIP 本身坏掉"来测这条 ——
    那种文件 `_peek_names` 返回空、根本进不了 OOXML 通路（测的是另一条分支）。
    """
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("word/document.xml", "<not-closed")
    return path


def make_plain_zip(path):
    """普通 ZIP（无任何 OOXML 条目）—— 守"魔数 + 内部条目"两步判定。"""
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("readme.txt", "hello")
        z.writestr("data.bin", bytes(range(256)))
    return path


def make_pptx(path, slide_texts):
    """最小 .pptx：每页一个 `ppt/slides/slideN.xml`，文字经 `a:p`/`a:t`。

    **刻意支持 10 页以上**：条目名是 `slide1`~`slide10`，纯字符串排序会把 `slide10`
    排到 `slide2` 前面 ⇒ 页序错乱。夹具必须能暴露这个（`slide_texts` 给 10+ 条）。
    """
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("[Content_Types].xml", "<Types/>")
        z.writestr("ppt/presentation.xml", "<p:presentation/>")
        for idx, text in enumerate(slide_texts, 1):
            z.writestr(
                "ppt/slides/slide%d.xml" % idx,
                '<?xml version="1.0"?><p:sld xmlns:a="%s" '
                'xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">'
                "<p:cSld><a:p><a:r><a:t>%s</a:t></a:r></a:p></p:cSld></p:sld>"
                % (_NS_A, text))
    return path


def make_png(path, width=8, height=8):
    """生成一张**结构合法**的最小 PNG（宽高可指定）。

    为什么要真的拼一个合法 PNG（而不是"以 PNG 魔数开头的字节"）：
    速览侧的判定只看 magic，那种夹具够用；但**同一批夹具还要喂给宿主渲染层**
    （协议 §3.6 的 `image` 节点），而 `BitmapImage` 会真解码 —— 半截字节会直接
    进 `catch` 分支。于是"图能显示"这条断言会因为**夹具不是真图**而红。
    一次造对，两边都能用。

    注意 IHDR 的 CRC 与 IDAT 的 zlib 都要对：WPF 会校验 CRC。
    """
    import struct
    import zlib

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)   # 8bit truecolor
    # 每行 = 1 字节 filter(0) + width*3 字节 RGB。
    # 颜色只跟**列号**有关（不是随机、不含时间），这样同一夹具每次生成完全一致 ——
    # 否则"文件字节相同"这类断言会莫名抖动。
    row_pixels = b"".join(bytes([(col * 32) % 256, 64, 200]) for col in range(width))
    raw = b"".join(b"\x00" + row_pixels for _ in range(height))
    data = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))
    return write_bytes(path, data)


def make_gif(path):
    """最小合法 GIF89a（1×1 像素）。

    选 GIF 而不是再加一张 PNG，是为了让"多格式都认得"这件事**真的被测到** ——
    只测 PNG 的话，把 `_IMAGE_MAGIC` 砍到只剩 PNG 一条也能全绿。
    """
    return write_bytes(path, (
        b"GIF89a"                       # 头
        b"\x01\x00\x01\x00\x80\x00\x00"  # 逻辑屏幕 1x1、全局色表
        b"\x00\x00\x00\xff\xff\xff"      # 色表：黑 / 白
        b"\x2c\x00\x00\x00\x00\x01\x00\x01\x00\x00"   # 图像描述符
        b"\x02\x02\x44\x01\x00\x3b"      # LZW + 结束符
    ))


def make_wav(path):
    """RIFF 容器但**不是** WebP（`WAVE` 而非 `WEBP`）。

    守 `_image_kind` 的第二步判定：`RIFF` 是容器不是格式，`.wav` / `.avi` 也以它开头。
    只判第一层魔数的话，一个音频文件会被塞进 `<Image>` ⇒ 渲染层解码失败 ⇒
    用户看到"无法显示图片"，而真相是"这压根不是图片"。夹具必须是**真 WAV 头**，
    否则测不出"第二步判定缺失"这个 bug（半截字节会被其它原因拦下）。
    """
    return write_bytes(path, (
        b"RIFF" + (36).to_bytes(4, "little") + b"WAVE"
        + b"fmt " + (16).to_bytes(4, "little")
        + (1).to_bytes(2, "little") + (1).to_bytes(2, "little")
        + (8000).to_bytes(4, "little") + (8000).to_bytes(4, "little")
        + (1).to_bytes(2, "little") + (8).to_bytes(2, "little")
        + b"data" + (0).to_bytes(4, "little")
    ))


def make_zip_bomb(path):
    """ZIP 炸弹：解压后 ~40 MB 的 document.xml，压缩后只有几十 KB。

    ZIP 的极端压缩比是 OOXML 通路**独有**的风险面（PDF 通路没有这件事）——
    不设解压上限的话，一次速览就能把内存吃光 / 把工具进程拖死。
    """
    big = ("<w:document>"
           + ("<w:p><w:r><w:t>x</w:t></w:r></w:p>" * 800000)
           + "</w:document>")
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", "<Types/>")
        z.writestr("word/document.xml", big)
    return path


def nodes_of(payload):
    """从 `ezt panel ... --json` 的输出里取 nodes（形状见 _step15_w2c.sh）。"""
    return ((payload.get("data") or {}).get("nodes")) or []


def texts(nodes):
    return [n.get("text", "") for n in nodes if n.get("type") == "text"]


def kind_of(nodes):
    for n in nodes:
        if n.get("type") == "kv":
            for pair in n.get("items", []):
                if len(pair) == 2 and pair[0] == "类型":
                    return pair[1]
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True)
    ap.add_argument("--ez", required=True, help="ezt.exe 的绝对路径")
    ap.add_argument("--install-root", required=True)
    ap.add_argument("--config-root", required=True)
    ap.add_argument("--dir", default=None, help="夹具目录（默认 <repo>/_scratch/preview-verify）")
    # 可选：显式指定工具目录源。用途 = 用**已有的已部署运行时**快速自测，
    # 同时保证被测的是仓库里的 preview（而不是安装根里的旧副本）。
    ap.add_argument("--tools-dir", default=None)
    args = ap.parse_args()

    def base_cmd():
        cmd = []
        if args.tools_dir:
            cmd += ["--tools-dir", args.tools_dir]
        return cmd + ["--install-root", args.install_root, "--config-root", args.config_root]

    work = args.dir or os.path.join(args.repo, "_scratch", "preview-verify")
    os.makedirs(work, exist_ok=True)

    # ── 夹具：四种典型输入 ────────────────────────────────────────────────
    # 每个夹具都要有**明确的正/负预期**：只造"该告警的"不造"不该告警的"，
    # 会让"一律告警"这种坏实现照样全绿（弱断言）。
    fx = {
        "utf8": write_bytes(os.path.join(work, "utf8.txt"),
                            "第一行中文\n第二行 hello\n"),
        "gbk": write_bytes(os.path.join(work, "gbk.txt"),
                           "中文内容测试编码".encode("gbk")),
        "bom": write_bytes(os.path.join(work, "bom.txt"),
                           b"\xef\xbb\xbf" + "带BOM的文本".encode("utf-8")),
        # ── 二进制：**非图片**的真二进制（含 NUL、无任何图片魔数）──────────────
        #    ⚠️ 2026-09-23 起**不能**再用 `\x89PNG...` 当这个夹具了：
        #    图片已有 `image` 节点通路，PNG magic 会被 `_image_kind` 拦下走图片分支，
        #    于是"二进制文件"这条断言会因为**夹具选错**而红 —— 那是夹具的错，不是代码的错。
        #    改用 ELF 头（`\x7fELF`）：真二进制、含 NUL、且**不在**已知图片/文档魔数表里。
        "bin": write_bytes(os.path.join(work, "blob.bin"),
                           b"\x7fELF\x02\x01\x01" + bytes(8) + bytes(72)),
        # ── UTF-16 / UTF-32 文本：NUL 启发式会把它们误判成二进制（ASCII 字符自带 NUL），
        #    必须靠 BOM 先于 NUL 判定救回来。**注意 `utf-16` 才写 BOM**，
        #    `utf-16-le` / `utf-16-be` 不写 —— 用错编解码器会让夹具本身失去意义。
        "u16": write_bytes(os.path.join(work, "u16.txt"),
                           "中文ABC\n第二行".encode("utf-16")),
        "u16be": write_bytes(os.path.join(work, "u16be.txt"),
                             b"\xfe\xff" + "中文ABC".encode("utf-16-be")),
        "u32": write_bytes(os.path.join(work, "u32.txt"),
                           "中文ABC".encode("utf-32")),
        # ── 两类**伪**文本：绝不能被 BOM 放行（反向夹具，见 16.8 / 16.9）────────
        #    ① `FF FE` 是真实的二进制魔数 —— MPEG 音频帧同步就落在这段，
        #       没打 ID3 标签的 MP3 完全可能以它开头。这个夹具含 NUL ⇒ 会撞到二进制门，
        #       正是"BOM 旁路"必须被 `_looks_like_text` 挡住的场景。
        "fakemp3": write_bytes(os.path.join(work, "fake.bin"),
                               b"\xff\xfe\x00\x01\x02\x03\xff\x7f\x00\xff\xfe\x00\xaa\xbb"),
        #    ② BOM 声明 UTF-16 但**长度是奇数** ⇒ 算术上就不可能是该编码的文本。
        "odbom": write_bytes(os.path.join(work, "odd.bin"),
                             b"\xff\xfe" + bytes(range(1, 120))),
        # ── PDF 三态：能提取 / 无文字层 / 结构损坏（各有专门降级文案）────────────
        #    为什么三种都要：它们的**下一步动作完全不同**（看内容 / 用别的工具 / 修文件），
        #    只造正向夹具的话，"降级文案写错或缺失"这条永远不会红。
        "pdf_text": write_bytes(os.path.join(work, "with-text.pdf"),
                                make_pdf(b"BT /F1 18 Tf 72 700 Td ("
                                         + PDF_SAMPLE_TEXT.encode("latin-1") + b") Tj ET")),
        "pdf_blank": write_bytes(os.path.join(work, "blank.pdf"),
                                 make_pdf(b"0 0 200 200 re f")),
        "pdf_broken": write_bytes(os.path.join(work, "broken.pdf"),
                                  b"%PDF-1.4\n" + bytes(range(256)) * 4),
        # ── 缺陷 ③ 的**原始夹具**：未压缩、前 4096 字节全是 ASCII 的最小 PDF ──────
        #    在加 PDF 通路之前，它会因为没有 NUL 而落到文本通路，把 PDF 源码当正文显示
        #    （`%PDF-1.4 1 0 obj << /Type /Catalog …`）。现在必须先被 magic 拦下。
        "pdf_noxref": write_bytes(os.path.join(work, "plain-noxref.pdf"),
                                  b"%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
                                  b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"
                                  b"trailer\n<< /Root 1 0 R >>\n%%EOF\n"),
        # ── 反向：**扩展名是 .pdf、内容却是纯文本** ──────────────────────────
        #    守"只按内容判定"这个前提：若哪天有人图省事改成按扩展名分派，这条会红。
        "fake_pdf_ext": write_bytes(os.path.join(work, "actually-text.pdf"),
                                    "这不是 PDF，只是改了扩展名。\n"),
        # ── Office（OOXML）：容器 = ZIP、内容 = XML，标准库即可提取文字 ──────────
        #    三种正向 + 三种降级 + 两组反向 + ZIP 炸弹，与 PDF 那一组同构。
        "docx": make_docx(os.path.join(work, "sample.docx"),
                          [DOCX_SAMPLE_TEXT, "Hello Word 段落二", "第三段"]),
        "xlsx": make_xlsx(os.path.join(work, "sample.xlsx"),
                          ["姓名", "年龄", "张三", "28"], [[[0, 1], [2, 3]]]),
        # ── 空文字层：docx 里全是空段落（真实场景 = 整页都是图片/图表）────────
        #    必须给"没有可提取的文字"这条专门文案：空白面板会让用户以为速览坏了。
        "docx_empty": make_docx(os.path.join(work, "empty.docx"), ["   ", ""]),
        # ── 损坏的 OOXML：ZIP 结构可读、XML 半截 ⇒ 走"降级说明"分支 ─────────────
        "docx_badxml": make_badxml_docx(os.path.join(work, "badxml.docx")),
        # ── ZIP 炸弹：解压后 ~40 MB 的 document.xml 压到几十 KB ────────────────
        #    ZIP 天然有极高压缩比（这就是为什么 OOXML 通路必须单独加解压上限）。
        #    断言只要求"被上限拦下"—— 判据是专门文案，不是"没崩"（挂了也是没崩）。
        "docx_bomb": make_zip_bomb(os.path.join(work, "bomb.docx")),
        # ── 反向：`.docx` 扩展名 + 纯文本内容 ⇒ 必须仍判「文本」───────────────
        "fake_docx_ext": write_bytes(os.path.join(work, "actually-text.docx"),
                                     "我是纯文本，只是扩展名叫 docx\n"),
        # ── 反向：**普通 ZIP**（非 OOXML）⇒ 必须仍判「二进制文件」─────────────
        #    守"两步判定"：只看 `PK` 魔数就放行的话，任何压缩包都会被当 Office 解析。
        "plain_zip": make_plain_zip(os.path.join(work, "plain.zip")),
        # ── pptx：3 页 + **12 页**（后者专门守"页序按数字排"）──────────────────
        #    3 页用来看"能不能提"；12 页用来抓 `slide10` 排到 `slide2` 前面的
        #    字符串排序 bug —— 只用 ≤9 页的话，那个 bug 永远不红。
        "pptx": make_pptx(os.path.join(work, "sample.pptx"),
                          ["第一页标题", "第二页要点", "第三页收尾"]),
        "pptx_many": make_pptx(os.path.join(work, "many.pptx"),
                               [f"第{i:02d}页" for i in range(1, 13)]),
        # ── 图片（协议 §3.6）：真 PNG + 真 GIF（多格式）+ 两种"看着像图但不是" ────
        #    正向必须用**真能解码**的图：同一批夹具要喂给宿主渲染层，
        #    半截字节过不了 BitmapImage，会让"图能显示"那条断言因夹具而红。
        "png": make_png(os.path.join(work, "real.png"), 8, 8),
        "gif": make_gif(os.path.join(work, "real.gif")),
        # ── 反向①：`.png` 扩展名 + 纯文本内容 ⇒ 必须仍判「文本」（按 magic 判，不按名字）
        "fake_png_ext": write_bytes(os.path.join(work, "actually-text.png"),
                                    "我不是图片，只是名字叫 png。\n"),
        # ── 反向②：真 WAV（RIFF 容器但非 WEBP）⇒ 必须判「二进制文件」而非图片 ——
        #    守"RIFF 要再看第 8~12 字节"这个第二步判定。
        "wav": make_wav(os.path.join(work, "sound.wav")),
        # ── 反向③：PNG magic 开头但**内容半截**（无法解码）──────────────────────
        #    速览侧会照样给 `image` 节点（它只看 magic），而宿主解码会失败 ⇒
        #    渲染成"无法显示图片"提示。这条守的是**渲染层的降级路径**。
        "png_truncated": write_bytes(os.path.join(work, "truncated.png"),
                                     b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\x0dIHDR" + bytes(40)),
    }

    # ── 20.x 命令处理器与边缘分支的夹具（2026-09-23 补齐零覆盖）──────────────
    #   为什么这批单独建：上面的 `fx` 全是"**内容判定**"的输入，而 20.x 要测的是
    #   **另一个维度** —— 命令处理器（reload / openExternal）与面板边缘分支
    #   （目录 / 多选）。它们的输入不是"一个文件"，因此不复用 `record(path)`。
    dir_fixture = os.path.join(work, "a-directory")
    os.makedirs(dir_fixture, exist_ok=True)
    dir_child_a = write_bytes(os.path.join(dir_fixture, "one.txt"), "a\n")
    dir_child_b = write_bytes(os.path.join(dir_fixture, "two.txt"), "b\n")
    # 幽灵路径：**曾经记录过、之后被删** —— 用来触发 openExternal 的 FileNotFoundError 分支。
    ghost = os.path.join(work, "ghost-gone.txt")
    write_bytes(ghost, "momentary\n")
    os.remove(ghost)

    def record(path):
        """真实通路第一步：记录选中项（等价于按 Ctrl+Alt+P 时宿主注入 inputPaths）。"""
        return subprocess.run(
            [args.ez, "invoke", "preview.show", "--quiet",
             "--json", json.dumps({"inputPaths": [path]}, ensure_ascii=False)]
            + base_cmd(),
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)

    def panel():
        """真实通路第二步：读回面板数据（新进程，靠 storage 落盘桥接）。"""
        out = subprocess.run(
            [args.ez, "panel", "preview", "main", "--json", "--quiet"] + base_cmd(),
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        try:
            return json.loads(out.stdout or "{}")
        except ValueError:
            return {}

    def record_paths(paths):
        """与 `record` 同路，但可记**多个**路径（测多选头部）。"""
        return subprocess.run(
            [args.ez, "invoke", "preview.show", "--quiet",
             "--json", json.dumps({"inputPaths": list(paths)}, ensure_ascii=False)]
            + base_cmd(),
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)

    def invoke(cmd_id, payload=None):
        """调一个命令处理器，拿回 (returncode, stdout+stderr 合并文本)。

        ⚠️ **工具抛异常时退出码是 2**（不是 0）—— 实测 `ezt invoke` 把 handler 异常
        折成一行文案 + **退出码 2**（`Program.cs` / 各 Command 的错误分支统一 `return 2`）。
        （注：`_step13_p4.sh` 里"负向用例退出 0"那条约定只适用于 `host.invokeTool`
        的**嵌套调用**结果——那里错误在 JSON 的 `code` 字段里，不冒泡成退出码。
        顶层 `ezt invoke` 是另一回事，别混用。）

        ⇒ 判据**两条都要**：退出码 == 2 **且** 文案里是**具体哪一个错**（§30.6：
        只断言"出了错"不够，必须断言"出的是哪个错"——两条降级路径互换也会过）。
        """
        r = subprocess.run(
            [args.ez, "invoke", cmd_id, "--quiet",
             "--json", json.dumps(payload or {}, ensure_ascii=False)]
            + base_cmd(),
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        return r.returncode, ((r.stdout or "") + (r.stderr or ""))

    got = {}
    for key, path in fx.items():
        r = record(path)
        if r.returncode != 0:
            info(f"记录 {key} 失败（退出码 {r.returncode}）：{(r.stderr or '')[:200]}")
        got[key] = panel()

    # ── 16.1 ★ 有效性前提 ──────────────────────────────────────────────────
    # 判据引用的数据必须先证明"真的拿到了"。全部为空时，下面"不该告警"的那几条
    # 会**恒真**（没有节点 ⇒ 当然没有告警节点）—— 那是最典型的假绿。
    n_gbk = nodes_of(got["gbk"])
    if not n_gbk:
        ck("判据有效性前提：面板数据通道返回了节点", False,
           "gbk 那条拿到 0 个节点 —— 后续所有判据都会退化，先修通路再谈判定")
        print(f"  [信息] 原始返回：{json.dumps(got['gbk'], ensure_ascii=False)[:300]}")
        return 1
    ck("判据有效性前提：面板数据通道返回了节点", True,
       f"gbk 节点数={len(n_gbk)}")

    # ── 16.2 ★ GBK 文本必须出编码告警 ─────────────────────────────────────
    gbk_texts = texts(n_gbk)
    warn = [t for t in gbk_texts if "非 UTF-8" in t or "GBK" in t]
    ck("★ GBK 中文文本 → 面板给出编码告警（不再只是默默显示乱码）",
       len(warn) == 1, f"text 节点={gbk_texts!r}")
    if warn:
        info(f"告警文案：{warn[0][:80]}")

    # ── 16.3 ★ 告警必须在正文**之前** ─────────────────────────────────────
    # 顺序也是契约：放在乱码之后，用户已经先入为主地以为文件坏了。
    idx_warn = next((i for i, n in enumerate(n_gbk)
                     if n.get("type") == "text" and ("非 UTF-8" in n.get("text", ""))), None)
    idx_body = None
    for i in range(len(n_gbk) - 1, -1, -1):
        n = n_gbk[i]
        if n.get("type") == "text" and "非 UTF-8" not in n.get("text", ""):
            idx_body = i
            break
    ck("★ 告警出现在正文之前（先解释、再展示可能损坏的内容）",
       idx_warn is not None and idx_body is not None and idx_warn < idx_body,
       f"warn@{idx_warn} body@{idx_body}")

    # ── 16.4 ★ 反向：正常 UTF-8 不得告警 ───────────────────────────────────
    # 没有这条，"一律告警"的错误实现照样全绿（弱断言）。
    utf8_texts = texts(nodes_of(got["utf8"]))
    utf8_warn = [t for t in utf8_texts if "非 UTF-8" in t]
    ck("反向：正常 UTF-8 文本**不**出现编码告警（防「一律告警」）",
       not utf8_warn and any("第一行中文" in t for t in utf8_texts),
       f"text 节点={utf8_texts!r}")

    # ── 16.5 BOM 被吞掉，且同样不告警 ─────────────────────────────────────
    bom_texts = texts(nodes_of(got["bom"]))
    bom_body = [t for t in bom_texts if "带BOM的文本" in t]
    ck("带 BOM 的 UTF-8：BOM 被吞掉（正文不含 U+FEFF）且不告警",
       len(bom_body) == 1 and "\ufeff" not in bom_body[0]
       and not [t for t in bom_texts if "非 UTF-8" in t],
       f"text 节点={bom_texts!r}")

    # ── 16.6 ★ 二进制：判定 + 提示语指路 ──────────────────────────────────
    bin_nodes = nodes_of(got["bin"])
    bin_kind = kind_of(bin_nodes)
    bin_texts = texts(bin_nodes)
    pointed = [t for t in bin_texts if "用系统程序打开" in t]
    ck("★ 二进制文件 → 只给元信息，且提示语指路「用系统程序打开」",
       bin_kind == "二进制文件" and len(pointed) >= 1,
       f"类型={bin_kind!r} text={bin_texts!r}")

    # ── 16.7 正向对照：文本文件不得被判成二进制 ────────────────────────────
    # 与 16.6 成对 —— 只有"该判文本的判对了"，16.6 才能说明问题是内容而非一刀切。
    ck("正向对照：UTF-8 文本被判为「文本」（不是二进制）",
       kind_of(nodes_of(got["utf8"])) == "文本",
       f"类型={kind_of(nodes_of(got['utf8']))!r}")

    # ── 16.8 ★★ UTF-16 / UTF-32 文本不得被误判为二进制 ─────────────────────
    # NUL 启发式对它们**必然误判**：ASCII 字符在 UTF-16 里是 `41 00`，自带 NUL 字节。
    # 所以这条守的是"BOM 判定**先于** NUL 判定"这个顺序契约 —— 与 16.3 的告警顺序同源，
    # 都是"顺序即契约"。修复前实测：UTF-16 的 txt 被显示成"二进制文件，不预览内容"。
    for label, key in (("UTF-16（含 BOM）", "u16"),
                       ("UTF-16BE（含 BOM）", "u16be"),
                       ("UTF-32（含 BOM）", "u32")):
        ns = nodes_of(got[key])
        ts = texts(ns)
        body = [t for t in ts if "中文ABC" in t]
        warned = [t for t in ts if "非 UTF-8" in t]
        ck(f"★ {label} 判为「文本」、正文正确且无编码告警",
           kind_of(ns) == "文本" and len(body) == 1 and not warned,
           f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 16.9 ★ 反向：`FF FE` 开头的**真二进制**不得被 BOM 放行 ──────────────
    # 没有这条，"BOM 一律放行"的坏实现照样全绿 —— 而那会把 MP3 显示成满屏乱码，
    # 比"判成二进制"更糟：用户看到的那堆字**看起来就像文件坏了**。
    # （`FF FE` 是真实魔数：MPEG 音频帧同步 `FF Ex`~`FF Fx` 就在这一段。）
    ns = nodes_of(got["fakemp3"])
    ck("★ 反向：以 FF FE 开头的二进制仍判「二进制文件」（BOM 不无条件放行）",
       kind_of(ns) == "二进制文件",
       f"类型={kind_of(ns)!r} text={texts(ns)!r}")

    # ── 16.10 ★ 反向：BOM 声明宽字符编码、但长度是奇数 ⇒ 判二进制 ───────────
    # 放它走文本通路会显示乱码**外加一条误导性告警**（"含非 UTF-8 字节"）——
    # 把"这压根不是文本"说成"编码选错了"，两者指向的修法完全不同，
    # 而用户手里只有那条告警可看。
    ns = nodes_of(got["odbom"])
    ck("★ 反向：BOM + 奇数长度（算术上不可能是 UTF-16）判「二进制文件」，且不出误导性编码告警",
       kind_of(ns) == "二进制文件" and not [t for t in texts(ns) if "非 UTF-8" in t],
       f"类型={kind_of(ns)!r} text={texts(ns)!r}")

    # ── 16.11 ★★ PDF 提取出**真实文字**（不是只把类型标成 PDF）────────────────
    # 这条必须断言**正文内容**而不只是"类型=PDF"：只标类型的话，
    # "提取逻辑整段失效但元信息照常渲染"也会绿 —— 而那正是最容易发生的情况。
    ns = nodes_of(got["pdf_text"])
    ts = texts(ns)
    ck("★★ 带文字层的 PDF → 类型=PDF，且**正文是提取出的文字**",
       kind_of(ns) == "PDF" and any(PDF_SAMPLE_TEXT in t for t in ts)
       and not any("二进制文件" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 16.12 ★ 无文字层（扫描件 / 图片版）→ 专门文案，不是空白 ──────────────
    # 空白面板会让用户以为"速览坏了"；必须说清"是没有文字层"，并指路系统程序。
    ns = nodes_of(got["pdf_blank"])
    ts = texts(ns)
    ck("★ 无文字层的 PDF → 类型=PDF 且给出「没有可提取的文字层」说明（不是空白）",
       kind_of(ns) == "PDF" and any("没有可提取的文字层" in t for t in ts)
       and any("用系统程序打开" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 16.13 ★ 结构损坏的 PDF → 降级为说明，**不崩** ────────────────────────
    # 面板的契约是"任何失败都变成一个 text 节点，绝不抛"（面板错误不弹对话框）。
    ns = nodes_of(got["pdf_broken"])
    ts = texts(ns)
    ck("★ 结构损坏的 PDF → 类型=PDF + 失败说明，不抛不崩（节点结构完整）",
       kind_of(ns) == "PDF" and len(ns) >= 2
       and any("读取 PDF 失败" in t for t in ts),
       f"节点数={len(ns)} 类型={kind_of(ns)!r} text={ts!r}")

    # ── 16.14 ★ 反向：扩展名是 `.pdf` 但内容是纯文本 ⇒ 仍判「文本」───────────
    # 守"只按内容判定"这个贯穿全模块的前提。若有人把分派改成按扩展名，
    # 这条会立刻红 —— 而那时"改了扩展名的文件被误判"这类问题才会开始出现在用户侧。
    ns = nodes_of(got["fake_pdf_ext"])
    ts = texts(ns)
    ck("★ 反向：`.pdf` 扩展名但内容是纯文本 ⇒ 判「文本」（判定按内容，不按扩展名）",
       kind_of(ns) == "文本" and any("这不是 PDF" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 16.15 ★★ 缺陷 ③ 的守卫：未压缩 PDF **不得**把源码当正文显示 ─────────
    # 这个夹具前 4096 字节全是 ASCII（无 NUL），在加 PDF 通路之前会落到文本通路，
    # 面板里出现的是 `%PDF-1.4 1 0 obj << /Type /Catalog …` 这种 PDF 源码 ——
    # 一条**静默的假成功**（退出码 0、节点结构完整、只是内容对用户毫无意义）。
    # 现在它必须先被 magic 拦到 PDF 通路，要么给文字、要么给明确说明 —— 两条都不能是源码。
    ns = nodes_of(got["pdf_noxref"])
    ts = texts(ns)
    ck("★★ 缺陷③守卫：未压缩（无 NUL）PDF 不再把源码当正文显示，而是给出说明",
       kind_of(ns) == "PDF"
       and not any("%PDF-" in t for t in ts)
       and any("用系统程序打开" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ══ 17.x Office（OOXML）：docx / xlsx 文字层 ═══════════════════════════════
    # 与 16.11~16.15 的 PDF 组同构：正向看**正文内容**、降级看**专门文案**、
    # 反向看"不误判"。为什么断言内容而不只标类型：只标类型的话，
    # "提取整段失效但元信息照常渲染"照样绿 —— 那正是最容易发生的情况。

    # ── 17.1 ★★ docx → 类型=Word 且正文是提取出的文字 ──────────────────────
    ns = nodes_of(got["docx"])
    ts = texts(ns)
    ck("★★ docx → 类型=Word，且**正文是提取出的文字**（三段齐全、不显示二进制提示）",
       kind_of(ns) == "Word" and any(DOCX_SAMPLE_TEXT in t for t in ts)
       and any("第三段" in t for t in ts)
       and not any("二进制文件" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 17.2 ★★ xlsx → 表格内容正确（含 sharedStrings 回填）─────────────────
    # 这条守的是**下标回填**：`t="s"` 的单元格存的是共享字符串下标，
    # 不回填的话面板上会出现一串数字（`0`、`1`…），看起来像"读到了乱码"。
    # 断言里同时要有"回填后的文字"和"反例"（不能出现裸下标当内容）。
    ns = nodes_of(got["xlsx"])
    ts = texts(ns)
    body = "\n".join(ts)
    ck("★★ xlsx → 类型=Excel，表格文字正确（sharedStrings 已回填为文字，不是下标）",
       kind_of(ns) == "Excel" and "姓名" in body and "张三" in body
       and "28" in body,
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 17.3 ★ 反向：`.docx` 扩展名 + 纯文本内容 ⇒ 仍判「文本」──────────────
    ns = nodes_of(got["fake_docx_ext"])
    ts = texts(ns)
    ck("★ 反向：`.docx` 扩展名但内容是纯文本 ⇒ 判「文本」（同 PDF，判定按内容）",
       kind_of(ns) == "文本" and any("我是纯文本" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 17.4 ★★ 反向：普通 ZIP（非 OOXML）⇒ 仍判「二进制文件」─────────────
    # 守"魔数 + 内部条目"两步判定。只看 `PK` 魔数就放行的话，
    # 任何压缩包（.zip/.jar/.apk）都会被当成 Office 文档去解析。
    ns = nodes_of(got["plain_zip"])
    ck("★★ 反向：普通 .zip（无 OOXML 条目）仍判「二进制文件」（两步判定生效）",
       kind_of(ns) == "二进制文件" and any("用系统程序打开" in t for t in texts(ns)),
       f"类型={kind_of(ns)!r} text={texts(ns)!r}")

    # ── 17.5 ★ 空文字层 docx → 专门说明 + 指路，不白屏 ─────────────────────
    ns = nodes_of(got["docx_empty"])
    ts = texts(ns)
    ck("★ 无文字层 docx（全图片/图表）→ 类型=Word + 「没有可提取的文字」说明（不白屏）",
       kind_of(ns) == "Word" and any("没有可提取的文字" in t for t in ts)
       and any("用系统程序打开" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 17.6 ★ 非法 XML（ZIP 结构可读、XML 解析失败）→ 降级说明，不崩 ────────
    # 这是"损坏"的**真正可达路径**：ZIP 能打开 ⇒ 走到 OOXML 通路；
    # XML 半截 ⇒ 解析抛异常 ⇒ 必须变成 text 节点而非冒泡（面板不弹对话框）。
    ns = nodes_of(got["docx_badxml"])
    ts = texts(ns)
    ck("★ 非法 XML 的 docx → 类型=Word + 失败说明，不抛不崩（节点结构完整）",
       kind_of(ns) == "Word" and len(ns) >= 2
       and any("失败" in t or "运行" in t for t in ts),
       f"节点数={len(ns)} 类型={kind_of(ns)!r} text={ts!r}")

    # ── 17.7 ★★ ZIP 炸弹 → 被解压上限拦下（非 OOM / 非挂死）────────────────
    # ZIP 的极端压缩比是 OOXML 通路**独有**的新风险面（PDF 通路没有）。
    # 判据落在"专门文案"上 —— 光看"没崩"是弱断言：真炸了可能进程直接死，
    # 而那会让整条通路静默缺失，反而更糟。
    ns = nodes_of(got["docx_bomb"])
    ts = texts(ns)
    ck("★★ ZIP 炸弹（解压后 40 MB）→ 被上限拦下，给出「过大」说明（非 OOM / 非挂死）",
       kind_of(ns) == "Word" and any("过大" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ══ 18.x PowerPoint（pptx）：第三格式 ═════════════════════════════════════
    # pptx 的提取函数与 docx/xlsx 同时写好，但此前**零断言** —— 按"验收在功能之前"
    # 的纪律补齐。它与前两者的**结构性差异**是"多页 + 页序"，所以断言多一条排序契约。

    # ── 18.1 ★★ pptx → 类型=PowerPoint，且正文是提取出的文字 ────────────────
    ns = nodes_of(got["pptx"])
    ts = texts(ns)
    body = "\n".join(ts)
    ck("★★ pptx → 类型=PowerPoint，且**正文是提取出的文字**（各页文字齐全）",
       kind_of(ns) == "PowerPoint" and "第一页标题" in body and "第三页收尾" in body
       and not any("二进制文件" in t for t in ts),
       f"类型={kind_of(ns)!r} text={ts!r}")

    # ── 18.2 ★★ 多页顺序契约：页序按**数字**排，不按字符串 ────────────────────
    # 这是 pptx 独有的坑：条目名 `slide1`…`slide10`，字符串排序会把 `slide10` 排到
    # `slide2` **之前** ⇒ 10 页以上的演示文稿页序错乱。断言落在**相邻两页的相对位置**
    # 上（`第02页` 必须出现在 `第10页` 之前），这才真正守住"数字排序"。
    # ⚠️ 不能用 ≤9 页的夹具 —— 那样字符串排序恰好等于数字排序，bug 永远不红。
    ns = nodes_of(got["pptx_many"])
    ts = texts(ns)
    body = "\n".join(ts)
    p02 = body.find("第02页")
    p10 = body.find("第10页")
    p12 = body.find("第12页")
    ck("★★ 12 页 pptx 的页序按数字排（第02页 在 第10页 之前，第12页 在最后）",
       kind_of(ns) == "PowerPoint" and 0 <= p02 < p10 < p12 >= 0,
       f"02@{p02} 10@{p10} 12@{p12}（负值=未找到）")

    # ── 18.3 ★ 反向：普通 ZIP 也得同时守住 pptx 分支不越权 ────────────────────
    # （复用 17.4 的夹具）确认"加 pptx 分支"没有把别的 ZIP 也拉进来 ——
    # 三个格式共用一个分派点，任何一处判定写松都会让普通 zip 误判。
    ns = nodes_of(got["plain_zip"])
    ck("★ 反向：加 pptx 分支后，普通 .zip 仍判「二进制文件」（三分支互不越权）",
       kind_of(ns) == "二进制文件",
       f"类型={kind_of(ns)!r}")

    # ══ 19.x 图片（image 节点，协议 §3.6）════════════════════════════════════
    #   这是"二进制文件只看元信息"这条限制**第一次被松开**，所以要守的比前面几组更多：
    #   既要"真图被认出来"，也要"看着像图的不是图"，还要"是图但不能显示时降级对不对"。
    #   缺任何一组，都可能出现"把 WAV 当图塞进 <Image>"或"把改了名的文本当图"这类问题。

    def image_nodes(ns):
        return [n for n in ns if n.get("type") == "image"]

    # ── 19.1 ★★ 真 PNG → 出 `image` 节点，且 path 指向该文件 ──────────────────
    ns = nodes_of(got["png"])
    imgs = image_nodes(ns)
    ck("★★ 真 PNG → 面板给出 image 节点（不再只说「二进制文件」）",
       kind_of(ns) == "PNG 图片" and len(imgs) == 1,
       f"类型={kind_of(ns)!r} image节点数={len(imgs)}")
    if imgs:
        # path 必须是**宿主能直接打开**的那个真实路径 —— 不是文件名、不是相对路径。
        # 判据落"这个文件真的存在"，而不是"字段非空"：后者一条 `path=""` 也能过。
        p = imgs[0].get("path", "")
        ck("★ image 节点的 path 是指向该文件的绝对路径（宿主读得到）",
           os.path.isabs(p) and os.path.isfile(p)
           and os.path.samefile(p, fx["png"]),
           f"path={p!r}")

    # ── 19.2 ★ GIF 也认得（防"魔数表只留 PNG 也能全绿"）──────────────────────
    ns = nodes_of(got["gif"])
    ck("★ GIF 图片同样出 image 节点（魔数表不止 PNG 一条）",
       kind_of(ns) == "GIF 图片" and len(image_nodes(ns)) == 1,
       f"类型={kind_of(ns)!r}")

    # ── 19.3 ★ 反向：`.png` 扩展名 + 纯文本内容 ⇒ 仍判「文本」────────────────
    #  与 16.x 的 `fake_pdf_ext` / 17.x 的 `fake_docx_ext` 同一套路：
    #  守"只按内容判定"这个贯穿全项目的前提。谁图省事改成按扩展名分派，这条就红。
    ns = nodes_of(got["fake_png_ext"])
    ck("★ 反向：`.png` 扩展名但内容是文本 ⇒ 仍判「文本」（图片也按 magic 判）",
       kind_of(ns) == "文本" and not image_nodes(ns),
       f"类型={kind_of(ns)!r} text={texts(ns)!r}")

    # ── 19.4 ★★ 反向：真 WAV（RIFF 但非 WEBP）⇒ 判「二进制文件」，不是图片 ─────
    #  这条守 `_image_kind` 的**第二步判定**。`RIFF` 是容器不是格式（`.wav`/`.avi` 也是），
    #  只判第一层魔数 ⇒ 音频被塞进 <Image> ⇒ 渲染层解码失败 ⇒
    #  用户看到"无法显示图片"，而真相是"这压根不是图片"—— 一句会把人带偏的假诊断。
    #  （与 OOXML"魔数只说是不是 ZIP、内部条目才说是什么"完全同构。）
    ns = nodes_of(got["wav"])
    ck("★★ 反向：真 WAV（RIFF 容器但非 WEBP）判「二进制文件」，不是图片",
       kind_of(ns) == "二进制文件" and not image_nodes(ns),
       f"类型={kind_of(ns)!r}")

    # ── 19.5 ★ 半截 PNG：速览侧照样给 image 节点（它只看 magic）──────────────
    #  这条**故意**断言"工具侧不检查能否解码"——那是**宿主**的职责（协议 §3.6.3 的降级路径）。
    #  为什么值得钉住这个分工：工具侧若也去解码一遍，就得在 Python 里造一个图片解码依赖
    #  （GPL 兼容的候选很少），而宿主本来就有 WPF 的解码器。**能复用宿主能力的别在工具里重造。**
    #  对应的宿主侧降级断言在 `verify-desktop.py` §1c（19.6）。
    ns = nodes_of(got["png_truncated"])
    ck("★ 半截 PNG 仍给 image 节点（解码与否是宿主职责，工具侧只看 magic）",
       len(image_nodes(ns)) == 1,
       f"image节点数={len(image_nodes(ns))} 类型={kind_of(ns)!r}")

    # ── 19.6 ★ 反向：普通 ZIP / PDF 等既有通路不得被图片分支抢走 ─────────────
    #  分派是"一条 if 链"，新增分支最常见的破坏是**位置放错**（放在二进制门之后 = 等于没做）
    #  或**条件写松**（把 PK 开头也当图）。用已有夹具做一次回归，成本极低。
    ns_pdf = nodes_of(got["pdf_text"])
    ck("★ 反向：加图片分支后，PDF / Office / 文本三路判定均未越权",
       kind_of(ns_pdf) == "PDF"
       and kind_of(nodes_of(got["docx"])) == "Word"
       and kind_of(nodes_of(got["utf8"])) == "文本"
       and not image_nodes(ns_pdf),
       f"PDF={kind_of(ns_pdf)!r} docx={kind_of(nodes_of(got['docx']))!r} "
       f"utf8={kind_of(nodes_of(got['utf8']))!r}")

    # ══ 20.x 命令处理器与边缘分支（2026-09-23 补齐零覆盖）══════════════════════
    #   上面 16~19 全部覆盖**内容判定**（`_content_nodes`）；这一组覆盖**另外两个维度**：
    #   ① 命令处理器（`reload` / `openExternal`）—— 面板按钮的落点，此前**零断言**；
    #   ② 面板边缘分支（目录 / 多选）—— 此前**零断言**。
    #   为什么这组尤其重要：`reload` 是用户**唯一的"换文件"入口**（本面板 refreshMs=0，
    #   不轮询），它跨"工具↔宿主自动重拉"这条集成路径 —— 一旦宿主侧回归，此前无任何信号。

    # ── 20.1 ★★ reload：面板"刷新"按钮的落点，返回 ok:true ────────────────────
    #  守什么：`reload` 是个**无副作用**命令（它不做任何事，靠宿主"命令完成后自动重拉"
    #  生效）。若有人误把它改成也调 `show`，会把已记录的选中项**清空**（面板按钮只传
    #  {panelId}，不带 shellSelection 上下文）—— 那正是它被设计成空操作的原因。
    rc, text = invoke("preview.reload")
    reload_ok = '"ok"' in text and "true" in text
    ck("★★ reload 处理器返回 {ok:true}（面板刷新按钮的落点，无副作用）",
       reload_ok, f"rc={rc} out={text[:120]!r}")

    # ── 20.2 ★★ reload 后重拉面板：选中项**未被动过**（守住"无副作用"这条契约）──
    #  这是 20.1 的**行为**验证（20.1 只验了返回值形状）。判据：reload 前后
    #  `panel_data` 的 heading 仍是同一个文件名 —— 若 reload 误调了 show，
    #  lastSelection 会被清空，heading 会退化成"速览"提示页。
    record(fx["utf8"])                       # 先记一个确定的选中项
    before = (nodes_of(panel()) or [{}])[0].get("text")
    invoke("preview.reload")
    after = (nodes_of(panel()) or [{}])[0].get("text")
    ck("★★ reload 不改变已记录的选中项（面板重拉后仍是同一个文件）",
       before == after == os.path.basename(fx["utf8"]),
       f"before={before!r} after={after!r} 期望={os.path.basename(fx['utf8'])!r}")

    # ── 20.3 ★★ openExternal：未记录任何文件 → 报"还没有记录"（专门文案）──────
    #  为什么必须落**文案**而不是"出错了"（S2 / §30.6 的核心教训）：
    #  "没记录任何文件"与"文件不存在"的退出码**完全一样**（都是 2）。
    #  只断言"退出码是 2"⇒ 两条降级路径互换也照样绿。必须断言**出的是哪个错**。
    #  ⚠️ 安全：这里记录**空列表**（无路径）⇒ openExternal 走"还没有记录"分支，
    #  **不会**触发 `os.startfile`（真实打开文件是本脚本必须避开的外部副作用）。
    record_paths([])
    rc, text = invoke("preview.openExternal")
    ck("★★ openExternal 未记录文件 → 退出码 2 且明确报「还没有记录任何文件」",
       rc == 2 and "还没有记录任何文件" in text and "文件不存在" not in text,
       f"rc={rc} out={text[:160]!r}")

    # ── 20.4 ★★ openExternal：文件已消失 → 报"文件不存在"（与 20.3 是**两条不同**提示）
    #  守什么：记录一个**存在过、之后被删**的路径 ⇒ 走 `os.path.exists` 失败分支。
    #  这条与 20.3 互为反向：若代码把两个 if 写反 / 合并成一句，两条里必有一条红。
    #  ⚠️ 安全：目标是**已被删除**的路径 ⇒ 也到不了 `os.startfile`。
    record(ghost)
    rc, text = invoke("preview.openExternal")
    ck("★★ openExternal 目标已消失 → 退出码 2 且明确报「文件不存在」（与 20.3 区分开）",
       rc == 2 and "文件不存在" in text and "还没有记录" not in text,
       f"rc={rc} out={text[:160]!r}")

    # ── 20.5 ★ 目录预览：类型=目录 + 子项数 + 名字列表（此前零覆盖）────────────
    #  判据落**三处**：类型标签、"N 个子项"的数字、以及子项名字**真的出现在 list 里**。
    #  只断言"类型=目录"的话，"子项列表忘渲染 / 列表为空"也照样绿。
    record(dir_fixture)
    ns = nodes_of(panel())
    kinds = [p[1] for n in ns if n.get("type") == "kv"
             for p in n.get("items", []) if len(p) == 2 and p[0] == "类型"]
    size_txt = next((p[1] for n in ns if n.get("type") == "kv"
                     for p in n.get("items", []) if len(p) == 2 and p[0] == "大小"), "")
    listed = [it for n in ns if n.get("type") == "list" for it in n.get("items", [])]
    ck("★★ 目录 → 类型=目录、子项数正确、且子项名真的列在 list 里",
       kinds[:1] == ["目录"] and "2 个子项" in size_txt
       and set(listed) >= {"one.txt", "two.txt"},
       f"类型={kinds!r} 大小={size_txt!r} 列出={listed!r}")

    # ── 20.6 ★ 反向：目录分支**不得**被文本/图片分支抢走 ────────────────────────
    #  守什么：目录 `isdir` 分支排在 `os.stat` 之后、文件探测之前。若有人把顺序挪错，
    #  目录会被 `os.path.isdir` 之后当文件读 ⇒ 报"读取失败"。这条与 20.5 的差别是
    #  它断言**没有**错误节点（"读取失败"是 OSError 分支的文案）。
    dir_texts = texts(ns)
    ck("★ 反向：目录预览不出现「读取失败」（目录分支未被文件分支抢走）",
       not any("读取失败" in t or "列目录失败" in t for t in dir_texts),
       f"texts={dir_texts!r}")

    # ── 20.7 ★★ 多选：只预览第 1 个 + 明确列出其余（此前零覆盖）────────────────
    #  竞品对照（PowerToys Peek / QuickLook）：它们的多选是"在选中集内翻页"；
    #  本项目受面板协议 §11 第 1 条（只读+按钮）/ 第 3 条（无双向推送）限制，退化为"预览第 1 个
    #  + 列出其余"。这条断言**钉住当前的退化行为**，使它是**明文契约**而非静默差异。
    #  ⚠️ 若将来实现"选中集内翻页"，这条会红 —— 那正是提醒"协议 §11 已知限制要同步更新"。
    record_paths([dir_child_a, dir_child_b])
    ns = nodes_of(panel())
    head = (ns or [{}])[0].get("text")
    multi_txt = next((t for t in texts(ns) if "选中项" in t), "")
    multi_list = next((n.get("items", []) for n in ns
                       if n.get("type") == "list" and len(n.get("items", [])) == 1), [])
    ck("★★ 多选 → 只预览第 1 个（heading=第一个文件名）+ 明示「共 N 个」并列出其余",
       head == os.path.basename(dir_child_a)
       and "共 2 个选中项" in multi_txt
       and multi_list == [dir_child_b],
       f"heading={head!r} 说明={multi_txt!r} 其余={multi_list!r}")

    # ── 20.8 ★ 面板按钮契约：刷新/打开两个按钮的 commandId 与清单声明一致 ───────
    #  守什么：面板按钮只传 `{panelId}`（协议 §4.1），落点必须是**清单里声明过**的命令。
    #  一个 commandId 拼错（如 `preview.refresh`）会让按钮点了没反应，而**面板照常渲染** ——
    #  典型零信号。这里把面板数据里的按钮与 `tool.json` 的 panels[].commands 对账。
    btns = [b.get("commandId") for n in ns if n.get("type") == "buttons"
            for b in n.get("items", [])]
    with open(os.path.join(args.repo, "tools", "preview", "tool.json"), encoding="utf-8") as fh:
        manifest = json.load(fh)
    declared = next((p.get("commands", []) for p in
                     manifest["contributes"]["panels"] if p.get("id") == "main"), [])
    ck("★ 面板按钮的 commandId 都在清单 panels[].commands 里声明过（拼错即点了没反应）",
       btns and set(btns) <= set(declared) and len(btns) == 2,
       f"按钮={btns!r} 声明={declared!r}")

    # ══ 21.x `input` 节点（协议 §3.7，V1.3）════════════════════════════════════
    #  为什么这组能在这里（CLI 里）断言：`input` 与 `image` 不同 —— 它能用一行文字完整
    #  表达（§12.2 第 2 步）。所以契约层 + 渲染分支可以**完全不依赖 GUI** 验穿。
    #  WPF 才需要的三条（21.2 控件计数 / 21.4 失焦不回传 / 21.5 失焦后不带旧值 / 21.6 重绘不吞字）
    #  留给 verify-desktop.py —— 见本组末尾的"覆盖面声明"。
    #
    #  ⚠️ 编号 21.x 而非 20.x：20.x 已被上面"命令处理器与边缘分支"占用（2026-09-23 让号）。
    #  协议 §3.7.8 已同步改为 21.x —— 两边必须一致，否则引用时对不上。

    # 夹具：paneltool 的 `input` 面板（合法 / 重复 key / 未声明 submitCommandId / 缺 key 四情形同载荷）
    def pt_panel(panel_id):
        out = subprocess.run(
            [args.ez, "panel", "paneltool", panel_id, "--json", "--quiet"] + base_cmd(),
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        try:
            return json.loads(out.stdout or "{}")
        except ValueError:
            return {}

    def pt_text(panel_id):
        out = subprocess.run(
            [args.ez, "panel", "paneltool", panel_id, "--text", "--quiet"] + base_cmd(),
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        return out.returncode, (out.stdout or "") + (out.stderr or "")

    inp = pt_panel("input")
    inp_nodes = nodes_of(inp)

    # ── 21.1 ★★ CLI 渲染 `input` → 可读表示（不是「未知节点类型」）────────────────
    #  这条是"CLI 必须支持 input"的兑现点。判据四条一起：
    #   ① 有 `key=q` 的输入框行（`[输入框 key=q value=`）；
    #   ② **没有**「未知节点类型 'input'」（否则就是被当未知类型吃掉了）；
    #   ③ 合法节点的 placeholder / submitCommandId 也带出来了（只印 key 会漏掉半条契约）；
    #   ④ 值用**引号**包着（否则空串 `""` 与空格 `" "` 在输出里长得一样 ——
    #     而 21.6 恰恰要区分"没输入"和"输入了空白"，见 PanelCommand.RenderNode 注释）。
    #
    #  ⚠️ 断言工具与实现的格式必须**同时**对：第一版这里写的是 `[输入框 key=q value=""]`，
    #  但实现把 placeholder/submit 接在同一行的 `]` 之前 ⇒ 那个字面量**根本不存在**，
    #  断言恒假（实测踩到：21.1 红而功能其实是好的）。
    #  ⇒ 判据改成**前缀**（`[输入框 key=q value=""`），不假设后面紧跟 `]`。
    rc_t, txt_t = pt_text("input")
    ck("★★ CLI 渲染 input 节点为 [输入框 key=… value=\"…\"]（不是「未知节点类型」）",
       rc_t == 0
       and '[输入框 key=q value=""' in txt_t
       and "未知节点类型 'input'" not in txt_t
       and 'placeholder="输入关键字…"' in txt_t
       and 'submit=paneltool.ping' in txt_t,
       f"rc={rc_t} 含可读行={'[输入框 key=q value=\"\"' in txt_t} "
       f"含未知提示={'未知节点类型' in txt_t}")

    # ── 21.1b ★★ 反向：`input` 节点**不再**落在"未知类型"分支（防回归到 V1.2 行为）──
    #  与 21.1 分开写，是因为 21.1 的 `and` 链里任何一条红都只说"整体不对"，
    #  而这条**单独**指出"渲染分支丢了"这个具体回归。分开断，红了知道去哪找。
    ck("★★ 反向：input 不再被渲染成未知节点（V1.2 行为回归守卫）",
       "未知节点类型 'input'" not in txt_t,
       f"输出片段={txt_t[:200]!r}")

    # ── 21.10 ★★ 请求里 `inputs` 为**空对象**（不是 null、不是缺字段）─────────────
    #  判据落在**形状**上不是"值"上：没有 UI 来源时值必然是空的，
    #  但"字段在不在"是契约（协议 §3.7.7：新宿主**总是**带 inputs）——
    #  缺字段不会报错，只会让工具侧永远拿到空输入 ⇒ 典型静默失败。
    #  用 paneltool 的两个面板交叉验证：input 面板（有 input 节点）与 main 面板（**无** input 节点）
    #  的 inputs 形态**必须一致** —— 这正是 21.10 的反例"工具侧解析各自为政"所指。
    main_panel_payload = pt_panel("main")
    # ⚠️ 观测量从哪来：paneltool 的 input 面板把 `inputs` 的**形态**（absent / dict / …）
    # 单独回显成一条 text 节点。为什么不能只看"值是不是空串"：
    # `args.get("inputs") or {}` 会把 `{}` 和 `None` 压成同一个结果 ——
    # "宿主没带这个键"与"宿主带了空对象"在**值**上完全一样。
    # ⇒ 必须单独报形态，否则这条"形态契约"断言恒绿（典型静默失败，见 21.10 注）。
    echo_nodes = [n.get("text", "") for n in inp_nodes if n.get("type") == "text"]
    shape_line = next((t for t in echo_nodes if t.startswith("inputs 形态")), "")
    echo_line = next((t for t in echo_nodes if t.startswith("① q 收到")), "")

    ck("★★ 宿主总带 inputs 字段（工具侧观测到 dict，不是 absent/null —— 21.10 的形态契约）",
       shape_line == "inputs 形态 = dict",
       f"形态行={shape_line!r} 回显={echo_line!r} 全部text={echo_nodes!r}")

    # ── 21.8 ★★ submitCommandId 引用未声明命令 → Warning（码 contributes.panel-unknown-command）──
    #  判据两条：① **码**要在（不是"出了个警告"—— 21.8 的判据落的是码，见协议 §3.7.8 注）；
    #            ② 那个**具体的**未声明命令 id 要在文案里（否则分不清是哪个节点的问题）。
    ck("★★ input.submitCommandId 引用未声明命令 → Warning（码 contributes.panel-unknown-command）",
       "contributes.panel-unknown-command" in txt_t and "paneltool.nope" in txt_t,
       f"输出={txt_t[-400:]!r}")

    # ── 21.8b ★★ 反向：**合法**的 submitCommandId **不得**触发该 Warning ─────────
    #  这是 21.8 的**反向断言**：只有正向的话，一个"永远警告"的实现也能过 ——
    #  而"永远警告"比"不警告"更糟（用户学会无视警告）。
    #  paneltool.input 里 key=q 的 submitCommandId=paneltool.ping 是**已声明**的，
    #  所以警告的**条数**必须是 1（只有 badsubmit 那条），不能是 2。
    unknown_warn_count = txt_t.count("contributes.panel-unknown-command")
    ck("★★ 反向：已声明的 submitCommandId 不触发警告（警告数恰好 1，只有 badsubmit 那条）",
       unknown_warn_count == 1,
       f"该码出现 {unknown_warn_count} 次（应为 1）")

    # ── 21.8c ★★ key 重复 → Warning（码 panel.input-duplicate-key）───────────────
    #  口径同 §2.3 的 panels[].id：**后者被忽略**。所以文案要指明"第几个节点"，
    #  否则用户不知道是哪两个撞了。
    ck("★★ input key 重复 → Warning（码 panel.input-duplicate-key，指明重复位置）",
       "panel.input-duplicate-key" in txt_t and "key 'q'" in txt_t,
       f"输出={txt_t[-400:]!r}")

    # ── 21.8d ★★ key 缺失 → Warning（码 panel.input-missing-key）────────────────
    ck("★★ input 缺 key → Warning（码 panel.input-missing-key）",
       "panel.input-missing-key" in txt_t,
       f"输出={txt_t[-400:]!r}")

    # ── 21.9 ★★ 反向：`input` 不改变已有 6 种 + `image` 的渲染 ───────────────────
    #  "加了节点类型最容易碰坏渲染分发"（§12.2 第 6 步）。这条在 CLI 侧只断**有 input 节点的
    #  面板仍把其他类型画对**；WPF 侧的完整回归在 verify-desktop.py §1c（21.9w）。
    #  paneltool.input 面板 = 1×heading + 5×input + 2×text ⇒ heading 与 text 必须照画。
    #  （节点构成与 tools/paneltool/main.py 的 `input` 分支一一对应；改夹具要同步改这里。）
    ck("★ 反向：含 input 的面板里，其他节点类型（heading/text）照常渲染",
       any(n.get("type") == "heading" for n in inp_nodes)
       and len([n for n in inp_nodes if n.get("type") == "text"]) == 2
       and len([n for n in inp_nodes if n.get("type") == "input"]) == 5
       and "# input 节点验收" in txt_t,
       f"类型分布={[n.get('type') for n in inp_nodes]!r}")

    # ── 21.9b ★★ 反向：`image` / `main` 等其他面板**未受影响** ───────────────────
    #  与 21.9 配套：加 `input` 分支时最容易手滑的是把 `switch` 的位置写错、
    #  吃掉相邻的 `case`。这里断 `main`（6 种节点）与 `image` 面板仍返回原样。
    main_nodes = nodes_of(main_panel_payload)
    rc_i, txt_i = pt_text("image")
    ck("★★ 反向：main（6 种节点）与 image 面板在加入 input 后仍正常",
       len(main_nodes) == 6
       and [n.get("type") for n in main_nodes] ==
       ["heading", "text", "kv", "list", "separator", "buttons"]
       and rc_i == 0
       and "图片渲染验收" in txt_i,
       f"main 类型={[n.get('type') for n in main_nodes]!r} image rc={rc_i}")

    # ── 覆盖面声明（不产生断言，只写清"这组没断什么"）────────────────────────────
    #  留在这里是为了防止下一个人误以为 21.x 有遗漏。协议 §3.7.8 的 10 条全部落码：
    #    本文件（CLI）覆盖：21.1 / 21.8 / 21.9 / 21.10（形态）
    #    verify-desktop.py 覆盖：21.2（TextBox 计数）/ 21.3（回显）/ 21.4 / 21.5（失焦）/ 21.6（不吞字）/ 21.9（WPF 侧）
    #    selftest 覆盖：21.7（节流 —— 假时钟 10 条用例，见
    #      SelfTestCommand.RunInputThrottleCases；纯逻辑不依赖 GUI，这是协议
    #      §12.2 第 4 步"可用假时钟测"的兑现点）
    info("21.x 覆盖：CLI 侧 21.1/21.8/21.9/21.10 · WPF 侧 21.2/21.3/21.4/21.5/21.6/21.6b/21.9 · selftest 侧 21.7 —— 全部落码（V1.3 完结 + 复盘加固 21.6b）")

    return FAILS


if __name__ == "__main__":
    sys.exit(1 if main() else 0)
