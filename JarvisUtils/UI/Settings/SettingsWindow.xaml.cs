using System;
using System.Windows;
using System.Windows.Controls;
using JarvisUtils.Core;
using JarvisUtils.UI.Theme;

namespace JarvisUtils.UI.Settings;

public partial class SettingsWindow : Window
{
    private readonly ModuleRunner _runner;

    public SettingsWindow(ModuleRunner runner)
    {
        InitializeComponent();
        _runner = runner;

        foreach (var module in runner.Modules)
            ModulesList.Items.Add(module.DisplayName);
        
        ModulesList.SelectionChanged += ModulesList_SelectionChanged;

        // пока окно настроек активно — модули и хоткеи приостановлены:
        // проверять бинды нужно, переключившись в другое приложение
        Activated += (_, _) => _runner.SuspendAll();
        Deactivated += (_, _) => _runner.ResumeAll();
        Closed += (_, _) => _runner.ResumeAll();

        if (ModulesList.Items.Count > 0) ModulesList.SelectedIndex = 0;
    }

    private void ModulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ModulesList.SelectedIndex;
        EditorHost.Children.Clear();
        if (index < 0 || index >= _runner.Modules.Count) return;
        BuildEditor(_runner.Modules[index]);
    }

    private void BuildEditor(IJarvisModule module)
    {
        // ── Шапка: название + тумблер ──
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var toggleLabel = new TextBlock
        {
            Text = module.IsEnabled ? "включён" : "выключен",
            Foreground = JarvisTheme.ForegroundBrush(0.5),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var toggle = new JarvisToggleSwitch // кодовый тумблер: начальное состояние без анимации
        {
            IsChecked = module.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += (_, _) => { module.SetEnabled(true); toggleLabel.Text = "включён"; };
        toggle.Unchecked += (_, _) => { module.SetEnabled(false); toggleLabel.Text = "выключен"; };

        var switchPanel = new StackPanel { Orientation = Orientation.Horizontal };
        switchPanel.Children.Add(toggle);
        switchPanel.Children.Add(toggleLabel);
        DockPanel.SetDock(switchPanel, Dock.Right);
        header.Children.Add(switchPanel);

        header.Children.Add(new TextBlock
        {
            Text = module.DisplayName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        EditorHost.Children.Add(header);

        if (module is IHotkeyConfigurable configurable)
        {
            EditorHost.Children.Add(Section("ГОРЯЧИЕ КЛАВИШИ"));
            foreach (var slot in configurable.HotkeySlots)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                row.Children.Add(new TextBlock
                {
                    Text = slot.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 340,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = JarvisTheme.ForegroundBrush(0.85)
                });
                var box = new HotkeyCaptureBox(slot.Gesture) { Width = 190, VerticalAlignment = VerticalAlignment.Center };
                box.GestureChanged += g => OnGestureChanged(module, slot, box, g);
                box.SetFeedback(null); // сразу подсветить «без модификаторов», если так
                row.Children.Add(box);
                EditorHost.Children.Add(row);
            }
        }

        if (module is IModuleSettingsEditor editor)
            EditorHost.Children.Add(editor.BuildEditor());
        else if (module is not IHotkeyConfigurable)
            EditorHost.Children.Add(new TextBlock
            { Text = "У модуля нет настроек.", Foreground = JarvisTheme.ForegroundBrush(0.5) });
    }

    private void OnGestureChanged(IJarvisModule module, HotkeySlot slot, HotkeyCaptureBox box, string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            slot.Apply(gesture!); // снять привязку — валидное состояние
            box.SetFeedback(null);
            return;
        }

        // конфликт внутри приложения: жест уже стоит другому слоту (другого модуля или этому же)
        var owner = FindGestureOwner(gesture, slot);
        if (owner != null)
        {
            box.SetFeedback($"Комбинация уже занята модулем «{owner.DisplayName}»");
            box.RevertTo(slot.Gesture); // не применяем — показываем прежний бинд
            return;
        }

        slot.Apply(gesture);

        // системная регистрация могла провалиться — комбинация занята другим приложением
        if (module.IsEnabled && module is JarvisModuleBase { HotkeysRegistered: false })
            box.SetFeedback("Не зарегистрировано: комбинация занята другим приложением");
        else
            box.SetFeedback(null);
    }

    private IJarvisModule? FindGestureOwner(string gesture, HotkeySlot except)
    {
        foreach (var m in _runner.Modules)
        {
            if (m is not IHotkeyConfigurable hc) continue;
            foreach (var s in hc.HotkeySlots)
            {
                if (ReferenceEquals(s, except)) continue;
                if (string.Equals(s.Gesture, gesture, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
        }
        return null;
    }

    private static TextBlock Section(string title) => new()
    {
        Text = title,
        Foreground = JarvisTheme.ForegroundBrush(0.42),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 22, 0, 8)
    };
}