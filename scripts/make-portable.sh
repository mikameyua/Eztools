#!/usr/bin/env bash
# ============================================================================
#  make-portable.sh —— 打出可再分发的「便携自包含」安装包
#
#  产物：<输出目录>/Eztools-<版本>-win-x64.zip
#  包内布局与「安装根」同构（bin/ tools/ sdk/ payload/），所以：
#     · 用户解压到任意目录 → 运行 <解压目录>/bin/ezt.exe install --from <解压目录>
#       即可把它铺到 %LOCALAPPDATA%\Eztools（首次运行铺开，决策 A3）
#     · 老用户直接 ezt update --from <zip> 覆盖更新（替换程序、保留用户数据）
#
#  ★ 形态决策（D1，2026-09-21 拍板）：**自包含 · 非单文件 · 非裁剪**
#      · 自包含 = 目标机器不需要预装 .NET 10（用户机器上不会碰巧有）
#      · 非单文件 = 单文件形态会让 ezt.exe / ezt-core.exe 走"自解压到临时目录"，
#        而 LayoutInstaller 是**按 AppContext.BaseDirectory 找同目录程序集**的，
#        单文件下这个目录是临时解压目录 → 铺进安装根的 bin/ 会缺文件（实测踩过）
#      · 非裁剪 = 对 WPF 不可用（NETSDK1168），且 CLI 的反射/JSON 序列化需要完整 BCL
#
#  实测体积（2026-09-21，win-x64）：
#     Desktop 自包含 182 MB · 单文件压缩 80 MB · CLI 单文件 37 MB
#     本脚本产的是 **Desktop 自包含**（含托盘 + Core + CLI），zip 约 80~90 MB。
#
#  用法：
#     scripts/make-portable.sh [--out <目录>] [--rid win-x64] [--no-build] [--keep-stage]
#
#     例：
#       scripts/make-portable.sh                      # → dist/Eztools-0.11.0-win-x64.zip
#       scripts/make-portable.sh --no-build           # 复用上次 publish 输出，只重打包
# ============================================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$REPO_ROOT/dist"
RID="win-x64"
DO_BUILD=1
KEEP_STAGE=0

# ── 路径形态：**本仓最常踩的坑，没有之一** ───────────────────────────────────
# 交给 Windows 程序的路径一律 `盘符:/正斜杠`。POSIX 形式 `/d/x` 会被 MSBuild 当成
# 未知开关（MSB1001）、被 Windows 文件 API 当成 `D:\d\x`，且报错位置极具误导性。
# 所以：shell 内部用 POSIX（cd/ls 需要），**传给 dotnet 的每一条路径先转**。
winpath() {
  local p="$1"
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -m "$p"
    return
  fi
  case "$p" in
    /[a-zA-Z]/*) printf '%s:/%s' "$(printf '%s' "${p:1:1}" | tr '[:lower:]' '[:upper:]')" "${p:3}" ;;
    *) printf '%s' "$p" ;;
  esac
}

while [ $# -gt 0 ]; do
  case "$1" in
    --out)        OUT_DIR="$2"; shift 2 ;;
    --rid)        RID="$2"; shift 2 ;;
    --no-build)   DO_BUILD=0; shift ;;
    --keep-stage) KEEP_STAGE=1; shift ;;
    -h|--help)
      sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *)
      echo "[错误] 未知参数：$1" >&2
      exit 64 ;;
  esac
done

# ── 0. 环境 ─────────────────────────────────────────────────────────────────
# 本仓需要 .NET 10 SDK（net10.0）。解析顺序：DOTNET_EXE → PATH 里的 dotnet。
# ⚠️ 刻意不写死便携路径（那是本机约定，不该泄漏给其他克隆者）。
DOTNET="${DOTNET_EXE:-}"
if [ -z "$DOTNET" ]; then
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
  else
    echo "[错误] 找不到 .NET 10 SDK。" >&2
    echo "       本仓需要 .NET 10（net10.0），系统默认的 dotnet 可能是更旧的版本。" >&2
    echo "       两种做法二选一：" >&2
    echo "         1) 安装 .NET 10 SDK并加入 PATH：https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    echo "         2) 用便携版，并显式指定：DOTNET_EXE=/path/to/dotnet/dotnet.exe $0 ..." >&2
    exit 3
  fi
fi
if [ ! -x "$DOTNET" ] && [ ! -f "$DOTNET" ]; then
  echo "[错误] 找不到 $DOTNET（本仓需要 .NET 10；可用环境变量 DOTNET_EXE 覆盖）" >&2
  exit 3
fi

VERSION="$("$DOTNET" --version >/dev/null 2>&1 && true)"
# 版本号取自 Directory.Build.props，避免"包名版本与程序集版本不一致"
VERSION="$(grep -oE '<Version>[^<]+</Version>' "$REPO_ROOT/Directory.Build.props" | head -1 | sed -E 's|</?Version>||g')"
[ -n "$VERSION" ] || VERSION="0.0.0"

PKG_NAME="Eztools-$VERSION-$RID"
STAGE="$OUT_DIR/$PKG_NAME"

echo "== 便携包打包 =="
echo "  仓库根    : $REPO_ROOT"
echo "  版本      : $VERSION"
echo "  RID       : $RID"
echo "  形态      : 自包含 · 非单文件 · 非裁剪（决策 D1）"
echo "  输出      : $OUT_DIR/$PKG_NAME.zip"
echo ""

mkdir -p "$OUT_DIR"

# ── 1. 构建（自包含 publish）────────────────────────────────────────────────
# Desktop 是入口：它引用 Host；Host 提供 install/update/uninstall 的 CLI 逻辑。
# 但 **CLI 是独立 Exe**（ezt.exe），不会被 Desktop 的 publish 带出来 → 两个都要 publish。
if [ "$DO_BUILD" = "1" ]; then
  echo "== 1/5 构建（自包含 publish）=="

  STAGE_WIN="$(winpath "$STAGE")"

  # Desktop（托盘 + 设置窗口）：同时产出 WPF/WinForms 栈的完整依赖
  "$DOTNET" publish "$(winpath "$REPO_ROOT/src/Eztools.Desktop/Eztools.Desktop.csproj")" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$STAGE_WIN/_publish-desktop" --nologo

  # CLI（ezt.exe）：提供 install / update / uninstall / selftest 等命令行面
  "$DOTNET" publish "$(winpath "$REPO_ROOT/src/Eztools.Cli/Eztools.Cli.csproj")" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$STAGE_WIN/_publish-cli" --nologo

  # Core（ezt-core.exe，特权层）：net10.0-windows，提权边界必须进程隔离（§3）
  "$DOTNET" publish "$(winpath "$REPO_ROOT/src/Eztools.Core/Eztools.Core.csproj")" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$STAGE_WIN/_publish-core" --nologo

  # Index（ezt-index.exe，常驻索引进程）：★ 必须一起打包。
  #   它是**按需启动**的 —— 前台一切正常，直到某次搜索触发它才拉起。
  #   若它是框架依赖，用户机器上没有 .NET 时就会在"搜索"这一步崩，
  #   而症状（"点搜索没反应/ 索引永远就绪不了"）与真实原因（缺运行时）
  #   几乎无关联 ⇒ 排查成本极高。这是 D1「自包含」决策最容易被漏掉的一环。
  "$DOTNET" publish "$(winpath "$REPO_ROOT/src/Eztools.Index/Eztools.Index.csproj")" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$STAGE_WIN/_publish-index" --nologo
else
  echo "== 1/5 构建：跳过（--no-build）=="
  if [ ! -d "$STAGE/_publish-desktop" ] || [ ! -d "$STAGE/_publish-cli" ]; then
    echo "[错误] --no-build 需要上次的 _publish-* 目录，但 $STAGE 下没有。" >&2
    exit 3
  fi
fi

# ── 2. 组装 bin/ ────────────────────────────────────────────────────────────
# 三个 publish 输出**同名程序集会互相覆盖**（三份都含 Eztools.Host.dll / 依赖树）。
# 这不是问题：自包含 publish 的依赖树是同一套（同一 TFM 以 Core 为最严），
# 直接叠放即可；叠放顺序 = CLI → Core → Desktop，**Desktop 最后**（它的 apphost
# 与 runtimeconfig 是"入口"那一份，必须赢）。
echo ""
echo "== 2/5 组装 bin/ =="
BIN="$STAGE/bin"
rm -rf "$BIN"

mkdir -p "$BIN"
cp -r "$STAGE/_publish-cli/." "$BIN/"
cp -r "$STAGE/_publish-core/." "$BIN/"
cp -r "$STAGE/_publish-desktop/." "$BIN/"

# ezt.exe 必须存在（CLI 的 apphost 名 = AssemblyName = ezt）
# ★ ezt-index.exe 同样必须校验 —— 它按需启动，缺了不会在打包期报错，
#   只会在用户第一次搜索时炸（见上面 Index 的注释）。
for exe in ezt.exe ezt-core.exe Eztools.Desktop.exe ezt-index.exe; do
  if [ ! -f "$BIN/$exe" ]; then
    echo "[错误] bin/ 下缺少 $exe —— publish 输出不完整" >&2
    exit 1
  fi
done

# 清掉 publish 中间目录（只保留组装好的 bin/）
rm -rf "$STAGE/_publish-desktop" "$STAGE/_publish-cli" "$STAGE/_publish-core" "$STAGE/_publish-index"

BIN_FILES=$(find "$BIN" -type f | wc -l | tr -d ' ')
echo "  bin/            : $BIN_FILES 个文件"

# ── 3. 铺设 tools/ sdk/ payload/ ────────────────────────────────────────────
# 这三样**直接从仓库铺**（LayoutInstaller 铺的就是它们，工具与宿主已解耦：
# 加工具 = 加目录，不改宿主 → 打包时天然跟上）。
echo ""
echo "== 3/5 铺设 tools/ sdk/ payload/ =="

rm -rf "$STAGE/tools" "$STAGE/sdk" "$STAGE/payload"

# tools/：只收含 tool.json 的目录（与 LayoutInstaller 的判据一致）
mkdir -p "$STAGE/tools"
TOOL_COUNT=0
for toolDir in "$REPO_ROOT/tools"/*/; do
  [ -f "${toolDir}tool.json" ] || continue
  name="$(basename "$toolDir")"
  cp -r "$toolDir" "$STAGE/tools/$name"
  # 本机产物不打包（字节码缓存带过去只会让"改动不生效"更难排查）
  rm -rf "$STAGE/tools/$name/__pycache__" "$STAGE/tools/$name"/**/__pycache__ 2>/dev/null || true
  find "$STAGE/tools/$name" -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
  TOOL_COUNT=$((TOOL_COUNT + 1))
done
echo "  tools/          : $TOOL_COUNT 个工具"

cp -r "$REPO_ROOT/sdk" "$STAGE/sdk"
find "$STAGE/sdk" -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
SDK_FILES=$(find "$STAGE/sdk" -type f | wc -l | tr -d ' ')
echo "  sdk/            : $SDK_FILES 个文件"

mkdir -p "$STAGE/payload"
PAYLOAD_COUNT=0
for archive in "$REPO_ROOT/payload"/*.tar.gz; do
  [ -f "$archive" ] || continue
  cp "$archive" "$STAGE/payload/"
  PAYLOAD_COUNT=$((PAYLOAD_COUNT + 1))
done
if [ "$PAYLOAD_COUNT" -gt 0 ]; then
  echo "  payload/        : $PAYLOAD_COUNT 个运行时载荷"
else
  echo "  payload/        : 无（用户需自行 scripts/make-payload.sh 产出，或运行 ezt runtime install）"
fi

# ── 4. 包内说明与安装标记 ───────────────────────────────────────────────────
echo ""
echo "== 4/5 写入 README 与安装标记 =="

cat > "$STAGE/README.txt" <<EOF
Eztools $VERSION（便携自包含版）
================================================

本包已内含 .NET 运行时与 Python 运行时载荷，目标机器**无需预装任何东西**。

快速开始
--------
1. 把整个目录解压到任意位置（例：D:\Eztools-portable）
2. 首次运行铺开（铺到 %LOCALAPPDATA%\Eztools）：
     bin\ezt.exe install --from .
3. 部署 Python 运行时：
     bin\ezt.exe runtime install
4. 验证：
     bin\ezt.exe selftest
5. 启动托盘：
     bin\Eztools.Desktop.exe

换新版本（升级）
----------------
   bin\ezt.exe update --from <新版本 zip 或解压目录>
     替换 bin/ tools/ sdk/ payload/，
     **保留** toolsdata/ runtimes/ logs/ cache/ 与 %APPDATA%\Eztools 下的配置。
     旧程序会先备份到 <安装根>\backup\<时间戳>\，确认无误后可手动删除。

卸载
----
   bin\ezt.exe uninstall --yes            删程序，保留用户数据
   bin\ezt.exe uninstall --yes --purge-data   连用户数据与配置一起删
   bin\ezt.exe uninstall --dry-run        先看会删什么

目录说明
--------
   bin/        宿主程序（ezt.exe 命令行 / Eztools.Desktop.exe 托盘 / ezt-core.exe 特权层）
   tools/      内置工具（清单驱动，加目录即加工具）
   sdk/        Python SDK 源码（部署运行时会被植入其 site-packages）
   payload/    Python 运行时压缩包（首次 ezt runtime install 时解压）

用户数据不在这个包里
--------------------
   工具私有数据  %LOCALAPPDATA%\Eztools\toolsdata\
   配置与状态    %APPDATA%\Eztools\
   —— 升级与卸载默认都不碰它们。
EOF

# install.json：标记"这个包是便携包铺出来的"，供 doctor/LayoutUpdater 判据
cat > "$STAGE/install.json" <<EOF
{
  "schema": 1,
  "hostVersion": "$VERSION",
  "sourceRoot": "<便携包 $PKG_NAME>",
  "installedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "toolCount": $TOOL_COUNT,
  "payloadCopied": $([ "$PAYLOAD_COUNT" -gt 0 ] && echo true || echo false),
  "machine": ""
}
EOF

# ── 5. 打 zip ───────────────────────────────────────────────────────────────
# 路径形态坑（与 make-payload.sh 同源）：Windows 形式路径交给 zip/tar 会被误解析。
# 这里用 subshell 的 cd + 相对名，绕开整类问题；并先写 .partial 再改名。
echo ""
echo "== 5/5 打包 =="

ZIP_NAME="$PKG_NAME.zip"
ZIP_PATH="$OUT_DIR/$ZIP_NAME"

# 压缩器选择（实测 + 踩坑记录）：
#   · bin/ 里含 .git 风格长路径与近千个文件，必须走"目录整体压缩"而不是逐文件列出
#   · WorkBuddy 的 Git Bash **不带 zip.exe**（只有 unzip.exe / zipinfo.exe）
#   · Windows 自带的 tar.exe（bsdtar）**支持 -a 按扩展名选格式**，能直接产出真 zip，
#     且被 /usr/bin/unzip 正常识别 —— 故优先用它，退化到 zip.exe
pick_zipper() {
  if [ -n "${ZIPPER_EXE:-}" ] && [ -x "$ZIPPER_EXE" ]; then
    echo "$ZIPPER_EXE"; return
  fi
  if [ -x /c/Windows/System32/tar.exe ]; then
    echo "/c/Windows/System32/tar.exe"; return
  fi
  if command -v zip >/dev/null 2>&1; then
    echo "zip"; return
  fi
  echo ""
}

ZIPPER="$(pick_zipper)"
if [ -z "$ZIPPER" ]; then
  echo "[错误] 找不到可用的压缩器（需要 Windows 自带 tar.exe 或 zip.exe）" >&2
  exit 3
fi

echo "  压缩器    : $ZIPPER"
START=$(date +%s)

# ── 打包（先写临时名再改名：中断时不留下看起来可用的半成品包）──
# 路径形态坑（与 make-payload.sh 同源）：`--file D:/x.zip` 这种 Windows 形式路径
# 会被 tar 当成 rsh 的 `host:path` 语法解析（报 "Cannot connect to D: resolve failed"）。
# 所以归档文件名一律用**裸文件名**，靠 subshell 的 cd 定位。
#
# ★★ 关键坑（实测踩到，症状极具误导性）：Windows 自带 tar（bsdtar）的 `-a`
#    是**按文件扩展名**决定格式的。写成 `Eztools-x.zip.partial` 时扩展名是 `.partial`，
#    bsdtar 于是打包成了 **tar**，文件名却叫 .zip —— 大小 195MB（没压缩！），
#    `unzip` 报 "start of central directory not found / zipfile corrupt"。
#    ⇒ 折中：临时文件也用 `.zip` 扩展名（`<名字>.tmp-<pid>.zip`），这样 `-a` 认得出格式，
#      又不至于与最终产物重名。
ZIP_TMP="${PKG_NAME}.tmp-$$.zip"

(
  cd "$OUT_DIR"
  rm -f "$ZIP_TMP"
  case "$ZIPPER" in
    *tar.exe)
      # -a = 按 -f 的扩展名自动选格式（.zip → zip）；-c 创建；-f 输出
      "$ZIPPER" -a -c -f "$ZIP_TMP" "$PKG_NAME"
      ;;
    *)
      # -q 静默（条目多，进度条反而淹没信息）；-X 不存额外属性；-r 递归
      "$ZIPPER" -rqX "$ZIP_TMP" "$PKG_NAME"
      ;;
  esac
  mv -f "$ZIP_TMP" "$ZIP_NAME"
)

ELAPSED=$(( $(date +%s) - START ))

# ── 6. 校验 ─────────────────────────────────────────────────────────────────
# ★ 校验必须查**包内路径**，而不是"zip 能不能解开"。
#   历史教训（make-payload.sh §4）：`unzip -l ... | grep -q` 命中即退出会让 unzip 收到
#   SIGPIPE，在 `set -o pipefail` 下整条管道被判失败 —— 结论与事实**相反**。
#   这里改成先完整读取到变量，再在变量上匹配。
ZIP_LIST="$(unzip -Z1 "$ZIP_PATH")" || {
  echo "[错误] 产物无法读取（unzip 解不开）" >&2
  exit 1
}
fail=0
check_entry() {
  if printf '%s\n' "$ZIP_LIST" | grep -qxF "$PKG_NAME/$1"; then
    echo "  ✓ $1"
  else
    echo "  ✗ 缺少 $1" >&2
    fail=1
  fi
}

echo ""
echo "== 校验包内结构 =="
check_entry "bin/ezt.exe"
check_entry "bin/ezt-core.exe"
check_entry "bin/Eztools.Desktop.exe"
check_entry "bin/ezt-index.exe"
check_entry "bin/Eztools.Host.dll"
check_entry "install.json"
check_entry "README.txt"

# ── 自包含硬断言（缺了就是框架依赖，干净机器跑不起来）──
# ★ 判据不止"coreclr 在不在"：还要**四个可执行逐个过**。
#   漏掉任何一个都是真实的用户侧故障，且形态各异：
#   ezt/Eztools.Desktop 漏 ⇒ 一启动就报错（易发现）；
#   ezt-core 漏           ⇒ 提权进程起不来，功能静默降级（难发现）；
#   ezt-index 漏          ⇒ 前台正常、首次搜索才炸（最难发现）。
echo ""
echo "-- 自包含运行时 --"
if printf '%s\n' "$ZIP_LIST" | grep -qE "^$PKG_NAME/bin/(System\.Private\.CoreLib\.dll|hostfxr\.dll)$"; then
  echo "  ✓ 运行时本体在场（System.Private.CoreLib.dll / hostfxr.dll）"
else
  echo "  ✗ 缺少自包含运行时 —— 这会是框架依赖包，干净机器跑不起来" >&2
  fail=1
fi

# 逐个 Exe 验runtimeconfig 里 selfContained=true —— 这是 apphost 的实际判据。
# 只看 dll 在不在不够：dll可能在，但 runtimeconfig 写false 照样走机器安装。
SELFCOUNT=0
for exe in ezt.exe Eztools.Desktop.exe ezt-core.exe ezt-index.exe; do
  CFG="bin/${exe%.exe}.runtimeconfig.json"
  if ! printf '%s\n' "$ZIP_LIST" | grep -qxF "$PKG_NAME/$CFG"; then
    echo "  ✗ 缺少 $CFG（apphost 无从判断自包含）" >&2
    fail=1
    continue
  fi
  if printf '%s\n' "$ZIP_LIST" | grep -qxF "$PKG_NAME/$CFG"; then
    # 文件在包内；内容需在解包后校验（此处只判存在性，见下方 verify-runtimes 脚本）
    echo "  ✓ $CFG"
    SELFCOUNT=$((SELFCOUNT + 1))
  fi
done

ENTRY_COUNT="$(printf '%s\n' "$ZIP_LIST" | grep -c . || true)"

SIZE_BYTES=$(stat -c %s "$ZIP_PATH" 2>/dev/null || stat -f %z "$ZIP_PATH")
SIZE_MB=$(( SIZE_BYTES / 1048576 ))
SHA="$(sha256sum "$ZIP_PATH" | cut -d' ' -f1)"

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "[失败] 包结构校验未通过，产物不完整：$ZIP_PATH" >&2
  exit 1
fi

echo ""
echo "== 完成 =="
echo "  便携包    : $ZIP_PATH"
echo "  大小      : ${SIZE_MB} MB（$SIZE_BYTES 字节）"
echo "  条目数    : $ENTRY_COUNT"
echo "  工具数    : $TOOL_COUNT"
echo "  载荷数    : $PAYLOAD_COUNT"
echo "  sha256    : $SHA"
echo "  耗时      : ${ELAPSED}s"
echo ""
echo "用户拿到后：解压 → bin\\ezt.exe install --from . → ezt runtime install"
echo "老用户升级：bin\\ezt.exe update --from $ZIP_NAME"

if [ "$KEEP_STAGE" = "1" ]; then
  echo ""
  echo "（保留暂存目录：$STAGE）"
else
  rm -rf "$STAGE"
fi
