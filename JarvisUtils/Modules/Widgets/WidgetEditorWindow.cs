using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

/// <summary>
/// Редактор «своего» виджета: XAML и скрипт слева, живое превью справа.
/// XAML перерисовывается на лету; скрипт — по F5 (компиляция Roslyn не бесплатна).
/// Ctrl+S — применить к виджету; при закрытии изменения применяются сами.
/// </summary>
internal sealed class WidgetEditorWindow : Window
{
    private static readonly Dictionary<string, WidgetEditorWindow> OpenEditors = new();

    private readonly WidgetModel _model;
    private readonly WidgetsModule _module;
    private readonly WidgetModel _draft;
    private readonly TextBox _name;
    private readonly TextBox _xaml;
    private readonly TextBox _script;
    private readonly Border _previewFrame;
    private readonly TextBlock _sizeLabel;
    private readonly TextBlock _statusGlyph;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _debounce;
    private CustomContent? _preview;
    private bool _scriptLive;
    private bool _dirty;

    public static void Open(WidgetModel model, WidgetsModule module)
    {
        if (OpenEditors.TryGetValue(model.Id, out var existing))
        {
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var editor = new WidgetEditorWindow(model, module);
        OpenEditors[model.Id] = editor;
        editor.Show();
        editor.Activate();
    }

    public static void CloseFor(WidgetModel model)
    {
        if (OpenEditors.TryGetValue(model.Id, out var editor)) { editor._dirty = false; editor.Close(); }
    }

    private WidgetEditorWindow(WidgetModel model, WidgetsModule module)
    {
        _model = model;
        _module = module;
        _draft = model.Clone();
        _draft.Id = model.Id;

        Title = $"Виджет — {model.Name}";
        Width = 1120;
        Height = 700;
        MinWidth = 820;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1D));
        FontFamily = new FontFamily("Segoe UI");
        Resources.MergedDictionaries.Add(WidgetUi.Theme);
        Loaded += (_, _) => ApplyDarkTitleBar();

        // ── верхняя панель ──
        _name = new TextBox { Text = model.Name, Width = 240, VerticalAlignment = VerticalAlignment.Center };
        _name.TextChanged += (_, _) => { _draft.Name = _name.Text; MarkDirty(); };

        var templates = new ComboBox { Width = 210, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        templates.Items.Add(new ComboBoxItem { Content = "Вставить шаблон…", IsEnabled = false });
        foreach (var t in WidgetTemplates.All) templates.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t });
        templates.SelectedIndex = 0;
        templates.SelectionChanged += (_, _) =>
        {
            if ((templates.SelectedItem as ComboBoxItem)?.Tag is not WidgetTemplate t) return;
            _xaml!.Text = t.Xaml;
            _script!.Text = t.Script;
            templates.SelectedIndex = 0;
        };

        var run = ToolbarButton(FluentIcons.Play, "Запустить скрипт", "F5", RunScript);
        var save = ToolbarButton(FluentIcons.Save, "Применить", "Ctrl+S", Save);
        save.Background = JarvisTheme.Solid(JarvisTheme.Accent, 0.18);
        save.BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.45);

        var top = new DockPanel { Margin = new Thickness(16, 12, 16, 10), LastChildFill = false };
        top.Children.Add(_name);
        top.Children.Add(templates);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(run);
        right.Children.Add(save);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);

        // ── редакторы ──
        _xaml = CodeBox(model.Xaml);
        _xaml.TextChanged += (_, _) => { _draft.Xaml = _xaml.Text; MarkDirty(); ScheduleRebuild(); };
        _script = CodeBox(model.Script);
        _script.TextChanged += (_, _) =>
        {
            _draft.Script = _script.Text;
            MarkDirty();
            if (_scriptLive) SetStatus(null, "Скрипт изменён — F5, чтобы перезапустить превью");
        };

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "XAML", Content = _xaml });
        tabs.Items.Add(new TabItem { Header = "Скрипт C#", Content = _script });
        tabs.Items.Add(new TabItem { Header = "Справка", Content = HelpView() });

        // ── превью ──
        _previewFrame = new Border
        {
            Width = model.Width,
            Height = model.Height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var previewViewbox = new Viewbox { Child = _previewFrame, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(18) };
        _sizeLabel = new TextBlock
        {
            Text = $"{model.Width:0} × {model.Height:0} — размер меняется у самого виджета",
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 11,
            Margin = new Thickness(14, 0, 0, 10),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var previewArea = new Grid
        {
            Background = new DrawingBrush
            {
                // мелкая «шахматка» — видно прозрачные области виджета
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute,
                Drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x27)), null,
                    Geometry.Parse("M0,0 H8 V8 H0Z M8,8 H16 V16 H8Z"))
            }
        };
        previewArea.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x12, 0x18)) });
        previewArea.Children.Add(previewViewbox);
        previewArea.Children.Add(_sizeLabel);
        var previewHost = new Border
        {
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x33)),
            BorderThickness = new Thickness(1),
            Child = previewArea
        };

        var main = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(tabs, 0);
        Grid.SetColumn(previewHost, 2);
        main.Children.Add(tabs);
        main.Children.Add(previewHost);

        // ── статус ──
        _statusGlyph = FluentIcons.Glyph(FluentIcons.Check, 13);
        _statusText = new TextBlock { Margin = new Thickness(8, 0, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var status = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        _statusGlyph.VerticalAlignment = VerticalAlignment.Top;
        _statusGlyph.Margin = new Thickness(0, 2, 0, 0);
        status.Children.Add(_statusGlyph);
        status.Children.Add(_statusText);

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(status);
        layout.Children.Add(main);
        Content = layout;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RebuildPreview(); };

        PreviewKeyDown += OnKeyDown;
        Closing += (_, _) => { if (_dirty) Save(); };
        Closed += (_, _) =>
        {
            _preview?.Dispose();
            OpenEditors.Remove(_model.Id);
        };

        _scriptLive = !string.IsNullOrWhiteSpace(model.Script); // уже работающий виджет — превью со скриптом
        RebuildPreview();
        _dirty = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; RunScript(); }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Save(); }
    }

    private void MarkDirty()
    {
        _dirty = true;
        Title = $"Виджет — {_draft.Name} •";
    }

    private void ScheduleRebuild()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void RunScript()
    {
        _scriptLive = true;
        RebuildPreview();
    }

    private void RebuildPreview()
    {
        _preview?.Dispose();
        _preview = new CustomContent(_draft, _module.Services, () => { }, runScript: _scriptLive);
        _preview.ErrorChanged += UpdateStatus;
        _previewFrame.Child = _preview.View;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_preview?.Error is { } error) SetStatus(error, null);
        else SetStatus(null, _scriptLive || string.IsNullOrWhiteSpace(_draft.Script)
            ? "Без ошибок"
            : "XAML без ошибок · скрипт не запущен — F5");
    }

    private void SetStatus(string? error, string? info)
    {
        bool bad = error != null;
        _statusGlyph.Text = bad ? FluentIcons.Warning : FluentIcons.Check;
        var color = bad ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)) : JarvisTheme.AccentBrush;
        _statusGlyph.Foreground = color;
        _statusText.Foreground = bad ? color : JarvisTheme.ForegroundBrush(0.6);
        _statusText.Text = error ?? info ?? "";
    }

    private void Save()
    {
        _model.Name = string.IsNullOrWhiteSpace(_draft.Name) ? "Виджет" : _draft.Name.Trim();
        _model.Xaml = _draft.Xaml;
        _model.Script = _draft.Script;
        _module.ApplyEdit(_model);
        _dirty = false;
        Title = $"Виджет — {_model.Name}";
        SetStatus(_preview?.Error, "Применено к виджету");
    }

    // ── элементы ──

    private static TextBox CodeBox(string text) => new()
    {
        Text = text,
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 6, 0, 0)
    };

    private static Button ToolbarButton(string glyph, string label, string gesture, Action onClick)
    {
        var content = FluentIcons.WithLabel(glyph, label);
        content.Children.Add(new TextBlock { Text = gesture, Foreground = JarvisTheme.ForegroundBrush(0.4), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static FrameworkElement HelpView()
    {
        var text = new TextBox
        {
            Text = HelpText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 0)
        };
        return text;
    }

    private void ApplyDarkTitleBar()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        WinApi.DwmSetWindowAttribute(hwnd, WinApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private const string HelpText = """
        XAML — обычная разметка WPF. xmlns можно не писать: подставится сам.
        Корень — любой элемент (Border, Grid, StackPanel…). Стили приложения
        (кнопки, поля ввода) применяются автоматически. Иконки — шрифт
        «Segoe Fluent Icons»: <TextBlock FontFamily="Segoe Fluent Icons" Text="&#xE8EF;"/>

        ── Привязки ({Binding …}) ──
        Now                  текущее время (обновляется каждую секунду)
                             {Binding Now, StringFormat=HH:mm:ss}
        Cpu, CpuFraction     загрузка процессора: % и доля 0..1
        Ram, RamFraction     занятая память: % и доля 0..1
        RamUsedGb, RamTotalGb
        NetDown, NetUp       сеть, КБ/с
        DiskFreeGb           свободно на системном диске
        Battery              заряд, % (-1 — батареи нет)
        Uptime               время работы системы (TimeSpan)
        Data[ключ]           значения из скрипта: {Binding Data[left]}

        Доли 0..1 удобно отдавать в ScaleTransform — получается полоска:
        <ScaleTransform ScaleX="{Binding CpuFraction}"/>

        ── Команды (Command="{Binding …}" CommandParameter="…") ──
        Run      запустить программу, файл, папку или URL ("calc.exe", "https://…")
        Copy     скопировать текст в буфер
        Type     напечатать текст в активное окно
        Invoke   вызвать обработчик скрипта On("имя", …)

        ── Скрипт C# ──
        Выполняется один раз при загрузке виджета (компиляция — в фоне),
        дальше работают таймеры и обработчики, которые он зарегистрировал.
        Код выполняется на UI-потоке: тяжёлое — через Task.Run.

        Every(секунды, () => …)     повторять (первый раз — сразу)
        On("имя", () => …)          клик по кнопке с x:Name="имя"
                                    или Command="{Binding Invoke}" CommandParameter="имя"
        Data["ключ"] = значение     для {Binding Data[ключ]}
        Find<TextBlock>("имя")      элемент разметки по x:Name
        Root                        корневой элемент разметки
        Now, Sys.Cpu, Sys.RamPercent, Sys.NetDownKBps …  метрики
        Save("ключ", значение)      сохранить между перезапусками
        Load("ключ"), LoadNumber("ключ", 0)
        Run("notepad.exe"), Copy("текст"), Type("текст")
        Toast("сообщение"), Log("в jarvis.log")

        Пример — счётчик кликов:
            var n = (int)LoadNumber("n");
            Data["n"] = n;
            On("plus", () => { n++; Data["n"] = n; Save("n", n); });
        Разметка:
            <StackPanel>
              <TextBlock Text="{Binding Data[n]}" FontSize="30" Foreground="White"/>
              <Button x:Name="plus" Content="+1"/>
            </StackPanel>

        Ошибки показываются внизу редактора и красным значком на виджете.
        """;
}
