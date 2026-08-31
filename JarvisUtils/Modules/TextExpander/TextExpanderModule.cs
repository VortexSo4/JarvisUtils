using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
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

// ─── Основной модуль ────────────────────────────────────────────────
public sealed class TextExpanderModule : JarvisModuleBase, IModuleSettingsEditor
{
    private const bool TraceKeys = false;

    // Реестр встроенных функций
    private static readonly Dictionary<string, IExpansionFunction> Functions = new()
    {
        ["incr"] = new IncrFunction(),
        ["rand"] = new RandFunction(),
        ["date"] = new DateFunction(),
        ["time"] = new TimeFunction(),
        ["repeat"] = new RepeatFunction(),
        ["calc"] = new CalcFunction(),
    };

    // Кеш скомпилированных регулярных выражений для каждого правила
    private readonly ConcurrentDictionary<ExpansionRule, Regex> _regexCache = new();

    public sealed class Settings
    {
        public List<ExpansionRule> Rules { get; set; } = new();
    }

    private Settings _settings = null!;
    private readonly StringBuilder _buffer = new();
    private readonly KeyboardStateTracker _keyboard = new();
    private DispatcherTimer? _idle;
    private IntPtr _lastFocus = IntPtr.Zero;

    // ── UI редактора ──
    private static readonly Color CardIdle = Color.FromRgb(0x19, 0x1C, 0x25);
    private static readonly Color CardHover = Color.FromRgb(0x1F, 0x23, 0x30);
    private readonly List<ExpansionRule> _rows = new();
    private StackPanel? _rowsPanel;
    private TextBlock? _savedLabel;
    private DispatcherTimer? _saveDebounce;
    private bool _dirty;

    public override string DisplayName => "Текстовые сокращения";

    protected override void RegisterHotkeys() { } // нет хоткеев

    public override void Initialize(IJarvisServices services)
    {
        // Сначала вызываем базовую инициализацию, чтобы установить Services
        base.Initialize(services);

        // Теперь загружаем настройки
        _settings = Services.Settings.Load<Settings>(Name);
        if (_settings.Rules == null) _settings.Rules = new List<ExpansionRule>();

        Log.Info($"TextExpander: загружено правил — {_settings.Rules.Count}");

        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idle.Tick += (_, _) => _buffer.Clear();
    }

    protected override void OnEnabled()
    {
        _lastFocus = WinApi.GetForegroundWindow();
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
        Log.Info("TextExpander: подписка на клавиатуру снята");
    }

    private void OnKeyDown(object? sender, KeyHookEventArgs e)
    {
        if (e.IsInjected || Suspended) return;
        _keyboard.OnKey(e.Vk, true);

        var focus = WinApi.GetForegroundWindow();
        if (focus != _lastFocus) { _lastFocus = focus; _buffer.Clear(); }

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

    // ── Основная логика поиска и подстановки ──

    private void TryExpand()
    {
        var raw = _buffer.ToString();
        foreach (var rule in _settings.Rules.OrderByDescending(r => r.TriggerPattern.Length))
        {
            if (string.IsNullOrEmpty(rule.TriggerPattern)) continue;

            var regex = GetRegex(rule);
            var match = regex.Match(raw);
            if (!match.Success) continue;

            // Извлекаем параметры
            var args = new List<string>();
            int groupIndex = 1;
            while (true)
            {
                var groupName = $"p{groupIndex}";
                var group = match.Groups[groupName];
                if (!group.Success) break;
                args.Add(group.Value);
                groupIndex++;
            }

            // Подставляем параметры в шаблон замены
            var replacement = rule.ReplacementTemplate;
            for (int i = 0; i < args.Count; i++)
                replacement = replacement.Replace($"%{i + 1}", args[i]);

            // Обрабатываем встроенные функции
            var final = ExpandTokens(replacement);

            int len = match.Length;
            _buffer.Clear();
            Log.Info($"TextExpander: сработало '{match.Value}' (шаблон '{rule.TriggerPattern}')");
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                Services.Input.SendBackspaces(len);
                Services.Input.SendText(final);
            });
            break;
        }
    }

    private static Regex BuildRegexFromPattern(string pattern)
    {
        var escaped = Regex.Escape(pattern);
        var regexPattern = Regex.Replace(escaped, @"%(\d)", m =>
        {
            int num = int.Parse(m.Groups[1].Value);
            return $"(?<p{num}>.+?)";
        });
        return new Regex(regexPattern + "$", RegexOptions.Compiled);
    }

    private Regex GetRegex(ExpansionRule rule)
    {
        return _regexCache.GetOrAdd(rule, r => BuildRegexFromPattern(r.TriggerPattern));
    }

    private static string ExpandTokens(string input)
    {
        var pattern = @"\{(\w+)(?:\(([^)]*)\))?\}";
        return Regex.Replace(input, pattern, match =>
        {
            string funcName = match.Groups[1].Value;
            string argsRaw = match.Groups[2].Success ? match.Groups[2].Value : "";
            var args = argsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (Functions.TryGetValue(funcName, out var func))
            {
                var result = func.Evaluate(args);
                return result ?? match.Value;
            }
            return match.Value;
        });
    }

    // ══════════════ РЕДАКТОР НАСТРОЕК ══════════════

    public FrameworkElement BuildEditor()
    {
        _rows.Clear();
        _rows.AddRange(_settings.Rules);

        var root = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        root.Children.Add(new TextBlock
        {
            Text = "Правило: шаблон триггера с маркерами %1, %2, … (например :rand%1-%2:) и шаблон замены, в котором можно использовать те же маркеры и функции: {rand(1,100)}, {date}, {time}, {repeat(3,hello)}, {incr(1,10,n)}, {calc(12+34)}.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        });

        var head = new Grid { Margin = new Thickness(12, 18, 12, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var h1 = HeadLabel("ШАБЛОН ТРИГГЕРА");
        var h2 = HeadLabel("ШАБЛОН ЗАМЕНЫ");
        Grid.SetColumn(h1, 0);
        Grid.SetColumn(h2, 1);
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
            Content = "+  Добавить правило",
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

        root.Unloaded += (_, _) => FlushSave();
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
        Text = "Пока нет правил — добавьте первое.",
        Foreground = JarvisTheme.ForegroundBrush(0.4),
        FontSize = 12.5,
        Margin = new Thickness(12, 8, 0, 8)
    };

    private Border BuildRow(ExpansionRule model)
    {
        var trigger = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.NoWrap
        };
        trigger.SetBinding(TextBox.TextProperty, new Binding(nameof(ExpansionRule.TriggerPattern))
        { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        trigger.TextChanged += (_, _) => ScheduleSave();

        var repl = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 38,
            MaxHeight = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        repl.SetBinding(TextBox.TextProperty, new Binding(nameof(ExpansionRule.ReplacementTemplate))
        { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        repl.TextChanged += (_, _) => ScheduleSave();

        var del = new Button { Content = "×" };
        del.SetResourceReference(Button.StyleProperty, "IconButton");
        del.Margin = new Thickness(10, 4, 0, 0);
        del.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(trigger, 0);
        Grid.SetColumn(repl, 2);
        Grid.SetColumn(del, 3);

        var arrow = new TextBlock
        {
            Text = "→",
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        Grid.SetColumn(arrow, 1);

        grid.Children.Add(trigger);
        grid.Children.Add(arrow);
        grid.Children.Add(repl);
        grid.Children.Add(del);

        var card = new Border
        {
            Background = new SolidColorBrush(CardIdle),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
            Tag = trigger
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
            _rowsPanel.Children.Clear();

        var model = new ExpansionRule();
        _rows.Add(model);
        var card = BuildRow(model);
        _rowsPanel.Children.Add(card);
        card.Loaded += (_, _) => ((TextBox)card.Tag!).Focus();
        ScheduleSave();
    }

    private void RemoveRow(ExpansionRule model, Border card)
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
        _settings.Rules = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.TriggerPattern))
            .ToList();
        SaveSettings(_settings);
        FlashSaved();
    }

    private void FlashSaved()
    {
        if (_savedLabel == null) return;
        _savedLabel.BeginAnimation(UIElement.OpacityProperty, null);
        _savedLabel.Opacity = 1;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(1200) };
        _savedLabel.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}