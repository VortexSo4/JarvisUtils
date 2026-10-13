using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ClipboardHistory;

/// <summary>Сырые данные буфера: байты скопированы, буфер уже закрыт. Разбор — вне clipboard-потока.</summary>
internal sealed class ClipboardCapture
{
    public string? Text;
    public readonly List<(string Name, byte[] Data)> Rich = []; // HTML/RTF — для вставки «с форматированием»
    public byte[]? Png;
    public byte[]? Dib;
    public uint OwnerPid;
}

/// <summary>
/// Win32-доступ к буферу для истории: чтение с проверкой «приватных» меток и временная запись
/// для вставки. Все методы — ТОЛЬКО на clipboard-потоке (StaClipboard): буфер может держать
/// другое приложение, а отложенный рендеринг источника длится секундами.
/// Свои P/Invoke, а не NativeClipboard: там всё приватное и заточено под снимок/restore.
/// </summary>
internal static class HistoryClipboard
{
    public const uint CF_DIB = 8, CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int OpenRetries = 10, OpenRetryDelayMs = 25;
    private const long MaxImageBytes = 64L << 20;
    private const long MaxRichBytes = 4L << 20;

    // Метки, по которым менеджеры паролей (и наш InputSimulator) просят историю не вести.
    // Регистрация по имени возвращает тот же id, что и у NativeClipboard, — метки общие.
    private static readonly uint FmtExcludeMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtCanIncludeHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint FmtCanUploadCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    private static readonly uint FmtViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore"); // KeePass и др.
    public static readonly uint FmtTransient = RegisterClipboardFormat("JarvisUtils.Transient");
    public static readonly uint FmtPng = RegisterClipboardFormat("PNG");
    private static readonly string[] RichFormats = ["HTML Format", "Rich Text Format"];

    private static HwndSource? _owner; // message-only окно-владелец (clipboard-поток)

    public static uint SequenceNumber => GetClipboardSequenceNumber();

    public static uint RegisterFormat(string name) => RegisterClipboardFormat(name);

    /// <summary>
    /// Прочитать текущее содержимое для истории. null — пропустить: приватная метка, пусто,
    /// слишком большое или буфер занят. Текст приоритетнее картинки: Excel/Word кладут к ячейкам
    /// ещё и растр, рендеринг которого по запросу занял бы секунды.
    /// </summary>
    public static ClipboardCapture? Read(bool includeImages, int maxTextChars)
    {
        // быстрые проверки без открытия буфера
        if (IsClipboardFormatAvailable(FmtTransient) || IsClipboardFormatAvailable(FmtExcludeMonitor) ||
            IsClipboardFormatAvailable(FmtViewerIgnore))
            return null;

        if (!Open()) return null;
        try
        {
            // CanIncludeInClipboardHistory = 0 → «в историю не класть» (Windows Win+V смотрит так же)
            if (IsClipboardFormatAvailable(FmtCanIncludeHistory))
            {
                var flag = ReadGlobal(GetClipboardData(FmtCanIncludeHistory), 16);
                if (flag is { Length: >= 4 } && BitConverter.ToUInt32(flag, 0) == 0) return null;
            }

            var capture = new ClipboardCapture { OwnerPid = OwnerProcessId() };

            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadUnicode(GetClipboardData(CF_UNICODETEXT), maxTextChars);
                if (text == null || string.IsNullOrWhiteSpace(text)) return null;
                capture.Text = text;
                foreach (var name in RichFormats)
                {
                    uint f = RegisterClipboardFormat(name);
                    if (!IsClipboardFormatAvailable(f)) continue;
                    var data = ReadGlobal(GetClipboardData(f), MaxRichBytes);
                    if (data != null) capture.Rich.Add((name, data));
                }
                return capture;
            }

            if (!includeImages) return null;
            if (IsClipboardFormatAvailable(FmtPng)) // браузеры/Office/«Ножницы» кладут PNG — с альфой и без потерь
                capture.Png = ReadGlobal(GetClipboardData(FmtPng), MaxImageBytes);
            if (capture.Png == null && IsClipboardFormatAvailable(CF_DIB))
                capture.Dib = ReadGlobal(GetClipboardData(CF_DIB), MaxImageBytes);
            return capture.Png != null || capture.Dib != null ? capture : null;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// Положить данные для вставки: мимо Win+V/облака и с нашей меткой Transient — история
    /// их не запишет, а restore поймёт, что пользователь после нас ничего не копировал.
    /// </summary>
    public static bool SetTransient(IReadOnlyList<(uint Format, byte[] Data)> items)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            bool any = false;
            foreach (var (format, data) in items) any |= SetGlobal(format, data);
            if (!any) return false;
            var zero = new byte[4];
            SetGlobal(FmtExcludeMonitor, zero);
            SetGlobal(FmtCanIncludeHistory, zero);
            SetGlobal(FmtCanUploadCloud, zero);
            SetGlobal(FmtTransient, [1]);
            return true;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Обычная (не временная) запись — когда вставлять некуда и элемент просто кладём в буфер.</summary>
    public static bool SetPlain(IReadOnlyList<(uint Format, byte[] Data)> items)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            bool any = false;
            foreach (var (format, data) in items) any |= SetGlobal(format, data);
            return any;
        }
        finally { CloseClipboard(); }
    }

    public static bool HoldsTransient() => IsClipboardFormatAvailable(FmtTransient);

    // ── внутреннее ──

    private static bool Open()
    {
        _owner ??= new HwndSource(new HwndSourceParameters("JarvisUtils.HistoryClipboardOwner") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        for (int attempt = 0; ; attempt++)
        {
            if (OpenClipboard(_owner.Handle)) return true;
            if (attempt >= OpenRetries)
            {
                Log.Warn("история буфера: буфер занят другим приложением");
                return false;
            }
            Thread.Sleep(OpenRetryDelayMs);
        }
    }

    private static uint OwnerProcessId()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) owner = GetForegroundWindow(); // владелец мог уже закрыться
        if (owner == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(owner, out uint pid);
        return pid;
    }

    private static string? ReadUnicode(IntPtr h, int maxChars)
    {
        if (h == IntPtr.Zero) return null;
        long size = (long)GlobalSize(h);
        if (size <= 0) return null;
        // строка до первого \0, но не дальше размера блока (не все приложения его ставят)
        if (size / 2 > maxChars + 1L) { Log.Info($"история буфера: текст длиннее {maxChars} симв. — пропущен"); return null; }
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            int chars = (int)(size / 2);
            var s = Marshal.PtrToStringUni(p, chars);
            int zero = s.IndexOf('\0');
            return zero >= 0 ? s[..zero] : s;
        }
        finally { GlobalUnlock(h); }
    }

    private static byte[]? ReadGlobal(IntPtr h, long maxBytes)
    {
        if (h == IntPtr.Zero) return null;
        long size = (long)GlobalSize(h);
        if (size <= 0 || size > maxBytes) return null;
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

    /// <summary>Unicode-текст в формате CF_UNICODETEXT (с завершающим нулём).</summary>
    public static byte[] UnicodeBytes(string text) => Encoding.Unicode.GetBytes(text + "\0");

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
}
