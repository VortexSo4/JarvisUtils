using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JarvisUtils.Core;
using JarvisUtils.UI.Settings;
using JarvisUtils.UI.Theme;

namespace JarvisUtils.Modules.Widgets;

public sealed class WidgetsModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor
{
    private WidgetsSettings _settings = null!;
    private readonly List<WidgetWindow> _windows = [];
    private LiveDataProvider? _liveData;
    private bool _editMode;
    private WrapPanel? _chipsHost;

    public event Action? WidgetListChanged;

    public override string DisplayName => "Виджеты";

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<WidgetsSettings>();
        RegisterHotkey(_settings.HotkeyEditMode, ToggleEditMode);
    }

    protected override void OnEnabled()
    {
        _settings ??= LoadSettings<WidgetsSettings>();
        _liveData ??= new LiveDataProvider();

        foreach (var model in _settings.Widgets.Where(w => w.IsVisible))
        {
            var win = new WidgetWindow(model, _liveData!, SaveAll, AddWidgetFromEditor, ExportWidget, ImportWidget);
            win.Show();
            _windows.Add(win);
        }
    }

    protected override void OnDisabled()
    {
        foreach (var win in _windows.ToList())
        {
            win.Close();
            _windows.Remove(win);
        }
    }

    private void ToggleEditMode()
    {
        _editMode = !_editMode;
        foreach (var win in _windows)
        {
            win.SetEditMode(_editMode);
            if (_editMode && !win.IsVisible) win.Show();
        }
        Services.Overlay.ShowToast(_editMode ? "Режим редактирования включён" : "Изменения сохранены", "Виджеты");
        if (!_editMode) OnWidgetChanged();
    }

    private void SaveAll() => SaveSettings(_settings);

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new HotkeySlot("Включить/выключить режим редактирования",
            () => _settings.HotkeyEditMode,
            g => { _settings.HotkeyEditMode = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    public FrameworkElement BuildEditor()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var openEditorBtn = new Button { Content = "Открыть редактор", HorizontalAlignment = HorizontalAlignment.Left };
        openEditorBtn.Click += (_, _) =>
        {
            var template = _settings.Widgets.FirstOrDefault() ?? new WidgetModel();
            var editor = new WidgetsEditorWindow(template, _liveData!, SaveAll, AddWidgetFromEditor, ExportWidget, ImportWidget);
            editor.Show();
        };
        panel.Children.Add(openEditorBtn);

        panel.Children.Add(SettingsUi.Section("СПИСОК ВИДЖЕТОВ"));

        _chipsHost = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        RefreshChips();
        panel.Children.Add(_chipsHost);

        WidgetListChanged += RefreshChips;
        panel.Unloaded += (_, _) => WidgetListChanged -= RefreshChips;

        if (_settings.Widgets.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Виджетов пока нет. Откройте редактор и нажмите «Добавить виджет».",
                Foreground = JarvisTheme.ForegroundBrush(0.4),
                FontSize = 12.5,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return panel;
    }

    private void RefreshChips()
    {
        if (_chipsHost == null) return;

        var parent = _chipsHost.Parent as StackPanel;
        if (parent == null) return;

        int idx = parent.Children.IndexOf(_chipsHost);
        while (parent.Children.Count > idx)
            parent.Children.RemoveAt(parent.Children.Count - 1);

        _chipsHost = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        parent.Children.Add(_chipsHost);

        foreach (var widget in _settings.Widgets)
            _chipsHost.Children.Add(BuildWidgetChip(widget));

        if (_settings.Widgets.Count == 0)
        {
            parent.Children.Add(new TextBlock
            {
                Text = "Виджетов пока нет. Откройте редактор и нажмите «Добавить виджет».",
                Foreground = JarvisTheme.ForegroundBrush(0.4),
                FontSize = 12.5,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private UIElement BuildWidgetChip(WidgetModel widget)
    {
        var toggle = new JarvisToggleSwitch
        {
            IsChecked = widget.IsVisible,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += (_, _) => { widget.IsVisible = true; ShowWidget(widget); OnWidgetChanged(); WidgetListChanged?.Invoke(); };
        toggle.Unchecked += (_, _) => { widget.IsVisible = false; HideWidget(widget); OnWidgetChanged(); WidgetListChanged?.Invoke(); };

        var name = new TextBlock
        {
            Text = widget.Name,
            Foreground = JarvisTheme.ForegroundBrush(0.92),
            FontSize = 12.5,
            MaxWidth = 200,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Клик — открыть в редакторе",
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Margin = new Thickness(10, 0, 0, 0)
        };
        name.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            new WidgetsEditorWindow(widget, _liveData!, SaveAll, AddWidgetFromEditor, ExportWidget, ImportWidget).Show();
        };

        var remove = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Foreground = JarvisTheme.ForegroundBrush(0.45),
            Margin = new Thickness(10, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Удалить виджет"
        };
        remove.MouseEnter += (_, _) => remove.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
        remove.MouseLeave += (_, _) => remove.Foreground = JarvisTheme.ForegroundBrush(0.45);
        remove.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _settings.Widgets.Remove(widget);
            HideWidget(widget);
            OnWidgetChanged();
            WidgetListChanged?.Invoke();
        };

        var brush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        var chip = new Border
        {
            Background = brush,
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.30),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 5, 7, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new StackPanel { Orientation = Orientation.Horizontal }
        };
        ((StackPanel)chip.Child).Children.Add(toggle);
        ((StackPanel)chip.Child).Children.Add(name);
        ((StackPanel)chip.Child).Children.Add(remove);

        chip.MouseEnter += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromArgb(0x26, 0x00, 0xD2, 0xFF), TimeSpan.FromMilliseconds(90)));
        chip.MouseLeave += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(130)));

        return chip;
    }

    private void AddWidgetFromEditor(WidgetModel source)
    {
        var copy = CloneWidget(source);
        _settings.Widgets.Add(copy);
        OnWidgetChanged();
        ShowWidget(copy);
        WidgetListChanged?.Invoke();
        Services.Overlay.ShowToast($"Добавлен виджет: {copy.Name}", "Виджеты");
    }

    private static WidgetModel CloneWidget(WidgetModel source)
    {
        return new WidgetModel
        {
            Name = source.Name,
            Width = source.Width,
            Height = source.Height,
            Opacity = source.Opacity,
            IsVisible = true,
            Script = source.Script,
            Elements = source.Elements.Select(e => new WidgetElement
            {
                Type = e.Type,
                X = e.X, Y = e.Y,
                Width = e.Width, Height = e.Height,
                Text = e.Text,
                Binding = e.Binding,
                Background = e.Background,
                Foreground = e.Foreground,
                FontSize = e.FontSize,
                CornerRadius = e.CornerRadius
            }).ToList(),
            Vars = new Dictionary<string, object>(source.Vars)
        };
    }

    // Пункт 2: Экспорт/Импорт только одного виджета
    private void ExportWidget(WidgetModel model)
    {
        try
        {
            var json = JsonSerializer.Serialize(model, SettingsStore.JsonOptions);
            Clipboard.SetText(json);
            Services.Overlay.ShowToast("Виджет скопирован в буфер обмена", "Виджеты");
        }
        catch (Exception ex)
        {
            Log.Error($"export widget: {ex.Message}");
            Services.Overlay.ShowToast("Ошибка экспорта", "Виджеты");
        }
    }

    private void ImportWidget(WidgetModel target)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                Services.Overlay.ShowToast("Буфер обмена пуст", "Виджеты");
                return;
            }
            var json = Clipboard.GetText();
            var imported = JsonSerializer.Deserialize<WidgetModel>(json, SettingsStore.JsonOptions);
            if (imported == null)
            {
                Services.Overlay.ShowToast("Невалидный JSON", "Виджеты");
                return;
            }

            // Сохраняем ID и IsVisible, чтобы не ломать ссылки на окна
            imported.Id = target.Id;
            imported.IsVisible = target.IsVisible;

            target.Name = imported.Name;
            target.Width = imported.Width;
            target.Height = imported.Height;
            target.Opacity = imported.Opacity;
            target.Script = imported.Script;
            target.Elements = imported.Elements;
            target.Vars = imported.Vars;

            OnWidgetChanged();

            var existingWin = _windows.FirstOrDefault(w => w.Model == target);
            if (existingWin != null)
            {
                existingWin.Close();
                _windows.Remove(existingWin);
                if (target.IsVisible)
                {
                    var win = new WidgetWindow(target, _liveData!, SaveAll, AddWidgetFromEditor, ExportWidget, ImportWidget);
                    _windows.Add(win);
                    win.Show();
                }
            }

            WidgetListChanged?.Invoke();
            Services.Overlay.ShowToast($"Виджет импортирован", "Виджеты");
        }
        catch (Exception ex)
        {
            Log.Error($"import widget: {ex.Message}");
            Services.Overlay.ShowToast("Ошибка импорта: " + ex.Message, "Виджеты");
        }
    }

    private void ShowWidget(WidgetModel model)
    {
        var existing = _windows.FirstOrDefault(w => w.Model == model);
        if (existing == null)
        {
            var win = new WidgetWindow(model, _liveData!, SaveAll, AddWidgetFromEditor, ExportWidget, ImportWidget);
            _windows.Add(win);
            win.Show();
        }
        else existing.Show();
    }
    
    private void OnWidgetChanged()
    {
        SaveAll();
        foreach (var win in _windows.ToList())
            win.Rebuild();
    }

    private void HideWidget(WidgetModel model)
    {
        var existing = _windows.FirstOrDefault(w => w.Model == model);
        existing?.Hide();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        foreach (var win in _windows) win.Close();
    }
}