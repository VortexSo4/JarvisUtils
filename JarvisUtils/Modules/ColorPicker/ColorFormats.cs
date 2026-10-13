using System;
using System.Globalization;
using System.Windows.Media;

namespace JarvisUtils.Modules.ColorPicker;

/// <summary>Форматы копирования цвета. В настройках хранится строкой: hex | rgb | hsl.</summary>
internal static class ColorFormats
{
    public const string Hex = "hex", Rgb = "rgb", Hsl = "hsl";
    public static readonly string[] All = [Hex, Rgb, Hsl];

    public static string Format(Color c, string? format) => format switch
    {
        Rgb => ToRgb(c),
        Hsl => ToHsl(c),
        _ => ToHex(c)
    };

    public static string Label(string format) => format switch
    {
        Rgb => "rgb(r, g, b)",
        Hsl => "hsl(h, s%, l%)",
        _ => "HEX (#RRGGBB)"
    };

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string ToRgb(Color c) => $"rgb({c.R}, {c.G}, {c.B})";

    public static string ToHsl(Color c)
    {
        var (h, s, l) = HslOf(c);
        return $"hsl({h}, {s}%, {l}%)";
    }

    /// <summary>HSL в целых: оттенок 0–359°, насыщенность и светлота 0–100%.</summary>
    public static (int H, int S, int L) HslOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        double h = 0, s = 0;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return ((int)Math.Round(h) % 360, (int)Math.Round(s * 100), (int)Math.Round(l * 100));
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Читаемый цвет текста поверх заливки (по относительной яркости).</summary>
    public static Color ContrastText(Color c) =>
        0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 150 ? Color.FromRgb(0x10, 0x12, 0x18) : Colors.White;
}
