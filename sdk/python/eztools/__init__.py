# Copyright (c) 2026 Eztools contributors
# SPDX-License-Identifier: GPL-3.0-or-later

"""Eztools Python SDK。

工具的入口只需要三行：

    from eztools import Tool

    tool = Tool()

    @tool.handler("count")
    def count(args):
        return {"chars": len(args.get("text", ""))}

    if __name__ == "__main__":
        tool.run()

SDK 负责协议的全部细节（分帧、编码、错误包装、宿主回调），工具只写业务。
"""

from . import protocol as _protocol
from .protocol import PROTOCOL_VERSION, RpcError, encode_frame
from .tool import Tool, ToolContext, main

# 尽早固定 stdio 编码：一旦工具有任何输出发生在 reconfigure 之前，就会在中文 Windows 上乱码。
# 幂等，tool.run() 里还会再调一次。
_protocol.bootstrap_streams()

__all__ = [
    "Tool",
    "ToolContext",
    "main",
    "PROTOCOL_VERSION",
    "RpcError",
    "encode_frame",
]

# 与 Directory.Build.props 的 <Version> 保持一致（主版本.次版本 = 功能波次序号）。
# ⚠️ 改这里必须同时改 Directory.Build.props，否则 SDK 报出的版本会与宿主不一致。
__version__ = "0.11.0"
SDK_VERSION = __version__
