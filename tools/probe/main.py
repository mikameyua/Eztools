# Copyright (c) 2026 Eztools contributors
# SPDX-License-Identifier: GPL-3.0-or-later

"""诊断探针：把"工具进程真实看到的世界"暴露出来。

它存在的理由很具体——spike 阶段两个最贵的坑都是"工具侧看到的和宿主以为的不一样"：

1. ``PYTHONPATH`` 从父进程泄漏进 ``sys.path``（用户机器上任何设过该变量的软件都会改变工具行为）；
2. ``-I`` 隐含 ``-E``，导致 ``PYTHONUTF8`` / ``PYTHONIOENCODING`` 这类环境变量被忽略，
   编码只能靠 CLI 参数与入口 ``reconfigure``。

``probe.env`` 把这两件事变成可观测的返回值，将来任何一个工具行为诡异时，先跑它。
``probe.crash`` / ``probe.hang`` 是 ``ezt selftest`` 的钩子，用来验证宿主真的能回收失控进程。
``probe.delegate`` / ``probe.selfCall`` 是 P4 工具间协作（``host.invokeTool``）的验收钩子：
前者打通"跨工具调用"，后者证明"成环被立刻拒绝而不是挂起"。
"""

from __future__ import annotations

import os
import sys
import time

from eztools import Tool, __version__ as sdk_version
from eztools import protocol as _p

tool = Tool()


@tool.handler("echo")
def echo(args):
    text = args.get("text") or ""
    return {"echo": text, "length": len(text)}


@tool.handler("env")
def env(args):
    """回报运行环境的关键事实。任何"换台机器就坏了"的问题都从这里开始查。"""
    return {
        "python": sys.version,
        "executable": sys.executable,
        "prefix": sys.prefix,
        "sdkVersion": sdk_version,
        "pid": os.getpid(),
        "cwd": os.getcwd(),
        "isolated": sys.flags.isolated,
        "ignoreEnvironment": sys.flags.ignore_environment,
        "noUserSite": sys.flags.no_user_site,
        "utf8Mode": sys.flags.utf8_mode,
        "stdoutEncoding": getattr(sys.stdout, "encoding", None),
        "stdinEncoding": getattr(sys.stdin, "encoding", None),
        "sysPath": list(sys.path),
        # 关键断言：隔离模式下父进程的 PYTHONPATH 不该出现在 sys.path 中
        "inheritedPythonPath": os.environ.get("PYTHONPATH"),
        "pythonPathLeaked": any(
            p and os.environ.get("PYTHONPATH", "\x00") in p for p in sys.path
        ),
        "toolDataDir": os.environ.get("EZTOOLS_TOOL_DATA_DIR"),
        "sitePackagesOnPath": [p for p in sys.path if "site-packages" in p],
    }


@tool.handler("storage")
def storage(args):
    """写入后读回，验证 host.storage 双向通路。

    宿主把这份数据落在 ``%LOCALAPPDATA%\\Eztools\\toolsdata\\<id>\\storage.json``，
    与工具代码目录分离——卸载或升级工具不会丢用户数据（设计方案 §6.2）。
    """
    key = args.get("key") or "probe.lastCheck"
    token = args.get("value") or f"v{int(time.time() * 1000)}"

    tool.log(f"写入私有存储 {key}", "debug")
    tool.storage_set(key, token)
    read_back = tool.storage_get(key)

    return {
        "key": key,
        "wrote": token,
        "read": read_back,
        "roundTrip": read_back == token,
        "dataDir": str(tool.data_dir()),
    }


@tool.handler("crash")
def crash(args):
    """硬终止进程（os._exit 不走清理流程，等价于真实崩溃）。"""
    code = int(args.get("exitCode") or 3)
    sys.stderr.write(f"probe.crash: 主动以 exit={code} 终止\n")
    sys.stderr.flush()
    os._exit(code)


@tool.handler("hang")
def hang(args):
    """挂起不返回，交给宿主超时回收。"""
    seconds = float(args.get("seconds") or 3600)
    sys.stderr.write(f"probe.hang: 挂起 {seconds}s\n")
    sys.stderr.flush()
    time.sleep(seconds)
    return {"unreachable": True}


@tool.handler("delegate")
def delegate(args):
    """转调另一个工具的命令（P4 的工具间协作通路）。

    刻意把宿主的错误码原样带回，而不是吞掉或改写成自己的文案：这条命令的价值就是让
    验收脚本能同时断言**成功路径**与**拒绝路径**（不存在 / 归属不符 / 成环）。
    吞掉错误码会让负向用例退化成恒真断言。
    """
    target = args.get("toolId") or ""
    command = args.get("commandId") or ""
    payload = args.get("args") or {}

    if not target or not command:
        return {"ok": False, "code": _p.INVALID_PARAMS, "message": "需要 toolId 与 commandId"}

    try:
        envelope = tool.invoke_tool(target, command, payload) or {}
    except _p.RpcError as exc:
        return {"ok": False, "code": exc.code, "message": str(exc)}

    return {
        "ok": True,
        "code": 0,
        "toolId": envelope.get("toolId"),
        "commandId": envelope.get("commandId"),
        "delegated": envelope.get("result"),
    }


@tool.handler("self_call")
def self_call(args):
    """调用自己 —— 必然成环。

    宿主必须**立刻拒绝**而不是把这次调用排在自己后面：那正是死锁（自己等自己）。
    这是 P4 环检测最直接的一条活体证据。
    """
    try:
        tool.invoke_tool(
            tool.tool_id,
            args.get("commandId") or "probe.echo",
            {"text": "self"},
        )
    except _p.RpcError as exc:
        return {"caught": True, "code": exc.code, "message": str(exc)}

    return {"caught": False, "code": 0, "message": "自调用竟然没有被拒绝"}


@tool.handler("progress")
def progress(args):
    """尝试上报进度 —— 本工具是 ``lifecycle: transient``，按设计方案 §5.3 应当被拒绝。

    ``host.progress`` 是"仅 task 可用"的方法，收紧后由宿主回 -32005。
    与 oneshot 的两个命令一样，把错误码原样带回，让验收能断言"确实是档位/生命周期拒绝"。
    """
    try:
        tool.progress(float(args.get("percent") or 50), "probe 试图上报进度")
    except _p.RpcError as exc:
        return {"ok": False, "code": exc.code, "message": str(exc)}

    return {"ok": True, "code": 0, "lifecycle": tool.manifest.get("lifecycle")}


@tool.handler("notify")
def notify(args):
    """请求宿主弹通知，并把宿主的 ``delivered`` **原样**带回。

    这条命令的价值在于让"通知到底送没送到"变成可观测的返回值：
    CLI 宿主没有 UI，应答必须是 ``delivered: false`` + 说明"没有 UI"；
    托盘宿主则应答 ``delivered: true``。

    它同时是一条防退化的断言钩子 —— 早年宿主恒定回 ``delivered: false``
    （"通知通道在 P2 实现"），若哪天又退回恒定值，验收脚本会立刻发现。
    """
    try:
        result = tool.notify(
            args.get("title") or "探针通知",
            args.get("body") or "来自 probe.notify 的测试通知",
        ) or {}
    except _p.RpcError as exc:
        return {"ok": False, "code": exc.code, "message": str(exc)}

    return {
        "ok": True,
        "code": 0,
        "delivered": result.get("delivered"),
        "reason": result.get("reason"),
    }


if __name__ == "__main__":
    raise SystemExit(tool.run())
