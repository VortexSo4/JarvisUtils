using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Calculator;

/// <summary>
/// Строка калькулятора — геометрия окна как у мержера (место истории занял живой ответ):
/// «=» + выражение, ответ считается на лету. Клик по ответу или Enter — копирование и закрытие;
/// Shift+Enter — вставка ответа в окно, активное до калькулятора; стрелки вверх/вниз — история.
/// Esc, клик мимо окна или повторный Alt+= — закрыть. Если ввод пуст, через несколько секунд
/// тишины снизу появляется подсказка по синтаксису и дальше сменяется по таймеру.
/// </summary>
internal sealed class CalculatorWindow : Window
{
    // ── геометрия «как у мержера»: фиксированное место, не у курсора ──
    private const double WindowWidth = 700;
    private const double TopOffset = 200; // отступ от верха рабочей области

    private const string NoEqualsHint = "выражение начинается с «=»";
    private static readonly TimeSpan HintInterval = TimeSpan.FromSeconds(7);
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");

    private static readonly string[] Hints =
    [
        "10 km in mi, 5 кг в lb — перевод единиц",
        "100$ в руб, 50 eur in usd — курсы валют (обновляются сами)",
        "Shift+Enter — вставить ответ прямо в окно, где вы печатали",
        "5! — факториал (дробные тоже: 0.5!)",
        "100 f to c — температура: °C, °F, K",
        "0xFF, 0b1010, 0o17 — числа в других системах счисления",
        "255 in hex, to bin, to oct — перевод в систему счисления",
        "2^10 или 2**10 — возведение в степень",
        "1.5 GB in MB, 1 GiB in MiB — объём данных",
        "стрелки вверх/вниз — прошлые выражения",
        "sqrt(2) или √2 — квадратный корень",
        "20% of 150 = 30, а 150 + 20% = 180",
        "90 min to h, 60 km/h in m/s — время и скорость",
        "sin(pi/2) — тригонометрия, углы в радианах",
        "(2+3) km in m — слева от единицы можно писать выражение",
        "log(8, 2) — логарифм по основанию",
        "50% — проценты, а 10%3 — остаток от деления",
        "1 ha in m2, 3 gal in l — площадь и объём",
        "2pi и 2(3+4) — знак умножения можно не писать",
        "min(3, 1, 2), max, avg — несколько аргументов",
        "round(2.5), floor(2.9), ceil(2.1) — округления",
        "pi, e, tau, phi — встроенные константы",
    ];

    private readonly IJarvisServices _services;
    private readonly IntPtr _previousWindow;           // куда вставлять по Shift+Enter
    private readonly IReadOnlyList<string> _history;   // свежие — первыми, без «=»
    private readonly Action<string> _onCommit;         // выражение ушло в буфер/окно — в историю
    private readonly TextBox _input;
    private readonly TextBlock _resultText;
    private readonly TextBlock _noteText;
    private readonly Border _resultRow;
    private readonly ScaleTransform _scale;
    private readonly DispatcherTimer _hintTimer;
    private string? _copyValue; // null — результата нет, копировать нечего
    private int _hintIndex;
    private int _historyIndex = -1; // -1 — свой ввод, не из истории
    private string _draft = "=";   // что было в строке до листания истории
    private bool _recallingHistory;
    private bool _closing;

    public CalculatorWindow(IJarvisServices services, IntPtr previousWindow, IReadOnlyList<string> history, Action<string> onCommit)
    {
        _services = services;
        _previousWindow = previousWindow;
        _history = history;
        _onCommit = onCommit;

        // как у остальных оверлеев (acrylic/неон), но окно активируемое — в строку нужно печатать
        ShowActivated = true;
        Focusable = true;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual; // Left/Top задаём сами до Show
        Width = WindowWidth + OverlayVisuals.ExtraWidth;
        SizeToContent = SizeToContent.Height;
        Opacity = 0;
        if (OverlayVisuals.UseAcrylic)
        {
            AllowsTransparency = false; // layered + DWM-материал = серый прямоугольник
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0)
            });
        }
        else
        {
            AllowsTransparency = true;
        }

        _input = new TextBox
        {
            Text = "=", // «=» уже в строке — PowerToys-стиль; можно стереть и ввести заново
            FontSize = 17,
            FontFamily = MonoFont,
            Foreground = JarvisTheme.ForegroundBrush(0.95),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            CaretBrush = JarvisTheme.AccentBrush,
            SelectionBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.30)
        };
        _input.TextChanged += (_, _) =>
        {
            if (!_recallingHistory) _historyIndex = -1; // правка — это уже свой ввод
            UpdateResult();
        };

        _resultText = new TextBlock
        {
            FontSize = 15,
            FontFamily = MonoFont,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 23 // высота строки ответа зарезервирована — окно не прыгает при её появлении
        };
        // справка справа: дата курса, «1 km = …», hex/bin — тускло и мелко, чтобы не спорить с ответом
        _noteText = new TextBlock
        {
            FontSize = 12,
            FontFamily = MonoFont,
            Foreground = JarvisTheme.ForegroundBrush(0.33),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 340,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var resultGrid = new Grid();
        resultGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_noteText, 1);
        resultGrid.Children.Add(_resultText);
        resultGrid.Children.Add(_noteText);

        _resultRow = new Border
        {
            Padding = new Thickness(6, 7, 6, 7),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent), // не freeze: анимируется при наведении
            Child = resultGrid
        };
        ToolTipService.SetInitialShowDelay(_resultRow, 500);
        _resultRow.MouseEnter += (_, _) => AnimateRowHover(true);
        _resultRow.MouseLeave += (_, _) => AnimateRowHover(false);
        _resultRow.MouseLeftButtonUp += (_, _) => CopyAndClose();

        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        panel.Children.Add(_input);
        panel.Children.Add(new Border // разделитель «ввод / ответ» — место истории у мержера
        {
            Height = 1,
            Background = JarvisTheme.Solid(Colors.White, 0.10),
            Margin = new Thickness(2, 8, 2, 0)
        });
        panel.Children.Add(_resultRow);
        panel.Children.Add(BuildFooter());

        var (shell, scale) = OverlayVisuals.CreateShell(8, panel);
        _scale = scale;
        if (OverlayVisuals.UseAcrylic)
        {
            shell.Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.78); // строка ввода плотнее тостов — читабельнее
            shell.BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.30);
        }
        Content = shell;

        // подсказки: пауза без ввода → первая, дальше по таймеру — следующая
        _hintTimer = new DispatcherTimer { Interval = HintInterval };
        _hintTimer.Tick += (_, _) => ShowNextHint();

        // курс валют приходит из фона — пересчитать «загрузка курса…» в ответ
        CurrencyRates.Updated += OnRatesUpdated;

        UpdateResult(); // пустой ввод: запускает отсчёт до первой подсказки
        PositionWindow(); // позиция ДО Show — окно рождается сразу на месте, без «кадра в углу»

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => CloseAnimated(); // клик мимо строки — закрыть
        Loaded += (_, _) =>
        {
            WindowHelper.ForceForeground(this); // иначе окно появляется, а печать уходит в прежнее приложение
            _input.Focus();
            Keyboard.Focus(_input);
            _input.CaretIndex = _input.Text.Length; // курсор сразу после «=»
        };
        Closed += (_, _) =>
        {
            _hintTimer.Stop();
            CurrencyRates.Updated -= OnRatesUpdated; // статическое событие — иначе окно не соберётся GC
        };
    }

    private static TextBlock BuildFooter()
    {
        var footer = new TextBlock
        {
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 10.5,
            Margin = new Thickness(8, 7, 0, 0)
        };
        footer.Inlines.Add(new Run("Enter — скопировать · Shift+Enter — вставить в окно · "));
        footer.Inlines.Add(new Run(FluentIcons.ChevronUp + FluentIcons.ChevronDown)
        {
            FontFamily = FluentIcons.Family,
            FontSize = 10,
            BaselineAlignment = BaselineAlignment.Center
        });
        footer.Inlines.Add(new Run(" — история · Esc — закрыть"));
        return footer;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseAnimated();
                break;
            case Key.Return when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                e.Handled = true;
                InsertAndClose(); // без валидного результата — no-op
                break;
            case Key.Return:
                e.Handled = true;
                CopyAndClose(); // без валидного результата — no-op
                break;
            case Key.Up:
                e.Handled = true;
                BrowseHistory(+1);
                break;
            case Key.Down:
                e.Handled = true;
                BrowseHistory(-1);
                break;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (OverlayVisuals.UseAcrylic) WindowHelper.ApplyGlassBackdrop(this);
        HideFromAltTab();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        OverlayVisuals.PlayEntrance(this, _scale);
    }

    // ── история ──

    /// <param name="direction">+1 — к более старым (вверх), -1 — к более новым (вниз)</param>
    private void BrowseHistory(int direction)
    {
        int next = _historyIndex + direction;
        if (_history.Count == 0 || next < -1 || next >= _history.Count) return;
        if (_historyIndex == -1) _draft = _input.Text; // уходим из своего ввода — запомнить его
        _historyIndex = next;
        _recallingHistory = true;
        try { _input.Text = next == -1 ? _draft : "=" + _history[next]; }
        finally { _recallingHistory = false; }
        _input.CaretIndex = _input.Text.Length;
    }

    private string CurrentExpression() =>
        _input.Text.Length > 0 && _input.Text[0] == '=' ? _input.Text[1..].Trim() : "";

    // ── живой ответ под строкой ввода ──

    private void OnRatesUpdated() => Dispatcher.BeginInvoke(() =>
    {
        // пустой ввод не трогаем — там может крутиться подсказка
        if (!_closing && CurrentExpression().Length > 0) UpdateResult();
    });

    private void UpdateResult()
    {
        string text = _input.Text;
        if (text.Length > 0 && text[0] != '=') // «=» стёрли — подскажем сразу, а не по таймеру
        {
            _hintTimer.Stop();
            SetMessage(NoEqualsHint);
            return;
        }

        string expr = CurrentExpression();
        if (expr.Length == 0)
        {
            // пустой ввод: строка ответа молчит; пауза — и пойдут подсказки.
            // отсчёт каждый раз заново — от последней правки: «ничего не пишешь» = правок нет
            SetMessage("");
            _hintTimer.Stop();
            _hintTimer.Start();
            return;
        }

        _hintTimer.Stop(); // пошёл ввод — подсказки больше не крутятся
        var result = CalcEngine.Evaluate(expr);
        if (result.Kind == CalcResultKind.Value) SetValue(result);
        else SetMessage(result.Text, result.Note);
    }

    private void SetValue(CalcResult result)
    {
        _copyValue = result.Text;
        _resultText.BeginAnimation(OpacityProperty, null); // сброс fade подсказки
        _resultText.Inlines.Clear();
        _resultText.Inlines.Add(new Run(result.Text)
        {
            Foreground = JarvisTheme.AccentBrush,
            FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(result.Suffix)) // единица — тускло: она не копируется
            _resultText.Inlines.Add(new Run(" " + result.Suffix) { Foreground = JarvisTheme.ForegroundBrush(0.55) });
        _noteText.Text = result.Note ?? "";
        _resultRow.ToolTip = result.Tooltip != null ? BuildToolTip(result.Tooltip) : null;
        _resultRow.Cursor = Cursors.Hand;
    }

    /// <summary>Ошибка, ожидание курса или пусто: тусклый текст, копировать нечего.</summary>
    private void SetMessage(string message, string? note = null)
    {
        _copyValue = null;
        _resultText.BeginAnimation(OpacityProperty, null);
        _resultText.Inlines.Clear();
        _resultText.Inlines.Add(new Run(message) { Foreground = JarvisTheme.ForegroundBrush(0.38) });
        _noteText.Text = note ?? "";
        _resultRow.ToolTip = null;
        _resultRow.Cursor = Cursors.Arrow;
        AnimateRowHover(false, force: true);
    }

    private static ToolTip BuildToolTip(string text) => new()
    {
        Content = text,
        FontFamily = MonoFont,
        FontSize = 12,
        Foreground = JarvisTheme.ForegroundBrush(0.85),
        Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.96),
        BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.30),
        Padding = new Thickness(8, 5, 8, 5)
    };

    /// <summary>Одна подсказка из списка — по тику таймера, по кругу.</summary>
    private void ShowNextHint()
    {
        SetMessage(Hints[_hintIndex]);
        _hintIndex = (_hintIndex + 1) % Hints.Length;
        _resultText.BeginAnimation(OpacityProperty, // мягкая смена подсказки
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void AnimateRowHover(bool hover, bool force = false)
    {
        if (_copyValue == null && !force) return;
        var target = Color.FromArgb(hover ? (byte)0x14 : (byte)0x00, 0xFF, 0xFF, 0xFF);
        if (_resultRow.Background is SolidColorBrush b)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(80)));
    }

    // ── копирование, вставка и закрытие ──

    private void CopyAndClose()
    {
        if (_copyValue == null || _closing) return;
        string value = _copyValue;
        _onCommit(CurrentExpression());
        CloseAnimated();
        Dispatcher.BeginInvoke(() => CopyToClipboard(_services, value, $"Скопировано: {value}"));
    }

    /// <summary>
    /// Shift+Enter: вернуть фокус окну, где пользователь был до Alt+=, и напечатать ответ туда.
    /// Фокус отдаём, пока наше окно ещё активно (foreground-процессу это разрешено), и ждём,
    /// пока целевое окно действительно станет активным — иначе текст уйдёт не туда.
    /// </summary>
    private void InsertAndClose()
    {
        if (_copyValue == null || _closing) return;
        if (!ForegroundRestorer.IsAlive(_previousWindow))
        {
            CopyAndClose(); // прежнего окна уже нет — хотя бы в буфер
            return;
        }

        string value = _copyValue;
        var services = _services;
        IntPtr target = _previousWindow;
        _onCommit(CurrentExpression());
        _closing = true; // Deactivated от смены фокуса не должен запускать второе закрытие
        ForegroundRestorer.Activate(target);
        FadeOutAndClose();
        ForegroundRestorer.WhenForeground(target,
            () =>
            {
                try { services.Input.SendText(value); }
                catch (Exception ex) { Log.Warn($"Калькулятор: вставка не удалась: {ex.Message}"); }
            },
            () => CopyToClipboard(services, value, $"Окно не приняло фокус — скопировано: {value}"));
    }

    private static void CopyToClipboard(IJarvisServices services, string value, string toast)
    {
        try
        {
            services.Clipboard.SetText(value);
            services.Overlay.ShowToast(toast, "Калькулятор", 1300);
        }
        catch (Exception ex) { Log.Warn($"Калькулятор: копирование не удалось: {ex.Message}"); }
    }

    // ── геометрия «как у мержера»: фиксированное место ──

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2; // центр по горизонтали
        Top = wa.Top + TopOffset;               // фиксированный отступ сверху
    }

    internal void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        FadeOutAndClose();
    }

    private void FadeOutAndClose()
    {
        _hintTimer.Stop();
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
        anim.Completed += (_, _) => { try { Close(); } catch { /* окно уже закрыто */ } };
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>WS_EX_TOOLWINDOW — чтобы строка не мелькала в Alt+Tab.</summary>
    private void HideFromAltTab()
    {
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
