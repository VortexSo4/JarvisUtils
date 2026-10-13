using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ClipboardHistory;

/// <summary>
/// Слушатель изменений буфера (AddClipboardFormatListener). Окно-слушатель живёт на clipboard-потоке
/// (StaClipboard), поэтому и уведомление, и чтение происходят там же — UI-поток с хуками
/// в цепочке не участвует вовсе.
/// </summary>
internal sealed class ClipboardMonitor
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    // Приложения часто пишут в буфер серией (EmptyClipboard + несколько SetClipboardData,
    // а Office — ещё и повторно). Ждём тишины, чтобы прочитать итог один раз.
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly Func<bool> _includeImages;
    private readonly int _maxTextChars;
    private readonly Action<ClipboardCapture> _onCapture;

    // ── только clipboard-поток ──
    private HwndSource? _window;
    private DispatcherTimer? _timer;
    private uint _lastSequence;

    /// <param name="onCapture">Вызывается на clipboard-потоке — тяжёлую обработку уносить с него.</param>
    public ClipboardMonitor(Func<bool> includeImages, int maxTextChars, Action<ClipboardCapture> onCapture)
    {
        _includeImages = includeImages;
        _maxTextChars = maxTextChars;
        _onCapture = onCapture;
    }

    /// <summary>Без ожидания: clipboard-поток может быть занят долгим чтением.</summary>
    public void Start() => StaClipboard.Post(() =>
    {
        if (_window != null) return;
        _window = new HwndSource(new HwndSourceParameters("JarvisUtils.ClipboardHistoryListener") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        _window.AddHook(WndProc);
        // то, что уже лежит в буфере на старте, историей не считаем
        _lastSequence = HistoryClipboard.SequenceNumber;
        if (!AddClipboardFormatListener(_window.Handle))
            Log.Error($"история буфера: AddClipboardFormatListener не сработал ({Marshal.GetLastWin32Error()})");
        _timer = new DispatcherTimer(Debounce, DispatcherPriority.Background, (_, _) => OnQuiet(), StaClipboard.Dispatcher);
        _timer.Stop(); // конструктор с коллбэком запускает таймер сразу
    });

    public void Stop() => StaClipboard.Post(() =>
    {
        _timer?.Stop();
        _timer = null;
        if (_window == null) return;
        RemoveClipboardFormatListener(_window.Handle);
        _window.Dispose();
        _window = null;
    });

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _timer != null)
        {
            handled = true;
            _timer.Stop(); // перезапуск = отсчёт тишины заново
            _timer.Start();
        }
        return IntPtr.Zero;
    }

    private void OnQuiet()
    {
        _timer?.Stop();
        uint seq = HistoryClipboard.SequenceNumber;
        if (seq == _lastSequence) return; // содержимое не менялось (повторное уведомление)
        _lastSequence = seq;
        try
        {
            var capture = HistoryClipboard.Read(_includeImages(), _maxTextChars);
            if (capture != null) _onCapture(capture);
        }
        catch (Exception ex) { Log.Error($"история буфера: чтение: {ex.Message}"); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
