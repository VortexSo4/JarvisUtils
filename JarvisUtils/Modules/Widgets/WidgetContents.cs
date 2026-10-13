using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

/// <summary>Содержимое виджета. Окно-хост (WidgetWindow) отвечает за всё остальное.</summary>
internal interface IWidgetContent : IDisposable
{
    FrameworkElement View { get; }
    /// <summary>Окну нужен клавиатурный фокус (иначе оно не активируется кликом).</summary>
    bool NeedsKeyboard { get; }
    /// <summary>Сохранять пропорции при ресайзе (ширина / высота) или null.</summary>
    double? AspectRatio { get; }
    /// <summary>Пункты меню, специфичные для вида, — в начало контекстного меню.</summary>
    void BuildMenu(ContextMenu menu);
}

internal static class WidgetUi
{
    public const string ThemeUri = "pack://application:,,,/JarvisUtils;component/UI/Theme/JarvisStyles.xaml";

    /// <summary>
    /// Тема приложения. Глобальных стилей нет (App создаётся без InitializeComponent) —
    /// окна виджетов подключают её сами; один экземпляр на всех.
    /// </summary>
    public static readonly ResourceDictionary Theme = new() { Source = new Uri(ThemeUri) };

    /// <summary>TextBox без рамки и фона — тема приложения рисует поле ввода с рамкой.</summary>
    public static readonly ControlTemplate BareTextBox = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate TargetType='TextBox' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><ScrollViewer x:Name='PART_ContentHost' " +
        "Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'/></ControlTemplate>");

    public static MenuItem Item(string header, string? glyph, Action onClick, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (glyph != null) item.Icon = FluentIcons.Glyph(glyph, 14);
        item.Click += (_, _) => onClick();
        return item;
    }

    public static MenuItem Check(string header, bool isChecked, Action<bool> onChange)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => onChange(item.IsChecked);
        return item;
    }

    public static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }
}

// ═══════════════════════════ Заметка ═══════════════════════════

internal sealed class NoteContent : IWidgetContent
{
    private const double MinFont = 9, MaxFont = 40;

    private readonly WidgetModel _model;
    private readonly WidgetWindow _host;
    private readonly Border _root;
    private readonly TextBox _text;
    private readonly TextBlock _placeholder;

    public FrameworkElement View => _root;
    public bool NeedsKeyboard => true;
    public double? AspectRatio => null;

    public NoteContent(WidgetModel model, WidgetWindow host)
    {
        _model = model;
        _host = host;

        _text = new TextBox
        {
            Template = WidgetUi.BareTextBox,
            Text = model.Text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 0, 12, 10),
            FontSize = model.FontSize,
            FontFamily = new FontFamily("Segoe UI")
        };
        _text.TextChanged += (_, _) =>
        {
            _model.Text = _text.Text;
            _placeholder!.Visibility = _text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _host.ModelChanged();
        };
        _text.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; _host.ReleaseFocus(); }
        };
        _text.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            SetFontSize(_model.FontSize + (e.Delta > 0 ? 1 : -1));
        };

        _placeholder = new TextBlock
        {
            Text = "Заметка…",
            Margin = new Thickness(14, 0, 12, 0),
            FontSize = model.FontSize,
            IsHitTestVisible = false,
            Visibility = model.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };

        // полоса-«ручка» сверху: за неё заметку тянут (TextBox забирает мышь себе)
        var grip = new Border { Height = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        DockPanel.SetDock(grip, Dock.Top);

        var textHost = new Grid();
        textHost.Children.Add(_text);
        textHost.Children.Add(_placeholder);

        var dock = new DockPanel();
        dock.Children.Add(grip);
        dock.Children.Add(textHost);

        _root = new Border { CornerRadius = new CornerRadius(10), Child = dock };
        ApplyColor();
    }

    /// <summary>Сразу в режим ввода — для только что созданной заметки.</summary>
    public void FocusText()
    {
        _text.Focus();
        Keyboard.Focus(_text);
        _text.CaretIndex = _text.Text.Length;
    }

    private void ApplyColor()
    {
        var color = NotePalette.Find(_model.Color);
        var bg = WidgetUi.ParseColor(color.Hex, Colors.DimGray);
        _root.Background = new SolidColorBrush(Color.FromArgb(0xF2, bg.R, bg.G, bg.B));
        _root.BorderBrush = new SolidColorBrush(color.Light ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _root.BorderThickness = new Thickness(1);
        var fg = color.Light ? Color.FromRgb(0x1E, 0x20, 0x26) : Color.FromRgb(0xEC, 0xEE, 0xF3);
        _text.Foreground = new SolidColorBrush(fg);
        _text.CaretBrush = new SolidColorBrush(fg);
        _text.SelectionBrush = new SolidColorBrush(color.Light ? Color.FromArgb(0x60, 0, 0, 0) : JarvisTheme.Accent);
        _placeholder.Foreground = new SolidColorBrush(Color.FromArgb(0x70, fg.R, fg.G, fg.B));
        _host.SetToolbarTone(color.Light);
    }

    private void SetFontSize(double size)
    {
        _model.FontSize = Math.Clamp(size, MinFont, MaxFont);
        _text.FontSize = _model.FontSize;
        _placeholder.FontSize = _model.FontSize;
        _host.ModelChanged();
    }

    public void BuildMenu(ContextMenu menu)
    {
        var colors = new MenuItem { Header = "Цвет", Icon = FluentIcons.Glyph(FluentIcons.Palette, 14) };
        foreach (var c in NotePalette.All)
        {
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(WidgetUi.ParseColor(c.Hex, Colors.Gray)),
                BorderBrush = JarvisTheme.ForegroundBrush(0.3), BorderThickness = new Thickness(1)
            };
            var item = new MenuItem { Header = c.Name, Icon = swatch, IsCheckable = false };
            if (string.Equals(c.Hex, _model.Color, StringComparison.OrdinalIgnoreCase)) item.FontWeight = FontWeights.SemiBold;
            var chosen = c;
            item.Click += (_, _) => { _model.Color = chosen.Hex; ApplyColor(); _host.ModelChanged(); };
            colors.Items.Add(item);
        }
        menu.Items.Add(colors);
        menu.Items.Add(WidgetUi.Item("Крупнее", FluentIcons.ChevronUp, () => SetFontSize(_model.FontSize + 2), "Ctrl+колесо"));
        menu.Items.Add(WidgetUi.Item("Мельче", FluentIcons.ChevronDown, () => SetFontSize(_model.FontSize - 2)));
        menu.Items.Add(WidgetUi.Item("Копировать текст", FluentIcons.Copy, () => _host.Services.Clipboard.SetText(_model.Text)));
    }

    public void Dispose() { }
}

// ═══════════════════════════ Картинка ═══════════════════════════

internal sealed class ImageContent : IWidgetContent
{
    private readonly WidgetModel _model;
    private readonly WidgetWindow _host;
    private readonly Border _root;
    private readonly BitmapSource? _image;

    public FrameworkElement View => _root;
    public bool NeedsKeyboard => false;
    public double? AspectRatio => _image != null && _image.PixelHeight > 0 ? (double)_image.PixelWidth / _image.PixelHeight : null;

    /// <param name="preloaded">Только что снятый скриншот — не ждём чтения с диска.</param>
    public ImageContent(WidgetModel model, WidgetWindow host, BitmapSource? preloaded = null)
    {
        _model = model;
        _host = host;
        _image = preloaded ?? Load(model.ImageFile);

        FrameworkElement inner = _image != null
            ? new Image { Source = _image, Stretch = Stretch.Fill }
            : new TextBlock
            {
                Text = "Файл картинки не найден",
                Foreground = JarvisTheme.ForegroundBrush(0.6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        RenderOptions.SetBitmapScalingMode(inner, BitmapScalingMode.HighQuality);

        _root = new Border
        {
            Background = JarvisTheme.Solid(JarvisTheme.Bg),
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.45),
            BorderThickness = new Thickness(1),
            Child = inner
        };
        _root.PreviewMouseWheel += OnWheel;
    }

    public static BitmapSource? Load(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        var path = Path.Combine(WidgetPaths.ImagesDir, file);
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // файл не держим открытым — его можно удалить
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) { Log.Warn($"виджет-картинка: {ex.Message}"); return null; }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _host.SetOpacity(_model.Opacity + (e.Delta > 0 ? 0.1 : -0.1));
            return;
        }
        if (_model.Locked) return;
        double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        _host.ZoomAroundCursor(factor);
    }

    public void BuildMenu(ContextMenu menu)
    {
        if (_image == null) return;
        menu.Items.Add(WidgetUi.Item("Копировать картинку", FluentIcons.Copy, () => _host.Services.Clipboard.SetImage(_image)));
        menu.Items.Add(WidgetUi.Item("Сохранить как…", FluentIcons.Save, SaveAs));
        menu.Items.Add(WidgetUi.Item("Исходный размер", FluentIcons.Photo, () =>
            _host.ResizeTo(_image.PixelWidth / DpiUtil.Scale, _image.PixelHeight / DpiUtil.Scale), "колесо — масштаб"));
    }

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_model.Name}.png",
            Filter = "PNG|*.png|JPEG|*.jpg"
        };
        if (dialog.ShowDialog() != true || _image == null) return;
        try
        {
            BitmapEncoder encoder = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_image));
            using var fs = File.Create(dialog.FileName);
            encoder.Save(fs);
        }
        catch (Exception ex) { _host.Services.Overlay.ShowToast("Не удалось сохранить: " + ex.Message, "Виджеты"); }
    }

    public void Dispose() { }
}
