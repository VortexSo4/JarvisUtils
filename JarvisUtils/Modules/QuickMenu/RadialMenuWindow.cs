using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.QuickMenu;

/// <summary>
/// Кольцо у курсора. Окно не активируется (WS_EX_NOACTIVATE): клавиши ловятся через общий
/// keyboard hook, мышь — опросом позиции. Пункт выбирается НАПРАВЛЕНИЕМ от центра, поэтому
/// попадать точно в сектор не нужно; в «дырке» по центру выбора нет.
/// </summary>
internal sealed class RadialMenuWindow : Window
{
    private const double Inner = 62, Outer = 150; // радиусы кольца, DIP
    private const double DeadZone = 30;           // ближе к центру — ничего не выбрано
    private const double Margin = 36;             // поле вокруг кольца: ловит клики рядом
    private const double GapDegrees = 1.6;

    // 1–9, 0, затем верхний буквенный ряд — клавиши под левой рукой
    private static readonly Key[] Keys =
    [
        Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0,
        Key.Q, Key.W, Key.E, Key.R, Key.T, Key.Y, Key.U, Key.I, Key.O, Key.P
    ];

    private readonly List<QuickAction> _items;
    private readonly IKeyboardHook _keyboard;
    private readonly List<(Path Shape, SolidColorBrush Fill)> _segments = [];
    private readonly TextBlock _centerTitle;
    private readonly TextBlock _centerKey;
    private readonly Border _centerPill;
    private readonly DispatcherTimer _poll;
    private readonly ScaleTransform _scale = new(0.85, 0.85);
    private readonly double _size;
    private int _selected = -1;
    private Point _lastCursor;
    private bool _closed;

    public event Action<QuickAction>? Picked;

    public RadialMenuWindow(List<QuickAction> items, IKeyboardHook keyboard)
    {
        _items = items;
        _keyboard = keyboard;
        _size = (Outer + Margin) * 2;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // почти прозрачный фон — иначе клики «в дырку» и между секторами уходят окну под меню
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Height = _size;
        Resources.MergedDictionaries.Add(Widgets.WidgetUi.Theme);
        PlaceAtCursor();

        var canvas = new Canvas { Width = _size, Height = _size, RenderTransform = _scale, RenderTransformOrigin = new Point(0.5, 0.5) };
        double c = _size / 2, span = 360.0 / items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            double mid = -90 + i * span; // первый пункт — сверху, дальше по часовой
            var fill = new SolidColorBrush(Color.FromArgb(0xEE, 0x17, 0x1A, 0x22));
            var path = new Path
            {
                Data = Sector(c, mid - span / 2 + GapDegrees, mid + span / 2 - GapDegrees),
                Fill = fill,
                Stroke = JarvisTheme.Solid(JarvisTheme.Accent, 0.22),
                StrokeThickness = 1
            };
            canvas.Children.Add(path);
            _segments.Add((path, fill));

            // иконка в середине сектора, клавиша — у внешнего края
            double r = (Inner + Outer) / 2 - 6, rad = mid * Math.PI / 180;
            var glyph = FluentIcons.Glyph(items[i].Glyph, 20, JarvisTheme.ForegroundBrush(0.92));
            glyph.Width = 28;
            glyph.IsHitTestVisible = false;
            Canvas.SetLeft(glyph, c + r * Math.Cos(rad) - 14);
            Canvas.SetTop(glyph, c + r * Math.Sin(rad) - 13);
            canvas.Children.Add(glyph);

            if (i < Keys.Length)
            {
                double rk = Outer - 17;
                var key = new TextBlock
                {
                    Text = KeyLabel(Keys[i]),
                    FontSize = 10,
                    Foreground = JarvisTheme.ForegroundBrush(0.45),
                    Width = 16,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(key, c + rk * Math.Cos(rad) - 8);
                Canvas.SetTop(key, c + rk * Math.Sin(rad) - 7);
                canvas.Children.Add(key);
            }
        }

        // центр: название пункта под курсором (сама «дырка» прозрачная)
        _centerTitle = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = Inner * 2 - 18 };
        _centerKey = new TextBlock { FontSize = 10.5, Foreground = JarvisTheme.AccentBrush, TextAlignment = TextAlignment.Center };
        var centerStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        centerStack.Children.Add(_centerTitle);
        centerStack.Children.Add(_centerKey);
        _centerPill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x14, 0x1A)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 4, 8, 5),
            Child = centerStack,
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false
        };
        var centerHost = new Grid { Width = Inner * 2, Height = Inner * 2, IsHitTestVisible = false };
        centerHost.Children.Add(_centerPill);
        _centerPill.HorizontalAlignment = HorizontalAlignment.Center;
        _centerPill.VerticalAlignment = VerticalAlignment.Center;
        Canvas.SetLeft(centerHost, c - Inner);
        Canvas.SetTop(centerHost, c - Inner);
        canvas.Children.Add(centerHost);
        Content = canvas;

        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _poll.Tick += (_, _) => TrackCursor();

        MouseLeftButtonUp += (_, e) => { e.Handled = true; Pick(_selected); };
        MouseRightButtonUp += (_, e) => { e.Handled = true; CloseNow(); };
        Loaded += (_, _) =>
        {
            _keyboard.KeyDown += OnKey;
            _keyboard.MouseDown += OnGlobalMouseDown;
            _poll.Start();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
        };
        Closed += (_, _) =>
        {
            _poll.Stop();
            _keyboard.KeyDown -= OnKey;
            _keyboard.MouseDown -= OnGlobalMouseDown;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.MakeNonActivating(this); // фокус остаётся в окне пользователя
    }

    private void PlaceAtCursor()
    {
        WinApi.GetCursorPos(out var p);
        double s = DpiUtil.Scale, cx = p.X / s, cy = p.Y / s;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).WorkingArea;
        // кольцо по центру курсора; у края экрана — сдвигаем, чтобы влезло целиком
        Left = Math.Clamp(cx - _size / 2, screen.Left / s - Margin, screen.Right / s - _size + Margin);
        Top = Math.Clamp(cy - _size / 2, screen.Top / s - Margin, screen.Bottom / s - _size + Margin);
        _lastCursor = new Point(cx, cy);
    }

    private static Geometry Sector(double c, double fromDeg, double toDeg)
    {
        Point P(double r, double deg) => new(c + r * Math.Cos(deg * Math.PI / 180), c + r * Math.Sin(deg * Math.PI / 180));
        bool large = toDeg - fromDeg > 180;
        var figure = new PathFigure { StartPoint = P(Inner, fromDeg), IsClosed = true };
        figure.Segments.Add(new LineSegment(P(Outer, fromDeg), true));
        figure.Segments.Add(new ArcSegment(P(Outer, toDeg), new Size(Outer, Outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(P(Inner, toDeg), true));
        figure.Segments.Add(new ArcSegment(P(Inner, fromDeg), new Size(Inner, Inner), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Выбор по направлению от центра кольца — работает и когда курсор вне окна.</summary>
    private void TrackCursor()
    {
        WinApi.GetCursorPos(out var p);
        double s = DpiUtil.Scale;
        var cursor = new Point(p.X / s, p.Y / s);
        if ((cursor - _lastCursor).Length < 0.5) return; // мышь стоит — не перебиваем выбор с клавиатуры
        _lastCursor = cursor;

        double dx = cursor.X - (Left + _size / 2), dy = cursor.Y - (Top + _size / 2);
        if (Math.Sqrt(dx * dx + dy * dy) < DeadZone) { Select(-1); return; }
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90; // 0° — вверх, по часовой
        double span = 360.0 / _items.Count;
        int index = (int)Math.Floor(((angle + span / 2) % 360 + 360) % 360 / span);
        Select(index % _items.Count);
    }

    private void Select(int index)
    {
        if (index == _selected) return;
        if (_selected >= 0) Paint(_selected, false);
        _selected = index;
        if (index < 0)
        {
            _centerPill.Visibility = Visibility.Hidden;
            return;
        }
        Paint(index, true);
        _centerTitle.Text = _items[index].Title;
        _centerKey.Text = index < Keys.Length ? KeyLabel(Keys[index]) : "";
        _centerPill.Visibility = Visibility.Visible;
    }

    private void Paint(int index, bool on)
    {
        var (shape, fill) = _segments[index];
        fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
            on ? Color.FromArgb(0xF2, 0x0E, 0x3A, 0x48) : Color.FromArgb(0xEE, 0x17, 0x1A, 0x22),
            TimeSpan.FromMilliseconds(on ? 60 : 120)));
        shape.Stroke = on ? JarvisTheme.AccentBrush : JarvisTheme.Solid(JarvisTheme.Accent, 0.22);
        shape.StrokeThickness = on ? 1.5 : 1;
    }

    private void OnKey(object? sender, KeyHookEventArgs e)
    {
        if (e.IsInjected || _closed) return;
        var key = e.Key;
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseNow();
                return;
            case Key.Enter or Key.Space:
                e.Handled = true;
                Pick(_selected);
                return;
            case Key.Right or Key.Down or Key.Tab:
                e.Handled = true;
                Select((_selected + 1 + _items.Count) % _items.Count);
                return;
            case Key.Left or Key.Up:
                e.Handled = true;
                Select(_selected <= 0 ? _items.Count - 1 : _selected - 1);
                return;
        }
        int index = Array.IndexOf(Keys, key);
        if (index < 0 && key is >= Key.NumPad0 and <= Key.NumPad9)
            index = Array.IndexOf(Keys, key == Key.NumPad0 ? Key.D0 : Key.D1 + (key - Key.NumPad1));
        if (index >= 0 && index < _items.Count)
        {
            e.Handled = true;
            Pick(index);
        }
    }

    /// <summary>Клик вне кольца — закрыть (по окну меню клик обработает MouseLeftButtonUp).</summary>
    private void OnGlobalMouseDown(object? sender, MouseHookEventArgs e)
    {
        double s = DpiUtil.Scale;
        double x = e.X / s, y = e.Y / s;
        if (x < Left || y < Top || x > Left + _size || y > Top + _size)
            Dispatcher.BeginInvoke(CloseNow);
    }

    private void Pick(int index)
    {
        if (_closed) return;
        if (index < 0 || index >= _items.Count) { CloseNow(); return; }
        var item = _items[index];
        CloseNow();
        Picked?.Invoke(item);
    }

    public void CloseNow()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }

    private static string KeyLabel(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString()
    };
}
