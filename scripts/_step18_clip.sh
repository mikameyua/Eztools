#!/usr/bin/env bash
# ============================================================================
#  _step18_clip.sh —— W5-a 剪贴板历史库验收：捕获 / 去重 / 双路搜索 / 清理 / 回复制
#
#  覆盖面（W5-剪贴板-设计方案.md §7 W5-a 出口 + FR-1/2/3/7/8/9/13）：
#    · `ezt clip capture`  —— 读一次系统剪贴板入库（isNew / 去重 copyCount）
#    · `ezt clip search`   —— ≥3 字符 FTS trigram 命中 + <3 字符 LIKE 兜底（R2 三档）
#    · `ezt clip pin / clear --keep-pinned` —— 置顶豁免清理（守恒断言）
#    · `ezt clip copy`     —— 重新复制回系统剪贴板（读回比对，非"命令退 0 即成功"）
#    · `ezt clip status`   —— 总数/体积落数字
#
#  剪贴板写入/读回用 PowerShell Set-Clipboard/Get-Clipboard（文本经 UTF-8 临时文件
#  中转，不走命令行传参——中文经 argv 编码会被 locale 掉坑，实测踩过）。
#
#  黑名单三态判定在 selftest 的 PrivacyFilter 用例覆盖（Set-Clipboard 经 EmptyClipboard
#  后 GetClipboardOwner 返回 null，无法构造稳定来源进程 → e2e 黑名单属 W5-c 真实监听层）。
#
#  前置：acceptance.sh 已定义 pass/fail/check/step/SKIPPED、$EZ、$WORK、$REPO、$PY。
# ============================================================================
step "19  剪贴板历史库（W5-a）：捕获 / 去重 / 双路搜索 / 清理 / 回复制"

# 🔴 独立 config 根（W5-c 起）：acceptance 的桌面探针/selfcheck 实例可能带监听把
#    section 2 的 set_clipboard 内容写进共享 config 根 —— clip 断言必须自管隔离（S11 纪律）。
CLIP_CFG="$WORK/clip-config"
mkdir -p "$CLIP_CFG"
CLIP_ROOT_FLAG="--install-root \"$EZTOOLS_INSTALL_ROOT\" --config-root \"$(winpath "$CLIP_CFG")\""
MARKER="W5A-ACCEPT-$(date +%H%M%S)"
# 含三字中文子串（FTS 档）与两字中文词（LIKE 档）与 ASCII（hash/回复制比对）
CLIP_TEXT="验收剪贴板 ${MARKER} clip-accept-123"

# ── 19.1 空库 status：rc=0 且 total 落数字 ────────────────────────────────────
eval "\"\$EZ\" clip status $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-status-0.json" 2>"$WORK/clip-status-0.err"
check "ezt clip status 空库退出码 0" 0 $?
grep -oE '"total":[0-9]+' "$WORK/clip-status-0.json" | head -1 | cut -d: -f2 > "$WORK/clip-total0"
check "clip status total 落数字（空库=0）" 0 "$(cat "$WORK/clip-total0")"

# ── 19.2 捕获：Set-Clipboard → capture → saved/isNew ─────────────────────────
printf '%s' "$CLIP_TEXT" > "$WORK/clip-text1.txt"
powershell.exe -NoProfile -Command "Set-Clipboard -Value (Get-Content -Raw -Encoding UTF8 \"$WORK/clip-text1.txt\")" >/dev/null 2>&1
eval "\"\$EZ\" clip capture $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-capture1.json" 2>"$WORK/clip-capture1.err"
check "clip capture 退出码 0（有内容可捕）" 0 $?
check "capture saved=true" true "$(grep -oE '"saved":(true|false)' "$WORK/clip-capture1.json" | head -1 | cut -d: -f2)"
check "capture isNew=true（新条目）" true "$(grep -oE '"isNew":(true|false)' "$WORK/clip-capture1.json" | head -1 | cut -d: -f2)"
CLIP_ID=$(grep -oE '"id":[0-9]+' "$WORK/clip-capture1.json" | head -1 | cut -d: -f2)
check "capture 返回条目 id 落数字" 1 "$(printf '%s' "$CLIP_ID" | grep -cE '^[0-9]+$')"

# ── 19.3 去重：同内容二次捕获 → copyCount=2（FR-3）────────────────────────────
eval "\"\$EZ\" clip capture $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-capture2.json" 2>"$WORK/clip-capture2.err"
check "重复捕获 isNew=false" false "$(grep -oE '"isNew":(true|false)' "$WORK/clip-capture2.json" | head -1 | cut -d: -f2)"
check "重复捕获 copyCount=2（守恒：仍 1 行）" 2 "$(grep -oE '"copyCount":[0-9]+' "$WORK/clip-capture2.json" | head -1 | cut -d: -f2)"

# ── 19.4 双路搜索（FR-7 / §8 R2 三档）────────────────────────────────────────
# 🔴 check() 是字符串相等比较；hits 断言是"≥1"数值判断 → 必须手写 if（_step17 同款教训）。
#    且 grep -oE '"hits":[0-9]+' 输出是单字段（"hits":1），后续只能 cut，不能 awk 取 $2。
eval "\"\$EZ\" clip search 验收剪 $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-search3.json" 2>"$WORK/clip-search3.err"
HITS3=$(grep -oE '"hits":[0-9]+' "$WORK/clip-search3.json" | head -1 | cut -d: -f2)
: "${HITS3:=0}"
if [ "$HITS3" -ge 1 ]; then
  pass "搜索 ≥3 字符走 FTS（中文子串命中，hits=$HITS3）"
else
  fail "搜索 ≥3 字符走 FTS（中文子串命中）（实际 hits=$HITS3）"
fi
eval "\"\$EZ\" clip search 验收 $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-search2.json" 2>"$WORK/clip-search2.err"
HITS2=$(grep -oE '"hits":[0-9]+' "$WORK/clip-search2.json" | head -1 | cut -d: -f2)
: "${HITS2:=0}"
if [ "$HITS2" -ge 1 ]; then
  pass "搜索 <3 字符走 LIKE 兜底（两字中文命中，hits=$HITS2）"
else
  fail "搜索 <3 字符走 LIKE 兜底（两字中文命中）（实际 hits=$HITS2）"
fi
eval "\"\$EZ\" clip search \"绝不存在的标记词 nope-${MARKER}\" $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-search0.json" 2>"$WORK/clip-search0.err"
check "搜索未命中如实 hits=0" 0 "$(grep -oE '"hits":[0-9]+' "$WORK/clip-search0.json" | head -1 | cut -d: -f2)"

# ── 19.5 置顶豁免清理（FR-8 / FR-9）──────────────────────────────────────────
eval "\"\$EZ\" clip pin \"$CLIP_ID\" $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-pin.json" 2>"$WORK/clip-pin.err"
check "clip pin 退出码 0" 0 $?
# 塞两条噪声再清空（置顶豁免）
for i in 1 2; do
  printf '%s 噪声条目 %s' "$CLIP_TEXT" "$i" > "$WORK/clip-noise.txt"
  powershell.exe -NoProfile -Command "Set-Clipboard -Value (Get-Content -Raw -Encoding UTF8 \"$WORK/clip-noise.txt\")" >/dev/null 2>&1
  eval "\"\$EZ\" clip capture $CLIP_ROOT_FLAG --json --compact" >/dev/null 2>&1
done
eval "\"\$EZ\" clip clear --keep-pinned $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-clear.json" 2>"$WORK/clip-clear.err"
check "clip clear --keep-pinned 退出码 0" 0 $?
check "clear 删除数=2（噪声条目）" 2 "$(grep -oE '"deleted":[0-9]+' "$WORK/clip-clear.json" | head -1 | cut -d: -f2)"
eval "\"\$EZ\" clip search \"$MARKER\" $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-after-clear.json" 2>"$WORK/clip-after-clear.err"
HITS_KEEP=$(grep -oE '"hits":[0-9]+' "$WORK/clip-after-clear.json" | head -1 | cut -d: -f2)
: "${HITS_KEEP:=0}"
if [ "$HITS_KEEP" -eq 1 ]; then
  pass "置顶条目在 clear --keep-pinned 后存活（搜索仍命中）"
else
  fail "置顶条目在 clear --keep-pinned 后存活（实际 hits=$HITS_KEEP）"
fi

# ── 19.6 回复制：clip copy → Get-Clipboard 读回比对（FR-9，非"退 0 即成功"）──
eval "\"\$EZ\" clip copy \"$CLIP_ID\" $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-copy.json" 2>"$WORK/clip-copy.err"
check "clip copy 退出码 0" 0 $?
printf '%s' "$CLIP_TEXT" > "$WORK/clip-expected.txt"
powershell.exe -NoProfile -Command \
  "[Console]::OutputEncoding=[Text.Encoding]::UTF8; \$c=Get-Clipboard -Raw; \$e=Get-Content -Raw -Encoding UTF8 \"$WORK/clip-expected.txt\"; if (\$c -eq \$e) { 'CLIP-EQUAL' } else { 'CLIP-DIFF' }" \
  > "$WORK/clip-verify.txt" 2>/dev/null
check "回复制后剪贴板内容逐字比对一致" "CLIP-EQUAL" "$(tr -d '\r\n' < "$WORK/clip-verify.txt")"

# ── 19.7 清场：unpin + clear → total=0（status 守恒收尾）──────────────────────
eval "\"\$EZ\" clip unpin \"$CLIP_ID\" $CLIP_ROOT_FLAG" >/dev/null 2>&1
eval "\"\$EZ\" clip clear $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-clear2.json" 2>&1
check "全量 clear 退出码 0" 0 $?
eval "\"\$EZ\" clip status $CLIP_ROOT_FLAG --json --compact" > "$WORK/clip-status-end.json" 2>&1
check "清场后 status total=0（守恒）" 0 "$(grep -oE '"total":[0-9]+' "$WORK/clip-status-end.json" | head -1 | cut -d: -f2)"
