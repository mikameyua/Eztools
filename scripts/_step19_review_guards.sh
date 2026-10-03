#!/usr/bin/env bash
# ============================================================================
#  _step19_review_guards.sh —— 代码审查红线静态守卫（docs/代码审查规范与流程.md §3.9）
#
#  覆盖面（把"只写在文档里的红线"变成可执行检查）：
#    · G1 静默吞异常（空 catch）—— 裸空=FAIL / 有注释=WARN / 带理由豁免=EXEMPT
#    · G2 字符串 case 的 switch 缺 default（未知值静默丢弃）
#    · G3 验收脚本禁用模式（`| grep -q` / `A && B || C && D`）
#    · G4 幽灵代码候选（private 方法无调用点）
#    · G5 死产物目录（项目 TFM 与 bin/obj 实际 TFM 不一致）
#    · G6 跳过必须计数（断言脚本里「跳过」文案附近无 SKIPPED 计数）
#    · G7 分层守卫（W7：`src/*/Launcher/` 出现 UI 依赖或 Eztools.Core 引用）
#
#  ★ 本步的"元断言"（对检查器的检查）——这是本步存在的核心理由：
#    ① `--selftest` 必须全绿（当前 23 条）：**证明每条检查都真的会红**（双向突变验证）。
#       新增检查却不证明它会红 = 新增了一条"绿得毫无提示"的伪闸门（S2 形态）。
#    ② 豁免（EXEMPT）必须**落数字**：豁免是"让检查变松"的机制，不落数字就会
#       悄悄变成黑洞（§4.4"放宽检查也要带突变验证"同族）。
#    ③ 反向存在性：输出必须仍声明"行宽不检查"——把「不采纳」裁决固化成断言的
#       存在性，防止后人顺手把行宽规则加回来（README §3.6 的"别当待办修"）。
#
#  前置：acceptance.sh 已定义 pass/fail/check/step、$PY、$REPO、$WORK、winpath。
# ============================================================================
step "20  代码审查红线静态守卫（静默 catch / switch / 禁用模式 / 幽灵代码 / 死产物 / 跳过计数 / 分层）"

RG="$REPO/scripts/review-guards.py"
RG_LOG="$WORK/review-guards.log"
RG_JSON="$WORK/review-guards.json"
RG_SELF_LOG="$WORK/review-guards-selftest.log"

if [ ! -f "$RG" ]; then
  fail "静态守卫脚本缺席：$RG"
else
  pass "静态守卫脚本在位（$RG）"
fi

# ── 20.1 突变验证（--selftest）：每条检查都必须被证明会红 ─────────────────────
"$PY" -I -X utf8 -u "$RG" --selftest > "$RG_SELF_LOG" 2>&1
RG_SELF_RC=$?
check "静态守卫 --selftest 退出码 0（全部突变被抓住）" 0 "$RG_SELF_RC"
RG_SELF_SUM=$(grep -oE 'review-guards-selftest: PASS=[0-9]+ FAIL=[0-9]+' "$RG_SELF_LOG" | head -1)
check "突变验证汇总行落数字（PASS/FAIL 均可解析）" 1 \
  "$(printf '%s' "$RG_SELF_SUM" | grep -cE '^review-guards-selftest: PASS=[0-9]+ FAIL=[0-9]+$')"
RG_SELF_FAIL=$(printf '%s' "$RG_SELF_SUM" | grep -oE 'FAIL=[0-9]+' | cut -d= -f2)
check "突变验证自身 FAIL=0" 0 "${RG_SELF_FAIL:-none}"
RG_SELF_PASS=$(printf '%s' "$RG_SELF_SUM" | grep -oE 'PASS=[0-9]+' | cut -d= -f2)
# 断言用例数不为 0：夹具全被跳过时 PASS 会是 0，而"0 条全通过"是典型的假绿
check "突变验证用例数 ≥ 10（非空跑）" 1 \
  "$(if [ "${RG_SELF_PASS:-0}" -ge 10 ]; then echo 1; else echo 0; fi)"

# ── 20.2 真实仓扫描：🔴 FAIL 必须为 0 ────────────────────────────────────────
"$PY" -I -X utf8 -u "$RG" --repo "$(winpath "$REPO")" --json "$RG_JSON" > "$RG_LOG" 2>&1
RG_RC=$?
check "静态守卫扫描退出码 0（无红线命中）" 0 "$RG_RC"
RG_SUM=$(grep -oE 'review-guards: FAIL=[0-9]+ WARN=[0-9]+ EXEMPT=[0-9]+' "$RG_LOG" | head -1)
check "扫描汇总行落数字（FAIL/WARN/EXEMPT 三数可解析）" 1 \
  "$(printf '%s' "$RG_SUM" | grep -cE '^review-guards: FAIL=[0-9]+ WARN=[0-9]+ EXEMPT=[0-9]+$')"
RG_FAIL=$(printf '%s' "$RG_SUM" | grep -oE 'FAIL=[0-9]+' | cut -d= -f2)
check "🔴 红线命中 FAIL=0" 0 "${RG_FAIL:-none}"

# ── 20.3 WARN / EXEMPT 必须落数字（不阻断 ≠ 不用管 / 豁免不能是黑洞）─────────
RG_WARN=$(printf '%s' "$RG_SUM" | grep -oE 'WARN=[0-9]+' | cut -d= -f2)
RG_EXEMPT=$(printf '%s' "$RG_SUM" | grep -oE 'EXEMPT=[0-9]+' | cut -d= -f2)
check "🟡 WARN 落数字" 1 "$(printf '%s' "${RG_WARN:-x}" | grep -cE '^[0-9]+$')"
check "⚪ EXEMPT（显式豁免）落数字" 1 "$(printf '%s' "${RG_EXEMPT:-x}" | grep -cE '^[0-9]+$')"
printf '  [INFO] 静态守卫：FAIL=%s WARN=%s EXEMPT=%s（WARN 不阻断，需人工分类）\n' \
  "${RG_FAIL:-?}" "${RG_WARN:-?}" "${RG_EXEMPT:-?}"

# ── 20.4 JSON 产物三键可解析（防"汇总行说 0、明细里一堆"）────────────────────
RG_JSON_CHK=$("$PY" -I -X utf8 -c "
import json,sys
d=json.load(open(r'$RG_JSON',encoding='utf-8'))
print('OK' if all(k in d for k in ('fail','warn','exempt','hits')) else 'MISSING')
" 2>/dev/null | tr -d '\r\n')
check "JSON 明细产物含 fail/warn/exempt/hits 四键" "OK" "${RG_JSON_CHK:-missing}"
RG_JSON_N=$("$PY" -I -X utf8 -c "
import json
d=json.load(open(r'$RG_JSON',encoding='utf-8'))
print(len(d['hits']))
" 2>/dev/null | tr -d '\r\n')
check "JSON 明细条数 == WARN+EXEMPT+FAIL（守恒）" "$(( ${RG_WARN:-0} + ${RG_EXEMPT:-0} + ${RG_FAIL:-0} ))" "${RG_JSON_N:-none}"

# ── 20.5 反向存在性：「行宽不检查」必须仍被声明（把「不采纳」钉死）────────────
check "守卫仍声明「行宽不检查」（不采纳裁决未被悄悄推翻）" 1 \
  "$(grep -c '行宽（80/120 字符）：\*\*不检查\*\*' "$RG_LOG")"

# ── 20.6 热键判定的「环境 vs 代码」分流必须被证明会红 ─────────────────────────
# 背景（2026-09-28）：桌面验收里"热键注册 = 6 实际 5"曾被读成代码回归，真因是
# `Ctrl+Alt+W` 被本机别的进程占用（GetLastError=1409）。修法是三态判定
# （满额=pass / 有占用=skip 落数字 / 无占用=fail）。**放宽到 skip 必须带突变验证** ——
# 否则"所有失败都变跳过"就成了永久假绿（§4.4：放宽检查也要带突变验证）。
VD_SELF_LOG="$WORK/verify-desktop-hotkey-selftest.log"
"$PY" -I -X utf8 -u "$REPO/scripts/verify-desktop.py" --selftest > "$VD_SELF_LOG" 2>&1
check "热键三态判定 --selftest 退出码 0" 0 $?
VD_SELF_SUM=$(grep -oE 'verify-desktop-hotkey-selftest: PASS=[0-9]+ FAIL=[0-9]+' "$VD_SELF_LOG" | head -1)
check "热键三态判定汇总行落数字" 1 \
  "$(printf '%s' "$VD_SELF_SUM" | grep -cE '^verify-desktop-hotkey-selftest: PASS=[0-9]+ FAIL=[0-9]+$')"
check "热键三态判定：fail 分支仍会被判红（跳过分流没把检查掏空）" 0 \
  "$(printf '%s' "$VD_SELF_SUM" | grep -oE 'FAIL=[0-9]+' | cut -d= -f2)"
check "热键三态判定覆盖 ≥ 4 种情形（含「探针挂掉按失败处理」）" 1 \
  "$(if [ "$(printf '%s' "$VD_SELF_SUM" | grep -oE 'PASS=[0-9]+' | cut -d= -f2)" -ge 4 ]; then echo 1; else echo 0; fi)"
