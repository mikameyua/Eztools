#!/usr/bin/env bash
# ============================================================================
#  budget.sh —— 轻量化预算断言（设计方案 §15）
#
#  为什么要有这个脚本：预算写在文档里就只是一句愿望。这里把它变成**断言** ——
#  超限即非零退出，可以进 CI，也可以在动了大依赖之后随手跑一次。
#
#  它检查的八件事（对应 §15.4 的上限）：
#     1. 宿主可执行目录      ≤ 1 MB    （框架依赖产物；不含 .pdb）
#     2. Python 运行时解压后  ≤ 50 MB
#     3. 基础盘              ≤ 60 MB   （bin + runtimes + sdk）
#     4. 单个工具目录        ≤ 5 MB    （重依赖应进共享池，不进工具目录）
#     5. 共享依赖池          ≤ 150 MB  （pylibs/，未建则跳过）
#     6. 安装根总计          ≤ 400 MB
#     7. ezt 冷启动          ≤ 200 ms  （中位数）
#     8. 单次调用峰值内存     ≤ 80 MB   （宿主 + 工具进程之和）
#     9. 调用后无进程残留
#
#  用法：scripts/budget.sh [--quick]
#        --quick  跳过 7/8/9（计时与内存采样，约省 6 秒）
#
#  ⚠️ 本脚本用**真实安装根**（%LOCALAPPDATA%\Eztools），因为它要量的是"装完之后多大"。
#     用 EZTOOLS_INSTALL_ROOT 可以指向别处。
# ============================================================================
set -uo pipefail

# ── 预算上限：**要调就只改这一段** ──────────────────────────────────────────
BUDGET_HOST_KB=1024        # 宿主可执行目录（框架依赖，1 MB）
BUDGET_RUNTIME_KB=51200    # Python 运行时解压后（50 MB）
BUDGET_BASELINE_KB=61440   # 基础盘 = bin + runtimes + sdk（60 MB）
BUDGET_TOOL_KB=5120        # 单个工具目录（5 MB）
BUDGET_PYLIBS_KB=153600    # 共享依赖池（150 MB）
BUDGET_TOTAL_KB=409600     # 安装根总计（400 MB）
BUDGET_COLDSTART_MS=200    # ezt 冷启动中位数
BUDGET_PEAK_KB=81920       # 单次调用进程峰值（宿主 + 工具，80 MB）

COLDSTART_SAMPLES=5
PEAK_HANG_SECONDS=4
# ────────────────────────────────────────────────────────────────────────────

QUICK=0
[ "${1:-}" = "--quick" ] && QUICK=1

# ── 路径：一律 Windows 形式（理由见 acceptance.sh 顶部的长注释）──────────────
if REPO="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null)"; then
  :
else
  REPO="$(cd "$(dirname "$0")/.." && pwd)"
fi

assert_winpath() {  # assert_winpath <描述> <路径>
  case "$2" in
    [A-Za-z]:[\\/]*) return 0 ;;
    *) printf '[错误] %s 不是 Windows 形式路径: %s\n' "$1" "$2" >&2; exit 1 ;;
  esac
}
assert_winpath "仓库根" "$REPO"

TFM="${EZTOOLS_TFM:-net10.0-windows10.0.19041.0}"
BINDIR="$REPO/src/Eztools.Cli/bin/Debug/$TFM"
EZ="$BINDIR/ezt.exe"

if [ ! -f "$EZ" ]; then
  printf '[错误] 找不到宿主 %s\n' "$EZ" >&2
  printf '       先构建：D:/dotnet10/dotnet.exe build "D:/01-项目代码/Eztools/Eztools.sln"\n' >&2
  exit 2
fi

# 🔴 DOTNET_ROOT 必须指到便携 SDK（apphost 不扫 PATH，理由见 acceptance.sh）
export DOTNET_ROOT="${DOTNET_ROOT:-D:/dotnet10}"

# 🔴 计时**不能用 shell 的 date**。
#    本环境是 MSYS：每次 `date` 都是一次进程启动（实测约 200 ms）。
#    用 `s=$(date +%s%N) ... e=$(date +%s%N)` 量 150 ms 的程序，会得到 550 ms ——
#    量到的绝大部分是**测量开销**，不是程序耗时。
#    （这个坑在 spike 阶段踩过一次：当时以为"裸解释器要 430 ms"，其实是 shell 开销。）
#    所以计时一律交给 Python：`perf_counter` 无进程启动开销。
PY="$(command -v python || command -v python3 || true)"
if [ -z "$PY" ] && [ -f "$REPO/spike/_runtime-test/python/python.exe" ]; then
  PY="$REPO/spike/_runtime-test/python/python.exe"
fi
if [ -z "$PY" ]; then
  echo '[警告] 找不到 python，冷启动计时将跳过（内存采样不受影响）' >&2
fi

# 安装根：默认真实安装根
if [ -n "${EZTOOLS_INSTALL_ROOT:-}" ]; then
  INSTALL_ROOT="$EZTOOLS_INSTALL_ROOT"
else
  INSTALL_ROOT="$LOCALAPPDATA/Eztools"
fi

# ── 工具源：**必须显式钉到仓库**（S11 教训，2026-09-23）────────────────────────
# 本脚本有意用**真实安装根**量体积（那正是"装完之后多大"的答案），所以不能整脚本
# export EZTOOLS_INSTALL_ROOT。但**凡是会拉起工具进程 / 读工具清单**的调用
# （冷启动计时、峰值内存、残留检查）必须钉住 `--tools-dir`，否则：
#   本机 %LOCALAPPDATA%\Eztools\tools\ 若有一份**陈旧安装**（它"有清单"），
#   宿主会优先用它 ⇒ 量的是**陈旧副本的启动/内存**，而不是仓库当前源码。
# 实测：本机安装根缺 `preview`、且 `paneltool` 只有 4 个面板（少一个 image），
#   分叉**已经存在**，只是恰好 probe 同版本 ⇒ 现在还没显形（潜伏地雷）。
# 判据来源：docs/验收断言审视清单.md §12（静默失败登记册 S11）。
TOOLS_ARGS=(--tools-dir "$REPO/tools")
# ↑ 用数组而非字符串：路径含空格时不会被词分割。
#   $REPO 已是 Windows 形式（上面 assert_winpath 已校验），拼 `/tools` 即合法。
assert_winpath "仓库工具目录" "$REPO/tools"

PASS=0; FAIL=0; SKIP=0
pass() { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$1"; }
fail() { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$1"; }
skip() { SKIP=$((SKIP+1)); printf '  [跳过] %s\n' "$1"; }

# 数字对账：不要靠肉眼看输出
under() {  # under <描述> <实际上限KB> <预算KB> <显示单位>
  local desc="$1" actual="$2" budget="$3" unit="${4:-KB}"
  if [ "$actual" -le "$budget" ]; then
    pass "$(printf '%s = %d %s / 上限 %d %s' "$desc" "$actual" "$unit" "$budget" "$unit")"
  else
    fail "$(printf '%s = %d %s **超出**上限 %d %s' "$desc" "$actual" "$unit" "$budget" "$unit")"
  fi
}

dusize_kb() {  # dusize_kb <目录> [du 额外参数...]
  local d="$1"; shift
  [ -d "$d" ] || { printf '0'; return; }
  du -sk "$@" "$d" 2>/dev/null | awk 'NR==1{print $1+0}'
}

# ── 内存采样：tasklist 的"内存使用"列形如 "15,988 K"，逗号是千位分隔符 ────────
#    不要用 `cut -d, -f5` —— 会被数字内部的逗号切错（踩过）。
#
#    🔴 `MSYS2_ARG_CONV_EXCL='*'` 是必需的，不是保险：Git Bash 会把 `/FI` 当成
#    POSIX 路径转换成 `C:/.../PortableGit/.../FI`，于是 tasklist 直接报
#    「无效参数/选项」，stderr 被 `2>/dev/null` 吞掉、stdout 为空 ⇒ **mem_kb 恒返回 0**
#    ⇒ 采样失败那条断言必然 FAIL（更糟的情况是静默给出错误数字）。
#    实测：不加这个变量时 `tasklist /FI ...` → 报错；加了 → 正常。
#    用 env 前缀而不用 `//FI`：前者在任何 bash 下都正确（非 MSYS 环境忽略该变量，
#    `/FI` 照常可用），后者只在 MSYS 下成立。
mem_kb() {  # mem_kb <映像名>
  MSYS2_ARG_CONV_EXCL='*' tasklist /FI "IMAGENAME eq $1" 2>/dev/null | awk -v img="$1" '
    index($1, img) == 1 { gsub(/,/, "", $(NF-1)); s += $(NF-1) }
    END { print (s ? s : 0) }'
}

echo "=================================================================="
echo " Eztools 轻量化预算核对（设计方案 §15）"
echo "=================================================================="
printf '  仓库    : %s\n' "$REPO"
printf '  安装根  : %s\n' "$INSTALL_ROOT"
printf '  TFM     : %s\n' "$TFM"
[ "$QUICK" = 1 ] && printf '  模式    : --quick（跳过计时/内存/残留）\n'

# ── 1. 宿主可执行目录 ───────────────────────────────────────────────────────
printf '\n── 磁盘\n'
HOST_KB=$(dusize_kb "$BINDIR" --exclude='*.pdb')
under "宿主可执行目录（不含 .pdb）" "$HOST_KB" "$BUDGET_HOST_KB"

# ── 2. Python 运行时（解压后）───────────────────────────────────────────────
RUNTIME_KB=$(dusize_kb "$INSTALL_ROOT/runtimes")
if [ "$RUNTIME_KB" -gt 0 ]; then
  under "Python 运行时（解压后）" "$RUNTIME_KB" "$BUDGET_RUNTIME_KB"
else
  skip "Python 运行时未部署（先跑 ezt runtime install）"
fi

# ── 3. 基础盘 = 宿主 + 运行时 + SDK ─────────────────────────────────────────
#    ⚠️ 工具**不进**基础盘 —— 这是 §15.4 的核心规则
BIN_KB=$(dusize_kb "$INSTALL_ROOT/bin")
SDK_KB=$(dusize_kb "$INSTALL_ROOT/sdk")
BASELINE_KB=$((BIN_KB + RUNTIME_KB + SDK_KB))
under "基础盘（bin+runtimes+sdk）" "$BASELINE_KB" "$BUDGET_BASELINE_KB"

# ── 4. 单个工具目录 ─────────────────────────────────────────────────────────
TOOL_WORST=0; TOOL_WORST_NAME="（无工具）"
for t in "$REPO"/tools/*/; do
  [ -d "$t" ] || continue
  kb=$(dusize_kb "$t")
  if [ "$kb" -gt "$TOOL_WORST" ]; then
    TOOL_WORST="$kb"; TOOL_WORST_NAME="$(basename "$t")"
  fi
done
if [ "$TOOL_WORST" -gt 0 ]; then
  under "单个工具目录（最大：$TOOL_WORST_NAME）" "$TOOL_WORST" "$BUDGET_TOOL_KB"
else
  skip "没找到 tools/*/ 目录"
fi

# ── 5. 共享依赖池（还没建，跳过）────────────────────────────────────────────
PYLIBS_KB=$(dusize_kb "$INSTALL_ROOT/pylibs")
if [ "$PYLIBS_KB" -gt 0 ]; then
  under "共享依赖池 pylibs/" "$PYLIBS_KB" "$BUDGET_PYLIBS_KB"
else
  skip "共享依赖池尚未建立（出现第一个重依赖工具时再建，见 §15.3）"
fi

# ── 6. 安装根总计 ───────────────────────────────────────────────────────────
TOTAL_KB=$(dusize_kb "$INSTALL_ROOT")
if [ "$TOTAL_KB" -gt 0 ]; then
  under "安装根总计（含 runtimes/toolsdata/logs）" "$TOTAL_KB" "$BUDGET_TOTAL_KB"
else
  skip "安装根不存在：$INSTALL_ROOT"
fi

if [ "$QUICK" = 1 ]; then
  printf '\n（--quick：跳过冷启动 / 峰值内存 / 进程残留）\n'
  printf '\n==================================================================\n'
  printf ' 结果: 通过 %d / 失败 %d / 跳过 %d\n' "$PASS" "$FAIL" "$SKIP"
  printf '==================================================================\n'
  [ "$FAIL" -eq 0 ] && exit 0 || exit 1
fi

# ── 7. 冷启动：取中位数，避免被一次抖动带偏 ─────────────────────────────────
#    用 Python 计时（理由见文件上方 PY 的注释：shell 的 date 会引入约 400 ms 的假开销）
printf '\n── 启动/运行开销\n'
MEDIAN=""
if [ -n "$PY" ]; then
  # ★ 只量 `ezt version`：它**不起工具进程**、也不读工具清单，所以无需 --tools-dir。
  #   （冷启动关心的是宿主自身的启动开销，钉不钉工具源都不影响；保持原样即正确。）
  MEDIAN=$("$PY" - "$EZ" "$COLDSTART_SAMPLES" <<'PY'
import statistics, subprocess, sys, time
ez, n = sys.argv[1], int(sys.argv[2])
samples = []
for _ in range(n):
    t0 = time.perf_counter()
    subprocess.run([ez, "version"], capture_output=True)
    samples.append((time.perf_counter() - t0) * 1000)
print(f"{statistics.median(samples):.0f} {' '.join(f'{s:.0f}' for s in samples)}")
PY
)
fi

if [ -n "$MEDIAN" ]; then
  MED=$(printf '%s' "$MEDIAN" | awk '{print $1}')
  SAMPLES=$(printf '%s' "$MEDIAN" | cut -d' ' -f2-)
  if [ "$MED" -le "$BUDGET_COLDSTART_MS" ]; then
    pass "ezt 冷启动中位 = ${MED} ms / 上限 ${BUDGET_COLDSTART_MS} ms（样本: $SAMPLES）"
  else
    fail "ezt 冷启动中位 = ${MED} ms **超出**上限 ${BUDGET_COLDSTART_MS} ms（样本: $SAMPLES）"
  fi
else
  skip "冷启动计时（没找到可用的 python）"
fi

# ── 8. 单次调用峰值内存（宿主 + 工具进程之和）───────────────────────────────
#    用 probe.hang 让调用挂住几秒，才能采到稳态；超时设得比挂起久，让它正常返回。
# ★ 必须带 TOOLS_ARGS：本调用**真的会拉起 probe 工具进程**，不钉数据源就会
#   跑到本机陈旧安装的 probe 上（见文件上方 TOOLS_ARGS 的注释 / S11）。
PY_BEFORE=$(mem_kb python.exe)
"$EZ" invoke probe.hang --json "{\"seconds\":$PEAK_HANG_SECONDS}" --timeout 20000 \
  "${TOOLS_ARGS[@]}" >/dev/null 2>&1 &
CALL_PID=$!

PEAK=0
for _ in $(seq 1 40); do
  kill -0 "$CALL_PID" 2>/dev/null || break
  a=$(mem_kb ezt.exe); b=$(mem_kb python.exe)
  t=$((a + b))
  [ "$t" -gt "$PEAK" ] && PEAK=$t
  sleep 0.2
done
wait "$CALL_PID" 2>/dev/null

if [ "$PEAK" -gt 0 ]; then
  under "单次调用峰值内存（宿主+工具）" "$PEAK" "$BUDGET_PEAK_KB"
else
  fail "内存采样失败：调用期间没采到进程（probe.hang 是否可用？）"
fi

# ── 9. 进程残留：用**相对判据**（与自身调用前的基线比），不受机器上其他 python 干扰 ──
sleep 0.5
PY_AFTER=$(mem_kb python.exe)
if [ "$PY_AFTER" -le "$PY_BEFORE" ]; then
  pass "调用后无工具进程残留（调用前 ${PY_BEFORE} KB → 调用后 ${PY_AFTER} KB）"
else
  fail "调用后疑似有进程残留（调用前 ${PY_BEFORE} KB → 调用后 ${PY_AFTER} KB）"
fi

printf '\n==================================================================\n'
printf ' 结果: 通过 %d / 失败 %d / 跳过 %d\n' "$PASS" "$FAIL" "$SKIP"
if [ "$FAIL" -eq 0 ]; then
  printf ' 结论: 轻量化预算全部达标 —— 基础盘 %d KB（上限 %d KB）\n' "$BASELINE_KB" "$BUDGET_BASELINE_KB"
else
  printf ' 结论: 有 %d 项超出预算 —— 见上面的 [FAIL]。改动前请先说明理由（§15.4 硬规则）\n' "$FAIL"
fi
printf '==================================================================\n'

[ "$FAIL" -eq 0 ] && exit 0 || exit 1
