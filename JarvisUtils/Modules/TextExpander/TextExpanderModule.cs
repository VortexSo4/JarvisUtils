using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.TextExpander;

public sealed class TextExpanderModule : JarvisModuleBase, IModuleSettingsEditor
{
    /// <summary>
    /// true → писать в jarvis.log каждый распознанный символ и буфер (для диагностики).
    /// ВНИМАНИЕ: в лог попадёт всё, что вы набираете, — включать только на время
    /// отладки и выключать после. По умолчанию false.
    /// </summary>
    private const bool TraceKeys = true;

    /// <summary>
    /// Параметризованный триггер последовательности чисел: :incr1-10: или :incr1-10n:
    /// (n на конце — каждое число на новой строке). Ведущие нули в начальном числе
    /// (например :incr01-10:) сохраняют фиксированную ширину для всех чисел диапазона.
    /// Разворачивается целиком за один вызов — состояние между вызовами не хранится.
    /// </summary>
    private static readonly Regex IncrPattern = new(@":incr(\d+)-(\d+)(n)?:$", RegexOptions.Compiled);

    public sealed class AbbrRow
    {
        public string Abbr { get; set; } = "";
        public string Replacement { get; set; } = "";
    }

    public sealed class Settings
    {
        public Dictionary<string, string> Abbreviations { get; set; } = new()
        {
            [":em"] = "user@example.com",
            [":nm"] = "Иван Иванов",
            [":dt"] = "{date}",
            [":tm"] = "{time}",
            [":sig"] = "С уважением,\nИван Иванов\nuser@example.com"
        };
    }

    private Settings _settings = null!;
    private readonly StringBuilder _buffer = new();
    private readonly KeyboardStateTracker _keyboard = new();
    private DispatcherTimer? _idle;
    private IntPtr _lastFocus = IntPtr.Zero;

    // ── UI редактора (пересоздаётся при каждом открытии настроек) ──
    private static readonly Color CardIdle = Color.FromRgb(0x19, 0x1C, 0x25);
    private static readonly Color CardHover = Color.FromRgb(0x1F, 0x23, 0x30);
    private readonly List<AbbrRow> _rows = [];
    private StackPanel? _rowsPanel;
    private TextBlock? _savedLabel;
    private DispatcherTimer? _saveDebounce;
    private bool _dirty;

    public override string DisplayName => "Текстовые сокращения";

    protected override void RegisterHotkeys() { } // realtime-матчинг через keyboard hook

    public override void Initialize(IJarvisServices services)
    {
        _settings = services.Settings.Load<Settings>(Name);
        Log.Info($"TextExpander: загружено сокращений — {_settings.Abbreviations.Count}"); // маячок: пусто = файл настроек пуст
        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idle.Tick += (_, _) => _buffer.Clear();
        base.Initialize(services);
    }

    protected override void OnEnabled()
    {
        _lastFocus = WinApi.GetForegroundWindow();
        Services.Keyboard.KeyDown += OnKeyDown;
        Services.Keyboard.KeyUp += OnKeyUp;
        Services.Keyboard.MouseDown += OnGlobalMouseDown; // клик мышью = перенос каретки → контекст набора сброшен
        Log.Info("TextExpander: подписка на клавиатуру активна"); // маячок: события хука доходят до модуля
    }

    protected override void OnDisabled()
    {
        Services.Keyboard.KeyDown -= OnKeyDown;
        Services.Keyboard.KeyUp -= OnKeyUp;
        Services.Keyboard.MouseDown -= OnGlobalMouseDown;
        _buffer.Clear();
        Log.Info("TextExpander: подписка на клавиатуру снята");
    }

    private void OnKeyDown(object? sender, KeyHookEventArgs e)
    {
        if (e.IsInjected || Suspended) return; // собственные SendInput-события не буферизуем
        _keyboard.OnKey(e.Vk, true);

        // смена активного окна — набор идёт в другое поле, буфер сбрасываем
        var focus = WinApi.GetForegroundWindow();
        if (focus != _lastFocus) { _lastFocus = focus; _buffer.Clear(); }

        _idle!.Stop();
        _idle.Start();

        switch (e.Vk)
        {
            case 0x08: // Backspace — откат символа буфера
                if (_buffer.Length > 0) _buffer.Length--;
                return;
            case 0x1B or 0x0D or 0x09: // Esc / Enter / Tab
                _buffer.Clear();
                return;
            case >= 0x70 and <= 0x87: // F1–F24
                _buffer.Clear();
                return;
            case 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E: // навигация
            case 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5: // модификаторы
                return; // сами по себе буфер не ломают
        }
        if (_keyboard.HasNonShiftModifier) { _buffer.Clear(); return; } // Ctrl+V / Alt+Tab и т.п. — не текст

        var ch = _keyboard.CharFromKey(e.Vk, e.ScanCode);
        if (ch is null)
        {
            if (TraceKeys) Log.Info($"TE: vk=0x{e.Vk:X2} не дала символ, буфер сброшен (был '{_buffer}')");
            _buffer.Clear();
            return;
        }

        _buffer.Append(ch.Value);
        if (_buffer.Length > 100) _buffer.Remove(0, _buffer.Length - 100);
        if (TraceKeys) Log.Info($"TE: '{ch}' → буфер='{_buffer}'");
        TryExpand();
    }

    private void OnKeyUp(object? sender, KeyHookEventArgs e)
    {
        if (e.IsInjected) return;
        _keyboard.OnKey(e.Vk, false);
    }

    private void OnGlobalMouseDown(object? sender, MouseHookEventArgs e) => _buffer.Clear();

    private void TryExpand()
    {
        var raw = _buffer.ToString();

        // ── Параметризованный триггер :incrN-M: / :incrN-Mn: — проверяем первым,
        // т.к. это не литерал из словаря, а генерируемое значение ──
        var incrMatch = IncrPattern.Match(raw);
        if (incrMatch.Success)
        {
            var replacement = BuildIncrSequence(incrMatch);
            if (replacement != null)
            {
                Expand(incrMatch.Value, replacement);
                return;
            }
            // от→до некорректны (например from > to) — просто не разворачиваем,
            // даём человеку допечатать/поправить дальше
        }

        string? best = null;
        foreach (var abbr in _settings.Abbreviations.Keys.OrderByDescending(a => a.Length))
        {
            if (abbr.Length == 0) continue;
            if (MatchesSuffix(raw, abbr))
            {
                best = abbr;
                break;
            }
        }
        if (best == null) return;

        var repl = ExpandTokens(_settings.Abbreviations[best]);
        Expand(best, repl);
    }

    /// <summary>
    /// Разворачивает :incrN-M(n)?: в строку со всеми числами диапазона [from..to] включительно.
    /// Без n — через пробел в одну строку. С n — каждое число на отдельной строке.
    /// Ведущие нули в исходном "from" (например "01") сохраняют фиксированную ширину у всех чисел.
    /// Возвращает null, если границы некорректны (from > to).
    /// </summary>
    private static string? BuildIncrSequence(Match m)
    {
        var fromRaw = m.Groups[1].Value;
        var toRaw = m.Groups[2].Value;
        bool newline = m.Groups[3].Success;

        int from = int.Parse(fromRaw);
        int to = int.Parse(toRaw);
        if (from > to) return null;

        int width = fromRaw.StartsWith('0') ? fromRaw.Length : 0; // ширина с нулями, только если явно задана
        var sep = newline ? "\n" : " ";

        var sb = new StringBuilder();
        for (int i = from; i <= to; i++)
        {
            if (i > from) sb.Append(sep);
            sb.Append(width > 0 ? i.ToString().PadLeft(width, '0') : i.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Стирает набранный триггер и печатает замену. Общая точка выхода для всех видов срабатывания.</summary>
    private void Expand(string trigger, string replacement)
    {
        var typed = _buffer.ToString();
        _buffer.Clear();
        int len = trigger.Length; // ToEnglish взаимно-однозначен: длина = число реально набранных символов
        Log.Info($"TextExpander: сработало '{trigger}' (набрано '{typed}')"); // маячок: матч найден

        // SendInput не вызываем прямо внутри hook-коллбэка — откладываем на следующую итерацию диспетчера
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Services.Input.SendBackspaces(len);
            Services.Input.SendText(replacement);
        });
    }

    /// <summary>Регистронезависимый суффикс-матч с защитой от срабатывания в середине слова.</summary>
    private static bool MatchesSuffix(string text, string abbr)
    {
        if (!text.EndsWith(abbr, StringComparison.OrdinalIgnoreCase)) return false;
        int start = text.Length - abbr.Length;
        if (start > 0 && char.IsLetterOrDigit(text[start - 1]) && char.IsLetterOrDigit(abbr[0])) return false;
        return true;
    }

    private static string ExpandTokens(string s) =>
        s.Replace("{date}", DateTime.Now.ToString("dd.MM.yyyy")).Replace("{time}", DateTime.Now.ToString("HH:mm"));

    // ══════════════ Редактор настроек ══════════════

    public FrameworkElement BuildEditor()
    {
        _rows.Clear();
        _rows.AddRange(_settings.Abbreviations.Select(kv => new AbbrRow { Abbr = kv.Key, Replacement = kv.Value }));

        var root = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        root.Children.Add(new TextBlock
        {
            Text = "Наберите сокращение в любом поле ввода — оно развернётся в замену. Регистр и раскладка не важны. В замене поддерживаются {date} и {time}. Отдельно доступен :incrN-M: (и :incrN-Mn: с переносом строк) — разворачивается в числа от N до M.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        });

        var head = new Grid { Margin = new Thickness(12, 18, 12, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var h1 = HeadLabel("СОКРАЩЕНИЕ"); Grid.SetColumn(h1, 0);
        var h2 = HeadLabel("ЗАМЕНА"); Grid.SetColumn(h2, 1);
        head.Children.Add(h1);
        head.Children.Add(h2);
        root.Children.Add(head);

        _rowsPanel = new StackPanel();
        root.Children.Add(_rowsPanel);
        foreach (var row in _rows) _rowsPanel.Children.Add(BuildRow(row));
        if (_rows.Count == 0) _rowsPanel.Children.Add(EmptyHint());

        _savedLabel = new TextBlock
        {
            Text = "✓  сохранено",
            Foreground = JarvisTheme.AccentBrush,
            FontSize = 11,
            Opacity = 0,
            Margin = new Thickness(12, 8, 0, 0)
        };
        root.Children.Add(_savedLabel);

        var add = new Button
        {
            Content = "+  Добавить сокращение",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        add.Click += (_, _) => AddRow();
        root.Children.Add(add);

        root.Children.Add(new TextBlock
        {
            Text = "Изменения сохраняются автоматически.",
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0)
        });

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce!.Stop(); FlushSave(); };

        root.Unloaded += (_, _) => FlushSave(); // досохраняем при закрытии настроек
        return root;
    }

    private static TextBlock HeadLabel(string text) => new()
    {
        Text = text,
        Foreground = JarvisTheme.ForegroundBrush(0.4),
        FontSize = 10.5,
        FontWeight = FontWeights.SemiBold
    };

    private static TextBlock EmptyHint() => new()
    {
        Text = "Пока нет сокращений — добавьте первое.",
        Foreground = JarvisTheme.ForegroundBrush(0.4),
        FontSize = 12.5,
        Margin = new Thickness(12, 8, 0, 8)
    };

    private Border BuildRow(AbbrRow model)
    {
        var abbr = new TextBox
        {
            Width = 130,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Top
        };
        abbr.SetBinding(TextBox.TextProperty, new Binding(nameof(AbbrRow.Abbr))
        { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        abbr.TextChanged += (_, _) => ScheduleSave();

        var arrow = new TextBlock
        {
            Text = "→",
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 11, 10, 0)
        };

        var repl = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 38,
            MaxHeight = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        repl.SetBinding(TextBox.TextProperty, new Binding(nameof(AbbrRow.Replacement))
        { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        repl.TextChanged += (_, _) => ScheduleSave();

        var del = new Button { Content = "×" };
        del.SetResourceReference(Button.StyleProperty, "IconButton");
        del.Margin = new Thickness(10, 4, 0, 0);
        del.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(abbr, 0); Grid.SetColumn(arrow, 1); Grid.SetColumn(repl, 2); Grid.SetColumn(del, 3);
        grid.Children.Add(abbr);
        grid.Children.Add(arrow);
        grid.Children.Add(repl);
        grid.Children.Add(del);

        var card = new Border
        {
            Background = new SolidColorBrush(CardIdle), // не frozen — цвет анимируется на hover
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
            Tag = abbr // для фокуса после добавления
        };
        card.MouseEnter += (_, _) => Paint(card, CardHover);
        card.MouseLeave += (_, _) => Paint(card, CardIdle);
        del.Click += (_, _) => RemoveRow(model, card);
        return card;
    }

    private static void Paint(Border card, Color color) =>
        ((SolidColorBrush)card.Background).BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(color, TimeSpan.FromMilliseconds(90)));

    private void AddRow()
    {
        if (_rowsPanel!.Children.Count == 1 && _rowsPanel.Children[0] is TextBlock)
            _rowsPanel.Children.Clear(); // убрать empty-hint

        var model = new AbbrRow();
        _rows.Add(model);
        var card = BuildRow(model);
        _rowsPanel.Children.Add(card);
        card.Loaded += (_, _) => ((TextBox)card.Tag!).Focus();
        ScheduleSave();
    }

    private void RemoveRow(AbbrRow model, Border card)
    {
        _rows.Remove(model);
        _rowsPanel!.Children.Remove(card);
        if (_rows.Count == 0) _rowsPanel.Children.Add(EmptyHint());
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _dirty = true;
        _saveDebounce?.Stop();
        _saveDebounce?.Start();
    }

    private void FlushSave()
    {
        if (!_dirty || _rowsPanel == null) return;
        _dirty = false;
        _saveDebounce?.Stop();
        _settings.Abbreviations = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Abbr))
            .GroupBy(r => r.Abbr.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Replacement);
        SaveSettings(_settings);
        FlashSaved();
    }

    private void FlashSaved()
    {
        if (_savedLabel == null) return;
        _savedLabel.BeginAnimation(UIElement.OpacityProperty, null); // перезапуск с чистого состояния
        _savedLabel.Opacity = 1;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(1200) };
        _savedLabel.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}