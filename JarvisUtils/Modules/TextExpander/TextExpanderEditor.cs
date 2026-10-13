using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisUtils.Core;
using JarvisUtils.UI.Settings;
using JarvisUtils.UI.Theme;

namespace JarvisUtils.Modules.TextExpander;

/// <summary>
/// Страница настроек сокращений: карточки правил с опциями, поиск, проверка «вживую»
/// (dry run — ничего не отправляется) и справка по синтаксису. Автосохранение с задержкой.
/// </summary>
internal sealed class TextExpanderEditor
{
    internal delegate Task<(TriggerMatch? Match, ExpansionOutput? Output)> DryRun(IReadOnlyList<ExpansionRule> rules, string text);

    private static readonly Color CardIdle = Color.FromRgb(0x19, 0x1C, 0x25);
    private static readonly Color CardHover = Color.FromRgb(0x1F, 0x23, 0x30);
    private static readonly Color ErrorColor = Color.FromRgb(0xFF, 0x7A, 0x7A);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly List<ExpansionRule> _rows;
    private readonly TriggerMatcher _matcher;
    private readonly Action<List<ExpansionRule>> _save;
    private readonly DryRun _dryRun;
    private readonly List<RuleCard> _cards = [];

    private StackPanel _rowsPanel = null!;
    private TextBlock _emptyHint = null!;
    private TextBox _search = null!;
    private FrameworkElement _savedLabel = null!;
    private DispatcherTimer _saveDebounce = null!;
    private bool _dirty;

    private TextBox _testBox = null!;
    private TextBlock _testStatus = null!;
    private TextBlock _testGlyph = null!;
    private TextBlock _testOutput = null!;
    private Border _testOutputBox = null!;
    private TextBlock _testErrors = null!;
    private DispatcherTimer _testDebounce = null!;
    private int _testVersion;

    public TextExpanderEditor(List<ExpansionRule> rules, TriggerMatcher matcher, Action<List<ExpansionRule>> save, DryRun dryRun)
    {
        _rows = new List<ExpansionRule>(rules); // рабочая копия: новые пустые правила живут здесь до заполнения
        _matcher = matcher;
        _save = save;
        _dryRun = dryRun;
    }

    public FrameworkElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        root.Children.Add(new TextBlock
        {
            Text = "Наберите триггер в любом приложении — он заменится результатом шаблона. " +
                   "В триггере можно ловить числа и слова ({n}, {w}), в замене — вызывать функции ({date}, {clip}, {calc(…)}). " +
                   "Backspace сразу после замены возвращает набранный текст.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(BuildToolbar());

        _rowsPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Add(_rowsPanel);
        foreach (var rule in _rows) AddCard(rule);

        _emptyHint = new TextBlock
        {
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            FontSize = 12.5,
            Margin = new Thickness(12, 8, 0, 8)
        };
        root.Children.Add(_emptyHint);
        UpdateEmptyHint();

        var saved = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, Margin = new Thickness(12, 6, 0, 0) };
        saved.Children.Add(FluentIcons.Glyph(FluentIcons.Check, 11, JarvisTheme.AccentBrush));
        saved.Children.Add(new TextBlock { Text = "сохранено", Foreground = JarvisTheme.AccentBrush, FontSize = 11, Margin = new Thickness(6, 0, 0, 0) });
        _savedLabel = saved;
        root.Children.Add(saved);

        root.Children.Add(SettingsUi.Section("ПРОВЕРКА"));
        root.Children.Add(BuildTester());

        root.Children.Add(BuildReference());

        root.Children.Add(new TextBlock
        {
            Text = "Изменения сохраняются автоматически.",
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            FontSize = 11,
            Margin = new Thickness(0, 14, 0, 0)
        });

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); FlushSave(); };
        _testDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _testDebounce.Tick += (_, _) => { _testDebounce.Stop(); _ = RunTestAsync(); };

        root.Unloaded += (_, _) => FlushSave();
        return root;
    }

    // ── Панель: поиск + добавление ──

    private FrameworkElement BuildToolbar()
    {
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _search = new TextBox { Padding = new Thickness(16, 7, 9, 7) }; // текст после иконки поиска
        _search.TextChanged += (_, _) => ApplyFilter();
        var searchHost = WithPlaceholder(_search, "Поиск по триггеру, замене или описанию");
        var icon = FluentIcons.Glyph(FluentIcons.Search, 13, JarvisTheme.ForegroundBrush(0.4));
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(12, 0, 0, 0);
        icon.IsHitTestVisible = false;
        searchHost.Children.Add(icon);
        Grid.SetColumn(searchHost, 0);
        grid.Children.Add(searchHost);

        var add = new Button { Content = FluentIcons.WithLabel(FluentIcons.Add, "Добавить правило", 12), Margin = new Thickness(10, 0, 0, 0) };
        add.Click += (_, _) => AddRule();
        Grid.SetColumn(add, 1);
        grid.Children.Add(add);
        return grid;
    }

    private void ApplyFilter()
    {
        var q = _search.Text.Trim();
        foreach (var card in _cards)
        {
            var r = card.Model;
            bool visible = q.Length == 0 ||
                           (r.TriggerPattern ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                           (r.ReplacementTemplate ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                           (r.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            card.Root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        if (_rows.Count == 0) _emptyHint.Text = "Пока нет правил — добавьте первое.";
        else if (_cards.All(c => c.Root.Visibility != Visibility.Visible)) _emptyHint.Text = "Ничего не найдено.";
        else { _emptyHint.Visibility = Visibility.Collapsed; return; }
        _emptyHint.Visibility = Visibility.Visible;
    }

    private void AddRule()
    {
        if (_search.Text.Length > 0) _search.Text = ""; // новое правило должно быть видно
        var model = new ExpansionRule();
        _rows.Add(model);
        var card = AddCard(model);
        UpdateEmptyHint();
        card.Root.Loaded += (_, _) => card.Trigger.Focus();
        card.Root.BringIntoView();
        ScheduleSave();
    }

    private RuleCard AddCard(ExpansionRule model)
    {
        var card = new RuleCard(this, model);
        _cards.Add(card);
        _rowsPanel.Children.Add(card.Root);
        return card;
    }

    private void RemoveRule(RuleCard card)
    {
        _rows.Remove(card.Model);
        _cards.Remove(card);
        _rowsPanel.Children.Remove(card.Root);
        UpdateEmptyHint();
        ScheduleSave();
        ScheduleTest();
    }

    // ── Сохранение ──

    private void ScheduleSave()
    {
        _dirty = true;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void FlushSave()
    {
        if (!_dirty) return;
        _dirty = false;
        _saveDebounce.Stop();
        _save(_rows.Where(r => !string.IsNullOrWhiteSpace(r.TriggerPattern)).ToList());
        FlashSaved();
    }

    private void FlashSaved()
    {
        _savedLabel.BeginAnimation(UIElement.OpacityProperty, null);
        _savedLabel.Opacity = 1;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(1200) };
        _savedLabel.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ── Проверка (dry run) ──

    private FrameworkElement BuildTester()
    {
        var panel = new StackPanel();

        _testBox = new TextBox { FontFamily = Mono, FontSize = 13 };
        _testBox.TextChanged += (_, _) => ScheduleTest();
        panel.Children.Add(WithPlaceholder(_testBox, "Наберите текст, как в любом приложении — например, :inc1-5n:"));

        var status = new DockPanel { Margin = new Thickness(2, 10, 0, 0) }; // DockPanel, а не StackPanel: текст переносится
        _testGlyph = FluentIcons.Glyph(FluentIcons.Info, 12, JarvisTheme.ForegroundBrush(0.4));
        _testGlyph.VerticalAlignment = VerticalAlignment.Top;
        _testGlyph.Margin = new Thickness(0, 2, 0, 0);
        DockPanel.SetDock(_testGlyph, Dock.Left);
        _testStatus = new TextBlock
        {
            Text = "Здесь появится правило, которое сработает, и результат подстановки. Ничего никуда не отправляется.",
            Foreground = JarvisTheme.ForegroundBrush(0.5),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 0, 0)
        };
        status.Children.Add(_testGlyph);
        status.Children.Add(_testStatus);
        panel.Children.Add(status);

        _testOutput = new TextBlock
        {
            FontFamily = Mono,
            FontSize = 12.5,
            Foreground = JarvisTheme.ForegroundBrush(0.92),
            TextWrapping = TextWrapping.Wrap
        };
        _testOutputBox = new Border
        {
            Background = JarvisTheme.Solid(Color.FromRgb(0x10, 0x12, 0x18)),
            BorderBrush = JarvisTheme.Solid(Color.FromRgb(0x2A, 0x2E, 0x3A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = _testOutput,
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_testOutputBox);

        _testErrors = new TextBlock
        {
            Foreground = JarvisTheme.Solid(ErrorColor),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_testErrors);

        return Card(panel, new Thickness(12));
    }

    private void ScheduleTest()
    {
        if (_testDebounce == null) return;
        _testDebounce.Stop();
        _testDebounce.Start();
    }

    private async Task RunTestAsync()
    {
        int version = ++_testVersion;
        var text = _testBox.Text;
        if (text.Length == 0)
        {
            SetTestStatus(FluentIcons.Info, "Здесь появится правило, которое сработает, и результат подстановки. Ничего никуда не отправляется.", 0.5);
            _testOutputBox.Visibility = Visibility.Collapsed;
            _testErrors.Visibility = Visibility.Collapsed;
            return;
        }

        SetTestStatus(FluentIcons.Clock, "вычисляется…", 0.5);
        (TriggerMatch? Match, ExpansionOutput? Output) result;
        try { result = await _dryRun(_rows, text); }
        catch (Exception ex) { result = (null, null); SetTestStatus(FluentIcons.Warning, "ошибка: " + ex.Message, 0.7); return; }
        if (version != _testVersion) return; // пока считали, текст уже поменялся

        var (match, output) = result;
        if (match == null || output == null)
        {
            SetTestStatus(FluentIcons.Info, "Совпадений нет: ни один включённый триггер не совпадает с концом текста.", 0.5);
            _testOutputBox.Visibility = Visibility.Collapsed;
            _testErrors.Visibility = Visibility.Collapsed;
            return;
        }

        var title = $"Сработает «{match.Value}» — правило {match.Rule.TriggerPattern}";
        if (!string.IsNullOrWhiteSpace(match.Rule.Name)) title += $" ({match.Rule.Name})";
        if (match.Case != CaseStyle.None) title += match.Case == CaseStyle.Upper ? ", капсом" : ", с заглавной";
        SetTestStatus(FluentIcons.Lightning, title, 0.85, accent: true);

        RenderOutput(output);
        _testOutputBox.Visibility = Visibility.Visible;
        _testErrors.Text = string.Join("\n", output.Errors);
        _testErrors.Visibility = output.Errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetTestStatus(string glyph, string text, double opacity, bool accent = false)
    {
        _testGlyph.Text = glyph;
        _testGlyph.Foreground = accent ? JarvisTheme.AccentBrush : JarvisTheme.ForegroundBrush(0.4);
        _testStatus.Text = text;
        _testStatus.Foreground = JarvisTheme.ForegroundBrush(opacity);
    }

    /// <summary>Результат: текст как есть, клавиши — «чипами», место каретки — акцентной чертой.</summary>
    private void RenderOutput(ExpansionOutput output)
    {
        _testOutput.Inlines.Clear();
        if (output.Segments.Count == 0)
        {
            _testOutput.Inlines.Add(new Run("(пустая строка)") { Foreground = JarvisTheme.ForegroundBrush(0.4) });
            return;
        }
        foreach (var segment in output.Segments)
        {
            switch (segment)
            {
                case TextSegment t:
                    _testOutput.Inlines.Add(new Run(t.Text.Replace("\r\n", "\n")));
                    break;
                case KeySegment k:
                    _testOutput.Inlines.Add(new InlineUIContainer(Chip(k.Chord.Display)) { BaselineAlignment = BaselineAlignment.Center });
                    break;
                case CursorSegment:
                    _testOutput.Inlines.Add(new InlineUIContainer(new Rectangle
                    {
                        Width = 2,
                        Height = 15,
                        Fill = JarvisTheme.AccentBrush,
                        Margin = new Thickness(1, 0, 1, 0),
                        ToolTip = "здесь окажется каретка"
                    }) { BaselineAlignment = BaselineAlignment.Center });
                    break;
            }
        }
    }

    private static Border Chip(string text) => new()
    {
        Background = JarvisTheme.Solid(JarvisTheme.Accent, 0.14),
        BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.35),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(2, 0, 2, 0),
        Child = new TextBlock { Text = text, FontSize = 11, FontFamily = new FontFamily("Segoe UI"), Foreground = JarvisTheme.AccentBrush }
    };

    // ── Справка по синтаксису (сворачиваемая) ──

    private static readonly (string Code, string Text)[] TriggerHelp =
    [
        ("{n}", "целое число, можно с минусом"),
        ("{d}", "число с точкой или запятой"),
        ("{w}", "слово: буквы, цифры, _"),
        ("{*}", "любой текст (как прежние %1, %2)"),
        ("{n:имя}", "именованное значение — в замене %имя"),
        ("[...]", "необязательная часть; без плейсхолдеров внутри сама становится %N (её текст или пусто)"),
        ("re:...", "регулярное выражение .NET у конца набранного; группы — %1, %имя"),
        ("\\[  \\{  \\%", "литеральные [ { % в триггере"),
    ];

    private static readonly (string Code, string Text)[] ReplacementHelp =
    [
        ("%1  %2  %имя", "значения из триггера по порядку или по имени"),
        ("{date}  {date(+1d, dd.MM.yyyy)}", "дата; сдвиг: s m h d w M y"),
        ("{time}  {time(+30m)}", "время"),
        ("{clip}", "текст из буфера обмена"),
        ("{upper(…)} {lower} {title} {trim}", "регистр и пробелы; вызовы вкладываются: {upper({clip})}"),
        ("{calc(2^10 + sqrt(16))}", "вычисление, как в калькуляторе"),
        ("{incr(1, 5, n)}  {repeat(3, ab)}", "последовательность (n — с новой строки), повтор"),
        ("{rand(1, 6)}  {rand(a|b|c)}", "случайное число или вариант"),
        ("{counter(имя)}  {counter(имя, 100)}", "счётчик: растёт при каждом срабатывании"),
        ("{uuid}", "новый GUID"),
        ("{choice(да|нет|позже)}", "выбрать вариант из списка"),
        ("{input(Кому)}", "спросить значение в маленьком окне"),
        ("{cursor}", "каретка окажется здесь после вставки"),
        ("{key:enter}  {key:ctrl+a}  {key:tab*2}", "нажать клавишу в этой точке"),
        ("{= Args[0].Length * 2}", "выражение C# (Args, Named, Clip, Now; %1 подставляется в код)"),
        ("\"a, b\"  \\{  \\}  \\,", "строка с запятыми и скобками; литеральные символы"),
    ];

    private static FrameworkElement BuildReference()
    {
        var content = new StackPanel();
        content.Children.Add(HelpGroup("ТРИГГЕР", TriggerHelp));
        content.Children.Add(HelpGroup("ЗАМЕНА", ReplacementHelp));
        content.Children.Add(new TextBlock
        {
            Text = "Пример: триггер :inc{n}-{n}[n]: и замена {incr(%1,%2,%3)} — «:inc1-3:» даёт «1 2 3», а «:inc1-3n:» — столбик.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 10, 0, 2)
        });
        var card = Card(content, new Thickness(12, 8, 12, 12));
        card.Visibility = Visibility.Collapsed;

        var chevron = FluentIcons.Glyph(FluentIcons.ChevronRight, 10, JarvisTheme.ForegroundBrush(0.45));
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(chevron);
        header.Children.Add(new TextBlock
        {
            Text = "СПРАВКА ПО СИНТАКСИСУ",
            Foreground = JarvisTheme.ForegroundBrush(0.42),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0)
        });
        var headerHost = new Border
        {
            Background = Brushes.Transparent, // кликабельна вся строка, а не только текст
            Cursor = Cursors.Hand,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 18, 0, 4),
            Child = header
        };
        headerHost.MouseLeftButtonUp += (_, _) =>
        {
            bool open = card.Visibility != Visibility.Visible;
            card.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? FluentIcons.ChevronDown : FluentIcons.ChevronRight;
        };

        var root = new StackPanel();
        root.Children.Add(headerHost);
        root.Children.Add(card);
        return root;
    }

    private static FrameworkElement HelpGroup(string title, (string Code, string Text)[] rows)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = JarvisTheme.AccentBrush,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 4)
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.48, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.52, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var code = new TextBlock
            {
                Text = rows[i].Code,
                FontFamily = Mono,
                FontSize = 12,
                Foreground = JarvisTheme.ForegroundBrush(0.9),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 3, 12, 3)
            };
            var text = new TextBlock
            {
                Text = rows[i].Text,
                FontSize = 12,
                Foreground = JarvisTheme.ForegroundBrush(0.55),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 3)
            };
            Grid.SetRow(code, i);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(code);
            grid.Children.Add(text);
        }
        panel.Children.Add(grid);
        return panel;
    }

    // ── Общие примитивы ──

    private static Border Card(UIElement child, Thickness padding, bool hover = false)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(CardIdle), // не freeze: анимируется при наведении
            CornerRadius = new CornerRadius(10),
            Padding = padding,
            Child = child
        };
        if (hover)
        {
            card.MouseEnter += (_, _) => Paint(card, CardHover);
            card.MouseLeave += (_, _) => Paint(card, CardIdle);
        }
        return card;
    }

    private static void Paint(Border card, Color color) =>
        ((SolidColorBrush)card.Background).BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(color, TimeSpan.FromMilliseconds(90)));

    /// <summary>Поле с подсказкой-плейсхолдером (видна, пока поле пустое).</summary>
    private static Grid WithPlaceholder(TextBox box, string placeholder)
    {
        var hint = new TextBlock
        {
            Text = placeholder,
            Foreground = JarvisTheme.ForegroundBrush(0.3),
            FontSize = box.FontSize,
            IsHitTestVisible = false,
            // пустое поле — одна строка, а TextBox общего стиля выше строки и центрирует её
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Отступ текста в шаблоне общего стиля — Padding дважды (ScrollViewer + TextBoxView) + рамка
        // и 2 px. Padding из неявного стиля известен только после загрузки.
        void Align() => hint.Margin = new Thickness(box.Padding.Left * 2 + 3, 0, box.Padding.Right + 3, 0);
        Align();
        box.Loaded += (_, _) => Align();
        void Sync() => hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    private static Button IconButton(string glyph, string tooltip, bool danger)
    {
        // Неявный стиль TextBlock из ресурсов приложения задаёт Foreground и глифам внутри шаблона
        // кнопки — привязываем цвет глифа к Foreground кнопки явно (красный hover у «удалить»)
        var icon = new TextBlock { Text = glyph, FontFamily = FluentIcons.Family, FontSize = 13 };
        var button = new Button
        {
            Content = icon,
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 0, 0)
        };
        icon.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        if (!danger) button.Foreground = JarvisTheme.ForegroundBrush(0.55); // локальное значение глушит красный hover стиля
        return button;
    }

    private static TextBlock Label(string text, double opacity = 0.75) => new()
    {
        Text = text,
        Foreground = JarvisTheme.ForegroundBrush(opacity),
        FontSize = 12,
        Margin = new Thickness(2, 0, 0, 4)
    };

    // ── Карточка правила ──

    private sealed class RuleCard
    {
        private readonly TextExpanderEditor _owner;
        private readonly WrapPanel _status;
        private readonly TextBlock _error;
        private readonly FrameworkElement _errorRow;
        private readonly FrameworkElement _options;
        private readonly Button _optionsButton;
        private readonly FrameworkElement _mainRow;

        public ExpansionRule Model { get; }
        public Border Root { get; }
        public TextBox Trigger { get; }

        public RuleCard(TextExpanderEditor owner, ExpansionRule model)
        {
            _owner = owner;
            Model = model;

            var toggle = new JarvisToggleSwitch
            {
                IsChecked = model.Enabled,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-6, 3, 6, 0),
                ToolTip = "Правило включено"
            };
            toggle.Checked += (_, _) => { Model.Enabled = true; Changed(); };
            toggle.Unchecked += (_, _) => { Model.Enabled = false; Changed(); };

            Trigger = new TextBox
            {
                Text = model.TriggerPattern ?? "",
                FontFamily = Mono,
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.NoWrap
            };
            Trigger.TextChanged += (_, _) => { Model.TriggerPattern = Trigger.Text; Changed(); };

            var replacement = new TextBox
            {
                Text = model.ReplacementTemplate ?? "",
                FontFamily = Mono,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 34,
                MaxHeight = 140,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            replacement.TextChanged += (_, _) => { Model.ReplacementTemplate = replacement.Text; Changed(); };

            var arrow = FluentIcons.Glyph(FluentIcons.ChevronRight, 11, JarvisTheme.ForegroundBrush(0.3));
            arrow.VerticalAlignment = VerticalAlignment.Top;
            arrow.Margin = new Thickness(8, 11, 8, 0);

            _optionsButton = IconButton(FluentIcons.Settings, "Параметры правила", danger: false);
            var delete = IconButton(FluentIcons.Delete, "Удалить правило", danger: true);
            delete.Click += (_, _) => _owner.RemoveRule(this);

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var triggerHost = WithPlaceholder(Trigger, ":триггер:");
            var replacementHost = WithPlaceholder(replacement, "замена");
            Place(main, toggle, 0);
            Place(main, triggerHost, 1);
            Place(main, arrow, 2);
            Place(main, replacementHost, 3);
            Place(main, _optionsButton, 4);
            Place(main, delete, 5);
            _mainRow = main;

            _status = new WrapPanel { Margin = new Thickness(46, 6, 0, 0) };

            // ошибка — отдельной строкой с переносом (в WrapPanel текст не переносится)
            _error = new TextBlock { FontSize = 11.5, Foreground = JarvisTheme.Solid(ErrorColor), TextWrapping = TextWrapping.Wrap };
            var errorIcon = FluentIcons.Glyph(FluentIcons.Warning, 11, JarvisTheme.Solid(ErrorColor));
            errorIcon.VerticalAlignment = VerticalAlignment.Top;
            errorIcon.Margin = new Thickness(0, 2, 6, 0);
            var errorDock = new DockPanel { Margin = new Thickness(46, 6, 40, 0) };
            DockPanel.SetDock(errorIcon, Dock.Left);
            errorDock.Children.Add(errorIcon);
            errorDock.Children.Add(_error);
            _errorRow = errorDock;
            _options = BuildOptions();
            _options.Visibility = Visibility.Collapsed;
            _optionsButton.Click += (_, _) => ToggleOptions();

            var stack = new StackPanel();
            stack.Children.Add(main);
            stack.Children.Add(_errorRow);
            stack.Children.Add(_status);
            stack.Children.Add(_options);

            Root = Card(stack, new Thickness(10, 8, 8, 8), hover: true);
            Root.Margin = new Thickness(0, 0, 0, 6);
            Refresh();
        }

        private static void Place(Grid grid, UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        private void ToggleOptions()
        {
            bool open = _options.Visibility != Visibility.Visible;
            _options.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _optionsButton.Foreground = open ? JarvisTheme.AccentBrush : JarvisTheme.ForegroundBrush(0.55);
        }

        private FrameworkElement BuildOptions()
        {
            var panel = new StackPanel { Margin = new Thickness(46, 10, 40, 4) };

            panel.Children.Add(Label("Описание"));
            var name = new TextBox { Text = Model.Name ?? "" };
            name.TextChanged += (_, _) => { Model.Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text; Changed(); };
            panel.Children.Add(WithPlaceholder(name, "необязательно — для поиска и заголовка в окнах выбора"));

            panel.Children.Add(OptionToggle("Только с начала слова", "не срабатывает внутри слова: «a!=b» — нет, «a != b» — да",
                Model.WordStart, v => Model.WordStart = v));
            panel.Children.Add(OptionToggle("Учитывать регистр", "триггер без учёта регистра; «:Tm:» — результат с заглавной, «:TM:» — капсом",
                Model.CaseAware, v => Model.CaseAware = v));

            var apps = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            apps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            apps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            apps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var include = AppsBox("Только в приложениях", "code, devenv — пусто: везде", () => Model.Apps, v => Model.Apps = v);
            var exclude = AppsBox("Кроме приложений", "например, WindowsTerminal", () => Model.ExcludeApps, v => Model.ExcludeApps = v);
            Place(apps, include, 0);
            Place(apps, exclude, 2);
            panel.Children.Add(apps);

            return panel;
        }

        private FrameworkElement OptionToggle(string title, string hint, bool value, Action<bool> set)
        {
            var toggle = new JarvisToggleSwitch { IsChecked = value, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-6, 0, 6, 0) };
            toggle.Checked += (_, _) => { set(true); Changed(); };
            toggle.Unchecked += (_, _) => { set(false); Changed(); };

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 12.5, Foreground = JarvisTheme.ForegroundBrush(0.88) });
            text.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = JarvisTheme.ForegroundBrush(0.45), TextWrapping = TextWrapping.Wrap });

            var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(toggle, Dock.Left);
            dock.Children.Add(toggle);
            dock.Children.Add(text);
            return dock;
        }

        private FrameworkElement AppsBox(string title, string placeholder, Func<List<string>?> get, Action<List<string>?> set)
        {
            var box = new TextBox { Text = string.Join(", ", get() ?? []) };
            box.TextChanged += (_, _) =>
            {
                var list = box.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(TriggerMatcher.NormalizeApp).Where(a => a.Length > 0).ToList();
                set(list.Count > 0 ? list : null);
                Changed();
            };
            var panel = new StackPanel();
            panel.Children.Add(Label(title));
            panel.Children.Add(WithPlaceholder(box, placeholder));
            return panel;
        }

        private void Changed()
        {
            Refresh();
            _owner.ScheduleSave();
            _owner.ScheduleTest();
        }

        /// <summary>Строка статуса: ошибка триггера, доступные %переменные, активные опции.</summary>
        private void Refresh()
        {
            _mainRow.Opacity = Model.Enabled ? 1 : 0.5;
            _status.Children.Clear();
            bool errorVisible = false;

            if (!string.IsNullOrEmpty(Model.TriggerPattern))
            {
                var compiled = _owner._matcher.Get(Model);
                if (!compiled.IsValid)
                {
                    _error.Text = compiled.Error;
                    errorVisible = true;
                }
                else
                {
                    var vars = Enumerable.Range(1, compiled.NumberedGroups.Count)
                        .Where(i => compiled.NumberedGroups[i - 1] != null)
                        .Select(i => "%" + i)
                        .Concat(compiled.NamedGroups.Keys.Select(n => "%" + n))
                        .ToList();
                    if (vars.Count > 0)
                        _status.Children.Add(new TextBlock
                        {
                            Text = string.Join("  ", vars),
                            FontFamily = Mono,
                            FontSize = 11,
                            Foreground = JarvisTheme.ForegroundBrush(0.4),
                            Margin = new Thickness(0, 1, 10, 2),
                            ToolTip = "Значения из триггера, доступные в замене"
                        });
                }
            }

            if (Model.WordStart) _status.Children.Add(Badge("с начала слова"));
            if (Model.CaseAware) _status.Children.Add(Badge("регистр"));
            if (Model.Apps is { Count: > 0 }) _status.Children.Add(Badge("только: " + string.Join(", ", Model.Apps)));
            if (Model.ExcludeApps is { Count: > 0 }) _status.Children.Add(Badge("кроме: " + string.Join(", ", Model.ExcludeApps)));
            if (!string.IsNullOrWhiteSpace(Model.Name)) _status.Children.Add(Badge(Model.Name!, dim: true));

            _status.Visibility = _status.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _errorRow.Visibility = errorVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static Border Badge(string text, bool dim = false) => new()
        {
            Background = dim ? JarvisTheme.Solid(Colors.White, 0.06) : JarvisTheme.Solid(JarvisTheme.Accent, 0.10),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(0, 0, 6, 2),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = dim ? JarvisTheme.ForegroundBrush(0.6) : JarvisTheme.Solid(JarvisTheme.Accent, 0.9),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            }
        };
    }
}
