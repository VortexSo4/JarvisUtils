using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JarvisUtils.Core;

public abstract class JarvisModuleBase : IJarvisModule
{
    private sealed class CoreState
    {
        public HashSet<string> Disabled { get; set; } = [];
    }

    public IJarvisServices Services { get; private set; } = null!;
    
    private static readonly object SettingsIoGate = new();

    public virtual string Name => GetType().Name.Replace("Module", "");
    public virtual string DisplayName => Name;
    public bool IsEnabled { get; private set; }
    public bool Suspended { get; set; }

    /// <summary>Успешна ли последняя регистрация хоткеев (для обратной связи в настройках).</summary>
    public bool HotkeysRegistered { get; private set; } = true;

    public virtual void Initialize(IJarvisServices services)
    {
        Services = services;
        HotkeysRegistered = true;
        RegisterHotkeys();
        IsEnabled = true;
        OnEnabled();
        if (Services.Settings.Load<CoreState>("_core").Disabled.Contains(Name))
            SetEnabled(false);
        // пункт в трее "из коробки" — модуль не собирает меню вручную
        Services.TrayMenu.AddToggle(DisplayName, () => IsEnabled, v => SetEnabled(v));
    }

    protected abstract void RegisterHotkeys();

    public void SetEnabled(bool enabled)
    {
        if (Services == null || enabled == IsEnabled) return;
        if (enabled) { HotkeysRegistered = true; RegisterHotkeys(); IsEnabled = true; OnEnabled(); }
        else { Services.Hotkeys.UnregisterAll(this); IsEnabled = false; OnDisabled(); }

        // FIX: синхронная запись на диск из UI-потока (антивирус, просыпающийся
        // HDD) давала паузы — причём в момент, когда на потоке висят хуки.
        var name = Name;
        var enabledNow = enabled;
        var services = Services;
        Task.Run(() =>
        {
            try
            {
                lock (SettingsIoGate)
                {
                    var core = services.Settings.Load<CoreState>("_core");
                    if (enabledNow) core.Disabled.Remove(name); else core.Disabled.Add(name);
                    services.Settings.Save("_core", core);
                }
            }
            catch { }
        });
    }

    /// <summary>Перерегистрация хоткеев после смены в настройках.</summary>
    public void RestartHotkeys()
    {
        if (!IsEnabled) return;
        Services.Hotkeys.UnregisterAll(this);
        HotkeysRegistered = true;
        RegisterHotkeys();
    }

    protected virtual void OnEnabled() { }
    protected virtual void OnDisabled() { }

    public virtual void Shutdown()
    {
        if (IsEnabled) Services.Hotkeys.UnregisterAll(this);
    }

    protected T LoadSettings<T>() where T : class, new() => Services.Settings.Load<T>(Name);
    protected void SaveSettings<T>(T settings) where T : class => Services.Settings.Save(Name, settings);

    protected bool RegisterHotkey(string gesture, Action action)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return true; // «нет хоткея» — валидное состояние, не ошибка
        var ok = Services.Hotkeys.RegisterGesture(gesture, action, this);
        if (!ok) HotkeysRegistered = false; // UI покажет «комбинация не зарегистрирована»
        return ok;
    }
}