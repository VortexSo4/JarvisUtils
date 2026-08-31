using System.Collections.Generic;
using System.Threading.Tasks;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.LayoutFixer;

public sealed class LayoutFixerModule : JarvisModuleBase, IHotkeyConfigurable
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Alt+F12";
    }

    private Settings _settings = null!;

    public override string DisplayName => "Исправление раскладки";

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<Settings>();
        RegisterHotkey(_settings.Hotkey, async () => await FixAsync());
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new HotkeySlot("Исправить раскладку выделенного текста", () => _settings.Hotkey,
            g => { _settings.Hotkey = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    private async Task FixAsync()
    {
        var text = await Services.Selection.GetSelectedTextAsync(); // любой объём: слово, абзац, весь текст
        if (string.IsNullOrEmpty(text))
        {
            Services.Overlay.ShowToast("Нет выделенного текста", "Layout Fixer");
            return;
        }
        var converted = LayoutMap.Convert(text);
        if (converted == text)
        {
            Services.Overlay.ShowToast("Перекодировка не изменила текст", "Layout Fixer");
            return;
        }
        Services.Input.ReplaceSelection(converted);
        Services.Overlay.ShowToast(TextUtil.Preview(converted), "Раскладка исправлена");
    }
}