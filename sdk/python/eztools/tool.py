"""工具基类与主循环。

设计取舍说明（为什么 SDK 要读 ``tool.json``）：
``tool.json`` 里已经声明了"命令 id → handler 名"的映射，而它就在入口文件旁边。
让 SDK 读它，工具侧就**只需要按 handler 名注册函数**，命令 id 永远由清单单一来源决定——
不会出现"改了清单忘了改代码"的漂移。

关于 ``-I`` 隔离（重要）：
宿主以 ``python -I -X utf8 -u main.py`` 启动工具。``-I`` 隐含 ``-E -s``，会忽略父进程的
``PYTHONPATH`` 与用户 site-packages（实测：裸启动时父进程的 ``PYTHONPATH`` 会出现在
``sys.path[1]``，意味着"用户机器上任何设过 PYTHONPATH 的软件都能改变工具行为"）。
隔离的代价是**不能靠环境变量传 import 路径**，所以 SDK 由宿主植入运行时的 site-packages，
而工具自己的依赖（``Lib/``）由本模块的 :func:`Tool._install_import_paths` 显式加进 ``sys.path``。
"""

from __future__ import annotations

import json
import os
import sys
import traceback
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from . import protocol as _p

Handler = Callable[[Dict[str, Any]], Any]


class ToolContext:
    """宿主在 ``tool.initialize`` 里下发的上下文。"""

    def __init__(self, raw: Optional[Dict[str, Any]] = None) -> None:
        raw = raw or {}
        self.data_dir: Optional[str] = raw.get("dataDir")
        self.tool_dir: Optional[str] = raw.get("toolDir")
        self.log_file: Optional[str] = raw.get("logFile")
        self.host_version: Optional[str] = raw.get("hostVersion")
        self.protocol: int = raw.get("protocol") or _p.PROTOCOL_VERSION

    def __repr__(self) -> str:  # pragma: no cover - 调试用
        return f"ToolContext(data_dir={self.data_dir!r}, protocol={self.protocol})"


class Tool:
    """工具基类。子类化或直接实例化都可以，推荐直接实例化 + 装饰器注册。"""

    def __init__(
        self,
        tool_id: Optional[str] = None,
        name: Optional[str] = None,
        manifest_path: Optional[str] = None,
    ) -> None:
        self.tool_id = tool_id or os.environ.get("EZTOOLS_TOOL_ID") or ""
        self.name = name or self.tool_id
        self.manifest_path = Path(manifest_path) if manifest_path else None
        self.manifest: Dict[str, Any] = {}
        self.context = ToolContext()
        self.config: Dict[str, Any] = {}

        self._handlers: Dict[str, Handler] = {}
        self._command_to_handler: Dict[str, str] = {}
        self._handler_to_commands: Dict[str, List[str]] = {}
        self._host_seq = 0
        self._exit_requested = False
        self._initialized = False

    # ── 注册 API ────────────────────────────────────────────────

    def handler(self, name: str) -> Callable[[Handler], Handler]:
        """把一个函数注册为 handler。名字必须与 ``tool.json`` 里声明的 ``handler`` 一致。"""

        def decorator(fn: Handler) -> Handler:
            self._handlers[name] = fn
            return fn

        return decorator

    def command(self, command_id: str) -> Callable[[Handler], Handler]:
        """按命令 id 注册（等价于先用清单查到 handler 名再注册）。"""

        def decorator(fn: Handler) -> Handler:
            self._handlers[command_id] = fn
            return fn

        return decorator

    # ── 宿主回调 ────────────────────────────────────────────────

    def call_host(self, method: str, params: Optional[Dict[str, Any]] = None) -> Any:
        """向宿主发一次请求并等待响应。等待期间若收到宿主请求会就地处理。"""
        self._host_seq += 1
        request_id = f"h{self._host_seq}"
        _p.write_frame(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "method": method,
                "params": params or {},
            }
        )

        while True:
            frame = _p.read_frame()
            if frame is None:
                raise RuntimeError("宿主已关闭 stdio 连接")
            if not frame:
                continue

            if "method" in frame:
                # 宿主在等我们处理 handler 期间发来的请求（tool.stop 最常见）
                self._handle_request(frame)
                continue

            if frame.get("id") == request_id:
                if "error" in frame:
                    err = frame["error"] or {}
                    raise _p.RpcError(
                        err.get("message", "宿主返回错误"),
                        int(err.get("code", _p.INTERNAL_ERROR)),
                        err.get("data"),
                    )
                return frame.get("result")

    def log(self, message: str, level: str = "info") -> None:
        self.call_host(_p.HOST_LOG, {"level": level, "message": str(message)})

    def notify(self, title: str, body: str) -> Any:
        return self.call_host(_p.HOST_NOTIFY, {"title": title, "body": body})

    def progress(self, percent: float, message: str = "", task_id: Optional[str] = None) -> None:
        self.call_host(
            _p.HOST_PROGRESS,
            {"taskId": task_id or self.tool_id, "percent": percent, "message": message},
        )

    def storage_get(self, key: str, default: Any = None) -> Any:
        result = self.call_host(_p.HOST_STORAGE_GET, {"key": key}) or {}
        value = result.get("value")
        return default if value is None else value

    def storage_set(self, key: str, value: Any) -> None:
        self.call_host(_p.HOST_STORAGE_SET, {"key": key, "value": value})

    def storage_remove(self, key: str) -> None:
        self.call_host(_p.HOST_STORAGE_REMOVE, {"key": key})

    def primitive(self, name: str, args: Optional[Dict[str, Any]] = None) -> Any:
        """调用一个特权原语（P3）。

        前提：``tool.json`` 的 ``elevatedPrimitives`` 里声明过 ``name``，
        否则宿主直接拒绝（错误码 -32013）—— 清单是工具的唯一授权面。
        原语需要提权而 Core 未提权时，宿主透传 Core 的 -32015（ElevationRequired）。

        已知原语见 ``PrimitiveNames.Known``（首批：process.enumerate / process.terminate /
        handles.enumerate / volume.enumerate / volume.readMft）。
        """
        return self.call_host(_p.HOST_PRIMITIVE_CALL, {"name": name, "args": args or {}})

    def invoke_tool(
        self,
        tool_id: str,
        command_id: str,
        args: Optional[Dict[str, Any]] = None,
    ) -> Any:
        """调用**另一个工具**的命令（P4，``host.invokeTool``）。

        返回值是宿主包装过的结果：``{"result": <对方工具返回值>, "toolId", "commandId", "elapsedMs"}``，
        取业务数据请用 ``.get("result")``。

        限制（宿主侧强制，见 docs/P4-实施方案.md §2.4）：
        - ``weight: script`` 档的工具**不能**调用它（回 -32005）；
        - 不能调自己、也不能调调用链上的任何工具（回 -32004）——那是死锁，宿主直接拒绝而非等待；
        - 目标工具被禁用 / 已熔断时回错，不会静默失败。
        """
        return self.call_host(
            _p.HOST_INVOKE_TOOL,
            {"toolId": tool_id, "commandId": command_id, "args": args or {}},
        )

    def entry_dir(self) -> Path:
        """入口脚本所在目录（= 工具目录，也是本进程的工作目录）。"""
        return _entry_dir()

    def data_dir(self) -> Path:
        """工具私有数据目录（与代码目录分离，卸载工具不丢数据）。"""
        raw = self.context.data_dir or os.environ.get("EZTOOLS_TOOL_DATA_DIR")
        if not raw:
            raise RuntimeError("宿主未下发数据目录")
        path = Path(raw)
        path.mkdir(parents=True, exist_ok=True)
        return path

    # ── 主循环 ──────────────────────────────────────────────────

    def run(self) -> int:
        """阻塞运行主循环，直到宿主请求停止或 stdin 关闭。"""
        _p.bootstrap_streams()
        self._load_manifest()
        self._install_import_paths()

        # 通知宿主"我已就绪"。通知无 id，宿主不回响应。
        _p.write_frame(
            {
                "jsonrpc": "2.0",
                "method": _p.HOST_READY,
                "params": {
                    "toolId": self.tool_id,
                    "sdkVersion": _sdk_version(),
                    "python": sys.version.split()[0],
                    "pid": os.getpid(),
                    "handlers": sorted(self._handlers.keys()),
                },
            }
        )

        while not self._exit_requested:
            try:
                frame = _p.read_frame()
            except _p.RpcError as exc:
                # 分帧被污染：告知宿主但继续跑，不因为一帧坏掉就退出
                _p.log_to_stderr(str(exc))
                continue

            if frame is None:
                break
            if not frame:
                continue

            if "method" in frame:
                self._handle_request(frame)
            # 否则是迟到的响应，忽略

        return 0

    # ── 内部 ────────────────────────────────────────────────────

    def _handle_request(self, frame: Dict[str, Any]) -> None:
        method = frame.get("method")
        request_id = frame.get("id")
        params = frame.get("params") or {}

        if request_id is None:
            # JSON-RPC 通知：处理但不回复
            try:
                self._dispatch(method, params)
            except Exception as exc:  # pragma: no cover - 通知失败不该中断工具
                _p.log_to_stderr(f"处理通知 {method} 失败: {exc}")
            return

        try:
            result = self._dispatch(method, params)
            _p.write_frame({"jsonrpc": "2.0", "id": request_id, "result": result})
        except _p.RpcError as exc:
            _p.write_frame(
                {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "error": {"code": exc.code, "message": str(exc), "data": exc.data},
                }
            )
        except Exception as exc:  # noqa: BLE001 - 任何异常都必须变成协议错误，否则宿主会等到超时
            detail = traceback.format_exc()
            _p.log_to_stderr(detail)
            _p.write_frame(
                {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "error": {
                        "code": _p.INTERNAL_ERROR,
                        "message": f"{type(exc).__name__}: {exc}",
                        "data": detail,
                    },
                }
            )

    def _dispatch(self, method: Optional[str], params: Dict[str, Any]) -> Any:
        if method == _p.TOOL_INITIALIZE:
            self.config = params.get("config") or {}
            self.context = ToolContext(params.get("context"))
            if params.get("toolId"):
                self.tool_id = params["toolId"]
            self.name = self.name or self.tool_id
            self._initialized = True
            hook = self._handlers.get("__on_initialize__")
            if hook is not None:
                hook(params)
            return {
                "toolId": self.tool_id,
                "sdkVersion": _sdk_version(),
                "python": sys.version.split()[0],
                "pid": os.getpid(),
                "protocol": _p.PROTOCOL_VERSION,
            }

        if method == _p.TOOL_INVOKE:
            return self._invoke(params)

        if method == _p.TOOL_ON_CONFIG_CHANGED:
            self.config = params.get("config") or {}
            hook = self._handlers.get("__on_config_changed__")
            if hook is not None:
                hook(self.config)
            return {"ok": True}

        if method == _p.TOOL_PANEL_DATA:
            return self._panel_data(params)

        if method == _p.TOOL_RECOVER:
            hook = self._handlers.get("__on_recover__")
            if hook is None:
                # 非 resident 工具没有恢复语义，明确告诉宿主而不是假装成功
                return {"ok": False, "reason": "本工具未实现状态恢复（非 resident）"}
            hook(params.get("lastState") or {})
            return {"ok": True}

        if method == _p.TOOL_STOP:
            hook = self._handlers.get("__on_stop__")
            if hook is not None:
                try:
                    hook({})
                except Exception as exc:  # pragma: no cover
                    _p.log_to_stderr(f"on_stop 失败: {exc}")
            self._exit_requested = True
            return {"ok": True}

        raise _p.RpcError(f"工具未实现方法 {method}", _p.METHOD_NOT_FOUND)

    def _invoke(self, params: Dict[str, Any]) -> Any:
        handler_name = params.get("handler")
        command_id = params.get("commandId")

        if handler_name is None and command_id:
            handler_name = self._command_to_handler.get(command_id)
            if handler_name is None:
                raise _p.RpcError(
                    f"清单未声明命令 {command_id}（可用: {sorted(self._command_to_handler)}）",
                    _p.UNKNOWN_COMMAND,
                )

        if not handler_name:
            raise _p.RpcError("tool.invoke 需要 commandId 或 handler", _p.INVALID_PARAMS)

        fn = self._handlers.get(handler_name)
        if fn is None:
            raise _p.RpcError(
                f"未注册 handler '{handler_name}'（已注册: {sorted(self._handlers)}）",
                _p.UNKNOWN_HANDLER,
            )

        args = params.get("args")
        if args is None:
            args = {}
        if not isinstance(args, dict):
            raise _p.RpcError("args 必须是 JSON 对象", _p.INVALID_PARAMS)

        result = fn(args)
        return {} if result is None else result

    def _panel_data(self, params: Dict[str, Any]) -> Any:
        """处理 ``tool.panel.data``（P4 Wave 2c）。

        handler 名固定为 ``panel_data``（一个 handler 用 ``args.panelId`` 分派），
        不是每个面板一个 handler —— 见 docs/P4-Wave2c-面板协议.md §8.1。

        **未注册 handler 时返回空面板而不是报错**：与 ``tool.recover`` 的"明确告知未实现"
        不同 —— 空面板是**合法状态**（工具确实没东西可显示），报错会让宿主显示一个
        吓人的错误面板，而实际语义只是"暂无内容"。
        """
        fn = self._handlers.get("panel_data")
        if fn is None:
            return {"nodes": []}

        result = fn(
            {
                "panelId": params.get("panelId"),
                "context": params.get("context") or {},
            }
        )
        if isinstance(result, dict):
            return result
        # 返回了非字典（忘了 return？）：包装成空面板 + 提示节点，**不报错**
        return {
            "nodes": [
                {"type": "text", "text": "工具未返回面板数据（handler 返回值不是对象）"}
            ]
        }

    def _load_manifest(self) -> None:
        path = self.manifest_path
        if path is None:
            entry_dir = _entry_dir()
            candidate = entry_dir / "tool.json"
            path = candidate if candidate.is_file() else None

        if path is None or not path.is_file():
            return

        try:
            self.manifest = json.loads(path.read_text(encoding="utf-8"))
        except Exception as exc:  # pragma: no cover
            _p.log_to_stderr(f"读取 tool.json 失败: {exc}")
            return

        self.manifest_path = path
        if not self.tool_id:
            self.tool_id = self.manifest.get("id", "")
        if not self.name or self.name == self.tool_id:
            self.name = self.manifest.get("name") or self.tool_id

        for command in (self.manifest.get("contributes") or {}).get("commands", []) or []:
            command_id = command.get("id")
            handler = command.get("handler")
            if command_id and handler:
                self._command_to_handler[command_id] = handler
                self._handler_to_commands.setdefault(handler, []).append(command_id)

    def _install_import_paths(self) -> None:
        """把工具目录与 ``Lib/``（vendor 的第三方依赖）加进 ``sys.path``。

        这是设计方案 §5.4 的依赖策略：依赖直接 vendor 到工具目录的 ``Lib/``，
        不依赖 ``pip install``，也不需要打包。实测含编译代码的 wheel（如 Pillow）
        在"同一 Windows 平台 + Python 版本由宿主固定"时可以直接用。
        """
        # 注意：工具进程的工作目录是**工具目录**（宿主启动时就设成这里），
        # 所以工具内不要用相对路径去访问调用方的文件——宿主/{CLI} 会传绝对路径进来。
        entry_dir = _entry_dir()
        lib_dir = entry_dir / "Lib"
        for path in (str(entry_dir), str(lib_dir)):
            if path not in sys.path:
                sys.path.insert(0, path)


def _entry_dir() -> Path:
    """入口脚本所在目录。宿主以 ``python -I -X utf8 -u <entry>`` 启动，故 argv[0] 即入口。"""
    if sys.argv and sys.argv[0] and sys.argv[0].endswith(".py"):
        return Path(sys.argv[0]).resolve().parent
    return Path.cwd()


def _sdk_version() -> str:
    from . import __version__

    return __version__


def main(tool: Optional[Tool] = None) -> int:
    """便利入口：``if __name__ == "__main__": main()``。"""
    return (tool or Tool()).run()
