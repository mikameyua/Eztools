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

# ── 12.13 core stop + status 归位 ────────────────────────────────────────────
call_core "$CORE_OUT" "$EZ" core stop
call_core "$CORE_OUT" "$EZ" core status
rc=$?
if [ $rc -eq 0 ] && grep -F "Core 未运行" "$CORE_OUT" >/dev/null 2>&1; then
  pass "core stop 后 status 如实归位（core.json 已摘除）"
else
  fail "core stop/status 归位失败（rc=$rc）：$(head -c 200 "$CORE_OUT")"
fi
