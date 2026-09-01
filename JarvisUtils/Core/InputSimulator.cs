using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace JarvisUtils.Core;

public sealed class InputSimulator : IInputSimulator
{
    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_C = 0x43, VK_V = 0x56;
    
    DispatcherTimer? timer = null;

    // Буфер обмена Windows — общий COM-ресурс, к которому может параллельно
    // обращаться антивирус, менеджеры буфера обмена, сама ОС и т.п. Открытие
    // (OpenClipboard) может кратковременно фейлиться — это нормально и ожидаемо,
    // поэтому все операции с буфером идут через короткий ограниченный ретрай.
    private const int ClipboardRetries = 5;
    private const int ClipboardRetryDelayMs = 40;

    // Пауза перед восстановлением: даём целевому приложению время забрать
    // вставленный текст из буфера, прежде чем мы вернём туда старое содержимое.
    private const int RestoreDelayMs = 250;

    // UI-поток: InputSimulator создаётся в App.OnStartup на UI. Сюда уходит
    // финальный Ctrl+V (SendInput — быстро, как и раньше).
    private readonly Dispatcher _ui;

    // ── Состояние ниже использует ТОЛЬКО clipboard-поток (StaClipboard) ──
    private IDataObject? _pendingRestore;   // оригинал буфера пользователя
    private DispatcherTimer? _restoreTimer; // отложенное восстановление

    public InputSimulator() => _ui = Dispatcher.CurrentDispatcher;

    /// <summary>
    /// Вставляет текст через буфер обмена (Ctrl+V).
    ///
    /// FIX ФРИЗА: раньше бэкап/запись/восстановление буфера выполнялись прямо
    /// здесь — на потоке вызывающего (UI-поток с low-level хуками). Копирование
    /// форматов (отложенный рендеринг Excel/Word) и SetDataObject(..., true)
    /// идут СЕКУНДЫ — всё это время хуки не отвечали, и ОС не доставляла ввод
    /// ни одному приложению. Теперь всё буферное уходит на выделенный STA-поток,
    /// UI-поток занят миллисекундами. Метод можно звать прямо из коллбэка хука.
    /// </summary>
    public void SendText(string text) => StaClipboard.Post(() => SendTextCore(text));

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

    // ── Всё ниже выполняется на clipboard-потоке (StaClipboard) ──

    private void SendTextCore(string text)
    {
        // Новая вставка — отложенное восстановление от прошлой уже неактуально.
        // Частный случай: прошлый restore ещё не сработал, значит в буфере лежит
        // наша временная строка, а _pendingRestore хранит оригинал пользователя —
        // его и оставляем (раньше вторая подряд подстановка бэкапила текст первой
        // и «оригиналом» становился он).
        StopRestoreTimer();

        _pendingRestore ??= TryGetClipboardBackup();

        if (!TrySetClipboardText(text))
        {
            Log.Warn("SendText: не удалось положить текст в буфер обмена после всех попыток — вставка отменена");
            _pendingRestore = null; // буфер мы не трогали — восстанавливать нечего
            return;
        }

        // Ctrl+V — на UI-поток с приоритетом Send (проскакивает впереди анимаций
        // и рендера). Отправляем не из clipboard-потока, чтобы исключить взаимное
        // ожидание потоков друг другом.
        _ui.BeginInvoke(() => SendCtrlV(), DispatcherPriority.Send);

        // Восстановление — таймер НА clipboard-потоке. Раньше это был
        // DispatcherTimer UI-потока: SetDataObject(backup, true) с флашем
        // вставал на UI-потоке и вешал систему вместе с хуками.

        timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(RestoreDelayMs),
            DispatcherPriority.Background,
            (_, _) =>
            {
                timer!.Stop();

                if (ReferenceEquals(_restoreTimer, timer))
                    _restoreTimer = null;

                TryRestorePending();
            },
            StaClipboard.Dispatcher);

        _restoreTimer = timer;
        timer.Start();
    }

    private void StopRestoreTimer()
    {
        _restoreTimer?.Stop();
        _restoreTimer = null;
    }

    private void TryRestorePending()
    {
        var backup = _pendingRestore;
        _pendingRestore = null;
        if (backup == null) return; // бэкап не удалось снять — восстанавливать нечего
        if (!TrySetClipboardData(backup))
            Log.Warn("SendText: буфер не восстановлен после всех попыток — прежнее содержимое буфера утеряно");
    }

    // ── Буфер обмена: backup / restore, всё с ограниченными ретраями ──

    private static IDataObject? TryGetClipboardBackup()
    {
        for (int attempt = 1; attempt <= ClipboardRetries; attempt++)
        {
            try
            {
                var current = Clipboard.GetDataObject();
                // Оборачиваем в новый DataObject, чтобы снять независимую копию данных —
                // «живой» объект от Clipboard.GetDataObject() может стать недействительным
                // после последующего SetText.
                return current != null ? new DataObject(current) : null;
            }
            catch (Exception ex)
            {
                Log.Warn($"SendText: чтение буфера перед вставкой не удалось (попытка {attempt}/{ClipboardRetries}) — {ex.Message}");
                if (attempt < ClipboardRetries) Thread.Sleep(ClipboardRetryDelayMs);
            }
        }
        Log.Warn("SendText: не удалось прочитать текущий буфер — восстановление после вставки будет пропущено");
        return null;
    }

    private static bool TrySetClipboardText(string text)
    {
        for (int attempt = 1; attempt <= ClipboardRetries; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"SendText: Clipboard.SetText не удался (попытка {attempt}/{ClipboardRetries}) — {ex.Message}");
                if (attempt < ClipboardRetries) Thread.Sleep(ClipboardRetryDelayMs);
            }
        }
        return false;
    }

    private static bool TrySetClipboardData(IDataObject data)
    {
        for (int attempt = 1; attempt <= ClipboardRetries; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"SendText: восстановление буфера не удалось (попытка {attempt}/{ClipboardRetries}) — {ex.Message}");
                if (attempt < ClipboardRetries) Thread.Sleep(ClipboardRetryDelayMs);
            }
        }
        return false;
    }

    // ── Низкоуровневые примитивы ──

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