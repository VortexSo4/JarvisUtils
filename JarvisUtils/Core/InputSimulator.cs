using System.Collections.Generic;

namespace JarvisUtils.Core;

public sealed class InputSimulator : IInputSimulator
{
    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_C = 0x43, VK_V = 0x56;

    public void SendText(string text)
    {
        var inputs = new List<WinApi.INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (ch == '\n') { inputs.Add(KeyDown(VK_RETURN)); inputs.Add(KeyUp(VK_RETURN)); }
            else if (ch == '\t') { inputs.Add(KeyDown(VK_TAB)); inputs.Add(KeyUp(VK_TAB)); }
            else
            {
                inputs.Add(Unicode(ch, false));
                inputs.Add(Unicode(ch, true));
            }
        }
        Send(inputs);
    }

    public void SendBackspaces(int count)
    {
        var inputs = new List<WinApi.INPUT>();
        for (int i = 0; i < count; i++) { inputs.Add(KeyDown(VK_BACK)); inputs.Add(KeyUp(VK_BACK)); }
        Send(inputs);
    }

    public void SendCtrlC() => SendCombo(VK_CONTROL, VK_C);
    public void SendCtrlV() => SendCombo(VK_CONTROL, VK_V);

    /// <summary>Выделение в целевом приложении остаётся активным, поэтому достаточно «напечатать» текст поверх него.</summary>
    public void ReplaceSelection(string text) => SendText(text);

    private static WinApi.INPUT Unicode(char ch, bool up) => new()
    {
        type = WinApi.INPUT_KEYBOARD,
        U = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = WinApi.KEYEVENTF_UNICODE | (up ? WinApi.KEYEVENTF_KEYUP : 0) } }
    };

    private static void SendCombo(ushort modifier, ushort key) =>
        Send([KeyDown(modifier), KeyDown(key), KeyUp(key), KeyUp(modifier)]);

    private static WinApi.INPUT KeyDown(ushort vk) => new()
    { type = WinApi.INPUT_KEYBOARD, U = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk } } };

    private static WinApi.INPUT KeyUp(ushort vk) => new()
    { type = WinApi.INPUT_KEYBOARD, U = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = WinApi.KEYEVENTF_KEYUP } } };

    private static void Send(List<WinApi.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        uint sent = WinApi.SendInput((uint)arr.Length, arr, WinApi.INPUT.Size);
        if (sent != arr.Length) Log.Warn($"SendInput: {sent} из {arr.Length}");
    }
}