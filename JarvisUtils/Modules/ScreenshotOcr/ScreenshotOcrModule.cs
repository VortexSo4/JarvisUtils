using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ScreenshotOcr;

public sealed class ScreenshotOcrModule : JarvisModuleBase, IHotkeyConfigurable, IQuickActions
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Ctrl+Alt+O";
    }

    private Settings _settings = null!;

    public override string DisplayName => "Скриншот OCR";
    public override string Icon => FluentIcons.Screenshot;

    public IReadOnlyList<QuickAction> QuickActions =>
    [
        new("Распознать текст", FluentIcons.Language, StartCapture)
    ];

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<Settings>();
        RegisterHotkey(_settings.Hotkey, StartCapture);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new HotkeySlot("Скриншот области → OCR → буфер", () => _settings.Hotkey,
            g => { _settings.Hotkey = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    private void StartCapture() =>
        ScreenCapture.SelectRegion(Services.Hotkeys, (_, crop, _) => _ = OcrAsync(crop));

    private async Task OcrAsync(BitmapSource crop)
    {
        try
        {
            using var gdi = ToGdiBitmap(crop);
            var text = await OcrHelper.RecognizeAsync(gdi);
            if (string.IsNullOrWhiteSpace(text))
            {
                Services.Overlay.ShowToast("Текст не распознан (проверьте языковой пакет OCR)", "Screenshot OCR", 2500);
                return;
            }
            Services.Clipboard.SetText(text);
            Services.Overlay.ShowToast(TextUtil.Preview(text, 200), "OCR — скопировано в буфер", 2800);
        }
        catch (Exception ex)
        {
            Log.Error($"OCR: {ex}");
            Services.Overlay.ShowToast("Ошибка распознавания: " + ex.Message, "Screenshot OCR");
        }
    }

    private static Bitmap ToGdiBitmap(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }
}