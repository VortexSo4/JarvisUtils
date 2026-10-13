using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ClipboardHistory;

/// <summary>
/// Окно буфера обмена: поиск сверху, список с превью, управление с клавиатуры. Два режима —
/// «Вся история» и «Только избранное» (кнопка-звезда или Tab).
/// Enter — вставить в окно, активное до открытия; Ctrl+Enter — как простой текст;
/// Ctrl+P — поставить/снять звезду; Del — удалить (у избранной записи сначала снимает звезду,
/// чтобы избранное не терялось одним нажатием); Esc / клик мимо — закрыть. Клик по строке — вставить.
/// </summary>
internal sealed class ClipboardHistoryWindow : Window
{
    private const double WindowWidth = 680;
    private const double ListMaxHeight = 430;
    private const int RenderChunk = 40; // строки создаются порциями: 1000 записей разом — заметная пауза UI-потока
    private const int PageStep = 6;

    private static readonly Color RowSelectedBg = Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly ClipboardHistoryStore _store;
    private readonly Action<HistoryEntry, bool> _paste;
    private readonly Border _modeButton;
    private readonly TextBlock _modeIcon;
    private readonly TextBlock _modeLabel;

    private readonly TextBox _search;
    private readonly TextBlock _watermark;
    private readonly TextBlock _counter;
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private readonly ScaleTransform _scale;

    private readonly List<RowView> _rows = [];
    private readonly Dictionary<string, BitmapSource?> _thumbs = new();
    private List<HistoryEntry> _filtered = [];
    private int _selected = -1;
    private int _filterVersion;
    private Point _lastMouse = new(double.NaN, double.NaN);
    private bool _closing;

    private sealed record RowView(HistoryEntry Entry, FrameworkElement Root, SolidColorBrush BodyBrush, SolidColorBrush StripBrush,
        FrameworkElement Star, TextBlock StarIcon, FrameworkElement Delete);

    /// <summary>Режим «Только избранное».</summary>
    public bool FavoritesOnly { get; private set; }

    public ClipboardHistoryWindow(ClipboardHistoryStore store, Action<HistoryEntry, bool> paste, bool favoritesOnly = false)
    {
        _store = store;
        _paste = paste;
        FavoritesOnly = favoritesOnly;

        // активируемое окно (в поиск нужно печатать), но оформление — как у оверлеев (acrylic + неон)
        ShowActivated = true;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = WindowWidth;
        SizeToContent = SizeToContent.Height;
        Opacity = 0;
        AllowsTransparency = false; // layered + DWM-материал = серый прямоугольник
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0)
        });

        // ── строка поиска ──
        _watermark = new TextBlock
        {
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 15,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        _search = new TextBox
        {
            FontSize = 15,
            Foreground = JarvisTheme.ForegroundBrush(0.95),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CaretBrush = JarvisTheme.AccentBrush,
            SelectionBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.35),
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = BareTextBoxTemplate() // без рамки и свечения глобального стиля TextBox
        };
        _search.TextChanged += (_, _) =>
        {
            _watermark.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Refilter(keepId: null);
        };
        var field = new Grid { VerticalAlignment = VerticalAlignment.Center };
        field.Children.Add(_watermark);
        field.Children.Add(_search);

        _counter = new TextBlock
        {
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        // переключатель «Вся история / Только избранное»: не фокусируемый — фокус остаётся в поиске
        _modeIcon = FluentIcons.Glyph(FluentIcons.Star, 15);
        _modeLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
        var modeContent = new StackPanel { Orientation = Orientation.Horizontal };
        modeContent.Children.Add(_modeIcon);
        modeContent.Children.Add(_modeLabel);
        var modeBrush = new SolidColorBrush(Colors.Transparent);
        _modeButton = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 10, 5),
            Margin = new Thickness(10, 0, 0, 0),
            Background = modeBrush,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Вся история / только избранное (Tab)",
            Child = modeContent
        };
        _modeButton.MouseEnter += (_, _) => modeBrush.Color = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
        _modeButton.MouseLeave += (_, _) => modeBrush.Color = Colors.Transparent;
        _modeButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetFavoritesOnly(!FavoritesOnly); };

        var searchIcon = FluentIcons.Glyph(FluentIcons.Search, 15, JarvisTheme.ForegroundBrush(0.45));
        searchIcon.Margin = new Thickness(0, 0, 12, 0);
        var header = new Grid { Margin = new Thickness(18, 10, 12, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(field, 1);
        Grid.SetColumn(_counter, 2);
        Grid.SetColumn(_modeButton, 3);
        header.Children.Add(searchIcon);
        header.Children.Add(field);
        header.Children.Add(_counter);
        header.Children.Add(_modeButton);
        UpdateModeVisuals();

        // ── список ──
        _list = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        _scroll = new ScrollViewer
        {
            Content = _list,
            MaxHeight = ListMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        _scroll.ScrollChanged += (_, _) =>
        {
            // догружаем следующую порцию строк при прокрутке к низу
            if (_scroll.VerticalOffset + _scroll.ViewportHeight >= _scroll.ExtentHeight - 80) RenderMore();
        };
        _empty = new TextBlock
        {
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            FontSize = 12.5,
            Margin = new Thickness(20, 18, 20, 18),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var hint = new TextBlock
        {
            Text = "Enter — вставить · Ctrl+Enter — как текст · Ctrl+P — звезда · Tab — избранное / вся история · Del — удалить · Esc — закрыть",
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(18, 6, 16, 10)
        };

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(new Border { Height = 1, Background = JarvisTheme.Solid(JarvisTheme.Accent, 0.15) });
        content.Children.Add(_scroll);
        content.Children.Add(_empty);
        content.Children.Add(new Border { Height = 1, Background = JarvisTheme.Solid(Colors.White, 0.06) });
        content.Children.Add(hint);

        var (shell, scale) = OverlayVisuals.CreateShell(10, content);
        _scale = scale;
        shell.Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.80); // плотнее тостов: длинные тексты должны читаться
        shell.BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.28);
        Content = shell;

        PositionWindow();

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => CloseAnimated(); // клик мимо окна — закрыть
        Loaded += (_, _) =>
        {
            WindowHelper.ForceForeground(this); // иначе окно появится, а печать уйдёт в прежнее приложение
            _search.Focus();
            Keyboard.Focus(_search);
        };

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;

        if (_store.IsLoaded) Refilter(keepId: null);
        else
        {
            ShowEmpty("Загрузка истории…");
            _ = _store.EnsureLoadedAsync(); // по готовности придёт Changed
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.ApplyGlassBackdrop(this);
        // WS_EX_TOOLWINDOW — без кнопки в Alt+Tab; активация при этом остаётся
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        WinApi.SetWindowLong(hwnd, WinApi.GWL_EXSTYLE, WinApi.GetWindowLong(hwnd, WinApi.GWL_EXSTYLE) | WinApi.WS_EX_TOOLWINDOW);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        OverlayVisuals.PlayEntrance(this, _scale);
    }

    /// <summary>По центру рабочей области монитора с курсором — там, где пользователь сейчас работает.</summary>
    private void PositionWindow()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var wa = screen.WorkingArea;
        double scale = DpiUtil.Scale;
        double left = wa.Left / scale, top = wa.Top / scale, width = wa.Width / scale, height = wa.Height / scale;
        Left = Math.Round(left + (width - Width) / 2);
        Top = Math.Round(top + Math.Max(16, height * 0.14));
    }

    // ── клавиатура ──

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseAnimated();
                break;
            case Key.Down:
                e.Handled = true;
                Move(1);
                break;
            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                Move(PageStep, wrap: false);
                break;
            case Key.PageUp:
                e.Handled = true;
                Move(-PageStep, wrap: false);
                break;
            case Key.Return:
                e.Handled = true;
                PasteAt(_selected, plain: ctrl);
                break;
            case Key.Delete:
                // в поле поиска Del удаляет символ справа — забираем клавишу, только когда справа пусто
                if (_search.SelectionLength == 0 && _search.CaretIndex == _search.Text.Length)
                {
                    e.Handled = true;
                    RemoveAt(_selected);
                }
                break;
            case Key.P when ctrl:
                e.Handled = true;
                ToggleFavoriteAt(_selected);
                break;
            case Key.Tab: // в поле поиска Tab не нужен (одно поле — переводить фокус некуда)
                e.Handled = true;
                SetFavoritesOnly(!FavoritesOnly);
                break;
        }
    }

    // ── режим «Только избранное» ──

    /// <summary>Переключить режим (кнопка-звезда, Tab или повторный хоткей другого режима).</summary>
    public void SetFavoritesOnly(bool value)
    {
        if (FavoritesOnly == value) return;
        FavoritesOnly = value;
        UpdateModeVisuals();
        Refilter(keepId: null);
    }

    private void UpdateModeVisuals()
    {
        _modeIcon.Text = FavoritesOnly ? FluentIcons.StarFill : FluentIcons.Star;
        _modeIcon.Foreground = FavoritesOnly ? JarvisTheme.AccentBrush : JarvisTheme.ForegroundBrush(0.6);
        _modeLabel.Text = FavoritesOnly ? "Только избранное" : "Вся история";
        _modeLabel.Foreground = FavoritesOnly ? JarvisTheme.AccentBrush : JarvisTheme.ForegroundBrush(0.6);
        _modeButton.BorderBrush = FavoritesOnly ? JarvisTheme.Solid(JarvisTheme.Accent, 0.45) : JarvisTheme.Solid(Colors.White, 0.10);
        _watermark.Text = FavoritesOnly ? "Поиск в избранном…" : "Поиск в истории буфера…";
    }

    private void Move(int delta, bool wrap = true)
    {
        if (_filtered.Count == 0) return;
        int target = _selected + delta;
        target = wrap
            ? ((target % _filtered.Count) + _filtered.Count) % _filtered.Count
            : Math.Clamp(target, 0, _filtered.Count - 1);
        Select(target, scrollIntoView: true);
    }

    // ── фильтрация ──

    private void OnStoreChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        string? keep = _selected >= 0 && _selected < _filtered.Count ? _filtered[_selected].Id : null;
        Refilter(keep);
    });

    /// <summary>
    /// Поиск — на пуле потоков: в истории бывают мегабайтные тексты, а IndexOf по ним
    /// на UI-потоке (с хуками ввода) остановил бы мышь и клавиатуру всей системы.
    /// </summary>
    private void Refilter(string? keepId)
    {
        int version = ++_filterVersion;
        IReadOnlyList<HistoryEntry> all = _store.Snapshot();
        if (FavoritesOnly) all = all.Where(e => e.Favorite).ToList();
        var terms = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            Apply(all.ToList(), version, keepId);
            return;
        }
        Task.Run(() => all.Where(e => Matches(e, terms)).ToList())
            .ContinueWith(t => Dispatcher.BeginInvoke(() => Apply(t.Result, version, keepId)), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    internal static bool Matches(HistoryEntry e, string[] terms)
    {
        foreach (var term in terms)
        {
            bool hit = (e.IsImage ? e.Preview.Contains(term, StringComparison.OrdinalIgnoreCase) || "картинка image".Contains(term, StringComparison.OrdinalIgnoreCase)
                                  : e.Text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
                       || e.App?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
            if (!hit) return false;
        }
        return true;
    }

    private void Apply(List<HistoryEntry> items, int version, string? keepId)
    {
        if (version != _filterVersion || _closing) return; // пока искали, запрос уже сменился
        _filtered = items;
        _rows.Clear();
        _list.Children.Clear();
        _selected = -1;
        RenderMore();

        int total = FavoritesOnly ? _store.FavoriteCount : _store.Count;
        _counter.Text = _search.Text.Trim().Length > 0 ? $"{items.Count} из {total}" : total > 0 ? total.ToString(Ru) : "";

        if (items.Count == 0)
        {
            ShowEmpty(total > 0 ? "Ничего не найдено."
                : FavoritesOnly ? "В избранном пока пусто. Ctrl+P или звезда у записи — добавить, Tab — вернуться ко всей истории."
                : "История пуста. Скопируйте текст или картинку — они появятся здесь.");
            return;
        }
        _empty.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;

        int index = keepId != null ? items.FindIndex(e => e.Id == keepId) : -1;
        Select(Math.Max(0, index), scrollIntoView: true);
        if (index <= 0) _scroll.ScrollToTop();
    }

    private void ShowEmpty(string text)
    {
        _empty.Text = text;
        _empty.Visibility = Visibility.Visible;
        _scroll.Visibility = Visibility.Collapsed;
    }

    private void RenderMore(int upTo = 0)
    {
        int target = Math.Min(_filtered.Count, Math.Max(_rows.Count + RenderChunk, upTo + 1));
        for (int i = _rows.Count; i < target; i++)
        {
            var row = BuildRow(_filtered[i], i);
            _rows.Add(row);
            _list.Children.Add(row.Root);
        }
    }

    // ── строки ──

    private RowView BuildRow(HistoryEntry entry, int index)
    {
        // слева — миниатюра картинки или значок текста
        FrameworkElement leading;
        if (entry.IsImage)
        {
            var image = new Image { Stretch = Stretch.Uniform, Width = 64, Height = 44 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            LoadThumb(entry, image);
            leading = new Border
            {
                Width = 64,
                Height = 44,
                CornerRadius = new CornerRadius(4),
                Background = JarvisTheme.Solid(Colors.White, 0.05),
                ClipToBounds = true,
                Child = image
            };
        }
        else
        {
            leading = new Border
            {
                Width = 64,
                Height = 44,
                CornerRadius = new CornerRadius(4),
                Background = JarvisTheme.Solid(Colors.White, 0.04),
                Child = FluentIcons.Glyph(entry.HasRich ? FluentIcons.Document : FluentIcons.Font, 16, JarvisTheme.ForegroundBrush(0.45))
            };
        }
        leading.Margin = new Thickness(0, 0, 12, 0);
        leading.VerticalAlignment = VerticalAlignment.Top;

        var title = new TextBlock
        {
            Text = entry.Preview,
            Foreground = JarvisTheme.ForegroundBrush(0.92),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 36 // две строки превью
        };
        var meta = new TextBlock
        {
            Text = MetaLine(entry, DateTime.Now),
            Foreground = JarvisTheme.ForegroundBrush(0.42),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(meta);

        // кнопки видны у выбранной строки — список не пестрит; звезда избранного видна всегда
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var star = ActionButton(FluentIcons.Star, "Избранное (Ctrl+P)", () => ToggleFavoriteAt(IndexOf(entry)),
            () => entry.Favorite ? JarvisTheme.AccentBrush : JarvisTheme.ForegroundBrush(0.55), out var starIcon);
        var delete = ActionButton(FluentIcons.Delete, "Удалить из истории (Del)", () => RemoveAt(IndexOf(entry)),
            () => JarvisTheme.ForegroundBrush(0.55), out _);
        actions.Children.Add(star);
        actions.Children.Add(delete);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(leading);
        grid.Children.Add(texts);
        grid.Children.Add(actions);

        var bodyBrush = new SolidColorBrush(Colors.Transparent); // не freeze: анимируется; transparent — hit-testable
        var body = new Border
        {
            Background = bodyBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Cursor = Cursors.Hand,
            Child = grid
        };
        var stripBrush = new SolidColorBrush(Colors.Transparent);
        var strip = new Border { Width = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 6, 0, 6), Background = stripBrush };

        var root = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(body, 1);
        root.Children.Add(strip);
        root.Children.Add(body);

        // выбор мышью — только при реальном движении: иначе прокрутка клавиатурой «подсовывает»
        // строку под неподвижный курсор и выделение прыгает
        body.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (p == _lastMouse) return;
            _lastMouse = p;
            int i = IndexOf(entry);
            if (i != _selected) Select(i, scrollIntoView: false);
        };
        body.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            PasteAt(IndexOf(entry), plain: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        };

        var row = new RowView(entry, root, bodyBrush, stripBrush, star, starIcon, delete);
        UpdateRowActions(row, selected: false);
        return row;
    }

    private FrameworkElement ActionButton(string glyph, string tooltip, Action onClick, Func<Brush> idle, out TextBlock icon)
    {
        var glyphBlock = FluentIcons.Glyph(glyph, 14, idle());
        icon = glyphBlock;
        var brush = new SolidColorBrush(Colors.Transparent);
        var button = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = brush,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = glyphBlock,
            Margin = new Thickness(2, 0, 0, 0)
        };
        button.MouseEnter += (_, _) => { brush.Color = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF); glyphBlock.Foreground = JarvisTheme.AccentBrush; };
        button.MouseLeave += (_, _) => { brush.Color = Colors.Transparent; glyphBlock.Foreground = idle(); };
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); }; // не даём клику дойти до строки (= вставка)
        return button;
    }

    private void LoadThumb(HistoryEntry entry, Image target)
    {
        if (_thumbs.TryGetValue(entry.Id, out var cached)) { target.Source = cached; return; }
        Task.Run(() => HistoryImages.LoadFile(_store.ThumbPath(entry)))
            .ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                _thumbs[entry.Id] = t.Result;
                target.Source = t.Result;
            }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private int IndexOf(HistoryEntry entry) => _filtered.IndexOf(entry);

    private void Select(int index, bool scrollIntoView)
    {
        if (index < 0 || index >= _filtered.Count) return;
        if (index >= _rows.Count) RenderMore(index);
        if (_selected >= 0 && _selected < _rows.Count) Highlight(_rows[_selected], false);
        _selected = index;
        var row = _rows[index];
        Highlight(row, true);
        if (scrollIntoView) row.Root.BringIntoView();
    }

    private static void Highlight(RowView row, bool selected)
    {
        var duration = TimeSpan.FromMilliseconds(70);
        row.BodyBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(selected ? RowSelectedBg : Colors.Transparent, duration));
        row.StripBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(selected ? JarvisTheme.Accent : Colors.Transparent, duration));
        UpdateRowActions(row, selected);
    }

    /// <summary>Звезда: залитая и акцентная у избранного (видна всегда), контурная — только у выбранной строки.</summary>
    private static void UpdateRowActions(RowView row, bool selected)
    {
        bool fav = row.Entry.Favorite;
        row.StarIcon.Text = fav ? FluentIcons.StarFill : FluentIcons.Star;
        row.StarIcon.Foreground = fav ? JarvisTheme.AccentBrush : JarvisTheme.ForegroundBrush(0.55);
        row.Star.Visibility = selected || fav ? Visibility.Visible : Visibility.Hidden;
        row.Delete.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
    }

    // ── действия ──

    private void PasteAt(int index, bool plain)
    {
        if (_closing || index < 0 || index >= _filtered.Count) return;
        var entry = _filtered[index];
        _closing = true;
        Close(); // сразу, без анимации: фокус должен вернуться в целевое окно до Ctrl+V
        _paste(entry, plain);
    }

    /// <summary>
    /// Del. У избранной записи сначала снимается звезда (избранное не теряется одним нажатием;
    /// в режиме «Только избранное» это и есть «убрать из избранного»), повторный Del удаляет запись.
    /// </summary>
    private void RemoveAt(int index)
    {
        if (index < 0 || index >= _filtered.Count) return;
        var entry = _filtered[index];
        if (entry.Favorite)
        {
            ToggleFavoriteAt(index);
            return;
        }
        _store.Remove(entry.Id);
        Refilter(NeighbourId(index));
    }

    /// <summary>Ctrl+P / звезда: поставить или снять. В режиме избранного снятая запись уходит из списка.</summary>
    private void ToggleFavoriteAt(int index)
    {
        if (index < 0 || index >= _filtered.Count) return;
        var entry = _filtered[index];
        bool leavesView = FavoritesOnly && entry.Favorite;
        _store.SetFavorite(entry.Id, !entry.Favorite);
        Refilter(leavesView ? NeighbourId(index) : entry.Id);
    }

    /// <summary>Кого выбрать после ухода записи из списка: следующую, а у последней — предыдущую.</summary>
    private string? NeighbourId(int index) =>
        index + 1 < _filtered.Count ? _filtered[index + 1].Id : index > 0 ? _filtered[index - 1].Id : null;

    internal void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
        anim.Completed += (_, _) => { try { Close(); } catch { /* окно уже закрыто */ } };
        BeginAnimation(OpacityProperty, anim);
    }

    // ── форматирование ──

    internal static string MetaLine(HistoryEntry e, DateTime now)
    {
        var parts = new List<string>(3) { RelativeTime(e.Time, now) };
        if (!string.IsNullOrWhiteSpace(e.App)) parts.Add(e.App!);
        if (!e.IsImage && e.Text != null) parts.Add($"{e.Text.Length.ToString("N0", Ru)} симв.");
        return string.Join(" · ", parts);
    }

    internal static string RelativeTime(DateTime t, DateTime now)
    {
        var span = now - t;
        if (span < TimeSpan.FromMinutes(1)) return "только что";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} мин назад";
        if (t.Date == now.Date) return $"{(int)span.TotalHours} ч назад";
        if (t.Date == now.Date.AddDays(-1)) return $"вчера, {t:HH:mm}";
        return t.Year == now.Year ? t.ToString("d MMM, HH:mm", Ru) : t.ToString("d MMM yyyy", Ru);
    }

    /// <summary>Голый шаблон TextBox: глобальный стиль даёт рамку и свечение, в строке поиска они лишние.</summary>
    private static ControlTemplate BareTextBoxTemplate()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = host };
    }
}
