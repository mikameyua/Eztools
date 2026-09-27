# -*- coding: utf-8 -*-
# Copyright (c) 2026 Eztools contributors
# SPDX-License-Identifier: GPL-3.0-or-later

"""tasktool —— task 生命周期验收工具。

宿主启动钩子（RunStartupLifecycleAsync）执行 startup 命令：宿主审计日志出现
"task 工具 tasktool 启动执行 / 执行完成" 即为启动钩子真实执行过的凭证。
（工具进程以 -I 隔离启动不继承环境变量，所以可观测凭证放宿主日志而非文件标记。）
"""
import os

from eztools import Tool

tool = Tool()


@tool.handler("startup")
def startup(args):
    """宿主启动时执行一次；返回值仅记录用。"""
    return {"ok": True, "pid": os.getpid(), "note": "startup hook executed"}


if __name__ == "__main__":
    raise SystemExit(tool.run())
