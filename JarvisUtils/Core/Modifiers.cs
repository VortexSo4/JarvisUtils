using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace JarvisUtils.Core;

[Flags]
public enum Modifiers
{
    None = 0,
    Alt = 1,      // MOD_ALT
    Control = 2,  // MOD_CONTROL
    Shift = 4,    // MOD_SHIFT
    Win = 8       // MOD_WINDOWS
}

public static class HotkeyGesture
{
    public static (Modifiers Mods, Key Key) Parse(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return (Modifiers.None, Key.None);
        var mods = Modifiers.None;
        var key = Key.None;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Modifiers.Control; break;
                case "alt": mods |= Modifiers.Alt; break;
                case "shift": mods |= Modifiers.Shift; break;
                case "win" or "meta": mods |= Modifiers.Win; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) return (Modifiers.None, Key.None);
                    break;
            }
        }
        return (mods, key);
    }

    public static string Format(Modifiers mods, Key key)
    {
        var parts = new List<string>(5);
        if (mods.HasFlag(Modifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Modifiers.Win)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}