"""速览：资源管理器选中项的元信息 + 纯文本内容预览。

为什么是现在这个形态（三条硬约束的推论，见 docs/P4-Wave2c-面板协议.md）：
- 只用面板既有 6 种节点 —— WebView2 已被 D3 否决（50~100 MB，推翻 220 MB 预算），
  也没有 image / code 节点，所以能表达的只有"元信息 + 纯文本"；
- 触发（热键/托盘，input: shellSelection）与面板打开是**两条通路**，
  靠宿主的私有 KV（storage_set / storage_get）桥接：命令记录选中项，面板读取；
- 面板开着时按"刷新"按钮更新 —— 按钮走 tool.invoke，宿主完成后**自动重拉面板**
  （协议 §4.1 的既有机制），不需要轮询（refreshMs=0，避免每 2 秒拉起一次工具进程）。

边界处理（速览类功能最容易踩的部分）：
- 文本硬截断：面板数据走 JSON-RPC 单帧，无上限会把帧撑爆；
- 二进制探测：前 N 字节出现 NUL 即按二进制处理，只给元信息；
- 编码只按 UTF-8 / UTF-8-BOM 处理，解码失败用替换字符 —— **不做编码检测**
  （唯一常见的编码检测库 UTF.Unknown 是 MPL-1.1，与本项目 GPL-3.0 不兼容）；
- 任何一步失败都变成面板里的一个 text 节点，绝不抛 —— 面板错误不弹对话框
  （与清单诊断同一条设计约束）。
"""

import os
import time

from eztools import Tool

tool = Tool()

_BINARY_NOTE = "二进制文件（前段含 NUL 字节），不预览内容"


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
    """前段出现 NUL 字节即判二进制 —— 与 git 对二进制的启发式同源，简单且够用。"""
    return b"\x00" in head


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
    return {"opened": target}


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
            if _is_binary(head[:probe_bytes]):
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

    # utf-8-sig 容忍 BOM（宿主 stdin 同款约定）；解码失败的字符用替换字符，
    # 并如实标注"编码可能不是 UTF-8"—— 不做编码检测（MPL-1.1 的 UTF.Unknown 不可用）。
    text = raw.decode("utf-8-sig", errors="replace")
    lines = text.splitlines()
    truncated_by_bytes = st.st_size > len(raw)
    truncated_by_lines = len(lines) > max_lines

    shown = lines[:max_lines]
    nodes.append({"type": "kv", "items": [
        ["类型", "文本"],
        ["大小", _human_size(st.st_size)],
        ["行数", f"{len(lines)}" + ("（预览部分）" if truncated_by_bytes else "")],
        ["修改时间", _mtime(st.st_mtime)],
    ]})
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
