"""回显工具：协议往返、UTF-8 编码与大 payload 的活体探针。

这个工具刻意保持极简——它是 `ezt selftest` 的基准工具，用来证明"链路本身是通的"。
如果连它都跑不起来，问题一定在宿主、运行时或 SDK，而不是某个业务工具的代码。
"""

from eztools import Tool

tool = Tool()


@tool.handler("echo")
def echo(args):
    """原样回显文本，并附带长度（长度可用于验证多字节字符没有被逐字节破坏）。"""
    text = args.get("text")
    if text is None:
        text = ""
    if not isinstance(text, str):
        text = str(text)

    if tool.config.get("uppercase"):
        text = text.upper()

    return {
        "echo": text,
        "length": len(text),
        "byteLength": len(text.encode("utf-8")),
    }


@tool.handler("large")
def large(args):
    """生成指定字符数的文本，用于验证大 payload 不被截断。"""
    size = int(args.get("size") or 1024 * 1024)
    return {"text": "A" * size, "length": size}


if __name__ == "__main__":
    raise SystemExit(tool.run())
