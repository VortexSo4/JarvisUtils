using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace JarvisUtils.Modules.ScreenshotOcr;

public static class OcrHelper
{
    /// <summary>Offline-OCR через Windows.Media.Ocr. null — язык OCR не установлен / текста нет.</summary>
    public static async Task<string?> RecognizeAsync(Bitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) return null;

        var prepared = Prepare(bitmap);
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"jarvis_ocr_{Guid.NewGuid():N}.png");
            try
            {
                prepared.Save(tempPath, ImageFormat.Png);
                var file = await StorageFile.GetFileFromPathAsync(tempPath);
                using var stream = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                var result = await engine.RecognizeAsync(software);
                var text = string.Join("\n", result.Lines.Select(l => l.Text));
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        finally
        {
            if (!ReferenceEquals(prepared, bitmap)) prepared.Dispose();
        }
    }

    private static Bitmap Prepare(Bitmap src)
    {
        double scale = 1.0;
        if (src.Width < 200 || src.Height < 50) scale = 2.0; // мелкие фрагменты — апскейл для качества
        int maxDim = Math.Min(4096, (int)OcrEngine.MaxImageDimension);
        if (Math.Max(src.Width, src.Height) * scale > maxDim)
            scale = maxDim / (double)Math.Max(1, Math.Max(src.Width, src.Height));
        if (Math.Abs(scale - 1.0) < 0.05) return src;

        var resized = new Bitmap((int)Math.Round(src.Width * scale), (int)Math.Round(src.Height * scale));
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, resized.Width, resized.Height);
        }
        return resized;
    }
}