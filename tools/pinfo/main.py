"""pinfo —— 特权原语端到端示例（P3）。

它演示的正确姿势（与设计方案 §9 对齐）：
  · 工具只做**声明**（``tool.json`` 的 ``elevatedPrimitives``）与**调用**（SDK 的 ``primitive()``）；
  · 提权发生在 Core 进程里，本进程永远不提权、不接触任何特权句柄；
  · 需要 `ezt core start` 之后才可用（未运行时宿主返回 -32017，工具如实转告用户）。

用法：
  ezt invoke pinfo.list                    # 列出前 20 个进程
  ezt invoke pinfo.list --json '{"filter":"py","limit":5}'
"""

from __future__ import annotations

from typing import Any, Dict

from eztools import Tool
from eztools.protocol import RpcError

app = Tool()


@app.handler("list")
def list_processes(args: Dict[str, Any]) -> Dict[str, Any]:
    """经 process.enumerate 原语列出进程，支持名称过滤与条数上限。"""
    try:
        result = app.primitive("process.enumerate")
    except RpcError as exc:
        # 常见情况：Core 未运行（-32017）。工具把语义错误如实转告，不吞不糊。
        return {"ok": False, "code": exc.code, "error": str(exc)}

    processes = (result or {}).get("processes") or []
    needle = str(args.get("filter") or "").lower()
    if needle:
        processes = [p for p in processes if needle in str(p.get("name", "")).lower()]

    limit = int(args.get("limit") or app.config.get("limit") or 20)
    return {
        "ok": True,
        "total": result.get("count") if result else 0,
        "returned": min(len(processes), limit),
        "processes": processes[:limit],
    }


if __name__ == "__main__":
    app.run()
