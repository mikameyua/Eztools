#!/usr/bin/env bash
# ============================================================================
#  _step17_ocr.sh —— W4 屏幕取字验收：CLI 原语层（W4-a 引擎 + W4-c 验收接线）
#
#  覆盖面（设计方案 §7 W4-a 产出③ + FR-8）：
#    · `ezt ocr langs` —— 语言包枚举与退出码契约（0 = 有语言包 / 7 = 缺失）
#    · `ezt ocr probe` —— 引擎自检：样图 "EZTOOLS OCR 2026" 识别 ≥8 字符 + 含 OCR + 含数字
#
#  为什么遮罩 UI 探针（--probe-ocr-overlay）不进这里：
#    它真实移动鼠标、改写剪贴板（W4-b 设计明文的副作用），只允许跑在"无人交互"
#    的会话 —— acceptance 的执行环境不满足该前提。UI 面由 verify-desktop.py 的
#    selfcheck 断言（热键注册 = 5 / OCR 热键行 / 设置清单 desktop 节）覆盖，
#    端到端注入验证见 W4-手工验收清单.md（探针手跑步骤）。
#
#  同理，--probe-ocr-hotkey（W4-c 真按键探针：keybd_event 注入 Ctrl+Alt+O → 遮罩
#  → Esc 收窗）也不进 acceptance —— 同为真按键副作用探针（遮罩抢焦点 1~2 秒）。
#  它已进 verify-desktop.py（1c-6 段，与 --probe-search-summon 同量级先例）。
#
#  R1 环境依赖分支：OCR 语言包是 Windows Feature（Language.OCR~），与安装根无关，
#  新机器大概率缺失。缺失时 `ezt ocr langs` 退出码 7 —— 这正是设计好的显式失败出口，
#  本节计 SKIP 并打印安装引导（不 FAIL：在没装语言包的机器上断言"引擎能识别"
#  是恒红；断言"缺失时有明确出口"才是可以在任何机器上守住的判据）。
#
#  前置：acceptance.sh 已定义 pass/fail/step/SKIPPED、$EZ、$WORK、$REPO、$PY。
# ============================================================================

step "17b/18  屏幕取字（OCR）：语言包 / 引擎自检 / 设置接线"

# ── 17.1 `ezt ocr langs`：语言包枚举 + 退出码契约 ────────────────────────────
"$EZ" ocr langs --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" --json --compact > "$WORK/ocr-langs.json" 2>"$WORK/ocr-langs.err"
LANGS_RC=$?
LANGS_COUNT=$(grep -oE '"count":[0-9]+' "$WORK/ocr-langs.json" | head -1 | cut -d: -f2)
: "${LANGS_COUNT:=0}"

case "$LANGS_RC" in
  0)
    pass "ezt ocr langs 退出码 0（语言包存在）"
    # 🔴 check() 是字符串相等比较，不是条件表达式 —— ≥/≤ 判断必须手写 if，
    #    传 "[[ 2 -ge 1 ]]" 进 check 会拿它当期望值字面量比对（实测假红，2026-09-26）。
    if [ "${LANGS_COUNT:-0}" -ge 1 ]; then
      pass "语言包数量 ≥ 1（langs.count 落数字：$LANGS_COUNT）"
    else
      fail "语言包数量 ≥ 1（实际 count=$LANGS_COUNT）"
    fi
    ;;
  7)
    # R1 环境依赖分支：缺失 = 探明的显式出口，不是失败。两条断言计入 SKIPPED。
    SKIPPED=$((SKIPPED + 2))
    printf '  [跳过] OCR 语言包缺失（rc=7，R1 环境依赖分支）—— 语言计数断言跳过，满额随之少 2\n'
    printf '         引导（设计方案 R1）：管理员 PowerShell 执行 Add-WindowsCapability -Online -Name Language.OCR~~~zh-CN~0.0.1\n'
    ;;
  *)
    fail "ezt ocr langs 退出码异常（0/7 之外）：rc=$LANGS_RC"
    ;;
esac

# ── 17.2 `ezt ocr probe`：引擎自检（样图识别质量基线）────────────────────────
"$EZ" ocr probe --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" --json --compact > "$WORK/ocr-probe.json" 2>"$WORK/ocr-probe.err"
PROBE_RC=$?
PROBE_CHARS=$(grep -oE '"sampleChars":[0-9]+' "$WORK/ocr-probe.json" | head -1 | cut -d: -f2)
: "${PROBE_CHARS:=0}"

case "$PROBE_RC" in
  0)
    pass "ezt ocr probe 退出码 0（引擎创建 + 样图识别达标）"
    if [ "${PROBE_CHARS:-0}" -ge 8 ]; then
      pass "样图识别字符数 ≥ 8（EZTOOLS OCR 2026 共 16 字符；实际 $PROBE_CHARS）"
    else
      fail "样图识别字符数 ≥ 8（实际 chars=$PROBE_CHARS，<8 = 引擎状态异常）"
    fi
    ;;
  1)
    # probe 的 rc=1 有两种原因：语言包缺失（与 17.1 同一环境分支）或识别质量异常。
    # 用 probe 自身的 reason 字段区分 —— 只有前者才配得上 SKIP，后者是 Fail。
    if grep -q "no-language-pack" "$WORK/ocr-probe.json"; then
      SKIPPED=$((SKIPPED + 2))
      printf '  [跳过] OCR 探针因语言包缺失未跑（rc=1 no-language-pack，同 R1 分支）—— 满额随之少 2\n'
    else
      fail "ezt ocr probe 自检未过（引擎在但识别质量异常，rc=1 非 no-language-pack）"
    fi
    ;;
  *)
    fail "ezt ocr probe 退出码异常（0/1 之外）：rc=$PROBE_RC"
    ;;
esac

# ── 17.3 设置接线：config desktop 节经 ezt config 可读写（W4-c FR-9）─────────
# 宿主设置落 config/desktop.json（伪工具节复用 P1a 全链路）。CLI 与设置窗口改的是
# 同一个文件 —— 这里从 CLI 侧验证同一条链（窗口侧由 verify-desktop 的设置清单断言）。
"$EZ" config get desktop --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" --json --compact > "$WORK/ocr-config.json" 2>"$WORK/ocr-config.err"
CONFIG_RC=$?
case "$CONFIG_RC" in
  0)
    pass "ezt config get desktop 退出码 0（宿主设置节可读，P1a 链路对 desktop.json 生效）"
    ;;
  *)
    # 旧版 CLI 不认识非工具 id 时会 4/64 —— 这是真回归（W4-c 语义），不是环境分支
    fail "ezt config get desktop 退出码 $CONFIG_RC ≠ 0（宿主设置节不可读？）"
    ;;
esac
