using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace JarvisUtils.Core;

public sealed class InputSimulator : IInputSimulator
{
    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_C = 0x43, VK_V = 0x56;

    /// <summary>
    /// Вставляет текст через буфер обмена (Ctrl+V) вместо посимвольной юникод-печати —
    /// быстрее и атомарно для приложения-получателя, что убирает гонки на длинных
    /// многострочных заменах. Текущее содержимое буфера сохраняется перед вставкой и
    /// восстанавливается после, чтобы не затирать то, что было скопировано у пользователя.
    /// </summary>
    public void SendText(string text)
    {
        IDataObject? backup = null;
        try
        {
            backup = TryGetClipboardBackup();
        }
        catch (Exception ex)
        {
            Log.Warn($"SendText: не удалось прочитать текущий буфер перед вставкой — {ex.Message}");
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // Буфер обмена бывает временно занят другим процессом в Windows — короткий ретрай.
            Log.Warn($"SendText: Clipboard.SetText не удался, ретрай — {ex.Message}");
            Thread.Sleep(30);
            Clipboard.SetText(text);
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

    // ── Буфер обмена: backup / restore ──

    private static IDataObject? TryGetClipboardBackup()
    {
        // Оборачиваем в новый DataObject, чтобы снять независимую копию данных прямо
        // сейчас — "живой" объект от Clipboard.GetDataObject() может стать недействительным
        // после следующего SetText.
        var current = Clipboard.GetDataObject();
        return current != null ? new DataObject(current) : null;
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
            try
            {
                Clipboard.SetDataObject(backup, true);
            }
            catch (Exception ex)
            {
                Log.Warn($"SendText: не удалось восстановить буфер обмена — {ex.Message}");
            }
        };
        timer.Start();
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