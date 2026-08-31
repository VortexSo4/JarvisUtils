using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;

namespace JarvisUtils.Core;

public sealed class OverlayService(IHotkeyManager hotkeys, IKeyboardHook hooks) : IOverlayService
{
    private readonly List<ToastWindow> _toasts = [];
    private ListPopupWindow? _popup;

    public void ShowToast(string message, string? title = null, double durationMs = 1600)
    {
        var toast = new ToastWindow(message, title, durationMs);
        _toasts.Add(toast);
        toast.Closed += (_, _) => { _toasts.Remove(toast); ReflowToasts(); };
        ReflowToasts(); // позиция ДО Show: иначе первый кадр рисуется в позиции по умолчанию
        toast.Show();
        toast.UpdateLayout();
        ReflowToasts(); // уточнение по фактической высоте (совпадает с измеренной)
    }

    public void ShowListPopup(ListPopupRequest request)
    {
        _popup?.ForceClose();
        var popup = new ListPopupWindow(request, hotkeys, hooks);
        _popup = popup;
        popup.Closed += (_, _) => { if (ReferenceEquals(_popup, popup)) _popup = null; };
        popup.Show(); // окно уже позиционировано в конструкторе (до Show)
    }

    private void ReflowToasts()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 14;
        for (int i = _toasts.Count - 1; i >= 0; i--) // новые — снизу
        {
            var t = _toasts[i];
            double h = t.ActualHeight > 0 ? t.ActualHeight : t.EstimatedHeight;
            t.Left = area.Right - t.Width - 14;
            t.Top = bottom - h;
            bottom -= h + 8;
        }
    }
}

internal static class OverlayVisuals
{
    /// <summary>
    /// true  — настоящий acrylic: окно НЕ layered, glass-фрейм растянут на всё окно (WindowChrome).
    /// false — фоллбэк «чистый неон»: прозрачные поля + внешнее свечение, без системного материала.
    /// </summary>
    public const bool UseAcrylic = true;

    /// <summary>Поля под внешнее свечение — только в неоновом режиме.</summary>
    public const double GlowMargin = 16;

    public static double ExtraWidth => UseAcrylic ? 0 : GlowMargin * 2;

    public static void ConfigureWindow(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = true;
        window.Focusable = false;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.Background = Brushes.Transparent;
        window.WindowStartupLocation = WindowStartupLocation.Manual; // Left/Top задаём сами до Show
        if (UseAcrylic)
        {
            window.AllowsTransparency = false; // layered + DWM-материал = серый прямоугольник на всё окно
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0)
            });
        }
        else
        {
            window.AllowsTransparency = true;
        }
    }

    /// <summary>
    /// Высота окна по измеренному контенту — ДО Show. Нужна, чтобы задать Left/Top заранее:
    /// acrylic-материал рисуется DWM независимо от Opacity окна, поэтому «невидимый» при
    /// Opacity=0 первый кадр в позиции по умолчанию (угол экрана) реально виден.
    /// </summary>
    public static double MeasureShellHeight(Border shell, double windowWidth)
    {
        double inner = windowWidth - (UseAcrylic ? 0 : GlowMargin * 2);
        shell.Measure(new Size(inner, double.PositiveInfinity));
        return shell.DesiredSize.Height + (UseAcrylic ? 0 : GlowMargin * 2);
    }

    public static (Border Shell, ScaleTransform Scale) CreateShell(double cornerRadius, FrameworkElement content)
    {
        var scale = new ScaleTransform(0.96, 0.96);

        Border shell;
        if (UseAcrylic)
        {
            shell = new Border
            {
                Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.55), // под заливкой виден acrylic
                BorderThickness = new Thickness(1),
                BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.22), // edge-lit контур
                CornerRadius = new CornerRadius(8),                       // ≈ радиус DWMWCP_ROUND
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
        }
        else
        {
            shell = new Border
            {
                Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.82),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(cornerRadius),
                Margin = new Thickness(GlowMargin),
                Effect = new DropShadowEffect { Color = JarvisTheme.Accent, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.35 },
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
        }

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(TopAccentLine());
        dock.Children.Add(content);
        shell.Child = dock;
        return (shell, scale);
    }

    /// <summary>Появление: fade + лёгкий scale из центра (0.96 → 1.0), 140 мс, ease-out.</summary>
    public static void PlayEntrance(Window window, ScaleTransform scale)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        var scaleX = new DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        var scaleY = scaleX.Clone();
        window.BeginAnimation(Window.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    /// <summary>Неоновая линия сверху: прозрачный → акцент → прозрачный.</summary>
    private static Border TopAccentLine()
    {
        var accent = JarvisTheme.Accent;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 0.0),
                new GradientStop(accent, 0.5),
                new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1.0)
            }
        };
        gradient.Freeze();
        var line = new Border { Height = 1.5, Background = gradient };
        DockPanel.SetDock(line, Dock.Top);
        return line;
    }
}

internal sealed class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly ScaleTransform _scale;
    private bool _closing;

    internal double EstimatedHeight { get; private set; }

    public ToastWindow(string message, string? title, double durationMs)
    {
        OverlayVisuals.ConfigureWindow(this);
        Width = 380 + OverlayVisuals.ExtraWidth;
        SizeToContent = SizeToContent.Height;
        Opacity = 0;

        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 13) };
        if (!string.IsNullOrWhiteSpace(title))
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = JarvisTheme.AccentBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = JarvisTheme.ForegroundBrush(0.92),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });

        var (shell, scale) = OverlayVisuals.CreateShell(10, panel);
        _scale = scale;
        Content = shell;
        EstimatedHeight = OverlayVisuals.MeasureShellHeight(shell, Width); // высота до Show

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(300, durationMs)) };
        _timer.Tick += (_, _) => CloseAnimated();
        MouseLeftButtonDown += (_, _) => CloseAnimated();
        Loaded += (_, _) =>
        {
            OverlayVisuals.PlayEntrance(this, _scale);
            _timer.Start();
        };
    }

    private void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)); // закрытие — чистый fade
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.MakeNonActivating(this);
        if (OverlayVisuals.UseAcrylic) WindowHelper.ApplyGlassBackdrop(this);
    }
}

/// <summary>
/// Оверлей-список у курсора. Навигация — временные глобальные хоткеи (цифры/стрелки/Enter/Esc/Del),
/// окно неактивируемое, поэтому выделение в целевом приложении не теряется.
/// </summary>
internal sealed class ListPopupWindow : Window
{
    private static readonly Color RowSelectedBg = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
    private static readonly Color BadgeDim = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);

    private readonly ListPopupRequest _request;
    private readonly IHotkeyManager _hotkeys;
    private readonly IKeyboardHook _hooks;
    private readonly List<RowVisual> _rows = [];
    private readonly ScaleTransform _scale;
    private double _estimatedHeight;
    private int _selected;
    private bool _finished;

    private sealed record RowVisual(SolidColorBrush BodyBrush, SolidColorBrush StripBrush, SolidColorBrush BadgeBrush);

    public ListPopupWindow(ListPopupRequest request, IHotkeyManager hotkeys, IKeyboardHook hooks)
    {
        _request = request;
        _hotkeys = hotkeys;
        _hooks = hooks;

        OverlayVisuals.ConfigureWindow(this);
        Width = 420 + OverlayVisuals.ExtraWidth;
        SizeToContent = SizeToContent.Height;
        Opacity = 0;

        var panel = new StackPanel { Margin = new Thickness(8, 5, 8, 8) };
        if (!string.IsNullOrWhiteSpace(request.Title))
            panel.Children.Add(new TextBlock
            {
                Text = request.Title,
                Foreground = JarvisTheme.AccentBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 0, 6)
            });

        for (int i = 0; i < request.Items.Count; i++)
            panel.Children.Add(BuildRow(request.Items[i], i));

        panel.Children.Add(new TextBlock
        {
            Text = request.OnDelete != null
                ? "↑↓ навигация · 1–9 выбор · Del удалить · Esc отмена"
                : "↑↓ навигация · 1–9 выбор · Esc отмена",
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            FontSize = 10.5,
            Margin = new Thickness(6, 8, 0, 2)
        });

        var (shell, scale) = OverlayVisuals.CreateShell(12, panel);
        _scale = scale;
        Content = shell;

        // позиционируем ДО Show — окно рождается сразу на месте у курсора,
        // без «кадра в углу экрана» и телепорта
        _estimatedHeight = OverlayVisuals.MeasureShellHeight(shell, Width);
        PositionAtCursor();

        _hooks.MouseDown += OnGlobalMouseDown;
        Closed += (_, _) => Cleanup();
        if (_rows.Count > 0) Select(0);
    }

    private UIElement BuildRow(OverlayListItem item, int index)
    {
        var text = new TextBlock
        {
            Text = item.Text,
            Foreground = JarvisTheme.ForegroundBrush(0.92),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var textPanel = new StackPanel();
        textPanel.Children.Add(text);
        if (!string.IsNullOrEmpty(item.Subtitle))
            textPanel.Children.Add(new TextBlock
            {
                Text = item.Subtitle,
                Foreground = JarvisTheme.ForegroundBrush(0.45),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        var badgeBrush = new SolidColorBrush(BadgeDim); // не freeze: цвет анимируется при выборе
        var badge = new TextBlock
        {
            Text = index < 10 ? ((index + 1) % 10).ToString() : "·",
            Foreground = badgeBrush,
            FontSize = 11,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0)
        };

        var bodyBrush = new SolidColorBrush(Colors.Transparent); // transparent ≠ null: кисть hit-testable
        var body = new Border
        {
            Background = bodyBrush,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel { Orientation = Orientation.Horizontal }
        };
        ((StackPanel)body.Child).Children.Add(badge);
        ((StackPanel)body.Child).Children.Add(textPanel);

        var stripBrush = new SolidColorBrush(Colors.Transparent);
        var strip = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 4, 0, 4),
            Background = stripBrush
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(strip, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(strip);
        grid.Children.Add(body);

        body.MouseEnter += (_, _) => Select(index);
        body.MouseLeftButtonUp += (_, _) => Pick(index);

        _rows.Add(new RowVisual(bodyBrush, stripBrush, badgeBrush));
        return grid;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.MakeNonActivating(this);
        if (OverlayVisuals.UseAcrylic) WindowHelper.ApplyGlassBackdrop(this);
        RegisterTempHotkeys();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PositionAtCursor(); // уточнение по фактической высоте — совпадает с измеренной, скачка нет
        OverlayVisuals.PlayEntrance(this, _scale);
    }

    private void PositionAtCursor()
    {
        WinApi.GetCursorPos(out var p);
        double x = p.X / DpiUtil.Scale, y = p.Y / DpiUtil.Scale + 8;
        double h = ActualHeight > 0 ? ActualHeight : _estimatedHeight;
        var wa = SystemParameters.WorkArea;
        if (x + Width > wa.Right - 8) x = wa.Right - Width - 8;
        if (x < wa.Left + 8) x = wa.Left + 8;
        if (y + h > wa.Bottom - 8) y = Math.Max(wa.Top + 8, wa.Bottom - h - 8);
        Left = x;
        Top = y;
    }

    private void RegisterTempHotkeys()
    {
        _hotkeys.Register(Key.Up, Modifiers.None, () => Move(-1), this);
        _hotkeys.Register(Key.Down, Modifiers.None, () => Move(1), this);
        _hotkeys.Register(Key.Return, Modifiers.None, () => Pick(_selected), this);
        _hotkeys.Register(Key.Escape, Modifiers.None, Cancel, this);
        if (_request.OnDelete != null)
            _hotkeys.Register(Key.Delete, Modifiers.None, () => Delete(_selected), this);
        for (int i = 0; i < Math.Min(10, _request.Items.Count); i++)
        {
            int index = i;
            var key = i == 9 ? Key.D0 : (Key)((int)Key.D1 + i);
            _hotkeys.Register(key, Modifiers.None, () => Pick(index), this);
        }
    }

    private void Move(int delta)
    {
        if (_request.Items.Count == 0) return;
        Select((_selected + delta + _request.Items.Count) % _request.Items.Count);
    }

    private void Select(int index)
    {
        _selected = index;
        for (int i = 0; i < _rows.Count; i++)
            AnimateRow(_rows[i], i == _selected);
    }

    private static void AnimateRow(RowVisual row, bool selected)
    {
        var duration = TimeSpan.FromMilliseconds(80);
        row.BodyBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? RowSelectedBg : Colors.Transparent, duration));
        row.StripBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? JarvisTheme.Accent : Colors.Transparent, duration));
        row.BadgeBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? JarvisTheme.Accent : BadgeDim, duration));
    }

    private void Pick(int index)
    {
        if (_finished || index < 0 || index >= _request.Items.Count) return;
        _finished = true;
        var request = _request;
        Close();
        request.OnPick(index);
    }

    private void Cancel()
    {
        if (_finished) return;
        _finished = true;
        var request = _request;
        Close();
        request.OnCancel?.Invoke();
    }

    private void Delete(int index)
    {
        if (_finished || _request.OnDelete == null || index < 0 || index >= _request.Items.Count) return;
        _finished = true;
        var request = _request;
        Close();
        request.OnDelete!.Invoke(index);
    }

    public void ForceClose()
    {
        _finished = true;
        Close();
    }

    private void OnGlobalMouseDown(object? sender, MouseHookEventArgs e)
    {
        double m = OverlayVisuals.UseAcrylic ? 0 : OverlayVisuals.GlowMargin;
        double x = e.X / DpiUtil.Scale, y = e.Y / DpiUtil.Scale;
        bool inside = x >= Left + m && x <= Left + Width - m && y >= Top + m && y <= Top + ActualHeight - m;
        if (!inside) Cancel();
    }

    private void Cleanup()
    {
        _hooks.MouseDown -= OnGlobalMouseDown;
        _hotkeys.UnregisterAll(this);
    }
}