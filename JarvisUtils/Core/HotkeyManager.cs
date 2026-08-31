using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace JarvisUtils.Core;

public sealed class HotkeyManager : IHotkeyManager
{
    private sealed record Registration(int Id, Key Key, Modifiers Mods, Action Action, object? Owner);

    private readonly List<Registration> _regs = [];
    private readonly Dictionary<int, Registration> _byId = new();
    private HwndSource? _source;
    private int _nextId = 0x4200;
    private bool _suspended;

    /// <summary>Вызывать на UI-потоке (нужен message pump).</summary>
    public void Initialize()
    {
        var parameters = new HwndSourceParameters("JarvisUtils.HotkeyHost") { ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WinApi.WM_HOTKEY && !_suspended && _byId.TryGetValue((int)wParam, out var reg))
        {
            handled = true;
            try { reg.Action(); }
            catch (Exception ex) { Log.Error($"hotkey {reg.Mods}+{reg.Key}: {ex}"); }
        }
        return IntPtr.Zero;
    }

    public bool Register(Key key, Modifiers modifiers, Action action, object? owner = null)
    {
        if (key == Key.None || _source == null) return false;
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!WinApi.RegisterHotKey(_source.Handle, id, (uint)modifiers | 0x4000 /*MOD_NOREPEAT*/, vk))
        {
            Log.Warn($"Хоткей {HotkeyGesture.Format(modifiers, key)} не зарегистрирован (занят?)");
            return false;
        }
        var reg = new Registration(id, key, modifiers, action, owner ?? action.Target);
        _regs.Add(reg);
        _byId[id] = reg;
        return true;
    }

    public bool RegisterGesture(string gesture, Action action, object? owner = null)
    {
        var (mods, key) = HotkeyGesture.Parse(gesture);
        return Register(key, mods, action, owner);
    }

    public void UnregisterAll(object owner)
    {
        if (_source == null) return;
        for (int i = _regs.Count - 1; i >= 0; i--)
        {
            var r = _regs[i];
            if (!ReferenceEquals(r.Owner, owner)) continue;
            WinApi.UnregisterHotKey(_source.Handle, r.Id);
            _byId.Remove(r.Id);
            _regs.RemoveAt(i);
        }
    }

    public void Suspend() => _suspended = true;
    public void Resume() => _suspended = false;
}