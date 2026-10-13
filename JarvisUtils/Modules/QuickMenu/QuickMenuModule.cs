using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JarvisUtils.Core;
using JarvisUtils.UI.Theme;

namespace JarvisUtils.Modules.QuickMenu;

public sealed class QuickMenuSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>Скрытые пункты: «Модуль/Название действия».</summary>
    public List<string> Hidden { get; set; } = [];
}

/// <summary>
/// Радиальное быстрое меню: хоткей — кольцо действий модулей у курсора. Фокус не
/// забирается, поэтому действия работают с тем окном, где пользователь был.
/// </summary>
public sealed class QuickMenuModule(Func<IReadOnlyList<IJarvisModule>> modules, Action openSettings)
    : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor
{
    private QuickMenuSettings _settings = new();
    private RadialMenuWindow? _window;

    public override string DisplayName => "Быстрое меню";
    public override string Icon => FluentIcons.Tiles;

    protected override void RegisterHotkeys()
    {
        _settings = LoadSettings<QuickMenuSettings>();
        RegisterHotkey(_settings.Hotkey, Toggle);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Открыть радиальное меню у курсора", () => _settings.Hotkey, g =>
        {
            _settings.Hotkey = g;
            SaveSettings(_settings);
            RestartHotkeys();
        })
    ];

    protected override void OnDisabled() => _window?.CloseNow();

    private void Toggle()
    {
        if (_window != null) { _window.CloseNow(); return; } // повторный хоткей — закрыть
        var items = VisibleActions().Select(x => x.Action).ToList();
        if (items.Count == 0)
        {
            Services.Overlay.ShowToast("Нет доступных действий — включите модули в настройках", DisplayName);
            return;
        }
        _window = new RadialMenuWindow(items, Services.Keyboard);
        _window.Closed += (_, _) => _window = null;
        _window.Picked += Run;
        _window.Show();
    }

    /// <summary>
    /// Действие — после того как кольцо исчезло с экрана: скриншот, пипетка и OCR
    /// иначе захватили бы само меню.
    /// </summary>
    private void Run(QuickAction action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action.Run(); }
            catch (Exception ex) { Log.Error($"быстрое меню «{action.Title}»: {ex}"); }
        };
        timer.Start();
    }

    private IEnumerable<(string Key, QuickAction Action)> AllActions()
    {
        foreach (var module in modules())
        {
            if (!module.IsEnabled || module is not IQuickActions provider) continue;
            foreach (var action in provider.QuickActions)
                yield return ($"{module.Name}/{action.Title}", action);
        }
        yield return ("App/Настройки", new QuickAction("Настройки", FluentIcons.Settings, openSettings));
    }

    private IEnumerable<(string Key, QuickAction Action)> VisibleActions() =>
        AllActions().Where(a => !_settings.Hidden.Contains(a.Key));

    public FrameworkElement BuildEditor()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Кольцо появляется у курсора и не забирает фокус. Направление мыши выбирает пункт, " +
                   "ЛКМ — выполнить, Esc, ПКМ или клик мимо — закрыть. Клавиши 1–9, 0, Q–P выбирают пункт сразу.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });
        panel.Children.Add(UI.Settings.SettingsUi.Section("ПУНКТЫ МЕНЮ"));
        foreach (var (key, action) in AllActions().ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var toggle = new JarvisToggleSwitch { IsChecked = !_settings.Hidden.Contains(key), VerticalAlignment = VerticalAlignment.Center };
            toggle.Checked += (_, _) => { _settings.Hidden.Remove(key); SaveSettings(_settings); };
            toggle.Unchecked += (_, _) => { if (!_settings.Hidden.Contains(key)) _settings.Hidden.Add(key); SaveSettings(_settings); };
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            var icon = FluentIcons.Glyph(action.Glyph, 15);
            icon.Width = 22;
            icon.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = action.Title, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
        }
        return panel;
    }
}
