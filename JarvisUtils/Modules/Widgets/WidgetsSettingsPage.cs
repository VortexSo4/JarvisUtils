using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JarvisUtils.Core;
using JarvisUtils.UI.Settings;
using JarvisUtils.UI.Theme;

namespace JarvisUtils.Modules.Widgets;

/// <summary>Страница виджетов в настройках: галерея «создать» + список с управлением.</summary>
internal sealed class WidgetsSettingsPage(WidgetsModule module)
{
    private static readonly Color CardIdle = Color.FromRgb(0x19, 0x1C, 0x25);
    private static readonly Color CardHover = Color.FromRgb(0x1F, 0x23, 0x30);

    private StackPanel? _list;
    private TextBlock? _listHeader;

    public FrameworkElement Build()
    {
        var root = new StackPanel();

        root.Children.Add(SettingsUi.Section("СОЗДАТЬ"));
        var gallery = new WrapPanel();
        gallery.Children.Add(Tile(FluentIcons.Note, "Заметка", "Стикер поверх окон", _ => module.CreateNote(null)));
        gallery.Children.Add(Tile(FluentIcons.Screenshot, "Скриншот", "Область поверх окон", PinFromSettings));
        gallery.Children.Add(Tile(FluentIcons.Photo, "Из буфера", "Закрепить картинку", _ => module.PinClipboardImage()));
        foreach (var t in WidgetTemplates.All)
        {
            var template = t;
            gallery.Children.Add(Tile(t.Glyph, t.Name, t.Description,
                _ => module.CreateFromTemplate(template, openEditor: ReferenceEquals(template, WidgetTemplates.Blank))));
        }
        root.Children.Add(gallery);

        root.Children.Add(new TextBlock
        {
            Text = "Виджеты двигаются за любое место и меняют размер за края. Правый клик — меню: слой, прозрачность, " +
                   "блокировка, сквозные клики. Картинки масштабируются колесом, Ctrl+колесо — прозрачность.",
            Foreground = JarvisTheme.ForegroundBrush(0.45),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        var header = new DockPanel { Margin = new Thickness(0, 22, 0, 8) };
        var hideAll = new JarvisToggleSwitch { IsChecked = !module.AllHidden, VerticalAlignment = VerticalAlignment.Center };
        hideAll.Checked += (_, _) => { if (module.AllHidden) module.ToggleAll(); };
        hideAll.Unchecked += (_, _) => { if (!module.AllHidden) module.ToggleAll(); };
        var hideAllPanel = new StackPanel { Orientation = Orientation.Horizontal };
        hideAllPanel.Children.Add(new TextBlock { Text = "показывать", Foreground = JarvisTheme.ForegroundBrush(0.5), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        hideAllPanel.Children.Add(hideAll);
        DockPanel.SetDock(hideAllPanel, Dock.Right);
        header.Children.Add(hideAllPanel);
        _listHeader = (TextBlock)SettingsUi.Section("");
        _listHeader.Margin = new Thickness(0);
        _listHeader.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_listHeader);
        root.Children.Add(header);

        _list = new StackPanel();
        root.Children.Add(_list);
        RefreshList();

        // список живой: виджеты меняются и вне настроек (скрыли с панели, создали хоткеем)
        Action refresh = () => root.Dispatcher.BeginInvoke(RefreshList, DispatcherPriority.Background);
        module.Changed += refresh;
        root.Unloaded += (_, _) => module.Changed -= refresh;
        return root;
    }

    /// <summary>Окно настроек прячем на время выделения — иначе оно попадёт в кадр.</summary>
    private void PinFromSettings(FrameworkElement source)
    {
        var owner = Window.GetWindow(source);
        owner?.Hide();
        // кадр снимается сразу — даём DWM убрать окно с экрана
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            module.PinScreenshot(() => { owner?.Show(); owner?.Activate(); });
        };
        timer.Start();
    }

    private void RefreshList()
    {
        if (_list == null) return;
        _list.Children.Clear();
        var widgets = module.Widgets;
        _listHeader!.Text = widgets.Count == 0 ? "ВИДЖЕТЫ" : $"ВИДЖЕТЫ · {widgets.Count}";
        if (widgets.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "Пока пусто — создайте заметку или виджет из галереи выше.",
                Foreground = JarvisTheme.ForegroundBrush(0.4),
                FontSize = 12.5,
                Margin = new Thickness(2, 4, 0, 0)
            });
            return;
        }
        foreach (var w in widgets.ToList()) _list.Children.Add(Row(w));
    }

    // ── плитка галереи ──

    private static Border Tile(string glyph, string title, string subtitle, Action<FrameworkElement> onClick)
    {
        var icon = FluentIcons.Glyph(glyph, 20, JarvisTheme.AccentBrush);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var panel = new StackPanel();
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 1) });
        panel.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = JarvisTheme.ForegroundBrush(0.45), TextTrimming = TextTrimming.CharacterEllipsis });

        var brush = new SolidColorBrush(CardIdle);
        var tile = new Border
        {
            Width = 148,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 11, 12, 11),
            CornerRadius = new CornerRadius(10),
            Background = brush,
            BorderBrush = JarvisTheme.Solid(Colors.White, 0.05),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = panel
        };
        tile.MouseEnter += (_, _) => Animate(brush, CardHover);
        tile.MouseLeave += (_, _) => Animate(brush, CardIdle);
        tile.MouseLeftButtonUp += (_, _) => onClick(tile);
        return tile;
    }

    // ── строка списка ──

    private Border Row(WidgetModel w)
    {
        string glyph = w.Kind switch
        {
            WidgetKind.Note => FluentIcons.Note,
            WidgetKind.Image => FluentIcons.Photo,
            _ => WidgetTemplates.All.FirstOrDefault(t => t.Name == w.Name)?.Glyph ?? FluentIcons.Code
        };
        var icon = FluentIcons.Glyph(glyph, 16, JarvisTheme.ForegroundBrush(w.Visible ? 0.85 : 0.35));
        icon.Width = 22;

        var name = new TextBlock
        {
            Text = w.Kind == WidgetKind.Note && w.Text.Length > 0 ? TextUtil.Preview(w.Text, 48) : w.Name,
            FontSize = 13,
            Foreground = JarvisTheme.ForegroundBrush(w.Visible ? 0.92 : 0.45),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Двойной клик — переименовать"
        };
        var rename = new TextBox { Text = w.Name, Visibility = Visibility.Collapsed, Padding = new Thickness(6, 3, 6, 3) };
        name.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            name.Visibility = Visibility.Collapsed;
            rename.Visibility = Visibility.Visible;
            rename.Focus();
            rename.SelectAll();
        };
        void Commit() { module.Rename(w, rename.Text); name.Visibility = Visibility.Visible; rename.Visibility = Visibility.Collapsed; }
        rename.LostKeyboardFocus += (_, _) => Commit();
        rename.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            else if (e.Key == Key.Escape) { rename.Text = w.Name; Commit(); }
        };

        string layer = w.Layer switch { WidgetLayer.Topmost => "поверх окон", WidgetLayer.Desktop => "под окнами", _ => "обычный" };
        string kind = w.Kind switch { WidgetKind.Note => "заметка", WidgetKind.Image => "картинка", _ => "свой" };
        var flags = string.Join(" · ", new[]
        {
            kind, layer,
            w.Locked ? "закреплён" : null,
            w.ClickThrough ? "сквозные клики" : null,
            w.Visible ? null : "скрыт"
        }.Where(s => s != null));
        var subtitle = new TextBlock { Text = flags, FontSize = 11, Foreground = JarvisTheme.ForegroundBrush(0.4) };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        var nameHost = new Grid();
        nameHost.Children.Add(name);
        nameHost.Children.Add(rename);
        texts.Children.Add(nameHost);
        texts.Children.Add(subtitle);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(IconButton(FluentIcons.View, "Показать, где он", () => module.Locate(w)));
        if (w.Kind != WidgetKind.Image)
            actions.Children.Add(IconButton(FluentIcons.Edit, w.Kind == WidgetKind.Note ? "Писать в заметку" : "Редактор", () => module.Edit(w)));
        actions.Children.Add(IconButton(FluentIcons.Duplicate, "Дублировать", () => module.Duplicate(w)));
        actions.Children.Add(DeleteButton(w));

        var toggle = new JarvisToggleSwitch { IsChecked = w.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        toggle.Checked += (_, _) => module.SetVisible(w, true);
        toggle.Unchecked += (_, _) => module.SetVisible(w, false);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(actions, 2);
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        grid.Children.Add(actions);
        grid.Children.Add(toggle);

        var brush = new SolidColorBrush(CardIdle);
        var card = new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid
        };
        card.MouseEnter += (_, _) => Animate(brush, CardHover);
        card.MouseLeave += (_, _) => Animate(brush, CardIdle);
        return card;
    }

    private static Button IconButton(string glyph, string tip, Action onClick)
    {
        var button = new Button { Content = FluentIcons.Glyph(glyph, 14), ToolTip = tip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Удаление в два клика: первый «взводит» кнопку на пару секунд.</summary>
    private Button DeleteButton(WidgetModel w)
    {
        var glyph = FluentIcons.Glyph(FluentIcons.Delete, 14);
        var button = new Button { Content = glyph, ToolTip = "Удалить" };
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        DispatcherTimer? disarm = null;
        button.Click += (_, _) =>
        {
            if (disarm == null)
            {
                glyph.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
                button.ToolTip = "Нажмите ещё раз, чтобы удалить";
                disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                disarm.Tick += (_, _) =>
                {
                    disarm!.Stop();
                    disarm = null;
                    glyph.ClearValue(TextBlock.ForegroundProperty);
                    button.ToolTip = "Удалить";
                };
                disarm.Start();
                return;
            }
            disarm.Stop();
            module.Delete(w);
        };
        return button;
    }

    private static void Animate(SolidColorBrush brush, Color to) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(90)));
}
