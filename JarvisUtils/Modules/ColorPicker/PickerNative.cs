using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ColorPicker;

/// <summary>
/// Временный переход потока в per-monitor DPI v2. Приложение system-DPI-aware: на мониторах
/// с другим масштабом Windows виртуализирует координаты курсора и снимок экрана, и пипетка
/// брала бы «размазанный» соседний пиксель. Внутри области — честные физические пиксели.
/// </summary>
internal readonly struct PhysicalDpiScope : IDisposable
{
    private static readonly IntPtr PerMonitorV2 = new(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private readonly IntPtr _previous;

    private PhysicalDpiScope(IntPtr previous) => _previous = previous;

    public static PhysicalDpiScope Enter() => new(SetThreadDpiAwarenessContext(PerMonitorV2));

    public void Dispose()
    {
        if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}

/// <summary>Снимок квадрата size×size физических пикселей вокруг точки (GDI, ~доли мс).</summary>
internal sealed class ScreenSampler : IDisposable
{
    private const int SRCCOPY = 0x00CC0020;

    public int Size { get; }
    public uint[] Pixels { get; } // 0x00RRGGBB, построчно сверху вниз

    private readonly IntPtr _memDc, _dib, _oldBitmap, _bits;

    public ScreenSampler(int size)
    {
        Size = size;
        Pixels = new uint[size * size];
        var screen = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down: строка 0 — верхняя
                biPlanes = 1,
                biBitCount = 32
            };
            _dib = CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _oldBitmap = SelectObject(_memDc, _dib);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>Координаты — физические (звать внутри PhysicalDpiScope). Вне экранов пиксели чёрные.</summary>
    public bool Sample(int centerX, int centerY)
    {
        int half = Size / 2;
        var screen = GetDC(IntPtr.Zero);
        bool ok;
        try
        {
            // Без CAPTUREBLT: layered-окна (наша подложка и лупа) в снимок не попадают,
            // а курсор не мерцает. Дополнительно оба окна исключены через WDA_EXCLUDEFROMCAPTURE.
            ok = BitBlt(_memDc, 0, 0, Size, Size, screen, centerX - half, centerY - half, SRCCOPY);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        if (!ok) return false;
        GdiFlush();
        var raw = new int[Pixels.Length];
        Marshal.Copy(_bits, raw, 0, raw.Length);
        for (int i = 0; i < raw.Length; i++) Pixels[i] = (uint)raw[i] & 0x00FFFFFF;
        return true;
    }

    public uint Center => Pixels[Size / 2 * Size + Size / 2];

    public void Dispose()
    {
        SelectObject(_memDc, _oldBitmap);
        WinApi.DeleteObject(_dib);
        DeleteDC(_memDc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}

/// <summary>
/// Невидимая «ловушка кликов» на весь виртуальный экран: голое Win32-окно, созданное в per-monitor
/// контексте (физические координаты на любом мониторе и масштабе). Layered с альфой 1/255 — глазу
/// не видно, но клики принимает; не активируется — фокус остаётся в приложении пользователя.
/// Клики обрабатываются на отпускании: и нажатие, и отпускание достаются нам, а не окну под курсором.
/// </summary>
internal sealed class PickerSurface : IDisposable
{
    private const string ClassName = "JarvisUtils.ColorPickerSurface";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                      WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MOUSEWHEEL = 0x020A;
    private const int MA_NOACTIVATE = 3, LWA_ALPHA = 0x2, SW_SHOWNOACTIVATE = 4, IDC_CROSS = 32515;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private static readonly WndProcDelegate Proc = WndProc; // держим делегат: его вызывает нативный код
    private static readonly Dictionary<IntPtr, PickerSurface> Instances = new();
    private static bool _classRegistered;

    public event Action? LeftClick;
    public event Action? RightClick;

    public IntPtr Handle { get; private set; }

    public PickerSurface()
    {
        using var _ = PhysicalDpiScope.Enter();
        EnsureClass();
        var vs = WinApi.VirtualScreenRect(); // в per-monitor контексте — физические пиксели
        Handle = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "",
            WS_POPUP, vs.X, vs.Y, vs.Width, vs.Height, IntPtr.Zero, IntPtr.Zero, WinApi.GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx: {Marshal.GetLastWin32Error()}");
        Instances[Handle] = this;
        SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); // Win10 2004+: не попадаем в BitBlt экрана
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        SetCursor(LoadCursor(IntPtr.Zero, IDC_CROSS));
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Instances.Remove(Handle);
        DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = WinApi.GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, IDC_CROSS),
            hbrBackground = GetStockObject(4), // BLACK_BRUSH: при альфе 1/255 неотличимо от прозрачного
            lpszClassName = ClassName
        };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx: {Marshal.GetLastWin32Error()}");
        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            Instances.TryGetValue(hwnd, out var self);
            switch (msg)
            {
                case WM_SETCURSOR:
                    SetCursor(LoadCursor(IntPtr.Zero, IDC_CROSS));
                    return 1;
                case WM_MOUSEACTIVATE:
                    return MA_NOACTIVATE;
                case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MOUSEWHEEL:
                    return IntPtr.Zero; // проглатываем: окну под курсором ничего не достаётся
                case WM_LBUTTONUP:
                    self?.LeftClick?.Invoke();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    self?.RightClick?.Invoke();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex) { Log.Error($"пипетка: {ex}"); } // исключение в нативный код = падение процесса
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, int flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);

    internal static void ExcludeFromCapture(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
}
