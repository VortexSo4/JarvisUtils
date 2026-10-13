using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Отправка результата в активное окно: текст, нажатия клавиш, позиционирование каретки.
/// Тонкость — порядок: длинный/многострочный текст InputSimulator вставляет через буфер
/// АСИНХРОННО (clipboard-поток → Ctrl+V на UI-потоке), а клавиши уходят в SendInput сразу.
/// Поэтому после каждого текстового сегмента дожидаемся, пока Ctrl+V реально отправлен.
/// </summary>
public sealed class ExpansionSender(IInputSimulator input)
{
    private const ushort VK_LEFT = 0x25, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int MaxTypedLength = 100; // как в InputSimulator: длиннее — через буфер

    // Пауза между двумя вставками через буфер: целевое приложение должно успеть
    // прочитать первый текст, прежде чем буфер перезапишется вторым.
    private const int PasteGapMs = 150;

    /// <summary>Отправить результат. Простой текст — одним SendText, как раньше.</summary>
    public async Task SendAsync(ExpansionOutput output)
    {
        if (output.IsPlain)
        {
            input.SendText(output.Text);
            return;
        }

        // Зажатый Shift (двоеточие в «:sig:») превратил бы ← в выделение, а Enter — в Shift+Enter
        await WaitModifiersReleasedAsync(TimeSpan.FromMilliseconds(1500));

        var segments = output.Segments;
        bool pastedBefore = false;
        for (int i = 0; i < segments.Count; i++)
        {
            switch (segments[i])
            {
                case TextSegment t when t.Text.Length > 0:
                    bool paste = WillPaste(t.Text);
                    if (paste && pastedBefore) await Task.Delay(PasteGapMs);
                    input.SendText(t.Text);
                    pastedBefore |= paste;
                    if (paste) await FlushPendingPasteAsync();
                    break;
                case KeySegment k:
                    var chord = k.Chord;
                    if (chord.Ctrl || chord.Shift || chord.Alt || chord.Win) SendChord(chord);
                    else input.SendKey(chord.Vk, chord.Count);
                    break;
            }
        }

        int back = output.CursorBackCount;
        if (back > 0) input.SendKey(VK_LEFT, back);
    }

    /// <summary>Отмена подстановки: стереть вставленное и вернуть набранный триггер.</summary>
    public async Task UndoAsync(int insertedUnits, string trigger)
    {
        await FlushPendingPasteAsync(); // вставка через буфер могла ещё не дойти до окна
        input.SendBackspaces(insertedUnits);
        input.SendText(trigger);
    }

    public static bool WillPaste(string text) => text.Length > MaxTypedLength || text.Any(char.IsControl);

    /// <summary>
    /// Дождаться, пока отложенный PasteText отправит Ctrl+V. PasteText ставит работу в очередь
    /// clipboard-потока, а тот — Ctrl+V в очередь UI с приоритетом Send. Пустая операция на
    /// clipboard-потоке выполнится после PasteCore, а продолжение на UI — после Ctrl+V.
    /// </summary>
    public static async Task FlushPendingPasteAsync()
    {
        try
        {
            await StaClipboard.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { /* clipboard-поток занят — дальше не ждём */ }
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private static bool IsDown(int vk) => (WinApi.GetAsyncKeyState(vk) & 0x8000) != 0;

    public static async Task WaitModifiersReleasedAsync(TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < max && (IsDown(VK_SHIFT) || IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN)))
            await Task.Delay(15);
    }

    // ── Клавиши с модификаторами (IInputSimulator умеет только одиночные) ──

    private static void SendChord(KeyChord chord)
    {
        var mods = new List<ushort>();
        if (chord.Ctrl) mods.Add(VK_CONTROL);
        if (chord.Shift) mods.Add(VK_SHIFT);
        if (chord.Alt) mods.Add(VK_MENU);
        if (chord.Win) mods.Add(VK_LWIN);

        var inputs = new List<WinApi.INPUT>();
        foreach (var m in mods) inputs.Add(Key(m, up: false));
        for (int i = 0; i < chord.Count; i++)
        {
            inputs.Add(Key(chord.Vk, up: false));
            inputs.Add(Key(chord.Vk, up: true));
        }
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(Key(mods[i], up: true));

        var arr = inputs.ToArray();
        uint sent = WinApi.SendInput((uint)arr.Length, arr, WinApi.INPUT.Size);
        if (sent != arr.Length) Log.Warn($"TextExpander: SendInput {sent} из {arr.Length}");
    }

    // Стрелки, Home/End, PgUp/PgDn, Ins/Del — без флага extended их можно принять за цифровой блок
    private static uint ExtendedFlag(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D ? 0x0001u : 0;

    private static WinApi.INPUT Key(ushort vk, bool up) => new()
    {
        type = WinApi.INPUT_KEYBOARD,
        U = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = (up ? WinApi.KEYEVENTF_KEYUP : 0) | ExtendedFlag(vk) } }
    };
}

/// <summary>Окна и процессы: имя процесса активного окна, возврат фокуса.</summary>
public static class ForegroundHelper
{
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly Dictionary<uint, string> NameCache = new();

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>Имя процесса без .exe; кешируется по PID (вызов из хука должен быть дешёвым).</summary>
    public static string? ProcessName(uint pid)
    {
        if (pid == 0) return null;
        if (NameCache.TryGetValue(pid, out var cached)) return cached;
        string? name;
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
        catch { name = null; }
        if (NameCache.Count > 256) NameCache.Clear();
        if (name != null) NameCache[pid] = name;
        return name;
    }

    /// <summary>
    /// Вернуть фокус окну (после нашего окна ввода). Пока наш поток привязан к вводу
    /// текущего активного окна, SetForegroundWindow разрешён (как WindowHelper.ForceForeground).
    /// </summary>
    public static async Task<bool> RestoreAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (WinApi.GetForegroundWindow() != hwnd)
        {
            var foreground = WinApi.GetForegroundWindow();
            uint ownThread = WinApi.GetCurrentThreadId();
            uint fgThread = foreground != IntPtr.Zero ? WinApi.GetWindowThreadProcessId(foreground, IntPtr.Zero) : 0;
            bool attached = fgThread != 0 && fgThread != ownThread && WinApi.AttachThreadInput(ownThread, fgThread, true);
            try
            {
                WinApi.BringWindowToTop(hwnd);
                WinApi.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) WinApi.AttachThreadInput(ownThread, fgThread, false);
            }
        }

        var sw = Stopwatch.StartNew();
        while (WinApi.GetForegroundWindow() != hwnd && sw.ElapsedMilliseconds < 600)
            await Task.Delay(15);
        if (WinApi.GetForegroundWindow() != hwnd) return false;
        await Task.Delay(40); // фокус внутри окна (дочерний контрол) восстанавливается чуть позже
        return true;
    }
}
