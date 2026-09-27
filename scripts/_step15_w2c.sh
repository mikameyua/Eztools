#!/usr/bin/env bash
# ============================================================================
#  _step15_w2c.sh —— P4 Wave 2c 验收：面板协议（panels 贡献点 + weight: full 增量）
#
#  前置：acceptance.sh 已定义 PASS/FAIL/pass/fail/step/check、$EZ、$WORK、$REPO、$PY，
#        且已 export EZTOOLS_INSTALL_ROOT / EZTOOLS_CONFIG_ROOT。
#
#  为什么这些断言必须真跑而不是静态看代码：
#    面板链路上有三类**静态看不出来**的失败模式，它们都能让命令退出 0：
#      ① 方法名写错（`tool.panel.data` 拼成 `tool.panel.get`）→ 工具回 MethodNotFound
#         被容错层吃掉 → 面板**恒空**，"看起来只是没内容"。
#      ② 命令引用写错 → 按钮能画出来、点下去报未知命令（最差的能显示不可用）。
#      ③ 宿主把工具返回的原始载荷**规范化/吞掉** → 断言对象变成宿主加工后的东西，
#         脚本从此测的是宿主而不是协议。
#    所以：15.x 里凡涉及"工具到底说了什么"的，都断言**原始返回的形状与内容**。
#
#  环境策略：本片段**自己造隔离的 --tools-dir 根**（不复用仓库 tools/）——
#    负向夹具（bad-lite / bad-size）是**故意写坏的清单**，放进 tools/ 会让
#    3.x / 6.x 的发现数量断言连带失败，掩盖真正的失败原因（_step14 同样的策略）。
# ============================================================================

step "15/18  P4 Wave 2c：面板协议（panels 声明 / tool.panel.data / weight: full 闸门）"

if [ -z "${PY:-}" ]; then
  PY="$(command -v python || command -v python3 || true)"
  [ -n "$PY" ] || PY="$REPO/spike/_runtime-test/python/python.exe"
fi

W2C="$WORK/w2c"
rm -rf "$W2C"
mkdir -p "$W2C/tools"

PT="$W2C/tools/paneltool"
mkdir -p "$PT"
cp "$REPO/tools/paneltool/tool.json" "$PT/tool.json"
cp "$REPO/tools/paneltool/main.py"   "$PT/main.py"

# 负向夹具：把清单从仓库源拷来后**就地改坏**（不依赖 _scratch/w2c 的手工残留，
# 否则本脚本在干净机器上跑不出这三条 —— 夹具必须是自包含的）。
"$PY" - "$W2C/tools" <<'PY'
import io, json, os, sys
base = sys.argv[1]

def write(name, obj):
    d = os.path.join(base, name)
    os.makedirs(d, exist_ok=True)
    with io.open(os.path.join(d, "tool.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    # 每个夹具都要有 entry 文件，否则"工具可加载"这条会先挂掉，掩盖真正的诊断断言
    with io.open(os.path.join(d, "main.py"), "w", encoding="utf-8", newline="\n") as f:
        f.write("from eztools import Tool\ntool = Tool()\nif __name__ == '__main__': raise SystemExit(tool.run())\n")

# 15.5 夹具：weight: lite 却声明 panels → 必须 Error
write("badlite", {
    "id": "badlite", "name": "坏档位", "version": "1.0.0", "runtime": "python",
    "entry": "main.py", "weight": "lite",
    "contributes": {"panels": [{"id": "p1", "title": "面板"}]},
})

# 15.9 / 15.11 夹具：尺寸与 refreshMs 全越界 + 引用不存在的命令 → 一串 Warning，且值被钳制
write("badsize", {
    "id": "badsize", "name": "坏尺寸", "version": "1.0.0", "runtime": "python",
    "entry": "main.py", "weight": "full", "needs": ["ui.panel"],
    "contributes": {
        "commands": [{"id": "badsize.ping", "title": "p", "handler": "ping"}],
        "panels": [
            {"id": "p1", "title": "越界", "width": 99999, "height": 10, "refreshMs": 10,
             "commands": ["badsize.nope"]}
        ],
    },
})

# 15.8 夹具：panels[].id 缺失 → 必须 Error（结构性缺陷，该面板无法被路由）
write("noid", {
    "id": "noid", "name": "无 id", "version": "1.0.0", "runtime": "python",
    "entry": "main.py", "weight": "full", "needs": ["ui.panel"],
    "contributes": {"panels": [{"title": "没有 id 的面板"}]},
})

# 15.16 夹具：hotkeys[].opensPanel 指向不存在的面板 → 必须 Warning（热键本身仍可用）。
# **这个夹具同时是"校验时机"的回归守卫**：opensPanel 的交叉校验必须跑在 panels 解析**之后**，
# 若有人把它挪回热键循环里（那里 panels 还空着），本夹具会**反过来**报"合法声明被判非法"，
# 而正向夹具（okopenspanel）会红 —— 两边一起把时机钉死。
write("badopenspanel", {
    "id": "badopenspanel", "name": "坏面板引用", "version": "1.0.0", "runtime": "python",
    "entry": "main.py", "weight": "full", "needs": ["ui.panel"],
    "contributes": {
        "commands": [{"id": "badopenspanel.go", "title": "go", "handler": "go"}],
        "panels": [{"id": "real", "title": "真面板"}],
        "hotkeys": [{"command": "badopenspanel.go", "default": "Ctrl+Alt+F9",
                     "opensPanel": "not-exist"}],
    },
})

# 正向夹具：opensPanel 指向**本工具真的声明了**的面板 → 不该有任何 opensPanel 相关诊断。
# 与 badopenspanel 成对：只有正向能过才说明"报错是因为名字错，不是因为功能本身不可用"。
write("okopenspanel", {
    "id": "okopenspanel", "name": "对的面板引用", "version": "1.0.0", "runtime": "python",
    "entry": "main.py", "weight": "full", "needs": ["ui.panel"],
    "contributes": {
        "commands": [{"id": "okopenspanel.go", "title": "go", "handler": "go"}],
        "panels": [{"id": "real", "title": "真面板"}],
        "hotkeys": [{"command": "okopenspanel.go", "default": "Ctrl+Alt+F10",
                     "opensPanel": "real"}],
    },
})

# 对照组：refreshMs: 1500 合法 → 必须原样保留（没有它，15.11 只能证明"某个值被改了"）
write("okref", {
    "id": "okref", "name": "合法刷新", "version": "1.0.0", "runtime": "python",
    "entry": "main.py", "weight": "full", "needs": ["ui.panel"],
    "contributes": {"panels": [{"id": "p1", "title": "t", "refreshMs": 1500}]},
})
PY

# 夹具目录里只保留"清单能解析"的部分用于正向断言；负向夹具单独用 --tools-dir 指过去，
# 避免它们的 Error 污染正向工具的诊断集合。
TOOLS_ROOT="$(cygpath -m "$W2C/tools" 2>/dev/null || echo "$W2C/tools")"

# 只含正例的根（paneltool + okref）—— 15.6「无 Error」必须在这个根上断言
POS_ROOT="$W2C/pos"
mkdir -p "$POS_ROOT"
cp -r "$PT" "$POS_ROOT/paneltool"
cp -r "$W2C/tools/okref" "$POS_ROOT/okref"
POS_ROOT_W="$(cygpath -m "$POS_ROOT" 2>/dev/null || echo "$POS_ROOT")"

panel_json() {  # panel_json <输出文件> <参数...>
  local out="$1"; shift
  "$EZ" panel "$@" --json --quiet --tools-dir "$TOOLS_ROOT" > "$out" 2>&1
  return $?
}

# ── 15.1 ★ 列出面板：输出里必须含**具体的 panelId** ─────────────────────────
#     反例：若实现返回空列表，"退出码 0" 照样绿 —— 所以断言对象是面板标识本身。
panel_json "$W2C/list.json"
check "ezt panel 列出面板（退出码 0）" 0 $?

# 15.1b 需要"哪些工具被注册"这个信息，而 `ezt panel` 的输出里**没有** tools[]
#（只有 count/panels）。所以这里单独跑一次 doctor 落盘 —— 判据引用一个不存在的
# 字段是"断言恒真/恒假"的经典成因（本轮实测踩到过），不能靠想象。
TOOLS_JSON="$W2C/list.json"
"$EZ" doctor --json --quiet --tools-dir "$TOOLS_ROOT" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  > "$W2C/list-doctor.json" 2>&1

"$PY" - "$W2C/list.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
panels = d.get("panels") or []
qualed = {p.get("qualified") for p in panels}
# 正例（paneltool 的 6 个面板）必须都在
#   注：`input` 是 V1.3 增量（协议 §3.7）—— 这里必须列出，否则"面板被悄悄摘掉"没有信号。
want = {"paneltool.main", "paneltool.empty", "paneltool.weird", "paneltool.broken",
        "paneltool.image", "paneltool.input"}
missing = want - qualed
print(f"  [信息] 面板 {d.get('count')} 个: {sorted(qualed)}")
if missing:
    print(f"  [FAIL] 缺少面板: {sorted(missing)}")
    sys.exit(1)
# 反向：列出**完整期望集**而不是"⊇ 正例"。
# 原判据是子集，于是列表里混进 badopenspanel.real / badsize.p1 时照样绿 ——
# 而断言描述写的是"列出 4 个面板"，描述与事实不符（实测列表是 9 个）。
# 这里把 TOOLS_ROOT 下**所有合法声明**都列出来；多一个少一个都算失败。
#   注：badlite（weight lite）与 noid（缺 id）是**故意非法**的，它们的 panels 不该出现 —— 
#   这正是 15.1b 守的那条；本断言只要求"合法的那批不多不少"。
#   preview.main 来自仓库 tools/preview（内置源同样被扫到，是合法声明）。
expected = want | {"badopenspanel.real", "badsize.p1", "okopenspanel.real",
                   "okref.p1", "preview.main"}
extra = qualed - expected
if extra:
    print(f"  [FAIL] 出现了不该在列表里的面板（合法批之外）: {sorted(extra)}")
    sys.exit(1)
gone = expected - qualed
if gone:
    print(f"  [FAIL] 合法声明却未出现在列表里: {sorted(gone)}")
    sys.exit(1)
sys.exit(0)
PY
if [ $? -eq 0 ]; then
  pass "ezt panel 列出的面板集合与全部合法声明**逐一对齐**（不多不少，含具体 panelId）"
else
  fail "面板清单不符：$(tr -d '\r\n' < "$W2C/list.json" | head -c 240)"
fi

# 15.1b 非法声明被**整份清单拒绝**，且拒绝范围是局部的（不连坐其它工具）。
#
#        ⚠️ 这条断言前后改过两次，两次都是因为"判据描述的不是机制"：
#        ① 原判据"列表里不得出现 badlite./noid. 前缀的面板" —— 突变验证
#          （白名单改成永不命中的值）后仍然 174/0 全绿，证明它是空的：
#          `ezt panel` 的列表按定义只含**已注册**面板，非法那些根本进不来，
#          这条只是把 15.1 的话换了个说法，永远不会独立变红。
#        ② 第二版猜"noid 工具还留着、只是没了面板" —— 实测**猜错了**：
#          带 Error 诊断的清单是**整份被拒**的（工具也不注册），于是断言变红。
#          这反而问出了真实机制，也说明"先跑一次再写断言"比"照着代码想象"可靠。
#        现在断言的是实测出来的机制：Error 清单整份出局、好工具一个不少。
"$PY" - "$TOOLS_JSON" "$W2C/list-doctor.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
qualed = {p.get("qualified") for p in (d.get("panels") or [])}

dd = json.load(open(sys.argv[2], encoding="utf-8"))
tool_ids = {t.get("id") for t in (dd.get("tools") or [])}
errs = [x for x in (dd.get("diagnostics") or []) if x.get("severity") == "Error"]

print(f"  [信息] 面板 {len(qualed)} 个 · 工具 {len(tool_ids)} 个 · Error {len(errs)} 条")
# ★ 有效性前提：必须真扫到工具，否则下面所有判据都会退化（缺席的方向恒真）
if not tool_ids:
    print("  [FAIL] doctor 没扫到任何工具 —— 本断言的判据失效（有效性前提不成立）")
    sys.exit(1)

# ① 带 Error 的两份清单（badlite / noid）**整份出局**：工具与面板都不在
for bad in ("badlite", "noid"):
    if bad in tool_ids:
        print(f"  [FAIL] {bad} 带 Error 诊断却仍被注册为工具 —— 坏清单必须整份出局")
        sys.exit(1)
leaked = sorted(q for q in qualed if q.startswith("badlite.") or q.startswith("noid."))
if leaked:
    print(f"  [FAIL] 非法声明的面板混入了列表: {leaked}")
    sys.exit(1)

# ② 拒绝必须**不连坐**：好工具与它们的面板一个不少。
#    这条才是本断言独有的价值 —— 若实现改成"发现坏清单就清空注册表"，
#    或把 Error 提升成致命错误提前退出，15.1 与 15.8 都不会红，只有这条会。
if "paneltool" not in tool_ids or "preview" not in tool_ids:
    print(f"  [FAIL] 好工具被连带拒了 —— 一个坏清单影响了其它工具：{sorted(tool_ids)}")
    sys.exit(1)
if not {"paneltool.main", "paneltool.empty"} <= qualed:
    print(f"  [FAIL] 好工具的面板被连带拒了：{sorted(qualed)}")
    sys.exit(1)
if not errs:
    print("  [FAIL] 一份带 Error 的清单都没有 —— 夹具没生效，本断言在空跑")
    sys.exit(1)
sys.exit(0)
PY
check "非法清单整份出局（工具+面板都不注册），且好工具与好面板一个不少（不连坐）" 0 $?

# ── 15.2 ★ 拉取面板数据：nodes 长度必须 > 0 ─────────────────────────────────
panel_json "$W2C/main.json" paneltool main
check "ezt panel paneltool main 退出码 0" 0 $?

"$PY" - "$W2C/main.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
nodes = ((d.get("data") or {}).get("nodes")) or []
ok = d.get("ok") is True and d.get("panelId") == "main" and len(nodes) > 0
print(f"  [信息] ok={d.get('ok')} panelId={d.get('panelId')} nodeCount={d.get('nodeCount')} 实取={len(nodes)}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "面板数据通道打通，nodes 长度 > 0（非空数组）"
else
  fail "面板数据为空或面板 id 不符：$(tr -d '\r\n' < "$W2C/main.json" | head -c 240)"
fi

# ── 15.3 ★ 节点类型：返回的 type 必须是**原始值**，宿主不得改写 ──────────────
#     断言到具体值（不是"有几个节点"）：渲染层把 type 吃掉 / 宿主做归一化，
#     节点数照样对，但面板会整片空白 —— 这是最隐蔽的一类。
"$PY" - "$W2C/main.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
nodes = ((d.get("data") or {}).get("nodes")) or []
types = [n.get("type") for n in nodes]
want = ["heading", "text", "kv", "list", "separator", "buttons"]
print(f"  [信息] nodes[0].type={types[0] if types else None!r} 全部类型={types}")
# 6 种节点类型一个不少（协议 §3.4 穷举），且顺序与工具返回一致（宿主不得重排）
sys.exit(0 if types == want else 1)
PY
if [ $? -eq 0 ]; then
  pass "节点 type 原样透出且覆盖全部 6 种（heading/text/kv/list/separator/buttons）"
else
  fail "节点类型不符（被改写/被丢/被重排）：$(tr -d '\r\n' < "$W2C/main.json" | head -c 240)"
fi

# 15.3b --text 人类可读渲染也必须产出内容（CLI 两个出口都得通）
"$EZ" panel paneltool main --text --quiet --tools-dir "$TOOLS_ROOT" > "$W2C/main.txt" 2>&1
check "ezt panel paneltool main --text 退出码 0" 0 $?
# 🔴 不用 grep -q：命中即退出会让上游收 SIGPIPE，set -o pipefail 下结论会反转。
#    用 wc 计数（本项目在 8.6 已踩过这个）。
TEXT_LINES=$(grep -c "" "$W2C/main.txt" 2>/dev/null | tr -d ' ')
if [ "${TEXT_LINES:-0}" -ge 6 ]; then
  pass "--text 渲染出 $TEXT_LINES 行（6 种节点都落到了文本上）"
else
  fail "--text 渲染内容过少（$TEXT_LINES 行）：$(head -c 200 "$W2C/main.txt")"
fi

# ── 15.4 ★ 按钮的 commandId 必须能解析到已声明命令 ──────────────────────────
#     面板按钮点击走 tool.invoke。若引用的命令不存在，用户会看到"能点、点了报错" ——
#     最差的失败模式（能显示但不可用），必须在加载期就拦下。
"$PY" - "$W2C/main.json" "$TOOLS_ROOT/paneltool/tool.json" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
manifest = json.load(open(sys.argv[2], encoding="utf-8"))
declared = {c["id"] for c in manifest["contributes"]["commands"]}
nodes = ((data.get("data") or {}).get("nodes")) or []
btns = [n for n in nodes if n.get("type") == "buttons"]
refs = [b.get("commandId") for n in btns for b in (n.get("items") or [])]
print(f"  [信息] 面板按钮引用: {refs}（已声明命令: {sorted(declared)}）")
unknown = [r for r in refs if r not in declared]
if not refs:
    print("  [FAIL] 没有找到 buttons 节点（15.4 无从断言）")
    sys.exit(1)
if unknown:
    print(f"  [FAIL] 引用了未声明的命令: {unknown}")
    sys.exit(1)
sys.exit(0)
PY
if [ $? -eq 0 ]; then
  pass "buttons 节点引用的 commandId 全部能解析到已声明命令"
else
  fail "按钮引用了不存在的命令：$(tr -d '\r\n' < "$W2C/main.json" | head -c 240)"
fi

# ── 15.5 ★★ weight: lite 声明 panels → **加载期 Error** ──────────────────────
#     这是 Wave 2c 的核心闸门。断言两件事：① 有 Error；② 诊断码字符串正确。
#     只看"命令退非零"是不够的 —— 任何清单错误都能让 doctor 退非零。
"$EZ" doctor --json --quiet --tools-dir "$W2C/tools/badlite" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  > "$W2C/badlite.json" 2>&1
"$PY" - "$W2C/badlite.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
diags = d.get("diagnostics") or []
codes = [x.get("code") for x in diags]
errors = [x for x in diags if x.get("severity") == "Error"]
hit = [x for x in errors if x.get("code") == "contributes.panels-require-full"]
print(f"  [信息] badlite 诊断码: {codes}")
if not hit:
    print(f"  [FAIL] 未出现 contributes.panels-require-full（Error）；errors={[x.get('code') for x in errors]}")
    sys.exit(1)
sys.exit(0)
PY
if [ $? -eq 0 ]; then
  pass "weight: lite 声明 panels → 加载期 Error「contributes.panels-require-full」"
else
  fail "档位闸门未生效：$(tr -d '\r\n' < "$W2C/badlite.json" | head -c 300)"
fi

# ── 15.6 ★ 对照：weight: full 且声明 panels → **无 Error 诊断** ──────────────
#     没有这条对照，15.5 只能证明"某个清单报错了"，不能证明"闸门是按 weight 生效"。
"$EZ" doctor --json --quiet --tools-dir "$POS_ROOT_W" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  > "$W2C/pos.json" 2>&1
"$PY" - "$W2C/pos.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
errors = [x for x in (d.get("diagnostics") or []) if x.get("severity") == "Error"]
print(f"  [信息] 正例根 errorCount={d.get('errorCount')} errors={[x.get('code') for x in errors]}")
sys.exit(1 if errors else 0)
PY
if [ $? -eq 0 ]; then
  pass "对照：weight: full 声明 panels 无 Error（闸门按 weight 生效，非全局拒绝）"
else
  fail "正例根出现 Error：$(tr -d '\r\n' < "$W2C/pos.json" | head -c 300)"
fi

# 15.6b 正例根上 refreshMs=1500 必须**原样保留**（不是被归零，也不是被钳到边界）
"$EZ" panel okref --json --quiet --tools-dir "$POS_ROOT_W" > "$W2C/okref.json" 2>&1
"$PY" - "$W2C/okref.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
p = (d.get("panels") or [{}])[0]
ok = p.get("refreshMs") == 1500
print(f"  [信息] okref refreshMs={p.get('refreshMs')}（期望 1500，原样保留）")
sys.exit(0 if ok else 1)
PY
check "合法 refreshMs=1500 原样保留（不钳制、不归零）" 0 $?

# ── 15.7 ★ 未知 type 的节点 → 跳过该节点但**面板整体仍 ok** ──────────────────
#     反例：实现成"遇到未知类型就整包失败" → 面板白屏，前向兼容性丧失。
panel_json "$W2C/weird.json" paneltool weird
check "含未知节点的面板仍返回 ok（退出码 0）" 0 $?

"$PY" - "$W2C/weird.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
nodes = ((d.get("data") or {}).get("nodes")) or []
types = [n.get("type") for n in nodes]
# 工具原样返回 3 个节点（含 1 个未知）—— 宿主**不在 CLI 层过滤**，
# 因为它必须让排查者看见"工具发了个我不认识的节点"（协议 §3.4 / §3.5）。
ok = (d.get("ok") is True
      and len(nodes) == 3
      and "__future_widget__" in types)
print(f"  [信息] weird 节点 {len(nodes)} 个，types={types}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "未知节点类型不导致整包失败（3 个节点原样透出，含 __future_widget__）"
else
  fail "未知节点处理不符：$(tr -d '\r\n' < "$W2C/weird.json" | head -c 240)"
fi

# ── 15.8 ★ panels[].id 缺失 → 诊断码 contributes.panel-missing-id（Error）────
"$EZ" doctor --json --quiet --tools-dir "$W2C/tools/noid" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  > "$W2C/noid.json" 2>&1
"$PY" - "$W2C/noid.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
diags = d.get("diagnostics") or []
hit = [x for x in diags if x.get("code") == "contributes.panel-missing-id"
       and x.get("severity") == "Error"]
print(f"  [信息] noid 诊断: {[(x.get('severity'), x.get('code')) for x in diags]}")
if not hit:
    print("  [FAIL] 未出现 contributes.panel-missing-id（Error）—— 缺 id 的面板无法被路由，必须是 Error")
    sys.exit(1)
sys.exit(0)
PY
if [ $? -eq 0 ]; then
  pass "panels[].id 缺失 → Error「contributes.panel-missing-id」（结构性缺陷，非 Warning）"
else
  fail "缺 id 未报 Error：$(tr -d '\r\n' < "$W2C/noid.json" | head -c 300)"
fi

# ── 15.8b ★★ hotkeys[].opensPanel 的交叉校验（含**时机**回归守卫）──────────
#   两条成对断言，缺一条就会漏掉一半真相：
#     · badopenspanel → 引用了不存在的面板 ⇒ 必须 Warning（且热键**仍保留**，不能连键一起丢）
#     · okopenspanel  → 引用了真实存在的面板 ⇒ **不得**有任何 opensPanel 诊断
#   后者是"校验时机"的守卫：若有人把校验挪回热键循环（那时 panels 还空着），
#   正向夹具会立刻变红 —— 这正是本次实现时差点踩的坑（hotkeys 比 panels 早解析 105 行）。
"$EZ" doctor --json --quiet --tools-dir "$W2C/tools/badopenspanel" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  > "$W2C/badopenspanel.json" 2>&1
"$PY" - "$W2C/badopenspanel.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
diags = d.get("diagnostics") or []
hit = [x for x in diags if x.get("code") == "contributes.hotkey-unknown-panel"]
print(f"  [信息] badopenspanel 诊断: {[(x.get('severity'), x.get('code')) for x in diags]}")
if not hit:
    print("  [FAIL] 未出现 contributes.hotkey-unknown-panel —— 指向不存在面板的引用必须被指出")
    sys.exit(1)
if any(x.get("severity") != "Warning" for x in hit):
    print(f"  [FAIL] 严重级别应为 Warning（热键仍可用，只是少个副作用）：{hit}")
    sys.exit(1)
sys.exit(0)
PY
if [ $? -eq 0 ]; then
  pass "opensPanel 指向不存在的面板 → Warning「contributes.hotkey-unknown-panel」（热键本身仍生效）"
else
  fail "非法 opensPanel 未正确诊断：$(tr -d '\r\n' < "$W2C/badopenspanel.json" | head -c 300)"
fi

"$EZ" doctor --json --quiet --tools-dir "$W2C/tools/okopenspanel" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  > "$W2C/okopenspanel.json" 2>&1
"$PY" - "$W2C/okopenspanel.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
diags = d.get("diagnostics") or []
bad = [x for x in diags if x.get("code") == "contributes.hotkey-unknown-panel"]
errs = [x for x in diags if x.get("severity") == "Error"]
print(f"  [信息] okopenspanel 诊断: {[(x.get('severity'), x.get('code')) for x in diags]}")
# 正向断言的**有效性前提**：这个夹具必须真的被扫到了。
# 只断言"没有坏诊断"是不够的 —— 扫不到任何工具时**同样**没有诊断，那条断言会恒真
#（本轮实测踩到：--tools-dir 指到工具目录而非其父目录，工具数为 0，正向照样 PASS）。
if not (d.get("tools") or []):
    print("  [FAIL] okopenspanel 夹具根本没被扫到（工具数为 0）—— 正向断言恒真，无效")
    sys.exit(1)
if bad:
    print(f"  [FAIL] 合法引用被判非法 —— 交叉校验很可能跑在 panels 解析之前：{bad}")
    sys.exit(1)
if errs:
    print(f"  [FAIL] 合法清单不该有 Error：{errs}")
    sys.exit(1)
sys.exit(0)
PY
if [ $? -eq 0 ]; then
  pass "对照：opensPanel 指向真实面板无任何诊断（校验时机正确，不是一律报错）"
else
  fail "合法 opensPanel 被误判：$(tr -d '\r\n' < "$W2C/okopenspanel.json" | head -c 300)"
fi

# ── 15.9 ★ width/height 越界 → **钳制到边界** 且出 Warning ──────────────────
#     关键在"钳制后是合法值"：若原样透传 99999，WPF 窗口会开成天文数字尺寸，
#     且这属于**静默降级**（命令照常成功）—— 只有断言具体数值才抓得到。
"$EZ" panel badsize --json --quiet --tools-dir "$W2C/tools/badsize" > "$W2C/badsize-panel.json" 2>&1
"$PY" - "$W2C/badsize-panel.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
panels = d.get("panels") or []
if not panels:
    print(f"  [FAIL] badsize 面板未列出（钳制后本应可用）：{d}")
    sys.exit(1)
p = panels[0]
ok = (p.get("width") == 1600 and p.get("height") == 240)
print(f"  [信息] badsize 钳制后 width={p.get('width')}（期望 1600）height={p.get('height')}（期望 240）")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "宽高超界被钳制到边界（99999→1600 / 10→240），不是原样透传"
else
  fail "宽高未正确钳制：$(tr -d '\r\n' < "$W2C/badsize-panel.json" | head -c 240)"
fi

# 15.9b 同时必须出 Warning（钳制是"降级可用"，用户有权知道被改了）
"$EZ" doctor --json --quiet --tools-dir "$W2C/tools/badsize" \
  --install-root "$EZTOOLS_INSTALL_ROOT" --config-root "$EZTOOLS_CONFIG_ROOT" \
  > "$W2C/badsize-doctor.json" 2>&1
"$PY" - "$W2C/badsize-doctor.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
diags = d.get("diagnostics") or []
codes = {x.get("code") for x in diags if x.get("severity") == "Warning"}
need = {"contributes.panel-size-out-of-range",
        "contributes.panel-refresh-out-of-range",
        "contributes.panel-unknown-command"}
missing = need - codes
print(f"  [信息] badsize Warning 码: {sorted(codes)}")
if missing:
    print(f"  [FAIL] 缺少 Warning: {sorted(missing)}")
    sys.exit(1)
sys.exit(0)
PY
if [ $? -eq 0 ]; then
  pass "其余降级（尺寸/刷新/未知命令引用）如实报 Warning（三条诊断码齐全）"
else
  fail "降级诊断不全：$(tr -d '\r\n' < "$W2C/badsize-doctor.json" | head -c 300)"
fi

# ── 15.10 ★ 工具返回非对象 → 宿主报告格式错误但**不崩溃**（退出码仍 0）──────
#     这是"面板错误不弹框、不中断宿主"（协议 §3.5 总原则）的活体判据。
#     反例：宿主直接索引 ["nodes"] 会抛 InvalidOperationException —— 命令退 3，
#     而且**恰恰是这条负向用例把排查入口弄崩了**（本项目实现时踩过一次）。
panel_json "$W2C/broken.json" paneltool broken
check "工具返回非对象时 ezt panel 仍退出 0（不崩溃）" 0 $?

"$PY" - "$W2C/broken.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
# dataShape 让"工具返回了什么形状"成为可断言的事实；
# nodeCount 必须容忍非对象载荷（=0 而不是抛异常）。
ok = (d.get("ok") is True
      and d.get("dataShape") in ("array", "scalar", "object-without-nodes", "null")
      and d.get("nodeCount") == 0)
print(f"  [信息] broken: dataShape={d.get('dataShape')} nodeCount={d.get('nodeCount')} raw={str(d.get('data'))[:60]}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "宿主识别并如实报告载荷形状（dataShape=array，nodeCount 容错为 0）"
else
  fail "非对象载荷处理不符：$(tr -d '\r\n' < "$W2C/broken.json" | head -c 240)"
fi

# ── 15.11 ★ refreshMs 越界 → **归 0**（不是钳制到 500）且出 Warning ─────────
#     语义要点：钳制会把笔误（10）放大成"比作者写的还勤"的刷新。
#     安全默认值必须往"更少打扰"倒 —— 所以归一化目标是 0（不自动刷新）。
"$PY" - "$W2C/badsize-panel.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
p = (d.get("panels") or [{}])[0]
ok = p.get("refreshMs") == 0
print(f"  [信息] badsize refreshMs={p.get('refreshMs')}（期望 0 = 不自动刷新；注意不是 500）")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "refreshMs 越界归 0（不自动刷新）—— 安全默认值偏向更少打扰，而非钳到 500"
else
  fail "refreshMs 归一化语义不对：$(tr -d '\r\n' < "$W2C/badsize-panel.json" | head -c 240)"
fi

# ── 15.12 ★★ 面板按钮 → 走 tool.invoke 能真实调用并返回结果 ─────────────────
#     不是"按钮能不能画出来"，而是"按钮指向的命令**真的能执行**"。
#     断言到业务返回值（pong=paneltool），而不是"退出码 0" ——
#     后者在命令是空壳时照样成立。
"$EZ" invoke paneltool.ping --json '{"panelId":"main"}' --quiet \
  --tools-dir "$TOOLS_ROOT" > "$W2C/ping.json" 2>&1
check "面板按钮指向的命令可被真实调用（tool.invoke 通路）" 0 $?

"$PY" - "$W2C/ping.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
# fromPanel 回带 panelId：证明"从哪个面板点过来的"这个上下文真的传到了工具（协议 §4.1 第 2 条）
ok = d.get("pong") == "paneltool" and d.get("fromPanel") == "main"
print(f"  [信息] 按钮命令返回: pong={d.get('pong')!r} fromPanel={d.get('fromPanel')!r}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "按钮命令返回真实业务结果（pong=paneltool），且 panelId 上下文已传达到工具"
else
  fail "按钮命令返回不符：$(tr -d '\r\n' < "$W2C/ping.json" | head -c 240)"
fi

# 15.12b 反向：weight: lite 的工具**不能**用面板通道（闸门在发起侧）
#        只做"full 能用"是半条断言 —— 另一半是"lite 用不了"。
"$EZ" panel badlite p1 --json --quiet --tools-dir "$W2C/tools/badlite" \
  > "$W2C/lite-panel.json" 2>&1
LITE_RC=$?
if [ "$LITE_RC" -ne 0 ]; then
  pass "weight: lite 的工具拉面板数据被拒（退出码 $LITE_RC）"
else
  fail "lite 档竟然能拉面板数据（档位闸门在发起侧失效）"
fi

# ── 15.13 面板数量下限（防"整块功能被悄悄摘掉"）────────────────────────────
#     上面每条都过了，但若哪天 paneltool 的 panels 被删空，15.1 会失败；
#     这里再加一条：协议文档本身必须在场（Wave 2c 的前置产物）。
if [ -f "$REPO/docs/P4-Wave2c-面板协议.md" ]; then
  pass "面板协议文档在场（docs/P4-Wave2c-面板协议.md）"
else
  fail "协议文档缺失 —— Wave 2c 违背「先定协议再写码」的常驻规则"
fi
