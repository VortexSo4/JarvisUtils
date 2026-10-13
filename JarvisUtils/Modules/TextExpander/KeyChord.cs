using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>Клавиша с модификаторами для {key:…}: "enter", "shift+tab", "ctrl+a", "left*3".</summary>
public sealed class KeyChord
{
    public ushort Vk { get; private init; }
    public bool Ctrl { get; private init; }
    public bool Shift { get; private init; }
    public bool Alt { get; private init; }
    public bool Win { get; private init; }
    public int Count { get; private init; } = 1;
    public string Display { get; private init; } = "";

    private const int MaxCount = 200;

    private static readonly Dictionary<string, (ushort Vk, string Label)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = (0x0D, "Enter"), ["return"] = (0x0D, "Enter"),
        ["tab"] = (0x09, "Tab"),
        ["esc"] = (0x1B, "Esc"), ["escape"] = (0x1B, "Esc"),
        ["space"] = (0x20, "Space"),
        ["backspace"] = (0x08, "Backspace"), ["bs"] = (0x08, "Backspace"), ["back"] = (0x08, "Backspace"),
        ["delete"] = (0x2E, "Del"), ["del"] = (0x2E, "Del"),
        ["insert"] = (0x2D, "Ins"), ["ins"] = (0x2D, "Ins"),
        ["home"] = (0x24, "Home"), ["end"] = (0x23, "End"),
        ["pgup"] = (0x21, "PgUp"), ["pageup"] = (0x21, "PgUp"),
        ["pgdn"] = (0x22, "PgDn"), ["pagedown"] = (0x22, "PgDn"),
        ["left"] = (0x25, "Left"), ["up"] = (0x26, "Up"), ["right"] = (0x27, "Right"), ["down"] = (0x28, "Down"),
        ["menu"] = (0x5D, "Menu"), ["apps"] = (0x5D, "Menu"),
    };

    /// <summary>Разбор спецификации; null — неизвестная клавиша.</summary>
    public static KeyChord? Parse(string spec, string? countArg = null)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0) return null;

        int count = 1;
        int star = spec.LastIndexOf('*');
        if (star > 0 && int.TryParse(spec.AsSpan(star + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
        {
            count = c;
            spec = spec[..star].Trim();
        }
        if (!string.IsNullOrWhiteSpace(countArg) && int.TryParse(countArg.Trim(), out var c2)) count = c2;
        if (count < 1 || count > MaxCount) return null;

        var parts = spec.Split('+').Select(p => p.Trim()).ToList();

        bool ctrl = false, shift = false, alt = false, win = false;
        foreach (var mod in parts.Take(parts.Count - 1))
        {
            switch (mod.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win": win = true; break;
                default: return null;
            }
        }

        var keyName = parts[^1];
        if (!TryResolve(keyName, out var vk, out var label)) return null;

        var display = string.Join("+", new[] { ctrl ? "Ctrl" : null, shift ? "Shift" : null, alt ? "Alt" : null, win ? "Win" : null, label }
            .Where(s => s != null));
        if (count > 1) display += " x" + count;
        return new KeyChord { Vk = vk, Ctrl = ctrl, Shift = shift, Alt = alt, Win = win, Count = count, Display = display };
    }

    private static bool TryResolve(string name, out ushort vk, out string label)
    {
        vk = 0;
        label = name;
        if (Named.TryGetValue(name, out var n)) { vk = n.Vk; label = n.Label; return true; }

        // F1..F24
        if (name.Length is 2 or 3 && (name[0] is 'f' or 'F') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + f - 1);
            label = "F" + f;
            return true;
        }
        if (name.Length == 1)
        {
            char ch = char.ToUpperInvariant(name[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = ch; label = ch.ToString(); return true; }
        }
        return false;
    }
}
