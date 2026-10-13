using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JarvisUtils.Modules.Calculator;

/// <summary>
/// Вычислитель выражений в духе калькулятора PowerToys Run.
/// Поддерживает: + - * / % ^ (или **), факториал «!» (целые точно, дробные — через
/// гамма-функцию), константы (pi, e, tau, phi), функции (sqrt, ln, log, sin…, min, max…),
/// скобки, неявное умножение (2pi, 2(3+4)) и юникод-символы (π, √, ×, ÷, −, ², ³),
/// литералы 0xFF / 0b1010 / 0o17, «20% of 150» и «150 + 20%» (= 180).
/// Тригонометрия — в радианах, десятичный разделитель — точка.
/// Перевод единиц, валют и систем счисления — уровнем выше, в <see cref="CalcEngine"/>.
/// </summary>
public static class MathEvaluator
{
    /// <summary>true и value при успехе; иначе — текст ошибки для dim-строки под вводом.</summary>
    public static bool TryEvaluate(string expression, out double value, out string error)
    {
        value = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(expression)) { error = "введите выражение"; return false; }
        try
        {
            value = new Parser(expression).Parse();
            return true;
        }
        catch (EvalException ex) { error = ex.Message; }
        catch { error = "не удалось вычислить"; }
        return false;
    }

    /// <summary>
    /// Как <see cref="TryEvaluate"/>, но понимает и «10 km in mi», «100$ в руб», «255 in hex» —
    /// результат уже отформатирован (число без единицы: её пользователь написал сам).
    /// Курс валют ещё не загружен → false и error «загрузка курса…» (загрузка уже запущена).
    /// </summary>
    public static bool TryEvaluateText(string expression, out string result, out string error)
    {
        var r = CalcEngine.Evaluate(expression);
        result = r.Kind == CalcResultKind.Value ? r.Text : "";
        error = r.Kind == CalcResultKind.Value ? "" : r.Text;
        return r.Kind == CalcResultKind.Value;
    }

    /// <summary>Компактная запись: без хвостов нулей и артефактов двоичной арифметики (0.1+0.2 → 0.3).</summary>
    public static string Format(double v)
    {
        if (double.IsNaN(v)) return "не число";
        if (double.IsPositiveInfinity(v)) return "∞";
        if (double.IsNegativeInfinity(v)) return "-∞";
        if (v == 0) return "0";
        if (Math.Abs(v) < 1e15 && v == Math.Floor(v))
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("G15", CultureInfo.InvariantCulture); // G15: 0.30000000000000004 → 0.3
    }

    // ── Токены ──

    private enum TokKind { Num, Ident, Op, LParen, RParen, Comma, End }

    private readonly record struct Tok(TokKind Kind, double Num, string Text, char Op);

    private static Tok Num(double v) => new(TokKind.Num, v, "", ' ');
    private static Tok Id(string s) => new(TokKind.Ident, 0, s, ' ');
    private static Tok Op(char c) => new(TokKind.Op, 0, "", c);
    private static Tok Punct(TokKind k) => new(k, 0, "", ' ');

    private static List<Tok> Tokenize(string s)
    {
        var toks = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            switch (c)
            {
                case '+': toks.Add(Op('+')); i++; break;
                case '-' or '−' or '–': toks.Add(Op('-')); i++; break; // − U+2212, – U+2013 (автозамена в Word)
                case '*' or '×' or '·':
                    if (c == '*' && i + 1 < s.Length && s[i + 1] == '*') { toks.Add(Op('^')); i += 2; } // ** = ^
                    else { toks.Add(Op('*')); i++; }
                    break;
                case '/' or '÷': toks.Add(Op('/')); i++; break;
                case '^': toks.Add(Op('^')); i++; break;
                case '%': toks.Add(Op('%')); i++; break; // остаток; «50%» без операнда справа — проценты
                case '!': toks.Add(Op('!')); i++; break;
                case '(': toks.Add(Punct(TokKind.LParen)); i++; break;
                case ')': toks.Add(Punct(TokKind.RParen)); i++; break;
                case ',': toks.Add(Punct(TokKind.Comma)); i++; break;
                case '√': toks.Add(Id("sqrt")); i++; break;   // √ ≡ sqrt
                case 'π': toks.Add(Id("pi")); i++; break;
                case '²' or '³': toks.Add(Op(c)); i++; break; // постфиксные степени
                default:
                    if (c == '0' && i + 2 < s.Length && RadixOf(s[i + 1]) is int radix and > 0 && IsRadixDigit(s[i + 2], radix))
                    {
                        // 0xFF, 0b1010, 0o17 (подчёркивания-разделители: 0b1111_0000)
                        int start = i;
                        i += 2;
                        ulong acc = 0;
                        while (i < s.Length && (IsRadixDigit(s[i], radix) || s[i] == '_'))
                        {
                            if (s[i] != '_')
                            {
                                ulong digit = (ulong)Convert.ToInt32(s[i].ToString(), 16);
                                if (acc > (ulong.MaxValue - digit) / (ulong)radix)
                                    throw new EvalException($"«{s[start..]}» — слишком большое число");
                                acc = acc * (ulong)radix + digit;
                            }
                            i++;
                        }
                        toks.Add(Num(acc));
                    }
                    else if (char.IsDigit(c) || c == '.')
                    {
                        int start = i;
                        while (i < s.Length && char.IsDigit(s[i])) i++;
                        if (i < s.Length && s[i] == '.')
                        {
                            i++;
                            while (i < s.Length && char.IsDigit(s[i])) i++;
                        }
                        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                        {
                            int save = i;
                            i++;
                            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                            if (i < s.Length && char.IsDigit(s[i]))
                                while (i < s.Length && char.IsDigit(s[i])) i++;
                            else i = save; // «2e» — это 2·e, а не оборванная экспонента
                        }
                        string text = s[start..i];
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                            throw new EvalException($"«{text}» — не число");
                        toks.Add(Num(val));
                    }
                    else if (char.IsLetter(c) || c == '_')
                    {
                        int start = i;
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                        toks.Add(Id(s[start..i].ToLowerInvariant()));
                    }
                    else throw new EvalException($"неожидаемый символ «{c}»");
                    break;
            }
        }
        toks.Add(Punct(TokKind.End));
        return toks;
    }

    private static int RadixOf(char c) => c switch
    {
        'x' or 'X' => 16,
        'b' or 'B' => 2,
        'o' or 'O' => 8,
        _ => 0
    };

    private static bool IsRadixDigit(char c, int radix) => radix switch
    {
        2 => c is '0' or '1',
        8 => c is >= '0' and <= '7',
        _ => char.IsAsciiHexDigit(c)
    };

    // ── Грамматика (рекурсивный спуск) ──
    //
    // Add     := Mul (('+' | '-') Mul)*               «a ± b%» — это a·(1 ± b/100), как у Google
    // Mul     := Unary (('*' | '/' | '%' | неявное умножение) Unary | '%' | '%' of Unary)*
    // Unary   := ('+' | '-') Unary | Pow
    // Pow     := Postfix ('^' Unary)?              правая ассоциативность; -2^2 = -(2^2)
    // Postfix := Primary ('!' | '²' | '³')*
    // Primary := число | константа | функция '(' арг ')' | функция Unary | '(' Add ')'

    private sealed class Parser(string source)
    {
        private readonly List<Tok> _t = Tokenize(source);
        private int _p;

        private Tok Peek => _t[_p];
        private Tok Peek2 => _p + 1 < _t.Count ? _t[_p + 1] : _t[_p];
        private Tok Next() => _t[_p++];

        public double Parse()
        {
            double v = Add();
            if (Peek.Kind != TokKind.End)
                throw new EvalException($"неожидаемый символ «{Describe(Peek)}»");
            return v;
        }

        private double Add()
        {
            double v = Mul(out _);
            while (Peek is { Kind: TokKind.Op, Op: '+' or '-' })
            {
                char op = Next().Op;
                double r = Mul(out bool percentTerm);
                if (percentTerm) r *= v; // «150 + 20%» = 180: процент берётся от левой части
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        /// <param name="percentTerm">слагаемое — ровно «число%» (без других операций): для «a ± b%»</param>
        private double Mul(out bool percentTerm)
        {
            double v = Unary();
            percentTerm = false;
            int ops = 0;
            while (true)
            {
                if (Peek is { Kind: TokKind.Op, Op: '%' } && Peek2 is { Kind: TokKind.Ident, Text: "of" or "от" })
                {
                    Next(); Next();
                    v = v / 100 * Unary(); // «20% of 150» = 30; раньше это была ошибка, так что ничего не ломаем
                    ops++;
                    percentTerm = false;
                }
                else if (Peek is { Kind: TokKind.Op, Op: '%' } && !StartsValue(Peek2))
                {
                    Next();
                    v /= 100; // «50%» → 0.5: % без операнда справа — проценты
                    percentTerm = ++ops == 1;
                }
                else if (Peek is { Kind: TokKind.Op, Op: '*' or '/' or '%' })
                {
                    char op = Next().Op;
                    double r = Unary();
                    ops++;
                    percentTerm = false;
                    if (op == '*') v *= r;
                    else
                    {
                        if (r == 0) throw new EvalException("деление на ноль");
                        v = op == '/' ? v / r : v % r;
                    }
                }
                else if (StartsValue(Peek))
                {
                    v *= Unary(); // неявное умножение: 2pi, 2(3+4), (1+2)(3+4), 2 sqrt(2)
                    ops++;
                    percentTerm = false;
                }
                else return v;
            }
        }

        private static bool StartsValue(Tok t) =>
            t.Kind is TokKind.Num or TokKind.Ident or TokKind.LParen;

        private double Unary()
        {
            if (Peek is { Kind: TokKind.Op, Op: '-' }) { Next(); return -Unary(); }
            if (Peek is { Kind: TokKind.Op, Op: '+' }) { Next(); return Unary(); }
            return Pow();
        }

        private double Pow()
        {
            double v = Postfix();
            if (Peek is { Kind: TokKind.Op, Op: '^' })
            {
                Next();
                v = Math.Pow(v, Unary()); // 2^3^2 = 2^(3^2); 2^-3 — унарный минус в показателе
            }
            return v;
        }

        private double Postfix()
        {
            double v = Primary();
            while (Peek is { Kind: TokKind.Op, Op: '!' or '²' or '³' })
            {
                char op = Next().Op;
                v = op switch { '!' => Factorial(v), '²' => v * v, _ => v * v * v };
            }
            return v;
        }

        private double Primary()
        {
            Tok t = Peek;
            switch (t.Kind)
            {
                case TokKind.Num:
                    Next();
                    return t.Num;

                case TokKind.LParen:
                    Next();
                    double v = Add();
                    Expect(TokKind.RParen, "не хватает «)»");
                    return v;

                case TokKind.Ident:
                    Next();
                    return ApplyName(t.Text);

                default:
                    throw new EvalException(t.Kind == TokKind.End
                        ? "выражение неполное"
                        : $"неожидаемый символ «{Describe(t)}»");
            }
        }

        private double ApplyName(string name)
        {
            switch (name)
            {
                case "pi": return Math.PI;
                case "e": return Math.E;
                case "tau": return 2 * Math.PI;
                case "phi": return (1 + Math.Sqrt(5)) / 2;
            }

            if (!Functions.TryGetValue(name, out var f))
                throw new EvalException($"неизвестное имя «{name}»");

            var args = new List<double>();
            if (Peek.Kind == TokKind.LParen)
            {
                Next();
                args.Add(Add());
                while (Peek.Kind == TokKind.Comma) { Next(); args.Add(Add()); }
                Expect(TokKind.RParen, "не хватает «)»");
            }
            else
            {
                args.Add(Unary()); // без скобок: sin pi, sqrt 9 — применяется к следующему операнду
            }

            if (args.Count < f.MinArgs || args.Count > f.MaxArgs)
                throw new EvalException($"«{name}»: неверное число аргументов");
            return f.Invoke([.. args]);
        }

        private void Expect(TokKind kind, string message)
        {
            if (Peek.Kind == kind) { Next(); return; }
            if (Peek.Kind == TokKind.End) throw new EvalException("выражение неполное");
            throw new EvalException($"{message} (нашёлся «{Describe(Peek)}»)");
        }

        private static string Describe(Tok t) => t.Kind switch
        {
            TokKind.Num => Format(t.Num),
            TokKind.Ident => t.Text,
            TokKind.LParen => "(",
            TokKind.RParen => ")",
            TokKind.Comma => ",",
            _ => t.Op.ToString()
        };
    }

    // ── Функции ──

    private sealed record MathFunc(int MinArgs, int MaxArgs, Func<double[], double> Invoke);

    private static MathFunc One(Func<double, double> f) => new(1, 1, a => f(a[0]));
    private static MathFunc Many(int min, int max, Func<double[], double> f) => new(min, max, f);

    private static readonly Dictionary<string, MathFunc> Functions = new()
    {
        ["sqrt"]  = One(Math.Sqrt),
        ["cbrt"]  = One(x => x < 0 ? -Math.Pow(-x, 1.0 / 3.0) : Math.Pow(x, 1.0 / 3.0)),
        ["abs"]   = One(Math.Abs),
        ["exp"]   = One(Math.Exp),
        ["ln"]    = One(Math.Log),
        ["log"]   = Many(1, 2, a => a.Length == 1 ? Math.Log10(a[0]) : Math.Log(a[0], a[1])),
        ["log2"]  = One(Math.Log2),
        ["log10"] = One(Math.Log10),
        ["sin"]   = One(Math.Sin),
        ["cos"]   = One(Math.Cos),
        ["tan"]   = One(Math.Tan),
        ["asin"]  = One(Math.Asin),
        ["acos"]  = One(Math.Acos),
        ["atan"]  = One(Math.Atan),
        ["sinh"]  = One(Math.Sinh),
        ["cosh"]  = One(Math.Cosh),
        ["tanh"]  = One(Math.Tanh),
        ["floor"] = One(Math.Floor),
        ["ceil"]  = One(Math.Ceiling),
        ["round"] = Many(1, 2, a => a.Length == 1
            ? Math.Round(a[0], MidpointRounding.AwayFromZero)
            : Math.Round(a[0], (int)a[1], MidpointRounding.AwayFromZero)),
        ["sign"]  = One(x => Math.Sign(x)),
        ["fact"]  = One(Factorial),
        ["gamma"] = One(Gamma),
        ["min"]   = Many(2, 32, a => a.Min()),
        ["max"]   = Many(2, 32, a => a.Max()),
        ["avg"]   = Many(2, 32, a => a.Average()),
    };

    // ── Факториал и гамма-функция ──

    private static double Factorial(double x)
    {
        if (x == Math.Floor(x)) // целое — точный цикл
        {
            if (x < 0) throw new EvalException("факториал отрицательного целого не определён");
            if (x > 170) throw new EvalException("факториал: слишком большое число (максимум 170)");
            double r = 1;
            for (long i = 2; i <= (long)x; i++) r *= i;
            return r;
        }
        return Gamma(x + 1); // дробный факториал через Γ: 0.5! = Γ(1.5) ≈ 0.886
    }

    /// <summary>Гамма-функция, аппроксимация Ланцоша (g=7, n=9).</summary>
    private static double Gamma(double x)
    {
        if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1 - x)); // формула отражения
        x -= 1;
        double[] g =
        [
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
            1.5056327351493116e-7
        ];
        double a = g[0];
        for (int i = 1; i < g.Length; i++) a += g[i] / (x + i);
        double t = x + 7.5;
        return Math.Sqrt(2 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * a;
    }

    private sealed class EvalException(string message) : Exception(message);
}