using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using SysClipboard = System.Windows.Clipboard;

namespace JarvisUtils.Core;

public sealed class SelectionService(IInputSimulator input) : ISelectionService
{
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
        IDataObject? backup = null;
        SafeClipboard(() => backup = SysClipboard.GetDataObject());
        SafeClipboard(SysClipboard.Clear);

        input.SendCtrlC();

        string? text = null;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(20); // приложение обрабатывает Ctrl+C
            bool has = false;
            SafeClipboard(() => has = SysClipboard.ContainsText());
            if (has) { SafeClipboard(() => text = SysClipboard.GetText()); break; }
        }

        await Task.Delay(60); // дать дочитать буфер перед восстановлением
        SafeClipboard(() =>
        {
            if (backup != null) SysClipboard.SetDataObject(backup, true);
            else SysClipboard.Clear();
        });
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void SafeClipboard(Action a) { try { a(); } catch (Exception ex) { Log.Warn($"clipboard: {ex.Message}"); } }
}