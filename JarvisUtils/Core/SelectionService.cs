using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace JarvisUtils.Core;

public sealed class SelectionService(IInputSimulator input) : ISelectionService
{
    private const int CaptureTimeoutMs = 3000;

    /// <summary>
    /// Сначала UI Automation (не трогает буфер), затем Ctrl+C с backup/restore.
    /// Метод async: во время ожидания нужно прокачивать очередь сообщений, иначе
    /// инжектированный Ctrl+C застрянет в нашем же keyboard hook.
    /// </summary>
    public async Task<string?> GetSelectedTextAsync()
    {
        var uia = await TryViaUIAutomationAsync();
        if (!string.IsNullOrEmpty(uia)) return uia;
        return await ViaClipboardAsync();
    }

    private static async Task<string?> TryViaUIAutomationAsync()
    {
        try
        {
            var task = Task.Run(() =>
            {
                var element = AutomationElement.FocusedElement;
                if (element?.GetCurrentPattern(TextPattern.Pattern) is not TextPattern tp) return null;
                var ranges = tp.GetSelection();
                if (ranges.Length == 0) return null;
                return string.Join(Environment.NewLine,
                    ranges.Select(r => r.GetText(50_000)).Where(t => !string.IsNullOrWhiteSpace(t)));
            });
            return await task.WaitAsync(TimeSpan.FromMilliseconds(400));
        }
        catch (TimeoutException) { return null; }
        catch (Exception ex) { Log.Warn($"UIA: {ex.Message}"); return null; }
    }

    private async Task<string?> ViaClipboardAsync()
    {
        // Снимок — ДО Ctrl+C (после копирования прежнее содержимое уже не вернуть).
        // Ждём асинхронно: UI-поток с хуками продолжает качать сообщения.
        var backup = await StaClipboard.RunAsync(NativeClipboard.Capture, CaptureTimeoutMs);
        uint sequence = NativeClipboard.SequenceNumber;

        input.SendCtrlC();

        string? text = null;
        bool changed = false;
        for (int i = 0; i < 15 && text == null; i++)
        {
            await Task.Delay(20); // приложение обрабатывает Ctrl+C
            if (NativeClipboard.SequenceNumber == sequence) continue;
            changed = true;
            text = await StaClipboard.RunAsync(NativeClipboard.GetText, 500);
        }

        // Буфер не менялся (выделения не было) — восстанавливать нечего.
        // Post: идёт в ту же очередь, что и последующая вставка, — порядок сохранится.
        if (changed && backup != null)
            StaClipboard.Post(() => NativeClipboard.Restore(backup));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
