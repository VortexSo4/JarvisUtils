using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace JarvisUtils.Modules.TextExpander;

// ─── AST шаблона замены ───────────────────────────────────────────────

public abstract class TemplateNode;

/// <summary>Литеральный текст. Quoted — из "строки" в аргументе (не обрезается по краям).</summary>
public sealed class TextNode(string text, bool quoted = false) : TemplateNode
{
    public string Text { get; } = text;
    public bool Quoted { get; } = quoted;
}

/// <summary>%1..%9 или %имя. Raw — исходный текст на случай, если такого значения нет.</summary>
public sealed class RefNode(string key, string raw) : TemplateNode
{
    public string Key { get; } = key;
    public string Raw { get; } = raw;
}

/// <summary>{имя}, {имя(арг, …)} или {имя:сырой аргумент}.</summary>
public sealed class CallNode(string name, IReadOnlyList<IReadOnlyList<TemplateNode>> args, string raw) : TemplateNode
{
    public string Name { get; } = name;
    public IReadOnlyList<IReadOnlyList<TemplateNode>> Args { get; } = args;
    public string Raw { get; } = raw;
}

/// <summary>{cursor} — сюда встанет каретка после вставки.</summary>
public sealed class CursorNode : TemplateNode;

/// <summary>{key:enter}, {key:ctrl+a*2}, {key(tab, 3)} — нажатие клавиши в этой точке.</summary>
public sealed class KeyNode(IReadOnlyList<IReadOnlyList<TemplateNode>> args, string raw) : TemplateNode
{
    public IReadOnlyList<IReadOnlyList<TemplateNode>> Args { get; } = args;
    public string Raw { get; } = raw;
}

/// <summary>{= выражение C#}.</summary>
public sealed class ScriptNode(string code, string raw) : TemplateNode
{
    public string Code { get; } = code;
    public string Raw { get; } = raw;
}

/// <summary>
/// Рекурсивный разбор шаблона замены (вместо прежнего регулярного выражения):
/// вложенные вызовы {upper({clip})}, строки в кавычках с запятыми и скобками,
/// экранирование \{ \} \% \, \( \) \" \|. Некорректная конструкция не ошибка —
/// она остаётся литеральным текстом, как и раньше неизвестные {токены}.
/// </summary>
public sealed class TemplateParser
{
    private const int CacheLimit = 256;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<TemplateNode>> Cache = new();

    private readonly string _s;
    private int _pos;

    private TemplateParser(string s) => _s = s;

    public static IReadOnlyList<TemplateNode> Parse(string template)
    {
        template ??= "";
        if (Cache.TryGetValue(template, out var cached)) return cached;
        if (Cache.Count >= CacheLimit) Cache.Clear();
        var parser = new TemplateParser(template);
        var nodes = parser.ParseSequence(inArgs: false);
        Cache[template] = nodes;
        return nodes;
    }

    // Обратный слэш перед прочими символами (и перед самим собой) остаётся как есть:
    // пути вида C:\Users и \\server в замене не ломаются.
    private static bool IsEscapable(char c) => c is '{' or '}' or '%' or ',' or '(' or ')' or '"' or '|';

    private List<TemplateNode> ParseSequence(bool inArgs)
    {
        var nodes = new List<TemplateNode>();
        var text = new StringBuilder();
        int depth = 0; // скобки внутри аргумента: {calc((1+2)*3)}, {calc(max(1,2))}

        void Flush()
        {
            if (text.Length == 0) return;
            nodes.Add(new TextNode(text.ToString()));
            text.Clear();
        }

        while (_pos < _s.Length)
        {
            char c = _s[_pos];

            if (inArgs)
            {
                if (depth == 0 && (c == ',' || c == ')')) break;
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '"' && TryReadQuoted(out var quoted))
                {
                    Flush();
                    nodes.Add(new TextNode(quoted, quoted: true));
                    continue;
                }
            }

            if (c == '\\' && _pos + 1 < _s.Length && IsEscapable(_s[_pos + 1]))
            {
                text.Append(_s[_pos + 1]);
                _pos += 2;
                continue;
            }

            if (c == '{')
            {
                int start = _pos;
                var token = TryParseToken();
                if (token != null) { Flush(); nodes.Add(token); continue; }
                _pos = start + 1;
                text.Append('{');
                continue;
            }

            if (c == '%' && _pos + 1 < _s.Length)
            {
                char n = _s[_pos + 1];
                if (n == '%') { text.Append('%'); _pos += 2; continue; }
                if (n is >= '1' and <= '9')
                {
                    Flush();
                    nodes.Add(new RefNode(n.ToString(), _s.Substring(_pos, 2)));
                    _pos += 2;
                    continue;
                }
                if (char.IsLetter(n) || n == '_')
                {
                    int end = _pos + 1;
                    while (end < _s.Length && (char.IsLetterOrDigit(_s[end]) || _s[end] == '_')) end++;
                    Flush();
                    nodes.Add(new RefNode(_s[(_pos + 1)..end], _s[_pos..end]));
                    _pos = end;
                    continue;
                }
            }

            text.Append(c);
            _pos++;
        }
        Flush();
        return nodes;
    }

    /// <summary>"строка" с экранированием \" и \\. Нет закрывающей кавычки — не строка.</summary>
    private bool TryReadQuoted(out string value)
    {
        var sb = new StringBuilder();
        for (int i = _pos + 1; i < _s.Length; i++)
        {
            char c = _s[i];
            if (c == '\\' && i + 1 < _s.Length && _s[i + 1] is '"' or '\\') { sb.Append(_s[++i]); continue; }
            if (c == '"') { _pos = i + 1; value = sb.ToString(); return true; }
            sb.Append(c);
        }
        value = "";
        return false;
    }

    /// <summary>Разбор {…} с позиции '{'. null — это не токен (позиция не важна, вызывающий её восстановит).</summary>
    private TemplateNode? TryParseToken()
    {
        int start = _pos;
        _pos++; // '{'

        if (_pos < _s.Length && _s[_pos] == '=')
        {
            int end = FindScriptEnd(_pos + 1);
            if (end < 0) return null;
            var code = _s[(_pos + 1)..end].Trim();
            _pos = end + 1;
            return code.Length == 0 ? null : new ScriptNode(code, _s[start.._pos]);
        }

        int nameStart = _pos;
        while (_pos < _s.Length && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;
        if (_pos == nameStart || char.IsDigit(_s[nameStart])) return null;
        var name = _s[nameStart.._pos];
        if (_pos >= _s.Length) return null;

        var args = new List<IReadOnlyList<TemplateNode>>();
        char c = _s[_pos];
        if (c == ':')
        {
            // {имя:сырой аргумент} — до '}', без вложенности (\} — литеральная скобка)
            var sb = new StringBuilder();
            int i = _pos + 1;
            for (; i < _s.Length && _s[i] != '}'; i++)
            {
                if (_s[i] == '\\' && i + 1 < _s.Length && _s[i + 1] == '}') { sb.Append('}'); i++; continue; }
                sb.Append(_s[i]);
            }
            if (i >= _s.Length) return null;
            args.Add([new TextNode(sb.ToString(), quoted: true)]);
            _pos = i;
        }
        else if (c == '(')
        {
            _pos++;
            while (true)
            {
                var arg = ParseSequence(inArgs: true);
                if (_pos >= _s.Length) return null; // нет ')'
                args.Add(TrimArg(arg));
                if (_s[_pos] == ',') { _pos++; continue; }
                _pos++; // ')'
                break;
            }
            while (_pos < _s.Length && _s[_pos] == ' ') _pos++;
            // {date()} — ноль аргументов, а не один пустой
            if (args.Count == 1 && args[0].Count == 0) args.Clear();
        }

        if (_pos >= _s.Length || _s[_pos] != '}') return null;
        _pos++;
        var raw = _s[start.._pos];

        if (name.Equals("cursor", StringComparison.OrdinalIgnoreCase) && args.Count == 0) return new CursorNode();
        if (name.Equals("key", StringComparison.OrdinalIgnoreCase) && args.Count > 0) return new KeyNode(args, raw);
        return new CallNode(name, args, raw);
    }

    /// <summary>Конец {= …}: парная '}' с учётом вложенных скобок и строковых/символьных литералов C#.</summary>
    private int FindScriptEnd(int from)
    {
        int depth = 0;
        for (int i = from; i < _s.Length; i++)
        {
            char c = _s[i];
            if (c == '"' || c == '\'')
            {
                bool verbatim = c == '"' && i > from && _s[i - 1] == '@';
                for (i++; i < _s.Length; i++)
                {
                    if (verbatim)
                    {
                        if (_s[i] == '"') { if (i + 1 < _s.Length && _s[i + 1] == '"') { i++; continue; } break; }
                    }
                    else
                    {
                        if (_s[i] == '\\') { i++; continue; }
                        if (_s[i] == c) break;
                    }
                }
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}')
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return -1;
    }

    /// <summary>Пробелы по краям аргумента не значимы (как раньше: TrimEntries), строки в кавычках — значимы.</summary>
    private static List<TemplateNode> TrimArg(List<TemplateNode> arg)
    {
        if (arg.Count > 0 && arg[0] is TextNode { Quoted: false } first)
            arg[0] = new TextNode(first.Text.TrimStart());
        if (arg.Count > 0 && arg[^1] is TextNode { Quoted: false } last)
            arg[^1] = new TextNode(last.Text.TrimEnd());
        arg.RemoveAll(n => n is TextNode { Quoted: false, Text.Length: 0 });
        return arg;
    }
}
