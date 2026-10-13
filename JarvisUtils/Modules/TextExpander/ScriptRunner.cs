using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using JarvisUtils.Modules.Calculator;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>Глобальные переменные выражения {= …}: Args[0] — это %1, Named["имя"] — %имя.</summary>
public sealed class ExpanderScriptGlobals
{
    private readonly Func<string>? _clip;
    private string? _clipValue;

    public ExpanderScriptGlobals(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> named, Func<string>? clip)
    {
        Args = args.ToArray();
        Named = named;
        _clip = clip;
    }

    public string[] Args { get; }
    public IReadOnlyDictionary<string, string> Named { get; }
    public DateTime Now => DateTime.Now;

    /// <summary>Текст буфера обмена — читается только если выражение к нему обращается.</summary>
    public string Clip => _clipValue ??= _clip?.Invoke() ?? "";
}

/// <summary>
/// Выражения C# через Roslyn. Компиляция дорогая (первая — секунды: загрузка Roslyn),
/// поэтому делегаты кешируются по тексту кода, а всё выполняется в пуле потоков с таймаутом.
/// </summary>
public static class ScriptRunner
{
    private const int CacheLimit = 64;
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(3);

    private static readonly ConcurrentDictionary<string, Lazy<Task<ScriptRunner<object>>>> Cache = new();

    private static readonly Lazy<ScriptOptions> Options = new(() => ScriptOptions.Default
        .WithReferences(typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(ExpanderScriptGlobals).Assembly)
        .WithImports("System", "System.Linq", "System.Text", "System.Globalization", "System.Collections.Generic", "System.Math"));

    /// <summary>Прогрев Roslyn в фоне, чтобы первое срабатывание {= …} не ждало секунды.</summary>
    public static void Warmup() => _ = EvaluateAsync("1", new ExpanderScriptGlobals([], new Dictionary<string, string>(), null));

    public static async Task<(bool Ok, string Result)> EvaluateAsync(string code, ExpanderScriptGlobals globals)
    {
        try
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            var compile = Cache.GetOrAdd(code, c => new Lazy<Task<ScriptRunner<object>>>(() => Task.Run(() => Compile(c)))).Value;
            var runner = await compile.WaitAsync(CompileTimeout).ConfigureAwait(false);

            // Бесконечный цикл в выражении прервать нельзя (скрипт не проверяет токен отмены):
            // по таймауту просто перестаём ждать — поток пула останется занятым.
            var result = await Task.Run(() => runner(globals)).WaitAsync(RunTimeout).ConfigureAwait(false);
            return (true, Format(result));
        }
        catch (CompilationErrorException ex)
        {
            return (false, ex.Diagnostics.FirstOrDefault()?.GetMessage(CultureInfo.CurrentUICulture) ?? ex.Message);
        }
        catch (TimeoutException)
        {
            return (false, "превышено время выполнения");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private static ScriptRunner<object> Compile(string code)
    {
        var script = CSharpScript.Create<object>(code, Options.Value, typeof(ExpanderScriptGlobals));
        var diagnostics = script.Compile();
        if (diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            throw new CompilationErrorException(diagnostics.First(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).GetMessage(CultureInfo.CurrentUICulture), diagnostics);
        return script.CreateDelegate();
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        double d => MathEvaluator.Format(d),
        float f => MathEvaluator.Format(f),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? ""
    };
}
