#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ============================================================================
#  review-guards.py —— 代码审查红线静态守卫（把 docs/代码审查规范与流程.md §3.9
#  里可脚本化的部分变成可执行检查）
#
#  为什么需要它：§3.9 的红线此前只存在于文档里，"提不提"取决于审查者记不记得。
#  本脚本把其中**可机械判定**的几条固化成检查，并自带双向突变验证（--selftest）。
#
#  检查集（ID 与规范文档 §3.1~§3.6 对应）：
#    G1 🔴 静默吞异常 —— C# catch 块剥掉注释后为空（无日志 / 无抛出 / 无错误返回）
#    G2 🔴 switch 缺 default —— 语句式 switch 无 default 分支（未知值静默丢弃，S3 家族）
#    G3 🔴 验收脚本禁用模式 —— acceptance.sh / _step*.sh 里出现 `| grep -q`
#          或 `A && B || C || D`，两者都会让退出码失去意义（恒真 / 分支串味）
#    G4 🟡 幽灵代码候选 —— private 方法在源码语料里只有定义处一次出现（无调用点）
#    G5 🟡 死产物目录 —— 项目 TFM 与 bin/obj 下实际 TFM 目录不一致（踩坑全集 §2.31）
#    G6 🟡 跳过必须计数 —— 断言脚本里出现「跳过」文案却附近没有 SKIPPED 计数
#          （2026-09-28 断言语审抓到 2 条「假绿通道」后新增，见 check_g6 的说明）
#    G7 🔴 分层守卫（W7）—— `src/*/Launcher/` 的分层红线：
#          · `Eztools.Host/Launcher`（平台中立层）禁 UI/平台依赖（System.Windows*）——
#            否则打破"纯逻辑可被 selftest 测 + 编译器强制不碰 Win32"；
#          · 两侧都禁 `Eztools.Core` 引用（NFR-2 零特权）。
#          ★ Desktop 侧**允许** UI/Win32（图标提取、Shell、注册表本来就在那儿）。
#    G8 🔴 生产接线守卫（W11）—— 指定生产文件里必须能找到指定接线符号（剥注释后判）：
#          W11 的排除/限定/开窗即发各自横跨多个文件，任何一段"被人删掉/改名/没接回"都会
#          静默退回半截实现（§2.3 的 pathFilter 正是这么来的）。★ G4 对"public 方法在
#          生产零调用"有类级盲区（RI-5），且 selftest 里的引用会让它永远不红 ——
#          本守卫按**文件 + 符号**逐条钉住，§11.4 的"故意删 excluded?.Contains /
#          include?.Contains ⇒ 两条路径各红一次"由此机器化。
#
#  ⚠️ 故意不检查行宽（80/120 字符）：本仓已判定"不采纳"（长行是有意取舍，见
#     docs/README.md §3.9「代码审查报告-全面对照」行）—— --selftest 里有一条反向断言
#     钉住它，防后人顺手加回来。
#
#  级别语义：🔴 FAIL 计入失败并使退出码非 0；🟡 WARN 不计入失败但**必须落数字**
#  （不阻断 ≠ 不用管）。`--strict` 可把 WARN 升级为失败，供专项审使用。
#
#  用法：
#    python -I -X utf8 -u scripts/review-guards.py                 # 扫本仓
#    python -I -X utf8 -u scripts/review-guards.py --repo <dir>
#    python -I -X utf8 -u scripts/review-guards.py --strict        # WARN 也算失败
#    python -I -X utf8 -u scripts/review-guards.py --selftest      # 双向突变验证
#    python -I -X utf8 -u scripts/review-guards.py --json <path>
#
#  铁律（自查）：
#    · 本脚本自身不得使用 `| grep -q` —— 机器可读汇总行靠打印，不靠管道退码
#    · 夹具一律造在系统临时目录（**绝不放 docs/**）—— 否则检查器会把自己的夹具
#      当成被检查对象（docs/README.md §4.4 的自指事故）
# ============================================================================

import argparse
import json
import os
import re
import shutil
import sys
import tempfile

# ── 语料范围 ────────────────────────────────────────────────────────────────
SRC_EXT = {".cs", ".xaml", ".py", ".sh", ".ps1", ".cmd", ".bat",
           ".json", ".csproj", ".props", ".targets", ".xml", ".js", ".ts"}
SKIP_DIRS = {"bin", "obj", ".git", "node_modules", "__pycache__", ".vs",
             "PowerToys", "spike", "dist", "payload", "_scratch", "_review",
             ".workbuddy", ".idea", ".vscode"}
# 不参与"引用计数"的目录（文档是"宣称"的地方，不是"调用"的地方 —— 见 G4 说明）
REF_EXCLUDE_DIRS = SKIP_DIRS | {"docs"}

# ── C# 排除名单（G4 误报源）────────────────────────────────────────────────
GHOST_EXCLUDE_NAMES = {
    "Dispose", "DisposeAsync", "Equals", "GetHashCode", "ToString", "Finalize",
    "Main", "InitializeComponent", "GetEnumerator", "Write", "Read",
    "OnDeserialized", "OnDeserializing", "OnSerialized", "OnSerializing",
}
GHOST_EXCLUDE_PREFIX = ("On", "get_", "set_", "add_", "remove_", "op_")
GHOST_EXCLUDE_SUFFIX = ("_Click", "_Changed", "_Loaded", "_Closing", "_Closed",
                        "_Opened", "_Selected", "_TextChanged")
# 带这些特性标注的方法视为框架回调 / 反射入口，跳过
FRAMEWORK_ATTRS = ("DllImport", "LibraryImport", "Fact", "Theory", "InlineData",
                   "TestMethod", "TestInitialize", "JsonConstructor",
                   "ModuleInitializer", "Conditional", "Obsolete", "GeneratedCode",
                   "ReliabilityContract", "HandleProcessCorruptedStateExceptions",
                   "SuppressMessage", "DebuggerDisplay")

CS_EXT = {".cs"}
SKIP_FILE_SUFFIX = (".Designer.cs", ".g.cs", ".g.i.cs", ".AssemblyInfo.cs")


# ============================================================================
# C# 词法剥离：注释 / 字符串 / 字符常量 → 空格（保留换行以维持行号）
#
# ⚠️ keep_strings=True 时**只剥注释、保留字符串原文**：因为 G2 判据要找"字符串 case"
#    （`case "x":`）—— 若先把字符串洗成空格再去匹配字符串字面量，永远匹配不到
#    （自查时被 --selftest 抓出过一次，属"检查恒假"类 S2 形态）。
#    两者字符数严格 1:1，故 raw 与 stripped 的下标可直接互用。
# ============================================================================
def strip_csharp(src: str, keep_strings: bool = False) -> str:
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            while i < n and src[i] != "\n":
                out.append(" ")
                i += 1
            continue
        if c == "/" and nxt == "*":
            out.append("  ")
            i += 2
            while i < n and not (src[i] == "*" and i + 1 < n and src[i + 1] == "/"):
                out.append("\n" if src[i] == "\n" else " ")
                i += 1
            if i < n:
                out.append("  ")
                i += 2
            continue
        if c == "@" and nxt == '"':          # verbatim string
            if keep_strings:
                out.append('@"')
            else:
                out.append("  ")
            i += 2
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i + 1] == '"':
                        out.append('""' if keep_strings else "  ")
                        i += 2
                        continue
                    out.append('"' if keep_strings else " ")
                    i += 1
                    break
                out.append(src[i] if keep_strings or src[i] == "\n" else " ")
                i += 1
            continue
        if src[i:i + 3] == '"""':             # C# 11 原始字符串字面量（含 $$"""）
            # 🔴 必须处理：漏掉会让剥离器**错位**，把后面的真代码吞成"字符串"，
            #    于是真调用点在引用索引里消失 ⇒ 误报幽灵代码。
            #    实测根因：SearchWindow.cs 的 VirtualizingListTemplateXaml（第一次 --selftest
            #    没覆盖到，是靠"候选 vs grep 对不上"人工追问才抓出来的）。
            j = i
            while j < n and src[j] == '"':
                j += 1
            qn = j - i                        # 开头引号数（C# 要求结尾 ≥ 开头）
            out.append(('"' * qn) if keep_strings else (" " * qn))
            k = j
            end = None                        # ⚠️ 必须用 None 当哨兵：写成 -1 再赋元组，
            #                                     后面的 `end < 0` 会 TypeError（本脚本自查抓到过）
            while k < n:
                if src[k] == '"':
                    m = k
                    while m < n and src[m] == '"':
                        m += 1
                    if (m - k) >= qn:
                        end = (k, m)
                        break
                    k = m
                else:
                    k += 1
            if end is None:                   # 未闭合 → 吃到末尾，防错位扩散
                for ch in src[j:]:
                    out.append(ch if (keep_strings or ch == "\n") else " ")
                return "".join(out)
            for ch in src[j:end[0]]:
                out.append(ch if (keep_strings or ch == "\n") else " ")
            out.append(('"' * (end[1] - end[0])) if keep_strings
                       else (" " * (end[1] - end[0])))
            i = end[1]
            continue
        if c == '"':                          # 普通 / 插值字符串
            out.append('"' if keep_strings else " ")
            i += 1
            while i < n:
                if src[i] == "\\":
                    out.append(src[i:i + 2] if keep_strings else "  ")
                    i += 2
                    continue
                if src[i] == '"':
                    out.append('"' if keep_strings else " ")
                    i += 1
                    break
                out.append(src[i] if keep_strings or src[i] == "\n" else " ")
                i += 1
            continue
        if c == "'":                          # 字符常量
            out.append(" ")
            i += 1
            while i < n:
                if src[i] == "\\":
                    out.append("  ")
                    i += 2
                    continue
                if src[i] == "'":
                    out.append(" ")
                    i += 1
                    break
                out.append(" ")
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def line_of(text: str, idx: int) -> int:
    return text.count("\n", 0, idx) + 1


def match_brace(text: str, open_idx: int):
    """返回与 text[open_idx]=='{' 配对的 '}' 下标；找不到返回 -1。"""
    depth = 0
    i = open_idx
    n = len(text)
    while i < n:
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def match_paren(text: str, open_idx: int) -> int:
    depth = 0
    i = open_idx
    n = len(text)
    while i < n:
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def iter_files(root: str, exts, skip_dirs):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in skip_dirs]
        for fn in filenames:
            if os.path.splitext(fn)[1].lower() in exts:
                yield os.path.join(dirpath, fn)


def rel(root: str, path: str) -> str:
    try:
        return os.path.relpath(path, root).replace("\\", "/")
    except ValueError:
        return path.replace("\\", "/")


# ============================================================================
# G1 静默吞异常：catch 块剥注释后为空
#
# 三档（实测 48 处命中后定的分级 —— 见 docs/代码审查规范与流程.md §7.2）：
#   🔴 FAIL  裸空 catch：块内连注释都没有 ⇒ 零信息，必是遗漏
#   🟡 WARN  有注释的空 catch：作者是有意的，但"沉默"被写成了注释而不是**机器可见的
#            声明** ⇒ 建议改用下面的豁免标记
#   豁免     块内（或紧邻 catch 的**上一行**）写明 `review-guards:allow-empty-catch :: <理由>`，
#            且理由非空 ⇒ 跳过。**位置规则**：标记就在 catch 块内，或写在 `catch` 关键字
#            所在的上一行 —— 写在 `try` 上方**不算**（那是在标注 try，不是标注 catch；
#            放宽成宽窗会让相邻 catch 的豁免互相串味）。**豁免必须带理由**：只写标记不写
#            理由 = FAIL（否则"随手加个标记"就能取消检查，等于把闸门拆了还亮绿灯）
# ============================================================================
CATCH_RE = re.compile(r"\bcatch\b\s*(?:\([^)]*\))?\s*\{", re.S)
# ⚠️ 理由取"整行"（`[^\n]*`），**不能**用 `[^\n*]*`：迁移 48 处时实测——原作者注释里
#    有 Markdown 加粗（`**绝不能**…`），一遇到 `*` 就截断成空串，于是**合法理由被判"缺理由"**
#    （好在判据是"缺理由 = FAIL"，属于响亮失败，没变成静默漏放行）。行尾的 `*/` 单独剥掉。
EXEMPT_RE = re.compile(r"review-guards:allow-empty-catch\s*::\s*([^\n]*)")
EXEMPT_TAG = "review-guards:allow-empty-catch"


def check_g1(root: str, hits: list):
    for path in iter_files(root, CS_EXT, SKIP_DIRS):
        if path.endswith(SKIP_FILE_SUFFIX):
            continue
        raw = open(path, encoding="utf-8-sig", errors="replace").read()
        src = strip_csharp(raw)
        for m in CATCH_RE.finditer(src):
            open_idx = src.index("{", m.start())
            close_idx = match_brace(src, open_idx)
            if close_idx < 0:
                continue
            body = src[open_idx + 1:close_idx]
            if body.strip() != "":
                continue
            # raw 与 src 长度 1:1（剥离保长），下标可直接互用
            raw_block = raw[open_idx + 1:close_idx]
            # 标记只能在**本块内**或**catch 上一行**（不收 300 字符宽窗 —— 会让相邻 catch
            # 的豁免标记互相串味，等于把豁免变成通配符）
            line_start = raw.rfind("\n", 0, m.start()) + 1
            prev_line_start = raw.rfind("\n", 0, max(0, line_start - 1)) + 1
            ctx = raw_block + "\n" + raw[prev_line_start:line_start]
            ln = line_of(src, m.start())

            if EXEMPT_TAG in ctx:
                mm = EXEMPT_RE.search(ctx)
                # 剥掉可能被一起吞进来的块注释收尾符与首尾空白
                reason = re.sub(r"\s*\*/?\s*$", "", mm.group(1)).strip() if mm else ""
                if len(reason) >= 4:
                    hits.append({
                        "check": "G1", "level": "EXEMPT",
                        "file": rel(root, path), "line": ln,
                        "msg": f"显式豁免空 catch（理由：{reason}）",
                    })
                    continue
                hits.append({
                    "check": "G1", "level": "FAIL",
                    "file": rel(root, path), "line": ln,
                    "msg": f"豁免标记 `{EXEMPT_TAG}` 缺理由 —— 豁免必须写"
                           f"`:: <理由>`，否则等于把检查取消掉还亮绿灯",
                })
                continue

            has_comment = ("//" in raw_block) or ("/*" in raw_block)
            hits.append({
                "check": "G1", "level": "WARN" if has_comment else "FAIL",
                "file": rel(root, path), "line": ln,
                "msg": ("catch 块内只有注释、没有语句 —— 作者是有意的，但注释不是声明；"
                        f"请改用 `// {EXEMPT_TAG} :: <理由>` 把它变成机器可见的豁免"
                        if has_comment else
                        "catch 块完全为空（连注释都没有）—— 异常被静默吞掉，"
                        "必须落日志/计数，或写明豁免理由"),
            })


# ============================================================================
# G2 语句式 switch 缺 default
# ============================================================================
SWITCH_STMT_RE = re.compile(r"\bswitch\s*\(", re.S)
# ⚠️ 只认 `default:` 标签，**不认** `default(T)` 表达式 —— 后者不是 switch 分支，
#    算进去会让"缺 default"漏报（检查变松，绿得毫无提示）。
DEFAULT_RE = re.compile(r"\bdefault\s*:")
# 判据收窄的理由（实测后定）：switch on 封闭枚举且列全 case 时不写 default 是低风险
# 写法；**真正会静默丢未知值的是"字符串 case"这类解析路径**——那正是本仓踩过第 4 次
# 的形态（switch 无 default ⇒ 未知参数静默丢弃）。故只对含字符串 case 的 switch 报红。
STRING_CASE_RE = re.compile(r'\bcase\s+(@?"|\$")')


def check_g2(root: str, hits: list):
    for path in iter_files(root, CS_EXT, SKIP_DIRS):
        if path.endswith(SKIP_FILE_SUFFIX):
            continue
        raw = open(path, encoding="utf-8-sig", errors="replace").read()
        src = strip_csharp(raw)
        for m in SWITCH_STMT_RE.finditer(src):
            p_open = src.index("(", m.start())
            p_close = match_paren(src, p_open)
            if p_close < 0:
                continue
            j = p_close + 1
            while j < len(src) and src[j].isspace():
                j += 1
            if j >= len(src) or src[j] != "{":
                continue                      # switch 表达式 / 非法形态，不管
            b_close = match_brace(src, j)
            if b_close < 0:
                continue
            body = src[j:b_close]
            if DEFAULT_RE.search(body):
                continue
            # 找"字符串 case"必须在**保留字符串**的副本上做（剥离器把字符串洗成空格）
            body_keep = strip_csharp(raw[j:b_close], keep_strings=True)
            if not STRING_CASE_RE.search(body_keep):
                continue                      # 枚举 switch 缺 default：不报（见文件头判据说明）
            hits.append({
                "check": "G2", "level": "FAIL",
                "file": rel(root, path), "line": line_of(src, m.start()),
                "msg": "字符串 case 的 switch 无 default 分支 —— 未知值会被静默丢弃"
                       "（踩坑全集 §2.31 同族：名存实亡 flag / 静默丢弃未知参数）",
            })


# ============================================================================
# G3 验收脚本禁用模式
# ============================================================================
G3_PATTERNS = [
    (re.compile(r"\|\s*grep\s+-[A-Za-z]*q"), "`… | grep -q` —— grep -q 命中即退会让上游收 "
                                             "SIGPIPE / 退出码失去意义，断言退化为恒真"),
    (re.compile(r"&&.*\|\|.*&&"), "`A && B || C && D` —— B 成功时 D 也会执行，两份输出会串味"),
]


def check_g3(root: str, hits: list):
    sdir = os.path.join(root, "scripts")
    if not os.path.isdir(sdir):
        return
    targets = []
    acc = os.path.join(sdir, "acceptance.sh")
    if os.path.isfile(acc):
        targets.append(acc)
    for fn in sorted(os.listdir(sdir)):
        if fn.startswith("_step") and fn.endswith(".sh"):
            targets.append(os.path.join(sdir, fn))
    for path in targets:
        for ln, line in enumerate(open(path, encoding="utf-8-sig", errors="replace"), 1):
            if line.lstrip().startswith("#"):
                continue                      # 反面教材写在注释里是**对的**
            for pat, why in G3_PATTERNS:
                if pat.search(line):
                    hits.append({
                        "check": "G3", "level": "FAIL",
                        "file": rel(root, path), "line": ln, "msg": why,
                    })


# ============================================================================
# G4 幽灵代码候选：private 方法在源码语料里只有定义处一次出现
# ============================================================================
PRIVATE_METHOD_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*"
    r"\bprivate\b[ \t]+(?:static[ \t]+)?(?:async[ \t]+)?(?:unsafe[ \t]+)?"
    r"(?:partial[ \t]+)?(?:virtual[ \t]+)?(?:sealed[ \t]+)?(?:new[ \t]+)?"
    r"(?:readonly[ \t]+)?(?:extern[ \t]+)?"
    r"[\w<>\[\],\.\?]+[ \t]+([A-Za-z_]\w*)[ \t]*\(",
    re.M)


def build_identifier_index(root: str) -> dict:
    """全源码语料的标识符词频。用途：判断某方法是否"只有定义处出现过一次"。

    ⚠️ 两个必须记住的取舍（都由实测误报换来）：
      · **保留字符串内容**（keep_strings=True）：插值串 `$"{Describe(v)}"` 里的调用
        是**真调用**；若把字符串洗成空格，这类调用点会在索引里消失 ⇒ 误报幽灵代码
        （实测：ConfigValues.Describe 被误报）。
      · **剔除注释**：注释里提到方法名是"宣称"不是"调用"，不应算作引用
        （这正是幽灵代码要抓的形态）。
    """
    freq = {}
    for path in iter_files(root, SRC_EXT, REF_EXCLUDE_DIRS):
        try:
            txt = open(path, encoding="utf-8-sig", errors="replace").read()
        except OSError:
            continue
        if path.endswith(".cs"):
            txt = strip_csharp(txt, keep_strings=True)
        for word in re.findall(r"[A-Za-z_]\w*", txt):
            freq[word] = freq.get(word, 0) + 1
    return freq


def check_g4(root: str, hits: list):
    freq = build_identifier_index(root)
    for path in iter_files(os.path.join(root, "src"), CS_EXT, SKIP_DIRS):
        if path.endswith(SKIP_FILE_SUFFIX):
            continue
        raw = open(path, encoding="utf-8-sig", errors="replace").read()
        src = strip_csharp(raw)
        lines = raw.splitlines()
        for m in PRIVATE_METHOD_RE.finditer(src):
            name = m.group(1)
            if name in GHOST_EXCLUDE_NAMES:
                continue
            if name.startswith(GHOST_EXCLUDE_PREFIX) or name.endswith(GHOST_EXCLUDE_SUFFIX):
                continue
            # 构造器（名字 == 类名）不算方法
            ln = line_of(src, m.start())
            ctx = "\n".join(lines[max(0, ln - 4):ln])
            if any(a in ctx for a in FRAMEWORK_ATTRS):
                continue
            cls = re.findall(r"\b(?:class|record|struct)\s+([A-Za-z_]\w*)", src[:m.start()])
            if cls and cls[-1] == name:
                continue
            if freq.get(name, 0) <= 1:
                hits.append({
                    "check": "G4", "level": "WARN",
                    "file": rel(root, path), "line": ln,
                    "msg": f"private 方法 `{name}` 在源码语料（不含 docs 与 bin/obj）里"
                           f"仅出现 1 次 —— 疑似无调用点的幽灵代码，请人工确认"
                           f"（本仓已踩：定义 IsPidAlive() 却无人调用，'修复'其实没落盘）",
                })


# ============================================================================
# G5 死产物目录：项目 TFM 与 bin/obj 下实际 TFM 目录不一致
# ============================================================================
TFM_DIR_RE = re.compile(r"^(net|netcoreapp)\d")


def project_tfms(csproj: str):
    txt = open(csproj, encoding="utf-8-sig", errors="replace").read()
    tfms = []
    for tag in ("TargetFrameworks", "TargetFramework"):
        for m in re.finditer(rf"<{tag}[^>]*>([^<]+)</{tag}>", txt):
            tfms += [t.strip() for t in m.group(1).split(";") if t.strip()]
    return set(tfms)


def check_g5(root: str, hits: list):
    sdir = os.path.join(root, "src")
    if not os.path.isdir(sdir):
        return
    for proj in sorted(os.listdir(sdir)):
        pdir = os.path.join(sdir, proj)
        csproj = os.path.join(pdir, f"{proj}.csproj")
        if not os.path.isfile(csproj):
            continue
        allowed = project_tfms(csproj)
        if not allowed:
            continue
        for cfg in ("Debug", "Release"):
            for top in ("bin", "obj"):
                d = os.path.join(pdir, top, cfg)
                if not os.path.isdir(d):
                    continue
                for sub in sorted(os.listdir(d)):
                    if not TFM_DIR_RE.match(sub) or sub in allowed:
                        continue
                    hits.append({
                        "check": "G5", "level": "WARN",
                        "file": rel(root, os.path.join(d, sub)), "line": 0,
                        "msg": f"{proj} 的 TFM 是 {sorted(allowed)}，但 {top}/{cfg}/ 下存在"
                               f" `{sub}/` —— 死产物目录，从它启动会跑旧二进制"
                               f"（踩坑全集 §2.31，已踩 3 次：Desktop 2 次 / Cli 1 次）",
                    })


# ============================================================================
# G6 跳过必须计数（断言脚本）
#
# 判据来源：2026-09-28 断言语审抓到**两条「假绿通道」**（共 5 处断言在特定环境下
# 静默不执行且不计数）：verify-desktop 的 live 段（2 条）与 clip-ocr 语言包分支（3 条）。
# 两处的形态完全一样 —— **打了『跳过』文案，却没有 SKIPPED 递增**，于是"通过 N"变成
# 依赖环境的随机变量，而汇总行不显示跳过（两次都报"失败 0"）。
#
# 这是可机检的：**凡"给人看的跳过文案"，附近必须有计数**。
# 精度控制（避免把"断言名里含跳过"误报）：
#   · 只认**输出语句**（sh 的 printf/echo、py 的 info(...)）—— `check "…跳过…"` / `ck(…)`
#     是**断言名**不是跳过通知，一律排除；
#   · 注释行排除；
#   · 窗口 ±6 行内出现过 `SKIPPED` 即认为已计数。
# ⚠️ 它守不住"计错了数"（+1 写成 +2）—— 那需要人读，见报告 §二 的"数一遍再写"。
# ============================================================================
G6_SKIP_WORD = re.compile(r"跳过|skip", re.I)
G6_NOTICE_SH = re.compile(r"^\s*(printf|echo)\b")
G6_NOTICE_PY = re.compile(r"^\s*info\(")
G6_COUNTER = re.compile(r"SKIPPED")


def check_g6(root: str, hits: list):
    sdir = os.path.join(root, "scripts")
    if not os.path.isdir(sdir):
        return
    targets = [("sh", os.path.join(sdir, "acceptance.sh"))]
    for fn in sorted(os.listdir(sdir)):
        if fn.startswith("_step") and fn.endswith(".sh"):
            targets.append(("sh", os.path.join(sdir, fn)))
    for fn in ("verify-desktop.py", "verify-preview.py"):
        targets.append(("py", os.path.join(sdir, fn)))

    for kind, path in targets:
        if not os.path.isfile(path):
            continue
        lines = open(path, encoding="utf-8-sig", errors="replace").read().splitlines()
        for i, line in enumerate(lines):
            if line.lstrip().startswith("#"):
                continue
            if not G6_SKIP_WORD.search(line):
                continue
            is_notice = (G6_NOTICE_SH.match(line) if kind == "sh"
                         else G6_NOTICE_PY.match(line))
            if not is_notice:
                continue                       # 断言名里含"跳过"≠ 跳过通知
            lo, hi = max(0, i - 6), min(len(lines), i + 7)
            if any(G6_COUNTER.search(lines[k]) for k in range(lo, hi)):
                continue
            hits.append({
                "check": "G6", "level": "WARN",
                "file": rel(root, path), "line": i + 1,
                "msg": "出现「跳过」文案，但附近 6 行内没有 SKIPPED 计数 —— "
                       "跳过必须落数字，否则「通过 N」会随环境漂移且零信号"
                       "（2026-09-28 抓到的 2 条假绿通道就是这个形态）",
            })


# ============================================================================
# G7 分层守卫（W7 启动器）：纯逻辑层不得碰 UI / 平台 API；启动器代码不得碰特权层
#
# 判据来源：W7 设计方案 §3.2 / §12.5 —— 分层在这里**不是偏好而是硬约束**：
#   · 纯逻辑 provider 落 `Eztools.Host/Launcher`（Host 的 TFM 是 net10.0，平台中立）
#     ⇒ ① `Eztools.Cli` 与 `Eztools.Desktop` 都引用 Host ⇒ selftest 能直接测纯函数；
#        ② **编译器强制**"不碰 Win32/WPF"，而不是靠自觉；
#   · Win32 相关（Shell 扫描 / 注册表 / 图标提取）落 `Eztools.Desktop/Launcher`
#     —— 放 Host 会产生 CA1416 平台兼容告警 ⇒ 破坏"警告数 = 基线"这条验收判据；
#   · 启动器代码不得引用 `Eztools.Core`（NFR-2 零特权：files 能力的唯一入口是
#     `SearchIndexClient`）。
#
# ★ 为什么分两个作用域（★ 2026-10-01 W7-b 修正）：第一版把"禁 UI 依赖"也套到了
#   `src/Eztools.Desktop/Launcher/` 上 —— 那是**错的**：那一层本来就该有 WPF/Win32
#   （图标提取、SHGetFileInfo、注册表）。分层的判据是"**平台中立层**不得沾 UI"，
#   而不是"Launcher 目录不得沾 UI"。清单只会把自己逼到关掉（同 G4 收窄的理由）。
# ★ 为什么用 `strip_csharp`：**注释里写"为什么不碰 System.Windows"是合法的**
#   （本仓大量文档注释正是这么写的）—— 不剥注释，守卫会把自己的理由书判成违规。
# ============================================================================
G7_UI_FORBIDDEN = ("System.Windows", "System.Windows.Forms", "System.Drawing")
G7_PRIVILEGE_FORBIDDEN = "Eztools.Core"

# (作用域目录, 是否禁 UI/平台依赖) —— 只有 Host 侧平台中立层禁 UI
G7_LAYER_SCOPE = (
    (os.path.join("src", "Eztools.Host", "Launcher"), True),
    (os.path.join("src", "Eztools.Desktop", "Launcher"), False),
)


def check_g7(root: str, hits: list):
    for scope, forbid_ui in G7_LAYER_SCOPE:
        d = os.path.join(root, scope)
        if not os.path.isdir(d):
            continue                     # 目录尚未建（如 W7-b 前的 Desktop/Launcher）⇒ 跳过
        for name in sorted(os.listdir(d)):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(d, name)
            src = strip_csharp(open(path, encoding="utf-8-sig", errors="replace").read())

            # 每文件每条**类别**最多一条命中（System.Windows 是 System.Windows.Forms
            # 的子串 —— 逐 token 报会重复计数，汇总数字就不可信了）
            matched = [t for t in G7_UI_FORBIDDEN if t in src] if forbid_ui else []
            if matched:
                hits.append({
                    "check": "G7", "level": "FAIL",
                    "file": rel(root, path), "line": 0,
                    "msg": f"平台中立层（Eztools.Host）出现 UI/平台依赖 {matched} —— 它必须落 "
                           f"Eztools.Desktop/Launcher（Host 用 WPF/WinForms/Win32 会同时"
                           f"破坏可测性与 CA1416 警告基线，见 W7 设计方案 §3.2）",
                })

            if G7_PRIVILEGE_FORBIDDEN in src:
                hits.append({
                    "check": "G7", "level": "FAIL",
                    "file": rel(root, path), "line": 0,
                    "msg": f"启动器代码引用了 {G7_PRIVILEGE_FORBIDDEN} —— 违反 NFR-2 零特权；"
                           f"files 能力的唯一入口是 SearchIndexClient（W7 设计方案 §12.5）",
                })


# ============================================================================
# G8 生产接线守卫（W11）：指定生产文件里必须能找到指定接线符号
#
# 为什么存在：W11 的排除/限定/开窗即发横跨 6 个文件，任何一段被删掉（或改名没接回）
# 都会静默退回半截实现 —— §2.3 的 pathFilter（校验后丢弃）与 §0.2.2 的"无人发
# search.start"正是这个形态。G4 抓不到它们：那些符号有 selftest 引用（"断言守副本"
# 家族）或不是 private（G4 只查 private 定义无调用点，RI-5 类级盲区）。
#
# 判据：**剥注释后**（keep_strings=True —— "--exclude" 这类needle 本身在字符串字面量里）
# 指定文件含指定子串。文件缺失也是 FAIL（守卫不能因文件被移走而静默放过）。
# 两条查询路径（§2.43）各占一行 —— 删掉任一条闸的代码，对应行就红。
# ============================================================================
G8_WIRING = [
    # （仓库相对路径, 必须存在的符号, 消失时的人读解释）
    ("src/Eztools.Index/IndexRpcServer.cs", "ApplyPathFilter",
     "search.start 必须真正应用 pathFilter —— 删掉 = 退回「校验后丢弃」的半截实现（W11 §2.3 的原始缺陷）"),
    ("src/Eztools.Index/IndexBootstrap.cs", "IndexPrune.Run",
     "自举三趟法必须编排「排空 + 落盘前压缩」—— 删掉 = .ezidx 永不瘦身（W11-a D6/R11）"),
    ("src/Eztools.Index/IndexBootstrap.cs", "ApplyPathFilter",
     "自举必须应用初始限定（--path-filter 成死参数 = 调用侧断链，§0.2.2 同族）"),
    ("src/Eztools.Index/QueryEngine.cs", "MarkByPath",
     "pathFilter 路径锚定必须接到 SearchService —— 删掉 = 限定整条链失效（W11-b）"),
    ("src/Eztools.Index/QueryEngine.cs", "Excluded?.Contains(ce.Frn",
     "子集路径的排除闸 —— 删掉 = 同一查询在两条路径上结果不一致（§2.43）"),
    ("src/Eztools.Index/QueryEngine.cs", "excluded.Contains(frns",
     "全扫路径的排除闸 —— 删掉 = 排除对存量索引静默失效（§2.43；删掉它 selftest W11-15/18 必红）"),
    ("src/Eztools.Index/QueryEngine.cs", "!include.Contains(ce.Frn",
     "子集路径的限定闸 —— 删掉 = 缓存里的范围外条目漏出（§2.43）"),
    ("src/Eztools.Index/QueryEngine.cs", "!include.Contains(frns",
     "全扫路径的限定闸 —— 删掉 = 限定静默失效（§2.43；删掉它 selftest W11-18/21 必红）"),
    ("src/Eztools.Index/QueryEngine.cs", "Excluded?.Extend",
     "排除作用域的 USN 增量补标 —— 删掉 = 自举时排除、增量时不排除 = 漏排除（W11 §2.1）"),
    ("src/Eztools.Index/QueryEngine.cs", "Include?.Extend",
     "限定作用域的 USN 增量补标 —— 删掉 = 限定后新建文件静默丢失（W11-b）"),
    ("src/Eztools.Desktop/TrayApplication.cs", "StartAsync",
     "开窗即发 search.start —— 删掉 = pathFilter 无生产调用方（R14：两侧各半截的原始形态）"),
    ("src/Eztools.Host/Search/SearchIndexProcess.cs", "--path-filter",
     "--path-filter 进程传参链（W11 §5.2 #8；删掉 = 初始限定断链）"),
    ("src/Eztools.Index/Program.cs", "--exclude",
     "--exclude 参数解析（W11-a §5.2 #8；删掉 = 排除配置到不了索引进程）"),
    ("src/Eztools.Index/Program.cs", "--path-filter",
     "--path-filter 参数解析（W11-b；删掉 = 初始限定到不了索引进程）"),
]


def check_g8(root: str, hits: list):
    for relpath, needle, msg in G8_WIRING:
        path = os.path.join(root, relpath)
        if not os.path.isfile(path):
            hits.append({
                "check": "G8", "level": "FAIL",
                "file": relpath, "line": 0,
                "msg": f"生产文件缺失（守卫无法验证接线）：{relpath} —— 若是改名/移动，"
                       f"请同步更新 G8_WIRING 表；若是误删，恢复文件",
            })
            continue

        # keep_strings=True：needle 可能本来就在字符串字面量里（--exclude 等）；
        # 注释仍剥掉 —— "注释里提到接线"不等于"接线存在"（与 G7 同一条判据纪律）。
        src = strip_csharp(
            open(path, encoding="utf-8-sig", errors="replace").read(), keep_strings=True)
        if needle not in src:
            hits.append({
                "check": "G8", "level": "FAIL",
                "file": relpath, "line": 0,
                "msg": f"{msg}（未找到符号 `{needle}` —— 已被删除/改名/移走？）",
            })


# ============================================================================
# 运行器
# ============================================================================
CHECKS = [
    ("G1", "静默吞异常（空 catch）", check_g1),
    ("G2", "switch 缺 default", check_g2),
    ("G3", "验收脚本禁用模式", check_g3),
    ("G4", "幽灵代码候选", check_g4),
    ("G5", "死产物目录（TFM 不一致）", check_g5),
    ("G6", "跳过必须计数（断言脚本）", check_g6),
    ("G7", "分层守卫（Launcher 层）", check_g7),
    ("G8", "生产接线守卫（W11）", check_g8),
]


def run_checks(root: str):
    hits = []
    for _cid, _name, fn in CHECKS:
        try:
            fn(root, hits)
        except Exception as exc:              # 守卫自身出错必须是 FAIL，不能静默
            hits.append({"check": _cid, "level": "FAIL", "file": "<guard>", "line": 0,
                         "msg": f"守卫自身异常：{type(exc).__name__}: {exc}"})
    return hits


def report(root: str, hits: list, strict: bool, json_path=None):
    fails = [h for h in hits if h["level"] == "FAIL"]
    warns = [h for h in hits if h["level"] == "WARN"]
    exempts = [h for h in hits if h["level"] == "EXEMPT"]

    print("=" * 68)
    print("  代码审查红线静态守卫（docs/代码审查规范与流程.md §3.9）")
    print(f"  仓库: {root}")
    print("=" * 68)

    for cid, name, _fn in CHECKS:
        ch = [h for h in hits if h["check"] == cid]
        if any(h["level"] == "FAIL" for h in ch):
            mark = "🔴"
        elif ch:
            mark = "🟡"
        else:
            mark = "✅"
        n_f = len([h for h in ch if h["level"] == "FAIL"])
        n_w = len([h for h in ch if h["level"] == "WARN"])
        n_e = len([h for h in ch if h["level"] == "EXEMPT"])
        print(f"\n{mark} {cid} {name} —— 命中 {len(ch)}"
              f"{f'（FAIL {n_f} / WARN {n_w} / 豁免 {n_e}）' if ch else ''}")
        # 🔴 打印前先按严重度排序：**否则 FAIL 会被一堆 EXEMPT 挤出屏幕**
        #    （实测：48 处豁免 + 5 处 FAIL 时，只看得到豁免，失败的 5 条只存在于 JSON 里
        #      —— 这是"错误不可见"，与整个守卫的目的相反）
        rank = {"FAIL": 0, "WARN": 1, "EXEMPT": 2}
        for h in sorted(ch, key=lambda x: (rank.get(x["level"], 9), x["file"], x["line"]))[:20]:
            loc = f"{h['file']}:{h['line']}" if h["line"] else h["file"]
            print(f"     [{h['level']}] {loc}\n            {h['msg']}")
        if len(ch) > 20:
            print(f"     … 其余 {len(ch) - 20} 条见 --json 输出")

    print("\n" + "-" * 68)
    print("  行宽（80/120 字符）：**不检查** —— 本仓已判定「不采纳」"
          "（长行是有意取舍，docs/README.md §3.6）")
    print("-" * 68)

    rc = 1 if (fails or (strict and warns)) else 0
    print(f"\nreview-guards: FAIL={len(fails)} WARN={len(warns)} EXEMPT={len(exempts)}"
          f"{' (--strict)' if strict else ''}")
    print(f"review-guards-rc: {rc}")

    if json_path:
        with open(json_path, "w", encoding="utf-8") as fh:
            json.dump({"root": root, "fail": len(fails), "warn": len(warns),
                       "exempt": len(exempts), "strict": strict, "rc": rc,
                       "hits": hits}, fh, ensure_ascii=False, indent=1)
    return rc


# ============================================================================
# --selftest：双向突变验证（新检查 ⇔ 必须能被证明会红）
# ============================================================================
def _write(root, relpath, content):
    p = os.path.join(root, relpath)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8") as fh:
        fh.write(content)
    return p


def selftest():
    tmp = tempfile.mkdtemp(prefix="review-guards-selftest-")
    results = []

    def expect(desc, got, want):
        ok = (got == want)
        results.append((desc, ok, got, want))
        print(f"  [{'PASS' if ok else 'FAIL'}] {desc}"
              + ("" if ok else f"（期望 {want}，实际 {got}）"))

    try:
        # ── 夹具仓 A：每条检查的**正向**（必须红）───────────────────────────
        a = os.path.join(tmp, "positive")
        _write(a, "src/Demo/Demo.csproj",
               "<Project><PropertyGroup><TargetFramework>net10.0-windows"
               "</TargetFramework></PropertyGroup></Project>\n")
        _write(a, "src/Demo/Bad.cs", """
namespace Demo;
public class Bad
{
    private void NeverCalled() { }

    public void Run(int mode, string tag)
    {
        try { Do(); }
        catch { }

        try { Do(); }
        catch (System.Exception)
        {
            // 故意忽略：这里只有注释，剥注释后是空块 —— 记 WARN
        }

        try { Do(); }
        catch
        {
            // review-guards:allow-empty-catch :: 探针探测用，故意忽略（块内标记）
        }

        try { Do(); }
        catch
        {
            // review-guards:allow-empty-catch
        }

        try { Do(); }
        // review-guards:allow-empty-catch :: 标记写在 catch 上一行也算
        catch { }

        try { Do(); }
        catch
        {
            // review-guards:allow-empty-catch :: 理由里带 Markdown 加粗 **绝不能** 吞掉自举
        }

        switch (tag)
        {
            case "a": Do(); break;
            case "b": Do(); break;
        }

        Do();
    }
    private void Do() { }
}
""")
        _write(a, "scripts/acceptance.sh",
               "#!/usr/bin/env bash\n"
               "if jq -r .a \"$F\" | grep -q ok; then echo yes; fi\n"
               "REPO=\"$(cd \"$(dirname \"$0\")/..\" && pwd -W 2>/dev/null)\" || "
               "REPO=\"$(cd \"$(dirname \"$0\")/..\" && pwd)\"\n")
        os.makedirs(os.path.join(a, "src", "Demo", "bin", "Debug"), exist_ok=True)
        os.makedirs(os.path.join(a, "src", "Demo", "bin", "Debug", "net9.0-windows"),
                    exist_ok=True)
        # G6 正向夹具：出现「跳过」文案但**没有** SKIPPED 计数
        _write(a, "scripts/_step99_skip.sh",
               "#!/usr/bin/env bash\n"
               "printf '  [跳过] 2 条环境依赖断言\\n'\n"
               "check \"跳过必须可见\" 0 0\n")
        # G7 正向夹具：平台中立层里同时出现 UI 依赖与特权层引用（两类别各一条）
        _write(a, "src/Eztools.Host/Launcher/BadLayer.cs",
               "using System.Windows;\n"
               "using Eztools.Core.Primitives;\n"
               "\n"
               "namespace Demo;\n"
               "\n"
               "public sealed class BadLayer\n"
               "{\n"
               "    public string? Tag { get; set; }\n"
               "}\n")
        # G7 正向夹具（Desktop 侧）：那一层**允许** UI，但**不允许**特权层
        _write(a, "src/Eztools.Desktop/Launcher/BadDesktopLayer.cs",
               "using Eztools.Core.Primitives;\n"
               "\n"
               "namespace Demo;\n"
               "\n"
               "internal sealed class BadDesktopLayer\n"
               "{\n"
               "    public string? Tag { get; set; }\n"
               "}\n")
        # G8 正向夹具：QueryEngine 缺 4 个接线符号（两条 include 闸 + 增量 + 锚定）；
        # TrayApplication.cs 整个不建（文件缺失分支）；其余接线文件符号在场（不得误伤）。
        _write(a, "src/Eztools.Index/QueryEngine.cs", """
namespace Eztools.Index;
public class QueryEngine
{
    public void Q(int[] frns, int slot, object ce, object excluded)
    {
        var a = _volumes[ce.Store].Excluded?.Contains(ce.Frn) == true;
        if (excluded is not null && excluded.Contains(frns[slot])) { }
        if (target.Excluded is not null) { target.Excluded?.Extend(records); }
    }
}
""")
        _write(a, "src/Eztools.Index/IndexRpcServer.cs",
               "namespace Eztools.Index;\n"
               "public class IndexRpcServer\n"
               "{\n"
               "    public void S() { service.ApplyPathFilter(pf); }\n"
               "}\n")
        _write(a, "src/Eztools.Index/IndexBootstrap.cs",
               "namespace Eztools.Index;\n"
               "public class IndexBootstrap\n"
               "{\n"
               "    public void B() { IndexPrune.Run(store, scope); service.ApplyPathFilter(opt.PathFilter); }\n"
               "}\n")
        _write(a, "src/Eztools.Host/Search/SearchIndexProcess.cs",
               "namespace Eztools.Host.Search;\n"
               "public class SearchIndexProcess\n"
               "{\n"
               "    private string Args() => \" --path-filter \\\"x\\\"\";\n"
               "}\n")
        _write(a, "src/Eztools.Index/Program.cs",
               "namespace Eztools.Index;\n"
               "public class Program\n"
               "{\n"
               "    static int Main(string[] args)\n"
               "    {\n"
               "        foreach (var a in args)\n"
               "        {\n"
               "            switch (a)\n"
               "            {\n"
               "                case \"--exclude\": break;\n"
               "                case \"--path-filter\": break;\n"
               "                default: break;\n"
               "            }\n"
               "        }\n"
               "        return 0;\n"
               "    }\n"
               "}\n")

        hits_a = run_checks(a)
        by = {}
        for h in hits_a:
            by.setdefault(h["check"], []).append(h)

        expect("G1 正向：裸空 catch 必须报 FAIL（零信息）",
               len([h for h in by.get("G1", []) if h["level"] == "FAIL"]), 2)
        expect("G1 正向：注释-only 块报 WARN（注释不是声明）",
               len([h for h in by.get("G1", []) if h["level"] == "WARN"]), 1)
        expect("G1 正向：带理由的豁免标记 → EXEMPT（块内 + 上一行 + 含 `*` 三种写法）",
               len([h for h in by.get("G1", []) if h["level"] == "EXEMPT"]), 3)
        expect("G1 ★ 豁免反悔闸：只写标记不写理由必须报 FAIL",
               len([h for h in by.get("G1", [])
                    if h["level"] == "FAIL" and "缺理由" in h["msg"]]), 1)
        expect("G2 正向：字符串 case 且无 default 必须被报出", len(by.get("G2", [])), 1)
        expect("G3 正向：验收脚本 `| grep -q` 必须被报出",
               len([h for h in by.get("G3", []) if "grep" in h["msg"]]), 1)
        expect("G3 正向：`A && B || C && D` 必须被报出",
               len([h for h in by.get("G3", []) if "&&" in h["msg"]]), 1)
        expect("G4 正向：private 无调用点必须被报出",
               len([h for h in by.get("G4", []) if "NeverCalled" in h["msg"]]), 1)
        expect("G5 正向：TFM 不一致目录必须被报出", len(by.get("G5", [])), 1)
        expect("G6 正向：「跳过」文案无 SKIPPED 计数必须被报出", len(by.get("G6", [])), 1)
        expect("G7 正向：Host 侧 UI/特权层各报一条 + Desktop 侧特权层报一条（UI 在 Desktop 合法）",
               len(by.get("G7", [])), 3)
        expect("G8 正向：缺 4 个接线符号（两 include 闸 + 增量 + 锚定）+ 1 个文件缺失 ⇒ 5 条 FAIL",
               len(by.get("G8", [])), 5)
        expect("G8 正向：文件缺失必须报 FAIL（守卫不因文件被移走而静默放过）",
               len([h for h in by.get("G8", []) if "生产文件缺失" in h["msg"]]), 1)

        # ── 夹具仓 B：每条检查的**反向**（不得误伤）─────────────────────────
        b = os.path.join(tmp, "negative")
        _write(b, "src/Demo/Demo.csproj",
               "<Project><PropertyGroup><TargetFramework>net10.0-windows"
               "</TargetFramework></PropertyGroup></Project>\n")
        _write(b, "src/Demo/Good.cs", """
namespace Demo;
public class Good
{
    private void Log(string s) { System.Console.WriteLine(s); }
    private void Logged() { Log("ok"); }
    private void Thrown() { throw new System.InvalidOperationException(); }
    private void OnClick(object s, System.EventArgs e) { Covered(); }
    private static void Covered() { Log("covered"); }

    public void Run(int mode)
    {
        // 带日志的 catch —— 合法
        try { Do(); }
        catch (System.Exception ex) { Log(ex.Message); }

        // rethrow —— 合法
        try { Do(); }
        catch { throw; }

        // 带错误返回的 catch —— 合法
        try { Do(); }
        catch { return; }

        // switch 有 default —— 合法
        switch (mode)
        {
            case 1: Do(); break;
            default: Do(); break;
        }

        // 字符串 switch 但有 default —— 合法
        switch (mode.ToString())
        {
            case "a": Do(); break;
            default: Do(); break;
        }

        // ★ 判据收窄的反向断言：枚举式 switch 缺 default **不得**报出
        //   （实测后收窄：封闭枚举列全 case 时不写 default 是低风险写法）
        switch (mode)
        {
            case 1: Do(); break;
            case 2: Do(); break;
        }

        // switch 表达式（不是语句式）—— 不得误伤
        var label = mode switch { 1 => "a", _ => "b" };
        Log(label);

        Do(); Covered(); OnClick(null!, null!);
        Thrown(); Logged();
    }
    private void Do() { }
}
""")
        # 300 字符长行 —— 行宽已判「不采纳」，**不得**报出任何命中
        _write(b, "src/Demo/LongLine.cs",
               "namespace Demo;\npublic class LongLine\n{\n"
               "    private static readonly string Wide = \""
               + "x" * 300 + "\";\n}\n")
        # ★ 词法硬骨头：C# 11 原始字符串 + 插值串里的调用 —— 两条都是实测误报换来的夹具
        _write(b, "src/Demo/LexHard.cs", '''
namespace Demo;
public class LexHard
{
    private const string Tpl = """
        <Control Name="X" Attr="Y" />
        """;

    private static void UsedAfterRawString() { }
    private static string Span() => "x";

    public static void Ping()
    {
        UsedAfterRawString();
        System.Console.WriteLine($"v={Span()} tpl={Tpl}");
    }
}
''')
        # 合法用法：非验收脚本用 `| grep -q`；注释里写反面教材
        _write(b, "scripts/make-portable.sh",
               "#!/usr/bin/env bash\n"
               "if printf '%s\\n' \"$ZIP_LIST\" | grep -qxF \"$PKG/bin/x.dll\"; then :; fi\n")
        # G6 反向夹具：三种"看着像跳过、其实不是"的写法都不得报出
        #   ① 已计数（SKIPPED 在附近）② 断言名里含"跳过" ③ 注释里的"跳过"
        _write(b, "scripts/_step99_skip.sh",
               "#!/usr/bin/env bash\n"
               "SKIPPED=$((SKIPPED + 2))\n"
               "printf '  [跳过] 2 条环境依赖断言（满额随之少 2）\\n'\n"
               "# 注释里的跳过不算跳过通知\n"
               "check \"跳过必须带具体原因\" 0 0\n")
        _write(b, "scripts/acceptance.sh",
               "#!/usr/bin/env bash\n"
               "# 反面教材：不能用 `ls | grep -q`（命中即退会让上游收 SIGPIPE）\n"
               "REPO=$(cd x && pwd -W) || REPO=$(pwd)\n"
               "grep -qrE 'x' \"$D\" >/dev/null && echo raw-usage-is-fine\n")
        # G7 反向夹具：注释与字符串里提到 UI/特权层**不得**报出（判据是"引用了"，不是"提到"）
        _write(b, "src/Eztools.Host/Launcher/GoodLayer.cs",
               "namespace Eztools.Host.Launcher;\n"
               "\n"
               "/// <summary>\n"
               "/// 反向夹具：注释里出现 System.Windows.Forms 与 Eztools.Core 是**合法**的\n"
               "/// —— 本仓正是靠文档注释解释「为什么不碰它们」。\n"
               "/// </summary>\n"
               "public sealed class GoodLayer\n"
               "{\n"
               "    private const string Note = \"System.Windows.Forms 与 Eztools.Core 只是说明文字\";\n"
               "}\n")
        # G7 反向夹具（Desktop 侧）：**真用** WPF/Win32 也不得报出 —— 分层判据是
        # "平台中立层不得沾 UI"，不是"Launcher 目录不得沾 UI"（W7-b 修正的第一版误伤）
        _write(b, "src/Eztools.Desktop/Launcher/GoodDesktopLayer.cs",
               "using System.Windows;\n"
               "using System.Windows.Media.Imaging;\n"
               "\n"
               "namespace Eztools.Desktop;\n"
               "\n"
               "internal static class GoodDesktopLayer\n"
               "{\n"
               "    internal static BitmapSource? Probe() => null;\n"
               "}\n")
        # G8 反向夹具：全部接线文件**符号在场** ⇒ 不得报出（含字符串字面量里的 needle）
        _write(b, "src/Eztools.Index/QueryEngine.cs", """
namespace Eztools.Index;
public class QueryEngine
{
    public void Q(int[] frns, int slot, object ce, object excluded, object include)
    {
        var a = _volumes[ce.Store].Excluded?.Contains(ce.Frn) == true;
        if (excluded is not null && excluded.Contains(frns[slot])) { }
        if (include is not null && !include.Contains(ce.Frn)) { }
        if (include is not null && !include.Contains(frns[slot])) { }
        MarkByPath(store, volume, pathFilter);
        if (target.Excluded is not null) { target.Excluded?.Extend(records); }
        if (target.Include is not null) { target.Include?.Extend(records); }
    }
}
""")
        _write(b, "src/Eztools.Index/IndexRpcServer.cs",
               "namespace Eztools.Index;\n"
               "public class IndexRpcServer\n"
               "{\n"
               "    public void S() { service.ApplyPathFilter(pf); }\n"
               "}\n")
        _write(b, "src/Eztools.Index/IndexBootstrap.cs",
               "namespace Eztools.Index;\n"
               "public class IndexBootstrap\n"
               "{\n"
               "    public void B() { IndexPrune.Run(store, scope); service.ApplyPathFilter(opt.PathFilter); }\n"
               "}\n")
        _write(b, "src/Eztools.Host/Search/SearchIndexProcess.cs",
               "namespace Eztools.Host.Search;\n"
               "public class SearchIndexProcess\n"
               "{\n"
               "    private string Args() => \" --path-filter \\\"x\\\"\";\n"
               "}\n")
        _write(b, "src/Eztools.Index/Program.cs",
               "namespace Eztools.Index;\n"
               "public class Program\n"
               "{\n"
               "    static int Main(string[] args)\n"
               "    {\n"
               "        foreach (var a in args)\n"
               "        {\n"
               "            switch (a)\n"
               "            {\n"
               "                case \"--exclude\": break;\n"
               "                case \"--path-filter\": break;\n"
               "                default: break;\n"
               "            }\n"
               "        }\n"
               "        return 0;\n"
               "    }\n"
               "}\n")
        _write(b, "src/Eztools.Desktop/TrayApplication.cs",
               "namespace Eztools.Desktop;\n"
               "public class TrayApplication\n"
               "{\n"
               "    public void T() { client.StartAsync(pf); }\n"
               "}\n")

        hits_b = run_checks(b)
        byb = {}
        for h in hits_b:
            byb.setdefault(h["check"], []).append(h)

        expect("G1 反向：有日志 / rethrow / return 的 catch 不得报出",
               len(byb.get("G1", [])), 0)
        expect("G2 反向：有 default 的 switch / switch 表达式 / 枚举式缺 default 均不得报出",
               len(byb.get("G2", [])), 0)
        expect("G3 反向：注释里的反面教材 + 非验收脚本的合法用法不得报出",
               len(byb.get("G3", [])), 0)
        expect("G4 反向：有调用点的 private 方法 / 事件处理器不得报出",
               len([h for h in byb.get("G4", []) if "Logged" in h["msg"]
                    or "Covered" in h["msg"] or "OnClick" in h["msg"]]), 0)
        expect("G5 反向：TFM 一致的 bin 目录不得报出", len(byb.get("G5", [])), 0)
        expect("G6 反向：已计数 / 断言名含跳过 / 注释里的跳过 均不得报出",
               len(byb.get("G6", [])), 0)
        expect("G7 反向：注释与字符串里提到 UI/特权层不得报出（防误伤理由书）；Desktop 侧真用 WPF 也不报",
               len(byb.get("G7", [])), 0)
        expect("G8 反向：全部接线符号在场（含字符串字面量里的 needle）⇒ 不得报出",
               len(byb.get("G8", [])), 0)
        expect("★ 行宽反向断言：300 字符长行不得产生任何命中",
               len([h for h in hits_b if "LongLine" in h["file"]]), 0)
        expect("★ 词法反向断言：原始字符串 `\"\"\"` 之后的调用点不得被吞（防错位误报）",
               len([h for h in byb.get("G4", []) if "UsedAfterRawString" in h["msg"]]), 0)
        expect("★ 词法反向断言：只在插值串里被调用的方法不得被误报为幽灵",
               len([h for h in byb.get("G4", []) if "Span" in h["msg"]]), 0)

        # ── 退出码语义 ──────────────────────────────────────────────────────
        expect("退出码：夹具仓 A 有 FAIL ⇒ rc 非 0", int(_rc_of(a, hits_a) != 0), 1)
        expect("退出码：夹具仓 B 无 FAIL ⇒ rc 为 0", _rc_of(b, hits_b), 0)

        hard = [r for r in results if not r[1]]
        print(f"\nreview-guards-selftest: PASS={len(results) - len(hard)} "
              f"FAIL={len(hard)}  突变验证总计 {len(results)} 条")
        return 1 if hard else 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def _rc_of(root, hits):
    fails = [h for h in hits if h["level"] == "FAIL"]
    return 1 if fails else 0


def main():
    ap = argparse.ArgumentParser(description="代码审查红线静态守卫")
    ap.add_argument("--repo", default=os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))
    ap.add_argument("--strict", action="store_true", help="WARN 也计为失败（专项审用）")
    ap.add_argument("--selftest", action="store_true", help="双向突变验证（不出仓）")
    ap.add_argument("--json", default=None, help="把结果落盘为 JSON")
    args = ap.parse_args()

    if args.selftest:
        return selftest()

    root = os.path.abspath(args.repo)
    if not os.path.isdir(root):
        print(f"--repo 不是目录: {root}", file=sys.stderr)
        return 2
    return report(root, run_checks(root), args.strict, args.json)


if __name__ == "__main__":
    sys.exit(main())
