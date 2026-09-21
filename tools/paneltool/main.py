"""面板演示工具（P4 Wave 2c 验收钩子）。

它存在的理由是**让面板协议变成可断言的**：全部 6 种节点类型各来一个，
外加三条容错路径（空面板 / 含未知 type / 返回非对象）——
这样 `ezt panel paneltool <id>` 的返回值就能同时覆盖正向与负向用例，
而验收脚本**不需要 GUI**（见 docs/P4-Wave2c-面板协议.md §9）。

刻意**不**在 handler 里做任何"看起来更聪明"的事（如自动补 type、过滤空值）：
这个工具是**探针**，它要如实暴露宿主拿到什么，探针越"聪明"，断言越假。
"""

from __future__ import annotations

import time

from eztools import Tool

tool = Tool()

# 模块级计数器：每次拉面板自增，让"刷新"这件事在返回值里可观测
_pull_count = 0


@tool.handler("ping")
def ping(args):
    """面板按钮的回调命令 —— 固定返回值，便于断言"按钮真的调到了命令"。"""
    return {"pong": "paneltool", "fromPanel": args.get("panelId")}


@tool.handler("panel_data")
def panel_data(args):
    """面板数据（协议 §3.3）。

    一个 handler 用 panelId 分派（协议 §8.1），不为每个面板各写一个。
    """
    global _pull_count
    panel_id = args.get("panelId") or ""
    reason = (args.get("context") or {}).get("reason") or "open"
    _pull_count += 1

    if panel_id == "main":
        return {
            "nodes": [
                {"type": "heading", "text": "面板协议演示"},
                {"type": "text", "text": f"第 {_pull_count} 次拉取（reason={reason}）"},
                {"type": "kv", "items": [
                    ["工具 id", tool.tool_id],
                    ["节点类型数", "6"],
                    ["进程 pid", str(_pid())],
                ]},
                {"type": "list", "items": ["heading", "text", "kv", "list", "buttons", "separator"]},
                {"type": "separator"},
                {"type": "buttons", "items": [
                    {"label": "探活", "commandId": "paneltool.ping"},
                    {"label": "危险操作", "commandId": "paneltool.ping", "style": "danger"},
                ]},
            ]
        }

    if panel_id == "empty":
        return {"nodes": []}

    if panel_id == "weird":
        # 第 2 个节点是宿主不认识的类型：宿主应**跳过它但保住其余**
        return {
            "nodes": [
                {"type": "text", "text": "未知节点之前"},
                {"type": "__future_widget__", "text": "宿主不认识这个"},
                {"type": "text", "text": "未知节点之后"},
            ]
        }

    if panel_id == "broken":
        # 刻意返回非对象：验证宿主容错路径（显示错误提示而不是崩溃 / 退出非零）
        return ["这不是一个对象"]

    return {"nodes": [{"type": "text", "text": f"未知面板 {panel_id}"}]}


def _pid() -> int:
    import os

    return os.getpid()


if __name__ == "__main__":
    raise SystemExit(tool.run())
