using System;
using System.Text;

namespace JarvisUtils.Core;

public sealed class KeyboardStateTracker
{
    private readonly bool[] _down = new bool[256];
    private bool _capsOn = (WinApi.GetKeyState(0x14) & 1) != 0;

    // VK_CAPITAL

    public void OnKey(uint vk, bool down)
    {
        if (vk == 0x14) { if (down) _capsOn = !_capsOn; return; }
        if (vk is > 0 and < 256) _down[vk] = down;
    }

    public bool HasNonShiftModifier => // Ctrl / Alt / Win (Shift — легален при печати)
        _down[0x11] || _down[0xA2] || _down[0xA3] ||
        _down[0x12] || _down[0xA4] || _down[0xA5] ||
        _down[0x5B] || _down[0x5C];

    /// <summary>vk → символ с учётом модификаторов и раскладки активного окна. null для непечатаемых/dead key.</summary>
    public char? CharFromKey(uint vk, uint scanCode)
    {
        var state = new byte[256];
        for (int i = 0; i < 256; i++) if (_down[i]) state[i] = 0x80;
        if (_capsOn) state[0x14] |= 1;

        // Принудительно добавляем модификаторы через GetAsyncKeyState
        if ((WinApi.GetAsyncKeyState(0x10) & 0x8000) != 0) state[0x10] = 0x80; // Shift
        if ((WinApi.GetAsyncKeyState(0x11) & 0x8000) != 0) state[0x11] = 0x80; // Ctrl
        if ((WinApi.GetAsyncKeyState(0x12) & 0x8000) != 0) state[0x12] = 0x80; // Alt
        if ((WinApi.GetAsyncKeyState(0x5B) & 0x8000) != 0) state[0x5B] = 0x80; // LWin
        if ((WinApi.GetAsyncKeyState(0x5C) & 0x8000) != 0) state[0x5C] = 0x80; // RWin

        var fg = WinApi.GetForegroundWindow();
        uint tid = fg != IntPtr.Zero ? WinApi.GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
        var hkl = WinApi.GetKeyboardLayout(tid);

        var sb = new StringBuilder(8);
        int n = WinApi.ToUnicodeEx(vk, scanCode, state, sb, sb.Capacity, 0, hkl);
        if (n == 1 && sb.Length == 1 && !char.IsControl(sb[0])) return sb[0];
        return null;
    }
}