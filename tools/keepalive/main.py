# -*- coding: utf-8 -*-
"""keepalive —— resident 生命周期验收工具。

进程常驻：同一宿主内多次调用 ping 应返回同一 pid 且计数递增；
crash 以退出码 3 结束进程，宿主应自动重启（新 pid）并在连续崩溃后熔断。
"""
import os
import sys

from eztools import Tool

tool = Tool()
_calls = {"ping": 0}


@tool.handler("ping")
def ping(args):
    """返回 pid 与调用计数：同一进程内递增，自动重启后 pid 变化。"""
    _calls["ping"] += 1
    return {"pid": os.getpid(), "calls": _calls["ping"]}


@tool.handler("crash")
def crash(args):
    """故意以退出码 3 崩溃，验证宿主自动重启与熔断。"""
    sys.stderr.write("keepalive: 故意崩溃（验收）\n")
    sys.stderr.flush()
    sys.exit(3)


if __name__ == "__main__":
    raise SystemExit(tool.run())
