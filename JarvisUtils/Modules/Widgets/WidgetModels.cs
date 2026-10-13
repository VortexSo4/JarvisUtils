using System;
using System.Collections.Generic;
using System.IO;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

public enum WidgetKind
{
    Note,   // заметка-стикер
    Image,  // закреплённый скриншот / картинка
    Custom  // XAML + необязательный C#-скрипт
}

public enum WidgetLayer
{
    Topmost, // поверх всех окон
    Normal,  // как обычное окно
    Desktop  // всегда под остальными окнами
}

/// <summary>
/// Один виджет. Поля разных видов лежат в одном классе — так проще сериализация
/// и смена/копирование виджета; неиспользуемые поля вида просто пустые.
/// Координаты и размеры — в DIP (системный DPI), как у WPF-окон.
/// </summary>
public sealed class WidgetModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WidgetKind Kind { get; set; } = WidgetKind.Custom;
    public string Name { get; set; } = "Виджет";

    public double X { get; set; } = 200;
    public double Y { get; set; } = 200;
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 160;

    public double Opacity { get; set; } = 1.0;
    public WidgetLayer Layer { get; set; } = WidgetLayer.Topmost;
    public bool Locked { get; set; }       // нельзя двигать и менять размер
    public bool ClickThrough { get; set; } // клики проходят сквозь виджет (кроме режима редактирования)
    public bool Visible { get; set; } = true;

    // ── заметка ──
    public string Text { get; set; } = "";
    public string Color { get; set; } = NotePalette.Default.Hex;
    public double FontSize { get; set; } = 14;

    // ── картинка ──
    public string? ImageFile { get; set; } // имя файла в WidgetPaths.ImagesDir

    // ── свой виджет ──
    public string Xaml { get; set; } = "";
    public string Script { get; set; } = "";
    public Dictionary<string, string> Store { get; set; } = new(); // Save()/Load() из скрипта

    public WidgetModel Clone()
    {
        var copy = (WidgetModel)MemberwiseClone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Store = new Dictionary<string, string>(Store);
        return copy;
    }
}

public sealed class WidgetsSettings
{
    public string HotkeyEditMode { get; set; } = "Ctrl+Alt+W";
    public string HotkeyNewNote { get; set; } = "Win+Alt+N";
    public string HotkeyPinScreenshot { get; set; } = "Win+Alt+S";
    public string HotkeyToggleAll { get; set; } = "";
    public bool AllHidden { get; set; }
    public List<WidgetModel> Widgets { get; set; } = [];
}

public static class WidgetPaths
{
    public static readonly string Dir = Path.Combine(AppPaths.DataDir, "widgets");
    public static readonly string ImagesDir = Path.Combine(Dir, "img");
}

public sealed record NoteColor(string Name, string Hex, bool Light);

/// <summary>Палитра заметок. Light — светлый фон, текст на нём тёмный.</summary>
public static class NotePalette
{
    public static readonly NoteColor Default = new("Графит", "#1F2330", false);

    public static readonly NoteColor[] All =
    [
        Default,
        new("Жёлтая", "#F5D76E", true),
        new("Зелёная", "#8FD694", true),
        new("Голубая", "#7CC4F8", true),
        new("Розовая", "#F49AC1", true),
        new("Сиреневая", "#C3B1FA", true),
        new("Ночная", "#0F1A2B", false),
    ];

    public static NoteColor Find(string hex)
    {
        foreach (var c in All)
            if (string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase)) return c;
        return Default;
    }
}
