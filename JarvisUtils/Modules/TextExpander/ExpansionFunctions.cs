using System;
using System.Data;
using System.Text;

namespace JarvisUtils.Modules.TextExpander;

public sealed class IncrFunction : IExpansionFunction
{
    public string Name => "incr";

    public string? Evaluate(string[] args)
    {
        if (args.Length < 2) return null;
        if (!int.TryParse(args[0], out int from)) return null;
        if (!int.TryParse(args[1], out int to)) return null;
        bool newline = args.Length >= 3 && args[2].Trim().Equals("n", StringComparison.OrdinalIgnoreCase);

        if (from > to) return null;
        var sb = new StringBuilder();
        for (int i = from; i <= to; i++)
        {
            if (i > from) sb.Append(newline ? "\n" : " ");
            sb.Append(i);
        }
        return sb.ToString();
    }
}

public sealed class RandFunction : IExpansionFunction
{
    private static readonly Random Rng = new();

    public string Name => "rand";

    public string? Evaluate(string[] args)
    {
        if (args.Length < 2) return null;
        if (!int.TryParse(args[0], out int min)) return null;
        if (!int.TryParse(args[1], out int max)) return null;
        if (min > max) return null;
        return Rng.Next(min, max + 1).ToString();
    }
}

public sealed class DateFunction : IExpansionFunction
{
    public string Name => "date";

    public string? Evaluate(string[] args)
    {
        string format = args.Length > 0 ? args[0] : "dd.MM.yyyy";
        try
        {
            return DateTime.Now.ToString(format);
        }
        catch
        {
            return DateTime.Now.ToString("dd.MM.yyyy");
        }
    }
}

public sealed class TimeFunction : IExpansionFunction
{
    public string Name => "time";

    public string? Evaluate(string[] args)
    {
        string format = args.Length > 0 ? args[0] : "HH:mm";
        try
        {
            return DateTime.Now.ToString(format);
        }
        catch
        {
            return DateTime.Now.ToString("HH:mm");
        }
    }
}

public sealed class RepeatFunction : IExpansionFunction
{
    public string Name => "repeat";

    public string? Evaluate(string[] args)
    {
        if (args.Length < 2) return null;
        if (!int.TryParse(args[0], out int count)) return null;
        if (count <= 0 || count > 1000) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
            sb.Append(args[1]);
        return sb.ToString();
    }
}

public sealed class CalcFunction : IExpansionFunction
{
    public string Name => "calc";

    public string? Evaluate(string[] args)
    {
        if (args.Length == 0) return null;
        var expression = string.Join(",", args);
        try
        {
            var result = new DataTable().Compute(expression, null);
            return result?.ToString();
        }
        catch
        {
            return null;
        }
    }
}