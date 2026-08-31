using System.Windows;
using System.Windows.Controls;
using JarvisUtils.Core;

namespace JarvisUtils.UI.Settings;

/// <summary>Общие UI-примитивы страниц настроек (заголовки секций и т.п.).</summary>
internal static class SettingsUi
{
    internal static TextBlock Section(string title) => new()
    {
        Text = title,
        Foreground = JarvisTheme.ForegroundBrush(0.42),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 22, 0, 8)
    };
}