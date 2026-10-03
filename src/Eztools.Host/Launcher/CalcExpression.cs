// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Eztools.Host.Launcher;

/// <summary>计算器结果状态（设计方案 §10.7 C 的错误码表）。</summary>
public enum CalcStatus
{
    /// <summary>L0 门槛未过 —— 用户明显在搜文件（含字母/汉字/纯数字/超长）。**完全静默**。</summary>
    NotAnExpression,

    /// <summary>词法层拒绝的字符（字母/汉字等）。**完全静默**（L0 已挡，本码由 selftest 直测）。</summary>
    InvalidChar,

    /// <summary>语法错（半成品输入 `1+` / `(1+2`）。**UI 静默**，但错误码与位置**必须可达**（selftest 直测 + 探针 JSON）。</summary>
    Syntax,

    /// <summary>求值成功。</summary>
    Ok,

    /// <summary>`1/0` · `5%0`。</summary>
    DivideByZero,

    /// <summary>`1e308*10`（超出双精度范围）。</summary>
    Overflow,

    /// <summary>`(-8)^0.5`（复数结果）· `0^-1`。</summary>
    Undefined,
}

/// <summary>
/// 一次计算的结局。<see cref="Position"/> = 出错处在**原串**里的下标（语法错/求值错的定位用）。
/// </summary>
public readonly record struct CalcOutcome(CalcStatus Status, double Value, int Position)
{
    /// <summary>
    /// 是否该产出一行结果。**求值错也出行**（设计方案 §10.7 A）—— 输入已是完整合法表达式，
    /// 用户**期望**一个结果；静默会让人以为计算器坏了（§3.5「禁 `:-0` 式静默」的落点）。
    /// 语法错与 L0 未过则**不出行**（半成品输入高频，出行即噪声）。
    /// </summary>
    public bool HasRow =>
        Status is CalcStatus.Ok or CalcStatus.DivideByZero or CalcStatus.Overflow or CalcStatus.Undefined;

    /// <summary>
    /// 用户可见的错误文案（<see cref="CalcStatus.Ok"/> 时为空串）。
    /// **文案在此单点确定** —— 渲染层不拼字符串，否则三种错迟早被写成同一句。
    /// </summary>
    public string ErrorText => Status switch
    {
        CalcStatus.DivideByZero => "除零",
        CalcStatus.Overflow => "溢出（超出双精度范围）",
        CalcStatus.Undefined => "结果未定义",
        _ => "",
    };
}

/// <summary>
/// 算术表达式解析与求值（W7-c，设计方案 §10.7 B/C/D）—— **纯函数、零 IO、零 UI**。
///
/// <para><b>为什么自写而不是引第三方</b>（§13 技术选型）：零新依赖边，且错误码/位置**能直接断言**；
/// 第三方表达式的错误模型不受我们控制（把"语法错"与"求值错"混成一种异常，就再也分不出
/// "用户还在打字"和"结果算不出来"这两件必须分开处置的事）。</para>
///
/// <para><b>两层结构（写错这一层是本 provider 最容易出的错）</b>：
/// <list type="number">
/// <item><b>L0 字符门槛</b>（<see cref="TryGate"/>）：判"这串东西**像不像**算式"。
///   不像 ⇒ 完全静默。它存在的唯一理由是 R5:把文件名（`report-2026.txt`）挡在门外。</item>
/// <item><b>L1 语法/求值</b>（<see cref="ParseAndEvaluate"/>）：判"这串算式对不对"。
///   语法错静默、求值错出行 —— 见 <see cref="CalcOutcome.HasRow"/>。</item>
/// </list></para>
///
/// <para><b>★ 两道硬上限（不可省）</b>：
/// <see cref="MaxInputLength"/>（防超长输入卡住 UI）与 <see cref="MaxDepth"/>（防递归下降栈溢出 ——
/// `StackOverflowException` 在本进程内**不可捕获**，真踩到是直接崩托盘）。
/// 两者都按"未过门槛 ⇒ 静默"处理，不产生错误行。</para>
///
/// <para><b>异常纪律</b>：本类**不用异常做控制流** —— 半成品输入（`1+`）是高频正常路径，
/// 用异常实现既是性能噪声，也会把"语法错"混进真正的故障。解析器用显式错误状态短路。</para>
/// </summary>
public static class CalcExpression
{
    /// <summary>输入长度上限（设计方案 §11.3 C-11）。超出 ⇒ L0 不过、静默。</summary>
    public const int MaxInputLength = 512;

    /// <summary>括号/指数的嵌套深度上限（§11.3 C-12）。超出 ⇒ 语法错、静默。</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// **UI 路径的唯一入口**：L0 门槛 → 解析 → 求值。
    /// 门槛不过 ⇒ <see cref="CalcStatus.NotAnExpression"/>（provider 据此静默）。
    /// </summary>
    public static CalcOutcome Evaluate(string? text) =>
        TryGate(text, out var trimmed)
            ? ParseAndEvaluate(trimmed)
            : new CalcOutcome(CalcStatus.NotAnExpression, double.NaN, 0);

    /// <summary>
    /// **跳过 L0 门槛**的解析求值（解析器层的直接入口）。
    ///
    /// <para><b>为什么必须公开</b>：<see cref="CalcStatus.Syntax"/> 与 <see cref="CalcStatus.InvalidChar"/>
    /// 在 UI 上是**静默**的 —— 若没有这条无门槛入口，这两个错误码就再也断言不到，
    /// 于是"我静默了"与"解析器坏了"变成同一种观测（S2 家族）。selftest 直测本方法。</para>
    /// </summary>
    public static CalcOutcome ParseAndEvaluate(string? text)
    {
        var s = text ?? "";
        if (s.Length == 0 || s.Length > MaxInputLength)
        {
            return new CalcOutcome(CalcStatus.NotAnExpression, double.NaN, 0);
        }

        var parser = new Parser(s);
        var value = parser.ParseAll();
        return parser.Error is { } kind
            ? new CalcOutcome(kind, double.NaN, parser.ErrorPosition)
            : new CalcOutcome(CalcStatus.Ok, value, 0);
    }

    /// <summary>
    /// L0 字符门槛（设计方案 §10.7 A）。**通过 = "像算式"**，不代表算式合法。
    ///
    /// <para>规则：① 去空白后非空且不超长；② 只含 `0-9 . + - * / % ^ ( )` 与空白，
    /// 外加**科学记数的 `e`/`E`**（R5 的唯一字母例外，且必须紧跟在数字之后、后面是 `数字` 或 `符号+数字`
    /// —— 这样 `report-1.txt` 里的 `e` 依旧被挡）；③ 至少一个数字；④ 至少一个运算符/括号。</para>
    ///
    /// <para><b>★ 指数里的符号不算运算符</b>：`1e+5` 与 `1e5` 同判（都是**纯数字** ⇒ 静默），
    /// 与 §11.3 C-2「纯数字不出结果」同一条口径。少了这一条，`1e+5` 会突然开始出结果。</para>
    /// </summary>
    public static bool TryGate(string? raw, out string trimmed)
    {
        trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxInputLength)
        {
            return false;
        }

        var hasDigit = false;
        var hasOperator = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];

            if (c is >= '0' and <= '9')
            {
                hasDigit = true;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == '.')
            {
                continue;
            }

            if (c is '+' or '-' or '*' or '/' or '%' or '^' or '(' or ')')
            {
                var isExponentSign = c is '+' or '-' && i > 0 && trimmed[i - 1] is 'e' or 'E';
                hasOperator |= !isExponentSign;
                continue;
            }

            if (c is 'e' or 'E')
            {
                var prevIsDigit = i > 0 && trimmed[i - 1] is >= '0' and <= '9';
                var j = i + 1;
                if (j < trimmed.Length && trimmed[j] is '+' or '-')
                {
                    j++;
                }

                var nextIsDigit = j < trimmed.Length && trimmed[j] is >= '0' and <= '9';
                if (!prevIsDigit || !nextIsDigit)
                {
                    return false;
                }

                continue;
            }

            return false;   // 字母 / 汉字 / 其它符号（`°`、`$`…）
        }

        return hasDigit && hasOperator;
    }

    /// <summary>去掉全部空白（结果行副标题用：`= 128*3/4`）。</summary>
    public static string StripWhitespace(string text)
    {
        if (text.Length == 0)
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 数值格式化（设计方案 §10.7 D）。**恒用 <see cref="CultureInfo.InvariantCulture"/>** ——
    /// 否则中文区域会输出 `96,0`（小数点变逗号），而结果是要被复制进别的程序的。
    ///
    /// <list type="bullet">
    /// <item>整数且 |v| &lt; 1e15 ⇒ 无小数无千分位（`96`）；</item>
    /// <item>|v| ≥ 1e15 或（v ≠ 0 且 |v| &lt; 1e-4）⇒ 科学记数（`1.15292E+18`）；</item>
    /// <item>其它 ⇒ 最多 10 位小数并去尾随零（`0.3333333333` / `0.125`）。</item>
    /// </list>
    /// </summary>
    public static string Format(double value)
    {
        if (value == 0)
        {
            return "0";   // 归一 -0（`0-0` 不该显示成 `-0`）
        }

        var abs = Math.Abs(value);

        if (value == Math.Truncate(value) && abs < 1e15)
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        if (abs >= 1e15 || abs < 1e-4)
        {
            return NormalizeExponent(value.ToString("G6", CultureInfo.InvariantCulture));
        }

        // 远离零的中点（默认的银行家舍入会让 0.00000000005 向偶取整，与用户直觉不符）
        var rounded = Math.Round(value, 10, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 科学记数归一：`e` → `E`，且指数**必须显式带号**。
    /// （`G6` 在 .NET 上已经是 `1.15292E+18` 形态，这里做的是防漂移 —— 不依赖格式化器的默认行为。）
    /// </summary>
    private static string NormalizeExponent(string text)
    {
        var e = text.IndexOfAny(['e', 'E']);
        if (e < 0)
        {
            return text;
        }

        var mantissa = text[..e];
        var exponent = text[(e + 1)..];
        if (exponent.Length > 0 && exponent[0] is not ('+' or '-'))
        {
            exponent = "+" + exponent;
        }

        return mantissa + "E" + exponent;
    }

    // ── 递归下降解析器（设计方案 §10.7 B 的 EBNF 逐条落地）──────────────────────

    private sealed class Parser(string s)
    {
        private readonly string _s = s;
        private int _i;
        private int _depth;

        /// <summary>出错码（null = 目前一切正常）。**首个错误即定**（后续调用全部短路）。</summary>
        public CalcStatus? Error { get; private set; }

        public int ErrorPosition { get; private set; }

        private bool Failed => Error is not null;

        /// <summary>解析整串：表达式之后**必须没有剩余字符**（`1+2)` 是语法错，不是"算到 2 为止"）。</summary>
        public double ParseAll()
        {
            var value = ParseExpr();
            if (Failed)
            {
                return double.NaN;
            }

            SkipWs();
            return _i < _s.Length ? Fail(CalcStatus.Syntax, _i) : value;
        }

        private double Fail(CalcStatus kind, int pos)
        {
            // 首个错误即定案（含位置）：后续短路的调用不得把它改写成"更靠后的同种错"
            if (Error is null)
            {
                Error = kind;
                ErrorPosition = pos;
            }

            return double.NaN;
        }

        /// <summary>非有限数一律当场定案：NaN ⇒ 未定义、±∞ ⇒ 溢出（逐算子检查，不等到末尾）。</summary>
        private double Check(double v, int pos)
        {
            if (double.IsNaN(v))
            {
                return Fail(CalcStatus.Undefined, pos);
            }

            return double.IsInfinity(v) ? Fail(CalcStatus.Overflow, pos) : v;
        }

        // expr := term (('+' | '-') term)*
        private double ParseExpr()
        {
            var left = ParseTerm();
            while (!Failed)
            {
                SkipWs();
                if (_i >= _s.Length || _s[_i] is not ('+' or '-'))
                {
                    break;
                }

                var opPos = _i;
                var op = _s[_i];
                _i++;
                var right = ParseTerm();
                if (Failed)
                {
                    break;
                }

                left = Check(op == '+' ? left + right : left - right, opPos);
            }

            return left;
        }

        // term := unary (('*' | '/' | '%') unary)*
        private double ParseTerm()
        {
            var left = ParseUnary();
            while (!Failed)
            {
                SkipWs();
                if (_i >= _s.Length || _s[_i] is not ('*' or '/' or '%'))
                {
                    break;
                }

                var opPos = _i;
                var op = _s[_i];
                _i++;
                var right = ParseUnary();
                if (Failed)
                {
                    break;
                }

                if (op is '/' or '%')
                {
                    // ★ 必须显式判零：IEEE 的 `x/0` 给 ±∞、`x%0` 给 NaN，都不会抛 ——
                    //   不判就等于把"除零"静默成"溢出/未定义"（错误码说谎）。
                    if (right == 0)
                    {
                        return Fail(CalcStatus.DivideByZero, opPos);
                    }

                    // `%` 取 C 的 fmod（符号跟随**被除数**）：C# 的 double `%` 即此语义。
                    // 刻意不用 Math.IEEERemainder —— 它在除数为 0 时返回 NaN 而不抛，语义反直觉。
                    left = Check(op == '/' ? left / right : left % right, opPos);
                }
                else
                {
                    left = Check(left * right, opPos);
                }
            }

            return left;
        }

        // unary := ('+' | '-')* power
        // ★ 一元号弱于幂：`-2^2` = -(2^2) = -4（标准数学约定，selftest 钉住）
        private double ParseUnary()
        {
            SkipWs();
            var negate = false;
            while (_i < _s.Length && _s[_i] is '+' or '-')
            {
                negate ^= _s[_i] == '-';
                _i++;
                SkipWs();
            }

            var value = ParsePower();
            return negate ? -value : value;
        }

        // power := primary ('^' unary)?    // 右结合 ⇒ 2^3^2 = 2^(3^2) = 512
        private double ParsePower()
        {
            var baseValue = ParsePrimary();
            if (Failed)
            {
                return double.NaN;
            }

            SkipWs();
            if (_i >= _s.Length || _s[_i] != '^')
            {
                return baseValue;
            }

            var opPos = _i;
            if (_depth >= MaxDepth)
            {
                return Fail(CalcStatus.Syntax, opPos);
            }

            _depth++;
            _i++;
            var exponent = ParseUnary();
            _depth--;
            if (Failed)
            {
                return double.NaN;
            }

            // `0^-1`：IEEE 给 +∞，但语义是**未定义**而非溢出（错误码必须说实话）
            if (baseValue == 0 && exponent < 0)
            {
                return Fail(CalcStatus.Undefined, opPos);
            }

            return Check(Math.Pow(baseValue, exponent), opPos);
        }

        // primary := number | '(' expr ')'
        private double ParsePrimary()
        {
            SkipWs();
            if (_i >= _s.Length)
            {
                return Fail(CalcStatus.Syntax, _i);
            }

            var c = _s[_i];

            if (c == '(')
            {
                var pos = _i;
                if (_depth >= MaxDepth)
                {
                    return Fail(CalcStatus.Syntax, pos);
                }

                _depth++;
                _i++;
                var inner = ParseExpr();
                _depth--;
                if (Failed)
                {
                    return double.NaN;
                }

                SkipWs();
                if (_i >= _s.Length || _s[_i] != ')')
                {
                    return Fail(CalcStatus.Syntax, _i);
                }

                _i++;
                return inner;
            }

            if (c is >= '0' and <= '9' || c == '.')
            {
                return ParseNumber();
            }

            return char.IsLetter(c)
                ? Fail(CalcStatus.InvalidChar, _i)
                : Fail(CalcStatus.Syntax, _i);
        }

        // number := (digit+ ('.' digit*)? | '.' digit+) ([eE] [+-]? digit+)?
        // ★ 相对设计稿 EBNF 的**唯一放宽**：允许 `.5` 形态（前导小数点）。
        //   理由见类注与设计方案 §10.7 B 的落地说明 —— 语法上无歧义（没有别的构造以 '.' 开头），
        //   而 `.5+1` 被静默吞掉会被当成 bug。
        private double ParseNumber()
        {
            var start = _i;
            var digits = 0;
            while (_i < _s.Length && _s[_i] is >= '0' and <= '9')
            {
                _i++;
                digits++;
            }

            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && _s[_i] is >= '0' and <= '9')
                {
                    _i++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                return Fail(CalcStatus.Syntax, start);
            }

            // 指数：只在形态完整时才吃掉（`1e` / `1e+` ⇒ 回退，留给上层报语法错）
            if (_i < _s.Length && _s[_i] is 'e' or 'E')
            {
                var save = _i;
                _i++;
                if (_i < _s.Length && _s[_i] is '+' or '-')
                {
                    _i++;
                }

                if (_i < _s.Length && _s[_i] is >= '0' and <= '9')
                {
                    while (_i < _s.Length && _s[_i] is >= '0' and <= '9')
                    {
                        _i++;
                    }
                }
                else
                {
                    _i = save;
                }
            }

            return double.TryParse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? Check(v, start)
                : Fail(CalcStatus.Syntax, start);
        }

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }
    }
}
