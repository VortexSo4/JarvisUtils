using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace JarvisUtils.Core;

public sealed class InputSimulator : IInputSimulator
{
    private const ushort VK_BACK = 0x08, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_C = 0x43, VK_V = 0x56;

    // Короткий однострочный текст «печатаем» юникод-событиями: мгновенно и без буфера обмена.
    // Длинный — через буфер: тысячи событий ввода некоторые приложения переваривают медленно.
    private const int MaxTypedLength = 100;

    // Пауза перед восстановлением: даём целевому приложению время забрать
    // вставленный текст из буфера, прежде чем мы вернём туда старое содержимое.
    private const int RestoreDelayMs = 300;

    // UI-поток: InputSimulator создаётся в App.OnStartup на UI. Сюда уходит
    // финальный Ctrl+V (SendInput — быстро).
    private readonly Dispatcher _ui;

    // ── Состояние ниже использует ТОЛЬКО clipboard-поток (StaClipboard) ──
    private ClipboardSnapshot? _pendingRestore; // оригинал буфера пользователя
    private DispatcherTimer? _restoreTimer;     // отложенное восстановление

    public InputSimulator() => _ui = Dispatcher.CurrentDispatcher;

    /// <summary>
    /// Вставить текст в активное окно. Короткий однострочный — печатается, остальное — через
    /// буфер (PasteText). Метод можно звать прямо из коллбэка хука.
    /// </summary>
    public void SendText(string text)
    {
        if (text.Length == 0) return;
        if (CanType(text)) TypeText(text);
        else PasteText(text);
    }

    /// <summary>
    /// Вставка через буфер обмена (Ctrl+V) с восстановлением прежнего содержимого.
    /// Всё буферное — на выделенном STA-потоке: UI-поток с low-level хуками не блокируется.
    /// </summary>
    public void PasteText(string text) => StaClipboard.Post(() => PasteCore(text));

    public void SendBackspaces(int count)
    {
        var inputs = new List<WinApi.INPUT>();
        for (int i = 0; i < count; i++) { inputs.Add(KeyDown(VK_BACK)); inputs.Add(KeyUp(VK_BACK)); }
        Send(inputs);
    }

    public void SendCtrlC() => SendCtrlCombo(VK_C);
    public void SendCtrlV() => SendCtrlCombo(VK_V);

    public void SendKey(ushort vk, int count = 1)
    {
        var inputs = new List<WinApi.INPUT>(count * 2);
        for (int i = 0; i < count; i++) { inputs.Add(KeyDown(vk)); inputs.Add(KeyUp(vk)); }
        Send(inputs);
    }

    /// <summary>Выделение в целевом приложении остаётся активным, поэтому достаточно «напечатать» текст поверх него.</summary>
    public void ReplaceSelection(string text) => SendText(text);

    // Переводы строк/табы печатать нельзя: Enter в мессенджерах отправит сообщение.
    // С зажатым Ctrl/Alt/Win юникод-ввод превращается в сочетания (Alt → WM_SYSCHAR).
    private static bool CanType(string text) =>
        text.Length <= MaxTypedLength && !text.Any(char.IsControl) && !IsDown(VK_CONTROL) && !IsDown(VK_MENU) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN);

    private static bool IsDown(int vk) => (WinApi.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void TypeText(string text)
    {
        var inputs = new List<WinApi.INPUT>(text.Length * 2);
        foreach (var ch in text) { inputs.Add(Unicode(ch, false)); inputs.Add(Unicode(ch, true)); }
        Send(inputs);
    }

    // ── Всё ниже выполняется на clipboard-потоке (StaClipboard) ──

    private void PasteCore(string text)
    {
        _restoreTimer?.Stop();
        _restoreTimer = null;

        // Прошлый restore ещё не сработал: в буфере наша временная строка, а оригинал
        // пользователя уже лежит в _pendingRestore — его и оставляем.
        if (_pendingRestore == null || !NativeClipboard.HoldsTransient())
            _pendingRestore = NativeClipboard.Capture();

        if (!NativeClipboard.SetTransientText(text))
        {
            Log.Warn("PasteText: не удалось положить текст в буфер обмена — вставка отменена");
            RestorePending();
            return;
        }

        // Ctrl+V — на UI-поток с приоритетом Send. Отправляем не из clipboard-потока,
        // чтобы исключить взаимное ожидание потоков друг другом.
        _ui.BeginInvoke(SendCtrlV, DispatcherPriority.Send);

        // Таймер на clipboard-потоке (конструктор с диспетчером запускает его сразу)
        _restoreTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(RestoreDelayMs), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            if (!ReferenceEquals(sender, _restoreTimer)) return;
            _restoreTimer = null;
            RestorePending();
        }, StaClipboard.Dispatcher);
    }

    private void RestorePending()
    {
        var backup = _pendingRestore;
        _pendingRestore = null;
        if (backup == null) return; // снимок снять не удалось — восстанавливать нечего
        // Пользователь успел скопировать своё — его новое содержимое не трогаем
        if (!NativeClipboard.HoldsTransient()) return;
        if (!NativeClipboard.Restore(backup))
            Log.Warn("PasteText: буфер не восстановлен — прежнее содержимое буфера утеряно");
    }

    // ── Низкоуровневые примитивы ──

    private static WinApi.INPUT Unicode(char ch, bool up) => new()
    {
        type = WinApi.INPUT_KEYBOARD,
        U = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = WinApi.KEYEVENTF_UNICODE | (up ? WinApi.KEYEVENTF_KEYUP : 0) } }
    };

    // Alt/Shift/Win, которые пользователь ещё держит после хоткея (Alt+F12, Ctrl+Alt+V)
    private static readonly ushort[] StrayModifiers = [0xA4, 0xA5, 0xA0, 0xA1, VK_LWIN, VK_RWIN];

    /// <summary>
    /// Ctrl+клавиша. Зажатые Alt/Shift/Win отпускаются инжектом, иначе приложение получит
    /// Ctrl+Alt+C вместо Ctrl+C. Отпускаем ПОСЛЕ нажатия Ctrl: одиночное «Alt вниз → Alt вверх»
    /// активирует меню окна, а «Win вверх» — меню Пуск; клавиша между ними это подавляет.
    /// </summary>
    private static void SendCtrlCombo(ushort key)
    {
        var inputs = new List<WinApi.INPUT> { KeyDown(VK_CONTROL) };
        foreach (var vk in StrayModifiers)
            if (IsDown(vk)) inputs.Add(KeyUp(vk));
        inputs.Add(KeyDown(key));
        inputs.Add(KeyUp(key));
        inputs.Add(KeyUp(VK_CONTROL));
        Send(inputs);
    }

    // Стрелки, Home/End, PgUp/PgDn, Ins/Del — без флага extended их можно принять за цифровой блок
    private static uint ExtendedFlag(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E ? 0x0001u : 0;

    private static WinApi.INPUT KeyDown(ushort vk) => new()
    { type = WinApi.INPUT_KEYBOARD, U = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = ExtendedFlag(vk) } } };

    private static WinApi.INPUT KeyUp(ushort vk) => new()
    { type = WinApi.INPUT_KEYBOARD, U = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = WinApi.KEYEVENTF_KEYUP | ExtendedFlag(vk) } } };

    private static void Send(List<WinApi.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        uint sent = WinApi.SendInput((uint)arr.Length, arr, WinApi.INPUT.Size);
        if (sent != arr.Length) Log.Warn($"SendInput: {sent} из {arr.Length}");
    }
}
