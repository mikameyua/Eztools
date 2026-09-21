"""weight: script 档的参考实现 —— 每次调用起一个进程，用完即退。

它存在的理由不只是"少一个样板"：script 档按设计方案 §5.1 **只提供基础 API**
（日志 / 通知），没有依赖、没有设置界面。这条约束由宿主侧的闸门强制
（见 ``ToolHostManager.EnsureApiAllowed``），而本工具就是那道闸门的活体验收用例 ——
三个命令里有**两个是故意要被拒绝的**，拒绝时把错误码原样带回给调用方。

为什么不做成"复制即用的模板"：模板应当是**推荐形态**，而 script 档恰恰是
受限形态（拿不到存储、拿不到特权原语、不能调别的工具）。样板放这里，模板留在
``sdk/python/template``。
"""

from __future__ import annotations

import os

from eztools import RpcError, Tool

tool = Tool()


@tool.handler("echo")
def echo(args):
    """基础 API 路径：日志 + 返回值。host.log 属基础 API，必须放行。"""
    text = args.get("text") or ""
    tool.log(f"oneshot 回显 {len(text)} 字符", "info")
    return {"echo": text, "length": len(text), "pid": os.getpid()}


@tool.handler("storage")
def storage(args):
    """预期被宿主拒绝（-32005）。

    把 ``RpcError.code`` 原样返回，验收脚本才能断言"这确实是**档位拒绝**"，
    而不是"随便报了个错"——错误码是唯一能区分这两者的东西。
    """
    try:
        tool.storage_set("oneshot.probe", "x")
        value = tool.storage_get("oneshot.probe")
    except RpcError as exc:
        return {"ok": False, "code": exc.code, "message": str(exc)}

    return {"ok": True, "code": 0, "value": value}


@tool.handler("call")
def call(args):
    """预期被宿主拒绝（-32005）：script 档不能使用 host.invokeTool。"""
    try:
        tool.invoke_tool(
            args.get("toolId") or "echo",
            args.get("commandId") or "echo.echo",
            {"text": "oneshot"},
        )
    except RpcError as exc:
        return {"ok": False, "code": exc.code, "message": str(exc)}

    return {"ok": True, "code": 0}


if __name__ == "__main__":
    raise SystemExit(tool.run())
