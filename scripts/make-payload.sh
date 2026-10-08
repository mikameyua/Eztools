#!/usr/bin/env bash
# ============================================================================
#  make-payload.sh —— 把一份独立的 CPython 安装打成 Eztools 运行时载荷
#
#  产物：<输出目录>/python-<版本>-<rid>.tar.gz
#  宿主首次运行时会把它解压到 %LOCALAPPDATA%\Eztools\runtimes\python\<版本>\
#
#  为什么是"压缩包 + 首次解压"而不是直接拷贝：
#     实测 53MB 直接拷贝需 42 秒（不可接受），tar.gz 约 15~20MB，解压远快于拷贝。
#
#  推荐的来源（实测排序）：
#     ✅ python-build-standalone 的 install_only 产物 —— 完整安装布局、自带 pip、专为再分发设计
#     ✅ python.org Windows 安装器 —— 完整安装布局，也可重定位
#     ❌ python.org embeddable zip —— 带 ._pth、没有 pip、site 被禁用。不要用
#
#  用法：
#     scripts/make-payload.sh <源 Python 目录> [输出目录] [--minimal]
#
#     例（本机 spike 验证用的那份运行时）：
#       scripts/make-payload.sh spike/_runtime-test/python payload
# ============================================================================
set -euo pipefail

SRC="${1:-}"
OUT_DIR="${2:-payload}"
MINIMAL=0

for arg in "$@"; do
  case "$arg" in
    --minimal) MINIMAL=1 ;;
  esac
done

if [ -z "$SRC" ]; then
  echo "用法: scripts/make-payload.sh <源 Python 目录> [输出目录] [--minimal]" >&2
  echo "" >&2
  echo "  <源 Python 目录>  含 python.exe 的独立安装目录（不是 venv、不是 embeddable zip）" >&2
  exit 64
fi

if [ ! -f "$SRC/python.exe" ] && [ ! -f "$SRC/bin/python3" ]; then
  echo "[错误] $SRC 下没有 python.exe —— 请指向独立安装的运行时根目录" >&2
  exit 1
fi

PY="$SRC/python.exe"
[ -f "$PY" ] || PY="$SRC/bin/python3"

# ── 1. 体检：不符合可重定位要求就直接拒绝 ────────────────────────────────────
echo "== 运行时体检 =="

if [ -f "$SRC/pyvenv.cfg" ]; then
  echo "[错误] 检测到 pyvenv.cfg：$SRC 是一个虚拟环境，不是独立安装。" >&2
  echo "       虚拟环境不可重定位（内部记录创建时的解释器路径）。" >&2
  exit 1
fi

if ls "$SRC"/*._pth >/dev/null 2>&1; then
  echo "[错误] 检测到 ._pth 文件：这是 python.org 的 embeddable zip 布局。" >&2
  echo "       它禁用了 site、且不含 pip，会成为长期麻烦。请改用 python-build-standalone 的 install_only 产物。" >&2
  exit 1
fi

if [ ! -d "$SRC/Lib" ]; then
  echo "[错误] 缺少 Lib/ 目录，布局不完整" >&2
  exit 1
fi

VERSION="$("$PY" -I -X utf8 -c 'import sys;print(".".join(map(str,sys.version_info[:3])))')"
MACHINE="$("$PY" -I -X utf8 -c 'import platform;m=platform.machine().lower();print({"amd64":"x64","x86_64":"x64","arm64":"arm64","aarch64":"arm64"}.get(m,m))')"

if [ "$(uname -s | cut -c1-5)" = "MINGW" ] || [ "$(uname -s | cut -c1-6)" = "MSYS_N" ]; then
  RID="win-$MACHINE"
else
  RID="$(uname -s | tr '[:upper:]' '[:lower:]')-$MACHINE"
fi

echo "  版本      : $VERSION"
echo "  架构      : $MACHINE（RID $RID）"
echo "  源目录    : $SRC"
echo "  可重定位  : 通过（无 pyvenv.cfg、无 ._pth）"

# ── 2. 裁剪清单 ──────────────────────────────────────────────────────────────
# 剔除的都是"开发期用得上、运行期用不到"的东西。基础裁剪几乎零风险，
# --minimal 会额外去掉 pip / 头文件 / 静态库（想用自带 pip 给工具装依赖时不要用）。
EXCLUDES=(
  --exclude='*/__pycache__'
  --exclude='./Lib/test'
  --exclude='./Lib/idlelib'
  --exclude='./Lib/turtledemo'
  --exclude='./Lib/lib2to3'
  --exclude='./Lib/tkinter'
  --exclude='./tcl'
  --exclude='./Doc'
  --exclude='./share'
  --exclude='./DLLs/_tkinter.pyd'
  --exclude='./DLLs/tcl*.dll'
  --exclude='./DLLs/tk*.dll'
  --exclude='./DLLs/*_test*.pyd'
)

if [ "$MINIMAL" = "1" ]; then
  echo "  裁剪模式  : minimal（额外去掉 pip / include / libs / ensurepip）"
  EXCLUDES+=(
    --exclude='./Lib/site-packages/pip'
    --exclude='./Lib/site-packages/pip-*'
    --exclude='./Lib/site-packages/*.dist-info'
    --exclude='./Lib/ensurepip'
    --exclude='./include'
    --exclude='./libs'
    --exclude='./Scripts'
  )
else
  echo "  裁剪模式  : 基础（保留 pip，便于工具作者用自带运行时把依赖 vendor 到工具的 Lib/）"
fi

# ── 3. 打包 ─────────────────────────────────────────────────────────────────
# 路径形态有讲究（都是实测踩出来的）：
#   · tar 的 -f 参数会把 `D:/x` 解析成"远程主机 D 上的 /x"（rsh 语法）→ 报 "Cannot connect to D: resolve failed"
#     所以归档文件名一律用"裸文件名"，靠 subshell 的 cd 定位，绕开路径形态问题。
#   · -C 只当目录用，不吃 host:path 语法，Windows 形式路径反而更稳。
#   · 不能把回退写成 `A && B || C && D`：B 成功时 D 也会执行，会把两份输出拼进同一个变量。
if SRC_ABS="$(cd "$SRC" && pwd -W 2>/dev/null)"; then
  :
else
  SRC_ABS="$(cd "$SRC" && pwd)"
fi

mkdir -p "$OUT_DIR"
ARCHIVE_NAME="python-$VERSION-$RID.tar.gz"
echo "  源绝对路径: $SRC_ABS"

echo ""
echo "== 打包 =="
START=$(date +%s)

# 先写 .partial 再改名：避免中断时留下一个看起来可用的半成品载荷
#
# ★★ -h（--dereference）是**必须的**，不是可选优化 ★★
#   源 Python 目录里可能含**符号链接**（GitHub Actions 的 setup-python 就是：
#   hostedtoolcache 下的 python.exe 等是指向绝对路径的链接）。
#   不加 -h 时 tar 原样存链接，宿主解压时报：
#     Extracting the Tar entry '...' would have resulted in a link target
#     outside the specified destination directory
#   —— 因为链接目标是宿主机绝对路径（如 /c/hostedtoolcache/...），落在解压
#   目录之外，.NET 的 TarFile 出于路径穿越防护**直接拒绝**。
#   后果极具误导性：载荷在打包机上看完全正常，只有**换机器部署**才炸。
#   -h 让 tar 存实际文件内容 ⇒ 解压后是普通文件，可重定位。
(
  cd "$OUT_DIR"
  tar -czhf "$ARCHIVE_NAME.partial" -C "$SRC_ABS" "${EXCLUDES[@]}" .
  mv -f "$ARCHIVE_NAME.partial" "$ARCHIVE_NAME"
)
ARCHIVE="$OUT_DIR/$ARCHIVE_NAME"

ELAPSED=$(( $(date +%s) - START ))

# ── 4. 校验：根目录必须有解释器，否则宿主解压后会判定结构异常 ─────────────────
# 注意不能用 `tar -tzf ... | grep -q`：grep -q 命中即退出会让 tar 收到 SIGPIPE，
# 在 `set -o pipefail` 下整条管道被判为失败（判定结果与事实相反）。改成先完整读取再匹配。
ARCHIVE_LIST="$(tar -tzf "$ARCHIVE")" || {
  echo "[错误] 载荷无法读取（tar 解不开）" >&2
  exit 1
}
EXE_ENTRIES="$(printf '%s\n' "$ARCHIVE_LIST" | grep -cE '^\./(python\.exe|bin/python3)$' || true)"

if [ "$EXE_ENTRIES" -lt 1 ]; then
  echo "[错误] 载荷根目录下没有解释器（python.exe / bin/python3），宿主解压后会拒绝安装" >&2
  exit 1
fi

# ── 4b. 断言：载荷里不得含符号链接 ──────────────────────────────────────────
# 与上面 tar 的 -h 配对：-h 负责解引用，这里负责**证明它真的生效了**。
# 为什么值得单独一条断言：删掉 -h 后本脚本在打包机上一路绿灯（载荷看起来
# 完全正常），只有**宿主在另一台机器上解压**时才炸，且错误信息
# （"link target outside the specified destination directory"）不会指向根因。
# 这类"打包期看不出来、部署期才暴露"的问题必须在这里拦下。
#
# 判据用 tar -tvf 的**类型位**（首列第 1 字符）：'l' = 符号链接。
# 代价：多读一次归档（41 MB 约 1~3s）。换来的是一条能真红的防线，值。
ARCHIVE_TV="$(tar -tvzf "$ARCHIVE")" || {
  echo "[错误] 载荷无法读取（tar -tv 解不开）" >&2
  exit 1
}
LINK_COUNT="$(printf '%s\n' "$ARCHIVE_TV" | grep -cE '^l' || true)"

if [ "$LINK_COUNT" -gt 0 ]; then
  echo "[错误] 载荷内含 $LINK_COUNT 个符号链接 —— 宿主解压会拒绝（跨目录链接目标）" >&2
  echo "       这通常意味着 tar 的 -h（解引用）没生效。示例：" >&2
  printf '%s\n' "$ARCHIVE_TV" | grep -E '^l' | head -3 >&2
  exit 1
fi

ENTRY_COUNT="$(printf '%s\n' "$ARCHIVE_LIST" | grep -c . || true)"

SIZE_BYTES=$(stat -c %s "$ARCHIVE" 2>/dev/null || stat -f %z "$ARCHIVE")
SIZE_MB=$(( SIZE_BYTES / 1048576 ))
SHA="$(sha256sum "$ARCHIVE" | cut -d' ' -f1)"

echo ""
echo "== 完成 =="
echo "  载荷    : $ARCHIVE"
echo "  大小    : ${SIZE_MB} MB（$SIZE_BYTES 字节）"
echo "  条目数  : $ENTRY_COUNT"
echo "  sha256  : $SHA"
echo "  耗时    : ${ELAPSED}s"
echo ""
echo "宿主会自动扫描该目录（%LOCALAPPDATA%\\Eztools\\payload 或仓库 payload/）。"
echo "验证：ezt runtime payloads && ezt runtime install"
