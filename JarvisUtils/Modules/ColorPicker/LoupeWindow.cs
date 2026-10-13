using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ColorPicker;

/// <summary>
/// Лупа у курсора: увеличенная сетка пикселей вокруг точки, центральный пиксель в рамке,
/// под ней — образец и HEX/RGB. Прозрачна для мыши и не активируется.
/// </summary>
internal sealed class LoupeWindow : Window
{
    private const double Cell = 14;     // DIP на один экранный пиксель
    private const double CursorGap = 22; // отступ от курсора — лупа не закрывает то, что выбираем

    private readonly int _size;
    private readonly WriteableBitmap _bitmap;
    private readonly int[] _buffer;
    private readonly SolidColorBrush _swatchBrush = new(Colors.Black); // не freeze: цвет меняется каждый кадр
    private readonly TextBlock _hex;
    private readonly TextBlock _rgb;

    public LoupeWindow(int size)
    {
        _size = size;
        _buffer = new int[size * size];

        ShowActivated = false;
        Focusable = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; // layered: прозрачные скругления + клики насквозь (WS_EX_TRANSPARENT ниже)
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;

        double gridSide = size * Cell;
        _bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgr32, null);
        var zoom = new Image { Source = _bitmap, Width = gridSide, Height = gridSide, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor); // чёткие «квадратики», без сглаживания

        var grid = new Grid { Width = gridSide, Height = gridSide, ClipToBounds = true };
        grid.Children.Add(zoom);
        grid.Children.Add(GridLines(size, gridSide));
        // центральный пиксель: двойная рамка видна и на светлом, и на тёмном
        double c = size / 2 * Cell;
        grid.Children.Add(new Rectangle { Width = Cell + 2, Height = Cell + 2, Stroke = Brushes.Black, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(c - 1, c - 1, 0, 0) });
        grid.Children.Add(new Rectangle { Width = Cell, Height = Cell, Stroke = Brushes.White, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(c, c, 0, 0) });

        var zoomFrame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = JarvisTheme.Solid(Colors.White, 0.12),
            Child = grid,
            ClipToBounds = true
        };

        var swatch = new Border
        {
            Background = _swatchBrush,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = JarvisTheme.Solid(Colors.White, 0.25),
            Margin = new Thickness(0, 0, 10, 0)
        };
        _hex = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas, Cascadia Mono"), Foreground = JarvisTheme.ForegroundBrush(0.95) };
        _rgb = new TextBlock { FontSize = 11, FontFamily = new FontFamily("Consolas, Cascadia Mono"), Foreground = JarvisTheme.ForegroundBrush(0.55) };
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(_hex);
        labels.Children.Add(_rgb);
        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 9, 2, 0) };
        info.Children.Add(swatch);
        info.Children.Add(labels);

        var hint = new TextBlock
        {
            Text = "клик или Enter — копировать\nстрелки — 1 px · Esc — отмена",
            FontSize = 10,
            Foreground = JarvisTheme.ForegroundBrush(0.38),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0)
        };

        // ширину задаёт сетка пикселей (+рамка), подписи под ней переносятся, а не растягивают лупу
        var panel = new StackPanel { Margin = new Thickness(9), Width = gridSide + 2 };
        panel.Children.Add(zoomFrame);
        panel.Children.Add(info);
        panel.Children.Add(hint);

        Content = new Border
        {
            Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.94),
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.45),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = panel
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        const int WS_EX_TRANSPARENT = 0x20;
        WinApi.SetWindowLong(hwnd, WinApi.GWL_EXSTYLE,
            WinApi.GetWindowLong(hwnd, WinApi.GWL_EXSTYLE) | WinApi.WS_EX_NOACTIVATE | WinApi.WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        PickerSurface.ExcludeFromCapture(hwnd);
    }

    /// <summary>Обновить сетку и подписи. pixels — 0x00RRGGBB построчно, size×size.</summary>
    public void Update(uint[] pixels, uint center)
    {
        for (int i = 0; i < _buffer.Length; i++) _buffer[i] = (int)pixels[i];
        _bitmap.WritePixels(new Int32Rect(0, 0, _size, _size), _buffer, _size * 4, 0);

        var color = Color.FromRgb((byte)(center >> 16), (byte)(center >> 8), (byte)center);
        _swatchBrush.Color = color;
        _hex.Text = ColorFormats.ToHex(color);
        _rgb.Text = $"{color.R}, {color.G}, {color.B}";
    }

    /// <summary>
    /// Встать рядом с курсором (координаты курсора — логические, в системе координат
    /// приложения), не вылезая за рабочую область его монитора.
    /// </summary>
    public void MoveNear(WinApi.POINT cursor)
    {
        double scale = DpiUtil.Scale;
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        double x = cursor.X / scale + CursorGap, y = cursor.Y / scale + CursorGap;
        double w = ActualWidth > 0 ? ActualWidth : _size * Cell + 20, h = ActualHeight > 0 ? ActualHeight : _size * Cell + 70;
        if (x + w > wa.Right / scale) x = cursor.X / scale - CursorGap - w; // у правого края — слева от курсора
        if (y + h > wa.Bottom / scale) y = cursor.Y / scale - CursorGap - h; // у нижнего — сверху
        Left = Math.Max(wa.Left / scale, x);
        Top = Math.Max(wa.Top / scale, y);
    }

    private static UIElement GridLines(int size, double side)
    {
        var geometry = new GeometryGroup();
        for (int i = 1; i < size; i++)
        {
            double p = i * Cell;
            geometry.Children.Add(new LineGeometry(new Point(p, 0), new Point(p, side)));
            geometry.Children.Add(new LineGeometry(new Point(0, p), new Point(side, p)));
        }
        geometry.Freeze();
        return new Path { Data = geometry, Stroke = JarvisTheme.Solid(Colors.Black, 0.18), StrokeThickness = 1, SnapsToDevicePixels = true };
    }
}
