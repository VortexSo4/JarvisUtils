using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.CaseConverter;

public sealed class CaseConverterModule : JarvisModuleBase, IHotkeyConfigurable
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Ctrl+Alt+K";
        public int HoldMs { get; set; } = 350;
    }

    private Settings _settings = null!;
    private Modifiers _triggerMods;
    private uint _triggerVk;
    private bool _keyDown, _popupOpen, _skipNextUp;
    private DispatcherTimer? _holdTimer;
    private string? _selectedText;
    private int _lastIndex;

    public override string DisplayName => "Конвертер регистра";

    protected override void RegisterHotkeys() { } // работает через LL-hook: нужно различать press/hold

    protected override void OnEnabled()
    {
        _settings ??= LoadSettings<Settings>();
        LoadTrigger();
        Services.Keyboard.KeyDown += OnKeyDown;
        Services.Keyboard.KeyUp += OnKeyUp;
    }

    protected override void OnDisabled()
    {
        Services.Keyboard.KeyDown -= OnKeyDown;
        Services.Keyboard.KeyUp -= OnKeyUp;
        _holdTimer?.Stop();
        _keyDown = false;
    }

    private void LoadTrigger()
    {
        var (mods, key) = HotkeyGesture.Parse(_settings.Hotkey);
        _triggerMods = mods;
        _triggerVk = (uint)KeyInterop.VirtualKeyFromKey(key);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new HotkeySlot("Конвертация (нажатие — цикл, удержание — выбор варианта)",
            () => { _settings ??= LoadSettings<Settings>(); return _settings.Hotkey; },
            g => { _settings ??= LoadSettings<Settings>(); _settings.Hotkey = g; SaveSettings(_settings); LoadTrigger(); })
    ];

    private static bool IsDown(int vk) => (WinApi.GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool ModifiersMatch() =>
        IsDown(0x11) == _triggerMods.HasFlag(Modifiers.Control) &&
        IsDown(0x10) == _triggerMods.HasFlag(Modifiers.Shift) &&
        IsDown(0x12) == _triggerMods.HasFlag(Modifiers.Alt) &&
        (IsDown(0x5B) || IsDown(0x5C)) == _triggerMods.HasFlag(Modifiers.Win);

    private void OnKeyDown(object? sender, KeyHookEventArgs e)
    {
        if (Suspended || e.IsInjected || e.Vk != _triggerVk || !ModifiersMatch()) return;
        e.Handled = true; // комбинация перехвачена — в приложение не идёт
        if (_keyDown) return; // автоповтор
        _keyDown = true;

        _holdTimer?.Stop();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(150, _settings.HoldMs)) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer!.Stop();
            if (_keyDown && !_popupOpen) OpenVariantPopup();
        };
        _holdTimer.Start();
    }

    private void OnKeyUp(object? sender, KeyHookEventArgs e)
    {
        if (e.Vk != _triggerVk || !_keyDown) return;
        e.Handled = true;
        _keyDown = false;
        _holdTimer?.Stop();
        if (_skipNextUp) { _skipNextUp = false; return; }
        if (!_popupOpen) Cycle();
    }

    private async void Cycle()
    {
        _selectedText = await Services.Selection.GetSelectedTextAsync();
        if (string.IsNullOrEmpty(_selectedText)) { Services.Overlay.ShowToast("Нет выделенного текста", "Case Converter"); return; }
        _lastIndex = (_lastIndex + 1) % CaseConversions.All.Count;
        Apply(_lastIndex);
    }

    private async void OpenVariantPopup()
    {
        _selectedText = await Services.Selection.GetSelectedTextAsync();
        if (string.IsNullOrEmpty(_selectedText)) { Services.Overlay.ShowToast("Нет выделенного текста", "Case Converter"); return; }
        _popupOpen = true;
        Services.Overlay.ShowListPopup(new ListPopupRequest
        {
            Title = "Case Converter",
            Items = CaseConversions.All
                .Select(v => new OverlayListItem(v.Name, TextUtil.Preview(v.Convert(_selectedText!), 40)))
                .ToList(),
            OnPick = i => { _popupOpen = false; _skipNextUp = _keyDown; _lastIndex = i; Apply(i); },
            OnCancel = () => { _popupOpen = false; _skipNextUp = _keyDown; }
        });
    }

    private void Apply(int index)
    {
        var variant = CaseConversions.All[index];
        var converted = variant.Convert(_selectedText!);
        Services.Input.ReplaceSelection(converted);
        Services.Overlay.ShowToast(TextUtil.Preview(converted), variant.Name);
    }
}