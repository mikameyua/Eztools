"""工具模板：复制这个目录，改 tool.json 与下面的 handler 即可。

三件事不需要你做：
- 不需要写任何 UI 代码 —— 设置页由 tool.json 的 config schema 自动渲染（P1）
- 不需要注册命令 / 热键 / 菜单 —— 由 contributes 声明，宿主自动接管
- 不需要处理编码、分帧、超时、崩溃 —— 那是 SDK 与宿主的职责
"""

from eztools import Tool

tool = Tool()


@tool.handler("run")
def run(args):
    """handler 名必须与 tool.json 里声明的 handler 一致。"""
    # 读取配置（由宿主下发，未知键有默认值保护）
    enabled = tool.config.get("enabled", True)
    if not enabled:
        return {"skipped": True}

    # 需要向宿主说话时用这些：
    #   tool.log("正在处理")                      → 写入工具日志
    #   tool.progress(50, "一半了")               → 长任务进度（weight=task）
    #   tool.storage_set("lastRun", "2026-09-17") → 私有 KV，卸载工具也保留
    text = args.get("text", "")

    # 默认实现刻意同时回显原文与长度：复制模板出来直接跑就能看出链路是通的
    # （回显证明编码与分帧无损，长度证明参数确实传进来了）
    return {
        "echo": text,
        "length": len(text),
        "bytes": len(text.encode("utf-8")),
    }


if __name__ == "__main__":
    raise SystemExit(tool.run())
