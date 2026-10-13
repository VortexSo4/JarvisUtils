using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.TextExpander;

// ─── Основной модуль ────────────────────────────────────────────────
public sealed class TextExpanderModule : JarvisModuleBase, IModuleSettingsEditor
{
    private static readonly bool TraceKeys = false; // не const: иначе предупреждения о недостижимом коде
    private const int BufferLimit = 100;
    private const long UndoWindowMs = 30_000;

    public sealed class Settings
    {
        public List<ExpansionRule> Rules { get; set; } = new();
    }

    /// <summary>Последняя «простая» подстановка — её можно отменить Backspace'ом.</summary>
    private sealed record UndoState(IntPtr Hwnd, int InsertedUnits, string Trigger, long Tick);

    private Settings _settings = null!;
    private readonly TriggerMatcher _matcher = new();
    private CounterStore _counters = null!;
    private ExpansionSender _sender = null!;
    private readonly StringBuilder _buffer = new();
    private readonly KeyboardStateTracker _keyboard = new();
    private DispatcherTimer? _idle;
    private IntPtr _lastFocus = IntPtr.Zero;
    private uint _lastFocusPid;
    private bool _busy;      // идёт подстановка с интерактивом/асинхронными функциями
    private long _keySerial; // счётчик «настоящих» нажатий — понять, не печатал ли пользователь во время подстановки
    private UndoState? _undo;

    public override string DisplayName => "Текстовые сокращения";
    public override string Icon => FluentIcons.Lightning;

    protected override void RegisterHotkeys() { } // нет хоткеев

    public override void Initialize(IJarvisServices services)
    {
        // Сначала вызываем базовую инициализацию, чтобы установить Services
        base.Initialize(services);

        // Теперь загружаем настройки
        _settings = Services.Settings.Load<Settings>(Name);
        _settings.Rules ??= new List<ExpansionRule>();
        _counters = new CounterStore(Services.Settings);
        _sender = new ExpansionSender(Services.Input);

        Log.Info($"TextExpander: загружено правил — {_settings.Rules.Count}");

        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idle.Tick += (_, _) => { _idle.Stop(); _buffer.Clear(); };

        Prewarm();
    }

    /// <summary>
    /// Компиляция триггеров и прогрев Roslyn — в фоне, а не на первом нажатии в хуке.
    /// </summary>
    private void Prewarm()
    {
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            foreach (var rule in _settings.Rules)
            {
                var compiled = _matcher.Get(rule);
                if (!compiled.IsValid) Log.Warn($"TextExpander: правило '{rule.TriggerPattern}' отключено — {compiled.Error}");
            }
            if (_settings.Rules.Any(r => r.Enabled && (r.ReplacementTemplate ?? "").Contains("{=")))
                ScriptRunner.Warmup();
        });
    }

    protected override void OnEnabled()
    {
        _lastFocus = WinApi.GetForegroundWindow();
        _lastFocusPid = ForegroundHelper.ProcessIdOf(_lastFocus);
        Services.Keyboard.KeyDown += OnKeyDown;
        Services.Keyboard.KeyUp += OnKeyUp;
        Services.Keyboard.MouseDown += OnGlobalMouseDown;
        Log.Info("TextExpander: подписка на клавиатуру активна");
    }

    protected override void OnDisabled()
    {
        Services.Keyboard.KeyDown -= OnKeyDown;
        Services.Keyboard.KeyUp -= OnKeyUp;
        Services.Keyboard.MouseDown -= OnGlobalMouseDown;
        _buffer.Clear();
        _undo = null;
        Log.Info("TextExpander: подписка на клавиатуру снята");
    }

    private static bool IsModifier(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x14;

    // ВНИМАНИЕ: коллбэк low-level хука на UI-потоке. Всё, что дольше пары миллисекунд,
    // замораживает ввод во всей системе — тяжёлое уходит в Dispatcher.BeginInvoke.
    private void OnKeyDown(object? sender, KeyHookEventArgs e)
    {
        if (e.IsInjected) return;
        _keyboard.OnKey(e.Vk, true);
        if (Suspended) return;

        bool modifier = IsModifier(e.Vk);
        if (!modifier) _keySerial++;

        var focus = WinApi.GetForegroundWindow();
        if (focus != _lastFocus)
        {
            _lastFocus = focus;
            _lastFocusPid = ForegroundHelper.ProcessIdOf(focus);
            _buffer.Clear();
            _undo = null;
        }

        if (_busy) { _buffer.Clear(); return; }

        // Отмена: Backspace сразу после подстановки (до любой другой клавиши).
        // Удерживаемый Shift от «:» шлёт автоповтор keydown — модификаторы отмену не сбрасывают.
        if (_undo != null && !modifier)
        {
            var undo = _undo;
            _undo = null;
            if (e.Vk == 0x08 && !_keyboard.HasNonShiftModifier && undo.Hwnd == focus &&
                Environment.TickCount64 - undo.Tick < UndoWindowMs)
            {
                e.Handled = true; // сам Backspace не доходит до приложения
                _buffer.Clear();
                Log.Info($"TextExpander: отмена подстановки '{undo.Trigger}'");
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => RunUndo(undo));
                return;
            }
        }

        _idle!.Stop();
        _idle.Start();

        switch (e.Vk)
        {
            case 0x08: // Backspace
                if (_buffer.Length > 0) _buffer.Length--;
                return;
            case 0x1B or 0x0D or 0x09: // Esc/Enter/Tab
                _buffer.Clear();
                return;
            case >= 0x70 and <= 0x87: // F-keys
                _buffer.Clear();
                return;
            case 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E:
            case 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5:
                return;
        }
        if (_keyboard.HasNonShiftModifier) { _buffer.Clear(); return; }

        var ch = _keyboard.CharFromKey(e.Vk, e.ScanCode);
        if (ch is null)
        {
            if (TraceKeys) Log.Info($"TE: vk=0x{e.Vk:X2} не дала символ, буфер сброшен (был '{_buffer}')");
            _buffer.Clear();
            return;
        }

        _buffer.Append(ch.Value);
        if (_buffer.Length > BufferLimit) _buffer.Remove(0, _buffer.Length - BufferLimit);
        if (TraceKeys) Log.Info($"TE: '{ch}' → буфер='{_buffer}'");

        // Свои окна (поле {input}, настройки) не расширяем — там набирают сами шаблоны
        if (_lastFocusPid == (uint)Environment.ProcessId) return;
        TryExpand();
    }

    private void OnKeyUp(object? sender, KeyHookEventArgs e)
    {
        if (e.IsInjected) return;
        _keyboard.OnKey(e.Vk, false);
    }

    private void OnGlobalMouseDown(object? sender, MouseHookEventArgs e)
    {
        _buffer.Clear();
        _undo = null;
    }

    // ── Поиск и подстановка ──

    private void TryExpand()
    {
        var match = _matcher.Match(_settings.Rules, _buffer.ToString(), () => ForegroundHelper.ProcessName(_lastFocusPid));
        if (match == null) return;

        _buffer.Clear();
        Log.Info($"TextExpander: сработало '{match.Value}' (шаблон '{match.Rule.TriggerPattern}')");
        var hwnd = _lastFocus;
        long serial = _keySerial;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _ = RunExpansionAsync(match, hwnd, serial));
    }

    /// <summary>
    /// Конвейер: стереть триггер → вычислить шаблон (может показать выбор/ввод) →
    /// вернуть фокус исходному окну → вставить. Отмена интерактива возвращает набранный триггер.
    /// Для шаблонов без асинхронных функций всё выполняется синхронно, одной операцией диспетчера.
    /// </summary>
    private async Task RunExpansionAsync(TriggerMatch match, IntPtr hwnd, long serial)
    {
        _busy = true;
        var title = string.IsNullOrWhiteSpace(match.Rule.Name) ? match.Rule.TriggerPattern : match.Rule.Name!;
        var session = new InteractiveSession(Services, title);
        try
        {
            Services.Input.SendBackspaces(match.Length);

            var ctx = new ExpansionContext
            {
                Args = match.Args,
                Named = match.Named,
                Services = Services,
                Ui = session,
                Counters = _counters,
                Title = title
            };
            var output = await TemplateEngine.EvaluateAsync(match.Rule.ReplacementTemplate ?? "", ctx);

            if (session.FocusLost) return; // кликнули мимо окна ввода — пользователь ушёл, ничего не печатаем

            if (ctx.UsedInteractiveUi && !await ForegroundHelper.RestoreAsync(hwnd))
            {
                if (!output.Cancelled) await CopyAsFallbackAsync(output.Text);
                return;
            }

            if (output.Cancelled)
            {
                Services.Input.SendText(match.Value); // Esc — вернуть набранное
                return;
            }

            output.ApplyCase(match.Case);
            if (output.Errors.Count > 0)
            {
                Log.Warn($"TextExpander: '{match.Rule.TriggerPattern}': {string.Join("; ", output.Errors)}");
                Services.Overlay.ShowToast(output.Errors[0], DisplayName, 3000);
            }

            await _sender.SendAsync(output);

            // Пока шла подстановка, пользователь мог продолжить печатать — тогда отмена неуместна
            if (output.IsPlain && output.Text.Length > 0 && !ctx.UsedInteractiveUi && serial == _keySerial)
                _undo = new UndoState(hwnd, ExpansionOutput.CaretUnits(output.Text), match.Value, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            Log.Error($"TextExpander: подстановка '{match.Rule.TriggerPattern}': {ex}");
        }
        finally
        {
            _busy = false;
            _buffer.Clear();
        }
    }

    private async void RunUndo(UndoState undo)
    {
        try { await _sender.UndoAsync(undo.InsertedUnits, undo.Trigger); }
        catch (Exception ex) { Log.Error($"TextExpander: отмена: {ex}"); }
    }

    private async Task CopyAsFallbackAsync(string text)
    {
        if (text.Length == 0) return;
        var clipboard = Services.Clipboard;
        await Task.Run(() => clipboard.SetText(text));
        Services.Overlay.ShowToast("Не удалось вернуть фокус окну — результат скопирован в буфер обмена", DisplayName, 3000);
    }

    /// <summary>Интерактив одной подстановки: список выбора (оверлей) и окно ввода.</summary>
    private sealed class InteractiveSession(IJarvisServices services, string title) : IExpansionUi
    {
        public bool FocusLost { get; private set; }

        public Task<string?> ChooseAsync(string popupTitle, IReadOnlyList<string> options)
        {
            var tcs = new TaskCompletionSource<string?>();
            services.Overlay.ShowListPopup(new ListPopupRequest
            {
                Title = popupTitle,
                Items = options.Select(o => new OverlayListItem(TextUtil.Preview(o, 80))).ToList(),
                OnPick = i => tcs.TrySetResult(options[i]),
                OnCancel = () => tcs.TrySetResult(null)
            });

            // Попап может закрыть другой попап (ForceClose без OnCancel) — тогда подстановка
            // висела бы вечно. Ловим закрытие окна; результат ставим отложенно, потому что при
            // выборе Close() вызывается ДО OnPick и иначе выбор считался бы отменой.
            var popup = Application.Current.Windows.OfType<ListPopupWindow>().LastOrDefault();
            if (popup != null)
                popup.Closed += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => tcs.TrySetResult(null));
            return tcs.Task;
        }

        public async Task<string?> PromptAsync(string label, string? initial)
        {
            var window = new InputPromptWindow(title, label, initial);
            window.Show();
            var result = await window.Result;
            if (window.FocusLost) FocusLost = true;
            return result;
        }
    }

    // ══════════════ РЕДАКТОР НАСТРОЕК ══════════════

    public FrameworkElement BuildEditor() => new TextExpanderEditor(
        _settings.Rules,
        _matcher,
        rules => { _settings.Rules = rules; SaveSettings(_settings); },
        DryRunAsync).Build();

    /// <summary>Проверка в редакторе: поиск правила и вычисление без отправки.</summary>
    private async Task<(TriggerMatch? Match, ExpansionOutput? Output)> DryRunAsync(IReadOnlyList<ExpansionRule> rules, string text)
    {
        var match = _matcher.Match(rules, text, processName: null, ignoreAppFilter: true);
        if (match == null) return (null, null);
        var ctx = new ExpansionContext
        {
            Args = match.Args,
            Named = match.Named,
            Services = Services,
            Counters = _counters,
            DryRun = true,
            Title = match.Rule.Name ?? match.Rule.TriggerPattern
        };
        var output = await TemplateEngine.EvaluateAsync(match.Rule.ReplacementTemplate ?? "", ctx);
        output.ApplyCase(match.Case);
        return (match, output);
    }
}
