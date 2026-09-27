# Copyright (c) 2026 Eztools contributors
# SPDX-License-Identifier: GPL-3.0-or-later

"""面板演示工具（P4 Wave 2c 验收钩子）。

它存在的理由是**让面板协议变成可断言的**：全部 6 种节点类型各来一个，
外加三条容错路径（空面板 / 含未知 type / 返回非对象）——
这样 `ezt panel paneltool <id>` 的返回值就能同时覆盖正向与负向用例，
而验收脚本**不需要 GUI**（见 docs/P4-Wave2c-面板协议.md §9）。

刻意**不**在 handler 里做任何"看起来更聪明"的事（如自动补 type、过滤空值）：
这个工具是**探针**，它要如实暴露宿主拿到什么，探针越"聪明"，断言越假。
"""

from __future__ import annotations

import os
import time

from eztools import Tool

tool = Tool()

# 模块级计数器：每次拉面板自增，让"刷新"这件事在返回值里可观测
_pull_count = 0


@tool.handler("ping")
def ping(args):
    """面板按钮的回调命令 —— 固定返回值，便于断言"按钮真的调到了命令"。"""
    return {"pong": "paneltool", "fromPanel": args.get("panelId")}


@tool.handler("panel_data")
def panel_data(args):
    """面板数据（协议 §3.3）。

    一个 handler 用 panelId 分派（协议 §8.1），不为每个面板各写一个。
    """
    global _pull_count
    panel_id = args.get("panelId") or ""
    reason = (args.get("context") or {}).get("reason") or "open"
    _pull_count += 1

    if panel_id == "main":
        return {
            "nodes": [
                {"type": "heading", "text": "面板协议演示"},
                {"type": "text", "text": f"第 {_pull_count} 次拉取（reason={reason}）"},
                {"type": "kv", "items": [
                    ["工具 id", tool.tool_id],
                    ["节点类型数", "6"],
                    ["进程 pid", str(_pid())],
                ]},
                {"type": "list", "items": ["heading", "text", "kv", "list", "buttons", "separator"]},
                {"type": "separator"},
                {"type": "buttons", "items": [
                    {"label": "探活", "commandId": "paneltool.ping"},
                    {"label": "危险操作", "commandId": "paneltool.ping", "style": "danger"},
                ]},
            ]
        }

    if panel_id == "input":
        # input 节点（协议 §3.7）的验收面。
        #
        # 为什么把"合法"与三种"非法"放进**同一个面板**：契约层（§12.2 第 1 步）要断言的
        # 是"宿主对同一份载荷的分支判断"，夹具分开就变成三个独立载荷 ——
        # 那样"重复 key 只忽略后者、不影响前者"这条就无从断言（没有"前者"）。
        #
        # 输入值回显：inputs 由宿主带过来（§3.7.2）。这里**刻意原样回显**，
        # 而不是"聪明地"做点什么 —— 探针要如实暴露宿主送来了什么。
        # 用 repr 包起来，值里的空格 / 空串在 CLI 输出里才看得见（否则 " " 和 "" 长得一样）。
        #
        # ⚠️ **"字段在不在"必须单独暴露**：`args.get("inputs") or {}` 会把
        # `{}`（宿主带了空对象）和 `None`（宿主**没带**这个键）压成同一个结果 ——
        # 于是"宿主总带 inputs"（协议 §3.7.7 / 21.10）这条契约就**没有观测量**了。
        # 这正是本项目反复踩的"静默失败"形状：断言看起来在守，其实恒绿。
        # ⇒ 用 `in` 判定**键是否存在**，把形态单独报出来。
        inputs = args.get("inputs")
        inputs_shape = "absent" if inputs is None else type(inputs).__name__
        return {
            "nodes": [
                {"type": "heading", "text": "input 节点验收"},
                # ① 合法：submitCommandId 指向已声明的命令
                {"type": "input", "key": "q", "value": "",
                 "placeholder": "输入关键字…", "submitCommandId": "paneltool.ping"},
                {"type": "text", "text": f"inputs 形态 = {inputs_shape}"},
                {"type": "text", "text": f"① q 收到 = {(inputs or {}).get('q', '')!r}"},
                # ② 合法但无 submitCommandId：Enter 不该触发任何命令
                {"type": "input", "key": "second", "placeholder": "无提交命令"},
                # ③ 非法：key 重复（后者应被忽略 + Warning）
                {"type": "input", "key": "q", "value": "重复的"},
                # ④ 非法：submitCommandId 指向未声明的命令（应 Warning，节点仍渲染）
                {"type": "input", "key": "badsubmit", "submitCommandId": "paneltool.nope"},
                # ⑤ 非法：缺 key（应被跳过 + Warning）
                {"type": "input", "value": "没有 key"},
            ]
        }

    if panel_id == "empty":
        return {"nodes": []}

    if panel_id == "weird":
        # 第 2 个节点是宿主不认识的类型：宿主应**跳过它但保住其余**
        return {
            "nodes": [
                {"type": "text", "text": "未知节点之前"},
                {"type": "__future_widget__", "text": "宿主不认识这个"},
                {"type": "text", "text": "未知节点之后"},
            ]
        }

    if panel_id == "broken":
        # 刻意返回非对象：验证宿主容错路径（显示错误提示而不是崩溃 / 退出非零）
        return ["这不是一个对象"]

    if panel_id == "image":
        # 图片节点（协议 §3.6）的**渲染层**验收面。这里要覆盖三条不同的宿主路径：
        #   ① 真图       → 画出一个 Image 控件（证明 image 节点没被当未知类型吃掉）
        #   ② 路径不存在 → 降级成 text 提示，但**其余节点照画**（面板不白屏）
        #   ③ 远程 URL   → 拒绝（S2：宿主绝不为工具发网络请求）
        # 三条合成一个面板，是为了让"跳过 vs 照画"的对比落在**同一个**控件树里 ——
        # 分开成三个面板的话，"其余节点照画"就无从断言（没有"其余"）。
        #
        # 路径怎么来：**环境变量**（工具进程继承宿主的环境，见 ToolProcess 的 psi.Environment）。
        # 为什么不让工具自己造一张图：造图需要一个 PNG 编码器，而这是个**探针** ——
        # 探针应该如实反映宿主拿到什么，不该自带"能把数据变好看"的能力。
        # 谁提供夹具、谁负责它的真实性：`verify-desktop.py` 生成真 PNG 后从环境传进来。
        # 没设变量时 `real` 为空串 ⇒ 走"路径不存在"分支，断言依然成立（只是少一个 Image）。
        real = os.environ.get("EZTOOLS_PANELTOOL_IMAGE", "")
        return {
            "nodes": [
                {"type": "text", "text": "图片渲染验收"},
                {"type": "text", "text": "① 真图（应画出一个 Image 控件）"},
                {"type": "image", "path": real, "alt": "真图", "maxHeight": 120},
                {"type": "text", "text": "② 路径不存在（应降级为提示，且不吞掉上下节点）"},
                {"type": "image", "path": real + ".definitely-missing", "alt": "缺失图"},
                {"type": "text", "text": "③ 远程地址（应被拒绝，宿主不发请求）"},
                {"type": "image", "path": "https://example.invalid/should-not-load.png",
                 "alt": "远程图"},
            ]
        }

    return {"nodes": [{"type": "text", "text": f"未知面板 {panel_id}"}]}


def _pid() -> int:
    return os.getpid()


if __name__ == "__main__":
    raise SystemExit(tool.run())
