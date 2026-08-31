using System;
using System.Runtime.InteropServices;

namespace JarvisUtils.Core;

public sealed class HookManager : IKeyboardHook, IDisposable
{
    private IntPtr _kbHook = IntPtr.Zero, _mouseHook = IntPtr.Zero;
    private readonly WinApi.LowLevelHookProc _kbProc, _mouseProc;

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
        var hMod = WinApi.GetModuleHandle(null);
        if (_kbHook == IntPtr.Zero)
        {
            _kbHook = WinApi.SetWindowsHookEx(WinApi.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            if (_kbHook == IntPtr.Zero) Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        }
        if (_mouseHook == IntPtr.Zero)
            _mouseHook = WinApi.SetWindowsHookEx(WinApi.WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool down = msg is WinApi.WM_KEYDOWN or WinApi.WM_SYSKEYDOWN;
            bool up = msg is WinApi.WM_KEYUP or WinApi.WM_SYSKEYUP;
            if (down || up)
            {
                var s = Marshal.PtrToStructure<WinApi.KBDLLHOOKSTRUCT>(lParam);
                var e = new KeyHookEventArgs(s.vkCode, s.scanCode, down, (s.flags & WinApi.LLKHF_INJECTED) != 0);
                try { (down ? KeyDown : KeyUp)?.Invoke(this, e); }
                catch (Exception ex) { Log.Error($"hook handler: {ex}"); }
                if (e.Handled) return (IntPtr)1; // проглотить
            }
        }
        return WinApi.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WinApi.WM_LBUTTONDOWN or WinApi.WM_RBUTTONDOWN)
            {
                var s = Marshal.PtrToStructure<WinApi.MSLLHOOKSTRUCT>(lParam);
                MouseDown?.Invoke(this, new MouseHookEventArgs { X = s.pt.X, Y = s.pt.Y });
            }
        }
        return WinApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) { WinApi.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { WinApi.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }
}