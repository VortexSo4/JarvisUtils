using System;
using System.Collections.Generic;

namespace JarvisUtils.Modules.Widgets;

public enum WidgetElementType { Panel, Text, Button, Checkbox, Note }

public sealed class WidgetElement
{
    public override string ToString() => $"{Id} · {Type}: {(Text.Length > 20 ? Text[..20] + "…" : Text)}";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WidgetElementType Type { get; set; } = WidgetElementType.Text;
    public double X { get; set; } = 10;
    public double Y { get; set; } = 10;
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 30;
    public string Text { get; set; } = "Text";
    public string Binding { get; set; } = ""; // Имя источника данных (Time, CPU, и т.д.)
    public string Background { get; set; } = "#16141C";
    public string Foreground { get; set; } = "#FFFFFF";
    public double FontSize { get; set; } = 13;
    public double CornerRadius { get; set; } = 6;
}

public sealed class WidgetModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый виджет";
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 250;
    public double Height { get; set; } = 150;
    public double Opacity { get; set; } = 1.0;
    public bool IsVisible { get; set; } = true;
    public string Script { get; set; } = "// Доступно: Widget, El, Data, Dt, Vars\n// El[\"id\"].Text = Data.Time.ToString(\"HH:mm:ss\");";
    public List<WidgetElement> Elements { get; set; } = [];
    public Dictionary<string, object> Vars { get; set; } = []; // Пользовательские переменные
}

public sealed class WidgetsSettings
{
    public string HotkeyEditMode { get; set; } = "Ctrl+Alt+W";
    public List<WidgetModel> Widgets { get; set; } = [];
}