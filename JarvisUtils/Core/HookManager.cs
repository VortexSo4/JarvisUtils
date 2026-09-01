using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace JarvisUtils.Core;

public sealed class HookManager : IKeyboardHook, IDisposable
{
    private IntPtr _kbHook = IntPtr.Zero, _mouseHook = IntPtr.Zero;
    private readonly WinApi.LowLevelHookProc _kbProc, _mouseProc;
    private DispatcherTimer? _watchdog;

    public event EventHandler<KeyHookEventArgs>? KeyDown;
    public event EventHandler<KeyHookEventArgs>? KeyUp;
    public event EventHandler<MouseHookEventArgs>? MouseDown;

    public HookManager()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    /// <summary>Вызывать на потоке с message pump (UI). Коллбэки приходят на этот же поток.</summary>
    public void Install()
    {
        InstallHooks();

        // Windows МОЛЧА снимает low-level хук, если его коллбэк не уложился в
        // LowLevelHooksTimeout (сотни мс по умолчанию). После сильного фриза модули
        // «просто перестают работать» до перезапуска. Проверить живость хука через
        // API нельзя, поэтому периодически переустанавливаем: окно без хука —
        // микросекунды, зато снятый хук сам восстанавливается.
        if (_watchdog == null)
        {
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _watchdog.Tick += (_, _) => ReinstallHooks();
            _watchdog.Start();
        }
    }

    private void InstallHooks()
    {
        var hMod = WinApi.GetModuleHandle(null);
        if (_kbHook == IntPtr.Zero)
        {
            _kbHook = WinApi.SetWindowsHookEx(WinApi.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            if (_kbHook == IntPtr.Zero) Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        }
        if (_mouseHook == IntPtr.Zero)
            _mouseHook = WinApi.SetWindowsHookEx(WinApi.WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    private void ReinstallHooks()
    {
        try
        {
            if (_kbHook != IntPtr.Zero) { WinApi.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
            if (_mouseHook != IntPtr.Zero) { WinApi.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            InstallHooks();
        }
        catch (Exception ex) { Log.Error($"hook watchdog: {ex}"); }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int msg = wParam.ToInt32();
                bool down = msg is WinApi.WM_KEYDOWN or WinApi.WM_SYSKEYDOWN;
                bool up = msg is WinApi.WM_KEYUP or WinApi.WM_SYSKEYUP;
                if (down || up)
                {
                    var s = Marshal.PtrToStructure<WinApi.KBDLLHOOKSTRUCT>(lParam);
                    var e = new KeyHookEventArgs(s.vkCode, s.scanCode, down, (s.flags & WinApi.LLKHF_INJECTED) != 0);
                    var sw = Stopwatch.StartNew();
                    try { (down ? KeyDown : KeyUp)?.Invoke(this, e); }
                    catch (Exception ex) { Log.Error($"hook handler: {ex}"); }
                    // диагностика: медленный обработчик = снова фриз всей системы
                    if (sw.ElapsedMilliseconds > 50)
                        Log.Warn($"handler клавиатуры занял {sw.ElapsedMilliseconds} мс — тяжёлая работа в хуке блокирует ввод");
                    if (e.Handled) return (IntPtr)1; // проглотить
                }
            }
            catch (Exception ex)
            {
                // Ничто не должно вырваться из reverse-P/Invoke коллбэка наружу:
                // исключение, ушедшее в нативный код, убивает процесс целиком.
                Log.Error($"keyboard hook: {ex}");
            }
        }
        return WinApi.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (msg is WinApi.WM_LBUTTONDOWN or WinApi.WM_RBUTTONDOWN)
                {
                    var s = Marshal.PtrToStructure<WinApi.MSLLHOOKSTRUCT>(lParam);
                    MouseDown?.Invoke(this, new MouseHookEventArgs { X = s.pt.X, Y = s.pt.Y });
                }
            }
            catch (Exception ex)
            {
                // FIX КРАША: раньше try/catch здесь не было — любое исключение из
                // подписчика MouseDown улетало в нативный вызов и роняло ВЕСЬ процесс.
                Log.Error($"mouse hook: {ex}");
            }
        }
        return WinApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _watchdog?.Stop();
        _watchdog = null;
        if (_kbHook != IntPtr.Zero) { WinApi.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { WinApi.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }
}