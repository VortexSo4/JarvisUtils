using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JarvisUtils.Core;
using GdiRectangle = System.Drawing.Rectangle;

namespace JarvisUtils.Modules.ScreenshotOcr;

public sealed class ScreenshotOverlayWindow : Window
{
    public event Action<GdiRectangle>? SelectionCompleted; // координаты в пикселях исходного снимка
    public event Action? SelectionCancelled;

    private readonly double _scale;
    private readonly Canvas _canvas;
    private readonly Border _selection;
    private readonly TextBlock _sizeLabel;
    private readonly IHotkeyManager _hotkeys;
    private Point _start;
    private bool _selecting;

    public ScreenshotOverlayWindow(BitmapSource screen, GdiRectangle bounds, IHotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        _scale = DpiUtil.Scale;

        ShowActivated = false;
        Focusable = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;

        // NOTE: расчёт в системном DPI; на mixed-DPI мультимониторах возможен сдвиг
        Left = bounds.X / _scale;
        Top = bounds.Y / _scale;
        Width = bounds.Width / _scale;
        Height = bounds.Height / _scale;

        _canvas = new Canvas();
        _canvas.Children.Add(new Image { Source = screen, Stretch = Stretch.Fill, Width = Width, Height = Height });
        _canvas.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            Width = Width,
            Height = Height
        });
        _selection = new Border
        {
            BorderBrush = JarvisTheme.AccentBrush,
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.FromArgb(16, 0, 210, 255)),
            Visibility = Visibility.Collapsed
        };
        _sizeLabel = new TextBlock
        {
            Foreground = JarvisTheme.AccentBrush,
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.85),
            Padding = new Thickness(4, 1, 4, 1)
        };
        _canvas.Children.Add(_selection);
        _canvas.Children.Add(_sizeLabel);
        Content = _canvas;

        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonDown += (_, _) => SelectionCancelled?.Invoke();
        Closed += (_, _) => _hotkeys.UnregisterAll(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.MakeNonActivating(this); // фокус целевого приложения не трогаем
        _hotkeys.Register(Key.Escape, Modifiers.None, () => SelectionCancelled?.Invoke(), this);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _selecting = true;
        _start = e.GetPosition(_canvas);
        _selection.Visibility = Visibility.Visible;
        _sizeLabel.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting) return;
        var p = e.GetPosition(_canvas);
        double x = Math.Min(_start.X, p.X), y = Math.Min(_start.Y, p.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
        Canvas.SetLeft(_selection, x);
        Canvas.SetTop(_selection, y);
        _selection.Width = w;
        _selection.Height = h;
        Canvas.SetLeft(_sizeLabel, x);
        Canvas.SetTop(_sizeLabel, Math.Max(0, y - 24));
        _sizeLabel.Text = $"{(int)(w * _scale)} × {(int)(h * _scale)}";
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);
        double x = Math.Min(_start.X, p.X), y = Math.Min(_start.Y, p.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
        if (w * _scale < 4 || h * _scale < 4) { SelectionCancelled?.Invoke(); return; }
        SelectionCompleted?.Invoke(GdiRectangle.FromLTRB(
            (int)Math.Floor(x * _scale), (int)Math.Floor(y * _scale),
            (int)Math.Ceiling((x + w) * _scale), (int)Math.Ceiling((y + h) * _scale)));
    }
}