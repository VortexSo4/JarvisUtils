using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JarvisUtils.Core;

/// <summary>
/// Иконки из шрифта Segoe Fluent Icons (Win11; на Win10 — фолбэк на Segoe MDL2 Assets,
/// коды глифов у них в основном совпадают). Новые глифы сверять с таблицей:
/// https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font
/// </summary>
public static class FluentIcons
{
    public static readonly FontFamily Family = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // ── действия ──
    public const string Add = "";
    public const string Close = "";
    public const string Delete = "";
    public const string Edit = "";
    public const string Rename = "";
    public const string Save = "";
    public const string Copy = "";
    public const string Paste = "";
    public const string Cut = "";
    public const string Undo = "";
    public const string Refresh = "";
    public const string Search = "";
    public const string Filter = "";
    public const string Sort = "";
    public const string More = "";
    public const string Check = "";
    public const string Play = "";
    public const string Import = "";
    public const string Export = "";
    public const string OpenInWindow = "";
    public const string Duplicate = "";
    public const string Pin = "";
    public const string Unpin = "";
    public const string Lock = "";
    public const string Unlock = "";
    public const string View = "";
    public const string Hide = "";
    public const string ChevronUp = "";
    public const string ChevronDown = "";
    public const string ChevronRight = "";

    // ── объекты ──
    public const string Settings = "";
    public const string Folder = "";
    public const string NewFolder = "";
    public const string History = "";
    public const string Clock = "";
    public const string Calendar = "";
    public const string Stopwatch = "";
    public const string Calculator = "";
    public const string Keyboard = "";
    public const string Camera = "";
    public const string Screenshot = "";
    public const string Photo = "";
    public const string Note = "";
    public const string Palette = "";
    public const string Eyedropper = "";
    public const string Code = "";
    public const string Font = "";
    public const string Language = "";
    public const string Widgets = "";
    public const string Tiles = "";
    public const string Layers = "";
    public const string Sliders = "";
    public const string List = "";
    public const string ClipboardList = "";
    public const string Star = "";
    public const string StarFill = "";
    public const string Lightning = "";
    public const string Cpu = "";
    public const string Pulse = "";
    public const string Monitor = "";
    public const string Puzzle = "";
    public const string Power = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Help = "";
    public const string Document = "";

    /// <summary>Глиф как TextBlock (наследует Foreground, если не задан).</summary>
    public static TextBlock Glyph(string glyph, double size = 14, Brush? foreground = null)
    {
        var tb = new TextBlock
        {
            Text = glyph,
            FontFamily = Family,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        if (foreground != null) tb.Foreground = foreground;
        return tb;
    }

    /// <summary>Иконка + подпись в одну строку — для содержимого кнопок и пунктов меню.</summary>
    public static StackPanel WithLabel(string glyph, string label, double size = 13)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Glyph(glyph, size));
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }
}
