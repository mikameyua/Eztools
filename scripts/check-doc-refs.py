"""文档交叉引用完整性检查 —— `§N.M` 小节号 + 文件路径。

## 为什么需要它

本项目把**细节全部沉进 `docs/`**，MEMORY.md 只留指针，代码注释里也大量写
"协议 §3.4""设计文档 §28" 这类指针。于是**指针本身成了契约**：

- 指向一个**不存在的小节号** ⇒ 读者按图索骥找不到，比不写指针更糟
  （他会以为是自己没找到，而不是"这指针是错的"）。
- 指向一个**已被改名/删除的文件** ⇒ 同理。

而这两件事**零信号**：markdown 不会报错，代码注释更不会。
属于本项目"静默失败登记册"的同一族 —— 出错了但没有任何东西会说。

## 它查什么（两类）

**① 悬空小节号**：把所有 `§N.M` 引用与**全仓库标题里真实出现的小节号**对账。
- 判据用"全库并集"而非"同文档内"：跨文档引用是常态
  （脚本注释写 `§28`，而 §28 在设计文档里）。
- 父级视为存在：有 `§12.5.6` ⇒ `§12` 与 `§12.5` 也算存在。

**② 悬空文件路径**：`` `docs/x.md` `` `` `scripts/y.sh` `` 这类反引号路径必须真实存在。

## 已知的合法例外（不是 bug，别改）

- `tools/*/tool.json` —— **通配符**，不是具体路径。
- `src/modules/cmdpal/...` —— **PowerToys 上游**（参照克隆）里的路径，
  出现在 `PowerToys-模块化与插件商店-实现方案.md` 等"阅读笔记"类文档中，
  它们描述的本来就是**别人的**代码树。
- `tools/preview/Lib/pypdf/**` —— 第三方库自己的 PDF 规范引用（`§7.9.6` 等），
  与我们的小节号体系无关，**整体排除**。

## ★ 核心语义：什么算"真引用"

**反面**（被讨论的对象 ⇒ 不解析）：反引号内、双反引号内、加粗 `**...**` 内、围栏代码块内。
> 例：「原引用是 `§11.6`，其实应为 §3.5」—— 这里的 `§11.6` 是**被讨论的对象**，
> 不是"请去看第 11.6 节"。把它当悬空引用报出来就是**自己把自己搞红**。

**正面**（正文裸引用 ⇒ 必须解析得到）：反引号外的 `§N.M`、单反引号里的**文件路径**。
> 注意不对称：**路径**要靠单反引号来识别（裸写 `scripts/x.sh` 在散文里太容易误伤），
> 而**小节号**恰恰相反 —— 裸写才是真引用。两者预处理方式因此**不同**，
> 不能无脑套同一个 mask（第一版就是套了，导致路径检查恒空转）。

## 用法

    python scripts/check-doc-refs.py            # 只报告
    python scripts/check-doc-refs.py --strict   # 有悬空以退出码 1 结束

退出码：0 = 干净；1 = 有悬空引用（--strict 才有意义，否则仅作提示）。
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

# 独立运行时防 GBK locale：重定向到文件时默认编码是系统 locale（GBK），
# 而验收脚本 _step16_preview.sh 用 UTF-8 模式 grep 本脚本的中文输出 ⇒ 匹配不到
# （scanned='' 假绿告警，2026-09-24 实测）。显式归 UTF-8，环境无关。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# ── 扫描范围 ────────────────────────────────────────────────────────────────
# 只扫"本项目的"文档与代码。刻意排除：
#   PowerToys/  —— 上游克隆，6500+ 文件，且它有自己的文档体系
#   spike/      —— 实验目录
#   tools/preview/Lib/ —— vendor 的第三方库（含 pypdf 的 PDF 规范引用）
#   _scratch/ _review/ dist/ payload/ —— 临时与产物
SCAN_GLOBS = (
    "docs/*.md",
    "*.md",
    "scripts/*.sh",
    "scripts/*.py",
    "sdk/**/*.py",
    "sdk/**/*.md",
    "tools/*.py",
    "tools/*/tool.json",
    "src/**/*.cs",
    ".workbuddy/memory/*.md",
)

# ── ★ 历史档案（**只提示、不判失败**）────────────────────────────────────────
# 每日日志是**追加型历史记录**（项目约定：append-only，不追改）。它们写下的
# `§13.6` / `§14.8` 是**当时**的设计文档结构 —— 后来文档重排过（§13 现在是
# "反模式清单"、§14 是"P0 落地记录"），于是那些引用**今天**解析不到。
#
# 🔴 这**不是**缺陷：把历史日志改成指向新编号，等于**伪造当时的认知**
#    （那时候根本没有今天的 §13）。所以它们只做提示，不计入退出码。
#
# 判据：只有当引用出现在**会被人当作现行契约去查**的文件里（docs/、代码注释、
#       MEMORY.md 索引）才算悬空。日志里的按"历史注记"提示。
HISTORICAL_GLOBS = (".workbuddy/memory/",)

# 路径引用检查：只认这些前缀，避免把普通文字里的斜杠当成路径
PATH_PREFIXES = ("docs/", "scripts/", "sdk/", "src/", "tools/", "payload/")

# 合法例外：匹配到这些就不算悬空
EXEMPT_PATH_PATTERNS = (
    re.compile(r"\*"),                        # 通配符
    re.compile(r"^src/modules/cmdpal/"),      # PowerToys 上游路径
    re.compile(r"^src/modules/launcher/"),
    re.compile(r"—\s*已(?:更名|删除|不存在)"),  # 见下方 RETIRED_PATH_MARK
)

# ── ★ 已退役路径（**已更名或已删除**，引用它们是"记录历史"，不是悬空）────────────
# 问题：文档需要写「原名 `docs/极速文件搜索-实现方案.md`，2026-09-23 更名」——
#       那个路径**今天必然不存在**，但这句话**必须留着**（否则读者拿旧引用来查会一头雾水）。
#       判据只能落在"这句话在说它已经不在了"，而脚本无法理解语义。
#
# 解法：**显式标记**。路径后面紧跟一个破折号 + 「已更名 / 已删除 / 已不存在」，
#       即视为"这是在记录退役路径"，不计入悬空。
#
# ⚠️ 这个标记**必须由人写**（不能靠脚本猜）。它是"我知道这个路径不存在，且我是故意的"的
#    签名 —— 与 `（新建）` 前缀（"我知道它还不存在，且我是故意的"）正好互补：
#        不存在 + 将存在  → `（新建）docs/x.md`
#        不存在 + 曾存在  → `docs/x.md —— 已更名/已删除`
#    两者都**显式**，脚本就能把"笔误"与"记录"区分开。这正是 M-系列要的"零信号变有信号"。
RETIRED_PATH_MARK = re.compile(r"—\s*已(?:更名|删除|不存在)")

# 本脚本自己的 docstring 里必然出现"示例引用"（`docs/x.md`、`§7.9.6` 说明 pypdf 例外），
# 那是**文档内容**不是真引用 ⇒ 整体跳过自身，否则它一辈子红着（自指悖论）。
SELF_PATH = "scripts/check-doc-refs.py"

# ── ★ 待建小节（`（新建）§3.7`）────────────────────────────────────────────
# 与"待建文件路径"完全同构，但**对象是小节号**：
#   文档常写「在 `面板协议` 新增 `（新建）§3.7` `input` 节点规范」——
#   那个小节**现在必然不存在**，但这句话**必须留着**（它规定了将来往哪加）。
#   与 `§N.M` 的通例相反：这里**必须**用反引号包住，因为"待建"是一个**声明**，
#   不是"请去看这一节"的指针。裸写的 §N.M 仍按真指针校验（保持既有语义）。
#
# ⚠️ 这是**第三套**显式标记，三套语义互补：
#     不存在 + 将存在（文件） → `（新建）docs/x.md`
#     不存在 + 将存在（小节） → `（新建）§3.7`
#     不存在 + 曾存在（文件） → `docs/x.md —— 已更名/已删除`
#   全部要**人写**。脚本无法理解语义，只能认标记 —— 这正是"零信号变有信号"。
PENDING_SECTION_REF = re.compile(r"`\s*（新建）\s*§[0-9]+(?:\.[0-9]+)*\s*`")

# 小节号引用形态：§12 / §12.5 / §12.5.6
SECTION_REF = re.compile(r"§([0-9]+(?:\.[0-9]+)*)")

# 标题里的小节号。⚠️ 编号后的分隔符**不只有空格**：
#   `## 12.5 标题`      —— 数字 + 空格
#   `## 11. 已知限制`   —— 数字 + **句点** + 空格（本项目大量使用这一形态！）
#   `## §12.5 标题`     —— 带 § 前缀
# 第一版只写 `\s`，于是 `## 11. 已知限制` 这种**带句点的**标题**全部漏采**，
# 导致 §11 / §13 / §0 被误报成"悬空"（64 处假阳性）。
# 判据：数字后面跟 `.`、`、` 或直接空格，**只要不是数字或点号**就算标题边界。
SECTION_HEAD = re.compile(r"^#{1,6}\s+(?:§)?([0-9]+(?:\.[0-9]+)*)\s*[.、]?\s+\S", re.M)


def iter_targets(repo: pathlib.Path) -> list[pathlib.Path]:
    out: list[pathlib.Path] = []
    for pat in SCAN_GLOBS:
        for p in repo.glob(pat):
            if not p.is_file():
                continue
            s = str(p).replace("\\", "/")
            if "PowerToys/" in s or "spike/" in s or "Lib/" in s:
                continue
            out.append(p)
    # 去重并保持稳定顺序
    return sorted(set(out))


def collect_known_sections(repo: pathlib.Path) -> set[str]:
    """全库真实存在的小节号（含所有父级）。"""
    known: set[str] = set()
    for d in sorted((repo / "docs").glob("*.md")):
        txt = d.read_text(encoding="utf-8", errors="replace")
        for m in SECTION_HEAD.finditer(txt):
            parts = m.group(1).split(".")
            for i in range(1, len(parts) + 1):
                known.add(".".join(parts[:i]))
    return known


def is_historical(rel: str) -> bool:
    return any(rel.startswith(g) for g in HISTORICAL_GLOBS)


def _mask(text: str) -> str:
    """把一段文本替换成等长空白（保留换行，行号不漂）。"""
    return re.sub(r"[^\n]", " ", text)


def mask_code_spans(txt: str) -> str:
    """把"示例性引用"替换成等长占位，避免它们被当成真引用。

    为什么必须做：本文档与日志里大量出现**作为举例**的引用 ——
    「原引用是 `§11.6`，其实应为 §3.5」。若不做遮蔽，校验器会把
    举例用的 `§11.6` 当成真的悬空引用报出来（**自己把自己搞红**）。

    判据：**反引号/加粗内 = 代码或举例，不参与引用解析**；
          正文里的裸引用 = 真指针，必须解析得到。

    遮蔽的**四种**形态（每一种都是实际踩到的）：
      1. 围栏代码块 ``` ... ```（整块）；
      2. **双反引号** `` `` `x` `` `` —— 内部含单反引号，必须在单反引号规则**之前**跑；
      3. **单反引号** `` `x` `` —— 最常见的"引用示例"写法；
      4. **加粗强调** `**§11.6**` / `**没有 §11.6**` —— 举例时为了醒目会加粗，
         于是环没有反引号护着，会被当正文解析。

    第 4 条尤其反直觉：**"强调"与"引用"在源文本上长得差不多**，
    但语义完全不同 —— 被加粗的小节号几乎总是"我在举反例"，不是"请去看这一节"。

    ⚠️ **本函数只给 check_sections 用。** 路径检查（check_paths）需要**保留**反引号
    —— 它本来就是"只认反引号里的路径"。第一版把遮蔽也套到路径检查上，
    于是反引号被抹掉 ⇒ 正则永远匹配不到 ⇒ **路径检查彻底失效且零信号**
    （突变 M-G3 才抓出来）。教训：**同一个预处理不能无脑套到语义相反的检查上。**
    """
    out = txt
    # 1) 围栏代码块
    out = re.sub(r"```.*?```", lambda m: _mask(m.group(0)), out, flags=re.S)
    out = re.sub(r"~~~.*?~~~", lambda m: _mask(m.group(0)), out, flags=re.S)
    # 2) 双反引号（必须先于单反引号）
    out = re.sub(r"``.+?``", lambda m: _mask(m.group(0)), out, flags=re.S)
    # 3) 单反引号
    out = re.sub(r"`[^`\n]+`", lambda m: _mask(m.group(0)), out)
    # 4) 加粗段（**...**）—— 举例常写 `**§11.6**`
    out = re.sub(r"\*\*[^\n]*?\*\*", lambda m: _mask(m.group(0)), out)
    return out


def check_sections(
    repo: pathlib.Path, targets: list[pathlib.Path], known: set[str]
) -> tuple[list[tuple[str, int, str]], list[tuple[str, int, str]]]:
    """返回 (会判失败的悬空, 仅提示的历史注记)。"""
    bad: list[tuple[str, int, str]] = []
    hist: list[tuple[str, int, str]] = []
    for t in targets:
        rel = str(t.relative_to(repo)).replace("\\", "/")
        if rel == SELF_PATH:
            continue
        txt = mask_code_spans(t.read_text(encoding="utf-8", errors="replace"))
        # ★ 先把「（新建）§N.M」整段抹掉 —— 它是"待建小节"的显式声明，不是指针。
        #   必须先于 SECTION_REF 跑，且它在反引号内（mask_code_spans 已经抹过一次）——
        #   但 mask_code_spans 抹的是**全部**反引号内容，所以这里实际是"二次保险"：
        #   若将来 mask 规则调整（如改为不抹反引号），这条仍能兜住。
        txt = PENDING_SECTION_REF.sub(lambda m: _mask(m.group(0)), txt)
        for m in SECTION_REF.finditer(txt):
            n = m.group(1)
            if n not in known:
                line = txt[: m.start()].count("\n") + 1
                (hist if is_historical(rel) else bad).append((rel, line, f"§{n}"))
    return bad, hist


def check_paths(
    repo: pathlib.Path, targets: list[pathlib.Path]
) -> tuple[list[tuple[str, int, str]], list[tuple[str, int, str]]]:
    """返回 (会判失败的悬空, 仅提示的历史注记)。

    ⚠️ 与 check_sections 一样分两组：**历史日志里引用的"示例路径"不算缺陷**。
    日志常写「注入 `` `scripts/nope-xyz.sh` `` 期待变红」—— 那个路径**本来就不该存在**
    （它是故意的反例）。把它判失败等于**禁止日志记录自己做过什么实验**。
    """
    bad: list[tuple[str, int, str]] = []
    hist: list[tuple[str, int, str]] = []
    # 反引号里的路径；结尾吃掉常见标点
    cand = re.compile(r"`((?:" + "|".join(map(re.escape, PATH_PREFIXES)) + r")[^`\s]+?)`")
    for t in targets:
        rel = str(t.relative_to(repo)).replace("\\", "/")
        if rel == SELF_PATH:
            continue
        # 🔴 这里用**原始文本**，不能用 mask_code_spans 的结果 ——
        #    本检查的判据就是"反引号里的路径"，遮蔽会把反引号抹掉 ⇒ 恒不匹配 ⇒ 静默失效。
        #    （第一版就是照抄了 check_sections 的遮蔽，导致此检查一周目全空转。）
        #
        # 但**双反引号**（`` `` `x` `` ``）是例外：那是"用来展示含反引号的代码"的写法，
        # 本身就是"我在举例"的信号（单反引号里的路径是正常引用）。
        # ⇒ 只抹掉双反引号段，保留单反引号。
        txt = t.read_text(encoding="utf-8", errors="replace")
        txt = re.sub(r"``.+?``", lambda m: _mask(m.group(0)), txt, flags=re.S)
        # 🔴 heredoc 正文（`cat > x <<'EOF' ... EOF`）必须遮蔽 —— 里面的路径是**测试夹具**，
        #    本来就该不存在（如 acceptance step 18 自造的突变探针 `docs/mut-b.md`）。
        #    不遮蔽 ⇒ 脚本被自己的夹具搞红（与"自指悖论"同族）。
        #    判据：`<<` 后跟可选的引号 + 大写标识符，到行首同名标识符为止。
        txt = re.sub(
            r"<<-?'?([A-Z][A-Z0-9_]*)'?\n.*?\n\1\b",
            lambda m: _mask(m.group(0)),
            txt,
            flags=re.S,
        )
        for m in cand.finditer(txt):
            raw = m.group(1).rstrip(".,;:)")
            if not re.search(r"\.(sh|py|cs|md|json|ps1|xaml)$", raw):
                continue
            if any(p.search(raw) for p in EXEMPT_PATH_PATTERNS):
                continue
            # 「已更名 / 已删除」标记须**紧跟在路径之后**才算数（允许反引号后有个空格）。
            tail = txt[m.end(): m.end() + 24]
            if RETIRED_PATH_MARK.search(tail):
                continue
            if not (repo / raw).exists():
                line = txt[: m.start()].count("\n") + 1
                (hist if is_historical(rel) else bad).append((rel, line, raw))
    return bad, hist


def main() -> int:
    ap = argparse.ArgumentParser(description="文档交叉引用完整性检查")
    ap.add_argument("--repo", default=".", help="仓库根（默认当前目录）")
    ap.add_argument("--strict", action="store_true", help="有悬空引用时以退出码 1 结束")
    args = ap.parse_args()

    repo = pathlib.Path(args.repo).resolve()
    targets = iter_targets(repo)
    known = collect_known_sections(repo)

    sec_bad, sec_hist = check_sections(repo, targets, known)
    path_bad, path_hist = check_paths(repo, targets)
    hist_all = sec_hist + path_hist

    print(f"扫描文件 {len(targets)} 个 · 已知小节号 {len(known)} 个")
    print()

    def dump(title: str, items: list[tuple[str, int, str]]) -> None:
        print(f"── {title}: {len(items)}")
        grouped: dict[str, list[tuple[int, str]]] = {}
        for f, ln, what in items:
            grouped.setdefault(f, []).append((ln, what))
        for f, lst in sorted(grouped.items()):
            for ln, what in sorted(lst):
                print(f"     {f}:{ln}  {what}")
        print()

    # 按文档分组打印，便于定位
    dump("★ 悬空引用（会判失败 —— 这些是现行契约）", sec_bad)
    dump("悬空文件路径（会判失败）", path_bad)
    dump("历史注记（不判失败 —— 日志按当时的文档结构与实验记录写的，属正常）", hist_all)

    if sec_bad or path_bad:
        print(f"结果: 有问题 —— 小节号 {len(sec_bad)} 处 · 路径 {len(path_bad)} 处")
        return 1 if args.strict else 0

    print("结果: 干净 —— 现行契约里的 §N.M 引用与文件路径均可解析")
    if hist_all:
        print(f"      （另有 {len(hist_all)} 处历史注记，见上）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
