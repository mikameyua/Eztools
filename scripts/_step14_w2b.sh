#!/usr/bin/env bash
# ============================================================================
#  _step14_w2b.sh —— P4 Wave 2b 验收：便携更新器 / 卸载器 / 打包脚本
#
#  前置：acceptance.sh 已定义 PASS/FAIL/pass/fail/step/check、$EZ、$WORK、$REPO、$PY、
#        且已 export EZTOOLS_INSTALL_ROOT / EZTOOLS_CONFIG_ROOT。
#
#  为什么要真跑而不是断言"命令退出 0"：
#    更新与卸载的失败模式是**静默删多了/删少了**——
#      · 更新把用户数据一起清掉（用户下次装回来什么都没了）
#      · 卸载默认把用户数据也删了（同上，且不可恢复）
#      · --purge-data 没真删干净（看着"成功"，数据还在）
#    这三类的共同点是**命令都退出 0**。所以断言必须落在"某个具体路径还在不在"。
#
#  环境策略：本片段**自己造一个隔离的安装根 + 配置根**（不复用 acceptance.sh 的 $WORK/install），
#  因为它的操作是破坏性的（删目录）。共用的话会把前面十几步的现场破坏掉，
#  一旦本片段失败，前面的失败原因就被掩盖了。
# ============================================================================

step "14/14  P4 Wave 2b：便携更新（保留用户数据）/ 卸载（默认保留，--purge-data 全清）"

if [ -z "${PY:-}" ]; then
  PY="$(command -v python || command -v python3 || true)"
  [ -n "$PY" ] || PY="$REPO/spike/_runtime-test/python/python.exe"
fi

W2B="$WORK/w2b"
W2B_INSTALL="$(cygpath -m "$W2B/install" 2>/dev/null || echo "$W2B/install")"
W2B_CONFIG="$(cygpath -m "$W2B/config" 2>/dev/null || echo "$W2B/config")"
W2B_PKG="$(cygpath -m "$W2B/pkg" 2>/dev/null || echo "$W2B/pkg")"

rm -rf "$W2B"
mkdir -p "$W2B/install" "$W2B/config" "$W2B/pkg"

# ── 14.0 造一个"已安装形态"：程序 + 用户数据 + 运行时都齐 ────────────────────
#     程序目录要放**真文件**（不是空目录），否则"替换了几个文件"这类断言无从谈起。
mkdir -p "$W2B/install/bin" "$W2B/install/tools/echo" "$W2B/install/sdk" \
         "$W2B/install/payload" "$W2B/install/runtimes/python/3.13.12" \
         "$W2B/install/toolsdata/echo/data" "$W2B/install/logs" "$W2B/install/cache"

echo "old-bin"    > "$W2B/install/bin/ezt.exe"
echo "stale-file" > "$W2B/install/bin/removed-in-new-version.dll"   # 新版已删的文件（验"先删后拷"）
echo "{}"         > "$W2B/install/tools/echo/tool.json"
echo "m"          > "$W2B/install/sdk/__init__.py"
echo "p"          > "$W2B/install/payload/python-3.13.12-win-x64.tar.gz"
echo "runtime"    > "$W2B/install/runtimes/python/3.13.12/python.exe"
echo "USER-DATA"  > "$W2B/install/toolsdata/echo/data/state.json"    # ★ 用户数据（绝不能被更新清掉）
echo "log"        > "$W2B/install/logs/host-20260921.log"
mkdir -p "$W2B/config/config"
echo '{"theme":"dark"}' > "$W2B/config/config/echo.json"             # ★ 用户配置（绝不能被更新清掉）
echo '{"schema":1}'     > "$W2B/install/install.json"

# 新版包：只含程序目录，且**不含** bin/removed-in-new-version.dll（模拟"新版删了这个文件"）
mkdir -p "$W2B/pkg/bin" "$W2B/pkg/tools/echo" "$W2B/pkg/sdk" "$W2B/pkg/payload"
echo "NEW-bin" > "$W2B/pkg/bin/ezt.exe"
echo "NEW-tool" > "$W2B/pkg/tools/echo/tool.json"
echo "NEW-sdk"  > "$W2B/pkg/sdk/__init__.py"
echo "NEW-pay"  > "$W2B/pkg/payload/python-3.13.12-win-x64.tar.gz"

rz() {  # rz <参数...> —— 在隔离根上跑 ezt，回显 stdout
  "$EZ" "$@" --install-root "$W2B_INSTALL" --config-root "$W2B_CONFIG" --quiet
}

# ── 14.1 更新试运行：只报告，不落盘 ─────────────────────────────────────────
rz update --from "$W2B_PKG" --dry-run --json > "$W2B/update-dry.json" 2>&1
W2B_RC=$?
check "update --dry-run 退出码为 0" 0 $W2B_RC

"$PY" - "$W2B/update-dry.json" "$W2B/install" <<'PY'
import io, os, sys
p, inst = sys.argv[1], sys.argv[2]
try:
    import json
    d = json.load(io.open(p, encoding="utf-8"))
except Exception as exc:
    print(f"  [信息] 无法解析输出: {exc}")
    sys.exit(1)
# 试运行必须真的没落盘：bin/ezt.exe 还是旧内容
old = io.open(os.path.join(inst, "bin", "ezt.exe"), encoding="utf-8").read().strip()
ok = d.get("dryRun") is True and old == "old-bin"
print(f"  [信息] dryRun={d.get('dryRun')} 落盘后 bin/ezt.exe={old!r}（期望 'old-bin'）")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "update --dry-run 只报告不落盘（bin/ezt.exe 仍是旧内容）"
else
  fail "update --dry-run 落盘了：$(tr -d '\r\n' < "$W2B/update-dry.json" | head -c 200)"
fi

# ── 14.2 ★ 更新（真跑）：程序被替换 ─────────────────────────────────────────
rz update --from "$W2B_PKG" --json > "$W2B/update.json" 2>&1
check "update 退出码为 0" 0 $?

"$PY" - "$W2B/install" <<'PY'
import io, os, sys
inst = sys.argv[1]
def rd(*parts):
    return io.open(os.path.join(inst, *parts), encoding="utf-8").read().strip()
probs = []
if rd("bin", "ezt.exe") != "NEW-bin":
    probs.append(f"bin/ezt.exe 未替换（{rd('bin','ezt.exe')!r}）")
if rd("sdk", "__init__.py") != "NEW-sdk":
    probs.append("sdk/ 未替换")
# "先删后拷"的判据：新版已删的文件必须**消失**（否则是"降级残留"）
if os.path.exists(os.path.join(inst, "bin", "removed-in-new-version.dll")):
    probs.append("旧版已删文件残留（bin/removed-in-new-version.dll 仍在）—— 未做先删后拷")
if probs:
    print("  [信息] " + "；".join(probs))
    sys.exit(1)
print("  [信息] 程序目录已替换：bin/ezt.exe=NEW-bin，sdk=NEW-sdk，旧版残留文件已清除")
PY
if [ $? -eq 0 ]; then
  pass "更新替换程序目录，且旧版已删文件被清除（先删后拷，无降级残留）"
else
  fail "更新后程序目录状态不符"
fi

# ── 14.3 ★★ 更新必须保留用户数据与运行时（本片段最重要的一条）──────────────
#     历史上「配置存在工具目录里 → 卸载即丢用户数据」（§13 反模式），
#     这条就是那个反模式的正面判据。只断言"更新成功"完全测不出它。
"$PY" - "$W2B/install" "$W2B/config" <<'PY'
import io, os, sys
inst, cfg = sys.argv[1], sys.argv[2]
probs = []
def check_file(path, expect, label):
    if not os.path.exists(path):
        probs.append(f"{label} 被删了（{path}）")
        return
    got = io.open(path, encoding="utf-8").read().strip()
    if got != expect:
        probs.append(f"{label} 内容被改（{got!r} != {expect!r}）")

check_file(os.path.join(inst, "toolsdata", "echo", "data", "state.json"), "USER-DATA", "工具私有数据")
check_file(os.path.join(cfg, "config", "echo.json"), '{"theme":"dark"}', "用户配置")
check_file(os.path.join(inst, "runtimes", "python", "3.13.12", "python.exe"), "runtime", "运行时")
if probs:
    print("  [信息] " + "；".join(probs))
    sys.exit(1)
print("  [信息] toolsdata/ 与配置根原样保留，runtimes/ 未被重建")
PY
if [ $? -eq 0 ]; then
  pass "更新保留用户数据（toolsdata）与用户配置（config 根），未动 runtimes/"
else
  fail "更新破坏了用户数据或配置 —— 这是「卸载/更新即丢数据」反模式"
fi

# 14.3b 更新报告里必须**明说**保留了哪些（用户要知道数据还在）
"$PY" - "$W2B/update.json" <<'PY'
import io, json, sys
d = json.load(io.open(sys.argv[1], encoding="utf-8"))
preserved = d.get("preserved") or []
ok = "toolsdata" in preserved and "runtimes" in preserved
print(f"  [信息] preserved={preserved}")
sys.exit(0 if ok else 1)
PY
check "更新结果如实列出被保留的目录（toolsdata / runtimes）" 0 $?

# 14.3c 更新前必须留下回滚备份（否则"更新坏了"就只能重装）
BACKUP_COUNT=$(find "$W2B/install/backup" -type f 2>/dev/null | wc -l | tr -d ' ')
if [ "$BACKUP_COUNT" -ge 1 ]; then
  pass "更新前备份了旧程序（backup/ 下 $BACKUP_COUNT 个文件，可回滚）"
else
  fail "更新没有留下回滚备份（backup/ 为空）"
fi

# ── 14.4 卸载：不给 --yes 必须拒绝（防脚本手滑）──────────────────────────────
rz uninstall --json > "$W2B/uninstall-noyes.json" 2>&1
W2B_RC=$?
check "uninstall 缺 --yes 时拒绝（rc=64）" 64 $W2B_RC

# 拒绝之后**什么都没删** —— 只断言退出码不够，要断言现场未变
if [ -f "$W2B/install/bin/ezt.exe" ]; then
  pass "被拒绝的卸载未产生任何破坏（安装根完好）"
else
  fail "uninstall 拒绝后仍删了东西"
fi

# ── 14.5 卸载试运行：报告会删什么，但不落盘 ─────────────────────────────────
rz uninstall --dry-run --json > "$W2B/uninstall-dry.json" 2>&1
check "uninstall --dry-run 退出码为 0" 0 $?

if [ -f "$W2B/install/bin/ezt.exe" ] && [ -f "$W2B/install/toolsdata/echo/data/state.json" ]; then
  pass "uninstall --dry-run 只报告不落盘（程序与数据都还在）"
else
  fail "uninstall --dry-run 落盘了"
fi

# ── 14.6 ★★ 卸载（默认）：删程序、**保留**用户数据 ─────────────────────────
rz uninstall --yes --json > "$W2B/uninstall.json" 2>&1
check "uninstall --yes 退出码为 0" 0 $?

"$PY" - "$W2B/install" "$W2B/config" <<'PY'
import io, os, sys
inst, cfg = sys.argv[1], sys.argv[2]
probs = []
# 程序目录必须都没了
for name in ("bin", "tools", "sdk", "payload", "runtimes", "cache", "logs"):
    if os.path.exists(os.path.join(inst, name)):
        probs.append(f"{name}/ 未删除")
# 用户数据与配置必须都在
if not os.path.exists(os.path.join(inst, "toolsdata", "echo", "data", "state.json")):
    probs.append("默认卸载把工具私有数据删了（应保留）")
if not os.path.exists(os.path.join(cfg, "config", "echo.json")):
    probs.append("默认卸载把用户配置删了（应保留）")
if probs:
    print("  [信息] " + "；".join(probs))
    sys.exit(1)
print("  [信息] 程序目录已清、用户数据与配置保留")
PY
if [ $? -eq 0 ]; then
  pass "默认卸载删除程序目录、保留用户数据与配置（与 --purge-data 的区别成立）"
else
  fail "默认卸载行为不符（要么没删干净，要么误删了用户数据）"
fi

# 14.6b 残留提示必须**说清是哪些**（含糊的"含未删除项"看起来像卸载失败）
"$PY" - "$W2B/uninstall.json" <<'PY'
import io, json, sys
d = json.load(io.open(sys.argv[1], encoding="utf-8"))
kept = d.get("kept") or []
blob = " ".join(kept)
ok = ("toolsdata" in blob or "残留" in blob) and d.get("purgedUserData") is False
print(f"  [信息] purgedUserData={d.get('purgedUserData')} kept={kept}")
sys.exit(0 if ok else 1)
PY
check "卸载结果列出被保留项与残留目录（不是含糊的「含未删除项」）" 0 $?

# ── 14.7 ★★ --purge-data：连用户数据与配置一起清干净 ────────────────────────
rz uninstall --yes --purge-data --json > "$W2B/purge.json" 2>&1
check "uninstall --yes --purge-data 退出码为 0" 0 $?

PROB=""
[ -e "$W2B/install" ]    && PROB="$PROB 安装根仍在"
[ -e "$W2B/config" ]     && PROB="$PROB 配置根仍在"
[ -e "$W2B/install/toolsdata" ] && PROB="$PROB toolsdata 仍在"
if [ -z "$PROB" ]; then
  pass "purge 后安装根与配置根彻底消失（连 toolsdata 一起清）"
else
  fail "purge 未清干净:$PROB"
fi

# ── 14.8 打包脚本存在且可执行（不实跑——它要 publish 3 分钟，不适合放进验收）──
if [ -f "$REPO/scripts/make-portable.sh" ] && [ -x "$REPO/scripts/make-portable.sh" ]; then
  pass "便携打包脚本 make-portable.sh 存在且可执行"
else
  fail "scripts/make-portable.sh 缺失或不可执行"
fi

# 14.8b 打包脚本的**形态选择**必须是自包含（D1 决策）—— 静态断言防悄悄退回框架依赖
if grep -F -- "--self-contained true" "$REPO/scripts/make-portable.sh" >/dev/null 2>&1; then
  pass "打包脚本按 D1 决策产出自包含包（--self-contained true）"
else
  fail "打包脚本没有 --self-contained true —— 与 D1 决策（自包含）不符"
fi

# 14.8c 打包脚本必须产出 zip（A3 决策 = 便携 zip），不是 tar.gz
if grep -F 'ZIP_NAME="$PKG_NAME.zip"' "$REPO/scripts/make-portable.sh" >/dev/null 2>&1; then
  pass "打包脚本按 A3 决策产出便携 zip（不是 tar.gz）"
else
  fail "打包脚本产物不是 .zip —— 与 A3 决策（便携 zip）不符"
fi
