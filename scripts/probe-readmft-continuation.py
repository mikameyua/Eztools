#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
probe-readmft-continuation.py —— W3-a-3 验收① 的提权正例手工探针。

验收目标（docs/W3-极速文件搜索-分步实施计划.md W3-a-3 验收①）：
  真卷（默认 C:）全量枚举 > 10000 条、多批游标续传、FRN 无重复（重放消化除外）。

链路：本脚本 → `ezt primitive volume.readMft`（与工具完全相同的 PrimitiveClient → 管道 → Core）。
selftest 已用假 reader 验证过 VolumeWorker 的消化逻辑；本探针验证的是**真卷 + 真提权 + 真游标**。

用法（普通终端即可，推荐路径 A）：
  set DOTNET_ROOT=D:\\dotnet10
  src\\Eztools.Cli\\bin\\Debug\\net10.0-windows10.0.19041.0\\ezt.exe core start --elevate   ← UAC 确认，启动提权 Core
  src\\Eztools.Cli\\bin\\Debug\\net10.0-windows10.0.19041.0\\ezt.exe core status            ← 确认 提权=True
  python scripts\\probe-readmft-continuation.py

验证真实契约（不透明的续传令牌，不保证单调）：
  python scripts\\probe-readmft-continuation.py; echo $?
  （退出码 0 = PASS；用 echo $? 读，别接在管道后面——tail 会吃掉退出码）

退出码：0 = PASS；1 = 断言失败；2 = 用法/环境错误；3 = 未提权（-32015）。
"""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

ELEVATION_CODE = -32015
CORE_NOT_RUNNING_CODE = -32017


def log(msg: str) -> None:
    print(msg, flush=True)


def call_readmft(ezt: str, volume: str, cursor: int | None, max_records: int, timeout: int):
    args_json = {"volume": volume, "maxRecords": max_records}
    if cursor is not None:
        args_json["cursor"] = cursor
    proc = subprocess.run(
        [ezt, "primitive", "volume.readMft", "--json", "--compact",
         "--json", json.dumps(args_json), "--timeout", str(timeout)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    try:
        payload = json.loads(proc.stdout)
    except json.JSONDecodeError:
        payload = None
    return proc.returncode, payload, proc.stderr


def main() -> int:
    if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]

    repo = Path(__file__).resolve().parent.parent
    default_ezt = repo / "src" / "Eztools.Cli" / "bin" / "Debug" / "net10.0-windows10.0.19041.0" / "ezt.exe"

    ap = argparse.ArgumentParser(description="readMft 游标续传真卷探针（W3-a-3 验收①）")
    ap.add_argument("--ezt", default=str(default_ezt), help="ezt.exe 路径")
    ap.add_argument("--volume", default="C:", help="目标卷（默认 C:）")
    ap.add_argument("--max-records", type=int, default=5000,
                    help="每批条数（1~5000，primitive 硬上限 5000）")
    ap.add_argument("--min-unique", type=int, default=10000,
                    help="唯一 FRN 数下限（验收口径：> 10000 条）")
    ap.add_argument("--max-batches", type=int, default=2000, help="批次安全上限")
    ap.add_argument("--timeout", type=int, default=60, help="单批调用超时（秒）")
    ns = ap.parse_args()

    ezt = Path(ns.ezt)
    if not ezt.is_file():
        log(f"[环境错误] 找不到 {ezt} —— 先构建（DOTNET_ROOT=D:/dotnet10 时 "
            f"dotnet build Eztools.sln --no-incremental）或用 --ezt 指定已安装路径")
        return 2

    log(f"探针开始：volume={ns.volume} maxRecords={ns.max_records} ezt={ezt}")
    log("首批调用（无游标）……")

    cursor = None
    unique: set[int] = set()
    last_frn: int | None = None          # 上一批最后一条 FRN（重放消化判据）
    batches = total = replays = non_empty_batches = 0
    bad_dups: list[int] = []
    started = time.perf_counter()

    while True:
        rc, payload, stderr = call_readmft(
            str(ezt), ns.volume, cursor, ns.max_records, ns.timeout)
        batches += 1

        if payload is None:
            log(f"[FAIL] 第 {batches} 批输出不是 JSON（rc={rc}）stderr={stderr[:300]}")
            return 1
        if rc != 0 or "error" in payload:
            err = payload.get("error", {})
            code = err.get("code")
            if code in (ELEVATION_CODE, CORE_NOT_RUNNING_CODE):
                log(f"[需提权] Core 报 {code}：{err.get('message', '')}")
                log("  两条解决路径（推荐 A）：")
                log("  A. 只提权 Core：本终端执行  ezt core start --elevate  （UAC 确认），")
                log("     然后 ezt core status 确认 提权=True，再重跑本脚本；")
                log("  B. 整个终端提权：以管理员身份重开终端后重跑本脚本。")
                return 3
            log(f"[FAIL] 第 {batches} 批调用失败 rc={rc} error={err}")
            return 1

        records = payload.get("records") or []
        count = payload.get("count", len(records))
        done = bool(payload.get("done"))
        cursor_out = payload.get("cursor")

        if len(records) != count:
            log(f"[FAIL] 第 {batches} 批 count={count} 与 records 实长 {len(records)} 不一致")
            return 1
        if records:
            non_empty_batches += 1

        # ① FRN 判重：批首与上一批末条相同 = 重放消化（允许，计数）；其余重复 = 真失败
        for i, rec in enumerate(records):
            frn = rec["frn"]
            total += 1
            if i == 0 and batches > 1 and frn == last_frn:
                replays += 1
                continue
            if frn in unique:
                bad_dups.append(frn)
            unique.add(frn)

        # ② 游标 = **不透明续传令牌**（截断批 = 最后一条记录完整 FRN 含序列号位；
        #    整批消费 = 系统缓冲区边界）—— 两种格式、不保证跨批单调（2026-09-24 实测），
        #    所以这里不断言递增，只断言"在场"：未 done 必须给出 cursor（下方统一检查）。
        #    正确性由 FRN 判重（无漏无重）+ 批次上限（防停滞）兜住。

        if batches % 20 == 0 or done:
            log(f"  批 {batches}: count={count} done={done} "
                f"累计唯一 {len(unique)}（重放消化 {replays}）"
                f" {time.perf_counter() - started:.0f}s")

        if bad_dups:
            log(f"[FAIL] 第 {batches} 批出现非重放重复 FRN（前 5 个）：{bad_dups[:5]}")
            return 1

        if done:
            break
        if not records and cursor_out is None:
            log(f"[FAIL] 第 {batches} 批空记录且无游标（协议破坏）")
            return 1
        if batches > ns.max_batches:
            log(f"[FAIL] 超过批次安全上限 {ns.max_batches}（游标疑似停滞）")
            return 1
        cursor = cursor_out
        last_frn = records[-1]["frn"] if records else last_frn
        if cursor is None:
            log(f"[FAIL] 第 {batches} 批未 done 且未给 cursor（协议破坏）")
            return 1

    elapsed = time.perf_counter() - started
    log("─" * 60)
    log(f"批数 {batches} · 记录总数 {total} · 唯一 FRN {len(unique)} · "
        f"重放消化 {replays} · 非重放重复 0 · 耗时 {elapsed:.1f}s "
        f"({total / max(elapsed, 0.001):.0f} 条/s)")

    if len(unique) <= ns.min_unique:
        log(f"[FAIL] 唯一 FRN {len(unique)} <= 下限 {ns.min_unique}"
            f"（验收口径 > 10000）—— 换更大的卷或调低 --min-unique")
        return 1
    # 重放消化是信息统计不是判据：实测（2026-09-24，C: 153 万条 307 批）内核对
    # StartFileReferenceNumber 起始记录**不重放**，primitive 的 isReplay 分支不触发；
    # primitive 注释本就声明"两种语义都成立"（有重放消化 / 无重放直接继续）。
    log(f"[PASS] W3-a-3 验收①：真卷 {ns.volume} 多批续传无重复，"
        f"唯一 FRN {len(unique)} > {ns.min_unique}（重放消化 {replays}，信息统计）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
