using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
    private const int MaxQueuedLines = 8192;

    // Лог вызывается в том числе из коллбэков low-level хуков (catch-ветки
    // HookManager, ретраи буфера). Раньше каждая запись делала синхронный
    // File.AppendAllText с открытием файла — медленный диск/антивирус мог
    // поставить UI-поток (а с ним и ввод всей системы) на секунды.
    // Теперь Write() только ставит строку в очередь, пишет фоновый поток.
    private static readonly ConcurrentQueue<string> _pending = new();
    private static readonly AutoResetEvent _wake = new(false);

    static Log()
    {
        var writer = new Thread(WriterLoop) { Name = "JarvisUtils.Log", IsBackground = true };
        writer.Start();
    }

    public static void Info(string m) => Write("INFO ", m);
    public static void Warn(string m) => Write("WARN ", m);
    public static void Error(string m) => Write("ERROR", m);

    private static void Write(string level, string message)
    {
        try
        {
            if (_pending.Count >= MaxQueuedLines) return; // переполнение — теряем строки, но не память
            _pending.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            _wake.Set();
        }
        catch { /* логирование не должно ронять приложение */ }
    }

    private static void WriterLoop()
    {
        var batch = new StringBuilder(8192);
        while (true)
        {
            _wake.WaitOne();
            batch.Clear();
            while (_pending.TryDequeue(out var line)) batch.Append(line);
            if (batch.Length == 0) continue;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.LogFile, batch.ToString());
            }
            catch { /* логирование не должно ронять приложение */ }
        }
    }

    /// <summary>Дождаться вывода накопленного (вызывать при выходе из приложения).</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!_pending.IsEmpty && Environment.TickCount64 < deadline) Thread.Sleep(10);
        Thread.Sleep(30); // даём дописать пакет, который уже забрали из очереди
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