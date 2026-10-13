using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using SysClipboard = System.Windows.Clipboard;

namespace JarvisUtils.Core;

public sealed class ClipboardService : IClipboardService
{
    // Все операции уходят на выделенный STA-поток (StaClipboard): буфер обмена
    // может блокироваться на секунды (отложенный рендеринг, залоченный буфер),
    // а раньше это происходило на UI-потоке вместе с low-level хуками →
    // системный фриз мыши и клавиатуры. Run() ждёт не дольше таймаута, поэтому
    // UI-поток не может зависнуть надолго в принципе.

    private const int QuickTimeoutMs = 2000;   // Contains*/GetText/SetText — обычно мгновенно
    private const int RestoreTimeoutMs = 5000; // SetImage/GetImage

    public bool ContainsText => StaClipboard.Run(SysClipboard.ContainsText, QuickTimeoutMs);
    public bool ContainsImage => StaClipboard.Run(SysClipboard.ContainsImage, QuickTimeoutMs);

    public string? GetText() =>
        StaClipboard.Run(() => SysClipboard.ContainsText() ? SysClipboard.GetText() : null, QuickTimeoutMs);

    public BitmapSource? GetImage() => StaClipboard.Run(() =>
    {
        if (!SysClipboard.ContainsImage()) return null;
        var image = SysClipboard.GetImage();
        // BitmapSource создаётся здесь, на clipboard-потоке: замораживаем,
        // иначе его нельзя будет трогать с UI-потока.
        if (image != null && image.CanFreeze) image.Freeze();
        return image;
    }, RestoreTimeoutMs);

    public void SetText(string text) =>
        StaClipboard.Run(() => Retry(() => SysClipboard.SetText(text)), QuickTimeoutMs);

    public void SetImage(BitmapSource image)
    {
        // Freezable создан на UI-потоке — замораживаем там же, чтобы
        // clipboard-поток мог безопасно сериализовать его в HBITMAP.
        if (image.CanFreeze) image.Freeze();
        StaClipboard.Run(() => Retry(() => SysClipboard.SetImage(image)), RestoreTimeoutMs);
    }

    private static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (attempt < 4)
            {
                Log.Warn($"буфер обмена занят: {ex.Message}");
                Thread.Sleep(40);
            }
        }
    }
}