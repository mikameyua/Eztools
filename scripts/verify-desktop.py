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

# 独立运行时防 GBK 控制台：断言名里有 ⇒/⚠ 等 GBK 编不了的字符（2026-09-24 实测
# 用户终端 UnicodeEncodeError 崩溃）。验收脚本侧另有 PYTHONUTF8=1 兜底，这里保单跑。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

PASS = FAIL = 0
# 环境依赖分支被跳过时递增 —— 让"总断言数会漂移"这件事在日志里可见（见 §2c）。
# 不加这一项时，跳过表现为"通过数莫名从 222 变 219"，无任何信号（S9 复发形态）。
SKIPPED = 0


def ck(name: str, cond: bool, detail: str = "") -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"[PASS] {name}")
    else:
        FAIL += 1
        print(f"[FAIL] {name}   {detail}")


def write_tiny_png(path: str, width: int = 8, height: int = 8) -> str:
    """写一张**结构合法、WPF 真能解码**的最小 PNG（协议 §3.6 的 image 节点夹具）。

    为什么不能"以 PNG 魔数开头的字节"了事：`image` 节点的验收要穿过两层 ——
    速览侧只看 magic（那种字节够用），而**宿主侧会真解码**。
    半截字节过不了 `BitmapImage`，会走 §3.6.3 的降级分支，
    于是"Image 控件计数 == 1"会因夹具而红 —— 那是夹具的错，不是代码的错。
    实测过：`\x89PNG...IHDR` + 若干 0 字节 ⇒ `FileFormatException: 图像格式无法识别`。

    CRC 与 zlib 流都必须正确（WPF 校验 CRC），所以不能手拼。
    """
    import struct
    import zlib

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    row = b"".join(bytes([(c * 32) % 256, 64, 200]) for c in range(width))
    raw = b"".join(b"\x00" + row for _ in range(height))
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
                 + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
    return path


def info(text: str) -> None:
    print(f"[信息] {text}")


# ── 气泡摘要（result.hint）──────── 轻量 shim，避免为 5 行逻辑拉起整个 .NET 托盘 ──────────
#
# 为什么是"照抄规则"而不是"调真实代码"：TrayApplication.DescribeResult / Shorten 都是
# private static，宿主进程又不接受"给我看一眼你会显示什么"这种请求（连气泡本身都
# 可能被 Win11 勿扰静默丢弃，所以也不能靠观感判断）。这两条规则短且稳定，
# 照抄的漂移风险由 §2d 第 ⑥ 条断言守：工具侧与托盘侧的键名必须同时在场。
# 🔴 改 TrayApplication.DescribeResult 时必须同步这里 —— 否则断言会继续绿着骗人。
class _TrayDesc:
    """复刻 TrayApplication.#DescribeResult + #Shorten 的行为（口径见其 XML 注释）。"""

    SEP = "…"

    def shorten(self, text: str, max_len: int = 240) -> str:
        flat = text.replace("\r", " ").replace("\n", " ").strip()
        return flat if len(flat) <= max_len else flat[:max_len] + self.SEP

    def describe(self, result) -> str:
        if result is None:
            return "（无返回值）"

        # 与 C# 侧同一条护栏：hint 必须是 JSON 字符串，且去空白后非空
        if isinstance(result, dict):
            hint = result.get("hint")
            if isinstance(hint, str) and hint.strip():
                return hint

        return json.dumps(result, ensure_ascii=False, separators=(",", ":"))


def _touch_tray_desc_module() -> _TrayDesc:
    return _TrayDesc()


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
    desktop = os.path.join(repo, "src", "Eztools.Desktop", "bin", "Debug",
                           "net10.0-windows10.0.19041.0",
                           "Eztools.Desktop.exe")
    if not os.path.isfile(desktop):
        print(f"[FAIL] 未找到 {desktop}（先构建 Eztools.sln）")
        return 1

    env = dict(os.environ, DOTNET_ROOT=os.environ.get("DOTNET_ROOT", r"D:\dotnet10"))
    img = "Eztools.Desktop.exe"

    # ★★ 必须把工具源钉到**仓库里**，否则本机 `%LOCALAPPDATA%\Eztools\tools\` 的
    #    陈旧安装副本会**遮蔽**仓库源（工具发现按 id 去重，保留**先**出现的源）。
    #
    #    这是本轮实测撞到的一个"静默错"（属于 S3 那一类"真实依赖被藏起来"）：
    #    paneltool 新增了 `image` 面板后，自检报告里**完全没有这一条** ——
    #    不是渲染失败（那会有一行"自检失败"），而是**工具根本是另一个版本**。
    #    症状极隐蔽：其余 4 个面板的名字一模一样，所以此前一直全绿。
    #
    #    为什么以前没暴露：`--install-root` 只影响诊断噪音（见项目记忆），
    #    而 `verify-desktop.py` 从头到尾**没有传过任何工具源参数**，
    #    一直是"cwd 恰好有 tools/"这个隐式前提在撑着（正是 §四.12 记的那条坑）。
    #    显式传 `--tools-dir <repo>/tools` 后，被测的就一定是仓库当前源码。
    tools_dir = os.path.join(repo, "tools").replace("\\", "/")
    install_root = os.path.join(repo, "_scratch", "accept", "install").replace("\\", "/")
    config_root = os.path.join(repo, "_scratch", "accept", "config").replace("\\", "/")
    selfcheck_args = ["--tools-dir", tools_dir,
                      "--install-root", install_root, "--config-root", config_root]

    # ── image 节点（协议 §3.6）的渲染夹具 ────────────────────────────────────
    # 为什么由**本脚本**生成一张真 PNG 再喂给 paneltool：`image` 节点的验收面
    # 分裂在两个进程里 —— 工具侧只判 magic（`verify-preview.py` 19.1~19.6），
    # 而"WPF 真的把字节解成位图、画出一个 Image 控件"是**宿主侧**的事。
    # 要断言后者，就必须有一张**真能被解码**的图；半截字节会走降级分支，
    # 于是"Image 计数 == 1"会因为夹具而不是代码而红。
    # 生成代码与 `verify-preview.py` 的 `make_png` 保持一致（同一份 PNG 结构）。
    img_dir = os.path.join(repo, "_scratch", "desktop-img")
    os.makedirs(img_dir, exist_ok=True)
    real_png = os.path.join(img_dir, "panel-real.png")
    write_tiny_png(real_png, 8, 8)
    # 用环境变量把路径交给 paneltool（工具进程继承宿主环境，见 ToolProcess.psi.Environment）
    env["EZTOOLS_PANELTOOL_IMAGE"] = real_png

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

    # ★ 传 selfcheck_args：把工具源钉到仓库（见上面 tools_dir 那段注释 ——
    #   不传的话本机的陈旧安装副本会遮蔽仓库源，新面板会**整条消失**且不报错）。
    r = run(["--selfcheck", "--out", out_file, *selfcheck_args])
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
        ck("托盘项 = 3（与 Cli 侧一致 —— 同一个合成器；filehash/wordcount/preview；"
           "「搜索文件」是宿主直挂项，不进合成器模型）",
           m.group(2) == "3", m.group(2))
        ck("热键注册 = 6（3 个工具热键 + 搜索 W3-d-1 + OCR W4-c + 剪贴板 W5-c，RegisterHotKey 真实成功）",
           m.group(3) == "6", m.group(3))
        ck("图标按 SmallIconSize 取到 16x16", m.group(4) == "16" and m.group(5) == "16",
           f"{m.group(4)}x{m.group(5)}")

    # ★ 搜索热键（W3-d-1）：注册结局单列一行 —— 被第三方占用时的提示文案是断言面。
    #   判据落"具体组合键 + 箭头"（注册成功）或"未注册"（显式降级），恒定值/空行都算失败。
    search_lines = [ln for ln in lines if ln.startswith("搜索热键")]
    ck("自检报告了搜索热键的注册结局（W3-d-1）", len(search_lines) == 1, f"匹配 {len(search_lines)} 行")
    if search_lines:
        ok_line = "→ 搜索窗（已注册）" in search_lines[0] or "未注册" in search_lines[0]
        ck("搜索热键行含具体结局（已注册的组合键或显式降级说明）", ok_line, search_lines[0])

    # ★ OCR 热键 + 语言（W4-c）：与搜索热键同款断言面。
    #   语言行判"生效值"而非"配置文件写了什么" —— 合成链（命令行 > desktop.json > 默认）
    #   任何一环断了，这里打出来的值就不再是配置文件里的那个。
    ocr_lines = [ln for ln in lines if ln.startswith("OCR 热键")]
    ck("自检报告了 OCR 热键的注册结局（W4-c）", len(ocr_lines) == 1, f"匹配 {len(ocr_lines)} 行")
    if ocr_lines:
        ok_line = "→ 屏幕取字（已注册）" in ocr_lines[0] or "未注册" in ocr_lines[0]
        ck("OCR 热键行含具体结局（已注册的组合键或显式降级说明）", ok_line, ocr_lines[0])

    ocr_lang_lines = [ln for ln in lines if ln.startswith("OCR 语言：")]
    ck("自检报告了 OCR 语言的生效值（W4-c）", len(ocr_lang_lines) == 1, f"匹配 {len(ocr_lang_lines)} 行")
    if ocr_lang_lines:
        ok_line = "auto" in ocr_lang_lines[0] or "→" in ocr_lang_lines[0] or ":" in ocr_lang_lines[0]
        ck("OCR 语言行含具体值（auto 或 BCP-47 标签）", ok_line, ocr_lang_lines[0])

    # ★ 剪贴板热键 + 监听状态（W5-c）：两条链路的断言面分开 ——
    #   "面板能唤出"（热键行）与"复制真的进历史"（监听行，含上限/黑名单/暂停态生效值）。
    clip_lines = [ln for ln in lines if ln.startswith("剪贴板热键")]
    ck("自检报告了剪贴板热键的注册结局（W5-c）", len(clip_lines) == 1, f"匹配 {len(clip_lines)} 行")
    if clip_lines:
        ok_line = "→ 剪贴板历史面板（已注册）" in clip_lines[0] or "未注册" in clip_lines[0]
        ck("剪贴板热键行含具体结局（已注册的组合键或显式降级说明）", ok_line, clip_lines[0])

    monitor_lines = [ln for ln in lines if ln.startswith("剪贴板监听")]
    ck("自检报告了剪贴板监听的运行态（W5-c）", len(monitor_lines) == 1, f"匹配 {len(monitor_lines)} 行")
    if monitor_lines:
        ok_line = "运行中" in monitor_lines[0] or "已停用" in monitor_lines[0] or "未运行" in monitor_lines[0]
        ck("剪贴板监听行含具体运行态（运行中/已停用/未运行）", ok_line, monitor_lines[0])
        ck("★ 剪贴板监听运行中且上限/黑名单生效值已合成",
           "运行中" in monitor_lines[0] and "上限 1000 条" in monitor_lines[0],
           monitor_lines[0])

    # ★ 热键 → 面板的绑定必须出现在自检输出里（2026-09-21）。
    #   守的是"清单字段 → 仲裁带回 → 触发后开面板"这条**全在宿主内部**的链路：
    #   它断开时外面只看得到"按了键没反应"，退出码 0、零诊断。
    #   判据要求**具体的绑定对**而不只是"有一行" —— 后者在绑定全丢时也成立。
    bind_lines = [ln for ln in lines if ln.startswith("热键直达面板")]
    ck("自检报告了热键→面板的绑定关系", len(bind_lines) == 1, f"匹配 {len(bind_lines)} 行")
    if bind_lines:
        info(f"绑定：{bind_lines[0]}")
        ck("★ 绑定含具体的「命令→面板」对（preview.show→main）",
           "preview.show→main" in bind_lines[0], bind_lines[0])

    # ── 1b. P1b 设置窗口：schema → 控件映射清单（自检附带输出）────────────────
    # 期望映射（§7）：boolean→CheckBox · string+enum→ComboBox · integer→TextBox
    # preview 起加入了第 5 个有 schema 的工具（3 个 integer 字段）—— 共 11 个字段
    # W4-c：宿主设置节 desktop（search.hotkey / ocr.hotkey / ocr.language，3 个 string 字段）
    # 也以同格式进清单 —— 宿主设置接 P1a 的落地证据。
    expected = {
        "desktop": {"search.hotkey=TextBox", "ocr.hotkey=TextBox", "ocr.language=TextBox",
                     "clip.enabled=CheckBox", "clip.hotkey=TextBox", "clip.max-items=TextBox",
                     "clip.image-retention-days=TextBox", "clip.blacklist=TextBox"},
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
    panel_notices = {}
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
            continue
        m4 = re.match(r"面板提示 (\S+): (.*)$", ln)
        if m4:
            panel_notices[m4.group(1)] = [x for x in m4.group(2).split(" || ") if x]

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

    # ── 1c-2. image 节点（协议 §3.6）——**渲染层**的验收 ─────────────────────
    #     为什么这组必须在这里（而不是只在 verify-preview.py）：
    #     `verify-preview.py` 验的是**工具侧判定**（按 magic 认出图片）；
    #     而"WPF 真的把字节解成位图、画出一个 Image 控件"是**宿主侧**的事，
    #     两者之间隔着一条 `path` 契约（工具给路径、宿主读盘）。
    #     只验工具侧的话，"节点发出去了但渲染层把 image 当未知类型跳过"永远不红。
    #     paneltool.image 一个面板同时含三条路径：真图 / 不存在 / 远程。
    imgctl = panel_controls.get("paneltool.image", {})
    ck("★ image 节点渲染出 Image 控件（协议 §3.6 真的落到 WPF）",
       imgctl.get("Image") == 1, str(imgctl))

    # 三条路径的文本块计数（**逐条列清，别心算** —— 第一版就是心算错的，写成了 5）：
    #   说明 text 4 条：标题「图片渲染验收」+ 「① 真图…」+ 「② 路径不存在…」+ 「③ 远程地址…」
    #   降级 notice 2 条：路径不存在的提示 + 远程被拒的提示
    #   ⇒ 合计 6。真图那条**不产生** TextBlock —— 它落成一个 Image 控件。
    # 判据落**具体数字**而不是 ">=1"：后者在"三条 image 全被跳过"时照样过（弱断言）。
    ck("★ image 的三条路径各自给出正确结果（1 真图 + 2 条降级提示，无一被吞）",
       imgctl.get("Image") == 1 and imgctl.get("TextBlock") == 6,
       f"Image={imgctl.get('Image')} TextBlock={imgctl.get('TextBlock')}（期望 1 / 6）")

    # 依赖有效性前提：没有真图 fixture 时上面那条会退化成"0 个 Image"，
    # 那时应当**明确失败**而不是静默降级成另一条断言 —— 否则环境坏了也全绿。
    ck("image 夹具有效性前提：真 PNG 已生成且非空",
       os.path.isfile(real_png) and os.path.getsize(real_png) > 50,
       f"{real_png} size={os.path.getsize(real_png) if os.path.isfile(real_png) else 'N/A'}")

    # ★★ S2 安全边界：远程地址必须**被判成"不支持远程"**，而不是"恰好打不开"。
    #     为什么这条必须看**文案**（而不是只看"渲染出提示了"）：
    #     两种实现的控件树长得一模一样（都是 1 个 TextBlock）——
    #     ① 正确实现：识别出远程 ⇒ 主动拒绝，文案里说"不支持远程地址"
    #     ② 错误实现：没识别、直接当文件路径打开 ⇒ 也失败 ⇒ 文案说"文件不存在"
    #     只看"有提示"的话两者都过，而 ② 恰恰是**S2 失守**的形态：
    #     一旦未来有人把路径加载从 FileStream 换成 UriSource，它就变成真的发起网络请求。
    #     实测验证过：删掉 LooksRemote 守卫后，本断言失败（面板直接崩，见 _scratch/mut_img）。
    notices = panel_notices.get("paneltool.image", [])
    remote_rejected = [n for n in notices if "不支持远程地址" in n]
    missing_reported = [n for n in notices if "文件不存在" in n]
    ck("★★ 反向：远程地址被**明确拒绝**（S2：宿主不为工具发网络请求）",
       len(remote_rejected) == 1,
       f"提示={notices!r}")
    ck("★ 路径不存在 → 降级文案说「文件不存在」（与远程拒绝是**两条不同**的提示）",
       len(missing_reported) == 1,
       f"提示={notices!r}")

    # ── 1c-3. input 节点（协议 §3.7，V1.3）——WPF 侧的验收 ─────────────────────
    #     与 image 同理：verify-preview.py 验的是 CLI/契约层（21.1/21.8/21.9/21.10），
    #     节流 21.7 在 selftest（假时钟）；"WPF 真的渲染出输入框、值随请求回传、
    #     失焦清空"只有这里能验。自检编排：打字（真实 TextChanged 路径）→ 带快照二拉
    #     → 重绘 → 失焦（TrayApplication.PanelInventoryForSelfCheck 编排，
    #     PanelWindow 提供走生产路径的自检钩子）。
    panel_input = {}
    panel_input_echo = {}
    panel_input_blur = {}
    panel_input_readd = {}
    for ln in lines[1:]:
        m = re.match(r"面板输入 (\S+): 键 (\S+) ← \"(.*?)\" · 重绘后框 (.+) · 快照 (.+)$", ln)
        if m:
            panel_input[m.group(1)] = (m.group(2), m.group(3), m.group(4), m.group(5))
            continue
        m = re.match(r"面板输入回显 (\S+): (.*)$", ln)
        if m:
            panel_input_echo[m.group(1)] = m.group(2)
            continue
        m = re.match(r"面板输入失焦 (\S+): 快照 (.+?) · 框 (.+?) · 待发 (.+)$", ln)
        if m:
            panel_input_blur[m.group(1)] = (m.group(2), m.group(3), m.group(4))
            continue
        m = re.match(r"面板输入重入 (\S+): (.+)$", ln)
        if m:
            panel_input_readd[m.group(1)] = m.group(2)
            continue

    typed = "Eztools 自检输入"
    inp_keys = set(panel_input) | set(panel_input_echo) | set(panel_input_blur) | set(panel_input_readd)
    ck("自检编排覆盖了含 input 的面板（paneltool.input 三行输入报告齐全）",
       inp_keys == {"paneltool.input"}, f"实际 {sorted(inp_keys)}")

    # 21.2：input 渲染成真 TextBox。本夹具可渲染 3 个：2 个合法 + 1 个 submit 未声明
    # （照渲染 + 警告，§3.7.6）；缺 key 的那个被跳过 ⇒ TextBox=3 而不是 2 或 4。
    inp_ctl = panel_controls.get("paneltool.input", {})
    ck("★ 21.2 input 节点渲染成真 TextBox（3 个可渲染节点：2 合法 + 1 未声明 submit 照渲染）",
       inp_ctl.get("TextBox") == 3, str(inp_ctl))

    # 21.6：重绘不吞字。判据落两处：**控件里的字**（重绘按首现规则忽略节点 value=""）
    # 与**宿主快照**（下一次请求将携带的值）。只断快照的话，"控件被抹了但缓存还在"
    # 的实现也能过 —— 用户看到的可是控件。
    if "paneltool.input" in panel_input:
        _, _, boxes_json, snap_json = panel_input["paneltool.input"]
        boxes_after = json.loads(boxes_json)
        snap_after = json.loads(snap_json)
        ck("★ 21.6 整体重绘不吞字：重绘后框里仍是用户打的值（节点 value='' 未生效）",
           boxes_after.get("q") == typed, str(boxes_after))
        ck("★ 21.6 快照同步：下一次请求将携带的 inputs.q == 用户输入",
           snap_after.get("q") == typed, str(snap_after))

    # 21.6b（2026-09-24 复盘加固）：payload 定义"存在"（§3.7.2）—— 工具把 input 拿掉
    # 再放回 ⇒ 该 key 视为**重新首现**，节点 value 重新生效（值复位）。判据落控件文本：
    # 若实现漏了"成功渲染路径裁剪缓存"，放回后缓存仍持旧值 ⇒ 首现规则把它挡住，
    # 框里是旧输入而非节点 value —— 断言即红（突变 W-M5 验证）。
    readd_raw = panel_input_readd.get("paneltool.input", "")
    ck("★ 21.6b input 拿掉再放回 ⇒ key 视为首现、值复位（节点 value 重新生效）",
       readd_raw != "" and json.loads(readd_raw).get("q") == "Eztools 重入值", readd_raw[:200])

    echo = panel_input_echo.get("paneltool.input", "")
    ck("★ 21.3 回传路径：宿主快照随请求送达，工具原样回显 inputs.q",
       f"q 收到 = '{typed}'" in echo, echo[:220])
    ck("21.3 形态前提：请求带 inputs 字段且为对象（工具观测 inputs 形态 = dict）",
       "inputs 形态 = dict" in echo, echo[:220])

    # 21.9（WPF 侧）：input 不改变其余节点的渲染 —— 标题 heading 与 input 之后的
    # text 节点照画；三条降级警告与 CLI 同口径（重复 key / 未声明 submit / 缺 key）。
    ck("★ 21.9 WPF 侧：input 前后的节点照画（heading + 回显 text 都在）",
       "input 节点验收" in echo and "inputs 形态 = dict" in echo, echo[:220])
    inp_notices = panel_notices.get("paneltool.input", [])
    ck("21.8/降级口径 WPF 与 CLI 一致（重复 key / 未声明 submit / 缺 key 三条警告）",
       sum(("key 重复" in n) + ("submitCommandId 未在" in n) + ("缺少 key" in n) for n in inp_notices) == 3,
       str(inp_notices))

    # 21.4/21.5：失焦三清（快照 / 控件 / 待发）—— F1/F2 的守卫，本节最重要的负向断言。
    if "paneltool.input" in panel_input_blur:
        blur_snap, blur_boxes, blur_pending = panel_input_blur["paneltool.input"]
        ck("★★ 21.4 失焦 ⇒ inputs 快照清空（F1：后台不回传）",
           json.loads(blur_snap) == {}, blur_snap)
        ck("★★ 21.5 失焦 ⇒ 旧值不复活：控件同步清空（F2）",
           json.loads(blur_boxes) == {"q": "", "second": "", "badsubmit": ""}, blur_boxes)
        ck("21.5 失焦 ⇒ 节流待发一并清空（排队中的值不得在失焦后交付）",
           blur_pending == "False", blur_pending)

    # ── 1c-4. 搜索窗渲染面（W3-d-2/3）—— 虚拟化 / 高亮 / 动作 ────────────────
    #     为什么单独做渲染探针：W3-d-1 只验了"能拿到数据"（--probe-search 走真 stdio），
    #     而"拿到 200 条后列表怎么表现"是另一回事，且**会静默退化** —— 本轮实测就抓到一个：
    #     iNKORE 主题的 ListBox 模板不给 ItemsPresenter 配滚动宿主，VirtualizingStackPanel
    #     的 ScrollOwner 为空 ⇒ 退化成普通 StackPanel，200 条生成 200 个容器（虚拟化名存实亡）。
    #     这类退化"看得见吗？"—— 小数据量下完全看不出来，10 万条时才卡；必须用数字钉住。
    #     （另一个当场抓到的：HitText 只有私有构造器 ⇒ FEF 反射构造失败，搜索窗**首次渲染必崩**。）
    ui_file = os.path.join(repo, "_scratch", "desktop-search-ui.json")
    if os.path.exists(ui_file):
        os.remove(ui_file)
    r = run(["--probe-search-ui", "--no-prompt", "--out", ui_file])
    ck("搜索窗渲染探针退出码 0（W3-d-2/3）", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:200]}")

    ui = {}
    if os.path.isfile(ui_file):
        try:
            with open(ui_file, encoding="utf-8") as f:
                ui = json.load(f)
        except (OSError, json.JSONDecodeError) as ex:
            info(f"搜索窗探针输出不可解析：{ex}")
    ck("搜索窗探针落盘且可解析", bool(ui) and "itemsCount" in ui, str(ui)[:200])

    if ui:
        cnt = ui.get("itemsCount", 0)
        realized = ui.get("realizedContainers", -1)
        ck("★ 200 条命中全部进列表（数据层渲染）", cnt == 200, f"itemsCount={cnt}")

        # ★★ 虚拟化的**数字**证据：面板"看到"200 项（extent）但只生成个位数容器（realized）。
        #    两条缺一不可 —— 只断 realized 小，可能只是因为列表根本没内容；
        #    只断 extent，实现完全可以一次性生成 200 个容器。
        ck(f"★★ 虚拟化真生效：只生成 {realized} 个容器承载 200 项（关掉会等于 200）",
           0 < realized < 60 and realized < cnt, f"realized={realized} of {cnt}")
        ck("★ 面板 extentHeight == 200（虚拟化面板确实知道有 200 项）",
           ui.get("extentHeight") == 200, f"extent={ui.get('extentHeight')}")
        ck("★ 视口高度为正且远小于总数（真视口 = 虚拟化前提）",
           0 < ui.get("viewportHeight", 0) < cnt,
           f"viewport={ui.get('viewportHeight')} extent={ui.get('extentHeight')}")

        # 结构前提（"设了但没生效"与"没设"必须能区分 —— 本轮实测踩过）
        ck("★ 结果面板是 VirtualizingStackPanel 且滚动语义为逐项",
           ui.get("itemsPanelType") == "VirtualizingStackPanel"
           and ui.get("usedVirtualizingPanel") is True
           and ui.get("canContentScroll") is True
           and ui.get("isVirtualizing") is True,
           f"{ui.get('itemsPanelType')} used={ui.get('usedVirtualizingPanel')} "
           f"ccs={ui.get('canContentScroll')} iv={ui.get('isVirtualizing')}")

        # 计数显示：total(12345) ≠ hits(200) 时两个数都要显示且都对
        ck("★ 计数显示 total 与 hits 分离（显示 200 / 共 12345 条）",
           ui.get("statusRight") == "显示 200 / 共 12345 条", repr(ui.get("statusRight")))

        # ── 卷漂移提示纯函数（热插拔已知限制的可见性补丁）四例直测 ─────────────
        drift = ui.get("driftProbe") or {}
        ck("★ 卷漂移：本机有 E: 未索引 → 提示'重启工具可纳入索引'",
           "未索引的卷 E:" in (drift.get("added") or ""), repr(drift.get("added")))
        ck("★ 卷漂移：索引含已移除的 Z: → 提示'重启工具可刷新'",
           "已移除的卷 Z:" in (drift.get("removed") or ""), repr(drift.get("removed")))
        ck("★ 卷漂移：卷清单与盘符一致 / 未就绪 → 不提示（防噪声）",
           drift.get("none") in (None, "") and drift.get("notReady") in (None, ""),
           f"none={drift.get('none')} notReady={drift.get('notReady')}")

        # 首屏渲染耗时（数字）：build 是数据层、render 到布局/渲染完成
        info(f"搜索窗首屏：build {ui.get('buildMs'):.2f} ms · render {ui.get('renderMs'):.2f} ms")
        ck("★ 首屏渲染耗时落在可见区间（build < 200ms 且 render < 2000ms，200 条）",
           0 <= ui.get("buildMs", -1) < 200 and 0 <= ui.get("renderMs", -1) < 2000,
           f"build={ui.get('buildMs')} render={ui.get('renderMs')}")

        # ── 高亮（W3-d-3）：中文 + 不连续多段，且**从渲染出来的 Run 读**（不重算）──
        segs = ui.get("firstSegments", [])
        first_name = ui.get("firstName") or ""
        joined = "".join(s.get("text", "") for s in segs)
        bold_segs = [s.get("text", "") for s in segs if s.get("bold")]
        ck("★ 首项真的在可视化树上（firstRendered）", ui.get("firstRendered") is True, str(ui))
        ck("★★ 中文文件名多段高亮：渲染出的分段拼接 == 名字本身",
           bool(segs) and joined == first_name, f"joined={joined!r} name={first_name!r}")
        ck("★★ 不连续两段都加粗（'季报' + '年度'，多段高亮逐段应用）",
           bold_segs == ["季报", "年度"], f"bold={bold_segs}")
        ck("★ 高亮位置落在回传区间上（首段从 0 起）",
           bool(segs) and segs[0].get("bold") is True and segs[1].get("text") == "2026",
           str(segs))

        # ── 动作（W3-d-3）：全路径 + explorer 反斜杠归一 + 删除文案 ──
        ck("★ 首项路径来自索引进程（全路径，含盘符 \\）",
           first_name != "" and (ui.get("firstPath") or "").startswith("C:\\"),
           repr(ui.get("firstPath")))
        act = ui.get("actions", {})
        ck("★★ 打开动作传入的是**全路径**（不是文件名）且走 ShellExecute",
           act.get("openTarget") == "C:/Users/ishe/Desktop/x.txt"
           and act.get("openShellExecute") is True, str(act.get("openTarget")))
        ck("★ 定位动作走 explorer /select",
           act.get("revealExe") == "explorer.exe"
           and (act.get("revealArgs") or "").startswith('/select,"'), repr(act.get("revealArgs")))
        ck("★★ 定位路径反斜杠归一（正斜杠是静默 no-op —— 2026-09-24 实锤）",
           act.get("revealPathHasForwardSlash") is False
           and (act.get("revealPath") or "").count("\\") == 4, repr(act.get("revealPath")))
        ck("★★ 文件已删除时打开失败给**明确文案**（含\"文件不存在\"，不静默）",
           "文件不存在" in (act.get("deletedMessage") or ""), repr(act.get("deletedMessage")))

        # ── 卷清单行（W3-e-2 "跳过必须可见"）：哪些卷进了索引、哪些被跳过、**为什么** ──
        #     为什么钉住：原实现用 Where(Fixed && IsReady) 静默过滤，被排除的卷不留痕 ——
        #     用户搜索不到某个盘的文件时只能猜"是没索引还是没匹配"。属 S9′/S11 家族。
        #     三件事缺一不可：① 跳过**可见**（数量）；② 跳过**带原因**（文案，不能只说"跳过 1 个"）；
        #     ③ 跳过清单**真的走协议**（statusCalls，而非探针硬编码）。
        vline = ui.get("volumesLine", "")
        ck("★★ 卷清单行显示已索引/跳过卷数（跳过必须可见）",
           "已索引 2 个卷" in vline and "跳过 1 个卷" in vline, repr(vline))
        ck("★★ 跳过必须带**具体原因**（不能只说'跳过 1 个卷'）",
           "文件系统不支持" in vline and "exFAT" in vline, repr(vline))
        ck("★ 卷清单走真 search.status 协议（非硬编码，statusCalls ≥ 1）",
           ui.get("statusCalls", 0) >= 1, f"statusCalls={ui.get('statusCalls')}")
        ck("★★ 跳过项带原因枚举 + 非空文案（reason ∈ 枚举，reasonText 非空）",
           all(s.get("reason") in ("NotFixed", "NotReady", "NoDriveLetter", "UnsupportedFileSystem")
               and s.get("reasonText", "").strip()
               for s in ui.get("skippedVolumes", []))
           and len(ui.get("skippedVolumes", [])) == 1,
           str(ui.get("skippedVolumes")))
        ck("★★ 无跳过时文案**不含**'跳过'（防噪声：全 NTFS 环境不该出现跳过字样）",
           "跳过" not in (ui.get("noSkipText") or "跳过")
           and ui.get("noSkipText") == "已索引 2 个卷 · 2,468 项", repr(ui.get("noSkipText")))
        ck("★ UI 显示的卷清单文案 == 纯函数算子输出（UI 与规则同源）",
           vline == ui.get("expectVolumesLine") and vline != "", repr(vline))

        # ── 失败卷结构化（2026-09-25 缺口②）：`0+0≠2` 的守卫 ──
        #     原来失败只进 lastError 自由文本 ⇒ 守恒式被一段字符串绕过去，没有任何断言会红。
        ck("★★ 失败卷**可见**且与跳过分开说（索引失败 N 个卷，不混进'跳过'）",
           "索引失败 1 个卷" in vline and "F:" in vline, repr(vline))
        ck("★★ 失败项带 kind + code + reasonText（可机读，不是一段自由文本）",
           all(f.get("kind", "").strip() and isinstance(f.get("code"), int)
               and f.get("reasonText", "").strip() and f.get("message", "").strip()
               for f in ui.get("failedVolumes", []))
           and len(ui.get("failedVolumes", [])) == 1
           and ui["failedVolumes"][0]["kind"] == "AccessDenied"
           and ui["failedVolumes"][0]["code"] == 5,
           str(ui.get("failedVolumes")))
        ck("★★★ 三档守恒：已索引 + 跳过 + 失败 == 检测到的卷（缺任一项这式子就破）",
           2 + 1 + 1 == ui.get("detectedVolumes"),
           f"detectedVolumes={ui.get('detectedVolumes')}")

        # ── 暂停徽标（2026-09-25 缺口①「暂停必须可见」）──
        #     暂停后索引不再跟进 ⇒ 搜索结果会静默变旧。不显示 = 拿过时数据骗用户。
        ck("★★ 未暂停时卷清单行**不含**'已暂停'（反向夹具，防恒真）",
           "已暂停" not in (ui.get("volumesLineUnpaused") or "已暂停"),
           repr(ui.get("volumesLineUnpaused")))
        ck("★★★ 暂停徽标出现在**行首**（行尾会被省略号截断 = 暂停不可见）",
           (ui.get("volumesLinePaused") or "").startswith("索引已暂停")
           and "点此恢复" in (ui.get("volumesLinePaused") or ""),
           repr(ui.get("volumesLinePaused")))
        ck("★★ 只有暂停时状态行才可点（未暂停点击**不会**误触发暂停）",
           ui.get("clickablePaused") is True and ui.get("clickableUnpaused") is False,
           f"paused={ui.get('clickablePaused')} unpaused={ui.get('clickableUnpaused')}")
        ck("★★ 暂停/恢复**走真协议**（pauseCalls=1 ∧ resumeCalls=1 ∧ 状态复位）",
           ui.get("pauseCalls") == 1 and ui.get("resumeCalls") == 1
           and ui.get("pausedAfterResume") is False,
           f"pauseCalls={ui.get('pauseCalls')} resumeCalls={ui.get('resumeCalls')} "
           f"pausedAfterResume={ui.get('pausedAfterResume')}")
        ck("★ 恢复后状态行回显'已恢复'（用户看得见操作生效了）",
           "恢复" in (ui.get("pauseText") or ""), repr(ui.get("pauseText")))

        # 反向（结构约束）：UI 侧**不含匹配算法** —— 匹配/打分/高亮区间全在索引进程。
        # 这是"结果集跨进程下发"红线的守卫：UI 一旦自己算分，跨进程契约就废了（R3）。
        sw_src = os.path.join(repo, "src", "Eztools.Desktop", "SearchWindow.cs")
        with open(sw_src, encoding="utf-8") as f:
            src = f.read()
        banned = ["ScorePrefix", "ScoreFuzzy", "MatchMode", "SelectMode", "TryMatch", "TryFuzzy"]
        hit = [b for b in banned if b in src]
        ck("★★ 反向结构约束：SearchWindow 不含任何匹配/打分类（匹配只在索引进程）",
           not hit, f"命中禁用符号：{hit}")

        # 正向（结构约束）：C2 手工修复的回归守卫（2026-09-25）。
        # ① 前台锁定：托盘是后台进程，Activate() 会被 Windows 静默拒绝
        #    （Topmost 可见但键盘焦点留在原前台）⇒ "热键唤出后直接打得进字"
        #    依赖 AttachThreadInput 组合拳 + _summoning 屏蔽瞬态失焦隐藏；
        # ② 未就绪快照：每次唤出必须重拉卷摘要（首帧可能撞上自举未完成），
        #    "准备中"轮询代替把"还没好"显示成"已索引 0 个卷 · 0 项"（S9 家族）。
        # ★★ 结构约束只查**代码**、不查注释（2026-09-27 实测补正）：
        #   朴素子串匹配会被"解释性注释里提一句"误伤 —— 误红（注释提到就失败）与误绿
        #   （代码删了、注释还在就假通过）都让断言失去可信度，比不守更坏。所以先剥注释行。
        code_src = "\n".join(
            ln for ln in src.splitlines()
            if not ln.lstrip().startswith(("//", "///", "*", "/*"))
        )

        need = ["ForceForeground", "AttachThreadInput", "_summoning",
                "RefreshVolumeSummary", "ScheduleVolumePoll",
                "_realClose", "RealClose",          # X/Alt+F4=隐藏 拦截（"热键全灭"主坑守卫）
                "DetectVolumeDrift",
                # 2026-09-27 换实现后的键盘链：输入框=宿主化原生 EDIT（IMM32），
                # 命令键由控件上报，宿主用 Win32 焦点读数断言"打不进字"。
                "NativeInputBox", "OnInputCommand", "IsInputFocused"]
        miss = [n for n in need if n not in code_src]
        ck("★★ 正向结构约束：SearchWindow 含前台锁定 + 唤出刷新 + X 关闭拦截 + 原生输入框命令链（C2/G1 修复守卫）",
           not miss, f"缺失符号：{miss}")

        # ★★ 反向结构约束（2026-09-27 换实现的核心验收点）：
        # 合成层 / 轮询层 / hwnd 钩子 / WPF 键盘事件拦截 —— 这四件套是"上一轮补丁的副作用"
        # 的解药，共存即互相双打（§2.29）。换到原生 EDIT 后它们必须整体消失，
        # 任何一件"复活"都意味着有人又把干预层接回了单供字者架构。
        banned_keyboard = ["SynthesizeDirectChar", "PollTypingFallback", "PollKeyFallback",
                           "ImeProcessedKey", "PreviewKeyDown", "GetAsyncKeyState", "WndProcHook"]
        revived = [b for b in banned_keyboard if b in code_src]
        ck("★★ 反向结构约束：键盘干预层（合成/轮询/hwnd钩子/WPF 键盘事件）不得复活",
           not revived, f"命中已废干预符号：{revived}")
        ck("★ 死字段不复活：SearchWindow 不含 _volumesLoaded（每次唤出重拉，无需缓存标志）",
           "_volumesLoaded" not in src, "命中 _volumesLoaded（已废缓存标志回归）")
        # ③ NTFS 卷根 FRN=5 不出现在 FSCTL_ENUM_USN_DATA 枚举结果里
        #    （实测铁证：C:\ 根顶层 pagefile.sys 路径也断）⇒ PathResolver
        #    回溯必须显式判停，"根自指"兜底不覆盖无父记录的普通回溯路径。
        qe_src = os.path.join(repo, "src", "Eztools.Index", "QueryEngine.cs")
        with open(qe_src, encoding="utf-8") as f:
            qe = f.read()
        ck("★★ 正向结构约束：QueryEngine 含 RootFrn 判停（枚举不吐根记录，路径 `?\\` 守卫）",
           "RootFrn" in qe, "缺失 RootFrn（`?\\` 断链回归）")

    # ── 1c-5. 搜索窗唤出生命周期（C2/G1 手工三 bug 的自动化面）────────────────
    #     为什么必须有：热键"真按键"不可自动化（G1 明文），但 2026-09-25 用户实测的
    #     "第一次能唤出，以后热键全灭"是纯窗口层性质 —— 点一次标题栏 X 真关闭了窗口，
    #     之后每次热键 Show() 抛 InvalidOperationException 被托盘吞掉（只进日志）。
    #     这类"异常被 UI 入口吞掉"的静默死亡必须用探针钉住。
    summon_file = os.path.join(repo, "_scratch", "desktop-search-summon.json")
    if os.path.exists(summon_file):
        os.remove(summon_file)
    r = run(["--probe-search-summon", "--no-prompt", "--out", summon_file], timeout=180)
    ck("搜索窗唤出探针退出码 0", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:200]}")
    summon = {}
    if os.path.isfile(summon_file):
        try:
            with open(summon_file, encoding="utf-8") as f:
                summon = json.load(f)
        except (OSError, json.JSONDecodeError) as ex:
            info(f"唤出探针输出不可解析：{ex}")
    ck("唤出探针落盘且可解析", bool(summon) and "cycles" in summon, str(summon)[:200])

    if summon:
        cycles = (summon.get("cycles") or {}).get("cycles") or []
        for c in cycles:
            n = c.get("cycle")
            ck(f"★ 唤出循环第 {n} 轮：无异常",
               not c.get("summonError") and not c.get("resummonError") and not c.get("closeError"),
               f"{c.get('summonError')} {c.get('resummonError')} {c.get('closeError')}")
            t1 = c.get("t1") or {}
            ck(f"★★ 第 {n} 轮：唤出后稳定可见（'唤出即被藏'竞态守卫）",
               t1.get("visible") is True, f"t1={t1}")
            ck(f"★★ 第 {n} 轮：键盘焦点真落在输入框（'窗口在但打不进字'守卫）",
               t1.get("queryFocused") is True, f"t1={t1}")

        xv = (summon.get("cycles") or {}).get("closeViaX") or {}
        ax = xv.get("afterXResummon") or {}
        ck("★★ X/Alt+F4 关闭被拦截成隐藏（Close 后仍可再唤出 —— '热键全灭'主坑守卫）",
           not xv.get("resummonError") and ax.get("visible") is True and ax.get("queryFocused") is True,
           f"afterXResummon={ax} err={xv.get('resummonError')}")

    # live 变体（真链路 lei/LEI 大小写 + 未就绪诊断）：需要现成就绪索引，缺夹具则跳过。
    live_root = os.path.join(repo, "_scratch", "manual", "g2first2")
    if os.path.isdir(os.path.join(live_root, "index")):
        live_env = dict(env, EZTOOLS_INSTALL_ROOT=live_root.replace("\\", "/"))
        live_file = os.path.join(repo, "_scratch", "desktop-search-summon-live.json")
        lr = subprocess.run(
            [desktop, "--probe-search-summon", "--probe-search-live", "--wait-ready", "60000",
             "--no-prompt", "--out", live_file],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=live_env, cwd=repo, timeout=300)
        ck("搜索窗 live 探针退出码 0（真链路 lei/LEI）", lr.returncode == 0,
           f"code={lr.returncode} err={lr.stderr[:200]}")
        live = {}
        if os.path.isfile(live_file):
            try:
                with open(live_file, encoding="utf-8") as f:
                    live = json.load(f)
            except (OSError, json.JSONDecodeError) as ex:
                info(f"live 探针输出不可解析：{ex}")
        lv = live.get("live") or {}
        if lv.get("ready") is True:
            lei = (lv.get("lei") or {}).get("statusRight") or ""
            lei_up = (lv.get("leiUpper") or {}).get("statusRight") or ""
            ck("★★ 真链路 lei 与 LEI（CapsLock 等价物）计数一致（大小写折叠守卫）",
               lv.get("caseFoldEqual") is True and lei == lei_up and lei.startswith("显示"),
               f"{lei!r} vs {lei_up!r}")
        else:
            info(f"live 探针索引未就绪（ready={lv.get('ready')}），跳过大小写断言（状态行原文已落盘）")
    else:
        info("live 探针跳过：未找到现成索引夹具 _scratch/manual/g2first2/index")

    # ── 1c-6. W4-c：OCR 热键链路 + 宿主设置探针 ─────────────────────────────
    #     为什么必须有：selfcheck 只证明"热键注册成功"（RegisterHotKey 返回真），
    #     不证明"按键真的触发遮罩"。--probe-ocr-hotkey 用 keybd_event 注入真实
    #     Ctrl+Alt+O → WM_HOTKEY → 遮罩出现 → Esc 收窗，整条系统链一次钉死。
    #     （同 1c-5 先例：真按键注入有 1~2 秒抢焦点副作用，本脚本无人值守上下文
    #     已接受该量级 —— --probe-search-summon 同款。）
    #     宿主设置探针（--probe-host-settings）无副作用：程序化走真实设置窗口，
    #     改 OCR 热键 → 保存 → 断言生效 → unset 恢复默认（探针自恢复，无残留）。
    host_file = os.path.join(repo, "_scratch", "desktop-host-settings.json")
    if os.path.exists(host_file):
        os.remove(host_file)
    r = run(["--probe-host-settings", "--no-prompt", "--out", host_file,
             "--tools-dir", tools_dir, "--install-root", install_root,
             "--config-root", config_root], timeout=120)
    ck("宿主设置探针退出码 0（改键→保存→生效→恢复整条链）", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:200]}")
    host = {}
    if os.path.isfile(host_file):
        try:
            with open(host_file, encoding="utf-8") as f:
                host = json.load(f)
        except (OSError, json.JSONDecodeError) as ex:
            info(f"宿主设置探针输出不可解析：{ex}")
    after = host.get("after") or {}
    restored = host.get("restored") or {}
    ck("★★ 设置保存后 desktop.json 落盘新热键（Ctrl+Alt+K）",
       str(after.get("savedValue", "")).upper() == "CTRL+ALT+K", f"after={after}")
    ck("★★ 保存回调链立即生效（生效热键重合成 + 热键真重注册）",
       str(after.get("effectiveHotkey", "")).upper() == "CTRL+ALT+K"
       and after.get("registered") is True, f"after={after}")
    ck("★★ unset 恢复默认后回落 Ctrl+Alt+O 且重注册成功",
       str(restored.get("effectiveHotkey", "")).upper() == "CTRL+ALT+O"
       and restored.get("registered") is True, f"restored={restored}")

    hotkey_file = os.path.join(repo, "_scratch", "desktop-ocr-hotkey.json")
    if os.path.exists(hotkey_file):
        os.remove(hotkey_file)
    r = run(["--probe-ocr-hotkey", "--no-prompt", "--out", hotkey_file,
             "--tools-dir", tools_dir, "--install-root", install_root,
             "--config-root", config_root], timeout=120)
    ck("OCR 热键真按键探针退出码 0（真注入组合键→遮罩→Esc 收窗）", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:200]}")
    ocr_hotkey = {}
    if os.path.isfile(hotkey_file):
        try:
            with open(hotkey_file, encoding="utf-8") as f:
                ocr_hotkey = json.load(f)
        except (OSError, json.JSONDecodeError) as ex:
            info(f"OCR 热键探针输出不可解析：{ex}")
    ck("★★ 真按键 Ctrl+Alt+O 触发遮罩全屏出现（RegisterHotKey→WM_HOTKEY 链）",
       ocr_hotkey.get("overlayShown") is True, str(ocr_hotkey)[:200])
    ck("★★ Esc 真按键收窗（键盘消息→PreviewKeyDown→Cancel 链）",
       ocr_hotkey.get("escClosed") is True, str(ocr_hotkey)[:200])

    # ── 1c-7. W5-b：剪贴板面板探针 ──────────────────────────────────────────
    #     生命周期循环（唤出/收起/X=隐藏契约）+ 直贴契约：程序化过滤到 1 条 →
    #     真键 Enter → 断言面板隐藏 + 剪贴板逐字等于条目内容。
    #     副作用：Summon 真抢焦点 1~2 秒（--probe-search-summon 同量级）；Ctrl+V
    #     真注入被探针抑制（贴进终端不可接受）—— 端到端注入归手工清单 M 项。
    clip_file = os.path.join(repo, "_scratch", "desktop-clip-panel.json")
    if os.path.exists(clip_file):
        os.remove(clip_file)
    r = run(["--probe-clip-panel", "--no-prompt", "--out", clip_file,
             "--tools-dir", tools_dir, "--install-root", install_root,
             "--config-root", config_root], timeout=120)
    ck("剪贴板面板探针退出码 0（生命周期 + 直贴契约）", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:200]}")
    clip = {}
    if os.path.isfile(clip_file):
        try:
            with open(clip_file, encoding="utf-8") as f:
                clip = json.load(f)
        except (OSError, json.JSONDecodeError) as ex:
            info(f"剪贴板面板探针输出不可解析：{ex}")
    cycles = (clip.get("cycles") or {}).get("cycles") or []
    last_cycle = cycles[-1] if cycles else {}
    t0, t1, t2 = last_cycle.get("t0") or {}, last_cycle.get("t1") or {}, last_cycle.get("t2") or {}
    ck("★★ 唤出生命周期：热键路径 Toggle 后可见且焦点在输入框（t1）",
       t1.get("visible") is True and t1.get("queryFocused") is True, f"t1={t1}")
    ck("★★ 再按热键收起（t2 hidden）+ 唤出期不被失焦抖动藏掉（t0=t1=visible）",
       t0.get("visible") is True and t2.get("visible") is False, f"t0={t0} t2={t2}")
    close_via_x = (clip.get("cycles") or {}).get("closeViaX") or {}
    resummon = close_via_x.get("afterXResummon") or {}
    ck("★★ X=隐藏契约：Close 后可再唤出且焦点恢复（真关闭=热键全灭的经典坑）",
       resummon.get("visible") is True and resummon.get("queryFocused") is True,
       f"afterXResummon={resummon}")
    paste = clip.get("paste") or {}
    ck("★★ 直贴契约：真键 Enter 后面板隐藏 + 剪贴板逐字等于条目内容",
       paste.get("hiddenAfterEnter") is True and paste.get("clipboardMatches") is True,
       f"paste={ {k: paste.get(k) for k in ('itemsAtInject', 'hiddenAfterEnter', 'clipboardMatches')} }")
    ck("★ 直贴目标已捕获（唤出前前台句柄非零）+ 搜索过滤到恰 1 条",
       paste.get("lastForegroundNonZero") is True and paste.get("itemsAtInject") == 1,
       f"paste={ {k: paste.get(k) for k in ('lastForegroundNonZero', 'itemsAtInject')} }")

    # ── 1c-7b. W5-d：图片 OCR 提字（FR-15）─────────────────────────────────
    #     为什么必须单列：FR-15 的失败面**全是静默的** —— 菜单项恒不可用、识别不出却不报错、
    #     语言包缺失却谎报"图里没有文字"。三条判据（非图片不可用 / 图片可用 / 真提出字）
    #     缺任何一条就漏掉一类静默失效，所以三条一起上，并配一条反向。
    #     语言包缺失属环境依赖分支 ⇒ 落 skipped 跳过，不算失败（与既有 OCR 段同口径）。
    ocr_file = os.path.join(repo, "_scratch", "desktop-clip-ocr.json")
    if os.path.exists(ocr_file):
        os.remove(ocr_file)
    r = run(["--probe-clip-ocr", "--no-prompt", "--out", ocr_file], timeout=120)
    ck("图片 OCR 提字探针退出码 0（语言包缺失时按跳过处理）", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:200]}")
    ocr = {}
    if os.path.isfile(ocr_file):
        try:
            with open(ocr_file, encoding="utf-8") as f:
                ocr = json.load(f)
        except (OSError, json.JSONDecodeError) as ex:
            info(f"OCR 提字探针输出不可解析：{ex}")
    if ocr.get("skipped"):
        info(f"图片 OCR 提字：跳过（{ocr.get('skipped')}）—— 本机无 OCR 语言包，属环境依赖分支")
    else:
        ck("★ 图片 OCR 提字：非图片条目菜单项不可用（反向 —— 证明判据不是恒真）",
           ocr.get("menuEnabledForText") is False,
           f"menuEnabledForText={ocr.get('menuEnabledForText')}")
        ck("★ 图片 OCR 提字：图片条目菜单项可用（正向）",
           ocr.get("menuEnabledForImage") is True,
           f"menuEnabledForImage={ocr.get('menuEnabledForImage')}")
        ck("★★ 图片 OCR 提字：真实菜单点击 → 剪贴板拿到样图文字（含 OCR + 含数字 + ≥8 字）",
           ocr.get("recognized") is True,
           f"clipboard={ocr.get('clipboard')!r} status={ocr.get('statusText')!r}")

    # ── 1c-8. W5-c：剪贴板监听探针 ──────────────────────────────────────────
    #     "复制真的进历史"的唯一自动化面：真 AddClipboardFormatListener → 程序化写
    #     剪贴板 → 裸泵喂 WM_CLIPBOARDUPDATE → 断言事件触发 + 内容入库。
    #     副作用 = 改写一次系统剪贴板（不抢焦点，与 section 2 反复改剪贴板同量级）。
    mon_file = os.path.join(repo, "_scratch", "desktop-clip-monitor.json")
    if os.path.exists(mon_file):
        os.remove(mon_file)
    r = run(["--probe-clip-monitor", "--no-prompt", "--out", mon_file,
             "--tools-dir", tools_dir, "--install-root", install_root,
             "--config-root", config_root], timeout=60)
    ck("剪贴板监听探针退出码 0（真 listener → 真事件 → 真入库）", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:200]}")
    mon = {}
    if os.path.isfile(mon_file):
        try:
            with open(mon_file, encoding="utf-8") as f:
                mon = json.load(f)
        except (OSError, json.JSONDecodeError) as ex:
            info(f"剪贴板监听探针输出不可解析：{ex}")
    ck("★★ 剪贴板监听真事件链路：listener 启动 + 5 秒内捕获 + 内容入库",
       mon.get("listenerStarted") is True and mon.get("captured") is True
       and mon.get("storeHits") == 1,
       f"mon={ {k: mon.get(k) for k in ('listenerStarted', 'captured', 'elapsedMs', 'storeHits')} }")

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

    # ── 2b-2. ★★ 热键直达面板：宿主解析出的 opensPanel + 气泡正文（**不依赖前台窗口**）──
    #     这两条**故意**放在 2c 那个"需要真资源管理器窗口"的块**之外**：
    #     实测 2c 的两条选中项断言在桌面上没有资源管理器窗口时会走【跳过】分支，
    #     于是 ⑦（气泡正文真值）**一次都没跑过** —— 我正好是被它守的那个回归
    #     （DescribeResult 写好了但 InvokeAsync 调用点没接上），却因为跳过而没红。
    #     教训：**把"守卫关键回归"的断言挂在环境依赖的分支里，等于没有守卫**。
    #     这里的观测量来自 --probe-hotkey 的落盘载荷，`preview.show` 即便没有选中项
    #     也会正常返回（recorded=0 是合法结果），所以可以无条件断言。
    probe_out2 = os.path.join(repo, "_scratch", "probe-hotkey-uncond.json")
    if os.path.exists(probe_out2):
        os.remove(probe_out2)
    set_clipboard("")   # 与 2c 解耦：这条不关心选中了什么
    r = run(["--probe-hotkey", "preview.show", "--out", probe_out2])
    ck("热键项【速览】在无前台选中时也能触发（探针前置条件）", r.returncode == 0,
       f"code={r.returncode} err={r.stderr[:120]}")

    ucond = {}
    if os.path.exists(probe_out2):
        with open(probe_out2, encoding="utf-8") as fh:
            ucond = json.load(fh)

    # ★ 宿主解析出的 opensPanel 必须命中工具真的声明了的那个面板 id。
    #   为什么必须有：这条链路上「热键 → 命令 → 面板」有三个可能断开的接点
    #   （清单字段没解析 / 仲裁带回时丢了 / 调用成功后没去开面板），
    #   而它们**全部**表现为"按了键只弹气泡、面板不出现"—— 退出码 0、日志正常、零诊断。
    #   断言对象是**宿主自己报出来的值**，所以断的是"宿主真的知道了"，不是"清单里写了"。
    ck("★ 热键声明的 opensPanel 被宿主解析出来（=工具声明的主面板 id）",
       ucond.get("opensPanel") == "main", f"opensPanel={ucond.get('opensPanel')!r}")

    # ★ 气泡正文真值（原 ⑦）：宿主算出的正文必须等于工具给的 hint。
    #   §2d 的 ①~⑥ 只测一个 Python shim —— 那是我照抄 C# 规则写的，
    #   **抄对了不代表宿主调了它**。实测踩到：DescribeResult 方法写好了、
    #   InvokeAsync 的调用点却没接上（还原突变时被 cp 覆盖），
    #   6 条断言全绿、验收 169/0，功能实际为零。
    real_balloon = ucond.get("balloon")
    ck("★ 宿主算出的气泡正文 = 工具给的 hint（真路径，不是照抄的副本）",
       real_balloon == ucond.get("hint") and isinstance(real_balloon, str)
       and "recorded" not in real_balloon and not real_balloon.startswith("{"),
       f"balloon={real_balloon!r} hint={ucond.get('hint')!r}")

    if os.path.exists(probe_out2):
        os.remove(probe_out2)

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
    # 🔴 必须 normpath 成反斜杠形式：$REPO 从 bash 传来是正斜杠（D:/...），
    #    而 **explorer /select 对正斜杠路径静默 no-op**（不报错、不开窗）——
    #    2026-09-24 实测：正斜杠 20s TIMEOUT，反斜杠 OK hwnd=…。这就是托盘
    #    3 条选中项断言历轮必跳过的根因（含用户自己终端的轮次）。
    #    同理 close_windows_with_target 的等值匹配与 _wait-selection 的
    #    -ieq 比较，explorer 侧回报的都是反斜杠路径，正斜杠目标永远匹配不上
    #    （预清理与判据两处潜伏 bug 一并消除）。
    target = os.path.normpath(os.path.join(repo, "README.md"))
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
        out = (r.stdout or "").strip()
        if out:
            return out
        # stdout 空 = 脚本没产出任何行 —— 把 stderr 亮出来（此前 PowerShell 报错
        # 被静默吞掉，"COM 挂了"与"真没开窗"无法区分，2026-09-24 排障教训）。
        err = (r.stderr or "").strip()
        return err[:400] or "(dump 无任何输出)"

    try:
        if os.path.exists(probe_out):
            os.remove(probe_out)
        close_windows_with_target()

        # 开窗 + 轮询等待"前台窗口的选中项包含目标"—— 判据直接对齐产品需要的状态。
        # 不能只等"前台是 CabinetWClass"：已在前台的其它 Explorer 窗口（Home/此电脑）
        # 会提前满足，探针读到的就是别人的选中项（实测踩到）。
        # 🔴 这个分支内的 **3 条**断言是**环境依赖**的：桌面正被人使用时前台会漂移，
        #    等待可能超时 —— 超时报【跳过】并附现场状态（诚实暴露，不假红也不假绿）；
        #    机制本身由 2b 的热键断言与"反向"断言共同守。
        #
        # ⚠️ 跳过会让**总断言数变化**（本批 3 条），于是"验收通过 219/220/222"都出现过 ——
        #    这正是 S9（断言挂在环境分支内 ⇒ 整块跳过、一次都没跑）的复发形态。
        #    对策不是取消跳过（环境依赖无法消除），而是**把跳过变成可观测量**：
        #    跳过时递增 SKIPPED 并在末尾汇总行打出，让"少跑了几条"在日志里可见，
        #    而不是让人对着一个会漂移的总数猜。
        w = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", os.path.join(repo, "scripts", "_wait-selection.ps1"),
             "-Target", target],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        first = (w.stdout or "").strip().splitlines()
        opened = bool(first) and first[0].startswith("OK")

        if not opened:
            globals()["SKIPPED"] += 3   # 本分支守的 3 条断言（S9：跳过必须可观测）
            info("跳过 3 条选中项断言：20 秒内前台未出现目标选中状态（桌面正被使用？）。现场：")
            # _wait-selection 的首行（OK / TIMEOUT / COM_FAIL）也是证据的一部分：
            # 只看 dump 分不清"开了窗但没抢到前台"和"开窗本身失败"（2026-09-24 排障教训）。
            for ln in first:
                info("  [wait] " + ln)
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

            # ⑦ 气泡正文真值断言已上移到 §2b-2（无条件执行）——
            #    它曾留在这里，于是"桌面上没有资源管理器窗口"时整条被【跳过】，
            #    而它守的正是那个真实回归。这里不再重复。

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

    # ── 2d. ★ 气泡摘要约定：优先 result.hint，无则回落 JSON ─────────────────
    #     气泡与调用结果**同一时刻生成、同一处截断**（Shorten(…, 240)），所以这里
    #     用真的返回值 + 真的 Shorten 规则在 Python 侧重算一遍"气泡会显示什么"，
    #     再断言它是给话人看的 hint，而不是被拦腰截断的 JSON。
    #     为什么必须有：hint 一旦丢失（比如某次重构只改 TrayApplication 不改工具），
    #     症状是"气泡里是断头 JSON"—— 功能没坏、退出码 0、日志照样有，没有任何测试会红。
    desc = _touch_tray_desc_module()

    # ① 正向：preview.show 空选中时的返回值 → 气泡该显示 hint 那段话本身
    sample = {
        "recorded": 0,
        "hint": "未拿到选中项：请在资源管理器中选中文件后，再按 Ctrl+Alt+P（或托盘 → 速览选中的文件）",
    }
    d = desc.describe(sample)
    ck("有 hint 时气泡显示 hint 原文（不外露 recorded 等协议字段）",
       d == sample["hint"] and "recorded" not in d, repr(d[:120]))

    # ② 关键：hint 与"240 字符截断"一起看 —— 这正是落 JSON 会翻车的场景。
    #    判据 = 同一份载荷，走 hint 完整可见、走 JSON 内联会被截断（带 …）。
    long_hint = "已记录 1 个文件。打开 托盘 → 面板 → 速览 查看；面板开着时按『刷新』换下一个文件"
    payload = {
        "recorded": 1,
        "paths": ["D:/01-项目代码/Eztools/" + "很长的路径片段" * 30 + "/README.md"],
        "hint": long_hint,
    }
    d2 = desc.describe(payload)
    json_inline = desc.shorten(desc.describe({k: v for k, v in payload.items() if k != "hint"}))
    ck("同一载荷：hint 完整进气泡，JSON 内联则被 240 字符截断（hint 的价值所在）",
       d2 == long_hint and not d2.endswith("…") and json_inline.endswith("…"),
       f"hint={d2[:60]!r}… json={json_inline[-20:]!r}")

    # ③ 回落：既有工具（echo/filehash/wordcount）的短状态对象没有 hint，仍显示 JSON
    d3 = desc.describe({"pong": True})
    ck("无 hint 的旧工具回落显示 JSON（兼容既有工具，不显示『无返回值』）",
       "pong" in d3 and "无返回值" not in d3, repr(d3))

    # ④ 反向：hint 必须是**字符串**才算数；写成对象是工具写错了类型，不能 ToString 成类名。
    #    判据必须严到"输出里不含裸类型名，且是被引号包起来的 JSON 值"——
    #    只查 `"weird" in d4` 是不够的：把 dict 直接 str() 出来也含 weird（突变验证实测放行了）。
    d4 = desc.describe({"hint": {"weird": 1}, "ok": True})
    # 解析先做且带兜底：突变态下 describe 可能返回非 JSON 文本，直接 json.loads 会抛异常
    # 把**后续断言整段断掉**（实测：突变验证时后面 5 条直接消失，只剩 1 条 FAIL）——
    # 那是"测试脚本崩了"，不是"断言失败了"，两者在 CI 日志里必须能分辨。
    try:
        d4_obj = json.loads(d4)
    except ValueError:
        d4_obj = None
    ck("反向：hint 非字符串时回落 JSON（不把对象 ToString 成类型名）",
       d4_obj == {"hint": {"weird": 1}, "ok": True}
       and "'weird'" not in d4 and ": 1" not in d4 and "JsonObject" not in d4,
       repr(d4))

    # ⑤ 反向：空/空白 hint 等同没有 —— 否则气泡会显示一片空白
    d5 = desc.describe({"hint": "   ", "ok": True})
    ck("反向：空白 hint 回落 JSON（不显示空白气泡）", "ok" in d5 and d5.strip() != "",
       repr(d5))

    # ⑥ 两端口径一致：工具返回值里带了 hint，且与托盘读的是同一个键名（防"改了工具没改宿主"）
    preview_main = os.path.join(repo, "tools", "preview", "main.py")
    with open(preview_main, encoding="utf-8") as fh:
        src = fh.read()
    ck("preview 工具的 show/openExternal 都返回 hint（与托盘读取的键名一致）",
       src.count('"hint"') >= 2, f"count={src.count(chr(34) + 'hint' + chr(34))}")

    # ⑦ ★ 真路径留到 §2c 之后 —— 那里有真实的工具返回值可喂（见下）。

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

    print(f"[信息] 托盘验收：通过 {PASS} / 失败 {FAIL}"
          + (f" / 跳过 {SKIPPED}（环境依赖分支未满足，总断言数因此少于满额）" if SKIPPED else ""))
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
