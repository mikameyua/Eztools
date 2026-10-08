#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ============================================================================
#  w11-acceptance.py —— W11 手工验收清单的自动化面（清单 §A）
#
#  覆盖（对照 docs/W11-手工验收清单.md §0 的 M 项映射）：
#    A0  三出口一致性     —— search.status 五字段在场 + 三档守恒（M 项 0.2）
#    A1  排除真实索引生效 —— 真实 .ezidx 热加载 + --exclude ⇒ 排除对象搜不到、
#                           对照物仍在、状态数字自洽（M1.4/1.5/1.6）
#    A2  数字随规则变化   —— config set/unset ⇒ excludeRules 1→2→0，先存后还（M2.1/2.2/2.4）
#    A3  限定即时生效     —— 同一索引进程内 pathFilter X→Y→"" 三次 start，
#                           查询面随行 —— 证明"不重启不重建即生效"（M3 机器侧，D3）
#    A4  未命中原因可见   —— --exclude no_such_dir_xyz ⇒ stderr 带原因 + excludedFrns==0
#                           + 查询不受影响（M4.1/4.2，§4.4 Reason 纪律）
#    A5  限定未生效       —— 真建一个不在索引里的目录 ⇒ fail-open：结果不裁剪 + 原因可见
#                           （M5 全自动，D8）
#    A6  真实全量重建×2   —— 【opt-in --rebuild-real】临时数据根 + 真卷枚举，
#                           量耗时（供 R6）+ 体积对照（M1.8/1.9）。需提权 Core，默认 SKIP。
#
#  纪律：
#    · 断言落数字与退出码；SKIP 必须计数（G6）；rc = 0 当且仅当 FAIL == 0
#    · 不动用户配置（A2 先存后还，finally 兜底）；删除只针对本脚本自建的临时目录
#    · 索引进程一律 tool.stop 优雅停（等 .ezidx mmap 释放），绝不 kill 了事
#
#  用法：
#    python -I -X utf8 -u scripts/w11-acceptance.py
#    python -I -X utf8 -u scripts/w11-acceptance.py --rebuild-real        # 含真重建（慢）
#    可选：--repo / --install-root / --ezt / --index-exe / --json <path>
# ============================================================================

import argparse
import glob
import json
import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import time

# ── 全局 ────────────────────────────────────────────────────────────────────
RESULTS = []          # (name, status ∈ {PASS,FAIL,SKIP}, detail)
ENV = dict(os.environ)
if not ENV.get("DOTNET_ROOT"):
    # 框架依赖应用的运行时定位：apphost 只看 DOTNET_ROOT 与注册表默认安装位置，**不扫 PATH**。
    # 这里刻意**不写死便携路径**（那是本机约定，不该泄漏给其他克隆者）——
    # 改为从 PATH 上的 dotnet 反推其运行时根目录；仍拿不到就交给调用方显式指定。
    # ⚠️ DOTNET_ROOT 是**含 shared/ 的那层**（= dotnet.exe 的父目录），不是父父目录 ——
    #    apphost 会在 $DOTNET_ROOT/shared/Microsoft.NETCore.App/<ver>/ 下找运行时。
    _dotnet = shutil.which("dotnet")
    if _dotnet:
        ENV["DOTNET_ROOT"] = os.path.dirname(os.path.realpath(_dotnet))


def ck(name, ok, detail=""):
    RESULTS.append((name, "PASS" if ok else "FAIL", detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" —— {detail}" if detail else ""))
    return ok


def sk(name, detail):
    RESULTS.append((name, "SKIP", detail))
    print(f"  [SKIP] {name} —— {detail}")


# ── CLI（ezt.exe）───────────────────────────────────────────────────────────
def run_cli(ezt, cli_args, timeout=120):
    p = subprocess.run([ezt, *cli_args], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=ENV, timeout=timeout)
    return p.returncode, p.stdout, p.stderr


# ── 索引进程（ezt-index.exe，stdio JSON-RPC）────────────────────────────────
class IndexProc:
    """stdIO JSON-RPC 客户端：一行一帧；读线程 + 队列；tool.stop 优雅停。"""

    def __init__(self, exe, data_root, extra_args, stderr_path, cwd):
        self._stderr_f = open(stderr_path, "wb")
        self._p = subprocess.Popen(
            [exe, "--data-root", data_root, *extra_args],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self._stderr_f,
            text=True, encoding="utf-8", errors="replace", env=ENV, cwd=cwd)
        self._q = queue.Queue()
        self._id = 0
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _read_loop(self):
        try:
            for line in self._p.stdout:
                self._q.put(line.rstrip("\n"))
        except Exception:
            pass
        self._q.put(None)   # 流结束哨兵

    def request(self, method, params=None, timeout=60):
        self._id += 1
        rid = self._id
        frame = {"jsonrpc": "2.0", "id": rid, "method": method}
        if params is not None:
            frame["params"] = params
        self._p.stdin.write(json.dumps(frame, ensure_ascii=False) + "\n")
        self._p.stdin.flush()
        deadline = time.time() + timeout
        while True:
            remain = deadline - time.time()
            if remain <= 0:
                raise TimeoutError(f"{method} 无应答（{timeout}s）")
            try:
                line = self._q.get(timeout=min(remain, 5))
            except queue.Empty:
                continue
            if line is None:
                raise ConnectionError(f"{method} 时索引进程输出流已关闭")
            resp = json.loads(line)
            if resp.get("id") == rid:
                if "error" in resp:
                    raise RuntimeError(f"{method} 错误 {resp['error'].get('code')}: "
                                       f"{resp['error'].get('message')}")
                return resp.get("result") or {}

    def wait_ready(self, timeout=90):
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            try:
                last = self.status()
                if last.get("ready"):
                    return last
            except (ConnectionError, json.JSONDecodeError, RuntimeError):
                pass   # 自举期间端点未就绪 —— 继续等（与 CLI 同口径）
            time.sleep(0.4)
        raise TimeoutError(f"索引自举未在 {timeout}s 内就绪（last={last}）")

    def status(self):
        return self.request("search.status", {})

    def query(self, q, limit=200):
        self._qid = getattr(self, "_qid", 0) + 1
        return self.request("search.query",
                            {"q": q, "substr": True, "limit": limit, "epoch": self._qid})

    def start(self, path_filter=None):
        params = None if path_filter is None else {"pathFilter": path_filter}
        return self.request("search.start", params)

    def stop(self, timeout=20):
        try:
            self.request("tool.stop", timeout=10)
        except Exception:
            pass   # 流可能已关 —— 进程退出即达目的
        try:
            self._p.stdin.close()
        except Exception:
            pass
        try:
            self._p.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self._p.kill()
            self._p.wait(timeout=10)
        self._stderr_f.close()
        return self._p.returncode

    def stderr_text(self):
        self._stderr_f.close()
        with open(self._stderr_f.name, encoding="utf-8", errors="replace") as f:
            return f.read()


def has_component(path, component):
    return component.lower() in [seg.lower() for seg in path.replace("/", "\\").split("\\")]


# ── 发现辅助 ────────────────────────────────────────────────────────────────
STALE_SKIPPED = []    # 索引里仍在、磁盘上已不存在的命中（索引只是快照；拿它做 pathFilter 会撞 -32002）


def find_hits(proc, terms, component):
    r"""按候选词依次查询，返回 (component_hits, other_hits)：前者 = 路径含该目录分量的命中，
    后者 = 不含的命中（作对照物）。

    命中再过滤一遍**磁盘存在性**：索引是快照，已删除的条目可能还留在 .ezidx 里（本机 10-02
    的索引里 C:\Users\ishe\.dsh\profiles\node_modules 已删却仍排第一，直接喂给 A3 的
    pathFilter 就会撞 search.start -32002）。全部超阈值/无命中/命中全陈旧 ⇒ ([], [])。"""
    for term in terms:
        try:
            r = proc.query(term)
        except (RuntimeError, TimeoutError, ConnectionError):
            continue
        hits = r.get("hits") or []
        if not hits:
            continue
        comp, other = [], []
        for h in hits:
            p = h.get("path", "")
            if not p or not os.path.exists(p):
                if p:
                    STALE_SKIPPED.append(p)
                continue
            (comp if has_component(p, component) else other).append(h)
        if comp:
            return comp, other
    return [], []


# ── 主流程 ──────────────────────────────────────────────────────────────────
def main():
    ap = argparse.ArgumentParser(description="W11 手工验收清单的自动化面")
    here = os.path.dirname(os.path.abspath(__file__))
    ap.add_argument("--repo", default=os.path.dirname(here))
    ap.add_argument("--install-root",
                    default=os.path.join(os.environ.get("LOCALAPPDATA", ""), "Eztools"))
    ap.add_argument("--ezt", default=None)
    ap.add_argument("--index-exe", default=None)
    ap.add_argument("--rebuild-real", action="store_true",
                    help="追加 A6：真全量重建×2（临时数据根，需提权 Core，分钟级）")
    ap.add_argument("--json", default=None, help="结构化结果落盘路径")
    args = ap.parse_args()

    repo = args.repo
    tfm = "net10.0-windows10.0.19041.0"
    ezt = args.ezt or os.path.join(repo, "src", "Eztools.Cli", "bin", "Debug", tfm, "ezt.exe")
    index_exe = args.index_exe or os.path.join(
        repo, "src", "Eztools.Index", "bin", "Debug", "net10.0", "ezt-index.exe")
    install_root = args.install_root
    index_dir = os.path.join(install_root, "index")
    scratch = os.path.join(repo, "_scratch", "w11-accept")
    os.makedirs(scratch, exist_ok=True)
    stamp = time.strftime("%H%M%S")

    print("=" * 68)
    print("  W11 验收自动化（docs/W11-手工验收清单.md §A）")
    print(f"  repo={repo}")
    print(f"  install-root={install_root}")
    print("=" * 68)

    # ── 前置 ──
    if not os.path.isfile(ezt):
        ck("前置：ezt.exe 存在", False, f"未找到 {ezt}（先构建或用 --ezt 指定）")
        return 1
    if not os.path.isfile(index_exe):
        ck("前置：ezt-index.exe 存在", False, f"未找到 {index_exe}")
        return 1
    ck("前置：ezt / ezt-index 就位", True, f"{os.path.basename(ezt)} / {os.path.basename(index_exe)}")

    ezidx_files = sorted(glob.glob(os.path.join(index_dir, "*.ezidx")))
    has_index = len(ezidx_files) > 0
    if has_index:
        total_mb = sum(os.path.getsize(f) for f in ezidx_files) // (1024 * 1024)
        ck("前置：真实索引在场（热加载可用，零提权）", True,
           f"{len(ezidx_files)} 个 .ezidx，共 {total_mb} MB")
    else:
        sk("前置：真实索引在场", f"{index_dir} 下无 .ezidx —— A1/A3/A5 跳过（A2/A4 不依赖索引）")

    control = {"path": None, "leaf": None}    # 对照物（跨检查复用）
    component_hit = {"path": None, "leaf": None}   # 排除目标

    # ── A0 status 出口一致性（M 前置 0.2）──
    rc, out, err = run_cli(ezt, ["search", "status", "--json", "--compact", "--wait-ready", "15000"])
    ok = rc == 0
    st = {}
    if ok:
        try:
            st = json.loads(out.strip().splitlines()[-1])
        except (ValueError, IndexError):
            ok = False
    keys = ["excludeRules", "excludedFrns", "pathFilter", "pathFilterAnchored", "pathFilterReason"]
    ok = ok and all(k in st for k in keys) and st.get("conserved") is True
    ck("A0 search.status --json 五字段在场 + 三档守恒", ok,
       f"excludeRules={st.get('excludeRules')} conserved={st.get('conserved')} rc={rc}")
    indexed_volumes = st.get("volumes") or []

    # ── A1 排除真实索引生效（M1.4/1.5/1.6）──
    if has_index:
        idx1 = IndexProc(index_exe, install_root, [], os.path.join(scratch, f"a1a-{stamp}.err"), repo)
        try:
            idx1.wait_ready()
            comp, other = find_hits(idx1, ["node_modules", ".git"], "node_modules")
            if not comp:
                comp, other = find_hits(idx1, ["node_modules", ".git"], ".git")
            if not comp:
                sk("A1 排除真实索引生效", "索引里既无 node_modules 也无 .git 命中 —— 换环境或手工 M1")
            else:
                target = "node_modules" if has_component(comp[0]["path"], "node_modules") else ".git"
                component_hit.update(path=comp[0]["path"], leaf=comp[0]["name"])
                if other:
                    control.update(path=other[0]["path"], leaf=other[0]["name"])
                stale_note = (f"；已跳过 {len(STALE_SKIPPED)} 个磁盘上已不存在的索引条目"
                              f"（索引是快照：已删/不可见，如 {STALE_SKIPPED[0]}）" if STALE_SKIPPED else "")
                ck("A1 发现排除目标与对照物", True,
                   f"目标={component_hit['path']} 对照={control['path'] or '（本轮无，跳过对照断言）'}"
                   + stale_note)

                stop_rc = idx1.stop()
                idx2 = IndexProc(index_exe, install_root, ["--exclude", target],
                                 os.path.join(scratch, f"a1b-{stamp}.err"), repo)
                try:
                    idx2.wait_ready()
                    st2 = idx2.status()
                    r = idx2.query(component_hit["leaf"])
                    paths = [h.get("path", "") for h in r.get("hits") or []]
                    victim_gone = all(not has_component(p, target) for p in paths)
                    ck("A1 排除后：排除目标**搜不到**（M1.4 主判据）", victim_gone,
                       f"查询「{component_hit['leaf']}」共 {len(paths)} 条命中，含排除分量的 0 条"
                       if victim_gone else f"仍命中：{paths[:3]}")
                    if control["path"]:
                        r2 = idx2.query(control["leaf"])
                        paths2 = [h.get("path", "") for h in r2.get("hits") or []]
                        ck("A1 排除后：对照物**照常搜到**（正向对照，R5）",
                           control["path"] in paths2,
                           f"对照 {control['path']} {'在场' if control['path'] in paths2 else '失踪'}")
                    ck("A1 状态自洽：excludeRules==1 且 excludedFrns>0（M1.6）",
                       st2.get("excludeRules") == 1 and (st2.get("excludedFrns") or 0) > 0,
                       f"excludeRules={st2.get('excludeRules')} excludedFrns={st2.get('excludedFrns')}")
                finally:
                    idx2.stop()
        finally:
            try:
                if idx1._p.poll() is None:
                    idx1.stop()
            except Exception:
                pass
    else:
        sk("A1 排除真实索引生效", "无真实索引（见前置）")

    # ── A2 数字随规则变化（M2.1/2.2/2.4 的自动部分）──
    rc, out, _ = run_cli(ezt, ["config", "get", "desktop", "index.exclude", "--json"])
    orig = None
    if rc == 0:
        try:
            v = json.loads(out.strip().splitlines()[-1])
            orig = v if isinstance(v, str) and v.strip() else None
        except (ValueError, IndexError):
            orig = None
    try:
        seq = [("node_modules", 1), ("node_modules;.git", 2)]
        a2_ok = True
        detail = []
        for raw, want in seq:
            run_cli(ezt, ["config", "set", "desktop", "index.exclude", raw])
            rc, out, _ = run_cli(ezt, ["search", "status", "--json", "--compact", "--wait-ready", "15000"])
            got = None
            try:
                got = json.loads(out.strip().splitlines()[-1]).get("excludeRules")
            except (ValueError, IndexError):
                a2_ok = False
            a2_ok = a2_ok and got == want
            detail.append(f"{raw}⇒{got}(期望{want})")
        run_cli(ezt, ["config", "unset", "desktop", "index.exclude"])
        rc, out, _ = run_cli(ezt, ["search", "status", "--json", "--compact", "--wait-ready", "15000"])
        got = None
        try:
            got = json.loads(out.strip().splitlines()[-1]).get("excludeRules")
        except (ValueError, IndexError):
            a2_ok = False
        a2_ok = a2_ok and got == 0
        detail.append(f"unset⇒{got}(期望0)")
        ck("A2 排除规则数随配置变化 1→2→0（M2 自动部分，R4）", a2_ok, "；".join(detail))
    finally:
        # 先存后还：恢复用户原值（没有就 unset 回默认）
        if orig:
            run_cli(ezt, ["config", "set", "desktop", "index.exclude", orig])
        else:
            run_cli(ezt, ["config", "unset", "desktop", "index.exclude"])

    # ── A3 / A5：限定（pathFilter）——同一进程内即时生效 + fail-open ──
    if has_index:
        idx3 = IndexProc(index_exe, install_root, [], os.path.join(scratch, f"a3-{stamp}.err"), repo)
        try:
            idx3.wait_ready()
            if component_hit["path"] is None:
                comp, other = find_hits(idx3, ["node_modules", ".git"], "node_modules")
                if comp:
                    component_hit.update(path=comp[0]["path"], leaf=comp[0]["name"])
                if other:
                    control.update(path=other[0]["path"], leaf=other[0]["name"])

            if component_hit["path"] is None:
                sk("A3 限定即时生效", "索引里无可用的限定目标（同 A1 前置）")
            else:
                x_dir = component_hit["path"]           # 限定目标 = 排除目标同款（目录自身在索引里）
                # X = 排除目标目录自身；Y = 对照物的父目录（另一个子树）
                y_dir = os.path.dirname(control["path"]) if control["path"] else None

                ack = idx3.start(path_filter=x_dir)
                ck("A3 限定 X ⇒ 锚定成功（不重启，D3）", ack.get("pathFilterAnchored") is True,
                   f"X={x_dir} reason={ack.get('pathFilterReason')}")
                r = idx3.query(component_hit["leaf"])
                paths = [h.get("path", "") for h in r.get("hits") or []]
                ck("A3 限定 X ⇒ X 自身仍可见（限定根本身在子树内）",
                   x_dir in paths,
                   f"查询「{component_hit['leaf']}」命中 {len(paths)} 条，X 在场={x_dir in paths}")

                if y_dir and y_dir != os.path.dirname(x_dir):
                    ack2 = idx3.start(path_filter=y_dir)
                    ok2 = ack2.get("pathFilterAnchored") is True
                    r2 = idx3.query(component_hit["leaf"])
                    paths2 = [h.get("path", "") for h in r2.get("hits") or []]
                    x_absent = all(not has_component(p, "node_modules") and
                                   not has_component(p, ".git") for p in paths2) or x_dir not in paths2
                    ck("A3 换限定 Y（同一进程）⇒ X 消失（**即时生效** = M3 机器侧主判据）",
                       ok2 and x_absent,
                       f"Y={y_dir} 查询命中 {len(paths2)} 条，X 在场={x_dir in paths2}")
                    if control["path"]:
                        r3 = idx3.query(control["leaf"])
                        paths3 = [h.get("path", "") for h in r3.get("hits") or []]
                        ck("A3 限定 Y ⇒ Y 子树内的对照物照常可见",
                           control["path"] in paths3, f"对照在场={control['path'] in paths3}")

                ack3 = idx3.start(path_filter="")
                st_clear = idx3.status()
                r4 = idx3.query(component_hit["leaf"])
                paths4 = [h.get("path", "") for h in r4.get("hits") or []]
                ck("A3 空串清除 ⇒ X 恢复可见（清除语义）",
                   st_clear.get("pathFilter") == "" and x_dir in paths4,
                   f"status.pathFilter={st_clear.get('pathFilter')!r}（期望空），清除后 X 在场={x_dir in paths4}")

            # ── A5 限定未生效（fail-open，M5 全自动）──
            m5_dir = os.path.join(install_root, f"w11-m5-{stamp}")
            os.makedirs(m5_dir, exist_ok=True)
            try:
                ack5 = idx3.start(path_filter=m5_dir)
                reason = ack5.get("pathFilterReason") or ""
                ck("A5 磁盘存在但索引没有 ⇒ **fail-open**：不锚定 + 原因可见（M5 主判据，D8）",
                   ack5.get("pathFilterAnchored") is False and "找不到" in reason,
                   f"anchored={ack5.get('pathFilterAnchored')} reason={reason[:60]}")
                if control["path"]:
                    r5 = idx3.query(control["leaf"])
                    paths5 = [h.get("path", "") for h in r5.get("hits") or []]
                    ck("A5 结果**不被静默裁剪**（限定未生效时范围外文件照常搜到）",
                       control["path"] in paths5, f"对照在场={control['path'] in paths5}")
            finally:
                shutil.rmtree(m5_dir, ignore_errors=True)
        finally:
            idx3.stop()
    else:
        sk("A3 限定即时生效", "无真实索引（见前置）")
        sk("A5 限定未生效 fail-open", "无真实索引（见前置）")

    # ── A4 未命中规则原因可见（M4.1/4.2）──
    idx4 = IndexProc(index_exe, install_root, ["--exclude", "no_such_dir_xyz"],
                     os.path.join(scratch, f"a4-{stamp}.err"), repo)
    try:
        idx4.wait_ready()
        st4 = idx4.status()
        err4 = idx4.stderr_text()
        ok4 = ("规则未匹配到任何目录" in err4
               and st4.get("excludeRules") == 1 and (st4.get("excludedFrns") or 0) == 0)
        ck("A4 规则未命中任何目录 ⇒ **说得出原因**（不是\"排除 0 条\"了事，M4.2）", ok4,
           f"stderr 含原因={'规则未匹配到任何目录' in err4} "
           f"excludeRules={st4.get('excludeRules')} excludedFrns={st4.get('excludedFrns')}")
        if control["path"]:
            r6 = idx4.query(control["leaf"])
            paths6 = [h.get("path", "") for h in r6.get("hits") or []]
            ck("A4 未命中规则**不影响**正常结果（fail-open，不隐藏任何东西）",
               control["path"] in paths6, f"对照在场={control['path'] in paths6}")
    finally:
        idx4.stop()

    # ── A6 真实全量重建×2（opt-in；供 R6 的耗时 + 体积对照）──
    if args.rebuild_real:
        rc, out, _ = run_cli(ezt, ["core", "status", "--json"])
        core_running = False
        try:
            core_running = json.loads(out.strip().splitlines()[-1]).get("running") is True
        except (ValueError, IndexError, AttributeError):
            core_running = False
        if not core_running:
            sk("A6 真实全量重建×2", "Core 未运行（`ezt core start --elevate` 后重跑；或保留人工 B2）")
        else:
            rb_root = os.path.join(scratch, "rebuild-root")
            sizes_times = []
            for tag, extra in (("无规则", ["--volumes", "C:"]),
                               ("带排除", ["--volumes", "C:", "--exclude", "node_modules"])):
                idx_dir = os.path.join(rb_root, "index")
                if os.path.isdir(idx_dir):
                    shutil.rmtree(idx_dir, ignore_errors=True)
                sw = time.time()
                proc = IndexProc(index_exe, rb_root, extra,
                                 os.path.join(scratch, f"a6-{tag}-{stamp}.err"), repo)
                try:
                    proc.wait_ready(timeout=1200)
                    elapsed = time.time() - sw
                    sz = sum(os.path.getsize(f) for f in glob.glob(os.path.join(idx_dir, "*.ezidx")))
                    sizes_times.append((tag, elapsed, sz))
                    print(f"  [信息] A6 {tag}：全量重建 {elapsed:.1f}s，.ezidx {sz // (1024*1024)} MB（供 R6）")
                finally:
                    proc.stop()
            if len(sizes_times) == 2:
                ck("A6 真实重建：带排除规则的 .ezidx **严格小于**无规则（真实数据上的 R11）",
                   sizes_times[1][2] < sizes_times[0][2],
                   f"无规则={sizes_times[0][2]//(1024*1024)}MB/{sizes_times[0][1]:.0f}s · "
                   f"带排除={sizes_times[1][2]//(1024*1024)}MB/{sizes_times[1][1]:.0f}s")
            else:
                sk("A6 真实重建体积对照", "两次重建未都成功（见上方信息行）")
            shutil.rmtree(rb_root, ignore_errors=True)
    else:
        sk("A6 真实全量重建×2", "未加 --rebuild-real（默认跳过：分钟级且需提权 Core；耗时记录留给人工 B2）")

    # ── 汇总 ──
    n_pass = len([r for r in RESULTS if r[1] == "PASS"])
    n_fail = len([r for r in RESULTS if r[1] == "FAIL"])
    n_skip = len([r for r in RESULTS if r[1] == "SKIP"])
    print("=" * 68)
    print(f"  W11 验收自动化：PASS {n_pass} · FAIL {n_fail} · SKIP {n_skip}"
          f"（SKIP 为环境项计数，不影响 rc）")
    print(f"  判据：FAIL == 0 即通过；rc = {0 if n_fail == 0 else 1}")
    print("=" * 68)

    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({"pass": n_pass, "fail": n_fail, "skip": n_skip,
                       "rc": 0 if n_fail == 0 else 1,
                       "results": [{"name": n, "status": s, "detail": d} for n, s, d in RESULTS]},
                      f, ensure_ascii=False, indent=1)
    return 0 if n_fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
