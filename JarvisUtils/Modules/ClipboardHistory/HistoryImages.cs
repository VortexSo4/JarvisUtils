using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JarvisUtils.Modules.ClipboardHistory;

/// <summary>Преобразования картинок буфера: CF_DIB ⇄ BitmapSource, PNG, миниатюры, хэш пикселей.</summary>
internal static class HistoryImages
{
    private const int BI_BITFIELDS = 3, BI_ALPHABITFIELDS = 6;

    /// <summary>Декодировать PNG-байты или CF_DIB. Результат заморожен — можно отдавать любому потоку.</summary>
    public static BitmapSource Decode(byte[]? png, byte[]? dib)
    {
        using var stream = new MemoryStream(png ?? DibToBmpFile(dib!));
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// CF_DIB — это BMP-файл без 14-байтового заголовка. Достраиваем заголовок и отдаём
    /// штатному BMP-декодеру; смещение пикселей зависит от масок и палитры.
    /// </summary>
    public static byte[] DibToBmpFile(byte[] dib)
    {
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);
        int masks = headerSize == 40 ? compression switch { BI_BITFIELDS => 12, BI_ALPHABITFIELDS => 16, _ => 0 } : 0;
        int palette = clrUsed != 0 ? clrUsed * 4 : bitCount <= 8 ? (1 << bitCount) * 4 : 0;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(14 + headerSize + masks + palette).CopyTo(file, 10);
        Buffer.BlockCopy(dib, 0, file, 14, dib.Length);
        return file;
    }

    /// <summary>
    /// BGRA-пиксели (32bpp). Хэш по пикселям, а не по байтам формата: одна и та же картинка
    /// из разных приложений приходит разными PNG/DIB, но должна схлопнуться в одну запись.
    /// </summary>
    public static byte[] Pixels(BitmapSource image, out int stride)
    {
        BitmapSource bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        if (bgra.Format == PixelFormats.Bgr32) // 4-й байт у Bgr32 — мусор; для хэша и DIB делаем его непрозрачным
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
        return pixels;
    }

    public static string Hash(byte[] pixels, int width, int height)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(BitConverter.GetBytes(width), 0, 4, null, 0);
        sha.TransformBlock(BitConverter.GetBytes(height), 0, 4, null, 0);
        sha.TransformFinalBlock(pixels, 0, pixels.Length);
        return Convert.ToHexString(sha.Hash!);
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Миниатюра, вписанная в maxW×maxH (не увеличиваем мелкие картинки).</summary>
    public static BitmapSource Thumbnail(BitmapSource image, int maxW, int maxH)
    {
        double k = Math.Min(1.0, Math.Min((double)maxW / image.PixelWidth, (double)maxH / image.PixelHeight));
        if (k >= 1.0) return image;
        var scaled = new TransformedBitmap(image, new ScaleTransform(k, k));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>CF_DIB для вставки: 32bpp BI_RGB, строки снизу вверх (так читают все приложения).</summary>
    public static byte[] ToDib(BitmapSource image)
    {
        var pixels = Pixels(image, out int stride);
        int w = image.PixelWidth, h = image.PixelHeight;
        var dib = new byte[40 + pixels.Length];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8); // > 0 — bottom-up
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20);
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(pixels, y * stride, dib, 40 + (h - 1 - y) * stride, stride);
        return dib;
    }

    /// <summary>Загрузка PNG с диска без блокировки файла (OnLoad); null — файла нет или он битый.</summary>
    public static BitmapSource? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
}
