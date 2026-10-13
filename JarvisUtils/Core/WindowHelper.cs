using System;
using System.Windows;
using System.Windows.Interop;

namespace JarvisUtils.Core;

public static class WindowHelper
{
    /// <summary>WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW: окно никогда не крадёт фокус и не видно в Alt+Tab.</summary>
    public static void MakeNonActivating(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int ex = WinApi.GetWindowLong(hwnd, WinApi.GWL_EXSTYLE);
        WinApi.SetWindowLong(hwnd, WinApi.GWL_EXSTYLE, ex | WinApi.WS_EX_NOACTIVATE | WinApi.WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Вывести окно на передний план и отдать ему клавиатуру. Обычный Activate() из фонового
    /// трей-процесса Windows часто запрещает (foreground lock): окно появляется, но фокус
    /// остаётся в прежнем приложении. Пока наш поток привязан к вводу активного окна,
    /// SetForegroundWindow разрешён.
    /// </summary>
    public static void ForceForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var foreground = WinApi.GetForegroundWindow();
        if (hwnd != IntPtr.Zero && foreground != hwnd)
        {
            uint ownThread = WinApi.GetCurrentThreadId();
            uint fgThread = foreground != IntPtr.Zero ? WinApi.GetWindowThreadProcessId(foreground, IntPtr.Zero) : 0;
            bool attached = fgThread != 0 && fgThread != ownThread && WinApi.AttachThreadInput(ownThread, fgThread, true);
            try
            {
                WinApi.BringWindowToTop(hwnd);
                WinApi.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) WinApi.AttachThreadInput(ownThread, fgThread, false);
            }
        }
        window.Activate();
    }

    /// <summary>
    /// Win11: системный acrylic-backdrop (DWMSBT_TRANSIENTWINDOW), DWM-скругление углов, тёмный режим.
    /// Вызывать в OnSourceInitialized сразу после MakeNonActivating. HRESULT игнорируем:
    /// на Win10 атрибуты вернут ошибку и просто не применятся (целевая ОС — Win11 22H2+).
    /// </summary>
    public static void ApplyGlassBackdrop(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int backdrop = WinApi.DWMSBT_TRANSIENTWINDOW;
        WinApi.DwmSetWindowAttribute(hwnd, WinApi.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        int corner = WinApi.DWMWCP_ROUND;
        WinApi.DwmSetWindowAttribute(hwnd, WinApi.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int darkMode = 1;
        WinApi.DwmSetWindowAttribute(hwnd, WinApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
    }
}