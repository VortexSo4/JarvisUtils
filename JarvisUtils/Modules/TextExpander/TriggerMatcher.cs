using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JarvisUtils.Modules.TextExpander;

public enum CaseStyle { None, Capitalize, Upper }

/// <summary>Сработавшее правило: что набрано, захваченные значения и стиль регистра.</summary>
public sealed class TriggerMatch
{
    public required ExpansionRule Rule { get; init; }
    public required string Value { get; init; }   // буквально набранный триггер
    public required int Index { get; init; }      // позиция в буфере
    public required IReadOnlyList<string> Args { get; init; }
    public required IReadOnlyDictionary<string, string> Named { get; init; }
    public CaseStyle Case { get; init; }
    public int Length => Value.Length;
}

/// <summary>
/// Поиск правила по концу буфера набранных символов. Вызывается из коллбэка клавиатурного
/// хука — только дешёвые операции: скомпилированные триггеры кешируются.
/// </summary>
public sealed class TriggerMatcher
{
    private const int CacheLimit = 512;

    // Кеш по ТЕКСТУ шаблона и флагам, а не по объекту правила: редактор меняет
    // TriggerPattern у того же объекта, и кеш по правилу отдавал старый regex.
    private readonly ConcurrentDictionary<(string Pattern, bool CaseAware, bool WordStart), CompiledTrigger> _cache = new();

    public CompiledTrigger Get(ExpansionRule rule)
    {
        var key = (rule.TriggerPattern ?? "", rule.CaseAware, rule.WordStart);
        if (_cache.TryGetValue(key, out var compiled)) return compiled;
        // редактор порождает по записи на каждый промежуточный вариант набираемого шаблона
        if (_cache.Count >= CacheLimit) _cache.Clear();
        return _cache.GetOrAdd(key, k => TriggerCompiler.Compile(k.Pattern, k.CaseAware, k.WordStart));
    }

    /// <param name="processName">Ленивое имя процесса активного окна — нужно, только если у правил есть фильтр приложений.</param>
    /// <param name="ignoreAppFilter">Проверка в редакторе: приложение неизвестно, фильтры не применяем.</param>
    public TriggerMatch? Match(IEnumerable<ExpansionRule> rules, string buffer, Func<string?>? processName = null, bool ignoreAppFilter = false)
    {
        if (buffer.Length == 0) return null;
        string? process = null;
        bool processResolved = false;

        // Порядок как раньше: длинные шаблоны первыми (":inc%1-%2n:" раньше ":inc%1-%2:")
        foreach (var rule in rules.OrderByDescending(r => r.TriggerPattern?.Length ?? 0))
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.TriggerPattern)) continue;

            var compiled = Get(rule);
            if (compiled.Regex == null) continue;

            if (!ignoreAppFilter && HasAppFilter(rule))
            {
                if (!processResolved) { process = processName?.Invoke(); processResolved = true; }
                if (!AppAllowed(rule, process)) continue;
            }

            Match m;
            try { m = compiled.Regex.Match(buffer); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success || m.Length == 0) continue;

            return Build(rule, compiled, m);
        }
        return null;
    }

    private static TriggerMatch Build(ExpansionRule rule, CompiledTrigger compiled, Match m)
    {
        var args = new string[compiled.NumberedGroups.Count];
        for (int i = 0; i < args.Length; i++)
        {
            var key = compiled.NumberedGroups[i];
            args[i] = key != null && m.Groups[key].Success ? m.Groups[key].Value : "";
        }
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, key) in compiled.NamedGroups)
            named[name] = m.Groups[key].Success ? m.Groups[key].Value : "";

        return new TriggerMatch
        {
            Rule = rule,
            Value = m.Value,
            Index = m.Index,
            Args = args,
            Named = named,
            Case = rule.CaseAware ? DetectCase(m, compiled) : CaseStyle.None
        };
    }

    /// <summary>
    /// Стиль регистра по ЛИТЕРАЛЬНЫМ буквам набранного триггера (значения плейсхолдеров не в счёт):
    /// все заглавные (≥2 букв) → Upper, первая заглавная → Capitalize, иначе — как в шаблоне.
    /// </summary>
    private static CaseStyle DetectCase(Match m, CompiledTrigger compiled)
    {
        var excluded = new bool[m.Length];
        foreach (var key in compiled.ValueGroups)
        {
            var g = m.Groups[key];
            foreach (Capture c in g.Captures)
                for (int i = c.Index; i < c.Index + c.Length; i++)
                    if (i - m.Index is var rel && rel >= 0 && rel < excluded.Length) excluded[rel] = true;
        }

        int letters = 0, upper = 0;
        bool firstUpper = false;
        for (int i = 0; i < m.Length; i++)
        {
            if (excluded[i]) continue;
            char c = m.Value[i];
            if (!char.IsLetter(c)) continue;
            bool isUpper = char.IsUpper(c);
            if (letters == 0) firstUpper = isUpper;
            letters++;
            if (isUpper) upper++;
        }
        if (letters == 0 || upper == 0) return CaseStyle.None;
        if (letters >= 2 && upper == letters) return CaseStyle.Upper;
        if (firstUpper && upper == 1) return CaseStyle.Capitalize;
        return CaseStyle.None;
    }

    public static bool HasAppFilter(ExpansionRule rule) =>
        rule.Apps?.Any(a => !string.IsNullOrWhiteSpace(a)) == true ||
        rule.ExcludeApps?.Any(a => !string.IsNullOrWhiteSpace(a)) == true;

    public static bool AppAllowed(ExpansionRule rule, string? process)
    {
        var name = NormalizeApp(process);
        var include = rule.Apps?.Select(NormalizeApp).Where(a => a.Length > 0).ToList();
        if (include is { Count: > 0 } && !include.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        var exclude = rule.ExcludeApps?.Select(NormalizeApp).Where(a => a.Length > 0);
        if (exclude != null && name.Length > 0 && exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static string NormalizeApp(string? app)
    {
        var s = (app ?? "").Trim();
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    }
}
