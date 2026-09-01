using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            var win = new WidgetWindow(model, _liveData!, SaveAll);
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
        if (!_editMode) SaveAll();
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

        // Кнопки управления виджетами
        var topButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,15) };
        
        var openEditorBtn = new Button { Content = "Открыть редактор", Margin = new Thickness(0,0,10,0) };
        openEditorBtn.Click += (_, _) =>
        {
            var widgetToEdit = _settings.Widgets.FirstOrDefault() ?? CreateNewWidget();
            var editor = new WidgetsEditorWindow(widgetToEdit, _liveData!, SaveAll);
            editor.Show();
        };
        topButtons.Children.Add(openEditorBtn);

        var addWidgetBtn = new Button { Content = "+ Создать виджет" };
        addWidgetBtn.Click += (_, _) =>
        {
            var w = CreateNewWidget();
            ShowWidget(w);
            new WidgetsEditorWindow(w, _liveData!, SaveAll).Show();
        };
        topButtons.Children.Add(addWidgetBtn);
        
        panel.Children.Add(topButtons);
        panel.Children.Add(SettingsUi.Section("СПИСОК ВИДЖЕТОВ"));
        
        foreach (var widget in _settings.Widgets.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0,5,0,5) };
            
            var toggle = new JarvisToggleSwitch { IsChecked = widget.IsVisible, VerticalAlignment = VerticalAlignment.Center };
            toggle.Checked += (_, _) => { widget.IsVisible = true; ShowWidget(widget); SaveAll(); };
            toggle.Unchecked += (_, _) => { widget.IsVisible = false; HideWidget(widget); SaveAll(); };
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            
            var delBtn = new Button { Content = "✕", Width = 30, Height = 30, Margin = new Thickness(5,0,0,0), VerticalAlignment = VerticalAlignment.Center };
            delBtn.Click += (_, _) => { _settings.Widgets.Remove(widget); HideWidget(widget); SaveAll(); BuildEditor(); }; // Перезагрузка UI
            DockPanel.SetDock(delBtn, Dock.Right);
            row.Children.Add(delBtn);

            row.Children.Add(new TextBlock
            {
                Text = widget.Name,
                Foreground = JarvisTheme.ForegroundBrush(0.85),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14
            });
            panel.Children.Add(row);
        }

        return panel;
    }

    private WidgetModel CreateNewWidget()
    {
        var w = new WidgetModel { Name = "Виджет " + (_settings.Widgets.Count + 1) };
        _settings.Widgets.Add(w);
        SaveAll();
        return w;
    }

    private void ShowWidget(WidgetModel model)
    {
        var existing = _windows.FirstOrDefault(w => w.Model == model);
        if (existing == null)
        {
            var win = new WidgetWindow(model, _liveData!, SaveAll);
            _windows.Add(win);
            win.Show();
        }
        else existing.Show();
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