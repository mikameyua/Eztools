#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
verify-runtimes.py —— 校验「发布产物可在未安装 .NET 的干净环境启动」

★ 为什么不直接看 runtimeconfig.json 就够了：
    `SelfContained` 字段只声明意图，apphost 真正的判据是
    **运行时本体 + 依赖 + native host 都在同目录且能被解析**。
    少一个 System.Private.CoreLib.dll 或 coreclr.dll，
    启动时才会报"You must install or update .NET" —— 而这句话
    在**已经自包含**的包里出现，是最典型的"以为打包对了"的假绿。

本脚本做两层校验：
  ① 静态：解包后逐个 Exe 查 runtimeconfig.json 的 selfContained + 运行时文件齐备
  ② 动态：**真在清空 DOTNET_ROOT / DOTNET_HOST_PATH 的环境里启动**，
          确认不依赖机器上装没装 .NET

用法：
    python scripts/verify-runtimes.py --stage <解包目录> [--bin <bin子目录>]
    python scripts/verify-runtimes.py --zip <产物 zip>      # 自动解包到临时目录再验

判据：任一项FAIL ⇒ 退出码非 0。
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

# ── 四个可执行：漏掉任何一个都是真实故障，且**发现难度递增** ──
#   ezt               漏 ⇒ 一启动就报错（易发现）
#   Eztools.Desktop   漏 ⇒ 托盘起不来（中等）
#   ezt-core          漏 ⇒ 提权进程起不来，功能**静默降级**（难发现）
#   ezt-index         漏 ⇒ 前台正常、首次搜索才炸（最难发现）
EXES = ["ezt.exe", "Eztools.Desktop.exe", "ezt-core.exe", "ezt-index.exe"]

# 运行时本体与 native host：apphost 缺任何一个都起不来。
# 注意 hostpolicy.dll 同样是 native，不能只查托管 dll。
RUNTIME_FILES = [
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "System.Private.CoreLib.dll",
    "System.Runtime.dll",
]

# 隔离环境要清掉的变量：任何残留都会让 apphost 找到机器上的 .NET ⇒ 假绿。
ENV_VARS_TO_DROP = [
    "DOTNET_ROOT",
    "DOTNET_HOST_PATH",
    "DOTNET_ROOT_X64",
    "DOTNET_ROOT_X86",
    "CORECLR_ENABLE_PROFILING",
    "CORECLR_PROFILER",
    "COMPlus_EnableDiagnostics",
]

PASS = 0
FAIL = 0


def ck(name: str, ok: bool, detail: str = "") -> bool:
    global PASS, FAIL
    if ok:
        PASS += 1
        print(f"  [PASS] {name}" + (f" —— {detail}" if detail else ""))
    else:
        FAIL += 1
        print(f"  [FAIL] {name}" + (f" —— {detail}" if detail else ""))
    return ok


def clean_env() -> dict:
    """构造一个**不含任何 .NET 变量**、且 PATH 里找不到 dotnet 的环境。

    ⚠️ PATH 只留 System32（命令行/系统程序集所在）—— 刻意**不放
       dotnet 的安装目录**，否则 apphost 可能经注册表/PATH 找到机器安装，
       于是"框架依赖产物也能跑" ⇒ 判据失效（假绿）。
    """
    env = {
        k: v
        for k, v in os.environ.items()
        if k not in ENV_VARS_TO_DROP and not k.upper().startswith(("DOTNET_", "CORECLR_", "COMPLUS_"))
    }
    env["PATH"] = os.pathsep.join(
        [r"C:\Windows\System32", r"C:\Windows", r"C:\Windows\System32\Wbem"]
    )
    env["SystemRoot"] = r"C:\Windows"
    env["TEMP"] = env.get("TEMP", r"C:\Windows\Temp")
    env["TMP"] = env.get("TMP", r"C:\Windows\Temp")
    return env


def static_checks(bin_dir: Path) -> None:
    print("== ① 静态：运行时文件齐备性 ==")
    for f in RUNTIME_FILES:
        p = bin_dir / f
        size_kb = p.stat().st_size // 1024 if p.is_file() else 0
        ck(f"运行时文件 {f}", p.is_file() and size_kb > 0, f"{size_kb} KB" if p.is_file() else "缺失")

    print()
    print("== ② 静态：逐 Exe 的 runtimeconfig 声明 ==")
    for exe in EXES:
        cfg = bin_dir / (exe[: -len(".exe")] + ".runtimeconfig.json")
        if not ck(f"{exe} 有 runtimeconfig.json", cfg.is_file()):
            continue
        try:
            data = json.loads(cfg.read_text(encoding="utf-8"))
        except Exception as exc:  # noqa: BLE001
            ck(f"{exe} runtimeconfig 可解析", False, str(exc)[:120])
            continue
        ro = data.get("runtimeOptions", {})
        #★ 自包含有两种表达，必须都认（本脚本第一版只认后一种⇒ 对 .NET 6+ 产物假红）：
        #   ① .NET 6+：runtimeOptions.includedFrameworks 列出随包携带的共享框架
        #   ② 旧形态：runtimeOptions.selfContained == true
        included = ro.get("includedFrameworks") or []
        sc_flag = ro.get("selfContained")
        is_self_contained = bool(included) or sc_flag is True
        detail = (
            f"includedFrameworks={[f.get('name', '?').split('.')[-3] for f in included]}"
            if included
            else f"selfContained={sc_flag}"
        )
        ck(f"{exe} 自包含声明", is_self_contained, detail)

    print()
    print("== ③ 静态：宿主程序集齐备 ==")
    # ★ 只列**类库**产出物：Eztools.Index 是Exe 项目（AssemblyName=ezt-index），
    #   它只产出 ezt-index.exe，**不会**有 Eztools.Index.dll ——
    #   把 dll列进来会恒假FAIL（同 §2.43「断言守副本」的错误）。
    for dll in ["Eztools.Host.dll", "Eztools.Contracts.dll"]:
        p = bin_dir / dll
        ck(f"宿主程序集 {dll}", p.is_file(), f"{p.stat().st_size // 1024} KB" if p.is_file() else "缺失")
    # 索引能力是 Exe 形态，用 exe 本身当判据
    ck("Eztools.Index 以 ezt-index.exe 形态在场", (bin_dir / "ezt-index.exe").is_file(),
       "Exe 项目无同名 dll，见上方注释")


def dynamic_checks(bin_dir: Path, timeout: int) -> None:
    print()
    print("== ④ 动态：清空 .NET 环境变量后真启动 ==")
    print(f"   （清空 {', '.join(ENV_VARS_TO_DROP[:3])} 等；PATH 只留 System32）")

    env = clean_env()

    # ④a ezt.exe --version：控制台程序，最适合当启动探针
    ezt = bin_dir / "ezt.exe"
    if ezt.is_file():
        try:
            r = subprocess.run(
                [str(ezt), "--version"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=env,
                timeout=timeout,
                cwd=str(bin_dir),
            )
            out = (r.stdout or "") + (r.stderr or "")
            ok = r.returncode == 0 and "Eztools" in out
            ck("ezt.exe --version 在隔离环境启动", ok,
               f"rc={r.returncode} · {(r.stdout or '').strip()[:60]}")
            # 反向对照：若产物其实是框架依赖，这里会明确报缺 .NET
            if not ok and "install or update .NET" in out:
                ck("识别出框架依赖回退（诊断线索）", False,
                   "输出含 'You must install or update .NET' ⇒ **产物是框架依赖包**")
        except subprocess.TimeoutExpired:
            ck("ezt.exe --version 在隔离环境启动", False, f"超时（>{timeout}s）")
        except Exception as exc:  # noqa: BLE001
            ck("ezt.exe --version 在隔离环境启动", False, str(exc)[:120])

    # ④b Eztools.Desktop.exe --selfcheck：GUI 程序的正确探测方式。
    #    注意**不能**用 --version（GUI 无此参数 ⇒ 会弹窗或挂起）。
    desktop = bin_dir / "Eztools.Desktop.exe"
    if desktop.is_file():
        try:
            r = subprocess.run(
                [str(desktop), "--selfcheck"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=env,
                timeout=timeout + 20,
                cwd=str(bin_dir),
            )
            out = (r.stdout or "") + (r.stderr or "")
            missing_rt = "install or update .NET" in out
            # 判据 = rc 正常**且**输出无缺运行时（后者在部分形态下会带着 0 退出）
            ck("Eztools.Desktop.exe --selfcheck 在隔离环境启动",
               r.returncode == 0 and not missing_rt, f"rc={r.returncode}")
        except subprocess.TimeoutExpired:
            ck("Eztools.Desktop.exe --selfcheck 在隔离环境启动", False, "超时")
        except Exception as exc:  # noqa: BLE001
            ck("Eztools.Desktop.exe --selfcheck 在隔离环境启动", False, str(exc)[:120])

    # ④c ezt-core.exe：特权层，靠"能启动并拒绝非法参数"证伪。
    #     它需要 --root 才能真跑，rc≠0 恰好证明 **进程起来了**。
    #     ⚠️ 但 0x80008096（缺运行时）也是非 0 ⇒ 必须靠输出文本区分，
    #     否则框架依赖产物会假绿（第一版就是栽在这里）。
    core = bin_dir / "ezt-core.exe"
    if core.is_file():
        try:
            r = subprocess.run(
                [str(core), "--no-such-arg"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=env,
                timeout=timeout,
                cwd=str(bin_dir),
            )
            out = (r.stdout or "") + (r.stderr or "")
            missing_rt = "install or update .NET" in out
            ck("ezt-core.exe 在隔离环境启动（参数校验可达）",
               not missing_rt and r.returncode != 0, f"rc={r.returncode}")
        except subprocess.TimeoutExpired:
            ck("ezt-core.exe 在隔离环境启动（参数校验可达）", False, "超时")
        except Exception as exc:  # noqa: BLE001
            ck("ezt-core.exe 在隔离环境启动（参数校验可达）", False, str(exc)[:120])

    # ④d ezt-index.exe：★ 最关键的一个 ——
    #     它**按需启动**，缺运行时不会在装包时暴露，只在用户首次搜索时炸。
    #     ⚠️ 判据**不能只看 rc!=0**：apphost 找不到运行时时的退出码是
    #        0x80008096（2147516566），与其他失败码无法从数值区分；
    #        且它在部分形态下会先打印缺运行时再退出 ⇒ **必须查输出文本**。
    #        （这正是本条判据第一版的 bug：rc!=0 被判PASS，而输出里
    #          明写着 "You must install or update .NET" —— 已被反向验证抓出。）
    idx = bin_dir / "ezt-index.exe"
    if idx.is_file():
        try:
            r = subprocess.run(
                [str(idx), "--no-such-arg"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=env,
                timeout=timeout,
                cwd=str(bin_dir),
            )
            out = (r.stdout or "") + (r.stderr or "")
            missing_rt = "install or update .NET" in out
            ck("ezt-index.exe 在隔离环境启动（无缺运行时报错）", not missing_rt,
               f"rc={r.returncode} · {out.strip()[:60]}")
        except subprocess.TimeoutExpired:
            # 超时可能是"启动成功进入 RPC 循环"，不算失败（判据是"无缺运行时报错"）
            print("  [信息] ezt-index.exe 超时（可能进入 RPC 循环）—— 不计失败")
        except Exception as exc:  # noqa: BLE001
            ck("ezt-index.exe 在隔离环境启动（无缺运行时报错）", False, str(exc)[:120])


def main() -> int:
    ap = argparse.ArgumentParser(description="校验发布产物在无 .NET 环境可启动")
    ap.add_argument("--stage", help="解包后的包根目录（含 bin/）")
    ap.add_argument("--zip", help="产物 zip（自动解包后校验）")
    ap.add_argument("--bin", help="指定 bin 子目录名（默认 bin）")
    ap.add_argument("--timeout", type=int, default=60, help="单次启动探测超时（秒）")
    args = ap.parse_args()

    tmp_dir: str | None = None
    try:
        if args.zip:
            if not Path(args.zip).is_file():
                print(f"[FAIL] 找不到 zip：{args.zip}")
                return 1
            tmp_dir = tempfile.mkdtemp(prefix="eztools-verify-")
            print(f"== 解包 {Path(args.zip).name} → 临时目录 ==")
            with zipfile.ZipFile(args.zip) as zf:
                zf.extractall(tmp_dir)
            # zip 内顶层是 <PkgName>/，取它
            entries = [p for p in Path(tmp_dir).iterdir() if p.is_dir()]
            root = entries[0] if len(entries) == 1 else Path(tmp_dir)
            bin_dir = root / (args.bin or "bin")
        elif args.stage:
            bin_dir = Path(args.stage) / (args.bin or "bin")
        else:
            print("[FAIL] 需指定 --stage 或 --zip")
            return 1

        if not bin_dir.is_dir():
            print(f"[FAIL] 找不到 bin 目录：{bin_dir}")
            return 1

        print(f"目标：{bin_dir}")
        print()
        static_checks(bin_dir)
        dynamic_checks(bin_dir, args.timeout)

        print()
        print("=" * 56)
        print(f"  通过 {PASS} / 失败 {FAIL}")
        if FAIL:
            print("  结论：产物**未**达到「解压即用，无需安装 .NET」")
            return 1
        print("  结论：产物为**自包含**，干净环境可启动")
        return 0
    finally:
        if tmp_dir and os.path.isdir(tmp_dir):
            shutil.rmtree(tmp_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())