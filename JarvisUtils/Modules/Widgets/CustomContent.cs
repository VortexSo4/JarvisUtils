using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

/// <summary>
/// «Свой» виджет: XAML с привязками к WidgetContext + необязательный C#-скрипт.
/// Используется и в окне виджета, и в живом превью редактора.
/// </summary>
internal sealed class CustomContent : IWidgetContent
{
    private static readonly Regex MetricsUsage = new(@"\b(Cpu|Ram|NetDown|NetUp|DiskFree|Battery|Uptime|Sys)\b", RegexOptions.Compiled);

    private readonly WidgetModel _model;
    private readonly IJarvisServices _services;
    private readonly Action _save;
    private readonly Grid _root = new();
    private readonly Border _errorBadge;
    private WidgetContext? _context;
    private WidgetApi? _api;
    private bool _disposed;

    public FrameworkElement View => _root;
    public bool NeedsKeyboard { get; private set; }
    public double? AspectRatio => null;

    /// <summary>Ошибка разбора XAML или выполнения скрипта (null — всё в порядке).</summary>
    public string? Error { get; private set; }
    public event Action? ErrorChanged;

    /// <param name="runScript">false — только XAML (превью редактора до явного запуска).</param>
    public CustomContent(WidgetModel model, IJarvisServices services, Action save, bool runScript = true)
    {
        _model = model;
        _services = services;
        _save = save;

        // привязки со StringFormat форматируют по культуре элемента, а она по умолчанию en-US
        _root.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);

        _errorBadge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromRgb(0xC9, 0x3C, 0x3C)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Child = FluentIcons.Glyph(FluentIcons.Warning, 12, Brushes.White),
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(_errorBadge, 10);

        Load(runScript);
    }

    private void Load(bool runScript)
    {
        _context = new WidgetContext(_services);
        if (MetricsUsage.IsMatch(_model.Xaml) || MetricsUsage.IsMatch(_model.Script)) _context.EnableMetrics();

        FrameworkElement? element = null;
        try
        {
            element = Parse(_model.Xaml);
        }
        catch (Exception ex)
        {
            SetError("XAML, " + (ex is InvalidOperationException { InnerException: not null } ? ex.Message : ex.GetBaseException().Message));
        }

        _root.Children.Clear();
        if (element != null)
        {
            element.DataContext = _context;
            _root.Children.Add(element);
            NeedsKeyboard = FindAll<TextBoxBase>(element).Any();
        }
        _root.Children.Add(_errorBadge);

        if (runScript && element != null && !string.IsNullOrWhiteSpace(_model.Script))
            _ = RunScriptAsync(element);
    }

    /// <summary>
    /// XAML без xmlns допустим. Разметка оборачивается в Grid с темой приложения: так стили
    /// (кнопки, поля) применяются, а {StaticResource …} находит их прямо при разборе.
    /// Обёртка — в той же строке, что и начало разметки: номера строк в ошибках совпадают.
    /// </summary>
    public static FrameworkElement Parse(string xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml))
            return new Grid();
        var text = Regex.Replace(xaml.Trim(), @"^<\?xml[^>]*\?>", "");
        var prefix =
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            $"<Grid.Resources><ResourceDictionary Source=\"{WidgetUi.ThemeUri}\"/></Grid.Resources>";
        try
        {
            return (FrameworkElement)XamlReader.Parse(prefix + text + "</Grid>");
        }
        catch (Exception ex) when (FindPosition(ex) is var (line, column) && line > 0)
        {
            // позиция в первой строке сдвинута обёрткой — пересчитываем под текст пользователя
            if (line == 1) column = Math.Max(1, column - prefix.Length);
            var message = Regex.Replace(ex.GetBaseException().Message, @"\s*(Line \d+, position \d+\.?|номер строки.*)$", "");
            throw new InvalidOperationException($"строка {line}, позиция {column}: {message}", ex);
        }
    }

    private static (int Line, int Column) FindPosition(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is XamlParseException xp && xp.LineNumber > 0) return (xp.LineNumber, xp.LinePosition);
            if (e is System.Xml.XmlException xe && xe.LineNumber > 0) return (xe.LineNumber, xe.LinePosition);
        }
        return (0, 0);
    }

    private async Task RunScriptAsync(FrameworkElement element)
    {
        try
        {
            var runner = await WidgetScriptCompiler.CompileAsync(_model.Script); // компиляция — в фоне
            if (_disposed) return;
            _api = new WidgetApi(_context!, element, _model, _services, _save, SetError);
            await runner(_api);
        }
        catch (Exception ex)
        {
            SetError("Скрипт: " + ex.GetBaseException().Message);
        }
    }

    private void SetError(string? message)
    {
        Error = message;
        _errorBadge.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        _errorBadge.ToolTip = message;
        if (message != null) Log.Warn($"виджет «{_model.Name}»: {message}");
        ErrorChanged?.Invoke();
    }

    private static System.Collections.Generic.IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    public void BuildMenu(ContextMenu menu) { } // «Редактировать…» добавляет окно-хост

    public void Dispose()
    {
        _disposed = true;
        _api?.Dispose();
        _context?.Dispose();
    }
}
