using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JarvisUtils.Core;

namespace JarvisUtils.UI.Settings;

/// <summary>
/// Вкладка «Общие» — настройки уровня приложения, а не отдельного модуля.
/// Пока только доступ к логу/данным; сюда же позже лягут автозапуск и прочее
/// приложение-уровневое (страница специально оформлена как отдельный класс).
/// </summary>
public sealed class GeneralSettingsPage
{
    public FrameworkElement BuildEditor()
    {
        var root = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        root.Children.Add(new TextBlock
        {
            Text = "Настройки, правила и лог приложения хранятся локально:",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(SettingsUi.Section("ДИАГНОСТИКА"));

        var open = new Button
        {
            Content = "Открыть папку с логом",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        open.Click += (_, _) => OpenLogLocation();
        root.Children.Add(open);

        root.Children.Add(new TextBlock
        {
            Text = AppPaths.LogFile,
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        return root;
    }

    /// <summary>
    /// explorer /select — открывает папку данных с уже выделенным jarvis.log:
    /// сразу видно и директорию (что в ней лежит), и сам файл (Enter/двойной клик
    /// по выделению открывает лог в редакторе). Если лога ещё нет — просто папка.
    /// </summary>
    private static void OpenLogLocation()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir); // на случай, если лог ни разу не писался
            if (File.Exists(AppPaths.LogFile))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppPaths.LogFile}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"open log folder: {ex.Message}");
        }
    }
}