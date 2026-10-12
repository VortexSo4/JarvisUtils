using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisUtils.Core;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace JarvisUtils.Modules.Widgets;

public sealed class WidgetWindow : Window
{
    public WidgetModel Model { get; }
    private readonly LiveDataProvider _data;
    private readonly Action _onChange;
    private readonly Action<WidgetModel>? _onAddWidget;
    private readonly Action<WidgetModel>? _onExport;
    private readonly Action<WidgetModel>? _onImport;
    private readonly Canvas _canvas;
    private readonly Dictionary<string, FrameworkElement> _controls = new();

    private Script<object>? _script;
    private bool _scriptOk;
    private bool _editMode;
    private DispatcherTimer? _timer;
    private DateTime _lastTick;

    // Пункт 1: ручное управление перетаскиванием и ресайзом
    private Border? _resizeKnob;
    private bool _isDragging;
    private Point _dragStart;
    private bool _isResizing;
    private Point _resizeStart;
    private double _startW, _startH;

    public WidgetWindow(
        WidgetModel model,
        LiveDataProvider data,
        Action onChange,
        Action<WidgetModel>? onAddWidget = null,
        Action<WidgetModel>? onExport = null,
        Action<WidgetModel>? onImport = null)
    {
        Model = model;
        _data = data;
        _onChange = onChange;
        _onAddWidget = onAddWidget;
        _onExport = onExport;
        _onImport = onImport;

        Width = model.Width;
        Height = model.Height;
        Left = model.X;
        Top = model.Y;
        Opacity = model.Opacity;
        Title = model.Name;

        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;

        _canvas = new Canvas { Background = Brushes.Transparent };
        Content = _canvas;

        BuildElements();
        CompileScript();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _lastTick = DateTime.Now;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e) => _timer?.Stop();

    public void SetEditMode(bool enabled)
    {
        _editMode = enabled;
        if (enabled)
        {
            _canvas.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xD2, 0xFF));
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0x55);
            BorderThickness = new Thickness(1);

            _canvas.PreviewMouseLeftButtonDown += Canvas_DragDown;
            _canvas.PreviewMouseMove += Canvas_DragMove;
            _canvas.PreviewMouseLeftButtonUp += Canvas_DragUp;

            _resizeKnob = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = JarvisTheme.AccentBrush,
                BorderBrush = JarvisTheme.Solid(JarvisTheme.Bg, 0.8),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeNWSE
            };
            UpdateKnobPosition();
            _canvas.SizeChanged += (_, _) => UpdateKnobPosition();

            _resizeKnob.PreviewMouseLeftButtonDown += Knob_Down;
            _resizeKnob.PreviewMouseMove += Knob_Move;
            _resizeKnob.PreviewMouseLeftButtonUp += Knob_Up;

            _canvas.Children.Add(_resizeKnob);
        }
        else
        {
            _canvas.Background = Brushes.Transparent;
            BorderBrush = null;
            BorderThickness = new Thickness(0);
            _canvas.PreviewMouseLeftButtonDown -= Canvas_DragDown;
            _canvas.PreviewMouseMove -= Canvas_DragMove;
            _canvas.PreviewMouseLeftButtonUp -= Canvas_DragUp;

            if (_resizeKnob != null)
            {
                _canvas.Children.Remove(_resizeKnob);
                _resizeKnob = null;
            }
        }
    }

    private void UpdateKnobPosition()
    {
        if (_resizeKnob != null)
        {
            Canvas.SetLeft(_resizeKnob, Width - _resizeKnob.Width - 4);
            Canvas.SetTop(_resizeKnob, Height - _resizeKnob.Height - 4);
        }
    }

    private void Canvas_DragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border b && b == _resizeKnob) return;
        _isDragging = true;
        _dragStart = e.GetPosition(this);
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_DragMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        Left += p.X - _dragStart.X;
        Top += p.Y - _dragStart.Y;
        Model.X = Left;
        Model.Y = Top;
        _onChange?.Invoke();
        e.Handled = true;
    }

    private void Canvas_DragUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _canvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Knob_Down(object sender, MouseButtonEventArgs e)
    {
        _isResizing = true;
        _resizeStart = e.GetPosition(this);
        _startW = Width;
        _startH = Height;
        _resizeKnob!.CaptureMouse();
        e.Handled = true;
    }

    private void Knob_Move(object sender, MouseEventArgs e)
    {
        if (!_isResizing || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        Width = Math.Max(50, _startW + (p.X - _resizeStart.X));
        Height = Math.Max(50, _startH + (p.Y - _resizeStart.Y));
        Model.Width = Width;
        Model.Height = Height;
        UpdateKnobPosition();
        _onChange?.Invoke();
        e.Handled = true;
    }

    private void Knob_Up(object sender, MouseButtonEventArgs e)
    {
        _isResizing = false;
        _resizeKnob!.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void WidgetWindow_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var itemEdit = new MenuItem { Header = "Открыть в редакторе" };
        itemEdit.Click += (_, _) => { new WidgetsEditorWindow(Model, _data, _onChange, _onAddWidget, _onExport, _onImport).Show(); };
        var itemHide = new MenuItem { Header = "Скрыть виджет" };
        itemHide.Click += (_, _) => { Model.IsVisible = false; Hide(); _onChange?.Invoke(); };

        menu.Items.Add(itemEdit);
        menu.Items.Add(itemHide);
        menu.IsOpen = true;
    }

    // Пункт 4 и 5: Единый стиль элементов с редактором
    private void BuildElements()
    {
        _canvas.Children.Clear();
        _controls.Clear();

        foreach (var el in Model.Elements)
        {
            var container = new Border
            {
                Width = el.Width,
                Height = el.Height,
                CornerRadius = new CornerRadius(el.CornerRadius),
                ClipToBounds = true
            };

            try
            {
                container.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(el.Background));
            }
            catch { container.Background = Brushes.Transparent; }

            Canvas.SetLeft(container, el.X);
            Canvas.SetTop(container, el.Y);

            FrameworkElement? ctrl = el.Type switch
            {
                WidgetElementType.Panel => null,
                WidgetElementType.Text => new TextBlock
                {
                    Text = el.Text,
                    Foreground = SafeColor(el.Foreground),
                    FontSize = el.FontSize,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                },
                WidgetElementType.Button => new Button
                {
                    Content = el.Text,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = SafeColor(el.Foreground),
                    FontSize = el.FontSize
                },
                WidgetElementType.Checkbox => new CheckBox
                {
                    Content = el.Text,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = SafeColor(el.Foreground),
                    FontSize = el.FontSize
                },
                WidgetElementType.Note => new TextBox
                {
                    Text = el.Text,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = SafeColor(el.Foreground),
                    FontSize = el.FontSize
                },
                _ => null
            };

            if (ctrl != null)
            {
                container.Child = ctrl;
                if (ctrl is Button btn)
                    btn.Click += (_, _) => _data.RegisterClick(el.Id);
                _controls[el.Id] = ctrl;
            }
            else
            {
                _controls[el.Id] = container;
            }

            _canvas.Children.Add(container);
        }
    }

    private static SolidColorBrush SafeColor(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.Gray); }
    }

    public void CompileScript()
    {
        if (string.IsNullOrWhiteSpace(Model.Script))
        {
            _scriptOk = false;
            return;
        }
        try
        {
            var options = ScriptOptions.Default
                .WithReferences(typeof(object).Assembly, typeof(Window).Assembly, typeof(Brush).Assembly)
                .WithImports("System", "System.Windows", "System.Windows.Media", "System.Collections.Generic");

            _script = CSharpScript.Create(Model.Script, options, globalsType: typeof(ScriptGlobals));
            _script.Compile();
            _scriptOk = true;
        }
        catch (Exception ex)
        {
            _scriptOk = false;
            foreach (var ctrl in _controls.Values)
                if (ctrl is TextBlock tb) tb.Text = "Script Error:\n" + ex.Message;
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        double dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        foreach (var el in Model.Elements)
        {
            if (string.IsNullOrEmpty(el.Binding) || !_controls.TryGetValue(el.Id, out var ctrl)) continue;
            string val = el.Binding switch
            {
                "Time" => _data.Time.ToString("HH:mm:ss"),
                "CPU" => _data.Cpu.ToString("0.0") + "%",
                "RAM" => _data.Ram.ToString("0") + " MB",
                _ => ""
            };
            if (ctrl is TextBlock tb) tb.Text = val;
            else if (ctrl is Button b) b.Content = val;
        }

        if (!_scriptOk || _script == null) return;

        var globals = new ScriptGlobals
        {
            Widget = this,
            El = _controls,
            Data = _data,
            Dt = dt,
            Vars = Model.Vars
        };

        try
        {
            await _script.RunAsync(globals);
        }
        catch (Exception ex)
        {
            _scriptOk = false;
            foreach (var ctrl in _controls.Values)
                if (ctrl is TextBlock tb) tb.Text = "Runtime Error:\n" + ex.Message;
        }
    }
    
    public void Rebuild()
    {
        BuildElements();
        CompileScript();
    }
}

public sealed class ScriptGlobals
{
    public dynamic Widget { get; set; }
    public dynamic El { get; set; }
    public dynamic Data { get; set; }
    public double Dt { get; set; }
    public Dictionary<string, object> Vars { get; set; }
}