using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JarvisUtils.Modules.CaseConverter;

public sealed record CaseVariant(string Name, Func<string, string> Convert);

public static class CaseConversions
{
    public static readonly IReadOnlyList<CaseVariant> All =
    [
        new("camelCase", s => { var w = SplitWords(s); return w.Count == 0 ? s : w[0].ToLowerInvariant() + string.Concat(w.Skip(1).Select(Cap)); }),
        new("PascalCase", s => string.Concat(SplitWords(s).Select(Cap))),
        new("snake_case", s => string.Join("_", SplitWords(s).Select(x => x.ToLowerInvariant()))),
        new("CONSTANT_CASE", s => string.Join("_", SplitWords(s).Select(x => x.ToUpperInvariant()))),
        new("kebab-case", s => string.Join("-", SplitWords(s).Select(x => x.ToLowerInvariant()))),
        new("dot.case", s => string.Join(".", SplitWords(s).Select(x => x.ToLowerInvariant()))),
        new("Title Case", s => string.Join(" ", SplitWords(s).Select(Cap))),
        new("Sentence case", s => { var w = SplitWords(s); return w.Count == 0 ? s : Cap(w[0]) + (w.Count > 1 ? " " + string.Join(" ", w.Skip(1).Select(x => x.ToLowerInvariant())) : ""); }),
        new("lower case", s => s.ToLowerInvariant()),
        new("UPPER CASE", s => s.ToUpperInvariant())
    ];

    private static List<string> SplitWords(string input)
    {
        var spaced = Regex.Replace(input.Trim(), @"[\s_\-\./\\]+", " ");
        spaced = Regex.Replace(spaced, @"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})", " "); // camelCase → camel Case
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}