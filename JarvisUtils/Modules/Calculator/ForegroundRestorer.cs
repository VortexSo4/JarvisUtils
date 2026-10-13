using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Calculator;

/// <summary>
/// Вернуть фокус окну, которое было активно до калькулятора, и дождаться, пока оно реально станет
/// foreground — только тогда можно печатать ответ (иначе SendInput уйдёт в никуда или в наше окно).
/// Ожидание — таймером диспетчера, без блокировки UI-потока с хуками.
/// </summary>
internal static class ForegroundRestorer
{
    private const int VK_RETURN = 0x0D;
    private const int SW_RESTORE = 9;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(800);

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>
    /// SetForegroundWindow для чужого окна. Звать, пока наше окно ещё активно: foreground-процесс
    /// вправе отдать фокус кому угодно. AttachThreadInput — страховка на случай, если фокус уже ушёл.
    /// </summary>
    public static void Activate(IntPtr hwnd)
    {
        if (!IsAlive(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        uint own = WinApi.GetCurrentThreadId();
        IntPtr fg = WinApi.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? WinApi.GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
        uint targetThread = WinApi.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        bool attachedFg = fgThread != 0 && fgThread != own && WinApi.AttachThreadInput(own, fgThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != own && targetThread != fgThread
                              && WinApi.AttachThreadInput(own, targetThread, true);
        try
        {
            WinApi.BringWindowToTop(hwnd);
            WinApi.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) WinApi.AttachThreadInput(own, targetThread, false);
            if (attachedFg) WinApi.AttachThreadInput(own, fgThread, false);
        }
    }

    /// <summary>
    /// Ждать (неблокирующе), пока hwnd станет foreground и будет отпущен Enter — его key-up не должен
    /// прилететь в целевое окно посреди вставки. Затем onReady; не вышло за таймаут — onFailed.
    /// </summary>
    public static void WhenForeground(IntPtr hwnd, Action onReady, Action onFailed)
    {
        var started = DateTime.UtcNow;
        int ticks = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = PollInterval };
        timer.Tick += (_, _) =>
        {
            bool foreground = WinApi.GetForegroundWindow() == hwnd;
            bool enterUp = (WinApi.GetAsyncKeyState(VK_RETURN) & 0x8000) == 0;
            bool timedOut = DateTime.UtcNow - started > Timeout;
            if (foreground && (enterUp || timedOut))
            {
                timer.Stop();
                onReady();
            }
            else if (timedOut || !IsAlive(hwnd))
            {
                timer.Stop();
                onFailed();
            }
            else if (!foreground && ++ticks % 6 == 0)
            {
                Activate(hwnd); // ~каждые 90 мс: Windows могла отдать фокус другому окну после закрытия нашего
            }
        };
        timer.Start();
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
