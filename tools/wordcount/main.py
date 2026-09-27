# Copyright (c) 2026 Eztools contributors
# SPDX-License-Identifier: GPL-3.0-or-later

"""文本统计工具：演示"带配置 schema 的工具"长什么样。

它同时覆盖了四类贡献点（commands / actions / hotkeys / menus）与三项配置，
是 P1「配置 schema 驱动设置 UI」的第一块试金石——设置页不需要为它写任何 UI 代码。
"""

from __future__ import annotations

import re
from pathlib import Path

from eztools import Tool

tool = Tool()

# 中日韩统一表意文字 + 假名 + 谚文：这些文字没有空格分词，按"字"计数
_CJK = re.compile(r"[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uac00-\ud7af]")
_LATIN_WORD = re.compile(r"[A-Za-z0-9_']+")

DEFAULT_MAX_FILE_MB = 64


def _tokenize(text: str) -> tuple[int, int]:
    """返回 (latin 词数, cjk 字数)。language=auto 时两者都数。"""
    language = tool.config.get("language") or "auto"

    latin = 0 if language == "cjk" else len(_LATIN_WORD.findall(text))
    cjk = 0 if language == "latin" else len(_CJK.findall(text))
    return latin, cjk


def _count(text: str) -> dict:
    count_whitespace = bool(tool.config.get("countWhitespace"))
    latin, cjk = _tokenize(text)

    return {
        "chars": len(text) if count_whitespace else len("".join(text.split())),
        "charsTotal": len(text),
        "words": latin + cjk,
        "latinWords": latin,
        "cjkChars": cjk,
        "lines": text.count("\n") + (1 if text and not text.endswith("\n") else 0),
        "bytes": len(text.encode("utf-8")),
    }


def _read_text(path: str) -> str:
    max_mb = int(tool.config.get("maxFileSizeMb") or DEFAULT_MAX_FILE_MB)
    file = Path(path)

    if not file.is_file():
        raise FileNotFoundError(f"文件不存在: {path}")

    size = file.stat().st_size
    if size > max_mb * 1024 * 1024:
        raise ValueError(f"文件过大（{size / 1048576:.1f} MB > {max_mb} MB）")

    # 二进制回退：不因为编码问题让整个工具失败
    try:
        return file.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return file.read_text(encoding="utf-8", errors="replace")


@tool.handler("count")
def count(args):
    text = args.get("text")
    if text is None:
        # 托盘菜单点击时**没有任何上下文**：宿主按 manifest 里声明的 `input: clipboard`
        # 把剪贴板文本注入到 args["input"]（见 docs/P2-托盘-实施方案.md §1.2）。
        # 这样工具不必自己碰系统剪贴板——那属于宿主侧的职责。
        text = args.get("input")
    if text is None:
        raise ValueError("缺少参数 text")
    return _count(str(text))


@tool.handler("countFile")
def count_file(args):
    # 支持三种调用形态：{path} / {paths:[...]} / 文件动作传入的 {files:[...]}
    path = args.get("path")
    if path is None:
        for key in ("paths", "files"):
            values = args.get(key)
            if isinstance(values, list) and values:
                path = values[0]
                break

    if path is None:
        raise ValueError("缺少参数 path（或 paths / files）")

    text = _read_text(str(path))
    result = _count(text)
    result["path"] = str(path)
    return result


if __name__ == "__main__":
    raise SystemExit(tool.run())
