using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.PlainTextPaste;

public sealed class PlainTextPasteModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor, IQuickActions
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Ctrl+Alt+V";
    }

    private Settings _settings = null!;

    public override string DisplayName => "Вставка без форматирования";
    public override string Icon => FluentIcons.Paste;

    public IReadOnlyList<QuickAction> QuickActions =>
    [
        new("Вставить без форматирования", FluentIcons.Paste, PastePlain)
    ];

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<Settings>();
        RegisterHotkey(_settings.Hotkey, PastePlain);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new HotkeySlot("Вставить как plain text", () => _settings.Hotkey,
            g => { _settings.Hotkey = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    private void PastePlain()
    {
        if (!Services.Clipboard.ContainsText)
        {
            Services.Overlay.ShowToast("В буфере обмена нет текста", "Paste as Plain Text");
            return;
        }
        var raw = Services.Clipboard.GetText() ?? "";
        var clean = PlainTextRules.Load().Apply(raw); // правила читаются с диска — правки подхватываются без перезапуска

        // только text-формат; исходный буфер (RTF/HTML/картинка) восстановится после вставки
        Services.Input.PasteText(clean);
        Services.Overlay.ShowToast("Вставлено без форматирования", "Paste as Plain Text");
    }

    public FrameworkElement BuildEditor()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "Правила очистки markdown хранятся в редактируемом JSON (применяются по порядку):",
            Foreground = JarvisTheme.ForegroundBrush(0.6),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = PlainTextRules.FilePath,
            Foreground = JarvisTheme.ForegroundBrush(0.75),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });

        var open = new Button { Content = "Открыть файл правил" };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{PlainTextRules.FilePath}\"") { UseShellExecute = true });

        var reset = new Button { Content = "Сбросить к дефолтным", Margin = new Thickness(8, 0, 0, 0) };
        reset.Click += (_, _) => { PlainTextRules.ResetToDefault(); Services.Overlay.ShowToast("Правила сброшены", "Paste as Plain Text"); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(open);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);
        return panel;
    }
}