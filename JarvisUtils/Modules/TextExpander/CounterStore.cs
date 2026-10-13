using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Постоянные счётчики {counter(имя[,старт])}. Отдельный файл (TextExpander.counters.json),
/// а не поле в настройках правил: счётчик пишется с фонового потока, пока редактор
/// может менять список правил на UI-потоке.
/// </summary>
public sealed class CounterStore
{
    public const string SettingsName = "TextExpander.counters";

    public sealed class Data
    {
        public Dictionary<string, long> Counters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ISettingsStore? _store;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<string, long> _values;

    public CounterStore(ISettingsStore? store)
    {
        _store = store;
        var data = store?.Load<Data>(SettingsName) ?? new Data();
        _values = new Dictionary<string, long>(data.Counters ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Текущее значение и сдвиг на +1 (с сохранением в фоне).</summary>
    public long Next(string name, long start)
    {
        long value;
        lock (_gate)
        {
            value = _values.TryGetValue(name, out var v) ? v : start;
            _values[name] = value + 1;
        }
        if (_store != null) Task.Run(SaveLatest);
        return value;
    }

    // Снимок берётся в момент записи, а не в момент инкремента: записи из пула могут
    // выполниться в любом порядке, но последняя всегда содержит все предыдущие инкременты.
    // Файловый ввод-вывод — под отдельным локом, чтобы Next на UI-потоке не ждал диск.
    private void SaveLatest()
    {
        lock (_saveGate)
        {
            Dictionary<string, long> snapshot;
            lock (_gate) snapshot = new Dictionary<string, long>(_values, StringComparer.OrdinalIgnoreCase);
            _store!.Save(SettingsName, new Data { Counters = snapshot });
        }
    }

    /// <summary>Значение, которое выдаст следующий Next (для проверки в редакторе).</summary>
    public long Peek(string name, long start)
    {
        lock (_gate) return _values.TryGetValue(name, out var v) ? v : start;
    }
}
