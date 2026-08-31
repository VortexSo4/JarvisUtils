using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ScreenshotOcr;

public sealed class ScreenshotOcrModule : JarvisModuleBase, IHotkeyConfigurable
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Ctrl+Alt+O";
    }

    private Settings _settings = null!;

    public override string DisplayName => "Скриншот OCR";

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

    private void StartCapture()
    {
        var bounds = WinApi.VirtualScreenRect();
        BitmapSource source;
        using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0,
                    new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            }
            source = ToSource(bmp);
        }

        var window = new ScreenshotOverlayWindow(source, bounds, Services.Hotkeys);
        window.SelectionCompleted += rect => { window.Close(); _ = OcrAsync(source, rect); };
        window.SelectionCancelled += () => window.Close();
        window.Show();
    }

    private static BitmapSource ToSource(Bitmap bmp)
    {
        IntPtr hBitmap = bmp.GetHbitmap();
        try { return Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
        finally { WinApi.DeleteObject(hBitmap); }
    }

    private async Task OcrAsync(BitmapSource screen, System.Drawing.Rectangle rect)
    {
        try
        {
            var crop = new CroppedBitmap(screen, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
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