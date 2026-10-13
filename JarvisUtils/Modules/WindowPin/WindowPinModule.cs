using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using JarvisUtils.Core;
using JarvisUtils.UI.Settings;

namespace JarvisUtils.Modules.WindowPin;

public sealed class WindowPinSettings
{
    // Ctrl+Alt+T — «Topmost»: свободно у модулей; Win+Ctrl+T не берём — это хоткей PowerToys Always On Top
    public string Hotkey { get; set; } = "Ctrl+Alt+T";
}

/// <summary>
/// Закрепление активного окна поверх остальных (HWND_TOPMOST). Модуль помнит, что закрепил сам,
/// и при выключении/выходе снимает закрепление — чужие окна не остаются «прилипшими» навсегда.
/// </summary>
public sealed class WindowPinModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor, IQuickActions
{
    internal sealed record PinnedWindow(IntPtr Handle, string Title, string? Process);

    private WindowPinSettings _settings = null!;
    private readonly List<PinnedWindow> _pinned = [];
    private StackPanel? _editorList; // открытый редактор настроек — обновляем при закреплении

    public override string DisplayName => "Поверх всех окон";
    public override string Icon => FluentIcons.Pin;

    public IReadOnlyList<QuickAction> QuickActions =>
    [
        new("Поверх всех окон", FluentIcons.Pin, ToggleForeground)
    ];

    internal IReadOnlyList<PinnedWindow> Pinned
    {
        get { Prune(); return _pinned; }
    }

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<WindowPinSettings>();
        RegisterHotkey(_settings.Hotkey, ToggleForeground);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Закрепить / открепить активное окно", () => _settings.Hotkey,
            g => { _settings.Hotkey = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    protected override void OnDisabled() => UnpinAll();

    public override void Shutdown()
    {
        UnpinAll();
        base.Shutdown();
    }

    private void ToggleForeground()
    {
        var hwnd = WinApi.GetForegroundWindow();
        if (!IsPinnable(hwnd, out var reason))
        {
            Services.Overlay.ShowToast(reason, DisplayName);
            return;
        }
        var title = Title(hwnd);
        bool pinned = !IsTopmost(hwnd);
        if (!Toggle(hwnd, pinned))
        {
            // UIPI: окно процесса с более высокими правами (запущено от администратора) нам не подвластно
            Services.Overlay.ShowToast("Не удалось изменить окно — возможно, оно запущено от имени администратора", DisplayName, 2200);
            return;
        }
        Services.Overlay.ShowToast(pinned ? "Закреплено поверх всех окон" : "Закрепление снято",
            TextUtil.Preview(title, 60), 1400);
    }

    /// <summary>Применить состояние и обновить учёт. false — SetWindowPos отказал.</summary>
    internal bool Toggle(IntPtr hwnd, bool pin)
    {
        // проверяем и фактический стиль: при отказе UIPI SetWindowPos не всегда возвращает false
        if (!SetWindowPos(hwnd, pin ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) ||
            IsTopmost(hwnd) != pin)
            return false;
        _pinned.RemoveAll(p => p.Handle == hwnd);
        if (pin) _pinned.Add(new PinnedWindow(hwnd, Title(hwnd), ProcessName(hwnd)));
        RefreshEditor();
        return true;
    }

    internal void UnpinAll()
    {
        foreach (var p in _pinned)
            if (IsWindow(p.Handle))
                SetWindowPos(p.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _pinned.Clear();
        RefreshEditor();
    }

    /// <summary>Закрытые окна и окна, с которых topmost сняли другим способом, из списка убираем.</summary>
    private void Prune() => _pinned.RemoveAll(p => !IsWindow(p.Handle) || !IsTopmost(p.Handle));

    /// <summary>Рабочий стол, панель задач и наши собственные окна не трогаем.</summary>
    internal static bool IsPinnable(IntPtr hwnd, out string reason)
    {
        reason = "Нет активного окна";
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId)
        {
            reason = "Окна JarvisUtils не закрепляются";
            return false;
        }
        var cls = ClassName(hwnd);
        if (hwnd == GetShellWindow() || cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            reason = "Рабочий стол и панель задач не закрепляются";
            return false;
        }
        reason = "";
        return true;
    }

    // ── настройки ──

    public FrameworkElement BuildEditor()
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(new TextBlock
        {
            Text = $"{_settings.Hotkey} закрепляет активное окно поверх остальных, повторное нажатие — снимает. " +
                   "При выключении модуля и выходе из JarvisUtils закрепление со всех окон снимается.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(SettingsUi.Section("ЗАКРЕПЛЁННЫЕ ОКНА"));
        _editorList = new StackPanel();
        _editorList.Unloaded += (_, _) => _editorList = null;
        root.Children.Add(_editorList);
        RefreshEditor();
        return root;
    }

    private void RefreshEditor()
    {
        var list = _editorList;
        if (list == null) return;
        list.Children.Clear();
        var pinned = Pinned;
        if (pinned.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Сейчас ничего не закреплено.", Foreground = JarvisTheme.ForegroundBrush(0.45), FontSize = 12 });
            return;
        }
        foreach (var p in pinned.ToArray())
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = FluentIcons.Glyph(FluentIcons.Pin, 13, JarvisTheme.AccentBrush);
            icon.Margin = new Thickness(0, 0, 10, 0);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(p.Title) ? "(без заголовка)" : p.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            if (p.Process != null)
                texts.Children.Add(new TextBlock { Text = p.Process, Foreground = JarvisTheme.ForegroundBrush(0.42), FontSize = 11 });

            var unpin = new Button { Content = FluentIcons.WithLabel(FluentIcons.Unpin, "Открепить"), Padding = new Thickness(10, 5, 10, 5) };
            var handle = p.Handle;
            unpin.Click += (_, _) => Toggle(handle, false);

            Grid.SetColumn(texts, 1);
            Grid.SetColumn(unpin, 2);
            row.Children.Add(icon);
            row.Children.Add(texts);
            row.Children.Add(unpin);
            list.Children.Add(row);
        }
        if (pinned.Count > 1)
        {
            var all = new Button { Content = "Открепить все", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            all.Click += (_, _) => UnpinAll();
            list.Children.Add(all);
        }
    }

    // ── Win32 ──

    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private const int WS_EX_TOPMOST = 0x8;

    internal static bool IsTopmost(IntPtr hwnd) => (WinApi.GetWindowLong(hwnd, WinApi.GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return null; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
}
