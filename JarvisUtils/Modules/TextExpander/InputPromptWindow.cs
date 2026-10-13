using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Маленькое окно ввода для {input(Подпись)}. В отличие от списка выбора, ему нужен фокус
/// клавиатуры — поэтому окно активируемое; исходному окну фокус возвращает модуль.
/// </summary>
internal sealed class InputPromptWindow : Window
{
    private const double WindowWidth = 380;

    private readonly TaskCompletionSource<string?> _result = new();
    private readonly TextBox _box;
    private readonly ScaleTransform _scale;
    private bool _finished;

    /// <summary>Окно закрылось из-за клика мимо (фокус ушёл в другое место) — возвращать фокус и печатать нельзя.</summary>
    public bool FocusLost { get; private set; }

    public Task<string?> Result => _result.Task;

    public InputPromptWindow(string title, string label, string? initial)
    {
        ShowActivated = true;
        Focusable = true;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = WindowWidth + OverlayVisuals.ExtraWidth;
        SizeToContent = SizeToContent.Height;
        Opacity = 0;
        // acrylic: окно НЕ layered (layered + DWM-материал = серый прямоугольник), glass-фрейм на всё окно
        AllowsTransparency = !OverlayVisuals.UseAcrylic;
        if (!AllowsTransparency)
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0)
            });

        var panel = new StackPanel { Margin = new Thickness(14, 9, 14, 11) };
        if (!string.IsNullOrWhiteSpace(title))
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = JarvisTheme.AccentBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 0, 4)
            });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = JarvisTheme.ForegroundBrush(0.85),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 7)
        });

        _box = new TextBox { Text = initial ?? "", FontSize = 14 };
        panel.Children.Add(_box);
        panel.Children.Add(new TextBlock
        {
            Text = "Enter — вставить · Esc — отмена",
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 10.5,
            Margin = new Thickness(2, 7, 0, 0)
        });

        var (shell, scale) = OverlayVisuals.CreateShell(8, panel);
        _scale = scale;
        if (OverlayVisuals.UseAcrylic) shell.Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.82);
        Content = shell;

        PositionAtCursor(OverlayVisuals.MeasureShellHeight(shell, Width));

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) =>
        {
            if (_finished) return;
            FocusLost = true;
            Finish(null);
        };
        Loaded += (_, _) =>
        {
            WindowHelper.ForceForeground(this); // иначе печать уйдёт в прежнее приложение
            _box.Focus();
            Keyboard.Focus(_box);
            _box.SelectAll();
        };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Finish(null); }
        else if (e.Key == Key.Return) { e.Handled = true; Finish(_box.Text); }
    }

    private void Finish(string? value)
    {
        if (_finished) return;
        _finished = true;
        _result.TrySetResult(value);
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (OverlayVisuals.UseAcrylic) WindowHelper.ApplyGlassBackdrop(this);
        // не показываться в Alt+Tab
        var hwnd = new WindowInteropHelper(this).Handle;
        WinApi.SetWindowLong(hwnd, WinApi.GWL_EXSTYLE, WinApi.GetWindowLong(hwnd, WinApi.GWL_EXSTYLE) | WinApi.WS_EX_TOOLWINDOW);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        OverlayVisuals.PlayEntrance(this, _scale);
    }

    private void PositionAtCursor(double height)
    {
        WinApi.GetCursorPos(out var p);
        double x = p.X / DpiUtil.Scale, y = p.Y / DpiUtil.Scale + 8;
        var wa = SystemParameters.WorkArea;
        if (x + Width > wa.Right - 8) x = wa.Right - Width - 8;
        if (x < wa.Left + 8) x = wa.Left + 8;
        if (y + height > wa.Bottom - 8) y = Math.Max(wa.Top + 8, wa.Bottom - height - 8);
        Left = x;
        Top = y;
    }
}
