using System.Collections.Generic;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Calculator;

public sealed class CalculatorSettings
{
    public string Hotkey { get; set; } = "Alt+OemPlus";
}

/// <summary>
/// Калькулятор в духе PowerToys Run. Alt+= открывает строку ввода у курсора;
/// выражение начинается с «=», ответ считается на лету и показывается под строкой.
/// Клик по ответу или Enter копирует результат в буфер обмена.
/// </summary>
public sealed class CalculatorModule : JarvisModuleBase, IHotkeyConfigurable
{
    private CalculatorSettings _settings = new();
    private CalculatorWindow? _window;

    public override string DisplayName => "Калькулятор";

    protected override void RegisterHotkeys()
    {
        _settings = LoadSettings<CalculatorSettings>();
        RegisterHotkey(_settings.Hotkey, Toggle);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Открыть калькулятор", () => _settings.Hotkey, ApplyHotkey)
    ];

    private void ApplyHotkey(string gesture)
    {
        _settings.Hotkey = gesture;
        SaveSettings(_settings);
        RestartHotkeys();
    }

    private void Toggle()
    {
        if (_window != null)
        {
            _window.CloseAnimated(); // повторный Alt+= закрывает строку
            return;
        }
        _window = new CalculatorWindow(Services);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    protected override void OnDisabled() => _window?.Close();

    public override void Shutdown()
    {
        _window?.Close();
        base.Shutdown();
    }
}