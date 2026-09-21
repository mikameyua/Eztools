#!/usr/bin/env bash
# ============================================================================
#  _step13_p4.sh —— P4 工具间协作（host.invokeTool）与 weight 档位闸门验收
#
#  前置：acceptance.sh 已定义 PASS/FAIL/pass/fail/skip/step/check、$EZ、$WORK、$REPO，
#        且已 export EZTOOLS_INSTALL_ROOT / EZTOOLS_CONFIG_ROOT。
#
#  为什么要真跑而不是静态断言：`host.invokeTool` 的失败模式是**死锁**——
#  静态看代码完全合理，跑起来才会发现"宿主等工具、工具等宿主、宿主等闸门"。
#  所以每条断言都走真实的跨进程往返。
#
#  约定：负向用例也期望 `ezt invoke` 退出 0。因为 probe / oneshot 会**捕获**宿主的
#  错误码并以结构化结果返回（这正是它们存在的意义）——因此断言对象是 JSON 里的
#  `code`，不是退出码。只看退出码会让这些负向用例变成恒真断言。
# ============================================================================

step "13/13  P4 工具间协作：host.invokeTool 通路 · 环检测 · weight 档位闸门"

if [ -z "${PY:-}" ]; then
  PY="$(command -v python || command -v python3 || true)"
  [ -n "$PY" ] || PY="$REPO/spike/_runtime-test/python/python.exe"
fi

P4_OUT="$WORK/p4-out.json"

# 调用一个命令并把工具返回值收进文件；返回值 = 工具 return 的 JSON 对象
invoke_tool_cmd() {  # invoke_tool_cmd <输出文件> <命令id> <json参数>
  local out="$1"; shift
  local cmd="$1"; shift
  "$EZ" invoke "$cmd" --json "${1:-{\}}" --quiet > "$out" 2>&1
  return $?
}

# 断言 JSON 里的某个顶层字段等于期望值；顺带打印 code 便于失败时定位
assert_field() {  # assert_field <文件> <字段> <期望值字符串> <断言名>
  "$PY" - "$1" "$2" "$3" <<'PY'
import json, sys
path, field, expected = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    data = json.load(open(path, encoding="utf-8"))
except Exception as exc:
    print(f"  [信息] 无法解析输出: {exc}；原文: {open(path, encoding='utf-8', errors='replace').read()[:200]}")
    sys.exit(1)
actual = data.get(field)
print(f"  [信息] {field} = {actual!r}（期望 {expected}）")
# 期望值以 - 开头或为纯数字时按整数比，避免 "0" vs 0 的假失败
try:
    ok = str(actual) == expected if expected.lstrip("-").isdigit() is False else int(actual or 0) == int(expected)
except (TypeError, ValueError):
    ok = str(actual) == expected
sys.exit(0 if ok else 1)
PY
}

# ── 13.1 ★ 核心：跨工具调用真的打通（probe → wordcount）────────────────────
#     断言链路末端是**目标工具的真实返回值**（chars=3 来自 wordcount 的中日韩按字计数），
#     而不是"调用返回了 200"这类表面证据。
invoke_tool_cmd "$P4_OUT" probe.delegate \
  '{"toolId":"wordcount","commandId":"wordcount.count","args":{"text":"一二三"}}'
rc=$?
if [ $rc -eq 0 ]; then
  "$PY" - "$P4_OUT" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
inner = d.get("delegated") or {}
# 同源校验：envelope 回带的 toolId/commandId 必须与请求的**同一个**，
# 防止"调通了但调的是别的工具/别的命令"（只断言 chars==3 时，恰好另一个工具也返回 3 就会漏过）。
ok = (d.get("ok") is True
      and d.get("toolId") == "wordcount"
      and d.get("commandId") == "wordcount.count"
      and inner.get("chars") == 3
      and inner.get("cjkChars") == 3)
print(f"  [信息] 转调结果: toolId={d.get('toolId')} commandId={d.get('commandId')} "
      f"内层 chars={inner.get('chars')} cjkChars={inner.get('cjkChars')}")
sys.exit(0 if ok else 1)
PY
  if [ $? -eq 0 ]; then
    pass "host.invokeTool 跨工具调用成功，且返回目标工具的真实结果（wordcount.chars=3）"
  else
    fail "跨工具调用结果不符：$(tr -d '\r\n' < "$P4_OUT" | head -c 240)"
  fi
else
  fail "probe.delegate 调用失败（rc=$rc）：$(tr -d '\r\n' < "$P4_OUT" | head -c 240)"
fi

# ── 13.2 归属校验：命令 id 必须属于声明的目标工具 ───────────────────────────
#     不校验的话，工具可以借另一个工具的命令 id 打第三个工具，报错也会指错对象。
invoke_tool_cmd "$P4_OUT" probe.delegate \
  '{"toolId":"wordcount","commandId":"probe.echo","args":{}}'
assert_field "$P4_OUT" code -32002
check "归属校验：命令 id 不属于声明工具时拒绝（-32002）" 0 $?

# ── 13.3 目标工具不存在 ─────────────────────────────────────────────────────
invoke_tool_cmd "$P4_OUT" probe.delegate \
  '{"toolId":"nosuchtool","commandId":"nosuchtool.run","args":{}}'
assert_field "$P4_OUT" code -32002
check "目标工具/命令不存在时拒绝（-32002）" 0 $?

# ── 13.4 参数缺失：宿主按 InvalidParams 拒绝，而不是静默成功 ────────────────
invoke_tool_cmd "$P4_OUT" probe.delegate '{"toolId":"wordcount"}'
assert_field "$P4_OUT" code -32602
check "缺 commandId 时回 InvalidParams（-32602）" 0 $?

# ── 13.5 ★ 环检测：自调用必须被**立刻**拒绝 ─────────────────────────────────
#     这条是死锁的活体证据：若宿主实现成"排队等自己"，这里会挂到超时（默认 30s），
#     所以除了错误码，还要断言耗时——错误码对但耗时 30s 说明实现方式是错的。
P4_START=$(date +%s)
invoke_tool_cmd "$P4_OUT" probe.selfCall '{}'
P4_ELAPSED=$(( $(date +%s) - P4_START ))
assert_field "$P4_OUT" code -32004
if [ $? -eq 0 ]; then
  pass "自调用成环被拒绝（-32004）"
else
  fail "自调用未回 -32004：$(tr -d '\r\n' < "$P4_OUT" | head -c 240)"
fi

if [ "$P4_ELAPSED" -lt 20 ]; then
  pass "成环是立刻拒绝而非等待（耗时 ${P4_ELAPSED}s，远小于超时）"
else
  fail "成环走了等待路径（耗时 ${P4_ELAPSED}s）—— 疑似靠超时兜底，属实现错误"
fi

# ── 13.6 weight: script 档只提供基础 API（storage 被拒）────────────────────
invoke_tool_cmd "$P4_OUT" oneshot.storage '{}'
assert_field "$P4_OUT" code -32005
check "script 档拒绝 host.storage（-32005）" 0 $?

# 13.6b 错误文案必须给出改档指引（-32005 的设计意图就是"告诉你换哪一档"）
#       只断言错误码会漏掉这个意图的一半：码对但没说怎么办 = 用户卡住。
"$PY" - "$P4_OUT" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
msg = d.get("message") or ""
ok = ("lite" in msg or "full" in msg) and "weight" in msg
print(f"  [信息] 改档指引文案: {msg[:120]!r}")
sys.exit(0 if ok else 1)
PY
check "拒绝文案给出改档指引（含 weight 与 lite/full）" 0 $?

# ── 13.7 weight: script 档不能用 host.invokeTool ───────────────────────────
invoke_tool_cmd "$P4_OUT" oneshot.call '{}'
assert_field "$P4_OUT" code -32005
check "script 档拒绝 host.invokeTool（-32005）" 0 $?

# ── 13.8 weight: script 档的基础 API 正常（闸门不是一刀切）──────────────────
invoke_tool_cmd "$P4_OUT" oneshot.echo '{"text":"abc"}'
"$PY" - "$P4_OUT" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
ok = d.get("echo") == "abc" and d.get("length") == 3 and isinstance(d.get("pid"), int)
print(f"  [信息] script 档回显: {d.get('echo')!r} len={d.get('length')} pid={d.get('pid')}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "script 档基础 API 可用（host.log + 返回值正常）"
else
  fail "script 档正常路径不通：$(tr -d '\r\n' < "$P4_OUT" | head -c 240)"
fi

# ── 13.9 对照：同一条 storage 命令在 lite 档工具上必须成功 ──────────────────
#     没有这条对照，13.6 只能证明"某个命令失败了"，不能证明"闸门是按档位生效"。
invoke_tool_cmd "$P4_OUT" probe.storage '{"key":"p4.check","value":"v1"}'
"$PY" - "$P4_OUT" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
ok = d.get("roundTrip") is True and d.get("read") == "v1"
print(f"  [信息] lite 档私有存储往返: roundTrip={d.get('roundTrip')}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "对照：lite 档同一 API 正常 → 闸门按档位生效而非全局关闭"
else
  fail "lite 档 storage 往返失败，13.6 的结论不成立：$(tr -d '\r\n' < "$P4_OUT" | head -c 240)"
fi

# ── 13.10 host.progress 仅对 lifecycle: task 开放（§5.3）────────────────────
invoke_tool_cmd "$P4_OUT" probe.progress '{"percent":10}'
assert_field "$P4_OUT" code -32005
check "host.progress 对非 task 生命周期拒绝（-32005）" 0 $?

# ── 13.11 清单解析：oneshot 的 weight 确实是 script（否则上面全是恒真）──────
"$EZ" list --json --quiet > "$WORK/p4-list.json" 2>/dev/null
"$PY" - "$WORK/p4-list.json" <<'PY'
import json, sys
tools = {t["id"]: t for t in json.load(open(sys.argv[1], encoding="utf-8"))}
oneshot = tools.get("oneshot", {})
probe = tools.get("probe", {})
ok = oneshot.get("weight") == "script" and probe.get("weight") == "lite"
print(f"  [信息] oneshot.weight={oneshot.get('weight')} probe.weight={probe.get('weight')}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
    pass "档位解析正确（oneshot=script / probe=lite）—— 闸门断言的档位前提成立"
else
  fail "档位解析异常：oneshot/probe 的 weight 不是 script/lite"
fi

# ── 13.12 通知通道如实回报宿主能力（B1 回归防护）───────────────────────────
#      历史缺陷：host.notify 曾**恒定**回 delivered=false（文案"P2 实现"），
#      而 P2 早已收官 → 一条"说了做不到"的承诺。修复后它必须按宿主能力如实回报：
#        · CLI 宿主（本脚本用的就是 ezt）→ delivered=false，且 reason 说明是"没有 UI"
#        · 托盘宿主 → delivered=true（由 Desktop 的 --probe-notify 单独验证）
#      这里断言的是 **reason 的语义**而不是恒定值 —— 若哪天退回恒定 false，
#      reason 会变成"没实现"这类措辞，本断言即失败。
invoke_tool_cmd "$P4_OUT" probe.notify '{}'
"$PY" - "$P4_OUT" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
reason = d.get("reason") or ""
ok = (d.get("ok") is True
      and d.get("delivered") is False              # CLI 宿主没有 UI
      and "UI" in reason)                          # 且原因说的是"没有 UI"而非"没实现"
print(f"  [信息] notify 回报: delivered={d.get('delivered')} reason={reason!r}")
sys.exit(0 if ok else 1)
PY
if [ $? -eq 0 ]; then
  pass "host.notify 如实回报宿主能力（CLI 宿主 delivered=false + 原因=没有 UI，非恒定值）"
else
  fail "host.notify 未如实回报：$(tr -d '\r\n' < "$P4_OUT" | head -c 240)"
fi

# ── 13.13 `ezt diag --redact` 真脱敏（C4 回归防护）─────────────────────────
#      脱敏是"默认不做、显式开启"的功能，这类功能的典型退化是**静默不生效**：
#      命令照常退出 0、包照样生成，只是里面的用户名/路径一个没换。
#      所以断言必须是**包的正文里找不到身份信息**，而不是"命令成功"。
DIAG_DIR="$WORK/c4diag"
DIAG_REDACT="$DIAG_DIR/redacted.zip"
mkdir -p "$DIAG_DIR/install/logs" "$DIAG_DIR/config"

# 测试样本必须含真实的身份三要素（用户名 / 主目录 / 机器名），否则脱敏测了个空气。
# ⚠️ 用 python 写样本而不是 bash printf：printf 会把 `\E`、`\U` 之类当转义序列吃掉，
#    写出来的样本本身就残缺（实测踩到：`\Eztools` → ` ztools`），会让人误判成脱敏程序有 bug。
DIAG_USER="$(whoami 2>/dev/null | sed 's/.*\\//' || echo "")"
"$PY" - "$DIAG_DIR" <<'PY'
import io, os, sys
base = sys.argv[1]
os.makedirs(os.path.join(base, "install", "logs"), exist_ok=True)
os.makedirs(os.path.join(base, "config"), exist_ok=True)
user = os.environ.get("USERNAME") or os.environ.get("USER") or "unknown"
machine = os.environ.get("COMPUTERNAME") or "unknown"
home = os.path.expanduser("~")
install = os.path.join(base, "install").replace("/", "\\")
lines = [
    f"user={user}",
    f"home={home}",
    f"machine={machine}",
    f"install={install}",
    "业务内容：工具处理了 3 个文件，耗时 120ms",   # 这行**不该**被改（业务内容要保住）
]
with io.open(os.path.join(base, "install", "logs", "c4probe.log"), "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
PY

DIAG_WIN="$(cygpath -m "$DIAG_DIR" 2>/dev/null || echo "$DIAG_DIR")"
"$EZ" diag --install-root "$DIAG_WIN/install" --config-root "$DIAG_WIN/config" \
  --out "$DIAG_WIN/redacted.zip" --redact --quiet > "$WORK/p4-diag-redact.log" 2>&1

if [ -f "$DIAG_REDACT" ]; then
  "$PY" - "$DIAG_REDACT" <<'PY'
import io, os, sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
blob = ""
for name in z.namelist():
    blob += z.read(name).decode("utf-8", "replace")

user = os.environ.get("USERNAME") or os.environ.get("USER") or ""
machine = os.environ.get("COMPUTERNAME") or ""
home = os.path.expanduser("~")

problems = []
if user and user in blob:
    problems.append(f"用户名 {user!r} 未被替换")
if machine and machine in blob:
    problems.append(f"机器名 {machine!r} 未被替换")
if home and home in blob:
    problems.append(f"主目录 {home!r} 未被替换")

# 反向对照：业务内容必须**原样保留** —— 否则"脱敏"变成了"洗白"，
# 诊断包会失去它存在的意义（看起来安全，实际查不出问题）。
if "工具处理了 3 个文件" not in blob:
    problems.append("业务内容被误删（脱敏过度）")

if problems:
    print("  [信息] " + "；".join(problems))
    sys.exit(1)
print("  [信息] 身份三要素（用户名/机器名/主目录）已全部替换，业务内容原样保留")
PY
  if [ $? -eq 0 ]; then
    pass "diag --redact 真脱敏（包内无用户名/机器名/主目录，且业务内容未被误删）"
  else
    fail "diag --redact 脱敏不完整或过度"
  fi
else
  fail "diag --redact 未生成诊断包：$(head -c 200 "$WORK/p4-diag-redact.log" 2>/dev/null)"
fi

# 13.14 未脱敏时**必须**把敏感内容摆到眼前（这是 C4 的另一半）
#       只做 --redact 而不做提示的话，"默认不脱敏"就仍是盲区。
"$EZ" diag --install-root "$DIAG_WIN/install" --config-root "$DIAG_WIN/config" \
  --out "$DIAG_WIN/plain.zip" --quiet > "$WORK/p4-diag-plain.log" 2>&1
if grep -F "未脱敏" "$WORK/p4-diag-plain.log" >/dev/null 2>&1 \
   && grep -F "机器名" "$WORK/p4-diag-plain.log" >/dev/null 2>&1; then
  pass "diag 未脱敏时打印敏感内容清单（不是静默原样打包）"
else
  fail "diag 未脱敏时没有提示敏感内容：$(head -c 240 "$WORK/p4-diag-plain.log" 2>/dev/null)"
fi
