"""协议层：分帧、编码、方法名常量、错误类型。

三条硬约束（都是 spike 实测踩出来的，见 spike/README.md）：
1. **禁用 BOM** —— `.NET` 的 ``Encoding.UTF8`` 是"带 BOM 标识"的实例，用作
   ``StandardInputEncoding`` 时会在工具 stdin 开头写 ``EF BB BF``，工具侧解析首帧直接失败。
   症状表现为"调用超时"，与原因距离极远。所以：宿主侧禁用 BOM，工具侧 stdin 用 ``utf-8-sig`` 容忍。
2. **只写 ``\\n`` 分帧** —— 混入 ``\\r\\n`` 会污染分帧。
3. **逐帧 flush** —— 不 flush 会让宿主永久等待到超时。
"""

from __future__ import annotations

import json
import sys
from typing import Any, Dict, Optional

PROTOCOL_VERSION = 1

# ── 宿主 → 工具 ──
TOOL_INITIALIZE = "tool.initialize"
TOOL_INVOKE = "tool.invoke"
TOOL_ON_CONFIG_CHANGED = "tool.onConfigChanged"
TOOL_RECOVER = "tool.recover"
TOOL_STOP = "tool.stop"
# 拉取面板数据（P4 Wave 2c）：宿主 → 工具，工具回 {"nodes": [...]}。
# 注意方向 —— 这是**宿主发起**的方法，不是"工具调的宿主 API"，
# 故不走 weight 白名单；档位检查在宿主发起侧（仅 weight: full）。
# 见 docs/P4-Wave2c-面板协议.md §3 / §6.2。
TOOL_PANEL_DATA = "tool.panel.data"

# ── 工具 → 宿主 ──
HOST_READY = "host.ready"
HOST_LOG = "host.log"
HOST_NOTIFY = "host.notify"
HOST_PROGRESS = "host.progress"
HOST_STORAGE_GET = "host.storage.get"
HOST_STORAGE_SET = "host.storage.set"
HOST_STORAGE_REMOVE = "host.storage.remove"
HOST_PRIMITIVE_CALL = "host.primitive.call"
HOST_INVOKE_TOOL = "host.invokeTool"

# ── JSON-RPC 标准错误码 ──
PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603

# ── 本项目扩展码（与 C# 侧 RpcErrorCodes 保持一致）──
NOT_SUPPORTED = -32001
UNKNOWN_COMMAND = -32002
UNKNOWN_HANDLER = -32003
# 工具间调用成环（含自调用）：宿主拒绝而不等待，见 docs/P4-实施方案.md §2.2。
INVOKE_CYCLE = -32004
# 当前 weight 档位不提供该 API（script 档拿不到 storage / primitive / invokeTool）。
WEIGHT_NOT_PERMITTED = -32005


class RpcError(Exception):
    """可以从 handler 里抛出，SDK 会把它转成 JSON-RPC error 返回宿主。"""

    def __init__(self, message: str, code: int = INTERNAL_ERROR, data: Any = None) -> None:
        super().__init__(message)
        self.code = code
        self.data = data


def encode_frame(payload: Dict[str, Any]) -> str:
    """编码为一帧：一行 JSON + ``\\n``，不含 BOM。"""
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n"


def write_frame(payload: Dict[str, Any]) -> None:
    """写一帧并立即 flush。"""
    sys.stdout.write(encode_frame(payload))
    sys.stdout.flush()


def bootstrap_streams() -> None:
    """固定 stdio 的三个流的编码与换行方式。幂等，可重复调用。

    - stdout：``utf-8`` + ``newline="\\n"``（只写 ``\\n``，否则污染分帧）
    - stdin：``utf-8-sig`` —— **容忍对端写入的 UTF-8 BOM**。宿主侧已经禁用 BOM，
      但工具侧保留这层容忍，因为 BOM 导致的"首帧丢失 → 调用超时"极难定位，兜底很便宜。
    - stderr：``utf-8``，调试输出走这里，绝不写 stdout。
    """
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", newline="\n")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", newline="\n")
        if hasattr(sys.stdin, "reconfigure"):
            sys.stdin.reconfigure(encoding="utf-8-sig", newline="\n")
    except Exception as exc:  # pragma: no cover - 某些被嵌环境不支持 reconfigure
        log_to_stderr(f"reconfigure 流失败（继续运行）: {exc}")


def log_to_stderr(message: str) -> None:
    """调试输出必须走 stderr —— 写 stdout 会污染 JSON-RPC 分帧。"""
    try:
        sys.stderr.write(message + "\n")
        sys.stderr.flush()
    except Exception:  # pragma: no cover - 日志失败不得影响业务
        pass


def read_frame() -> Optional[Dict[str, Any]]:
    """读一帧。返回 None 表示 stdin 已关闭（宿主收回了进程）。"""
    line = sys.stdin.readline()
    if line == "":
        return None

    line = line.strip()
    if not line:
        return {}  # 空行忽略，交由调用方继续读

    try:
        return json.loads(line)
    except json.JSONDecodeError as exc:
        raise RpcError(
            f"非 JSON 帧（分帧被污染？）: {exc}", PARSE_ERROR, data={"line": line[:200]}
        ) from exc
