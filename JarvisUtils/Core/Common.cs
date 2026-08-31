using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace JarvisUtils.Core;

public static class AppPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarvisUtils");
    public static readonly string SettingsDir = Path.Combine(DataDir, "settings");
    public static readonly string RulesDir = Path.Combine(DataDir, "rules");
    public static readonly string ClipboardDir = Path.Combine(DataDir, "clipboard");
    public static readonly string ClipboardImagesDir = Path.Combine(ClipboardDir, "img");
    public static readonly string LogFile = Path.Combine(DataDir, "jarvis.log");
}

public static class Log
{
    private static readonly object Gate = new();
    public static void Info(string m) => Write("INFO ", m);
    public static void Warn(string m) => Write("WARN ", m);
    public static void Error(string m) => Write("ERROR", m);
    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* логирование не должно ронять приложение */ }
    }
}

public static class DpiUtil
{
    private static double? _scale;
    /// <summary>Масштаб системного DPI (96 dpi = 1.0).</summary>
    public static double Scale => _scale ??= GetScale();
    private static double GetScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }
}

public static class JarvisTheme
{
    public static readonly Color Accent = Color.FromRgb(0x00, 0xD2, 0xFF);
    public static readonly Color Bg = Color.FromRgb(0x16, 0x18, 0x20);

    public static SolidColorBrush AccentBrush => Solid(Accent);
    public static SolidColorBrush AccentBorder(byte alpha = 0x55) =>
        Solid(Color.FromArgb(alpha, Accent.R, Accent.G, Accent.B));
    public static SolidColorBrush Solid(Color color, double opacity = 1.0)
    {
        var b = new SolidColorBrush { Color = color, Opacity = opacity };
        b.Freeze();
        return b;
    }
    public static SolidColorBrush ForegroundBrush(double opacity = 1.0) => Solid(Colors.White, opacity);
}

public static class TextUtil
{
    public static string Preview(string? text, int max = 64)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var one = Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}