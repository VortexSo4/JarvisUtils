using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace JarvisUtils.Core;

public interface IJarvisServices
{
    IHotkeyManager Hotkeys { get; }
    IClipboardService Clipboard { get; }
    IInputSimulator Input { get; }
    ISelectionService Selection { get; }
    IOverlayService Overlay { get; }
    ISettingsStore Settings { get; }
    ITrayMenuBuilder TrayMenu { get; }
    IKeyboardHook Keyboard { get; } // для модулей на keyboard hook (TextExpander, CaseConverter)
}

public sealed class JarvisServices(
    IHotkeyManager hotkeys,
    IClipboardService clipboard,
    IInputSimulator input,
    ISelectionService selection,
    IOverlayService overlay,
    ISettingsStore settings,
    ITrayMenuBuilder trayMenu,
    IKeyboardHook keyboard)
    : IJarvisServices
{
    public IHotkeyManager Hotkeys { get; } = hotkeys;
    public IClipboardService Clipboard { get; } = clipboard;
    public IInputSimulator Input { get; } = input;
    public ISelectionService Selection { get; } = selection;
    public IOverlayService Overlay { get; } = overlay;
    public ISettingsStore Settings { get; } = settings;
    public ITrayMenuBuilder TrayMenu { get; } = trayMenu;
    public IKeyboardHook Keyboard { get; } = keyboard;
}

public interface IJarvisModule
{
    string Name { get; }
    string DisplayName { get; }
    bool IsEnabled { get; }
    bool Suspended { get; set; }
    void Initialize(IJarvisServices services);
    void SetEnabled(bool enabled);
    void Shutdown();
}

public interface IHotkeyManager
{
    bool Register(Key key, Modifiers modifiers, Action action, object? owner = null);
    bool RegisterGesture(string gesture, Action action, object? owner = null);
    void UnregisterAll(object owner);
    void Suspend();
    void Resume();
}

public interface IClipboardService
{
    bool ContainsText { get; }
    bool ContainsImage { get; }
    string? GetText();
    void SetText(string text);
    System.Windows.Media.Imaging.BitmapSource? GetImage();
    void SetImage(System.Windows.Media.Imaging.BitmapSource image);
    IDataObject? Backup();
    void Restore(IDataObject? backup);
}

public interface IInputSimulator
{
    void SendText(string text);
    void SendBackspaces(int count);
    void SendCtrlC();
    void SendCtrlV();
    void ReplaceSelection(string text);
}

public interface ISelectionService
{
    Task<string?> GetSelectedTextAsync();
}

public interface IOverlayService
{
    void ShowToast(string message, string? title = null, double durationMs = 1600);
    void ShowListPopup(ListPopupRequest request);
}

public interface ISettingsStore
{
    T Load<T>(string name) where T : class, new();
    void Save<T>(string name, T value) where T : class;
    string GetFilePath(string name);
}

public interface ITrayMenuBuilder
{
    void AddItem(string header, Action onClick);
    void AddToggle(string header, Func<bool> get, Action<bool> set);
    void AddSeparator();
    IReadOnlyList<TrayEntry> Entries { get; }
}

public interface IKeyboardHook
{
    event EventHandler<KeyHookEventArgs>? KeyDown;
    event EventHandler<KeyHookEventArgs>? KeyUp;
    event EventHandler<MouseHookEventArgs>? MouseDown;
}

public sealed class KeyHookEventArgs : EventArgs
{
    public uint Vk { get; }
    public uint ScanCode { get; }
    public bool IsKeyDown { get; }
    public bool IsInjected { get; }
    public bool Handled { get; set; } // true → клавиша проглатывается
    public Key Key => KeyInterop.KeyFromVirtualKey((int)Vk);
    internal KeyHookEventArgs(uint vk, uint scan, bool down, bool injected)
    { Vk = vk; ScanCode = scan; IsKeyDown = down; IsInjected = injected; }
}

public sealed class MouseHookEventArgs : EventArgs
{
    public int X { get; internal set; }
    public int Y { get; internal set; }
}

public sealed class OverlayListItem(string text, string? subtitle = null)
{
    public string Text { get; } = text;
    public string? Subtitle { get; } = subtitle;
}

public sealed class ListPopupRequest
{
    public string Title { get; init; } = "";
    public IReadOnlyList<OverlayListItem> Items { get; init; } = [];
    public Action<int> OnPick { get; init; } = _ => { };
    public Action<int>? OnDelete { get; init; }
    public Action? OnCancel { get; init; }
}

public abstract record TrayEntry(string Header);
public sealed record TrayButtonItem(string Header, Action OnClick) : TrayEntry(Header);
public sealed record TrayToggleEntry(string Header, Func<bool> Get, Action<bool> Set) : TrayEntry(Header);
public sealed record TraySeparatorEntry() : TrayEntry("-");

/// <summary>Слот хоткея для UI настроек (get/set жеста модуля).</summary>
public sealed class HotkeySlot(string label, Func<string> get, Action<string> set)
{
    public string Label { get; } = label;
    public string Gesture => get();
    public void Apply(string gesture) => set(gesture);
}

public interface IHotkeyConfigurable { IReadOnlyList<HotkeySlot> HotkeySlots { get; } }
public interface IModuleSettingsEditor { FrameworkElement BuildEditor(); }