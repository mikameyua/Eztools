# -*- coding: utf-8 -*-
"""托盘进程（Eztools.Desktop）的可重复验收。

为什么单独一个 Python 脚本而不是塞进 acceptance.sh：
托盘的验证要动三样 bash 很不顺手的东西 —— **剪贴板**（Win32 API）、
**通知区域注册表**（winreg）、**按进程名查内存**（tasklist）。用 Python 是自然选择。

为什么必须验证"托盘点击"这条链：
`menus` 声明的是命令，但**托盘点击没有上下文**。契约里不规定"参数从哪来"时，
菜单能显示、点击有反应、进程真的起来，却报"缺少参数" —— 一次都跑不成。
这条规则静态校验不了（handler 动态），只能真调一次。

**能自动化的都自动化了，剩下的明确标注**：
托盘是交互式的，鼠标真的点下去、气泡观感、图标 DPI 观感这三项无法自动断言
（见 docs/P2-托盘-实施方案.md §8）。但"图标真的显示"**可以** —— 查通知区域注册表。

退出码：0 = 全通过，1 = 有失败。父脚本据此计入总数。
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import subprocess
import sys
import time

PASS = FAIL = 0


def ck(name: str, cond: bool, detail: str = "") -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"[PASS] {name}")
    else:
        FAIL += 1
        print(f"[FAIL] {name}   {detail}")


def info(text: str) -> None:
    print(f"[信息] {text}")


def sh(cmd: list[str], **kw):
    """调外部命令。统一 errors='replace' —— tasklist/taskkill 的输出是系统 ANSI 代码页
    （中文 Windows 上是 GBK），按 UTF-8 解会抛 UnicodeDecodeError 并让 stdout 变成 None。"""
    kw.setdefault("capture_output", True)
    kw.setdefault("text", True)
    kw.setdefault("encoding", "gbk")
    kw.setdefault("errors", "replace")
    return subprocess.run(cmd, **kw)


# ── 剪贴板（Win32 API，不走 PowerShell —— 有些会话里它的输出通道不可靠）──────────
user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
kernel32.GlobalAlloc.restype = ctypes.c_void_p
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
user32.SetClipboardData.restype = ctypes.c_void_p
user32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002
user32.GetForegroundWindow.restype = ctypes.c_void_p
user32.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]


def set_clipboard(text: str) -> None:
    if not user32.OpenClipboard(None):
        raise OSError("OpenClipboard 失败（剪贴板被别的进程占用？）")
    try:
        user32.EmptyClipboard()
        data = text.encode("utf-16-le") + b"\x00\x00"
        handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
        ptr = kernel32.GlobalLock(handle)
        ctypes.memmove(ptr, data, len(data))
        kernel32.GlobalUnlock(handle)
        if not user32.SetClipboardData(CF_UNICODETEXT, handle):
            raise OSError("SetClipboardData 失败")
    finally:
        user32.CloseClipboard()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True, help="仓库根（Windows 形式路径）")
    args = parser.parse_args()

    repo = args.repo
    desktop = os.path.join(repo, "src", "Eztools.Desktop", "bin", "Debug", "net10.0-windows",
                           "Eztools.Desktop.exe")
    if not os.path.isfile(desktop):
        print(f"[FAIL] 未找到 {desktop}（先构建 Eztools.sln）")
        return 1

    env = dict(os.environ, DOTNET_ROOT=os.environ.get("DOTNET_ROOT", r"D:\dotnet10"))
    img = "Eztools.Desktop.exe"

    def run(exe_args, timeout=120):
        return subprocess.run([desktop, *exe_args], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", env=env, cwd=repo,
                              timeout=timeout)

    def pids():
        out = sh(["tasklist", "/FI", f"IMAGENAME eq {img}", "/FO", "CSV"]).stdout or ""
        found = []
        for line in out.splitlines()[1:]:
            cells = [c.strip('"') for c in line.split(",")]
            if len(cells) >= 2 and cells[1].isdigit():
                found.append(cells[1])
        return found

    def kill_all():
        sh(["taskkill", "/F", "/IM", img])
        for _ in range(20):
            if not pids():
                return True
            time.sleep(0.25)
        return False

    kill_all()

    # ── 1. 启动自检 ─────────────────────────────────────────────────────────
    out_file = os.path.join(repo, "_scratch", "desktop-selfcheck.txt")
    os.makedirs(os.path.dirname(out_file), exist_ok=True)
    if os.path.exists(out_file):
        os.remove(out_file)

    r = run(["--selfcheck", "--out", out_file])
    ck("自检退出码 0", r.returncode == 0, f"code={r.returncode} err={r.stderr[:150]}")

    lines = []
    if os.path.exists(out_file):
        with open(out_file, encoding="utf-8") as fh:
            lines = [ln for ln in (l.strip() for l in fh) if ln]
    line = lines[0] if lines else ""
    info(f"自检输出：{line}")
    m = re.search(r"工具 (\d+) 个 · 托盘项 (\d+) 个 · 热键注册 (\d+) 个 · 图标 (\d+)x(\d+)", line)
    ck("自检结果可读且格式正确", m is not None, repr(line[:150]))
    if m:
        ck("托盘项 = 3（与 Cli 侧一致 —— 同一个合成器；filehash/wordcount/preview）",
           m.group(2) == "3", m.group(2))
        ck("热键注册 = 3（RegisterHotKey 真实成功，仲裁无落选）", m.group(3) == "3", m.group(3))
        ck("图标按 SmallIconSize 取到 16x16", m.group(4) == "16" and m.group(5) == "16",
           f"{m.group(4)}x{m.group(5)}")

    # ── 1b. P1b 设置窗口：schema → 控件映射清单（自检附带输出）────────────────
    # 期望映射（§7）：boolean→CheckBox · string+enum→ComboBox · integer→TextBox
    # preview 起加入了第 5 个有 schema 的工具（3 个 integer 字段）—— 共 11 个字段
    expected = {
        "echo": {"uppercase=CheckBox"},
        "wordcount": {"countWhitespace=CheckBox", "language=ComboBox", "maxFileSizeMb=TextBox"},
        "filehash": {"algorithm=ComboBox", "uppercase=CheckBox", "chunkSizeKb=TextBox"},
        "pinfo": {"limit=TextBox"},
        "preview": {"maxTextBytes=TextBox", "maxLines=TextBox", "binaryProbeBytes=TextBox"},
    }
    got = {}
    for ln in lines[1:]:
        mm = re.match(r"设置清单 (\S+): (.+)$", ln)
        if mm:
            got[mm.group(1)] = set(x.strip() for x in mm.group(2).split(","))
    ck(f"设置清单覆盖全部 {len(expected)} 个有 schema 的工具",
       set(got) == set(expected), f"实际 {sorted(got)}")
    for tool in sorted(expected):
        ck(f"映射正确：{tool}（{len(expected[tool])} 个字段）",
           got.get(tool) == expected[tool], f"实际 {sorted(got.get(tool) or [])}")
    total = sum(len(v) for v in expected.values())
    ck(f"共渲染 {total} 个字段（boolean→CheckBox / enum→ComboBox / integer→TextBox）",
       all(got.get(t) == expected[t] for t in expected) and len(got) == len(expected),
       str(got))

    # ── 1c. P4 Wave 2c 面板渲染：协议通 ≠ WPF 真的画出来了 ────────────────────
    #     为什么这些断言必须有：`_step15_w2c.sh` 验的是**数据链路**（CLI 出口），
    #     但"WPF 把这堆节点真画成控件树"是另一件事 —— CLI 全绿而渲染层把 type 吃掉、
    #     或者按钮回调没绑上，数据侧一个断言都发现不了。
    #     本脚本走 --selfcheck（构造控件树但**不显示窗口**），于是渲染也进了自动化。
    panel_controls = {}
    panel_buttons = {}
    panel_meta = {}
    for ln in lines[1:]:
        m1 = re.match(r"面板清单 (\S+): 尺寸 (\d+)x(\d+) · 刷新 (\S+)$", ln)
        if m1:
            panel_meta[m1.group(1)] = (int(m1.group(2)), int(m1.group(3)), m1.group(4))
            continue
        m2 = re.match(r"面板控件 (\S+): (.+)$", ln)
        if m2:
            panel_controls[m2.group(1)] = {
                k: int(v) for k, v in (x.split("=") for x in m2.group(2).split(", "))
            }
            continue
        m3 = re.match(r"面板按钮 (\S+): (.*)$", ln)
        if m3:
            panel_buttons[m3.group(1)] = [x for x in m3.group(2).split(", ") if x]

    # 主面板：6 种节点全渲染 ⇒ 2 个按钮 + 1 条分隔线 + 1 个 kv 网格 + 1 个按钮排
    main = panel_controls.get("paneltool.main", {})
    ck("面板 main 渲染出控件树（协议 → WPF 真的落成控件）", bool(main), str(main))
    ck("面板 main 含 2 个按钮（buttons 节点的 2 项）", main.get("Button") == 2, str(main))
    ck("面板 main 含 1 条分隔线（separator 节点）", main.get("Separator") == 1, str(main))
    ck("面板 main 含 1 个两列网格（kv 节点）", main.get("Grid") == 1, str(main))
    ck("面板 main 含 1 个按钮排（WrapPanel）", main.get("WrapPanel") == 1, str(main))
    ck("面板 main 的按钮 commandId 来自工具的 buttons 节点",
       panel_buttons.get("paneltool.main") == ["paneltool.ping", "paneltool.ping"],
       str(panel_buttons.get("paneltool.main")))

    # 尺寸取自清单声明（不是宿主默认值）—— 反例：宿主忽略 width/height 用 640x480 就失败
    ck("面板 main 尺寸取自清单声明（720x520，非默认 640x480）",
       panel_meta.get("paneltool.main", (0, 0, ""))[:2] == (720, 520),
       str(panel_meta.get("paneltool.main")))

    # empty：空 nodes 必须渲染成"无内容"提示，而不是空白窗口
    ck("面板 empty（空 nodes）渲染出提示而非空白",
       panel_controls.get("paneltool.empty", {}).get("TextBlock", 0) >= 1,
       str(panel_controls.get("paneltool.empty")))

    # weird：未知 type 跳过 + 提示，其余节点照画（3 个 TextBlock = 2 个 text + 1 条提示）
    # 反例：实现成"遇到未知类型整包失败"→ TextBlock 只有 1 个（错误提示），这条即失败
    ck("面板 weird 跳过未知节点但保住其余（3 个文本块）",
       panel_controls.get("paneltool.weird", {}).get("TextBlock") == 3,
       str(panel_controls.get("paneltool.weird")))

    # broken：非对象载荷 → 提示（不崩、不白屏）；退出码已在上面断言为 0
    ck("面板 broken（工具返回非对象）渲染出错误提示而非崩溃",
       panel_controls.get("paneltool.broken", {}).get("TextBlock", 0) >= 2,
       str(panel_controls.get("paneltool.broken")))

    # ── 2. ★ 核心链路：托盘进程 → 宿主 → 工具进程（真实剪贴板驱动）───────────
    readme = os.path.join(repo, "README.md").replace("\\", "/")
    set_clipboard(readme)
    r = run(["--click", "0"])           # 第 0 项 = filehash.hashMany（input=clipboard）
    ck("托盘项【批量计算文件哈希】零上下文可跑（剪贴板=路径）", r.returncode == 0,
       f"code={r.returncode}")

    set_clipboard("托盘链路验证 hello world")
    r = run(["--click", "1"])           # 第 1 项 = wordcount.count
    ck("托盘项【统计文本】零上下文可跑（剪贴板=文本）", r.returncode == 0, f"code={r.returncode}")

    set_clipboard("   ")                # 反向：空内容必须失败，否则上面两条可能是恒真
    r = run(["--click", "0"])
    ck("反向：空剪贴板确实失败（说明上一条断言有效）", r.returncode != 0, f"code={r.returncode}")
    set_clipboard("")

    # ── 2b. ★ 热键路径的上下文注入（--click 到不了的那条路）───────────────────
    #     为什么必须有：_step11.sh 验的是热键**仲裁**（静态冲突 / wins / blocked-by），
    #     验的是"键归谁"；而"热键真的触发时往 args 里注入了什么"此前一条断言都没有。
    #     热键项在 RegisterHotkeysFromRegistry 里单独构造，Input 与托盘项**不同源**
    #     （历史上是硬编码 Clipboard），所以 --click 全绿也可能热键侧已静默丢掉
    #     剪贴板 —— 症状是某个热键"按了没反应"，而没有任何测试会红。
    #     这里钉住两种真实形态（将来把热键 Input 改成"按清单声明注入"时，这两条就是护栏）：
    #       · filehash.hash    —— 有热键、**没有任何 menus 项**（Input 只能靠回落默认值）
    #       · wordcount.count  —— 有热键、且 menus 里明确声明了 input: clipboard
    set_clipboard(readme)
    r = run(["--probe-hotkey", "filehash.hash"])
    ck("热键项【文件哈希】注入剪贴板路径（有热键、无 menus，靠回落默认值）",
       r.returncode == 0, f"code={r.returncode}")

    set_clipboard("热键注入验证 hello world")
    r = run(["--probe-hotkey", "wordcount.count"])
    ck("热键项【统计文本】注入剪贴板文本（有热键、menus 也声明了 input: clipboard）",
       r.returncode == 0, f"code={r.returncode}")

    set_clipboard("")                    # 反向：hash 无路径可用，必须失败
    r = run(["--probe-hotkey", "filehash.hash"])
    ck("反向：空剪贴板下热键项确实失败（说明上面两条不是恒真）",
       r.returncode != 0, f"code={r.returncode}")

    r = run(["--probe-hotkey", "echo.echo"])   # echo 未声明任何热键
    ck("未声明热键的命令走该探针 → 明确非零（不静默成功）",
       r.returncode != 0, f"code={r.returncode}")

    # ── 2c. ★ shellSelection 注入（速览的上下文来源，剪贴板断言覆盖不到）──────────
    #     观测量 = preview.show 的返回值（recorded / paths，经探针 --out 落盘）——
    #     只看退出码分不清"拿到 1 项"和"什么都没拿到"。
    #     确定性三要素（实测教训）：
    #       ① 预清理：关掉此前遗留的、选中项为测试目标的窗口（用 IWebBrowser2.Quit()，
    #          🔴 绝不能 taskkill explorer.exe —— 那会连任务栏一起杀）；
    #       ② 开窗后**轮询等待**前台真的变成 Explorer 文件夹窗口（CabinetWClass），
    #          固定 sleep 会被冷启动抖动打败；
    #       ③ 反向断言放宽为"结果不含目标"—— 此时前台可能是用户自己的其它窗口，
    #          它的选中项不该被我们污染。
    probe_out = os.path.join(repo, "_scratch", "probe-hotkey.json")
    target = os.path.join(repo, "README.md")
    ps_quit_target = (
        "$sh = New-Object -ComObject Shell.Application;"
        "foreach($w in $sh.Windows()){"
        "try{$s=$w.Document.SelectedItems();"
        "for($k=0;$k -lt $s.Count;$k++){"
        f"if($s.Item($k).Path -eq '{target}'){{ $w.Quit(); break }}"
        "}}catch{}}"
    )

    def close_windows_with_target() -> None:
        subprocess.run(["powershell", "-NoProfile", "-Command", ps_quit_target],
                       capture_output=True, timeout=60)
        time.sleep(1.5)

    def dump_shell_windows() -> str:
        r = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             os.path.join(repo, "scripts", "_dump-shell-windows.ps1")],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        return (r.stdout or "").strip() or "(无输出)"

    try:
        if os.path.exists(probe_out):
            os.remove(probe_out)
        close_windows_with_target()

        # 开窗 + 轮询等待"前台窗口的选中项包含目标"—— 判据直接对齐产品需要的状态。
        # 不能只等"前台是 CabinetWClass"：已在前台的其它 Explorer 窗口（Home/此电脑）
        # 会提前满足，探针读到的就是别人的选中项（实测踩到）。
        # 🔴 这两条断言是**环境依赖**的：桌面正被人使用时前台会漂移，等待可能超时 ——
        #    超时报【跳过】并附现场状态（诚实暴露，不假红也不假绿）；
        #    机制本身由 2b 的热键断言与"反向"断言共同守。
        w = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", os.path.join(repo, "scripts", "_wait-selection.ps1"),
             "-Target", target],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        first = (w.stdout or "").strip().splitlines()
        opened = bool(first) and first[0].startswith("OK")

        if not opened:
            info("跳过两条选中项断言：20 秒内前台未出现目标选中状态（桌面正被使用？）。现场：")
            for ln in dump_shell_windows().splitlines():
                info("  " + ln)
        else:
            r = run(["--probe-hotkey", "preview.show", "--out", probe_out])
            ck("热键项【速览】触发成功（input=shellSelection）", r.returncode == 0,
               f"code={r.returncode} err={r.stderr[:120]}")

            got = {}
            if os.path.exists(probe_out):
                with open(probe_out, encoding="utf-8") as fh:
                    got = json.load(fh)
            got_paths = got.get("paths") or []
            ck("工具真的收到了选中的文件（recorded=1 且路径命中）",
               got.get("recorded") == 1 and len(got_paths) >= 1
               and any(os.path.normcase(str(p)) == os.path.normcase(target) for p in got_paths),
               f"got={got}")

            # 反向：关掉我们的窗口后再探 —— 结果不得再含目标
            #（验证"读的是真实前台状态"，同时证明上一条不是恒真）
            close_windows_with_target()
            if os.path.exists(probe_out):
                os.remove(probe_out)
            r = run(["--probe-hotkey", "preview.show", "--out", probe_out])
            empty = {}
            if os.path.exists(probe_out):
                with open(probe_out, encoding="utf-8") as fh:
                    empty = json.load(fh)
            empty_paths = [os.path.normcase(str(p)) for p in (empty.get("paths") or [])]
            ck("反向：关闭窗口后结果不再含目标（读的是真实前台状态，不残留）",
               r.returncode == 0 and os.path.normcase(target) not in empty_paths, f"got={empty}")
    finally:
        if os.path.exists(probe_out):
            os.remove(probe_out)

    # ── 3. 单实例 ───────────────────────────────────────────────────────────
    subprocess.Popen([desktop, "--no-prompt"], env=env, cwd=repo,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(4)
    ck("托盘常驻进程存活", len(pids()) >= 1, f"pids={pids()}")

    r = run(["--no-prompt"])
    ck("第二个实例退出码 3（拒绝重复启动）", r.returncode == 3, f"code={r.returncode}")
    ck("已在运行的实例不受影响", len(pids()) >= 1, f"pids={pids()}")

    # ── 4. 图标是否真的登记进通知区域 ───────────────────────────────────────
    #    explorer 会为每个出现过的托盘图标在 HKCU\Control Panel\NotifyIconSettings 建一条记录。
    #    这比"构造 NotifyIcon 没抛异常"硬得多 —— 那只能证明代码跑过，证明不了图标看得见。
    found = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Control Panel\NotifyIconSettings") as root:
            i = 0
            while True:
                try:
                    sub = winreg.EnumKey(root, i)
                except OSError:
                    break
                i += 1
                try:
                    with winreg.OpenKey(root, sub) as sk:
                        if "Eztools.Desktop" in str(winreg.QueryValueEx(sk, "ExecutablePath")[0]):
                            found.append(sub)
                except OSError:
                    continue
    except FileNotFoundError:
        info("NotifyIconSettings 键不存在（更早的 Windows 版本），跳过")
    ck("托盘图标已登记进通知区域", len(found) > 0, "未找到 —— 图标可能没真正显示")

    # ── 5. 常驻内存与退出清理 ───────────────────────────────────────────────
    out = sh(["tasklist", "/FI", f"IMAGENAME eq {img}"]).stdout or ""
    total = 0
    for line in out.splitlines():
        if img not in line:
            continue
        cells = line.split()
        if len(cells) >= 2:
            digits = re.sub(r"[^\d]", "", cells[-2])
            if digits:
                total += int(digits)
    if total:
        info(f"托盘常驻工作集 ≈ {total / 1024:.1f} MB（§15 预算上限 100 MB）")
    ck("退出后无残留进程", kill_all(), f"仍有 {pids()}")

    print(f"[信息] 托盘验收：通过 {PASS} / 失败 {FAIL}")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
