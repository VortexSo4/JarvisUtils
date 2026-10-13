using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

/// <summary>
/// Рамка «сюда прилипнет»: показывается, пока виджет тащат рядом с краем экрана или
/// соседом. Одно окно на всех; клики проходят насквозь, фокус не берёт.
/// </summary>
internal static class SnapPreview
{
    private const int WS_EX_TRANSPARENT = 0x20;

    private static Window? _window;

    /// <summary>Координаты — экранные пиксели (как в WM_MOVING).</summary>
    public static void ShowAt(int x, int y, int width, int height)
    {
        var w = _window ??= Create();
        double s = DpiUtil.Scale;
        w.Left = x / s;
        w.Top = y / s;
        w.Width = Math.Max(1, width / s);
        w.Height = Math.Max(1, height / s);
        if (!w.IsVisible) w.Show();
    }

    public static void HideNow() => _window?.Hide();

    private static Window Create()
    {
        var accent = JarvisTheme.Accent;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B)),
                Background = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B))
            }
        };
        window.SourceInitialized += (_, _) =>
        {
            WindowHelper.MakeNonActivating(window);
            var hwnd = new WindowInteropHelper(window).Handle;
            WinApi.SetWindowLong(hwnd, WinApi.GWL_EXSTYLE, WinApi.GetWindowLong(hwnd, WinApi.GWL_EXSTYLE) | WS_EX_TRANSPARENT);
        };
        return window;
    }
}
