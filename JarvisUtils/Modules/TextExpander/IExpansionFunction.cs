using System;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Функция, которую можно вызывать внутри замены через синтаксис {имя(арг1, арг2, ...)}.
/// </summary>
public interface IExpansionFunction
{
    string Name { get; }
    string? Evaluate(string[] args);
}