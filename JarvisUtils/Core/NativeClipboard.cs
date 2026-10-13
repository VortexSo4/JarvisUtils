using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Interop;

namespace JarvisUtils.Core;

/// <summary>
/// Настоящая копия буфера обмена: байты каждого формата, снятые в момент Capture().
/// В отличие от Clipboard.GetDataObject() (живой OLE-прокси, который после SetText
/// «превращается» в новое содержимое) снимок не меняется вместе с буфером.
/// </summary>
public sealed class ClipboardSnapshot
{
    internal readonly List<(uint Format, byte[] Data)> Items = [];
}

/// <summary>
/// Буфер обмена через Win32 напрямую — быстро, без OLE-флаша и отложенного рендеринга с нашей стороны.
/// Все методы, кроме SequenceNumber, вызывать ТОЛЬКО на clipboard-потоке (StaClipboard).
/// </summary>
internal static class NativeClipboard
{
    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_DIB = 8, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const int OpenRetries = 10, OpenRetryDelayMs = 25;
    // Excel/Word рендерят десятки форматов по запросу — секундами. Снимаем важные форматы
    // первыми и останавливаемся по бюджету: частичный снимок лучше зависшего ввода.
    private const int CaptureBudgetMs = 1500;
    private const long MaxFormatBytes = 64L << 20;

    // Форматы, по которым история Win+V, облачный буфер и менеджеры буфера пропускают содержимое
    private static readonly uint FmtExcludeMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtCanIncludeHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint FmtCanUploadCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    // Метка нашей временной строки: по ней restore понимает, что пользователь ничего нового не скопировал
    private static readonly uint FmtTransient = RegisterClipboardFormat("JarvisUtils.Transient");
    private static readonly uint FmtHtml = RegisterClipboardFormat("HTML Format");
    private static readonly uint FmtRtf = RegisterClipboardFormat("Rich Text Format");

    private static HwndSource? _owner; // message-only окно-владелец, живёт на clipboard-потоке

    /// <summary>Счётчик изменений буфера. Можно читать с любого потока, буфер не открывает.</summary>
    public static uint SequenceNumber => GetClipboardSequenceNumber();

    /// <summary>Снимок всех переносимых форматов; null — буфер не удалось открыть.</summary>
    public static ClipboardSnapshot? Capture()
    {
        if (!Open()) return null;
        try
        {
            var formats = new List<uint>();
            for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f)) formats.Add(f);
            bool hasUnicode = formats.Contains(CF_UNICODETEXT);

            var snapshot = new ClipboardSnapshot();
            var sw = Stopwatch.StartNew();
            foreach (var f in formats.Where(f => IsCopyable(f, hasUnicode)).OrderBy(Priority))
            {
                if (sw.ElapsedMilliseconds > CaptureBudgetMs)
                {
                    Log.Warn($"буфер обмена: снимок прерван по времени ({sw.ElapsedMilliseconds} мс), часть форматов не сохранена");
                    break;
                }
                var data = ReadGlobal(GetClipboardData(f));
                if (data != null) snapshot.Items.Add((f, data));
            }
            return snapshot;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Вернуть снимок в буфер. Повторно в историю Win+V он не попадает.</summary>
    public static bool Restore(ClipboardSnapshot snapshot)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            foreach (var (format, data) in snapshot.Items) SetGlobal(format, data);
            if (snapshot.Items.Count > 0) MarkPrivate();
            return true;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Временный текст для вставки: мимо истории Win+V и облака, с нашей меткой.</summary>
    public static bool SetTransientText(string text)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            if (!SetGlobal(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"))) return false;
            MarkPrivate();
            SetGlobal(FmtTransient, [1]);
            return true;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>В буфере всё ещё наша временная строка (пользователь ничего не копировал после неё).</summary>
    public static bool HoldsTransient() => IsClipboardFormatAvailable(FmtTransient);

    public static string? GetText()
    {
        if (!Open()) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    // ── внутреннее ──

    private static bool Open()
    {
        _owner ??= new HwndSource(new HwndSourceParameters("JarvisUtils.ClipboardOwner") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        for (int attempt = 0; ; attempt++)
        {
            if (OpenClipboard(_owner.Handle)) return true;
            if (attempt >= OpenRetries)
            {
                Log.Warn("буфер обмена занят другим приложением");
                return false;
            }
            Thread.Sleep(OpenRetryDelayMs);
        }
    }

    private static void MarkPrivate()
    {
        var zero = new byte[4]; // DWORD 0
        SetGlobal(FmtExcludeMonitor, zero);
        SetGlobal(FmtCanIncludeHistory, zero);
        SetGlobal(FmtCanUploadCloud, zero);
    }

    private static bool IsCopyable(uint f, bool hasUnicode)
    {
        // не HGLOBAL: CF_BITMAP, METAFILEPICT, PALETTE, ENHMETAFILE, OWNERDISPLAY, DSP*-варианты,
        // private- и GDI-диапазоны. Картинка всё равно сохранится — через синтезируемый CF_DIB.
        if (f is 2 or 3 or 9 or 14 or 0x80 or 0x82 or 0x83 or 0x8E or (>= 0x200 and <= 0x3FF)) return false;
        // ANSI/OEM-текст система заново синтезирует из CF_UNICODETEXT
        if (hasUnicode && f is CF_TEXT or CF_OEMTEXT) return false;
        if (f == FmtTransient) return false;
        // OLE-служебные форматы ссылаются на объект прежнего владельца — после restore они лгут
        var name = FormatName(f);
        return name is not ("Ole Private Data" or "DataObject");
    }

    private static int Priority(uint f) =>
        f == CF_UNICODETEXT ? 0 :
        f == FmtHtml || f == FmtRtf || f == CF_HDROP ? 1 :
        f is CF_DIB or CF_DIBV5 ? 2 : 3;

    private static string? FormatName(uint f)
    {
        if (f < 0xC000) return null;
        var sb = new StringBuilder(128);
        return GetClipboardFormatName(f, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    private static byte[]? ReadGlobal(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        long size = (long)GlobalSize(h);
        if (size <= 0 || size > MaxFormatBytes) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(h); }
    }

    private static bool SetGlobal(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(data.Length, 1));
        if (h == IntPtr.Zero) return false;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return false; }
        try { Marshal.Copy(data, 0, p, data.Length); }
        finally { GlobalUnlock(h); }
        if (SetClipboardData(format, h) != IntPtr.Zero) return true; // память теперь принадлежит системе
        GlobalFree(h);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder name, int maxCount);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
}
