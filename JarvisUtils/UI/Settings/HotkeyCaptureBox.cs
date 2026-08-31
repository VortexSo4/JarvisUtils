using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisUtils.Core;

namespace JarvisUtils.UI.Settings;

/// <summary>
/// Поле захвата хоткея. Ведёт себя как кнопка: текст не выделяется мышью, контекстное
/// меню отключено. Принимаются любые комбинации, включая одиночные клавиши
/// (кроме зарезервированных: Esc — сброс, Tab/Enter/Space — игнорируются).
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    private static readonly Brush NormalBg = Frozen(Color.FromRgb(0x1B, 0x1E, 0x27));
    private static readonly Brush WarnBg = Frozen(Color.FromArgb(0x30, 0xFF, 0xB3, 0x00));
    private static readonly Brush ErrorBg = Frozen(Color.FromArgb(0x38, 0xFF, 0x50, 0x50));

    private const string Hint = "Кликните, затем нажмите комбинацию: сначала модификаторы (Ctrl/Alt/Shift/Win), потом клавишу. Esc — сбросить.";

    public event Action<string?>? GestureChanged;

    public HotkeyCaptureBox(string? initialGesture)
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        TextAlignment = TextAlignment.Center;
        FontFamily = new FontFamily("Consolas");
        Text = string.IsNullOrWhiteSpace(initialGesture) ? "— не задано —" : initialGesture;
        CaretBrush = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Background = NormalBg;
        ToolTip = Hint;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ── как кнопка: фокус без выделения текста ──

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true; // TextBox не начнёт выделение
        Focus();          // но фокус нужен — иначе клавиатура до нас не дойдёт
        Select(0, 0);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) e.Handled = true; // защита от drag-выделения
        base.OnPreviewMouseMove(e);
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e) => e.Handled = true; // без Copy/Paste-меню

    // ── захват клавиш ──

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return; // ждём основную клавишу
            case Key.Escape:
                SetGesture(null);
                return;
            case Key.Tab or Key.Enter or Key.Space or Key.ImeProcessed or Key.DeadCharProcessed:
                return; // зарезервированы UI / слишком разрушительны как глобальный перехват
        }

        var mods = Keyboard.Modifiers;
        var ourMods = Modifiers.None;
        if (mods.HasFlag(ModifierKeys.Control)) ourMods |= Modifiers.Control;
        if (mods.HasFlag(ModifierKeys.Alt)) ourMods |= Modifiers.Alt;
        if (mods.HasFlag(ModifierKeys.Shift)) ourMods |= Modifiers.Shift;
        if (mods.HasFlag(ModifierKeys.Windows)) ourMods |= Modifiers.Win;

        SetGesture(HotkeyGesture.Format(ourMods, key));
    }

    // ── обратная связь ──

    /// <summary>null — норм; текст — ошибка (конфликт / не зарегистрировано).</summary>
    public void SetFeedback(string? error)
    {
        if (error != null)
        {
            Background = ErrorBg;
            ToolTip = error;
            return;
        }
        bool bare = Text != "— не задано —" && HotkeyGesture.Parse(Text).Mods == Modifiers.None;
        Background = bare ? WarnBg : NormalBg;
        ToolTip = bare
            ? "Без модификаторов клавиша перехватывается глобально: набрать её в любом приложении будет нельзя."
            : Hint;
    }

    /// <summary>Вернуть отображаемый жест без события (откат конфликтной попытки).</summary>
    public void RevertTo(string? gesture) =>
        Text = string.IsNullOrWhiteSpace(gesture) ? "— не задано —" : gesture;

    private void SetGesture(string? gesture)
    {
        Text = string.IsNullOrWhiteSpace(gesture) ? "— не задано —" : gesture;
        SetFeedback(null);
        GestureChanged?.Invoke(gesture);
    }
}