# -*- coding: utf-8 -*-
# Copyright (c) 2026 Eztools contributors
# SPDX-License-Identifier: GPL-3.0-or-later

"""keepalive —— resident 生命周期验收工具。

进程常驻：同一宿主内多次调用 ping 应返回同一 pid 且计数递增；
crash 以退出码 3 结束进程，宿主应自动重启（新 pid）并在连续崩溃后熔断。
"""
import os
import sys

from eztools import Tool

tool = Tool()
_calls = {"ping": 0, "recover": 0}


@tool.handler("ping")
def ping(args):
    """返回 pid 与调用计数：同一进程内递增，自动重启后 pid 变化。"""
    _calls["ping"] += 1
    return {"pid": os.getpid(), "calls": _calls["ping"], "recovers": _calls["recover"]}


@tool.on_recover()
def on_recover(args):
    """§8.2 / W3 P0-3：resident 必须实现恢复（清单 \"recover\": true 的真实兑现）。

    本工具没有需要恢复的业务状态，但**必须让恢复通道真实可走**：
    宿主崩溃自动重启后调用它，这里记一次数，ping 回传 —— 验收据此断言
    "重启 → recover 被调用"的链路而不是只看进程活了。
    """
    _calls["recover"] += 1


@tool.handler("crash")
def crash(args):
    """故意以退出码 3 崩溃，验证宿主自动重启与熔断。"""
    sys.stderr.write("keepalive: 故意崩溃（验收）\n")
    sys.stderr.flush()
    sys.exit(3)


if __name__ == "__main__":
    raise SystemExit(tool.run())
