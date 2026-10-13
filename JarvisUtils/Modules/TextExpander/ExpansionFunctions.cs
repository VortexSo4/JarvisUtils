using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JarvisUtils.Modules.Calculator;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Реестр функций шаблона. Новая функция — одна строка в статическом конструкторе:
/// Register("name", (ctx, args) => ...) для синхронных, RegisterAsync — для асинхронных,
/// Register(new MyFunction()) — для классов IExpansionFunction.
/// Аргументы приходят уже вычисленными (вложенные {…} и %1 подставлены).
/// </summary>
public static class ExpansionFunctions
{
    private static readonly Dictionary<string, ExpansionFunc> Map = new(StringComparer.OrdinalIgnoreCase);

    static ExpansionFunctions()
    {
        // ── прежние функции (поведение сохранено) ──
        Register(new IncrFunction());
        Register(new RandFunction());
        Register(new RepeatFunction());
        Register(new CalcFunction());
        Register("date", (_, a) => DateTimeFunction.Evaluate(a, "dd.MM.yyyy"));
        Register("time", (_, a) => DateTimeFunction.Evaluate(a, "HH:mm"));

        // ── текст ──
        Register("upper", (_, a) => JoinArgs(a).ToUpper(CultureInfo.CurrentCulture));
        Register("lower", (_, a) => JoinArgs(a).ToLower(CultureInfo.CurrentCulture));
        Register("title", (_, a) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(JoinArgs(a).ToLower(CultureInfo.CurrentCulture)));
        Register("trim", (_, a) => JoinArgs(a).Trim());
        Register("uuid", (_, a) => UuidFunction(a));

        // ── буфер обмена: чтение идёт на clipboard-поток с таймаутом, UI не блокируем ──
        RegisterAsync("clip", async (ctx, _) =>
        {
            var clipboard = ctx.Services?.Clipboard;
            if (clipboard == null) return "";
            return await Task.Run(clipboard.GetText) ?? "";
        });

        Register("counter", (ctx, a) => CounterFunction(ctx, a));
        RegisterAsync("choice", ChoiceFunction);
        RegisterAsync("input", InputFunction);
    }

    public static void Register(IExpansionFunction function) =>
        Map[function.Name] = (_, args) => new ValueTask<string?>(function.Evaluate(args.ToArray()));

    public static void Register(string name, Func<ExpansionContext, IReadOnlyList<string>, string?> function) =>
        Map[name] = (ctx, args) => new ValueTask<string?>(function(ctx, args));

    public static void RegisterAsync(string name, ExpansionFunc function) => Map[name] = function;

    public static bool TryGet(string name, out ExpansionFunc function) => Map.TryGetValue(name, out function!);

    public static IReadOnlyCollection<string> Names => Map.Keys;

    /// <summary>Аргументы, разрезанные запятыми, обратно в текст: {upper(привет, мир)} → «ПРИВЕТ, МИР».</summary>
    internal static string JoinArgs(IReadOnlyList<string> args) => string.Join(", ", args);

    private static string UuidFunction(IReadOnlyList<string> args)
    {
        var format = args.Count > 0 && args[0] is "N" or "D" or "B" or "P" or "n" or "d" or "b" or "p" ? args[0].ToUpperInvariant() : "D";
        return Guid.NewGuid().ToString(format);
    }

    private static string? CounterFunction(ExpansionContext ctx, IReadOnlyList<string> args)
    {
        var name = args.Count > 0 && args[0].Length > 0 ? args[0] : "default";
        long start = 1;
        if (args.Count > 1 && !long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) return null;
        if (ctx.Counters == null) return start.ToString(CultureInfo.InvariantCulture);
        long value = ctx.DryRun ? ctx.Counters.Peek(name, start) : ctx.Counters.Next(name, start);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>{choice(a|b|c)} или {choice(a, b, c)}.</summary>
    internal static List<string> ChoiceOptions(IReadOnlyList<string> args)
    {
        IEnumerable<string> options = args.Count == 1 ? args[0].Split('|') : args;
        return options.Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
    }

    private static async ValueTask<string?> ChoiceFunction(ExpansionContext ctx, IReadOnlyList<string> args)
    {
        var options = ChoiceOptions(args);
        if (options.Count == 0) return null;
        if (ctx.DryRun) return "[выбор: " + string.Join(" | ", options) + "]";
        if (ctx.Ui == null) return options[0];
        ctx.UsedInteractiveUi = true;
        return await ctx.Ui.ChooseAsync(ctx.Title, options) ?? throw new ExpansionCancelledException();
    }

    private static async ValueTask<string?> InputFunction(ExpansionContext ctx, IReadOnlyList<string> args)
    {
        var label = args.Count > 0 && args[0].Length > 0 ? args[0] : "Значение";
        var initial = args.Count > 1 ? args[1] : null;
        if (ctx.DryRun) return "[ввод: " + label + "]";
        if (ctx.Ui == null) return initial ?? "";
        ctx.UsedInteractiveUi = true;
        return await ctx.Ui.PromptAsync(label, initial) ?? throw new ExpansionCancelledException();
    }
}

public sealed class IncrFunction : IExpansionFunction
{
    private const int MaxItems = 10_000;

    public string Name => "incr";

    /// <summary>{incr(от, до[, n | разделитель])}: n — с новой строки, по умолчанию — через пробел.</summary>
    public string? Evaluate(string[] args)
    {
        if (args.Length < 2) return null;
        if (!long.TryParse(args[0], out long from)) return null;
        if (!long.TryParse(args[1], out long to)) return null;
        if (Math.Abs(to - from) >= MaxItems) return null;

        string separator = " ";
        if (args.Length >= 3 && args[2].Length > 0)
            separator = args[2].Trim().Equals("n", StringComparison.OrdinalIgnoreCase) ? "\n" : args[2];

        long step = from <= to ? 1 : -1; // обратный порядок: {incr(5,1)} → 5 4 3 2 1
        var sb = new StringBuilder();
        for (long i = from; ; i += step)
        {
            if (i != from) sb.Append(separator);
            sb.Append(i);
            if (i == to) break;
        }
        return sb.ToString();
    }
}

public sealed class RandFunction : IExpansionFunction
{
    public string Name => "rand";

    /// <summary>{rand(мин, макс)} — целое включительно; {rand(a|b|c)} — случайный вариант.</summary>
    public string? Evaluate(string[] args)
    {
        if (args.Length == 1 && args[0].Contains('|'))
        {
            var options = ExpansionFunctions.ChoiceOptions(args);
            return options.Count == 0 ? null : options[Random.Shared.Next(options.Count)];
        }
        if (args.Length < 2) return null;
        if (!long.TryParse(args[0], out long min)) return null;
        if (!long.TryParse(args[1], out long max)) return null;
        if (min > max) return null;
        return Random.Shared.NextInt64(min, max + 1).ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class RepeatFunction : IExpansionFunction
{
    public string Name => "repeat";

    /// <summary>{repeat(раз, текст)}; запятые в тексте сохраняются.</summary>
    public string? Evaluate(string[] args)
    {
        if (args.Length < 2) return null;
        if (!int.TryParse(args[0], out int count)) return null;
        if (count <= 0 || count > 1000) return null;
        var text = string.Join(", ", args.Skip(1));
        var sb = new StringBuilder(text.Length * count);
        for (int i = 0; i < count; i++)
            sb.Append(text);
        return sb.ToString();
    }
}

public sealed class CalcFunction : IExpansionFunction
{
    public string Name => "calc";

    /// <summary>{calc(выражение)} — через вычислитель калькулятора (функции, степени, константы).</summary>
    public string? Evaluate(string[] args)
    {
        if (args.Length == 0) return null;
        // запятые внутри скобок — аргументы функций калькулятора: {calc(max(1,2))}
        var expression = string.Join(",", args);
        return MathEvaluator.TryEvaluate(expression, out double value, out _) ? MathEvaluator.Format(value) : null;
    }
}

/// <summary>{date}, {date(dd.MM.yyyy)}, {date(+1d, dd.MM)}, {time(+30m)}, {date(-1M+2d)}.</summary>
public static class DateTimeFunction
{
    private static readonly Regex OffsetRx = new(@"^(?:\s*[+-]\s*\d+\s*[smhdwMy])+\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex OffsetPart = new(@"([+-])\s*(\d+)\s*([smhdwMy])", RegexOptions.CultureInvariant);

    public static string? Evaluate(IReadOnlyList<string> args, string defaultFormat)
    {
        var now = DateTime.Now;
        var formatParts = new List<string>();
        foreach (var arg in args)
        {
            if (arg.Length == 0) continue;
            if (OffsetRx.IsMatch(arg))
            {
                if (!TryApplyOffsets(arg, ref now)) return null;
            }
            else formatParts.Add(arg);
        }
        // формат с запятой без кавычек разрезан на аргументы — склеиваем обратно
        var format = formatParts.Count > 0 ? string.Join(", ", formatParts) : defaultFormat;
        try { return now.ToString(format, CultureInfo.CurrentCulture); }
        catch (FormatException) { return now.ToString(defaultFormat, CultureInfo.CurrentCulture); }
    }

    public static bool TryApplyOffsets(string spec, ref DateTime value)
    {
        try
        {
            foreach (Match m in OffsetPart.Matches(spec))
            {
                int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (m.Groups[1].Value == "-") n = -n;
                value = m.Groups[3].Value[0] switch
                {
                    's' => value.AddSeconds(n),
                    'm' => value.AddMinutes(n),
                    'h' => value.AddHours(n),
                    'd' => value.AddDays(n),
                    'w' => value.AddDays(7.0 * n),
                    'M' => value.AddMonths(n),
                    _ => value.AddYears(n) // 'y'
                };
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException) { return false; }
    }
}
