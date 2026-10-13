using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ColorPicker;

/// <summary>
/// Один сеанс пипетки: невидимая подложка ловит клики, лупа следует за курсором, клавиши
/// (Esc, Enter/Пробел, стрелки) перехватываются low-level хуком только на время сеанса.
/// Снимок экрана через GDI стоит ~5 мс (DWM отдаёт кадр синхронно), поэтому кадры снимает
/// фоновый поток, а UI-поток с хуками только перерисовывает лупу готовыми пикселями.
/// </summary>
internal sealed class ColorPickerSession
{
    private const int GridSize = 11; // нечётный — есть центральный пиксель
    private const int FrameMs = 16;  // ~60 кадров/с: под неподвижным курсором картинка тоже меняется (видео, анимации)
    private const ushort VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_SHIFT = 0x10;

    private readonly IKeyboardHook _keyboard;
    private readonly Dispatcher _ui;
    private readonly AutoResetEvent _wake = new(false);
    private PickerSurface? _surface;
    private LoupeWindow? _loupe;
    private uint[]? _pendingFrame; // последний кадр от фонового потока, ещё не показанный
    private volatile bool _finished;

    /// <summary>Итог сеанса: цвет или null (отмена). Вызывается один раз, на UI-потоке.</summary>
    public event Action<Color?>? Finished;

    public ColorPickerSession(IKeyboardHook keyboard)
    {
        _keyboard = keyboard;
        _ui = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        try
        {
            _surface = new PickerSurface();
            // из оконной процедуры подложки — отложенно: Finish уничтожает это самое окно
            _surface.LeftClick += () => _ui.BeginInvoke(Pick);
            _surface.RightClick += () => _ui.BeginInvoke(Cancel);

            _loupe = new LoupeWindow(GridSize);
            new WindowInteropHelper(_loupe).Owner = _surface.Handle; // владеемое окно всегда над владельцем
            Tick(); // первый кадр синхронно: лупа рождается сразу у курсора, без кадра в углу
            _loupe.Show();
            Tick(); // после Show известен реальный размер — уточняем позицию

            _keyboard.KeyDown += OnKey;
            _keyboard.KeyUp += OnKey;
            new Thread(SamplerLoop) { Name = "JarvisUtils.ColorPicker", IsBackground = true }.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"пипетка: старт: {ex}");
            Finish(null);
        }
    }

    public void Cancel() => Finish(null);

    private void Pick()
    {
        if (_finished) return;
        // свежий кадр ровно в момент клика/Enter (разовые ~5 мс на UI-потоке допустимы)
        var pixels = SampleNow(out _);
        if (pixels == null) { Finish(null); return; }
        uint c = pixels[GridSize / 2 * GridSize + GridSize / 2];
        Finish(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
    }

    /// <summary>Синхронный кадр на UI-потоке (старт, тесты).</summary>
    private void Tick()
    {
        if (_finished || _loupe == null) return;
        var pixels = SampleNow(out var logical);
        if (pixels == null) return;
        _loupe.Update(pixels, pixels[GridSize / 2 * GridSize + GridSize / 2]);
        _loupe.MoveNear(logical);
    }

    /// <summary>Снимок вокруг курсора в физических пикселях; logical — та же точка в координатах приложения (для лупы).</summary>
    private static uint[]? SampleNow(out WinApi.POINT logical)
    {
        WinApi.GetCursorPos(out logical);
        using var _ = PhysicalDpiScope.Enter();
        using var sampler = new ScreenSampler(GridSize);
        return WinApi.GetCursorPos(out var physical) && sampler.Sample(physical.X, physical.Y) ? (uint[])sampler.Pixels.Clone() : null;
    }

    // ── фоновый поток кадров ──

    private void SamplerLoop()
    {
        try
        {
            using var _ = PhysicalDpiScope.Enter(); // на весь поток: координаты и снимок — физические
            using var sampler = new ScreenSampler(GridSize);
            while (!_finished)
            {
                if (WinApi.GetCursorPos(out var p) && sampler.Sample(p.X, p.Y))
                {
                    // UI ещё не забрал прошлый кадр — просто подменяем его, очередь не растёт
                    if (Interlocked.Exchange(ref _pendingFrame, (uint[])sampler.Pixels.Clone()) == null)
                        _ui.BeginInvoke(ShowPendingFrame, DispatcherPriority.Render);
                }
                _wake.WaitOne(FrameMs); // сдвиг стрелкой будит раньше
            }
        }
        catch (Exception ex) { Log.Error($"пипетка: поток кадров: {ex}"); }
    }

    private void ShowPendingFrame()
    {
        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame == null || _finished || _loupe == null) return;
        _loupe.Update(frame, frame[GridSize / 2 * GridSize + GridSize / 2]);
        WinApi.GetCursorPos(out var logical);
        _loupe.MoveNear(logical);
    }

    // ── клавиатура ──

    /// <summary>Коллбэк low-level хука: только решение «глотать/нет», работа — после возврата из хука.</summary>
    private void OnKey(object? sender, KeyHookEventArgs e)
    {
        if (_finished || e.IsInjected) return;
        Action? action = e.Vk switch
        {
            VK_ESCAPE => Cancel,
            VK_RETURN or VK_SPACE => Pick,
            VK_LEFT => () => Nudge(-1, 0),
            VK_RIGHT => () => Nudge(1, 0),
            VK_UP => () => Nudge(0, -1),
            VK_DOWN => () => Nudge(0, 1),
            _ => null
        };
        if (action == null) return;
        e.Handled = true; // клавиша не дойдёт до приложения под пипеткой (и отпускание тоже)
        if (e.IsKeyDown) _ui.BeginInvoke(action);
    }

    /// <summary>Стрелки двигают курсор на 1 физический пиксель (с Shift — на 10).</summary>
    private void Nudge(int dx, int dy)
    {
        if (_finished) return;
        int step = (WinApi.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ? 10 : 1;
        using (PhysicalDpiScope.Enter())
        {
            WinApi.GetCursorPos(out var p);
            SetCursorPos(p.X + dx * step, p.Y + dy * step);
        }
        _wake.Set();
    }

    private void Finish(Color? result)
    {
        if (_finished) return;
        _finished = true;
        _wake.Set(); // фоновый поток выходит сам и освобождает свой GDI
        _keyboard.KeyDown -= OnKey;
        _keyboard.KeyUp -= OnKey;
        try { _loupe?.Close(); } catch { /* уже закрыта */ }
        _surface?.Dispose();
        Finished?.Invoke(result);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
