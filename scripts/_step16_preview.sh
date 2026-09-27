#!/usr/bin/env bash
# ============================================================================
#  _step16_preview.sh —— 速览（preview）内容判定：文件类型 / 编码告警 / 提示语
#
#  为什么单开一段：`_content_nodes()` 是速览的**核心逻辑**，此前**零自动化覆盖**。
#  preview 的既有断言只覆盖四件外围的事（清单声明 / 面板在列表里 / 热键绑定 / config 映射），
#  于是实测暴露的三个判定缺陷一直没人发现（详见 verify-preview.py 的头注释）。
#
#  本段覆盖的判定面（全部已修 + 全部有断言）：
#    · 编码告警（GBK 有告警、UTF-8 不误报）
#    · 二进制提示语指路「用系统程序打开」
#    · UTF-16 / UTF-32 宽字符文本不误判（BOM 先于 NUL 判定）
#    · PDF 提取文字层（带文字层 / 无文字层 / 结构损坏三态）
#    · Office 文字层（docx / xlsx / pptx，OOXML = ZIP + XML，零第三方依赖）
#    · ZIP 炸弹防线（OOXML 通路独有的新风险面）
#    · pptx 页序契约（10 页以上按数字排，不按字符串 —— 见设计方案 §29.7）
#
#  前置：acceptance.sh 已定义 pass/fail/step、$EZ、$WORK、$REPO、$PY，
#        且已 export EZTOOLS_INSTALL_ROOT / EZTOOLS_CONFIG_ROOT。
# ============================================================================

step "16/18  速览内容判定：编码告警 / 二进制提示语 / PDF+Office 文字层"

if [ -z "${PY:-}" ]; then
  PY="$(command -v python || command -v python3 || true)"
  [ -n "$PY" ] || PY="$REPO/spike/_runtime-test/python/python.exe"
fi

PV_LOG="$WORK/preview-verify.log"

# 子脚本形态与 verify-desktop.py 一致：它自己发断言、自己算失败数 ——
# 判定逻辑（造夹具 / 比对节点）写成 Python 才能读，bash 里写这些比对是自找苦吃。
# ⚠️ **必须显式传 --tools-dir**（S11：不传参数也把真实依赖藏起来）：
#   本机 `%LOCALAPPDATA%\Eztools\tools\` 真有一份陈旧安装的 paneltool，
#   不传的话它**遮蔽仓库源** ⇒ 21.x 那组新断言会拿旧 paneltool 跑，
#   新加的 `input` 面板**整条从报告里消失**且不报错（实测踩到，见 MEMORY.md §四补 S11）。
"$PY" "$REPO/scripts/verify-preview.py" \
  --repo "$REPO" --ez "$EZ" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  --tools-dir "$TOOLS_DIR_WIN" \
  > "$PV_LOG" 2>&1
PV_RC=$?

# 把子脚本的每条 PASS/FAIL 计入总数 —— 否则总计数会漏掉这一整批
while IFS= read -r ln; do
  case "$ln" in
    *"[PASS]"*) pass "${ln##*\[PASS\] }" ;;
    *"[FAIL]"*) fail "${ln##*\[FAIL\] }" ;;
    *) [ -n "$ln" ] && printf '  %s\n' "$ln" ;;
  esac
done < "$PV_LOG"

# 子脚本自己崩了（异常退出且没输出任何断言）时别静默 ——
# "脚本崩了"与"断言失败了"在 CI 日志里必须能分辨。
if [ "$PV_RC" -ne 0 ] && ! grep -q "\[FAIL\]" "$PV_LOG"; then
  fail "速览验收脚本异常退出（退出码 $PV_RC），见 $PV_LOG"
fi

# ============================================================================
#  17/17  工具源显式性审计（S11 普查，2026-09-23 增）
#
#  为什么把审计**挂进验收**而不是"需要时手动跑"：
#    S11 的教训正是"靠环境恰好如此、且零信号"。一个**没人跑**的审计
#    本身就是同一个病（审计存在 vs 审计生效的区别）。
#    挂进验收后，任何新增的"未钉数据源调用 / 相对路径 / 函数定义顺序倒置"
#    都会在 CI 里直接变红。
#
#  判据落**数字**：审计脚本 exit 1 = 有风险（--strict）；
#  同时把 summary 的数字抠出来做正向断言，避免"exit 0 但其实什么都没查"。
# ============================================================================
step "17/18  工具源显式性审计（未钉数据源 / 相对路径 / 定义顺序）"

AUDIT_LOG="$WORK/audit-tool-sources.log"
bash "$REPO/scripts/audit-tool-sources.sh" --strict > "$AUDIT_LOG" 2>&1
AUDIT_RC=$?

# 正向断言：数字必须真的被数出来（防"脚本没跑 / 全部 0 但没读"）
nums=$(grep -E '未钉数据源的真实调用|相对路径形态的脚本|定义顺序可疑的脚本' "$AUDIT_LOG" | grep -oE '[0-9]+' | tr '\n' ' ')
n_unpinned=$(printf '%s' "$nums" | awk '{print $1}')
n_relpath=$(printf '%s' "$nums" | awk '{print $2}')
n_order=$(printf '%s' "$nums" | awk '{print $3}')

# 有效性前提检查（M3）：三个数字都得真切到，否则审计没跑成
if [ -z "$n_unpinned" ] || [ -z "$n_relpath" ] || [ -z "$n_order" ]; then
  fail "工具源审计未产出可解析的数字（脚本可能没跑成），见 $AUDIT_LOG"
else
  check "工具源审计：未钉数据源的调用数为 0"  "0" "$n_unpinned"
  check "工具源审计：相对路径形态的脚本数为 0" "0" "$n_relpath"
  check "工具源审计：函数定义顺序可疑的脚本数为 0" "0" "$n_order"
  # 反向断言（防"一律报 0"）：审计至少要能列出被扫描的脚本数 > 0
  scanned=$(grep -cE '^\s+\[(ok|风险)\]' "$AUDIT_LOG")
  if [ "$scanned" -gt 0 ]; then
    pass "工具源审计确实扫描了脚本（$scanned 个脚本有条目）"
  else
    fail "工具源审计没有任何脚本条目 —— 疑似扫描面为空（假绿）"
  fi
fi

if [ "$AUDIT_RC" -ne 0 ]; then
  fail "工具源审计报告有风险（退出码 $AUDIT_RC），见 $AUDIT_LOG"
  grep -E '^\s+\[风险\]|·' "$AUDIT_LOG" | head -10 | while IFS= read -r ln; do
    printf '  %s\n' "$ln"
  done
fi

# ============================================================================
#  18/18  文档交叉引用完整性（§N.M 小节号 + 文件路径）
#
#  为什么值得进验收：本项目把细节全沉进 docs/，MEMORY 只留指针，代码注释里也
#  大量写"协议 §3.4""设计文档 §28"。于是**指针本身成了契约** ——
#  指向不存在的小节/文件时，读者会以为是自己没找到，而不是"这指针是错的"。
#  而 markdown 与注释**都不会因此报错** ⇒ 典型的零信号。
#
#  判据落**数字**（与审计同构）：脚本 exit 1 = 有悬空；
#  同时抠出"悬空引用数 / 悬空路径数"做正向断言，防"exit 0 但其实没查"。
#  历史日志中的旧编号只提示、不判失败（日志是追加型历史，不追改）。
# ============================================================================
step "18/18  文档交叉引用完整性（§N.M 与文件路径）"

# 🔴 开头防御性清理两个突变探针（2026-09-24 实测）：探针由本步骤后半段创建、结束时删除，
#    但**上一次运行若中途被中断**（外部掐掉 / 沙箱拦截），探针会残留到本次 ——
#    于是**主检查**把探针里的 docs/mut-b.md、docs/mut-c.md 当真悬空报出（实测恰好 2 处），
#    FAIL 形态为"主检查悬空路径 2 处、单跑重试又消失"的幽灵。与 acceptance.sh 结尾注释
#    "上次中断 → 下次踩残留"同族；那里靠开头 rm -rf "$WORK" 兜底，探针在 $REPO/docs
#    之下，WORK 清理够不着，必须在这里自己清。
rm -f "$REPO/docs/_mut_retired_probe.md" "$REPO/docs/_mut_sec_probe.md"

REF_LOG="$WORK/check-doc-refs.log"
"$PY" "$REPO/scripts/check-doc-refs.py" --repo "$REPO" --strict > "$REF_LOG" 2>&1
REF_RC=$?

n_ref=$(grep -oE '小节号 [0-9]+ 处 · 路径 [0-9]+ 处' "$REF_LOG" | grep -oE '小节号 [0-9]+' | grep -oE '[0-9]+')
n_refpath=$(grep -oE '小节号 [0-9]+ 处 · 路径 [0-9]+ 处' "$REF_LOG" | grep -oE '路径 [0-9]+' | grep -oE '[0-9]+')

if [ -z "$n_ref" ] || [ -z "$n_refpath" ]; then
  # 没产出可解析数字 = 脚本没跑成 或 结果干净（干净时没这行）——
  # 靠 REF_RC 区分：干净是 0，跑挂是非 0。
  if [ "$REF_RC" -eq 0 ]; then
    pass "文档交叉引用：无悬空小节号"
    pass "文档交叉引用：无悬空文件路径"
  else
    fail "文档交叉引用脚本未产出可解析结果且退出码 $REF_RC，见 $REF_LOG"
  fi
else
  check "文档交叉引用：悬空小节号数为 0" "0" "$n_ref"
  check "文档交叉引用：悬空文件路径数为 0" "0" "$n_refpath"
fi

# 反向断言（防"一律报 0"）：至少得真扫到文件
ref_scanned=$(grep -oE '扫描文件 [0-9]+ 个' "$REF_LOG" | grep -oE '[0-9]+')
if [ -n "$ref_scanned" ] && [ "$ref_scanned" -gt 100 ]; then
  pass "文档交叉引用确实扫描了文件（$ref_scanned 个）"
else
  fail "文档交叉引用扫描面异常（scanned='$ref_scanned'，期望 >100）—— 疑似假绿"
fi

# ── 突变验证：「已更名/已删除」退役路径豁免分支（2026-09-23 增）──────────────
# 为什么必须做：上面那条豁免会让**真笔误**也可能被放过 —— 豁免分支本身
# 是"让检查变松"的代码，**松过头就再也抓不到任何东西**，而且它红了才是对的，
# 绿得毫无提示（S2 同族：新增解析分支后没人验证过它到底生不生效）。
# ⇒ 就地造三个探针：①带标记（应被豁免）②不带标记（必须仍报）③标记离太远（必须仍报）。
# 断言落**具体路径是否出现在报告里**（§6 铁律：静默错时退出码失效 ⇒ 落具体路径）。
MUT_PROBE="$REPO/docs/_mut_retired_probe.md"
cat > "$MUT_PROBE" <<'MUTEOF'
# MUT 突变探针（生成物，本轮验收结束时删除）
原名 `docs/mut-a.md` —— 已更名，这是记录历史。
这里裸引 `docs/mut-b.md` 必须被报出。
这里 `docs/mut-c.md` 隔了很远很远之后才写已更名，标记不该生效。
MUTEOF
"$PY" "$REPO/scripts/check-doc-refs.py" --repo "$REPO" > "$WORK/check-doc-refs-mut.log" 2>&1
mut_log=$(cat "$WORK/check-doc-refs-mut.log")
rm -f "$MUT_PROBE"

# ① 带标记 ⇒ 不出现（豁免生效）
case "$mut_log" in
  *"docs/mut-a.md"*) fail "退役路径豁免未生效：带「已更名」标记的路径仍被报为悬空" ;;
  *) pass "退役路径豁免生效：带「已更名」标记的路径未被报为悬空" ;;
esac
# ② 不带标记 ⇒ 必须出现（豁免没伤到真检查）
case "$mut_log" in
  *"docs/mut-b.md"*) pass "退役路径豁免未误伤：无标记的不存在路径仍被判悬空" ;;
  *) fail "退役路径豁免误伤：无标记的不存在路径 docs/mut-b.md 未被报出 ⇒ 检查已失效" ;;
esac
# ③ 标记离太远 ⇒ 必须出现（near-miss 防线）
case "$mut_log" in
  *"docs/mut-c.md"*) pass "退役路径豁免有距离限制：远处标记不生效" ;;
  *) fail "退役路径豁免无距离限制：远处「已更名」错误地豁免了 docs/mut-c.md" ;;
esac

# ── 突变验证：「（新建）§N.M」待建小节豁免分支（2026-09-23 增）────────────────
# 与文件路径的 `（新建）` 同构，但**对象是小节号**：写「在 `（新建）§3.7` 加 input 节点」时，
# 那个小节现在必然不存在，不该判悬空；而**裸引**的 §N.M 仍必须判（保持既有语义）。
# ⚠️ 同样是"让检查变松"的代码 ⇒ 必须证明它没瞎。
#
# 🔴 **本节用变量拼小节号，不写字面量** —— 原因是一次真实自指事故：
#    第一版把 `§99.7` / `§99.8` 直接写在下面这些 case 模式里，
#    于是**本脚本自己被自己的断言搞红**（step 18 报出 6 处悬空，全指向本文件）。
#    这与 heredoc 夹具是同一族问题：**检查器会把"用来测试的字符串"当成真引用**。
#    修法：让探针编号**在运行时拼出来**，源码里不出现完整的 `§N.M` 字面量。
SEC_A=$(printf '\302\247')"99.7"   # 编号 99.7 —— 待建，应被豁免
SEC_B=$(printf '\302\247')"99.8"   # 编号 99.8 —— 悬空，必须报出
SEC_PROBE="$REPO/docs/_mut_sec_probe.md"
{
  echo '# MUT 小节探针（生成物，本轮验收结束时删除）'
  echo "本次将在 \`（新建）${SEC_A}\` 加节点。"
  echo "请见 ${SEC_B} 的定义。"
} > "$SEC_PROBE"
"$PY" "$REPO/scripts/check-doc-refs.py" --repo "$REPO" > "$WORK/check-doc-refs-sec.log" 2>&1
sec_log=$(cat "$WORK/check-doc-refs-sec.log")
rm -f "$SEC_PROBE"

case "$sec_log" in
  *"$SEC_A"*) fail "待建小节豁免未生效：带「（新建）」标记的 ${SEC_A} 仍被报为悬空" ;;
  *) pass "待建小节豁免生效：带「（新建）」标记的 ${SEC_A} 未被报为悬空" ;;
esac
case "$sec_log" in
  *"$SEC_B"*) pass "待建小节豁免未误伤：无标记的悬空小节号 ${SEC_B} 仍被报出" ;;
  *) fail "待建小节豁免误伤：无标记的悬空小节号 ${SEC_B} 未被报出 ⇒ 检查已失效" ;;
esac
# 反向：断言抽掉了探针自己造成的噪音（本文件不该出现在报告里）
case "$sec_log" in
  *"_step16_preview.sh"*) fail "自指噪音：检查报告里出现了本脚本自身（断言字面量被当成真引用）" ;;
  *) pass "自指噪音为零：检查报告里没有本脚本自身" ;;
esac
