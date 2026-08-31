using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using SysClipboard = System.Windows.Clipboard;

namespace JarvisUtils.Core;

public sealed class ClipboardService : IClipboardService
{
    public bool ContainsText => Safe(SysClipboard.ContainsText);
    public bool ContainsImage => Safe(SysClipboard.ContainsImage);
    public string? GetText() => Safe(() => SysClipboard.ContainsText() ? SysClipboard.GetText() : null);
    public BitmapSource? GetImage() => Safe(() => SysClipboard.ContainsImage() ? SysClipboard.GetImage() : null);

    public void SetText(string text) => Retry(() => SysClipboard.SetText(text));
    public void SetImage(BitmapSource image) => Retry(() => SysClipboard.SetImage(image));

    public IDataObject? Backup()
    {
        try { return SysClipboard.GetDataObject(); }
        catch { return null; }
    }

    public void Restore(IDataObject? backup) => Retry(() =>
    {
        if (backup != null) SysClipboard.SetDataObject(backup, true);
        else SysClipboard.Clear();
    });

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

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