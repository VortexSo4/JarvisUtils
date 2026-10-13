using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Простая синхронная функция для синтаксиса {имя(арг1, арг2, ...)}.
/// null — «не удалось вычислить»: токен останется в тексте как есть.
/// Регистрация: ExpansionFunctions.Register(new MyFunction()).
/// </summary>
public interface IExpansionFunction
{
    string Name { get; }
    string? Evaluate(string[] args);
}

/// <summary>Полная форма функции: доступ к контексту (сервисы, dry run, UI) и асинхронность.</summary>
public delegate ValueTask<string?> ExpansionFunc(ExpansionContext ctx, IReadOnlyList<string> args);

/// <summary>Интерактивные части шаблона ({choice}, {input}). Реализует модуль; null — пользователь отменил.</summary>
public interface IExpansionUi
{
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> options);
    Task<string?> PromptAsync(string label, string? initial);
}

/// <summary>Пользователь отменил интерактивный шаг — вся подстановка отменяется.</summary>
public sealed class ExpansionCancelledException() : Exception("подстановка отменена");

/// <summary>Контекст одного вычисления шаблона.</summary>
public sealed class ExpansionContext
{
    private static readonly IReadOnlyDictionary<string, string> NoNames = new Dictionary<string, string>();

    /// <summary>%1..%N по порядку.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>%имя.</summary>
    public IReadOnlyDictionary<string, string> Named { get; init; } = NoNames;

    /// <summary>Проверка в редакторе: ничего не отправляется, интерактив и счётчики не трогаются.</summary>
    public bool DryRun { get; init; }

    public IJarvisServices? Services { get; init; }
    public IExpansionUi? Ui { get; init; }
    public CounterStore? Counters { get; init; }

    /// <summary>Подпись для попапов (описание правила или триггер).</summary>
    public string Title { get; init; } = "";

    /// <summary>Показывался ли интерактив — тогда перед вставкой нужно вернуть фокус исходному окну.</summary>
    public bool UsedInteractiveUi { get; set; }

    /// <summary>Нечитаемые/упавшие токены (для тоста и окна проверки).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Значение %1 / %имя или null, если такого нет.</summary>
    public string? Resolve(string key)
    {
        if (key.Length == 1 && key[0] is >= '1' and <= '9')
        {
            int i = key[0] - '1';
            return i < Args.Count ? Args[i] : null;
        }
        return Named.TryGetValue(key, out var v) ? v : null;
    }
}
