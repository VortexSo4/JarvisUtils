using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisUtils.Modules.Calculator;

public enum CalcResultKind { Value, Error, Pending }

/// <summary>
/// Ответ калькулятора. Text — число (его и копируем/вставляем) либо текст ошибки/ожидания;
/// Suffix — единица результата (показывается тускло, не копируется); Note — вторичная справка
/// справа (другие системы счисления, дата курса); Tooltip — подробности при наведении.
/// </summary>
public sealed record CalcResult(CalcResultKind Kind, string Text, string? Suffix = null, string? Note = null, string? Tooltip = null)
{
    public static CalcResult Error(string text) => new(CalcResultKind.Error, text);
}

/// <summary>
/// Надстройка над <see cref="MathEvaluator"/>: «выражение единица in/to/в единица»
/// (10 km in mi, (2+3) km in m, 100$ в руб), системы счисления (255 in hex) и подписи
/// к целым результатам (hex/bin). Чистая математика по-прежнему уходит в MathEvaluator.
/// </summary>
public static class CalcEngine
{
    // разделитель «откуда → куда». Слова — только в окружении пробелов: «in» бывает и дюймом («10 in to cm»)
    private static readonly Regex Separator = new(
        @"(?<=\s)(?:in|to|into|as|в|во)(?=\s)|->|=>|→",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingSeparator = new(
        @"(?:\s(?:in|to|into|as|в|во)|->|=>|→)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BaseLiteral = new(@"(?<![\w.])0[xbo][0-9a-f_]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BaseLiteralTail = new(@"(?<![\w.])0[xbo][0-9a-f_]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> BaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hex"] = 16, ["hexadecimal"] = 16, ["шестнадцатеричную"] = 16, ["шестнадцатеричная"] = 16,
        ["bin"] = 2, ["binary"] = 2, ["двоичную"] = 2, ["двоичная"] = 2,
        ["oct"] = 8, ["octal"] = 8, ["восьмеричную"] = 8, ["восьмеричная"] = 8,
        ["dec"] = 10, ["decimal"] = 10, ["десятичную"] = 10, ["десятичная"] = 10,
    };

    private const double MaxExactInteger = 9007199254740992; // 2^53: дальше double теряет единицы

    public static CalcResult Evaluate(string expression)
    {
        string expr = expression.Trim();
        if (expr.Length == 0) return CalcResult.Error("введите выражение");

        CalcResult? conversionError = null;
        var seps = Separator.Matches(expr);
        // справа налево: последний разделитель — самый вероятный («10 in to cm» — дюймы в сантиметры)
        for (int k = seps.Count - 1; k >= 0; k--)
        {
            var m = seps[k];
            string left = expr[..m.Index].Trim();
            string right = expr[(m.Index + m.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0) continue;
            var r = TryConvert(left, right);
            if (r.Kind != CalcResultKind.Error) return r;
            conversionError ??= r;
        }

        if (MathEvaluator.TryEvaluate(expr, out double value, out string error))
            return PlainNumber(value, BaseLiteral.IsMatch(expr));

        if (conversionError != null) return conversionError;
        if (TrailingSeparator.IsMatch(expr)) return CalcResult.Error("во что перевести?");
        if (TryExtractSource(expr, out _, out var units))
            return CalcResult.Error(units[0].Dim == Dimension.Currency
                ? $"во что перевести? Например: {expr} в {(units[0].Symbol == "RUB" ? "usd" : "руб")}"
                : $"во что перевести? Например: {expr} in …");
        return CalcResult.Error(error);
    }

    // ── обычное число ──

    private static CalcResult PlainNumber(double v, bool inputHadBaseLiteral)
    {
        string text = MathEvaluator.Format(v);
        if (!IsExactInteger(v)) return new CalcResult(CalcResultKind.Value, text);
        long n = (long)v;
        string tooltip = $"hex  {ToBase(n, 16)}\noct  {ToBase(n, 8)}\nbin  {ToBase(n, 2)}";
        // hex/bin справа — только если пользователь сам думает в этих системах: иначе это шум у каждого «2+2»
        string? note = inputHadBaseLiteral ? $"{ToBase(n, 16)} · {GroupBinary(ToBase(n, 2))}" : null;
        return new CalcResult(CalcResultKind.Value, text, Note: note, Tooltip: tooltip);
    }

    // ── «левая часть in правая» ──

    private static CalcResult TryConvert(string left, string right)
    {
        if (BaseNames.TryGetValue(right, out int radix)) return ConvertBase(left, radix);

        var targets = UnitCatalog.Resolve(right, CurrencyRates.HasCode);
        if (targets.Count == 0) return CalcResult.Error($"неизвестная единица «{right}»");
        if (!TryExtractSource(left, out string numberExpr, out var sources))
            return CalcResult.Error($"укажите исходную единицу: например, «10 km in {right}»");

        UnitDef? from = null, to = null;
        foreach (var s in sources)
        {
            foreach (var t in targets)
                if (s.Dim == t.Dim) { from = s; to = t; break; }
            if (from != null) break;
        }
        if (from == null || to == null)
            return CalcResult.Error($"не перевести: {sources[0].Symbol} — {Describe(sources[0].Dim)}, {targets[0].Symbol} — {Describe(targets[0].Dim)}");

        double amount = 1; // «km in m», «usd в руб» — за единицу
        if (numberExpr.Trim().Length > 0 && !MathEvaluator.TryEvaluate(numberExpr, out amount, out string error))
            return CalcResult.Error(error);

        return from.Dim == Dimension.Currency
            ? ConvertCurrency(amount, from.Symbol, to.Symbol)
            : ConvertUnit(amount, from, to, right);
    }

    private static CalcResult ConvertUnit(double amount, UnitDef from, UnitDef to, string typedTarget)
    {
        double result = to.FromBase(from.ToBase(amount));
        if (double.IsNaN(result) || double.IsInfinity(result)) return CalcResult.Error("слишком большое число");
        // единицу показываем как её написал пользователь («мили», «км/ч»); температуру — каноном (°C)
        string suffix = to.Dim == Dimension.Temperature ? to.Symbol : typedTarget;
        // у температуры «1 °C = 33.8 °F» вводит в заблуждение (шкалы со сдвигом) — без справки
        string? note = to.Dim == Dimension.Temperature || from == to
            ? null
            : $"1 {from.Symbol} = {MathEvaluator.Format(RoundSignificant(from.Factor / to.Factor, 10))} {to.Symbol}";
        return new CalcResult(CalcResultKind.Value, MathEvaluator.Format(RoundSignificant(result, 10)), suffix, note);
    }

    private static CalcResult ConvertCurrency(double amount, string from, string to)
    {
        CurrencyRates.EnsureFresh();
        var rates = CurrencyRates.Current;
        if (rates == null)
            return CurrencyRates.IsLoading
                ? new CalcResult(CalcResultKind.Pending, "загрузка курса…")
                : CalcResult.Error("курс недоступен — нет сети?");
        if (!rates.PerUsd.TryGetValue(from, out double perUsdFrom)) return CalcResult.Error($"нет курса для {from}");
        if (!rates.PerUsd.TryGetValue(to, out double perUsdTo)) return CalcResult.Error($"нет курса для {to}");

        double rate = perUsdTo / perUsdFrom;
        string date = rates.RatesDateUtc.ToLocalTime().ToString("dd.MM", CultureInfo.InvariantCulture);
        string when = !rates.IsExpired ? $"курс на {date}"
            : CurrencyRates.IsLoading ? $"курс от {date}, обновляется…"
            : $"курс от {date} — нет сети";
        return new CalcResult(CalcResultKind.Value, FormatMoney(amount * rate), to,
            Note: $"1 {from} = {FormatMoney(rate)} {to} · {when}",
            Tooltip: $"Источник: {rates.Source}\nОбновлено: {rates.FetchedUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
    }

    private static CalcResult ConvertBase(string left, int radix)
    {
        if (!MathEvaluator.TryEvaluate(left, out double v, out string error)) return CalcResult.Error(error);
        if (!IsExactInteger(v)) return CalcResult.Error("системы счисления — только для целых чисел");
        long n = (long)v;
        string dec = MathEvaluator.Format(v);
        return radix switch
        {
            16 => new CalcResult(CalcResultKind.Value, ToBase(n, 16), Note: $"{dec} · {GroupBinary(ToBase(n, 2))}"),
            8 => new CalcResult(CalcResultKind.Value, ToBase(n, 8), Note: $"{dec} · {ToBase(n, 16)}"),
            2 => new CalcResult(CalcResultKind.Value, ToBase(n, 2), Note: $"{dec} · {ToBase(n, 16)}"),
            _ => new CalcResult(CalcResultKind.Value, dec, Note: $"{ToBase(n, 16)} · {GroupBinary(ToBase(n, 2))}")
        };
    }

    // ── разбор «число единица» ──

    /// <summary>
    /// «(2+3) km» → ("(2+3) ", [km]); «$100» → ("100", [USD]); «1.5GB» → ("1.5", [GB]).
    /// Берётся самый длинный хвост, который является единицей («km/h», а не «h»).
    /// </summary>
    private static bool TryExtractSource(string left, out string numberExpr, out IReadOnlyList<UnitDef> units)
    {
        numberExpr = left;
        units = [];
        if (left.Length > 0 && UnitCatalog.PrefixCurrencySymbols.Contains(left[0]))
        {
            units = UnitCatalog.Resolve(left[0].ToString());
            numberExpr = left[1..];
            return units.Count > 0;
        }

        for (int i = 0; i < left.Length; i++)
        {
            char c = left[i];
            if (char.IsWhiteSpace(c) || char.IsDigit(c) || c is '(' or ')' or '.' or '-' or '+') continue;
            if (i > 0)
            {
                char prev = left[i - 1];
                // хвост — отдельное слово или идёт сразу за числом/скобкой: «10km», «(1+2)m», «2pi m»
                if (!(char.IsWhiteSpace(prev) || char.IsDigit(prev) || prev is ')' or '.')) continue;
                if (BaseLiteralTail.IsMatch(left[..(i + 1)])) continue; // «0x1f» — не «0x1» и фаренгейты
            }
            var found = UnitCatalog.Resolve(left[i..], CurrencyRates.HasCode);
            if (found.Count == 0) continue;
            numberExpr = left[..i];
            units = found;
            return true;
        }
        return false;
    }

    // ── форматирование ──

    private static bool IsExactInteger(double v) =>
        !double.IsNaN(v) && !double.IsInfinity(v) && v == Math.Floor(v) && Math.Abs(v) <= MaxExactInteger;

    /// <summary>10 значащих цифр: «6.213711922 mi» вместо «6.21371192237334 mi», и без «1500.0000000000002».</summary>
    private static double RoundSignificant(double v, int digits) =>
        v == 0 || double.IsNaN(v) || double.IsInfinity(v)
            ? v
            : double.Parse(v.ToString("G" + digits, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Деньги: копейки у сумм от 1, у мелочи — 4 значащие цифры (1 RUB = 0.01052 USD).</summary>
    private static string FormatMoney(double v) =>
        MathEvaluator.Format(Math.Abs(v) >= 1 ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : RoundSignificant(v, 4));

    private static string ToBase(long n, int radix)
    {
        string prefix = radix switch { 16 => "0x", 8 => "0o", 2 => "0b", _ => "" };
        string sign = n < 0 ? "-" : "";
        ulong u = n < 0 ? (ulong)(-(n + 1)) + 1 : (ulong)n; // без переполнения на long.MinValue
        string digits = radix == 16 ? u.ToString("X") : ToRadix(u, radix);
        return sign + prefix + digits;
    }

    private static string ToRadix(ulong u, int radix)
    {
        if (u == 0) return "0";
        var sb = new StringBuilder();
        while (u > 0) { sb.Insert(0, (char)('0' + (int)(u % (ulong)radix))); u /= (ulong)radix; }
        return sb.ToString();
    }

    /// <summary>«0b11111111» → «0b1111 1111» — для глаз; в буфер такое не попадает.</summary>
    private static string GroupBinary(string bin)
    {
        int start = bin.IndexOf('b') + 1;
        string digits = bin[start..];
        var sb = new StringBuilder(bin[..start]);
        int lead = digits.Length % 4;
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (i - lead) % 4 == 0) sb.Append(' ');
            sb.Append(digits[i]);
        }
        return sb.ToString();
    }

    private static string Describe(Dimension d) => d switch
    {
        Dimension.Length => "длина",
        Dimension.Mass => "масса",
        Dimension.Time => "время",
        Dimension.Data => "объём данных",
        Dimension.Speed => "скорость",
        Dimension.Area => "площадь",
        Dimension.Volume => "объём",
        Dimension.Temperature => "температура",
        _ => "валюта"
    };
}
