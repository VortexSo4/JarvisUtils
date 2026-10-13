using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JarvisUtils.Modules.TextExpander;

// ─── Результат вычисления: последовательность текста и нажатий ─────────

public abstract class OutputSegment;
public sealed class TextSegment(string text) : OutputSegment { public string Text { get; set; } = text; }
public sealed class KeySegment(KeyChord chord) : OutputSegment { public KeyChord Chord { get; } = chord; }
public sealed class CursorSegment : OutputSegment;

public sealed class ExpansionOutput
{
    public List<OutputSegment> Segments { get; } = [];
    public bool Cancelled { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Только текст — известна точная длина вставки (для отмены Backspace'ом).</summary>
    public bool IsPlain => Segments.All(s => s is TextSegment);

    public string Text => string.Concat(Segments.OfType<TextSegment>().Select(t => t.Text));

    /// <summary>Сколько раз нажать ← после вставки, чтобы каретка встала на {cursor}.</summary>
    public int CursorBackCount
    {
        get
        {
            int index = Segments.FindIndex(s => s is CursorSegment);
            if (index < 0) return 0;
            return Segments.Skip(index + 1).OfType<TextSegment>().Sum(t => CaretUnits(t.Text));
        }
    }

    /// <summary>
    /// Число «шагов каретки»: \r\n — один шаг, суррогатная пара/эмодзи — один (графемы .NET).
    /// По столько же нажатий ←/Backspace нужно, чтобы пройти этот текст.
    /// </summary>
    public static int CaretUnits(string text) => text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>Человекочитаемый вид для окна «Проверка».</summary>
    public string Describe() => string.Concat(Segments.Select(s => s switch
    {
        TextSegment t => t.Text,
        KeySegment k => "[" + k.Chord.Display + "]",
        CursorSegment => "|",
        _ => ""
    }));

    internal void AddText(string text)
    {
        if (text.Length == 0) return;
        if (Segments.Count > 0 && Segments[^1] is TextSegment last) last.Text += text;
        else Segments.Add(new TextSegment(text));
    }

    /// <summary>Перенос регистра набранного триггера на результат (CaseAware).</summary>
    public void ApplyCase(CaseStyle style)
    {
        if (style == CaseStyle.None) return;
        var culture = CultureInfo.CurrentCulture;
        foreach (var t in Segments.OfType<TextSegment>())
        {
            if (style == CaseStyle.Upper) { t.Text = t.Text.ToUpper(culture); continue; }
            for (int i = 0; i < t.Text.Length; i++)
            {
                if (!char.IsLetter(t.Text[i])) continue;
                t.Text = t.Text[..i] + char.ToUpper(t.Text[i], culture) + t.Text[(i + 1)..];
                return; // Capitalize — только первая буква всего результата
            }
        }
    }
}

/// <summary>Вычисление разобранного шаблона замены.</summary>
public static class TemplateEngine
{
    public static async Task<ExpansionOutput> EvaluateAsync(string template, ExpansionContext ctx)
    {
        var nodes = TemplateParser.Parse(template);
        var output = new ExpansionOutput { Errors = ctx.Errors };
        try
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case CursorNode:
                        output.Segments.Add(new CursorSegment());
                        break;
                    case KeyNode key:
                    {
                        var chord = await ParseKeyAsync(key, ctx);
                        if (chord != null) output.Segments.Add(new KeySegment(chord));
                        else output.AddText(SubstituteRefs(key.Raw, ctx));
                        break;
                    }
                    default:
                        output.AddText(await EvalNodeAsync(node, ctx));
                        break;
                }
            }
        }
        catch (ExpansionCancelledException)
        {
            return new ExpansionOutput { Cancelled = true, Errors = ctx.Errors };
        }
        return output;
    }

    private static async Task<KeyChord?> ParseKeyAsync(KeyNode key, ExpansionContext ctx)
    {
        var spec = await EvalSequenceAsync(key.Args[0], ctx);
        var count = key.Args.Count > 1 ? await EvalSequenceAsync(key.Args[1], ctx) : null;
        var chord = KeyChord.Parse(spec, count);
        if (chord == null) ctx.Errors.Add($"неизвестная клавиша: {spec}");
        return chord;
    }

    private static async ValueTask<string> EvalSequenceAsync(IReadOnlyList<TemplateNode> nodes, ExpansionContext ctx)
    {
        if (nodes.Count == 1) return await EvalNodeAsync(nodes[0], ctx);
        var sb = new StringBuilder();
        foreach (var node in nodes) sb.Append(await EvalNodeAsync(node, ctx));
        return sb.ToString();
    }

    private static async ValueTask<string> EvalNodeAsync(TemplateNode node, ExpansionContext ctx)
    {
        switch (node)
        {
            case TextNode t:
                return t.Text;
            case RefNode r:
                return ctx.Resolve(r.Key) ?? r.Raw;
            case CallNode call:
                return await EvalCallAsync(call, ctx);
            case ScriptNode script:
                return await EvalScriptAsync(script, ctx);
            default:
                return ""; // {cursor}/{key} внутри аргументов функций смысла не имеют
        }
    }

    private static async ValueTask<string> EvalCallAsync(CallNode call, ExpansionContext ctx)
    {
        // Неизвестный токен остаётся как есть — как и раньше, текст в {фигурных скобках} не ломается
        if (!ExpansionFunctions.TryGet(call.Name, out var function))
            return SubstituteRefs(call.Raw, ctx);

        var args = new string[call.Args.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = await EvalSequenceAsync(call.Args[i], ctx);

        string? result;
        try { result = await function(ctx, args); }
        catch (ExpansionCancelledException) { throw; }
        catch (Exception ex)
        {
            ctx.Errors.Add($"{call.Name}: {ex.Message}");
            return SubstituteRefs(call.Raw, ctx);
        }

        if (result != null) return result;
        ctx.Errors.Add($"{call.Name}: не удалось вычислить ({string.Join(", ", args)})");
        return SubstituteRefs(call.Raw, ctx);
    }

    private static async ValueTask<string> EvalScriptAsync(ScriptNode script, ExpansionContext ctx)
    {
        // %1 подставляется в код текстом: {= %1 * 2} при %1 = 21 → «21 * 2»
        var code = SubstituteRefs(script.Code, ctx);
        var clipboard = ctx.Services?.Clipboard;
        var globals = new ExpanderScriptGlobals(ctx.Args, ctx.Named, clipboard == null ? null : () => clipboard.GetText() ?? "");
        var (ok, result) = await ScriptRunner.EvaluateAsync(code, globals);
        if (ok) return result;
        ctx.Errors.Add("выражение: " + result);
        return SubstituteRefs(script.Raw, ctx);
    }

    /// <summary>Подстановка %1/%имя в сырой текст (для оставляемых как есть токенов и кода выражений).</summary>
    public static string SubstituteRefs(string raw, ExpansionContext ctx)
    {
        if (raw.IndexOf('%') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '%' && i + 1 < raw.Length)
            {
                char n = raw[i + 1];
                if (n is >= '1' and <= '9' && ctx.Resolve(n.ToString()) is { } value)
                {
                    sb.Append(value);
                    i++;
                    continue;
                }
                if (char.IsLetter(n) || n == '_')
                {
                    int end = i + 1;
                    while (end < raw.Length && (char.IsLetterOrDigit(raw[end]) || raw[end] == '_')) end++;
                    if (ctx.Resolve(raw[(i + 1)..end]) is { } named)
                    {
                        sb.Append(named);
                        i = end - 1;
                        continue;
                    }
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
