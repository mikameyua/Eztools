"""文件校验工具：带 vendor 依赖策略的示范，也是"新增工具不改宿主一行代码"的验证样本。

它演示三件事：
1. **四类贡献点齐备**（命令 / 动作 / 热键 / 菜单）——全部由 ``tool.json`` 声明，宿主自动接管；
2. **配置驱动行为**（算法、大小写、分块大小）——P1 会据此自动渲染设置页，本文件零 UI 代码；
3. **双向协议**：批量计算时用 ``tool.log()`` 向宿主回传进度，走的是 ``host.log`` 反向调用。
"""

from __future__ import annotations

import hashlib
from pathlib import Path

from eztools import Tool

tool = Tool()

ALGORITHMS = {
    "sha256": hashlib.sha256,
    "sha1": hashlib.sha1,
    "md5": hashlib.md5,
    "blake2b": hashlib.blake2b,
}

DEFAULT_ALGORITHM = "sha256"
DEFAULT_CHUNK_KB = 1024

# 从命令行复制的路径可能带引号，取剪贴板输入时要剥掉
_PATH_QUOTES = "\"'"

# 通过 hashlib 自身探测可用的算法，避免把不存在的名字写进配置枚举
for _name in ("sha512", "sha3_256"):
    if _name in hashlib.algorithms_available:
        ALGORITHMS[_name] = getattr(hashlib, _name)


def _resolve_algorithm(requested) -> str:
    name = (requested or tool.config.get("algorithm") or DEFAULT_ALGORITHM).lower()
    if name not in ALGORITHMS:
        raise ValueError(f"不支持的算法 {name}（可用: {', '.join(sorted(ALGORITHMS))}）")
    return name


def _chunk_size() -> int:
    kb = int(tool.config.get("chunkSizeKb") or DEFAULT_CHUNK_KB)
    return max(4, kb) * 1024


def _hash_file(path: Path, algorithm: str) -> dict:
    if not path.is_file():
        raise FileNotFoundError(f"文件不存在: {path}")

    digest = ALGORITHMS[algorithm]()
    size = 0
    chunk = _chunk_size()

    with path.open("rb") as handle:
        while True:
            block = handle.read(chunk)
            if not block:
                break
            size += len(block)
            digest.update(block)

    text = digest.hexdigest()
    if tool.config.get("uppercase"):
        text = text.upper()

    return {
        "path": str(path),
        "name": path.name,
        "algorithm": algorithm,
        "size": size,
        "hash": text,
    }


def _clean_path(text: str) -> str:
    """剪贴板里的路径常被引号包着（从命令行复制时），去掉一层。"""
    return text.strip().strip(_PATH_QUOTES).strip()


def _collect_paths(args) -> list:
    for key in ("paths", "files"):
        values = args.get(key)
        if isinstance(values, list) and values:
            return [str(v) for v in values]

    single = args.get("path") or args.get("file")
    if single:
        return [str(single)]

    # 托盘菜单点击时**没有任何上下文**：宿主按 manifest 里声明的 `input: clipboard`
    # 把剪贴板文本注入到 args["input"]（见 docs/P2-托盘-实施方案.md §1.2）。
    # 剪贴板里可能是**多行路径**（资源管理器里多选后复制），所以按行拆开当多个路径处理，
    # 而不是只取第一行——只取第一行会静默丢掉其余文件。
    raw = args.get("input")
    if isinstance(raw, str):
        paths = [_clean_path(line) for line in raw.splitlines()]
        paths = [path for path in paths if path]
        if paths:
            return paths

    raise ValueError("缺少参数 path（或 paths / files / input）")


@tool.handler("hash")
def hash_one(args):
    paths = _collect_paths(args)
    return _hash_file(Path(paths[0]), _resolve_algorithm(args.get("algorithm")))


@tool.handler("hashMany")
def hash_many(args):
    paths = _collect_paths(args)
    algorithm = _resolve_algorithm(args.get("algorithm"))

    results = []
    errors = []
    total = len(paths)

    for index, raw in enumerate(paths, start=1):
        # 反向调用宿主写日志：多文件计算可能需要数十秒，用户应该在日志里看到进度
        tool.log(f"({index}/{total}) 计算 {Path(raw).name}", "debug")
        try:
            results.append(_hash_file(Path(raw), algorithm))
        except Exception as exc:  # noqa: BLE001 - 单个文件失败不该中断整批
            errors.append({"path": raw, "error": f"{type(exc).__name__}: {exc}"})

    return {
        "algorithm": algorithm,
        "count": len(results),
        "results": results,
        "errors": errors,
    }


if __name__ == "__main__":
    raise SystemExit(tool.run())
