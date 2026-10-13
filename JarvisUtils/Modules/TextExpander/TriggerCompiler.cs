using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>Результат компиляции шаблона триггера в регулярное выражение, привязанное к концу буфера.</summary>
public sealed class CompiledTrigger
{
    public Regex? Regex { get; init; }
    public string? Error { get; init; }

    /// <summary>Индекс i → ключ группы для %(i+1); null — номер не использован (значение пустое).</summary>
    public IReadOnlyList<string?> NumberedGroups { get; init; } = [];

    /// <summary>%имя → ключ группы.</summary>
    public IReadOnlyDictionary<string, string> NamedGroups { get; init; } = new Dictionary<string, string>();

    /// <summary>Группы с «пользовательскими» значениями: их символы не участвуют в определении регистра.</summary>
    public IReadOnlySet<string> ValueGroups { get; init; } = new HashSet<string>();

    public bool IsValid => Regex != null;
}

/// <summary>
/// Шаблон триггера → Regex. Синтаксис:
///   {n} целое, {d} число с . или ,, {w} слово, {*} любой текст (лениво); имя — {n:count};
///   [..] необязательная часть (чисто литеральная получает свой номер %N: есть → её текст, нет → "");
///   %1..%9 — старый синтаксис (ленивое «что угодно»); \[ \] \{ \} \% — литеральные символы;
///   re:... — сырое .NET-регулярное выражение.
/// </summary>
public static class TriggerCompiler
{
    public const string RawPrefix = "re:";

    // Регулярки крутятся в коллбэке low-level хука: катастрофический backtracking
    // в пользовательском re: не должен подвесить ввод всей системы.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(25);

    private const string WordStartGuard = @"(?<![\p{L}\p{Nd}])";

    private abstract class Node;
    private sealed class Literal(string text) : Node { public string Text { get; } = text; }
    private sealed class Placeholder(char kind, string? name, int? explicitNumber) : Node
    {
        public char Kind { get; } = kind;
        public string? Name { get; } = name;
        public int? ExplicitNumber { get; } = explicitNumber;
        public int Number { get; set; }
    }
    private sealed class Optional(List<Node> children) : Node
    {
        public List<Node> Children { get; } = children;
        public int Number { get; set; } // 0 — не захватывается (внутри есть плейсхолдеры)
    }

    private static readonly Regex PlaceholderToken =
        new(@"\G\{([ndw*])(?::([\p{L}_][\p{L}\p{Nd}_]*))?\}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static CompiledTrigger Compile(string pattern, bool caseAware, bool wordStart)
    {
        if (string.IsNullOrEmpty(pattern)) return new CompiledTrigger { Error = "пустой триггер" };

        var options = RegexOptions.CultureInvariant | (caseAware ? RegexOptions.IgnoreCase : RegexOptions.None);
        string prefix = wordStart ? WordStartGuard : "";

        try
        {
            if (pattern.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
                return CompileRaw(pattern[RawPrefix.Length..], prefix, options);

            int pos = 0;
            var nodes = ParseSequence(pattern, ref pos, inOptional: false);
            AssignNumbers(nodes);

            var sb = new StringBuilder(prefix).Append("(?:");
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            var valueGroups = new HashSet<string>();
            int max = 0;
            Emit(nodes, sb, named, valueGroups, ref max);
            sb.Append(")$");

            var regex = new Regex(sb.ToString(), options, MatchTimeout);
            var numbered = new string?[max];
            foreach (var n in regex.GetGroupNames())
                if (n.Length > 1 && n[0] == 'p' && int.TryParse(n.AsSpan(1), out int k) && k >= 1 && k <= max)
                    numbered[k - 1] = n;

            return Validate(new CompiledTrigger { Regex = regex, NumberedGroups = numbered, NamedGroups = named, ValueGroups = valueGroups });
        }
        catch (ArgumentException ex)
        {
            return new CompiledTrigger { Error = "ошибка в регулярном выражении: " + ex.Message };
        }
    }

    private static CompiledTrigger CompileRaw(string body, string prefix, RegexOptions options)
    {
        if (body.Length == 0) return new CompiledTrigger { Error = "пустое регулярное выражение" };
        var regex = new Regex(prefix + "(?:" + body + ")$", options, MatchTimeout);

        // .NET нумерует сначала безымянные группы, затем именованные — %N идёт по этой нумерации
        var numbers = regex.GetGroupNumbers().Where(n => n > 0).OrderBy(n => n).ToArray();
        int max = numbers.Length == 0 ? 0 : numbers[^1];
        var numbered = new string?[max];
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var valueGroups = new HashSet<string>();
        foreach (var n in numbers)
        {
            var key = regex.GroupNameFromNumber(n);
            numbered[n - 1] = key;
            valueGroups.Add(key);
            if (!int.TryParse(key, out _)) named[key] = key;
        }
        return Validate(new CompiledTrigger { Regex = regex, NumberedGroups = numbered, NamedGroups = named, ValueGroups = valueGroups });
    }

    // Триггер, совпадающий с пустой строкой, срабатывал бы на КАЖДОМ нажатии (например,
    // «[x]» — теперь это необязательный «x»). Такие правила отключаем с понятной ошибкой.
    private static CompiledTrigger Validate(CompiledTrigger t)
    {
        try
        {
            if (t.Regex!.IsMatch(""))
                return new CompiledTrigger { Error = "триггер совпадает с пустой строкой (нужен хотя бы один обязательный символ; литеральные [ ] экранируйте: \\[ )" };
        }
        catch (RegexMatchTimeoutException) { return new CompiledTrigger { Error = "слишком сложное регулярное выражение" }; }
        return t;
    }

    // ── Разбор ──

    private static List<Node> ParseSequence(string s, ref int pos, bool inOptional)
    {
        var nodes = new List<Node>();
        var text = new StringBuilder();
        void Flush() { if (text.Length > 0) { nodes.Add(new Literal(text.ToString())); text.Clear(); } }

        while (pos < s.Length)
        {
            char c = s[pos];
            if (inOptional && c == ']') break;

            if (c == '\\' && pos + 1 < s.Length && IsEscapable(s[pos + 1])) { text.Append(s[pos + 1]); pos += 2; continue; }

            if (c == '%' && pos + 1 < s.Length && s[pos + 1] is >= '1' and <= '9')
            {
                Flush();
                nodes.Add(new Placeholder('%', null, s[pos + 1] - '0'));
                pos += 2;
                continue;
            }

            if (c == '{')
            {
                var m = PlaceholderToken.Match(s, pos);
                if (m.Success)
                {
                    Flush();
                    nodes.Add(new Placeholder(char.ToLowerInvariant(m.Groups[1].Value[0]), m.Groups[2].Success ? m.Groups[2].Value : null, null));
                    pos += m.Length;
                    continue;
                }
            }

            if (c == '[')
            {
                int close = FindClosingBracket(s, pos);
                if (close > pos + 1)
                {
                    Flush();
                    int inner = pos + 1;
                    var children = ParseSequence(s, ref inner, inOptional: true);
                    nodes.Add(new Optional(children));
                    pos = close + 1;
                    continue;
                }
            }

            text.Append(c);
            pos++;
        }
        Flush();
        return nodes;
    }

    // Обратный слэш экранирует только служебные символы: старые триггеры вида «\o/» не ломаются
    private static bool IsEscapable(char c) => c is '[' or ']' or '{' or '}' or '%';

    /// <summary>Индекс парной ']' с учётом вложенности и экранирования; -1 — нет пары.</summary>
    private static int FindClosingBracket(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && IsEscapable(s[i + 1])) { i++; continue; }
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i;
        }
        return -1;
    }

    private static void AssignNumbers(List<Node> nodes)
    {
        var explicitNumbers = new HashSet<int>();
        CollectExplicit(nodes, explicitNumbers);
        int next = 0;
        int Next()
        {
            do next++; while (explicitNumbers.Contains(next));
            return next;
        }

        void Walk(List<Node> list)
        {
            foreach (var node in list)
            {
                switch (node)
                {
                    case Placeholder p:
                        p.Number = p.ExplicitNumber ?? Next();
                        break;
                    case Optional o when !ContainsPlaceholder(o.Children):
                        o.Number = Next(); // [n] → отдельный %N: "n" или ""
                        break;
                    case Optional o:
                        Walk(o.Children);
                        break;
                }
            }
        }
        Walk(nodes);
    }

    private static void CollectExplicit(List<Node> nodes, HashSet<int> set)
    {
        foreach (var node in nodes)
        {
            if (node is Placeholder { ExplicitNumber: { } n }) set.Add(n);
            else if (node is Optional o) CollectExplicit(o.Children, set);
        }
    }

    private static bool ContainsPlaceholder(List<Node> nodes) =>
        nodes.Any(n => n is Placeholder || (n is Optional o && ContainsPlaceholder(o.Children)));

    private static void Emit(List<Node> nodes, StringBuilder sb, Dictionary<string, string> named, HashSet<string> valueGroups, ref int max)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Literal l:
                    sb.Append(Regex.Escape(l.Text));
                    break;
                case Placeholder p:
                {
                    string group = "p" + p.Number;
                    max = Math.Max(max, p.Number);
                    valueGroups.Add(group);
                    if (p.Name != null) named.TryAdd(p.Name, group);
                    sb.Append("(?<").Append(group).Append('>').Append(p.Kind switch
                    {
                        'n' => @"-?\d+",
                        'd' => @"-?\d+(?:[.,]\d+)?",
                        'w' => @"[\p{L}\p{Nd}_]+",
                        _ => ".+?" // {*} и старый %N
                    }).Append(')');
                    break;
                }
                case Optional o:
                    if (o.Number > 0)
                    {
                        max = Math.Max(max, o.Number);
                        sb.Append("(?<p").Append(o.Number).Append('>');
                    }
                    else sb.Append("(?:");
                    Emit(o.Children, sb, named, valueGroups, ref max);
                    sb.Append(")?");
                    break;
            }
        }
    }
}
