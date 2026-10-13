using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using JarvisUtils.Core;
using GdiRectangle = System.Drawing.Rectangle;

namespace JarvisUtils.Modules.ScreenshotOcr;

/// <summary>Снимок экрана + выбор области (общий для OCR и закреплённых скриншотов).</summary>
public static class ScreenCapture
{
    /// <summary>Весь виртуальный экран (все мониторы) одним кадром.</summary>
    public static (BitmapSource Image, GdiRectangle Bounds) CaptureVirtualScreen()
    {
        var bounds = WinApi.VirtualScreenRect();
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);

        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return (source, bounds);
        }
        finally { WinApi.DeleteObject(hBitmap); }
    }

    /// <summary>
    /// Снять экран и дать выделить область. onSelected получает кадр, вырезанную область
    /// и её прямоугольник в экранных пикселях.
    /// </summary>
    public static void SelectRegion(IHotkeyManager hotkeys, Action<BitmapSource, CroppedBitmap, GdiRectangle> onSelected, Action? onCancelled = null)
    {
        var (screen, bounds) = CaptureVirtualScreen();
        var window = new ScreenshotOverlayWindow(screen, bounds, hotkeys);
        window.SelectionCompleted += rect =>
        {
            window.Close();
            var crop = new CroppedBitmap(screen, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
            crop.Freeze();
            onSelected(screen, crop, new GdiRectangle(rect.X + bounds.X, rect.Y + bounds.Y, rect.Width, rect.Height));
        };
        window.SelectionCancelled += () => { window.Close(); onCancelled?.Invoke(); };
        window.Show();
    }
}
