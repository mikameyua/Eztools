#!/usr/bin/env bash
# ============================================================================
# audit-tool-sources.sh —— 审计脚本里"起外部进程做验证"时，数据源是否被**显式钉住**
#
# 背景（S11 类风险的普查工具）：
#   宿主 ezt.exe 的工具发现有多层回退链，其中**后两层与"环境恰好如此"有关**：
#     ① --tools-dir <路径>                 ← 显式，最可靠
#     ② EZTOOLS_TOOLS_DIR                  ← 环境变量
#     ③ 安装根 tools\（有清单则用）        ← 默认 = %LOCALAPPDATA%\Eztools\tools
#        └ 仓库 tools\（由 exe 所在位置上溯）← 不受 cwd 影响，但受"哪份 exe"影响
#   而**安装根本身**由 EZTOOLS_INSTALL_ROOT 决定（默认 = 真实 %LOCALAPPDATA%\Eztools）。
#
#   一旦本机 %LOCALAPPDATA% 下有一份**陈旧安装**（且"有清单"），
#   则**不钉 ①② 且不钉 INSTALL_ROOT 的调用**会去用那份陈旧副本 —— 测的不是仓库源码。
#
#   实测后果：新增的 `paneltool.image` 面板在自检报告里**整条消失**（不是失败行，
#   是根本没有那一行），退出码 0，零诊断。（详见 docs/验收断言审视清单.md §12 / S11）
#
# 另一类风险：**相对路径**（如 `--tools-dir tools`）—— 按 **cwd** 解析，
#   脚本从别处启动，同一个参数会指向完全不同的目录。
#
# 本脚本做三件事：
#   A. 每个脚本里真正调用 "$EZ" 的行，标注它是否显式钉了数据源（含**继承**自 acceptance.sh 的 export）
#   B. 圈出**相对路径**形态的 --tools-dir / --install-root / --config-root
#   C. 检查本机是否存在"陈旧安装根"，并列出**已与仓库分叉**的同名工具
#
# 用法：
#   bash scripts/audit-tool-sources.sh            # 全量审计（只读）
#   bash scripts/audit-tool-sources.sh --strict   # 有待修项则退出码 1
#
# 退出码：0 = 无风险 ／ 1 = 有真实风险（--strict）／ 2 = 环境问题
#
# 维护记录：
#   2026-09-23 建。第一版把"注释行"和"非调用行"都当成了调用（假阳性 30 处），
#   且没考虑 _stepNN 由 acceptance.sh source 进来、**继承其 export** 的事实 ——
#   两个假阳性叠加，把真实风险（budget.sh 的 probe.hang）淹没在噪音里。
#   ⇒ 教训：审计工具自己的判据也要有**有效性前提检查**（此处 = 该行必须真的在起进程）。
# ============================================================================
set -u

STRICT=0
[ "${1:-}" = "--strict" ] && STRICT=1

# ── 路径：绝不把 POSIX 形态交给 Windows 程序 ────────────────────────────────
if REPO="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null)"; then :; else
  REPO="$(cd "$(dirname "$0")/.." && pwd)"
fi
case "$REPO" in
  [A-Za-z]:[\\/]*) : ;;
  *) [ -n "${MSYSTEM:-}" ] && REPO="$(cygpath -w "$REPO" 2>/dev/null || printf '%s' "$REPO")" ;;
esac

SCRIPTS_DIR="$REPO/scripts"
RISK=0        # 真实风险调用数
RELPATH=0     # 相对路径形态
SHADOW=0      # 陈旧遮蔽源

echo "=================================================================="
echo " 工具源显式性审计（S11 普查）"
echo "=================================================================="
echo "  仓库: $REPO"

# ── C. 陈旧安装根检测 ───────────────────────────────────────────────────────
echo ""
echo "── C. 本机安装根检查（陈旧副本 = 遮蔽源）"

if [ -n "${LOCALAPPDATA:-}" ]; then
  IR_WIN="$LOCALAPPDATA\\Eztools"
  IR_POSIX="$(cygpath -u "$IR_WIN" 2>/dev/null || printf '%s' "$IR_WIN")"
  TOOLS_POSIX="$IR_POSIX/tools"
  if [ -d "$TOOLS_POSIX" ]; then
    MANIFESTS=$(find "$TOOLS_POSIX" -maxdepth 2 -name 'tool.json' 2>/dev/null | wc -l | tr -d ' ')
    if [ "$MANIFESTS" -gt 0 ]; then
      echo "  [遮蔽源] $IR_WIN\\tools 有 $MANIFESTS 个工具清单"
      echo "           ⚠️ 不钉 --tools-dir 且不钉 install-root 的调用会**优先用这份**"
      SHADOW=1
      echo "           与仓库分叉的同名工具："
      for repo_tool in "$REPO"/tools/*/; do
        [ -d "$repo_tool" ] || continue
        tid="$(basename "$repo_tool")"
        inst_manifest="$TOOLS_POSIX/$tid/tool.json"
        [ -f "$inst_manifest" ] || continue
        v_repo=$(grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' "$repo_tool/tool.json" 2>/dev/null | head -1 | sed 's/.*"\([^"]*\)"$/\1/')
        v_inst=$(grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' "$inst_manifest" 2>/dev/null | head -1 | sed 's/.*"\([^"]*\)"$/\1/')
        if [ "$v_repo" != "$v_inst" ]; then
          echo "             ★ $tid : 仓库=$v_repo  安装=$v_inst  ← **已分叉**"
        fi
      done
      # 特殊检查：仓库有、安装没有的工具（新增工具在陈旧安装里完全缺失）
      missing=""
      for repo_tool in "$REPO"/tools/*/; do
        [ -d "$repo_tool" ] || continue
        tid="$(basename "$repo_tool")"
        [ -f "$TOOLS_POSIX/$tid/tool.json" ] || missing="$missing $tid"
      done
      [ -n "$missing" ] && echo "             ★ 安装根**完全缺失**的工具：$missing"
    else
      echo "  [ok] 安装根 tools\\ 存在但无清单（空目录不构成遮蔽）"
    fi
  else
    echo "  [ok] 本机无 $IR_WIN\\tools（不存在遮蔽源）"
  fi
else
  echo "  [跳过] 无 LOCALAPPDATA（非 Windows 环境？）"
fi

# ── A + B. 逐脚本扫描真实调用 ───────────────────────────────────────────────
echo ""
echo "── A/B. 脚本调用扫描"
echo ""

# 判断一行是否**真的在起进程**：以 "$EZ" 或 call_core ... "$EZ" 开头（忽略前导空白）
# 排除：注释行、if/printf/赋值等只**引用** $EZ 的行
is_real_invocation() {
  local l="$1"
  # 去掉前导空白
  l="${l#"${l%%[![:space:]]*}"}"
  case "$l" in
    \#*) return 1 ;;                       # 注释
  esac
  # 必须以 "$EZ" 或包含它作为**被执行的命令**
  # 只认这几种形态：`"$EZ" ...` / `call_core ... "$EZ" ...` / `$(... "$EZ" ...)`
  grep -qE '^("?\$EZ"?|call_core[[:space:]])' <<<"$l" && return 0
  grep -qE '\$\("?\$EZ"?' <<<"$l" && return 0
  return 1
}

for f in "$SCRIPTS_DIR"/*.sh; do
  base="$(basename "$f")"
  case "$base" in
    audit-tool-sources.sh|make-*.sh) continue ;;
  esac

  # ── 继承判定：_stepNN 由 acceptance.sh source，继承其 export ──────────────
  # acceptance.sh 自己 export EZTOOLS_INSTALL_ROOT / EZTOOLS_CONFIG_ROOT
  inherited_install=0
  case "$base" in
    _step1[1-6]*.sh)
      if grep -qE 'EZTOOLS_INSTALL_ROOT=' "$SCRIPTS_DIR/acceptance.sh" 2>/dev/null; then
        inherited_install=1
      fi
      ;;
    acceptance.sh)
      grep -qE 'EZTOOLS_INSTALL_ROOT=' "$f" 2>/dev/null && inherited_install=1
      ;;
  esac

  # 收集真实调用：**必须把续行拼起来**（`\` 结尾的下一行）
  #   为什么：`--tools-dir` 常写在续行上（见 budget.sh 的 probe.hang），
  #   只看单行会把"已钉住"误判成"未钉住" —— 审计工具自己也踩了同一类坑。
  #   实现要点：续行本身**不含 $EZ**（如 `"${TOOLS_ARGS[@]}" ...`），
  #   所以不能"只在遇到 $EZ 时才 flush"，否则会在续行前提前 flush。
  # 输出格式：<起始行号>|<拼接后的整条调用>
  real_calls=$(awk '
    {
      if (pending) {
        buf = buf " " $0
        if (buf ~ /\\[[:space:]]*$/) { sub(/\\[[:space:]]*$/, "", buf); next }
        # 续行结束：输出并收尾
        if (dbg) print startln "|" buf
        pending = 0
        next
      }
      if ($0 ~ /"?\$EZ"?/) {
        startln = NR
        buf = $0
        if (buf ~ /\\[[:space:]]*$/) { sub(/\\[[:space:]]*$/, "", buf); pending = 1; next }
        print startln "|" buf
      }
    }
  ' "$f" | while IFS= read -r entry; do
                 txt="${entry#*|}"
                 if is_real_invocation "$txt"; then printf '%s\n' "$entry"; fi
               done)

  [ -z "$real_calls" ] && continue

  total=0; pinned=0; unpinned=0; list=""
  while IFS= read -r entry; do
    [ -z "$entry" ] && continue
    total=$((total+1))
    num="${entry%%|*}"
    txt="${entry#*|}"
    # 该调用自身有根参数？（txt 已是拼好续行的整条命令）
    if grep -qE -- '--(tools-dir|install-root|config-root)|TOOLS_ARGS' <<<"$txt"; then
      pinned=$((pinned+1))
    elif [ "$inherited_install" -eq 1 ]; then
      # 脚本继承了 isolated 的 EZTOOLS_INSTALL_ROOT ⇒ 工具源已隔离
      pinned=$((pinned+1))
    else
      unpinned=$((unpinned+1))
      list="${list}${num}|${txt}"$'\n'
    fi
  done <<<"$real_calls"

  if [ "$unpinned" -gt 0 ]; then
    echo "  [风险] $base : $total 处真实调用，$unpinned 处**未钉数据源**"
    RISK=$((RISK+unpinned))
    while IFS= read -r u; do
      [ -z "$u" ] && continue
      printf '           · L%s\n' "$(printf '%s' "$u" | cut -c1-105)"
    done <<<"$list"
  else
    note=""
    [ "$inherited_install" -eq 1 ] && note="（经 acceptance.sh 的 export 间接钉住）"
    echo "  [ok]   $base : $total 处真实调用均已钉住 $note"
  fi

  # B. 相对路径形态的根参数（值既不是 $VAR、也不是绝对路径）
  #    ⚠️ 必须**排除注释行** —— 否则"修法说明里写到的 `--tools-dir tools`"
  #    会被当成真实风险反复报出来（第一版就是这样自欺的）。
  rel=$(grep -nE -- '--(tools-dir|install-root|config-root)[[:space:]]+[A-Za-z_.][^"[:space:]]*' "$f" 2>/dev/null | \
        awk -F: '{ line = $0; sub(/^[0-9]+:/, "", line); if (line ~ /^[[:space:]]*#/) next; print $0 }' | \
        grep -vE -- '--(tools-dir|install-root|config-root)[[:space:]]+"?\$' | \
        grep -vE -- '--(tools-dir|install-root|config-root)[[:space:]]+"?/[a-zA-Z]' | \
        grep -vE -- '--(tools-dir|install-root|config-root)[[:space:]]+"?[A-Za-z]:' || true)
  if [ -n "$rel" ]; then
    echo "         [相对路径风险] $base:"
    printf '%s\n' "$rel" | while IFS= read -r rl; do
      printf '           · %s\n' "$(printf '%s' "$rl" | cut -c1-110)"
    done
    RELPATH=$((RELPATH+1))
  fi
done

# ── D. "函数在定义之前被调用"检测 ───────────────────────────────────────────
# 本普查**顺带抓到的真 bug**（acceptance.sh）：`assert_winpath` 定义在 L51，
# 却先在 L31 被调用 ⇒ bash 从上往下执行 ⇒ 只往 stderr 打 `command not found`，
# 脚本无 set -e 就继续跑 ⇒ **断言静默空转**（"校验路径形态"这道门一直形同虚设）。
# 判据：本脚本内定义的函数，其**首次真实调用**出现在定义行之前。
echo ""
echo "── D. 定义先于调用检查（断言空转的典型形态）"

ORDERFAIL=0
for f in "$SCRIPTS_DIR"/*.sh; do
  base="$(basename "$f")"
  case "$base" in
    audit-tool-sources.sh) continue ;;
  esac

  fns=$(grep -oE '^[[:space:]]*(function[[:space:]]+)?[A-Za-z_][A-Za-z0-9_]*[[:space:]]*\(\)[[:space:]]*\{' "$f" 2>/dev/null | \
        sed -E 's/^[[:space:]]*(function[[:space:]]+)?([A-Za-z_][A-Za-z0-9_]*).*/\2/' | sort -u)
  [ -z "$fns" ] && continue

  flagged=""
  for fn in $fns; do
    defln=$(grep -nE "^[[:space:]]*(function[[:space:]]+)?${fn}[[:space:]]*\(\)" "$f" 2>/dev/null | head -1 | cut -d: -f1)
    [ -z "$defln" ] && continue
    # 首次真实调用行（排除：定义行自身、注释行）
    # ⚠️ 曾经这里多写了一条 `if (txt ~ "…" fn "()") next` 想"跳过定义行"——
    #    但 ERE 里 `()` 是**空分组**，等价于零个括号 ⇒ 它把 `fn "..."` 这种**普通调用**
    #    也一并匹配掉了 ⇒ 所有行都被 skip ⇒ 本检查**恒绿**（又一次"断言空转"）。
    #    定义行已由上面的 `$1 == d { next }` 排除，不需要这条。
    useln=$(grep -nE "(^|[^A-Za-z0-9_])${fn}[[:space:]]" "$f" 2>/dev/null | \
            awk -F: -v d="$defln" '
              $1 == d { next }
              { txt = $0; sub(/^[0-9]+:/, "", txt)
                t2 = txt; sub(/^[[:space:]]+/, "", t2)
                if (t2 ~ /^#/) next
                print $1; exit }')
    if [ -n "$useln" ] && [ "$useln" -lt "$defln" ] 2>/dev/null; then
      flagged="${flagged}    · ${fn}()  定义在 L${defln}，却在 L${useln} 先被调用\n"
    fi
  done

  if [ -n "$flagged" ]; then
    echo "  [风险] $base:"
    printf "${flagged}"
    ORDERFAIL=$((ORDERFAIL+1))
  fi
done

if [ "$ORDERFAIL" -eq 0 ]; then
  echo "  [ok]   所有脚本的自定义函数都在调用之前定义"
fi

# ── 总结 ────────────────────────────────────────────────────────────────────
echo ""
echo "=================================================================="
echo " 总结"
echo "=================================================================="
echo "  未钉数据源的真实调用 : $RISK"
echo "  相对路径形态的脚本   : $RELPATH"
echo "  定义顺序可疑的脚本   : $ORDERFAIL"
if [ "$SHADOW" -eq 1 ]; then
  echo "  本机陈旧遮蔽源       : 有"
else
  echo "  本机陈旧遮蔽源       : 无"
fi
echo ""

if [ "$RISK" -eq 0 ] && [ "$RELPATH" -eq 0 ] && [ "$ORDERFAIL" -eq 0 ]; then
  echo "  结论：所有调用的数据源均已显式钉住，且无函数定义顺序问题。"
  exit 0
fi

echo "  判定口径："
echo "   · '未钉数据源' = 该 \$EZ 调用既无 --tools-dir/--install-root/--config-root，"
echo "     所在脚本也未继承 EZTOOLS_INSTALL_ROOT ⇒ 依赖默认值，即**本机真实安装根**。"
if [ "$SHADOW" -eq 1 ]; then
  echo "   · 本机**确有陈旧安装**，故上述调用**正在测那份副本**，而非仓库源码。"
fi
echo "   · '相对路径' = 根参数写成 tools 这类相对名，按 cwd 解析；"
echo "     脚本从别处启动就会指向不同目录。"
echo "   · '定义顺序' = 函数在定义之前被调用 ⇒ bash 报 command not found 但不中止，"
echo "     该断言**静默空转**（校验形同虚设）。"
echo ""
echo "  修法：数据源显式传死（绝对路径）；函数统一放到文件上方。"
echo "        详见 docs/验收断言审视清单.md §12.2 规则 A。"

[ "$STRICT" -eq 1 ] && exit 1
exit 0
