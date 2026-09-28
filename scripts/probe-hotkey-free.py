#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ============================================================================
#  probe-hotkey-free.py —— 全局热键占用探测（环境诊断探针）
#
#  为什么需要它：`RegisterHotKey` 失败只有两种可能 —— 组合键被**本机别的进程**占用，
#  或同进程内重复注册。验收里表现为"热键注册 = 6 实际 5"，看起来像代码回归，
#  实际是环境冲突。实测案例（2026-09-28）：`Ctrl+Alt+W` 返回 GetLastError=1409
#  (ERROR_HOTKEY_ALREADY_REGISTERED)，而同组的 Ctrl+Alt+H/O/V/S 全部可注册
#  ⇒ 一眼区分"代码坏了"还是"键被占了"。
#
#  用法：
#    python -I -X utf8 -u scripts/probe-hotkey-free.py            # 探默认的 Eztools 热键集
#    python -I -X utf8 -u scripts/probe-hotkey-free.py --vk W,H,O # 只探指定字母
#    python -I -X utf8 -u scripts/probe-hotkey-free.py --json _scratch/hotkeys.json
#
#  退出码：0 = 全部可注册；1 = 有组合键被占用（打印占用者提示）；2 = 参数/环境错误
#
#  ⚠️ 探测用的是 `RegisterHotKey(hwnd=None, id, ...)` + **立刻 UnregisterHotKey**，
#     不会长期占键；但与正在运行的托盘实例共存时，Eztools 自己的键会被自己占住
#     ⇒ 跑之前先确认托盘没在跑（否则会把自己人当成"占用者"）。
# ============================================================================

import argparse
import ctypes
import json
import sys

MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_WIN = 0x0008
MOD_NOREPEAT = 0x4000

# Windows 的 RegisterHotKey 错误码（只列我们真会遇到的）
ERROR_HOTKEY_ALREADY_REGISTERED = 1409
ERR_TEXT = {
    1409: "ERROR_HOTKEY_ALREADY_REGISTERED —— 组合键已被**别的进程**注册（或本进程重复注册）",
    0: "",
}

# Eztools 当前声明的热键集（工具 3 个 + 宿主 3 个 + 验收会改成的那个）
DEFAULT_KEYS = [
    ("N", "wordcount 统计剪贴板文本（工具默认；2026-09-28 由 Ctrl+Alt+W 改成 N —— W 被占）"),
    ("H", "filehash 校验文件哈希（工具默认）"),
    ("P", "preview 速览（工具默认）"),
    ("S", "search 搜索文件（宿主直挂）"),
    ("O", "ocr 屏幕取字（宿主直挂）"),
    ("V", "clip 剪贴板历史（宿主直挂）"),
    ("K", "验收里被改成的新热键（临时）"),
    # ⚠️ 下面两个**尚未声明**：W6 视觉小波方案 §8 D2 已拍板要用它们。
    #    一并探测是为了让"开工前就发现键被占"，而不是等 W6 写完才在验收里红
    #    （2026-09-28 实测：两者在本机**都**不可注册）。
    ("X", "W6 规划：区域截图（capture.hotkey 默认值，未声明）"),
    ("C", "W6 规划：屏幕取色（pick.hotkey 默认值，未声明）"),
]


def probe(vk: int) -> tuple:
    """返回 (是否可注册, 错误码)。探测后立即释放。"""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    probe_id = 0x9A01                      # 任意非占用 id
    ok = user32.RegisterHotKey(None, probe_id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, vk)
    if ok:
        user32.UnregisterHotKey(None, probe_id)
        return True, 0
    return False, ctypes.get_last_error()


def main():
    ap = argparse.ArgumentParser(description="全局热键占用探测（Ctrl+Alt+<字母>）")
    ap.add_argument("--vk", default=None,
                    help="逗号分隔的字母，默认探 Eztools 热键集（W,H,P,S,O,V,K）")
    ap.add_argument("--json", default=None, help="把结果落盘为 JSON")
    args = ap.parse_args()

    if sys.platform != "win32":
        print("本探针仅适用于 Windows（RegisterHotKey）", file=sys.stderr)
        return 2

    if args.vk:
        keys = []
        for ch in args.vk.split(","):
            ch = ch.strip().upper()
            if len(ch) != 1:
                print(f"--vk 只接受单个字母：{ch!r}", file=sys.stderr)
                return 2
            keys.append((ch, "（命令行指定）"))
    else:
        keys = DEFAULT_KEYS

    print("=" * 68)
    print("  全局热键占用探测（组合键统一为 Ctrl+Alt+<字母>）")
    print("  提示：跑之前先确认 Eztools 托盘没在运行，否则会把自己人当占用者")
    print("=" * 68)

    results = []
    occupied = []
    for letter, desc in keys:
        vk = ord(letter)
        ok, err = probe(vk)
        results.append({"combo": f"Ctrl+Alt+{letter}", "letter": letter,
                        "vk": vk, "free": ok, "error": err, "desc": desc})
        if ok:
            print(f"  [ OK ] Ctrl+Alt+{letter:<2} 可注册    {desc}")
        else:
            occupied.append(f"Ctrl+Alt+{letter}")
            print(f"  [占用] Ctrl+Alt+{letter:<2} 不可注册  {desc}")
            print(f"         错误码 {err}：{ERR_TEXT.get(err, '未在本探针登记的错误码')}")

    print("-" * 68)
    if occupied:
        print(f" 结论：{len(occupied)} 个组合键被占用 —— {', '.join(occupied)}")
        print("   ① 先关掉可能抢占的软件（截图/录屏/游戏叠加层/远端控制类最常见）；")
        print("   ② 或在工具清单 / 设置里改该工具的默认热键；")
        print("   ③ 这是**环境冲突**，不是代码回归 —— 别去改 RegisterHotKey 调用。")
    else:
        print(f" 结论：{len(results)} 个组合键全部可注册 —— 热键相关的验收失败应从代码侧找原因")
    print("-" * 68)
    print(f"probe-hotkey-free: total={len(results)} occupied={len(occupied)}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"total": len(results), "occupied": occupied,
                       "results": results}, fh, ensure_ascii=False, indent=1)

    return 1 if occupied else 0


if __name__ == "__main__":
    sys.exit(main())
