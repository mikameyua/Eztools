#!/usr/bin/env bash
# ============================================================================
#  _step12_p3.sh —— P3 特权层验收（由 acceptance.sh source 进来）
#
#  前置：acceptance.sh 已定义 PASS/FAIL/pass/fail/skip/step、$EZ、$WORK，
#        且已 export EZTOOLS_INSTALL_ROOT / EZTOOLS_CONFIG_ROOT。
#  范围：非提权可自动验证的部分（提权项见 P3 方案 §8 手工清单）。
# ============================================================================

step "12/18  P3 特权层：Core 生命周期 · 原语 · 声明门槛 · 审计 · 全链路"

# python：JSON 断言用（沿用 budget.sh 的探测顺序）
PY="$(command -v python || command -v python3 || true)"
if [ -z "$PY" ] && [ -f "$REPO/spike/_runtime-test/python/python.exe" ]; then
  PY="$REPO/spike/_runtime-test/python/python.exe"
fi

CORE_OUT="$WORK/p3-core.json"
call_core() {  # call_core <输出文件> <参数...>：把 stdout 与退出码分开收
  local out="$1"; shift
  "$@" > "$out" 2>&1
  return $?
}

# ── 12.1 status 如实报告"未运行"（不是报错）─────────────────────────────────
call_core "$CORE_OUT" "$EZ" core status
rc=$?
if [ $rc -eq 0 ] && grep -F "Core 未运行" "$CORE_OUT" >/dev/null 2>&1; then
  pass "core status 未运行时如实报告（退出 0）"
else
  fail "core status 未运行时应退出 0 并报告未运行（rc=$rc）"
fi

# ── 12.2 core start ─────────────────────────────────────────────────────────
call_core "$CORE_OUT" "$EZ" core start
rc=$?
if [ $rc -eq 0 ] && grep -F "Core 已启动" "$CORE_OUT" >/dev/null 2>&1; then
  pass "core start 启动 Core（非提权形态）"
else
  fail "core start 失败（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi

# ── 12.3 ping ───────────────────────────────────────────────────────────────
call_core "$CORE_OUT" "$EZ" core ping --json
rc=$?
if [ $rc -eq 0 ] && grep -F '"pong":true' "$CORE_OUT" >/dev/null 2>&1; then
  pass "core ping 往返成功（pong）"
else
  fail "core ping 失败（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi

# ── 12.4 process.enumerate ──────────────────────────────────────────────────
call_core "$CORE_OUT" "$EZ" primitive process.enumerate --compact --json
rc=$?
if [ $rc -eq 0 ] && [ -n "$PY" ] && "$PY" - "$CORE_OUT" <<'PYEOF' 2>/dev/null
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
assert d["count"] >= 10, f"count={d['count']}"
assert isinstance(d["processes"], list)
PYEOF
then
  pass "process.enumerate 返回 ≥10 个进程（JSON 结构合法）"
else
  fail "process.enumerate 断言失败（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi

# ── 12.5 process.terminate 守卫：杀 Core 自身被拒绝（-32018）────────────────
CORE_PID=""
# core.json 落在安装根（$WORK/install），直接读
CORE_PID=$("$PY" - "$WORK/install/core.json" <<'PYEOF' 2>/dev/null
import json, sys
try:
    print(json.load(open(sys.argv[1], encoding="utf-8"))["pid"])
except Exception:
    print("")
PYEOF
)
call_core "$CORE_OUT" "$EZ" primitive process.terminate --json "{\"pid\":$CORE_PID}"
rc=$?
if [ $rc -eq 2 ] && grep -F '"code":-32018' "$CORE_OUT" >/dev/null 2>&1; then
  pass "process.terminate 拒绝结束 Core 自身（-32018，退出码 2）"
else
  fail "process.terminate 守卫未按预期拒绝（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi

# ── 12.6 handles.enumerate：Core 自查必须非空（pid 过滤指向 Core 自己，
#        其管道/日志句柄必然存在 —— "≥0 结构合法"的弱断言会放过全零的布局错误）──
call_core "$CORE_OUT" "$EZ" primitive handles.enumerate --compact --json "{\"pid\":$CORE_PID}"
rc=$?
if [ $rc -eq 0 ] && [ -n "$PY" ] && "$PY" - "$CORE_OUT" "$CORE_PID" <<'PYEOF' 2>/dev/null
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
assert d["count"] >= 1 and isinstance(d["handles"], list) and len(d["handles"]) == d["count"]
for h in d["handles"]:
    assert h["pid"] == int(sys.argv[2]) and h["type"] == "File" and h["path"], h
PYEOF
then
  pass "handles.enumerate Core 自查非空（count≥1，pid/type/path 结构正确）"
else
  fail "handles.enumerate 断言失败（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi

# ── 12.6b ★ 连续扫描必须稳定（RI-6 反向断言，2026-09-29 安全面专项审）─────────
# 背景（报告 §六）：挂住的扫描线程永远走不到 finally ⇒ DuplicateHandle 出来的副本
#   **漏在 Core 里**，而该副本本身又是下一轮扫描的候选 ⇒ **自增强**；
#   同时被放弃的 worker 会连同它的 OpenProcess 缓存一起漏掉。
#   修复前实测同一 Core 连续调用：耗时 5.2 → 10.1 → 21.8 s（逐次翻倍，第 4 次起
#   100% 超时 -32011），Core 句柄数 273 → 458 → 700 → 1087。
# 修法 = 记住已挂起的 (pid, handle) 并跳过 + 看门狗代关被放弃 worker 的副本。
# 本条断言"增长被切断"（三条判据各自能红）：
#   ① 第 3 次耗时 ≤ 第 1 次 × 2 + 1000 ms（修复前 21.8 > 5.2×2+1 = 11.4 ⇒ 必红）
#   ② 响应里必须带 skippedStalled / stalledObjects（调用方要能看到"已知的数据缺失"）
#   ③ 黑名单规模不得倒退（防"每轮清空" ⇒ 机制名存实亡）
# ⚠️ ③ 只在"本机确实存在会让 NtQueryObject 挂起的句柄"时才真正咬人（那种机器上
#    黑名单规模 > 0）；没有这种句柄的机器上两条都是 0，它恒真 —— 见报告 §6.7 的说明。
P3_TRACE="$WORK/install/logs/core-trace.log"
: > "$P3_TRACE" 2>/dev/null || true     # 只用本轮三条读数，免旧行干扰
for i in 1 2 3; do
  call_core "$WORK/p3-hstab$i.json" "$EZ" primitive handles.enumerate --timeout 200 --compact --json
done

P3_DUR="$("$PY" -I -X utf8 -c "
import re, sys
txt = open(sys.argv[1], encoding='utf-8', errors='replace').read()
ms = [int(m) for m in re.findall(r'scan end: ms=([0-9]+)', txt)]
print('|'.join(str(x) for x in ms[-3:]) if len(ms) >= 3 else 'SHORT')
" "$(winpath "$P3_TRACE")" 2>/dev/null | tr -d '\r\n')"
IFS='|' read -r P3_D1 P3_D2 P3_D3 <<< "$P3_DUR"
STAB=0
if [ -n "${P3_D1:-}" ] && [ -n "${P3_D3:-}" ] && [ "$P3_D1" != "SHORT" ]; then
  if [ "$P3_D3" -le $(( P3_D1 * 2 + 1000 )) ]; then STAB=1; fi
fi
check "★ 连续三次扫描耗时不再增长（RI-6 自增强泄漏已被切断）" 1 "$STAB"
if [ "$STAB" -eq 0 ]; then
  printf '        实测：D1=%s D2=%s D3=%s（判据 D3 ≤ D1×2+1000）\n' "${P3_D1:-?}" "${P3_D2:-?}" "${P3_D3:-?}"
fi

P3_FIELDS="$("$PY" -I -X utf8 -c "
import json, sys
def rd(p):
    d = json.load(open(p, encoding='utf-8-sig'))
    return d.get('skippedStalled'), d.get('stalledObjects')
try:
    s2, k2 = rd(sys.argv[1])
    s3, k3 = rd(sys.argv[2])
except Exception:
    print('0|0'); sys.exit(0)
shaped = all(isinstance(v, int) and v >= 0 for v in (s2, k2, s3, k3))
print('%d|%d' % (1 if shaped else 0, 1 if (shaped and k3 >= k2) else 0))
" "$(winpath "$WORK/p3-hstab2.json")" "$(winpath "$WORK/p3-hstab3.json")" 2>/dev/null | tr -d '\r\n')"
IFS='|' read -r P3_HAVE P3_MONO <<< "$P3_FIELDS"
check "RI-6 响应带 skippedStalled / stalledObjects 且为非负整数（数据缺失可见）" 1 "${P3_HAVE:-0}"
check "RI-6 黑名单规模不倒退（防每轮清空 ⇒ 机制名存实亡）" 1 "${P3_MONO:-0}"

# ── 12.7 volume.enumerate ───────────────────────────────────────────────────
call_core "$CORE_OUT" "$EZ" primitive volume.enumerate --compact --json
rc=$?
if [ $rc -eq 0 ] && [ -n "$PY" ] && "$PY" - "$CORE_OUT" <<'PYEOF' 2>/dev/null
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
assert d["count"] >= 1
assert d["elevated"] is False, "非提权 Core 应如实报告 elevated=false"
PYEOF
then
  pass "volume.enumerate 返回 ≥1 个卷（elevated=false 如实）"
else
  fail "volume.enumerate 断言失败（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi

# ── 12.8 volume.readMft 未提权 → 结构化 -32015（而非崩溃/超时）───────────────
call_core "$CORE_OUT" "$EZ" primitive volume.readMft --json '{"volume":"C:"}'
rc=$?
if [ $rc -eq 2 ] && grep -F '"code":-32015' "$CORE_OUT" >/dev/null 2>&1; then
  pass "volume.readMft 未提权返回结构化 ElevationRequired（-32015）"
else
  fail "volume.readMft 未提权行为异常（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi

# ── 12.9 普通调用不受特权层影响 ─────────────────────────────────────────────
call_core "$CORE_OUT" "$EZ" invoke echo.echo --text p3-ok
rc=$?
if [ $rc -eq 0 ]; then
  pass "普通工具调用不受特权层影响（echo 正常返回）"
else
  fail "echo 调用异常（rc=$rc）"
fi

# ── 12.10 pinfo 端到端：工具 → 宿主 → Core → 工具 ───────────────────────────
# invoke 的输出带控制台横幅（非纯 JSON），python 从第一个 "{" 起解析
call_core "$CORE_OUT" "$EZ" invoke pinfo.list --compact --timeout 60000
rc=$?
if [ $rc -eq 0 ] && [ -n "$PY" ] && "$PY" - "$CORE_OUT" <<'PYEOF' 2>/dev/null
import json, sys
t = open(sys.argv[1], encoding="utf-8").read()
d = json.loads(t[t.find("{"):])
assert d["ok"] is True, d
assert d["total"] >= 10, d.get("total")
PYEOF
then
  pass "pinfo 端到端：SDK primitive() → 宿主 → Core → 工具（total ≥10）"
else
  fail "pinfo 端到端失败（rc=$rc）：$(head -c 300 "$CORE_OUT")"
fi

# ── 12.11 审计日志：存在且包含本次 process.enumerate 调用 ────────────────────
AUDIT_FILE="$(ls "$WORK/install/logs/"audit-*.log 2>/dev/null | tail -1)"
if [ -n "$AUDIT_FILE" ] \
   && grep -F '"primitive":"process.enumerate"' "$AUDIT_FILE" >/dev/null 2>&1 \
   && grep -F '"toolId":"pinfo"' "$AUDIT_FILE" >/dev/null 2>&1; then
  pass "审计日志存在且记录了 pinfo 的 process.enumerate 调用"
else
  fail "审计日志缺失或未记录调用（$AUDIT_FILE）"
fi

# ── 12.11b ★ 审计降级必须**可见**（RI-1 反向断言，2026-09-29 安全面专项审）──────
# 背景（docs/代码审查报告-安全面专项-2026-09-29.md 的 RI-1）：
#   AuditLog 的降级告警原先只走 stdout，而**提权 Core 的 stdout 无人接收**
#   （CoreCommand.cs：提权只能走 ShellExecute，该处代码注释原文"ShellExecute 会把它吞掉"）
#   ⇒ 审计可用性降级在过去是**完全静默**的。修法 = 告警加文件出口 + Failures 经 hello/ping 暴露。
# 本条**故意制造审计写失败**，断言降级真的能被观察到 —— 否则"修好了"只是纸面结论。
mv "$WORK/install/logs" "$WORK/install/logs.ri1bak"
: > "$WORK/install/logs"     # 同名**文件**（不是目录）⇒ CreateDirectory / FileStream 全失败
call_core "$CORE_OUT" "$EZ" primitive process.enumerate --compact --json   # 触发一次审计写入
call_core "$CORE_OUT" "$EZ" core ping --json
AF="$("$PY" -I -X utf8 -c "
import json, sys
try:
    d = json.load(open(sys.argv[1], encoding='utf-8-sig'))
except Exception:
    print('PARSE_FAIL'); sys.exit(0)
# 兼容两种输出形态：CLI 可能直接给 result 内容，也可能带 jsonrpc/result 包装
r = d.get('result') if isinstance(d.get('result'), dict) else d
print(r.get('auditFailures', 'MISSING'))
" "$(winpath "$CORE_OUT")" 2>/dev/null | tr -d '\r\n')"
if [ "${AF:-MISSING}" = "MISSING" ] || [ "${AF:-x}" = "PARSE_FAIL" ]; then
  fail "auditFailures 字段缺失（审计健康无对外出口 —— RI-1 复发）"
elif [ "${AF:-0}" -ge 1 ] 2>/dev/null; then
  pass "★ 审计写失败被计数并经 ping 暴露（auditFailures=$AF）—— 降级不再静默"
else
  fail "auditFailures=$AF（期望 ≥1：已制造审计写失败却未被计数 ⇒ 降级仍不可见）"
fi
rm -f "$WORK/install/logs"
mv "$WORK/install/logs.ri1bak" "$WORK/install/logs"

# ── 12.12 安装形态：bin\ezt-core.exe 已铺进安装根 ────────────────────────────
# 自含式安装验证（第 7 步的部署目录在托盘步骤末尾会被清理，不能跨步引用）
P3_INSTALL="$WORK/p3-install"
"$EZ" install --install-root "$P3_INSTALL" --no-payload --quiet --from "$REPO" \
  > "$WORK/p3-install.log" 2>&1
P3_INSTALL_WIN="$(cygpath -m "$P3_INSTALL" 2>/dev/null || echo "$P3_INSTALL")"
if grep -q "Core 特权服务" "$WORK/p3-install.log" && [ -f "$P3_INSTALL_WIN/bin/ezt-core.exe" ]; then
  pass "安装形态 bin\\ezt-core.exe 存在（安装器 4b 步骤生效）"
else
  fail "安装器未把 ezt-core.exe 铺进安装根 bin（见 $WORK/p3-install.log）"
fi

# ── 12.12b ★ 声明门槛：未声明的原语必须被拒绝（RI-9 反向断言，2026-09-29）──────
# 背景：`ToolHostManager` 的 `elevatedPrimitives` 检查被 P3 方案称为"工具的**唯一授权面**"，
#   但它在**全验收面零覆盖**（grep `-32013` / `PrimitiveNotDeclared` / `未在 tool.json` 均 0 命中）
#   —— 实现完整 ≠ 机制有效（与 RI-1 同型）。本段补**双向契约**：
#     · 负向：未声明 ⇒ 拒绝，且**拒绝发生在 Core 之前**（审计里查不到这次调用）
#     · 正向对照：已声明 ⇒ 放行（证明门槛不是"一律拒绝"）
#   不做"断言字段存在"式检查 —— 必须真的跑一次被拒、一次放行。
GATE_TOOLS="$WORK/primgate"
rm -rf "$GATE_TOOLS"; mkdir -p "$GATE_TOOLS"
for gname in ungated gated; do
  mkdir -p "$GATE_TOOLS/$gname"
  cp "$REPO/sdk/python/template/tool.json" "$REPO/sdk/python/template/main.py" "$GATE_TOOLS/$gname/"
done

"$PY" -I -X utf8 - "$GATE_TOOLS" <<'PY'
import io, json, sys
base = sys.argv[1]
for name in ("ungated", "gated"):
    d = base + "/" + name
    m = json.load(io.open(d + "/tool.json", encoding="utf-8"))
    m["id"] = name
    m["name"] = "声明门槛探针-" + name
    m["needs"] = []
    m["contributes"] = {"commands": [{"id": name + ".call", "title": "调原语", "handler": "call"}]}
    m.pop("config", None)
    if name == "gated":
        m["elevatedPrimitives"] = ["process.enumerate"]
    else:
        m.pop("elevatedPrimitives", None)
    io.open(d + "/tool.json", "w", encoding="utf-8", newline="\n").write(
        json.dumps(m, ensure_ascii=False, indent=2) + "\n")
    io.open(d + "/main.py", "w", encoding="utf-8", newline="\n").write(
        'from eztools import Tool\ntool = Tool()\n\n'
        '@tool.handler("call")\ndef call(args):\n'
        '    r = tool.primitive("process.enumerate", {})\n'
        '    n = len(r) if isinstance(r, list) else len(r.get("processes", []))\n'
        '    return {"ok": True, "count": n}\n\n'
        'if __name__ == "__main__":\n    raise SystemExit(tool.run())\n')
PY

GATE_WIN="$(winpath "$GATE_TOOLS")"

"$EZ" invoke ungated.call --tools-dir "$GATE_WIN" --json > "$WORK/primgate-neg.log" 2>&1
NEG_RC=$?
NEG_HIT=$(grep -c 'elevatedPrimitives' "$WORK/primgate-neg.log")
NEG_OK=0
if [ "$NEG_RC" -ne 0 ]; then
  if [ "$NEG_HIT" -ge 1 ]; then NEG_OK=1; fi
fi
check "声明门槛负向：未声明 elevatedPrimitives 的原语必须被拒绝（rc≠0 且指名门槛）" 1 "$NEG_OK"
if [ "$NEG_OK" -eq 0 ]; then
  printf '        实测：rc=%s 输出=%s\n' "$NEG_RC" "$(head -c 200 "$WORK/primgate-neg.log" | tr -d '\r\n')"
fi

# ★ 反向取证：拒绝必须发生在 **Core 之前** —— 审计里不应有 ungated 的记录
#   （否则说明它其实被送到 Core 执行了，"拒绝"只是事后报错）
#   ⚠️ 不能写 `grep -c … || echo 0`：grep 无匹配时会**先输出 0 再走 ||**，变量变成两行。
GATE_AUDIT="$(ls "$WORK/install/logs/"audit-*.log 2>/dev/null | tail -1)"
NEG_AUDIT="$(grep -cF '"toolId":"ungated"' "$GATE_AUDIT" 2>/dev/null || true)"
NEG_AUDIT="${NEG_AUDIT:-0}"
check "★ 声明门槛在 Core 之前生效（被拒的调用未进审计 ⇒ 未被 Core 执行）" 0 "$NEG_AUDIT"

"$EZ" invoke gated.call --tools-dir "$GATE_WIN" --json > "$WORK/primgate-pos.log" 2>&1
POS_RC=$?
POS_CNT="$("$PY" -I -X utf8 -c "
import json, sys
try:
    d = json.load(open(sys.argv[1], encoding='utf-8-sig'))
except Exception:
    print(-1); sys.exit(0)
print(d.get('count', -1))
" "$(winpath "$WORK/primgate-pos.log")" 2>/dev/null | tr -d '\r\n')"
POS_OK=0
if [ "$POS_RC" -eq 0 ]; then
  if [ "${POS_CNT:-0}" -ge 1 ] 2>/dev/null; then POS_OK=1; fi
fi
check "声明门槛正向对照：已声明的原语必须放行（rc=0 且 count≥1）" 1 "$POS_OK"
if [ "$POS_OK" -eq 0 ]; then
  printf '        实测：rc=%s count=%s 输出=%s\n' "$POS_RC" "${POS_CNT:-?}" "$(head -c 200 "$WORK/primgate-pos.log" | tr -d '\r\n')"
fi

POS_AUDIT="$(grep -cF '"toolId":"gated"' "$GATE_AUDIT" 2>/dev/null || true)"
POS_AUDIT="${POS_AUDIT:-0}"
check "正向对照的调用确实到达了 Core（审计里有 gated 记录，与负向构成同源对照）" 1 \
  "$(if [ "${POS_AUDIT:-0}" -ge 1 ]; then echo 1; else echo 0; fi)"

# ── 12.13 core stop + status 归位 ────────────────────────────────────────────
call_core "$CORE_OUT" "$EZ" core stop
call_core "$CORE_OUT" "$EZ" core status
rc=$?
if [ $rc -eq 0 ] && grep -F "Core 未运行" "$CORE_OUT" >/dev/null 2>&1; then
  pass "core stop 后 status 如实归位（core.json 已摘除）"
else
  fail "core stop/status 归位失败（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi
