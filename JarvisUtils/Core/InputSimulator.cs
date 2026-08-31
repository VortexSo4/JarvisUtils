using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace JarvisUtils.Core;

public sealed class InputSimulator : IInputSimulator
{
    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_C = 0x43, VK_V = 0x56;

    // Буфер обмена Windows — общий COM-ресурс, к которому может параллельно
    // обращаться антивирус, менеджеры буфера обмена, сама ОС и т.п. Открытие
    // (OpenClipboard) может кратковременно фейлиться — это нормально и ожидаемо,
    // поэтому все операции с буфером идут через короткий ограниченный ретрай,
    // а не через один вызов и не через бесконечный цикл.
    private const int ClipboardRetries = 5;
    private const int ClipboardRetryDelayMs = 40;

    /// <summary>
    /// Вставляет текст через буфер обмена (Ctrl+V) вместо посимвольной юникод-печати —
    /// быстрее и атомарно для приложения-получателя. Текущее содержимое буфера
    /// сохраняется перед вставкой и восстанавливается после, чтобы не затирать то,
    /// что было скопировано у пользователя.
    /// </summary>
    public void SendText(string text)
    {
        var backup = TryGetClipboardBackup();

        if (!TrySetClipboardText(text))
        {
            Log.Warn("SendText: не удалось положить текст в буфер обмена после всех попыток — вставка отменена");
            return;
        }

        SendCtrlV();
        RestoreClipboardAfterDelay(backup);
    }

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

    // ── Буфер обмена: backup / restore, всё с ограниченными ретраями ──

    private static IDataObject? TryGetClipboardBackup()
    {
        for (int attempt = 1; attempt <= ClipboardRetries; attempt++)
        {
            try
            {
                var current = Clipboard.GetDataObject();
                // Оборачиваем в новый DataObject, чтобы снять независимую копию данных —
                // "живой" объект от Clipboard.GetDataObject() может стать недействительным
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

    private static void RestoreClipboardAfterDelay(IDataObject? backup)
    {
        if (backup == null) return;

        // Пауза перед восстановлением — даём целевому приложению время реально забрать
        // вставленный текст из буфера, прежде чем мы положим туда старое содержимое.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TrySetClipboardData(backup);
        };
        timer.Start();
    }

    private static void TrySetClipboardData(IDataObject data)
    {
        for (int attempt = 1; attempt <= ClipboardRetries; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"SendText: восстановление буфера не удалось (попытка {attempt}/{ClipboardRetries}) — {ex.Message}");
                if (attempt < ClipboardRetries) Thread.Sleep(ClipboardRetryDelayMs);
            }
        }
        Log.Warn("SendText: буфер не восстановлен после всех попыток — прежнее содержимое буфера утеряно");
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
