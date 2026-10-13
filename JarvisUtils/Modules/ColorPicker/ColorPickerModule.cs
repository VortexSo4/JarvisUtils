using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisUtils.Core;
using JarvisUtils.UI.Settings;

namespace JarvisUtils.Modules.ColorPicker;

public sealed class ColorPickerSettings
{
    // Ctrl+Alt+P — «Пипетка/Picker»: свободно у модулей и не пересекается с Win+Shift+C из PowerToys
    public string Hotkey { get; set; } = "Ctrl+Alt+P";
    public string Format { get; set; } = ColorFormats.Hex;
    public List<string> History { get; set; } = []; // #RRGGBB, свежие первыми
}

/// <summary>
/// Пипетка: хоткей → лупа у курсора, клик копирует цвет пикселя в выбранном формате.
/// Последние цвета видны в настройках с копированием в любом формате.
/// </summary>
public sealed class ColorPickerModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor, IQuickActions
{
    private const int MaxHistory = 20;

    private ColorPickerSettings _settings = null!;
    private ColorPickerSession? _session;

    public override string DisplayName => "Пипетка";
    public override string Icon => FluentIcons.Eyedropper;

    public IReadOnlyList<QuickAction> QuickActions =>
    [
        new("Пипетка", FluentIcons.Eyedropper, Toggle)
    ];

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<ColorPickerSettings>();
        _settings.History ??= [];
        RegisterHotkey(_settings.Hotkey, Toggle);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Взять цвет с экрана", () => _settings.Hotkey,
            g => { _settings.Hotkey = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    protected override void OnDisabled() => _session?.Cancel();

    public override void Shutdown()
    {
        _session?.Cancel();
        base.Shutdown();
    }

    private void Toggle()
    {
        if (_session != null)
        {
            _session.Cancel(); // повторный хоткей — отмена
            return;
        }
        var session = new ColorPickerSession(Services.Keyboard);
        _session = session;
        session.Finished += color =>
        {
            if (ReferenceEquals(_session, session)) _session = null;
            if (color is { } c) OnPicked(c);
        };
        session.Start();
    }

    private void OnPicked(Color color)
    {
        var text = ColorFormats.Format(color, _settings.Format);
        Copy(text);
        Services.Overlay.ShowToast($"{text} — скопировано", DisplayName, 1400);

        var hex = ColorFormats.ToHex(color);
        _settings.History.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
        _settings.History.Insert(0, hex);
        if (_settings.History.Count > MaxHistory) _settings.History.RemoveRange(MaxHistory, _settings.History.Count - MaxHistory);
        SaveInBackground();
    }

    /// <summary>Буфер — через clipboard-поток, но и его ожидание не держим на UI-потоке.</summary>
    private void Copy(string text) => Task.Run(() => Services.Clipboard.SetText(text));

    /// <summary>Диск — не на UI-потоке; сохраняем копию, чтобы следующий выбор не правил список во время сериализации.</summary>
    private void SaveInBackground()
    {
        var copy = new ColorPickerSettings { Hotkey = _settings.Hotkey, Format = _settings.Format, History = [.. _settings.History] };
        Task.Run(() => SaveSettings(copy));
    }

    // ── настройки ──

    public FrameworkElement BuildEditor()
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(new TextBlock
        {
            Text = "Лупа показывает пиксели под курсором. Клик или Enter — скопировать цвет, стрелки — сдвиг на 1 пиксель " +
                   "(с Shift — на 10), Esc или правый клик — отмена.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(SettingsUi.Section("ФОРМАТ КОПИРОВАНИЯ"));
        var format = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var f in ColorFormats.All) format.Items.Add(new ComboBoxItem { Content = ColorFormats.Label(f), Tag = f });
        format.SelectedIndex = Math.Max(0, Array.IndexOf(ColorFormats.All, _settings.Format));
        format.SelectionChanged += (_, _) =>
        {
            if (format.SelectedItem is not ComboBoxItem { Tag: string f }) return;
            _settings.Format = f;
            SaveInBackground();
        };
        root.Children.Add(format);

        root.Children.Add(SettingsUi.Section("ПОСЛЕДНИЕ ЦВЕТА"));
        var list = new StackPanel();
        root.Children.Add(list);
        RebuildHistory(list);
        return root;
    }

    private void RebuildHistory(StackPanel list)
    {
        list.Children.Clear();
        var colors = _settings.History
            .Select(h => ColorFormats.TryParseHex(h, out var c) ? c : (Color?)null)
            .Where(c => c.HasValue).Select(c => c!.Value).ToList();
        if (colors.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"Пока пусто. {_settings.Hotkey} — взять цвет с экрана.",
                Foreground = JarvisTheme.ForegroundBrush(0.45),
                FontSize = 12
            });
            return;
        }

        foreach (var color in colors) list.Children.Add(HistoryRow(color));

        var clear = new Button
        {
            Content = FluentIcons.WithLabel(FluentIcons.Delete, "Очистить"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        clear.Click += (_, _) =>
        {
            _settings.History.Clear();
            SaveInBackground();
            RebuildHistory(list);
        };
        list.Children.Add(clear);
    }

    /// <summary>Образец + три кнопки копирования (HEX / rgb / hsl) — значение формата прямо на кнопке.</summary>
    private FrameworkElement HistoryRow(Color color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = JarvisTheme.Solid(color),
            BorderBrush = JarvisTheme.Solid(Colors.White, 0.2),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = ColorFormats.ToHex(color)
        });
        foreach (var f in ColorFormats.All)
        {
            var value = ColorFormats.Format(color, f);
            var button = new Button
            {
                Content = value,
                FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                FontSize = 12,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                MinWidth = f == ColorFormats.Hex ? 84 : 150,
                ToolTip = "Скопировать",
                Cursor = Cursors.Hand
            };
            button.Click += (_, _) =>
            {
                Copy(value);
                Services.Overlay.ShowToast($"{value} — скопировано", DisplayName, 1200);
            };
            row.Children.Add(button);
        }
        return row;
    }
}
