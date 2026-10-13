using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Calculator;

public sealed class CalculatorSettings
{
    public string Hotkey { get; set; } = "Alt+OemPlus";

    /// <summary>Последние выражения (без «=»), свежие — первыми; без повторов.</summary>
    public List<string> History { get; set; } = [];
}

/// <summary>
/// Калькулятор в духе PowerToys Run. Alt+= открывает строку ввода;
/// выражение начинается с «=», ответ считается на лету и показывается под строкой.
/// Клик по ответу или Enter копирует результат, Shift+Enter вставляет его в прежнее окно.
/// Понимает единицы, валюты и системы счисления (см. CalcEngine).
/// </summary>
public sealed class CalculatorModule : JarvisModuleBase, IHotkeyConfigurable, IQuickActions
{
    private const int MaxHistory = 50;

    private CalculatorSettings _settings = new();
    private bool _settingsLoaded;
    private CalculatorWindow? _window;

    // запись настроек — в фоне (UI-поток держит хуки клавиатуры); версии отсекают устаревший
    // снимок, если две фоновые записи выполнились не в том порядке
    private readonly object _saveGate = new();
    private int _saveVersion;
    private int _writtenVersion;

    public override string DisplayName => "Калькулятор";
    public override string Icon => FluentIcons.Calculator;

    public IReadOnlyList<QuickAction> QuickActions =>
    [
        new("Калькулятор", FluentIcons.Calculator, Toggle)
    ];

    protected override void RegisterHotkeys()
    {
        // настройки — один раз: при перерегистрации хоткеев перечитка с диска могла бы
        // откатить историю, чья фоновая запись ещё не дошла до файла
        if (!_settingsLoaded)
        {
            _settings = LoadSettings<CalculatorSettings>();
            _settings.History ??= [];
            _settingsLoaded = true;
        }
        RegisterHotkey(_settings.Hotkey, Toggle);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Открыть калькулятор", () => _settings.Hotkey, ApplyHotkey)
    ];

    private void ApplyHotkey(string gesture)
    {
        _settings.Hotkey = gesture;
        SaveInBackground();
        RestartHotkeys();
    }

    private void Toggle()
    {
        if (_window != null)
        {
            _window.CloseAnimated(); // повторный Alt+= закрывает строку
            return;
        }
        // окно, в котором пользователь печатал до Alt+=: туда Shift+Enter вставит ответ
        IntPtr previous = WinApi.GetForegroundWindow();
        _window = new CalculatorWindow(Services, previous, _settings.History.ToArray(), AddToHistory);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    private void AddToHistory(string expression)
    {
        expression = expression.Trim();
        if (expression.Length == 0) return;
        var history = _settings.History;
        history.RemoveAll(h => string.Equals(h, expression, StringComparison.Ordinal));
        history.Insert(0, expression);
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        SaveInBackground();
    }

    private void SaveInBackground()
    {
        var snapshot = Snapshot();
        int version = ++_saveVersion;
        Task.Run(() =>
        {
            lock (_saveGate)
            {
                if (version <= _writtenVersion) return; // уже записан более свежий снимок
                _writtenVersion = version;
                SaveSettings(snapshot);
            }
        });
    }

    private CalculatorSettings Snapshot() => new() { Hotkey = _settings.Hotkey, History = [.. _settings.History] };

    protected override void OnDisabled() => _window?.Close();

    public override void Shutdown()
    {
        _window?.Close();
        // выход из приложения убивает фоновые задачи — недописанную историю сохраняем сразу
        lock (_saveGate)
        {
            if (_writtenVersion < _saveVersion)
            {
                _writtenVersion = _saveVersion;
                SaveSettings(Snapshot());
            }
        }
        base.Shutdown();
    }
}
