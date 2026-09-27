# _step11.sh —— 热键仲裁验收（由 acceptance.sh source，共享 pass/fail/step 变量）
# 场景：
#   A. 静态冲突：同一组合键的两种写法（Ctrl+Alt+X vs alt+ctrl+x）→ doctor 出 hotkey-conflict
#   B. CLI 仲裁归属：--json 恰有 1 wins / 1 blocked-by（按 hk-a/hk-b 过滤——--tools-dir 是追加语义）
#   C. 改键往返：双 set 制造冲突 → 归属按 Id 序 → 双 unset 回默认（跑在真实安装根，注意还原）
#
# 🔴 内嵌 python 一律用**单引号 heredoc**（$PY 加 <<'EOF'），不要用 $PY -c 加双引号串：
#    python 代码里的 f-string 双引号会终止 bash 的双引号串，把代码炸成 shell 语法错误。
#    （此处刻意不写反引号：注释里的反引号仍会被 bash 做命令替换。）

step "11/18  热键仲裁：静态冲突 / CLI 归属 / 改键往返"

HK="$WORK/devtools-hk"
mkdir -p "$HK/hk-a" "$HK/hk-b"
for d in hk-a hk-b; do
  cp -r "$REPO/sdk/python/template/." "$HK/$d/"
done

"$PY" - "$HK" <<'PYH'
import io, json, sys
base = sys.argv[1]
for tid, combo in (("hk-a", "Ctrl+Alt+X"), ("hk-b", "alt+ctrl+x")):
    d = base + "/" + tid + "/"
    m = json.load(io.open(d + "tool.json", encoding="utf-8"))
    m["id"] = tid
    m["name"] = "热键仲裁-" + tid
    m["contributes"]["commands"][0]["id"] = tid + ".run"
    m["contributes"]["hotkeys"] = [{"command": tid + ".run", "default": combo}]
    io.open(d + "tool.json", "w", encoding="utf-8", newline="\n").write(
        json.dumps(m, ensure_ascii=False, indent=2))
PYH
if [ "$?" -eq 0 ]; then pass "热键仲裁测试清单生成"; else fail "热键仲裁测试清单生成失败"; fi

# ── A. 静态冲突识别（doctor 诊断）──
"$EZ" doctor --tools-dir "$HK" --json --quiet > "$WORK/hk-doctor.json" 2>/dev/null
CONFLICTS=$("$PY" - "$WORK/hk-doctor.json" <<'PYDOC'
import json, io, sys
try:
    d = json.load(io.open(sys.argv[1], encoding='utf-8'))
    codes = [x['code'] for x in d.get('diagnostics', [])]
    print(sum(1 for c in codes if 'hotkey-conflict' in c))
except Exception as e:
    print('EXC:' + repr(e)[:150])
PYDOC
)
if [ "$CONFLICTS" = "1" ]; then
  pass "静态冲突被识别：Ctrl+Alt+X 与 alt+ctrl+x 规范化后同键（registry.hotkey-conflict）"
else
  fail "静态冲突未识别（期望 1 条 hotkey-conflict 诊断，实际 $CONFLICTS）"
fi

# ── B. CLI 仲裁归属（只筛 hk-a/hk-b）──
"$EZ" hotkeys --tools-dir "$HK" --json --quiet > "$WORK/hk-list.json" 2>/dev/null
ARB=$("$PY" - "$WORK/hk-list.json" <<'PYDOC'
import json, io, sys
try:
    d = json.load(io.open(sys.argv[1], encoding='utf-8'))
    hs = [h for h in d.get('hotkeys', []) if h['tool'] in ('hk-a', 'hk-b')]
    wins = [h['tool'] for h in hs if h['status'] == 'wins']
    blocked = [h['tool'] for h in hs if str(h['status']).startswith('blocked-by:')]
    print(str(len(wins)) + '/' + str(len(blocked)) + '/' + (wins[0] if wins else '?'))
except Exception as e:
    print('EXC:' + repr(e)[:150])
PYDOC
)
if [ "$ARB" = "1/1/hk-a" ]; then
  pass "仲裁归属确定：1 wins / 1 blocked-by，赢家 = hk-a（工具 Id 序）"
else
  fail "仲裁归属异常（期望 1/1/hk-a，实际 $ARB）"
fi

# ── C. 改键往返（真实安装根）──
"$EZ" hotkey set wordcount.count Ctrl+Shift+Z >/dev/null 2>&1
"$EZ" hotkey set filehash.hash  Ctrl+Shift+Z >/dev/null 2>&1
"$EZ" hotkeys --json --quiet > "$WORK/hk-post.json" 2>/dev/null
POST=$("$PY" - "$WORK/hk-post.json" <<'PYDOC'
import json, io, sys
try:
    d = json.load(io.open(sys.argv[1], encoding='utf-8'))
    ws = [h['tool'] for h in d['hotkeys'] if h['status'] == 'wins' and h['combo'] == 'Ctrl+Shift+Z']
    bs = [h['tool'] for h in d['hotkeys'] if str(h['status']).startswith('blocked-by:') and h['combo'] == 'Ctrl+Shift+Z']
    print((ws[0] if ws else '?') + '/' + (bs[0] if bs else 'none'))
except Exception as e:
    print('EXC:' + repr(e)[:150])
PYDOC
)
if [ "$POST" = "filehash/wordcount" ]; then
  pass "改键后仲裁翻转：Ctrl+Shift+Z 归 filehash，wordcount 落选"
else
  fail "改键后归属异常（期望 filehash/wordcount，实际 $POST）"
fi

"$EZ" hotkey unset wordcount.count >/dev/null 2>&1
"$EZ" hotkey unset filehash.hash  >/dev/null 2>&1
"$EZ" hotkeys --json --quiet > "$WORK/hk-final.json" 2>/dev/null
LEFT=$("$PY" - "$WORK/hk-final.json" <<'PYDOC'
import json, io, sys
try:
    d = json.load(io.open(sys.argv[1], encoding='utf-8'))
    print(sum(1 for h in d['hotkeys'] if h['combo'] == 'Ctrl+Shift+Z'))
except Exception as e:
    print('EXC:' + repr(e)[:150])
PYDOC
)
if [ "$LEFT" = "0" ]; then
  pass "unset 幂等回默认：Ctrl+Shift+Z 清零，原热键不受影响"
else
  fail "unset 后仍有残留（实际 $LEFT 个）"
fi
