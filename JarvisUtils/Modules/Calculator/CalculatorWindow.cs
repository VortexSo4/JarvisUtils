using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
/// Esc, клик мимо окна или повторный Alt+= — закрыть. Если ввод пуст, через 30 секунд тишины
/// снизу появляется подсказка по синтаксису и дальше сменяется каждые 30 секунд.
/// </summary>
internal sealed class CalculatorWindow : Window
{
    // ── геометрия «как у мержера»: фиксированное место, не у курсора ──
    private const double WindowWidth = 700;
    private const double TopOffset = 200; // отступ от верха рабочей области

    private const string NoEqualsHint = "выражение начинается с «=»";
    private static readonly TimeSpan HintInterval = TimeSpan.FromSeconds(7);

    private static readonly string[] Hints =
    [
        "5! — факториал (дробные тоже: 0.5!)",
        "2^10 или 2**10 — возведение в степень",
        "sqrt(2) или √2 — квадратный корень",
        "sin(pi/2) — тригонометрия, углы в радианах",
        "log(8, 2) — логарифм по основанию",
        "50% — проценты, а 10%3 — остаток от деления",
        "2pi и 2(3+4) — знак умножения можно не писать",
        "min(3, 1, 2), max, avg — несколько аргументов",
        "round(2.5), floor(2.9), ceil(2.1) — округления",
        "(2+3)! = 120 — факториал от выражения",
        "cbrt(27) — кубический корень",
        "pi, e, tau, phi — встроенные константы",
    ];

    private readonly IJarvisServices _services;
    private readonly TextBox _input;
    private readonly TextBlock _resultText;
    private readonly Border _resultRow;
    private readonly ScaleTransform _scale;
    private readonly DispatcherTimer _hintTimer;
    private string? _copyValue; // null — результата нет, копировать нечего
    private int _hintIndex;
    private bool _closing;

    public CalculatorWindow(IJarvisServices services)
    {
        _services = services;

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
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            Foreground = JarvisTheme.ForegroundBrush(0.95),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            CaretBrush = JarvisTheme.AccentBrush,
            SelectionBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.30)
        };
        _input.TextChanged += (_, _) => UpdateResult();

        _resultText = new TextBlock
        {
            FontSize = 15,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinHeight = 23 // высота строки ответа зарезервирована — окно не прыгает при её появлении
        };
        _resultRow = new Border
        {
            Padding = new Thickness(6, 7, 6, 7),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent), // не freeze: анимируется при наведении
            Child = _resultText
        };
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
        panel.Children.Add(new TextBlock
        {
            Text = "Enter — скопировать · Esc — закрыть",
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 10.5,
            Margin = new Thickness(8, 7, 0, 0)
        });

        var (shell, scale) = OverlayVisuals.CreateShell(8, panel);
        _scale = scale;
        if (OverlayVisuals.UseAcrylic)
        {
            shell.Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.78); // строка ввода плотнее тостов — читабельнее
            shell.BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.30);
        }
        Content = shell;

        // подсказки: 30 секунд без ввода → первая, дальше каждые 30 секунд — следующая
        _hintTimer = new DispatcherTimer { Interval = HintInterval };
        _hintTimer.Tick += (_, _) => ShowNextHint();

        UpdateResult(); // пустой ввод: запускает 30-секундный отсчёт до первой подсказки
        PositionWindow(); // позиция ДО Show — окно рождается сразу на месте, без «кадра в углу»

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => CloseAnimated(); // клик мимо строки — закрыть
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.CaretIndex = _input.Text.Length; // курсор сразу после «=»
        };
        Closed += (_, _) => _hintTimer.Stop();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseAnimated();
        }
        else if (e.Key == Key.Return)
        {
            e.Handled = true;
            CopyAndClose(); // без валидного результата — no-op
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

    // ── живой ответ под строкой ввода ──

    private void UpdateResult()
    {
        string text = _input.Text;
        if (text.Length > 0 && text[0] != '=') // «=» стёрли — подскажем сразу, не через 30 секунд
        {
            _hintTimer.Stop();
            SetResult(null, NoEqualsHint);
            return;
        }

        string expr = text.Length > 0 ? text[1..].Trim() : "";
        if (expr.Length == 0)
        {
            // пустой ввод: строка ответа молчит; 30 секунд тишины — и пойдут подсказки.
            // отсчёт каждый раз заново — от последней правки: «ничего не пишешь» = правок нет
            SetResult(null, "");
            _hintTimer.Stop();
            _hintTimer.Start();
            return;
        }

        _hintTimer.Stop(); // пошёл ввод — подсказки больше не крутятся
        if (MathEvaluator.TryEvaluate(expr, out double value, out string error))
        {
            string formatted = MathEvaluator.Format(value);
            SetResult(formatted, formatted);
        }
        else SetResult(null, error);
    }

    private void SetResult(string? copyValue, string display)
    {
        _copyValue = copyValue;
        _resultText.BeginAnimation(OpacityProperty, null); // сброс fade подсказки
        _resultText.Text = display;
        bool ok = copyValue != null;
        _resultText.Foreground = ok ? JarvisTheme.AccentBrush : JarvisTheme.ForegroundBrush(0.38);
        _resultText.FontWeight = ok ? FontWeights.SemiBold : FontWeights.Normal;
        _resultRow.Cursor = ok ? Cursors.Hand : Cursors.Arrow;
    }

    /// <summary>Одна подсказка из списка — по тику таймера (каждые 30 секунд), по кругу.</summary>
    private void ShowNextHint()
    {
        _copyValue = null;
        _resultText.Text = Hints[_hintIndex];
        _hintIndex = (_hintIndex + 1) % Hints.Length;
        _resultText.Foreground = JarvisTheme.ForegroundBrush(0.38);
        _resultText.FontWeight = FontWeights.Normal;
        _resultRow.Cursor = Cursors.Arrow;
        _resultText.BeginAnimation(OpacityProperty, // мягкая смена подсказки
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void AnimateRowHover(bool hover)
    {
        if (_copyValue == null) return;
        var target = Color.FromArgb(hover ? (byte)0x14 : (byte)0x00, 0xFF, 0xFF, 0xFF);
        if (_resultRow.Background is SolidColorBrush b)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(80)));
    }

    // ── копирование и закрытие ──

    private void CopyAndClose()
    {
        if (_copyValue == null || _closing) return;
        string value = _copyValue;
        CloseAnimated();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _services.Clipboard.SetText(value);
                _services.Overlay.ShowToast($"Скопировано: {value}", "Калькулятор", 1300);
            }
            catch (Exception ex) { Log.Warn($"Калькулятор: копирование не удалось: {ex.Message}"); }
        });
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