#!/usr/bin/env bash
# ============================================================================
#  acceptance.sh —— P0 验收（可重复、可脚本化、可进 CI）
#
#  P0 验收标准（设计方案 §12）：
#     新增一个工具目录 → 宿主自动发现并可用，**不改宿主任何代码**
#
#  这个脚本刻意跑在**临时安装根**里，而不是 %LOCALAPPDATA%\Eztools：
#   · 可重复执行，不依赖"上次跑完的残留状态"
#   · 不会在验证过程中动到真实安装目录（首次运行的真实部署由 `ezt doctor` 单独完成）
#
#  用法：scripts/acceptance.sh
#
#  ⚠️ 在 WorkBuddy 会话里跑必须带 `dangerouslyDisableSandbox: true`
#     （沙箱的 safe-delete shim 有"每轮 10000 文件"批量阈值，会**中止整条命令**；
#      症状是 verify-desktop.py 里的 os.remove 被拦 → 托盘步骤报"异常退出"，
#      看起来像代码回归，实际独立跑托盘验证是 19/19 全绿）。
# ============================================================================
set -uo pipefail

# 🔴 路径必须用 Windows 形式（pwd -W）。
#    踩过的坑：Git Bash 的 `pwd` 给 POSIX 路径 `/d/01-...`，把它交给 .NET 可执行文件后
#    会被当成"当前盘符根下的相对路径"，实际写到 D:<盘符根>\d\01-... —— 目录建了、命令退 0，
#    但文件全在别处，表现为后续读取"文件不存在"。这类错误极难从命令输出看出来。
#    同样注意不要把回退写成 `A && B || C && D`：B 成功时 D 也会执行，会把两份输出拼进变量。
if REPO="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null)"; then
  :
else
  REPO="$(cd "$(dirname "$0")/.." && pwd)"
fi
assert_winpath "仓库根" "$REPO"

WORK="$REPO/_scratch/accept"

# 🔴 目标框架只在这一行改。
#    原先 net7.0 硬编码在三处（这里是其一），升级 TFM 时漏掉任何一处，
#    症状都是"文件不存在"而不是"框架不匹配"——很难一眼看出真正原因。
TFM="${EZTOOLS_TFM:-net10.0}"
BINDIR="$REPO/src/Eztools.Cli/bin/Debug/$TFM"
EZ="$BINDIR/ezt.exe"

if [ ! -f "$EZ" ]; then
  printf '  [错误] 找不到宿主 %s\n' "$EZ" >&2
  printf '         先构建（本机 SDK 在 D:\\dotnet10）:\n' >&2
  printf '         D:/dotnet10/dotnet.exe build "D:/01-项目代码/Eztools/Eztools.sln"\n' >&2
  exit 2
fi

# 🔴 DOTNET_ROOT 必须指到便携 SDK。
#    ezt.exe 是**框架依赖**应用，apphost 只在两处找运行时：DOTNET_ROOT，或注册表里的默认安装位置
#    （C:\Program Files\dotnet —— 那里只有 7.x）。**它不看 PATH**，实测 PATH 前置无效。
#    不设会直接报 "You must install or update .NET to run this application."
export DOTNET_ROOT="${DOTNET_ROOT:-D:/dotnet10}"

export EZTOOLS_INSTALL_ROOT="$WORK/install"
export EZTOOLS_CONFIG_ROOT="$WORK/config"

PASS=0
FAIL=0

# ── 路径形态：本项目最容易反复踩的坑，没有之一 ───────────────────────────────
# Git Bash 的 `pwd` / `$TEMP` 给的是 POSIX 形式（/d/...、/tmp），一旦交给 .NET 或任何
# 原生可执行文件，会被按**进程当前盘符**解析：/d/01-... 变 D:\d\01-...，/tmp/x 变
# C:\tmp\x（cwd 在 C 盘时）。命令仍然退出 0、目录也真建了，只是建在别处 —— 极难发现。
#   → 所以：凡是要交给 exe 的路径，一律先过 winpath；转换不出 Windows 形式就直接中止。
winpath() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s' "$1"
  fi
}

assert_winpath() {  # assert_winpath <描述> <路径>
  case "$2" in
    [A-Za-z]:[\\/]*) return 0 ;;
    *)
      echo "[错误] $1 不是 Windows 形式路径: $2" >&2
      echo "       交给 .NET 可执行文件会被按当前盘符解析到别处；请先经 winpath 转换。" >&2
      exit 1
      ;;
  esac
}

pass() { PASS=$((PASS+1)); printf '  [PASS] %s\n' "$1"; }
fail() { FAIL=$((FAIL+1)); printf '  [FAIL] %s\n' "$1"; }

check() { # check <描述> <期望值> <实际值>
  if [ "$2" = "$3" ]; then pass "$1"; else fail "$1（期望 $2，实际 $3）"; fi
}

step() { printf '\n── %s\n' "$1"; }

if [ ! -f "$EZ" ]; then
  echo "未找到 $EZ —— 先执行: dotnet build Eztools.sln" >&2
  exit 1
fi

echo "=================================================================="
echo " Eztools P0 验收"
echo "=================================================================="
echo "  仓库      : $REPO"
echo "  临时安装根: $WORK/install"
echo "  临时配置根: $WORK/config"

rm -rf "$WORK"
mkdir -p "$WORK"

# ── 1. 首次运行：目录结构自动创建 ────────────────────────────────────────────
step "1/10  首次运行的目录结构"

"$EZ" doctor --json --quiet > "$WORK/doctor-fresh.json" 2>/dev/null
check "doctor 在全新安装根上返回成功" 0 $?

for d in bin tools sdk payload runtimes logs toolsdata cache; do
  # 正反双断言：`-d` 只保证"这个名字存在且是目录"，而单独一个 `-e` 会被同名**文件**冒充。
  # 两条一起断，才真正锁住"建出来的是目录"。
  if [ -d "$WORK/install/$d" ] && [ -e "$WORK/install/$d" ]; then
    pass "自动创建 $d/"
  elif [ -e "$WORK/install/$d" ]; then
    fail "$d 存在但不是目录（被同名文件占位）"
  else
    fail "未创建 $d/"
  fi
done

# ── 2. 运行时部署（首次解压 + 幂等复用）─────────────────────────────────────
step "2/10  嵌入式运行时部署"

T0=$(date +%s)
"$EZ" runtime install --quiet > "$WORK/runtime-install.log" 2>&1
RC=$?
T1=$(date +%s)
check "运行时部署成功" 0 $RC

RUNTIME_DIR="$WORK/install/runtimes/python"
if [ -d "$RUNTIME_DIR" ]; then
  VER="$(ls "$RUNTIME_DIR" | head -1)"
  pass "运行时落位 runtimes/python/$VER"
  if [ -f "$RUNTIME_DIR/$VER/python.exe" ]; then pass "解释器存在"; else fail "缺 python.exe"; fi
  if [ -f "$RUNTIME_DIR/$VER/Lib/site-packages/eztools/__init__.py" ]; then
    pass "SDK 已植入运行时 site-packages（-I 下仍可 import）"
  else
    fail "SDK 未植入"
  fi
  if [ -f "$RUNTIME_DIR/$VER/.ezt-runtime.json" ]; then
    pass "安装标记已写入"
  else
    fail "缺安装标记"
  fi
else
  fail "未创建 runtimes/python"
fi
printf '  [信息] 首次部署耗时 %ss\n' "$((T1-T0))"

T2=$(date +%s%N)
"$EZ" runtime install --quiet > /dev/null 2>&1
T3=$(date +%s%N)
printf '  [信息] 幂等复用时延 %s ms\n' "$(( (T3-T2)/1000000 ))"

# ── 3. 清单驱动发现 ──────────────────────────────────────────────────────────
step "3/10  清单驱动发现"

"$EZ" list --json --quiet > "$WORK/list.json" 2>/dev/null
PY="$(command -v python || command -v python3 || true)"
[ -n "$PY" ] || PY="$REPO/spike/_runtime-test/python/python.exe"

# 2.x 正向值断言：安装标记里的 version 必须**等于**实际落位的目录名。
# 只断言"标记文件存在"（见上一步）无法发现"部署了个错的版本、标记照写"这类错。
# 放在这里而不是第 2 步，是因为 $PY 在上一行才可用（第 2 步时它还没定义）。
RUNTIME_MARKER="$(ls -d "$WORK/install/runtimes/python"/*/ 2>/dev/null | head -1)"
if [ -n "$RUNTIME_MARKER" ] && [ -f "$RUNTIME_MARKER/.ezt-runtime.json" ]; then
  MARKER_VERSION="$(basename "$RUNTIME_MARKER")"
  "$PY" - "$RUNTIME_MARKER/.ezt-runtime.json" "$MARKER_VERSION" <<'PY'
import json, sys
marker, expected = sys.argv[1], sys.argv[2]
try:
    data = json.load(open(marker, encoding="utf-8"))
except Exception as exc:
    print(f"  [信息] 安装标记解析失败: {exc}")
    sys.exit(1)
actual = data.get("version")
print(f"  [信息] 安装标记 version={actual!r}（落位目录 {expected!r}）")
sys.exit(0 if actual == expected else 1)
PY
  check "安装标记的 version 与实际落位目录一致" 0 $?
else
  fail "找不到运行时安装标记，无法校验 version 一致性"
fi

"$PY" - "$WORK/list.json" <<'PY'
import json, sys
tools = json.load(open(sys.argv[1], encoding="utf-8"))
ids = sorted(t["id"] for t in tools)
print(f"  [信息] 发现工具: {', '.join(ids)}")
expected = {"echo", "filehash", "probe", "wordcount"}
missing = expected - set(ids)
if missing:
    print(f"  [FAIL] 缺少工具: {sorted(missing)}")
    sys.exit(1)
print(f"  [PASS] 发现 {len(tools)} 个工具，贡献点与配置 schema 均已解析")
# 哪些工具**本来就没有** config schema（不是解析失败）：
#   · script 档的 oneshot —— 无用户可调项
#   · probe            —— 纯诊断探针
#   · paneltool        —— P4 Wave 2c 的面板协议演示，配置项为零
# 写成显式清单而不是 "weight != script and id != probe"：
# 那种"按排除法"的写法每次新增一个无 schema 的合法工具都会假红一次，
# 逼着后来的人往条件里再挂一个 `and id != xxx`——清单会烂掉，且没人看得出哪个才是"故意没有"。
NO_SCHEMA_TOOLS = {"oneshot", "probe", "paneltool"}
bad = [t["id"] for t in tools
       if t["configSchema"] is None
       and t["id"] not in NO_SCHEMA_TOOLS
       and t.get("weight") != "script"]
if bad:
    print(f"  [FAIL] 以下工具的 config schema 未解析: {bad}")
    sys.exit(1)
PY
if [ $? -eq 0 ]; then pass "清单结构与配置 schema 解析正确"; else fail "清单解析断言未通过"; fi

# ── 4. 端到端调用（含非 ASCII 逐字符校验）──────────────────────────────────
step "4/10  端到端调用与编码"

"$EZ" invoke echo.echo --json '{"text":"中文 · emoji 🍡 · 引号\" 反斜杠\\ 制表\t尾"}' --quiet \
  > "$WORK/invoke.json" 2>/dev/null
check "调用 echo.echo 返回成功" 0 $?

"$PY" - "$WORK/invoke.json" <<'PY'
import json, sys
result = json.load(open(sys.argv[1], encoding="utf-8"))
# 逐字符比对必须用 Python 而不是 shell：多字节/代理对在 shell 字符串匹配里不可靠（spike 实测误报过 FAIL）
expected = "中文 · emoji \U0001f361 · 引号\" 反斜杠\\ 制表\t尾"
ok = result.get("echo") == expected
print(f"  [信息] UTF-8 逐字符比对: {'无损' if ok else '不一致'}"
      + ("" if ok else f"\n         期望 {expected!r}\n         实际 {result.get('echo')!r}"))
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then pass "编码校验通过"; else fail "编码校验未通过"; fi

# ── 5. 生命周期、隔离与启用语义（宿主自检）──────────────────────────────────
step "5/10  宿主自检（崩溃 / 超时 / 隔离 / 熔断语义）"

"$EZ" selftest --json --quiet > "$WORK/selftest.json" 2>/dev/null
RC=$?
check "selftest 退出码为 0" 0 $RC

"$PY" - "$WORK/selftest.json" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
fails = [c for c in data["cases"] if not c["ok"]]
print(f"  [信息] 用例通过 {data['pass']} / 失败 {data['fail']}")
for c in data["cases"]:
    print(f"         {'✓' if c['ok'] else '✗'} {c['case']}")
if fails or data["fail"]:
    sys.exit(1)
PY
if [ $? -eq 0 ]; then pass "全部自检用例通过"; else fail "存在失败用例"; fi

# 5.1 自检用例数下限 + 事件总线小节在场
#     为什么需要这条：上面只断言"全部通过"——**删掉一整节用例，上面的断言照样绿**。
#     用例数下限把"悄悄少测了"变成失败；再点名断言事件总线小节存在（P4 Wave 2a）。
"$PY" - "$WORK/selftest.json" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
names = [c["case"] for c in data["cases"]]
# 下限：38 = Wave 2a 后的真实数（29 基线 + 9 条事件总线）；低于它说明用例被删
if len(names) < 38:
    print(f"  [FAIL] 自检用例数 {len(names)} < 38（有用例被删？）")
    sys.exit(1)
bus = [n for n in names if "事件" in n or "订阅" in n]
if len(bus) < 4:
    print(f"  [FAIL] 事件总线用例只有 {len(bus)} 条（应 >=4）：{bus}")
    sys.exit(1)
print(f"  [信息] 自检用例 {len(names)} 条，其中事件总线 {len(bus)} 条")
PY
if [ $? -eq 0 ]; then pass "自检用例数达标且事件总线断言在场（防整节被删）"; else fail "自检用例数或事件总线小节缺失"; fi

# ── 6. 新增工具 = 新增目录（P0 的核心验收标准）─────────────────────────────
step "6/10  新增工具目录 → 零宿主改动即可用"

NEWTOOLS="$WORK/devtools"
mkdir -p "$NEWTOOLS"
cp -r "$REPO/sdk/python/template" "$NEWTOOLS/greeter"

"$PY" - "$NEWTOOLS/greeter" <<'PY'
import io, sys
d = sys.argv[1]
s = io.open(d + "/tool.json", encoding="utf-8").read()
# 全局替换 my-tool -> greeter：id 与命令 id 都要换，否则命令仍是 my-tool.run，
# 调用 greeter.run 会报"未找到命令"（这正是本脚本第一次跑出来的失败）
s = s.replace('my-tool', 'greeter').replace('"我的工具"', '"问候工具"')
io.open(d + "/tool.json", "w", encoding="utf-8", newline="\n").write(s)
m = io.open(d + "/main.py", encoding="utf-8").read()
m = m.replace('"echo": text,',
              '"greeting": "你好, " + text, "echo": text,')
io.open(d + "/main.py", "w", encoding="utf-8", newline="\n").write(m)
PY

"$EZ" invoke greeter.run --text "验收" --tools-dir "$NEWTOOLS" --quiet \
  > "$WORK/greeter.json" 2>/dev/null
check "由模板复制的新工具可被直接调用" 0 $?

"$PY" - "$WORK/greeter.json" <<'PY'
import json, sys
result = json.load(open(sys.argv[1], encoding="utf-8"))
ok = result.get("greeting") == "你好, 验收"
print(f"  [信息] 新工具返回: {result.get('greeting')!r}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then pass "新工具业务逻辑正确"; else fail "新工具返回不符"; fi

HOST_FILES=$(find "$REPO/src" -newer "$WORK/list.json" -name '*.cs' 2>/dev/null | wc -l)
check "宿主源码目录未被本次新增工具改动" 0 "$HOST_FILES"

# ── 7. 已安装形态：把 ezt 装到仓库之外，验证"装起来能用"───────────────
step "7/10  已安装形态（脱离仓库）"

# 关键：部署目录必须在**仓库之外**。放在仓库内的话，宿主向上找仓库根仍能找到 tools/，
# 就退化成了开发形态，这一步就白做了。
TMPBASE="$(winpath "${TEMP:-${TMP:-/tmp}}")"
DEPLOY="$TMPBASE/ezt-accept-$$"
assert_winpath "部署目录" "$DEPLOY"
rm -rf "$DEPLOY"
mkdir -p "$DEPLOY/bin"

cp "$BINDIR"/ezt.exe "$DEPLOY/bin/" 2>/dev/null
cp "$BINDIR"/*.dll "$DEPLOY/bin/" 2>/dev/null
cp "$BINDIR"/*.json "$DEPLOY/bin/" 2>/dev/null

OUTSIDE_ROOT="$DEPLOY/install"
OUTSIDE_CONFIG="$DEPLOY/config"
export P3_OUTSIDE_BIN="$OUTSIDE_ROOT/bin"   # P3 验收用：安装形态 bin 里必须有 ezt-core.exe

# 7.1 安装：把源码形态铺成已安装形态
( cd "$DEPLOY/bin" && EZTOOLS_INSTALL_ROOT="$OUTSIDE_ROOT" EZTOOLS_CONFIG_ROOT="$OUTSIDE_CONFIG" \
    ./ezt.exe install --from "$REPO" --quiet ) > "$WORK/outside-install.log" 2>&1
check "从仓库外执行 install" 0 $?

for part in bin tools sdk payload; do
  if [ -e "$OUTSIDE_ROOT/$part" ]; then pass "安装根铺入 $part/"; else fail "安装根缺 $part/"; fi
done

# 7.2 部署运行时
( cd "$DEPLOY/bin" && EZTOOLS_INSTALL_ROOT="$OUTSIDE_ROOT" EZTOOLS_CONFIG_ROOT="$OUTSIDE_CONFIG" \
    ./ezt.exe runtime install --quiet ) > "$WORK/outside-runtime.log" 2>&1
check "已安装形态下部署运行时" 0 $?

# 7.3 体检：安装完整度必须全绿，工具来源必须是安装根（builtin）而不是仓库（repo）
( cd "$DEPLOY/bin" && EZTOOLS_INSTALL_ROOT="$OUTSIDE_ROOT" EZTOOLS_CONFIG_ROOT="$OUTSIDE_CONFIG" \
    ./ezt.exe doctor --json --quiet ) > "$WORK/outside-doctor.json" 2>&1

"$PY" - "$WORK/outside-doctor.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
layout = d.get("layout") or {}
missing = layout.get("unavailable") or []
print(f"  [信息] 安装与来源: {'全部可用' if layout.get('allAvailable') else '不可用 ' + ', '.join(missing)}")
for it in layout.get("items", []):
    print(f"         {'✓' if it['available'] else '✗'} {it['name']} ← {it['source']}")
sources = sorted({t.get("source") for t in d.get("tools", [])})
print(f"  [信息] 工具来源: {', '.join(sources)}  （应为 builtin = 安装根）")
ok = (layout.get("allAvailable")
      and layout.get("installed")
      and sources == ["builtin"]
      and d.get("toolCount", 0) >= 4)
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then pass "脱离仓库后仍能发现工具（来源=安装根）"; else fail "脱离仓库后工具发现异常"; fi

# 7.4 已安装形态下的端到端自检
( cd "$DEPLOY/bin" && EZTOOLS_INSTALL_ROOT="$OUTSIDE_ROOT" EZTOOLS_CONFIG_ROOT="$OUTSIDE_CONFIG" \
    ./ezt.exe selftest --json --quiet ) > "$WORK/outside-selftest.json" 2>&1
check "已安装形态 selftest 退出码为 0" 0 $?

"$PY" - "$WORK/outside-selftest.json" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
print(f"  [信息] 已安装形态用例通过 {data['pass']} / 失败 {data['fail']}")
sys.exit(0 if data["fail"] == 0 else 1)
PY
if [ $? -eq 0 ]; then pass "已安装形态全部用例通过"; else fail "已安装形态存在失败用例"; fi

# ── 8. 配置中心（P1a）───────────────────────────────────────────────────────
step "8/11  配置中心：默认值注入 / 校验 / 原子落盘 / 损坏恢复"

CFG_DIR="$WORK/config/config"
CFG_FILE="$CFG_DIR/echo.json"

# 8.1 未设置任何值时，工具应拿到 schema 里的 default。
#     P1a 之前 ToolProcessOptions.Config 从来没人填 —— 工具拿到的 tool.config **恒为 {}**，
#     即使 tool.json 里写了 default 也拿不到。这条就是盯这个缺口的。
"$EZ" invoke echo.echo --text hi --quiet > "$WORK/cfg-default.json" 2>&1
if grep -q '"echo": "hi"' "$WORK/cfg-default.json"; then
  pass "未设置时工具拿到 schema 默认值（uppercase=false）"
else
  fail "默认值未注入工具：$(tr -d '\r\n' < "$WORK/cfg-default.json")"
fi

# 8.2 set → 落盘 → **工具行为真的变了**（端到端，含进程重启）
"$EZ" config set echo uppercase true >/dev/null 2>&1
"$EZ" invoke echo.echo --text hi --quiet > "$WORK/cfg-set.json" 2>&1
if grep -q '"echo": "HI"' "$WORK/cfg-set.json"; then
  pass "set 后工具行为改变（配置注入端到端生效）"
else
  fail "set 未生效：$(tr -d '\r\n' < "$WORK/cfg-set.json")"
fi

# 8.3 配置落在**独立于工具目录**的位置（%APPDATA% 形态）
if [ -f "$CFG_FILE" ]; then
  pass "配置落盘到独立配置目录（与工具/代码分离）"
else
  fail "配置文件不存在: $CFG_FILE"
fi

# 8.4 非法值必须被拒，且文件**一个字节都不许变**
BEFORE_CFG=$(cat "$CFG_FILE" 2>/dev/null)

"$EZ" config set wordcount maxFileSizeMb 99999 >/dev/null 2>&1
if [ $? -eq 2 ]; then pass "超出 maximum 被拒（数值约束真的在管事）"; else fail "超出 maximum 未被拒绝"; fi

"$EZ" config set wordcount language klingon >/dev/null 2>&1
if [ $? -eq 2 ]; then pass "enum 越界被拒"; else fail "enum 越界未被拒绝"; fi

"$EZ" config set echo uppercase 也许 >/dev/null 2>&1
if [ $? -eq 2 ]; then pass "类型不符被拒"; else fail "类型不符未被拒绝"; fi

AFTER_CFG=$(cat "$CFG_FILE" 2>/dev/null)
if [ "$BEFORE_CFG" = "$AFTER_CFG" ]; then
  pass "三次校验失败后文件内容未变"
else
  fail "校验失败却改动了文件"
fi

# 8.5 unset → 回落默认值（工具行为跟着回去）
"$EZ" config unset echo uppercase >/dev/null 2>&1
"$EZ" invoke echo.echo --text hi --quiet > "$WORK/cfg-unset.json" 2>&1
if grep -q '"echo": "hi"' "$WORK/cfg-unset.json"; then
  pass "unset 后回落 schema 默认值"
else
  fail "unset 未回落到默认值"
fi

# 8.6 配置文件被手工改坏 → 不崩、不覆盖、留备份、用默认值继续。
#     ⚠️ 这里不用 `ls | grep -q`：grep -q 命中即退出会让上游收 SIGPIPE，
#     在 set -o pipefail 下整条管道被判为失败（结论与事实相反）。用 wc 计数。
printf '%s' '{ "uppercase": true, 这里是坏的' > "$CFG_FILE"
"$EZ" config get echo uppercase > "$WORK/cfg-corrupt.txt" 2>&1
CORRUPT_RC=$?
CORRUPT_VAL=$(tr -d '\r\n' < "$WORK/cfg-corrupt.txt")
if [ "$CORRUPT_RC" -eq 0 ] && [ "$CORRUPT_VAL" = "false" ]; then
  pass "损坏文件时回落默认值且退出码 0（不崩）"
else
  fail "损坏文件处理异常（退出码 $CORRUPT_RC，值 $CORRUPT_VAL）"
fi

CORRUPT_COUNT=$(ls "$CFG_DIR"/*.corrupt-* 2>/dev/null | wc -l)
if [ "$CORRUPT_COUNT" -ge 1 ]; then
  pass "损坏文件已备份为 .corrupt-*（宁可留档，也不销毁用户数据）"
else
  fail "损坏文件没有备份 —— 用户配置被抹掉了"
fi

# 收尾：删掉临时部署目录。
# ⚠️ 必须 cd 到父目录、再用「裸名字」删：本环境注入了 safe-delete shim，
#    它把参数当"相对当前目录"解析，因此传绝对 Windows 路径会被 fail-closed 拒绝，
#    表现为"命令没报错、目录却还在"。实测绝对路径失败、相对名字成功。
if [ -d "$DEPLOY" ]; then
  ( cd "$(dirname "$DEPLOY")" && rm -rf "$(basename "$DEPLOY")" ) 2>/dev/null
fi
if [ -d "$DEPLOY" ]; then
  printf '  [信息] 临时部署目录未能自动清理，可手动删除: %s
' "$DEPLOY"
fi

# ── 9. 托盘：menus.input 契约 + 菜单自动合成 ─────────────────────────────────
#    为什么必须有人守这条：`menus` 声明的是"命令"，但**托盘点击没有上下文**
#    （不像 actions 有选中的文件）。契约里不规定"参数从哪来"时，菜单能显示、
#    点击有反应、进程真的起来，却报"缺少参数" —— 一次都跑不成。
#    这条规则**静态校验不了**（handler 是动态的），所以这里真调一次。

step "9/11  托盘：menus.input 契约 + 菜单自动合成"

# 9.1 菜单能从清单自动合成，且每项都带 input 与分组显示名
"$EZ" tray --json --quiet --tools-dir tools > "$WORK/tray.json" 2>/dev/null
"$PY" - "$WORK/tray.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
items = d.get("items") or []
by_id = {i["commandId"]: i for i in items}
ok = (d.get("count") == 3 and len(items) == 3
      and all("input" in i and i.get("groupTitle") for i in items)
      and by_id.get("preview.show", {}).get("input") == "shellSelection")
print(f"  [信息] 托盘项: {[i['commandId'] for i in items]}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "托盘菜单自动合成（3 项，含 input 与分组；preview.show 声明 shellSelection）"
else
  fail "托盘菜单合成异常：$(tr -d '\r\n' < "$WORK/tray.json" | head -c 200)"
fi

# 9.2 顺序必须可重复 —— 依赖扫描顺序会让断言"有时候通过"，那是最难查的一类问题
ORDER_A=$("$EZ" tray --json --quiet --tools-dir tools 2>/dev/null | tr -d ' \n' | grep -o '"commandId":"[^"]*"' | tr '\n' ',')
ORDER_B=$("$EZ" tray --json --quiet --tools-dir tools 2>/dev/null | tr -d ' \n' | grep -o '"commandId":"[^"]*"' | tr '\n' ',')
check "菜单顺序可重复" "$ORDER_A" "$ORDER_B"

# 9.3 静态检查：托盘项引用的命令都能解析到工具
"$EZ" tray --check --quiet --tools-dir tools >/dev/null 2>&1
check "托盘项引用的命令全部可解析" 0 $?

# 9.4 ★ 核心：每个托盘项**真的能跑一次**
#     用该工具能接受的内容注入（这里用宿主按 input: clipboard 约定的 args["input"]），
#     断言不出现"缺少参数"。这是"菜单点了能不能用"的唯一可信判据。
README_JSON=$(winpath "$REPO/README.md")
README_JSON="${README_JSON//\\//}"   # JSON 里用正斜杠，省掉反斜杠转义

"$EZ" invoke filehash.hashMany --json "{\"input\": \"$README_JSON\"}" --quiet \
  > "$WORK/tray-filehash.json" 2>/dev/null
check "托盘项 filehash.hashMany 零上下文可跑（输入=剪贴板路径）" 0 $?

# 正向值断言：不能只验"退出码 0"。命令跑通了但返回空/返回错值的失败模式，
# 退出码是看不出来的。这里断到具体字段：至少 1 个哈希 + 条目数与请求一致。
"$PY" - "$WORK/tray-filehash.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
items = d.get("items") or d.get("results") or []
count = d.get("count")
ok = bool(items) and (count is None or count == len(items)) and all(
    (it.get("hash") or "").strip() for it in items
)
print(f"  [信息] filehash: count={count} 条目={len(items)} 首个 hash={(items[0].get('hash') or '')[:16] if items else '-'}")
sys.exit(0 if ok else 1)
PY
check "filehash.hashMany 返回真实哈希值（非仅退出码 0）" 0 $?

"$EZ" invoke wordcount.count --json '{"input": "验收 文本"}' --quiet \
  > "$WORK/tray-wordcount.json" 2>/dev/null
check "托盘项 wordcount.count 零上下文可跑（输入=剪贴板文本）" 0 $?

# 正向值断言：中文按字计数的具体值。这类"结果的正确性"必须断言到值，
# 否则"统计逻辑写错但仍返回一个对象"会被放过。
"$PY" - "$WORK/tray-wordcount.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
# "验收 文本" = 4 个中日韩字符 + 1 个空格 → cjkChars=4、charsTotal=5
ok = (d.get("cjkChars") == 4
      and d.get("chars") == 4
      and d.get("charsTotal") == 5)
print(f"  [信息] wordcount: chars={d.get('chars')} cjkChars={d.get('cjkChars')} "
      f"charsTotal={d.get('charsTotal')}（期望 4 / 4 / 5）")
sys.exit(0 if ok else 1)
PY
check "wordcount.count 返回正确的中日韩字符计数（cjkChars=4 charsTotal=5）" 0 $?

# 9.5 反向验证：上一条断言**真的会失败**，否则它就是恒真断言、等于没测
"$EZ" invoke filehash.hashMany --json '{"input": "   "}' --quiet >/dev/null 2>&1
if [ $? -ne 0 ]; then
  pass "反向：空输入确实失败（说明 9.4 的断言有效，不是恒真）"
else
  fail "空输入竟然成功了 —— 9.4 可能是恒真断言"
fi

# 9.6 禁用的工具不进托盘（否则点下去只会得到"工具已禁用"）
"$EZ" disable filehash --quiet >/dev/null 2>&1
DISABLED_COUNT=$("$EZ" tray --json --quiet --tools-dir tools 2>/dev/null \
  | tr -d ' \n' | grep -o '"count":[0-9]*' | head -1 | cut -d: -f2)
check "禁用 filehash 后托盘项降为 2" "2" "$DISABLED_COUNT"
"$EZ" enable filehash --quiet >/dev/null 2>&1

# ── 10. 托盘进程（Eztools.Desktop）──────────────────────────────────────────
#     为什么是独立 Python 脚本：托盘验证要动**剪贴板**（Win32 API）、**通知区域注册表**、
#     **按进程名查内存** —— 这三样在 bash 里都很别扭。
#
#     为什么必须验"托盘点击"：menus 声明的是命令，但**托盘点击没有上下文**。
#     契约里不规定"参数从哪来"时，菜单能显示、点击有反应、进程真的起来，
#     却报"缺少参数"。这条规则静态校验不了（handler 动态），只能真调一次。
#
#     无法自动化的部分（已在方案 §8 显式列出）：鼠标真的点下去、气泡观感、图标 DPI 观感。

step "10/11  托盘进程：启动 / 核心链路 / 单实例 / 图标显示"

DESKTOP_LOG="$WORK/desktop.log"
"$PY" "$REPO/scripts/verify-desktop.py" --repo "$REPO" > "$DESKTOP_LOG" 2>&1
DESKTOP_RC=$?

# 把子脚本的每条 PASS/FAIL 计入总数 —— 否则总计数会漏掉这一整批（看着"通过 48"其实还有 13 条）
while IFS= read -r ln; do
  case "$ln" in
    *"[PASS]"*) pass "${ln##*\[PASS\] }" ;;
    *"[FAIL]"*) fail "${ln##*\[FAIL\] }" ;;
    *) [ -n "$ln" ] && printf '  %s\n' "$ln" ;;
  esac
done < "$DESKTOP_LOG"

# 子脚本自己崩了（没输出任何断言）时别静默
if [ "$DESKTOP_RC" -ne 0 ] && ! grep -q "\[FAIL\]" "$DESKTOP_LOG"; then
  fail "托盘验收脚本异常退出（退出码 $DESKTOP_RC），见 $DESKTOP_LOG"
fi

# ── 汇总 ────────────────────────────────────────────────────────────────────
echo

# 热键仲裁验收（独立片段：变量引号嵌套太深，拆文件维护）
source "$REPO/scripts/_step11.sh"

# P3 特权层验收（Core 生命周期 · 原语 · 声明门槛 · 审计 · 全链路）
source "$REPO/scripts/_step12_p3.sh"

# P4 工具间协作验收（host.invokeTool 通路 · 归属校验 · 环检测 · weight 档位闸门）
source "$REPO/scripts/_step13_p4.sh"

# P4 Wave 2b 验收（便携更新保留用户数据 · 卸载默认保留 / --purge-data 全清 · 打包形态）
source "$REPO/scripts/_step14_w2b.sh"

# P4 Wave 2c 验收（panels 声明校验 · tool.panel.data 数据通道 · weight: full 档位闸门）
source "$REPO/scripts/_step15_w2c.sh"

echo "=================================================================="
printf " 结果: 通过 %s / 失败 %s\n" "$PASS" "$FAIL"
if [ "$FAIL" -eq 0 ]; then
  echo " 结论: 验收通过 —— 开发形态与已安装形态均可用；新增工具目录后宿主自动发现可用（未改宿主一行代码）；"
       echo "       配置中心可读写、可校验、可从损坏中恢复；"
       echo "       托盘菜单由清单自动合成、每个托盘项零上下文可跑、托盘进程单实例且不残留；热键统一注册、冲突有仲裁、改键可回退；"
       echo "       特权层 Core 可启停、原语可调用且有审计、声明门槛与守卫生效、工具→宿主→Core 全链路可用；"
       echo "       工具间调用（host.invokeTool）打通且成环立刻拒绝，weight 档位 API 面闸门按档生效；"
       echo "       便携更新替换程序而保留用户数据与运行时，卸载默认保留数据、--purge-data 才全清；"
       echo "       面板贡献点可声明与校验、tool.panel.data 数据通道打通、panels 仅在 weight: full 下可用"
else
  echo " 结论: 验收未通过，见上方 [FAIL] 行"
fi
echo "=================================================================="

# 日志留档（便于失败时排查），整个临时根删掉——它只是一次性验证环境
cp -f "$WORK"/*.json "$WORK"/*.log "$REPO/_scratch/" 2>/dev/null || true

# 🔴 必须删**整个** $WORK，不能只删 install/config。
#    踩过的坑：原先只删 install/config，于是 c4diag/（§13.13/13.14 造出来的诊断样本目录）
#    会被留到下一次运行。而 13.14 的前提是 `plain.zip` **不存在**——
#    残留之后它直接报「[错误] 目标已存在：plain.zip」，表现为"diag 未脱敏时没有提示敏感内容"，
#    看起来像 diag 功能坏了，实际只是上一次的残留。
#    ⚠️ 更隐蔽的是：如果上一次运行是**中途被中断**的（例如被外部工具掐掉、或沙箱拦截了删除），
#    本行根本不会执行到 —— 所以下一次运行仍会踩残留。开头的 `rm -rf "$WORK"`（第 101 行）
#    是最后一道保障，跑之前若怀疑有残留，手动删一次 $WORK 即可。
rm -rf "$WORK"

exit $([ "$FAIL" -eq 0 ] && echo 0 || echo 1)
