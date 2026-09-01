using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly Canvas _canvas;
    private readonly Dictionary<string, FrameworkElement> _controls = new();
    
    private Script<object>? _script;
    private bool _scriptOk;
    private bool _editMode;
    private DispatcherTimer? _timer;
    private DateTime _lastTick;
    private Thumb? _resizeThumb;

    public WidgetWindow(WidgetModel model, LiveDataProvider data, Action onChange)
    {
        Model = model;
        _data = data;
        _onChange = onChange;

        // Подтягиваем стили Jarvis
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/JarvisUtils;component/UI/Theme/JarvisStyles.xaml")
        });

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
            MouseLeftButtonDown += WidgetWindow_MouseLeftButtonDown;
            MouseRightButtonDown += WidgetWindow_MouseRightButtonDown;

            _resizeThumb = new Thumb
            {
                Width = 14, Height = 14,
                Cursor = Cursors.SizeNWSE,
                Background = JarvisTheme.AccentBrush,
                BorderBrush = JarvisTheme.Solid(JarvisTheme.Bg, 0.8),
                BorderThickness = new Thickness(1),
                Template = new ControlTemplate(typeof(Thumb))
                {
                    VisualTree = new FrameworkElementFactory(typeof(Border))
                }
            };
            Canvas.SetRight(_resizeThumb, 0);
            Canvas.SetBottom(_resizeThumb, 0);
            _resizeThumb.DragDelta += (s, e) =>
            {
                Width = Math.Max(50, Width + e.HorizontalChange);
                Height = Math.Max(50, Height + e.VerticalChange);
                Model.Width = Width;
                Model.Height = Height;
                _onChange?.Invoke();
            };
            _canvas.Children.Add(_resizeThumb);
        }
        else
        {
            _canvas.Background = Brushes.Transparent;
            BorderBrush = null;
            MouseLeftButtonDown -= WidgetWindow_MouseLeftButtonDown;
            MouseRightButtonDown -= WidgetWindow_MouseRightButtonDown;
            if (_resizeThumb != null) _canvas.Children.Remove(_resizeThumb);
        }
    }

    private void WidgetWindow_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        // Применяем стили к меню
        menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        
        var itemEdit = new MenuItem { Header = "Открыть в редакторе" };
        itemEdit.Click += (_, _) => { new WidgetsEditorWindow(Model, _data, _onChange).Show(); };
        var itemHide = new MenuItem { Header = "Скрыть виджет" };
        itemHide.Click += (_, _) => { Model.IsVisible = false; Hide(); _onChange?.Invoke(); };
        
        menu.Items.Add(itemEdit);
        menu.Items.Add(itemHide);
        menu.IsOpen = true;
    }

    private void WidgetWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_editMode) DragMove();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_editMode)
        {
            Model.X = Left;
            Model.Y = Top;
            _onChange?.Invoke();
        }
    }

    private void BuildElements()
    {
        _canvas.Children.Clear();
        _controls.Clear();

        foreach (var el in Model.Elements)
        {
            FrameworkElement? ctrl = el.Type switch
            {
                WidgetElementType.Panel => new Border(),
                WidgetElementType.Text => new TextBlock(),
                WidgetElementType.Button => new Button(),
                WidgetElementType.Checkbox => new CheckBox(),
                WidgetElementType.Note => new TextBox(),
                _ => null
            };
            if (ctrl == null) continue;

            ctrl.Width = el.Width;
            ctrl.Height = el.Height;
            Canvas.SetLeft(ctrl, el.X);
            Canvas.SetTop(ctrl, el.Y);
            
            try
            {
                if (ctrl is Control c)
                {
                    c.FontSize = el.FontSize;
                    c.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(el.Foreground));
                    if (el.Type != WidgetElementType.Button) 
                        c.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(el.Background));
                }
                else if (ctrl is TextBlock tb)
                {
                    tb.FontSize = el.FontSize;
                    tb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(el.Foreground));
                    tb.Text = el.Text;
                }
            }
            catch { /* ignore color errors */ }

            if (ctrl is Button btn)
            {
                btn.Content = el.Text;
                btn.Click += (_, _) => _data.RegisterClick(el.Id);
            }

            _controls[el.Id] = ctrl;
            _canvas.Children.Add(ctrl);
        }
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

        // Нативные биндинги (если скрипт не задан или для базовых элементов)
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
}

public sealed class ScriptGlobals
{
    public dynamic Widget { get; set; }
    public dynamic El { get; set; }
    public dynamic Data { get; set; }
    public double Dt { get; set; }
    public Dictionary<string, object> Vars { get; set; }
}