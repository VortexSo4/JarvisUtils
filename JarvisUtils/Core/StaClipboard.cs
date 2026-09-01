using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace JarvisUtils.Core;

/// <summary>
/// Выделенный STA-поток для ВСЕХ операций с буфером обмена.
///
/// Буфер — OLE-ресурс, и операции с ним могут блокироваться на секунды:
///  - чтение с копированием всех форматов запускает отложенный рендеринг
///    в приложении-источнике (Excel/Word рисуют содержимое секунды);
///  - SetDataObject(..., true) делает OleFlushClipboard с тем же эффектом;
///  - другое приложение может держать буфер открытым.
/// Раньше всё это выполнялось на UI-потоке, на котором висят low-level хуки
/// (WH_KEYBOARD_LL / WH_MOUSE_LL): пока хук не вернул управление, ОС не
/// доставляет ввод НИ ОДНОМУ приложению — «замерзают» мышь и клавиатура
/// во всей системе. Все обращения к Clipboard.* теперь идут сюда.
/// STA + свой message pump (Dispatcher.Run) — требования OLE.
/// </summary>
public static class StaClipboard
{
    private const int StartTimeoutMs = 5000;
    private const int DefaultTimeoutMs = 10_000;

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    private static readonly Dispatcher _dispatcher = Start();

    /// <summary>Диспетчер clipboard-потока (таймеры для него создавать на нём самом).</summary>
    public static Dispatcher Dispatcher => _dispatcher;

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try { OleInitialize(IntPtr.Zero); } catch { /* уже инициализирован — не критично */ }
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run(); // message pump — нужен для OLE-буфера
        })
        { Name = "JarvisUtils.Clipboard", IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(StartTimeoutMs);

        return dispatcher ?? throw new InvalidOperationException("clipboard-поток не запустился");
    }

    /// <summary>Поставить операцию в очередь БЕЗ ожидания — для «горячих» путей (хуки, ввод).</summary>
    public static void Post(Action action)
    {
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error($"clipboard-поток: {ex}"); }
            });
        }
        catch { /* приложение закрывается — операция уже не нужна */ }
    }

    /// <summary>
    /// Выполнить с ожиданием (не дольше timeoutMs). Вызывающий поток (обычно UI)
    /// не может быть заблокирован дольше таймаута, даже если буфер «залип»:
    /// за это время хуки успевают ответить, и ввод системы не встаёт.
    /// </summary>
    public static T Run<T>(Func<T> func, int timeoutMs = DefaultTimeoutMs)
    {
        if (_dispatcher.HasShutdownStarted)
            return default!;

        try
        {
            return _dispatcher.Invoke(
                func,
                DispatcherPriority.Send,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (Exception ex)
        {
            Log.Warn($"clipboard: {ex.Message}");
            return default!;
        }
    }

    /// <summary>Вариант для операций без результата.</summary>
    public static void Run(Action action, int timeoutMs = DefaultTimeoutMs) =>
        Run<object?>(() => { action(); return null; }, timeoutMs);
}