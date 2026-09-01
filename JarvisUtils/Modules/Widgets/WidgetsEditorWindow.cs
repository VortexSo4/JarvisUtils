using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JarvisUtils.Core;
using JarvisUtils.UI.Theme;

namespace JarvisUtils.Modules.Widgets;

public sealed class WidgetsEditorWindow : Window
{
    private readonly WidgetModel _model;
    private readonly LiveDataProvider _data;
    private readonly Action _onChange;

    private readonly Canvas _previewCanvas;
    private readonly Border _previewHost;
    private readonly ScrollViewer _previewScroll;
    private readonly TranslateTransform _panTransform = new();
    private readonly ListBox _elementsList;
    private readonly StackPanel _propertyPanel;
    private readonly TextBox _scriptBox;
    private readonly TabControl _rightTabs;

    private WidgetElement? _selectedElement;

    // drag/resize state for elements
    private Border? _draggingEl;
    private Point _dragOffset;
    private bool _isPanning;
    private Point _panStart;
    private Point _panOrigin;

    private const double CanvasPadding = 400; // запас вокруг виджета, чтобы можно было таскать элементы за границы и панорамировать

    public WidgetsEditorWindow(WidgetModel model, LiveDataProvider data, Action onChange)
    {
        _model = model;
        _data = data;
        _onChange = onChange;

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/JarvisUtils;component/UI/Theme/JarvisStyles.xaml")
        });

        Title = "Редактор виджетов — " + model.Name;
        Width = 1050; Height = 680;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1D));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        // ═══ ЛЕВАЯ ПАНЕЛЬ (Элементы) ═══
        var leftPanel = new DockPanel { Margin = new Thickness(10) };

        var elementsHeader = new TextBlock { Text = "ЭЛЕМЕНТЫ ВИДЖЕТА", Margin = new Thickness(0, 0, 0, 8), Foreground = JarvisTheme.ForegroundBrush(0.6), FontSize = 11 };
        DockPanel.SetDock(elementsHeader, Dock.Top);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(addRow, Dock.Bottom);
        var addMenuBtn = new Button { Content = "+ Добавить элемент", HorizontalAlignment = HorizontalAlignment.Stretch };
        addMenuBtn.Click += (_, _) => ShowAddElementMenu(addMenuBtn);
        addRow.Children.Add(addMenuBtn);

        // ListBox теперь показывает el.ToString() (тип: текст), не полное имя класса
        _elementsList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _elementsList.SelectionChanged += (s, e) =>
        {
            _selectedElement = _elementsList.SelectedItem as WidgetElement;
            DrawPreview();
            UpdatePropertyPanel();
        };
        _elementsList.MouseRightButtonUp += ElementsList_MouseRightButtonUp;

        leftPanel.Children.Add(elementsHeader);
        leftPanel.Children.Add(addRow);
        leftPanel.Children.Add(_elementsList);

        RefreshElementsList();

        // ═══ ЦЕНТР (Холст с панорамированием + скролл + resize виджета) ═══
        _previewCanvas = new Canvas
        {
            RenderTransform = _panTransform,
            Width = _model.Width + CanvasPadding * 2,
            Height = _model.Height + CanvasPadding * 2,
            Background = Brushes.Transparent
        };

        _previewScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _previewCanvas,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)),
            CanContentScroll = false
        };

        _previewHost = new Border
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Child = _previewScroll
        };

        // Панорамирование средней/правой кнопкой мыши (левая теперь свободна для выделения/drag элементов)
        _previewHost.MouseRightButtonDown += PreviewHost_PanDown;
        _previewHost.MouseMove += PreviewHost_MouseMove;
        _previewHost.MouseRightButtonUp += PreviewHost_PanUp;
        _previewHost.MouseWheel += PreviewHost_MouseWheel;

        // ═══ ПРАВАЯ ПАНЕЛЬ — раздельные вкладки "Свойства" / "Скрипт" ═══
        _rightTabs = new TabControl { Margin = new Thickness(10) };

        var propsTab = new TabItem { Header = "Свойства" };
        _propertyPanel = new StackPanel { Margin = new Thickness(4, 10, 4, 10) };
        var propScroll = new ScrollViewer
        {
            Content = _propertyPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        propsTab.Content = propScroll;

        var scriptTab = new TabItem { Header = "Скрипт" };
        var scriptDock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };

        var applyBtn = new Button { Content = "Применить скрипт", Margin = new Thickness(0, 10, 0, 0), Height = 30 };
        DockPanel.SetDock(applyBtn, Dock.Bottom);

        var scriptErrorBox = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        DockPanel.SetDock(scriptErrorBox, Dock.Bottom);

        applyBtn.Click += (_, _) =>
        {
            _model.Script = _scriptBox.Text;
            var error = TryCompileScript(_model.Script);
            if (error != null)
            {
                scriptErrorBox.Text = error;
                scriptErrorBox.Visibility = Visibility.Visible;
            }
            else
            {
                scriptErrorBox.Visibility = Visibility.Collapsed;
            }
            _onChange?.Invoke();
        };

        _scriptBox = new TextBox
        {
            Text = _model.Script,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        scriptDock.Children.Add(applyBtn);
        scriptDock.Children.Add(scriptErrorBox);
        scriptDock.Children.Add(_scriptBox);
        scriptTab.Content = scriptDock;

        _rightTabs.Items.Add(propsTab);
        _rightTabs.Items.Add(scriptTab);

        grid.Children.Add(leftPanel);
        grid.Children.Add(_previewHost);
        grid.Children.Add(_rightTabs);

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(_previewHost, 1);
        Grid.SetColumn(_rightTabs, 2);

        Content = grid;

        // центрируем скролл на виджете при открытии
        Loaded += (_, _) =>
        {
            _previewScroll.ScrollToHorizontalOffset(CanvasPadding - 40);
            _previewScroll.ScrollToVerticalOffset(CanvasPadding - 40);
        };

        DrawPreview();
        UpdatePropertyPanel();
    }

    // ─────────────────────────────────────────────────────────
    // Список элементов
    // ─────────────────────────────────────────────────────────

    private void RefreshElementsList()
    {
        var prevSelected = _selectedElement;
        _elementsList.Items.Clear();
        foreach (var el in _model.Elements)
            _elementsList.Items.Add(el);

        if (prevSelected != null && _model.Elements.Contains(prevSelected))
            _elementsList.SelectedItem = prevSelected;
        else if (_model.Elements.Count > 0)
            _elementsList.SelectedIndex = 0;
    }

    private void ShowAddElementMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, IsOpen = true };
        foreach (WidgetElementType type in Enum.GetValues(typeof(WidgetElementType)))
        {
            var item = new MenuItem { Header = "+ " + TypeLabel(type) };
            item.Click += (_, _) => AddElement(type);
            menu.Items.Add(item);
        }
    }

    private static string TypeLabel(WidgetElementType type) => type switch
    {
        WidgetElementType.Panel => "Панель",
        WidgetElementType.Text => "Текст",
        WidgetElementType.Button => "Кнопка",
        WidgetElementType.Checkbox => "Чекбокс",
        WidgetElementType.Note => "Заметка",
        _ => type.ToString()
    };

    private void AddElement(WidgetElementType type)
    {
        var el = new WidgetElement { Type = type, Text = TypeLabel(type), Width = 100, Height = 30 };
        _model.Elements.Add(el);
        _onChange?.Invoke();
        RefreshElementsList();
        _elementsList.SelectedItem = el;
    }

    private void DuplicateElement(WidgetElement source)
    {
        var copy = new WidgetElement
        {
            Type = source.Type,
            X = source.X + 12,
            Y = source.Y + 12,
            Width = source.Width,
            Height = source.Height,
            Text = source.Text,
            Binding = source.Binding,
            Background = source.Background,
            Foreground = source.Foreground,
            FontSize = source.FontSize,
            CornerRadius = source.CornerRadius
        };
        _model.Elements.Add(copy);
        _onChange?.Invoke();
        RefreshElementsList();
        _elementsList.SelectedItem = copy;
        DrawPreview();
    }

    private void DeleteElement(WidgetElement el)
    {
        _model.Elements.Remove(el);
        if (_selectedElement == el) _selectedElement = null;
        _onChange?.Invoke();
        RefreshElementsList();
        DrawPreview();
        UpdatePropertyPanel();
    }

    private void ElementsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_elementsList.SelectedItem is not WidgetElement el) return;
        ShowElementContextMenu(el, e.GetPosition(_elementsList));
    }

    // ПКМ по элементу на холсте ИЛИ в списке — попап с удалением/дублированием (пункт 7, кнопка "удалить выбранный" убрана — пункт 6)
    private void ShowElementContextMenu(WidgetElement el, Point? relativeTo = null)
    {
        var menu = new ContextMenu();

        var itemDuplicate = new MenuItem { Header = "Дублировать" };
        itemDuplicate.Click += (_, _) => DuplicateElement(el);

        var itemFront = new MenuItem { Header = "На передний план" };
        itemFront.Click += (_, _) => { _model.Elements.Remove(el); _model.Elements.Add(el); _onChange?.Invoke(); RefreshElementsList(); DrawPreview(); };

        var itemBack = new MenuItem { Header = "На задний план" };
        itemBack.Click += (_, _) => { _model.Elements.Remove(el); _model.Elements.Insert(0, el); _onChange?.Invoke(); RefreshElementsList(); DrawPreview(); };

        var itemDelete = new MenuItem { Header = "Удалить" };
        itemDelete.Click += (_, _) => DeleteElement(el);

        menu.Items.Add(itemDuplicate);
        menu.Items.Add(itemFront);
        menu.Items.Add(itemBack);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemDelete);

        menu.IsOpen = true;
    }

    // ─────────────────────────────────────────────────────────
    // Отрисовка холста
    // ─────────────────────────────────────────────────────────

    private void DrawPreview()
    {
        _previewCanvas.Children.Clear();

        var widgetBorder = new Border
        {
            Width = _model.Width,
            Height = _model.Height,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x18, 0x20)),
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0x33),
            BorderThickness = new Thickness(1)
        };
        Canvas.SetLeft(widgetBorder, CanvasPadding);
        Canvas.SetTop(widgetBorder, CanvasPadding);
        _previewCanvas.Children.Add(widgetBorder);

        // лёгкая сетка внутри границ виджета — как в реальном режиме редактирования
        DrawGrid();

        foreach (var el in _model.Elements)
        {
            var elBorder = new Border
            {
                Width = el.Width,
                Height = el.Height,
                Background = SafeColor(el.Background),
                BorderBrush = _selectedElement == el ? JarvisTheme.AccentBrush : JarvisTheme.Solid(Colors.White, 0x22),
                BorderThickness = new Thickness(_selectedElement == el ? 2 : 1),
                Cursor = Cursors.SizeAll,
                Tag = el,
                CornerRadius = new CornerRadius(el.CornerRadius)
            };
            Canvas.SetLeft(elBorder, CanvasPadding + el.X);
            Canvas.SetTop(elBorder, CanvasPadding + el.Y);

            elBorder.MouseLeftButtonDown += Element_MouseLeftButtonDown;
            elBorder.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true; // не даём холсту начать панорамирование/снятие выделения
                _selectedElement = el;
                _elementsList.SelectedItem = el;
                DrawPreview();
                UpdatePropertyPanel();
                ShowElementContextMenu(el);
            };

            var lbl = new TextBlock
            {
                Text = el.Text,
                Foreground = SafeColor(el.Foreground),
                Margin = new Thickness(4),
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            elBorder.Child = lbl;

            _previewCanvas.Children.Add(elBorder);

            if (_selectedElement == el)
                AddResizeHandle(elBorder, el);
        }
    }

    private void DrawGrid()
    {
        const double step = 20;
        var gridBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        for (double x = 0; x <= _model.Width; x += step)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = CanvasPadding + x, Y1 = CanvasPadding,
                X2 = CanvasPadding + x, Y2 = CanvasPadding + _model.Height,
                Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false
            };
            _previewCanvas.Children.Add(line);
        }
        for (double y = 0; y <= _model.Height; y += step)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = CanvasPadding, Y1 = CanvasPadding + y,
                X2 = CanvasPadding + _model.Width, Y2 = CanvasPadding + y,
                Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false
            };
            _previewCanvas.Children.Add(line);
        }
    }

    private static SolidColorBrush SafeColor(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.Gray); }
    }

    // ─────────────────────────────────────────────────────────
    // Resize отдельного элемента (маленький "уголок" — не путать с resize виджета)
    // ─────────────────────────────────────────────────────────

    private void AddResizeHandle(Border elBorder, WidgetElement el)
    {
        var thumb = new Thumb
        {
            Width = 10,
            Height = 10,
            Cursor = Cursors.SizeNWSE,
            Background = JarvisTheme.AccentBrush
        };
        Canvas.SetLeft(thumb, CanvasPadding + el.X + el.Width - 5);
        Canvas.SetTop(thumb, CanvasPadding + el.Y + el.Height - 5);

        thumb.DragDelta += (s, e) =>
        {
            el.Width = Math.Max(12, el.Width + e.HorizontalChange);
            el.Height = Math.Max(12, el.Height + e.VerticalChange);
            _onChange?.Invoke();
            DrawPreview();
            UpdatePropertyPanel();
        };

        _previewCanvas.Children.Add(thumb);
    }

    // ─────────────────────────────────────────────────────────
    // Панорамирование холста (теперь ПКМ+drag, чтобы не конфликтовать с выделением/drag элементов;
    // плюс ScrollViewer уже даёт нормальный скролл колесом/полосами — пункт 3)
    // ─────────────────────────────────────────────────────────

    private void PreviewHost_PanDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(_previewHost);
        _panOrigin = new Point(_previewScroll.HorizontalOffset, _previewScroll.VerticalOffset);
        _previewHost.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(_previewHost);
        var dx = pos.X - _panStart.X;
        var dy = pos.Y - _panStart.Y;
        _previewScroll.ScrollToHorizontalOffset(_panOrigin.X - dx);
        _previewScroll.ScrollToVerticalOffset(_panOrigin.Y - dy);
    }

    private void PreviewHost_PanUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        _previewHost.ReleaseMouseCapture();
    }

    private void PreviewHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // обычный вертикальный скролл колесом; Shift+колесо — горизонтальный
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            _previewScroll.ScrollToHorizontalOffset(_previewScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
        // иначе — пусть ScrollViewer обработает сам (вертикальный скролл по умолчанию)
    }

    // ─────────────────────────────────────────────────────────
    // Drag элементов — теперь работает за пределами исходных размеров виджета
    // (ограничение убрано; можно вытащить элемент хоть за край, resize'ится нормально)
    // ─────────────────────────────────────────────────────────

    private void Element_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not WidgetElement el) return;

        _selectedElement = el;
        _elementsList.SelectedItem = el;
        DrawPreview();
        UpdatePropertyPanel();

        _draggingEl = b;
        _dragOffset = e.GetPosition(b);
        b.CaptureMouse();
        b.MouseMove += DraggingElement_MouseMove;
        b.MouseLeftButtonUp += DraggingElement_MouseLeftButtonUp;
        e.Handled = true;
    }

    private void DraggingElement_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingEl == null || _selectedElement == null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(_previewCanvas);
        _selectedElement.X = pos.X - _dragOffset.X - CanvasPadding;
        _selectedElement.Y = pos.Y - _dragOffset.Y - CanvasPadding;

        Canvas.SetLeft(_draggingEl, CanvasPadding + _selectedElement.X);
        Canvas.SetTop(_draggingEl, CanvasPadding + _selectedElement.Y);

        UpdatePositionFieldsOnly();
        _onChange?.Invoke();
    }

    private void DraggingElement_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b)
        {
            b.MouseMove -= DraggingElement_MouseMove;
            b.MouseLeftButtonUp -= DraggingElement_MouseLeftButtonUp;
            b.ReleaseMouseCapture();
        }
        _draggingEl = null;
        DrawPreview(); // перерисуем, чтобы ручка resize встала на новое место
    }

    // ─────────────────────────────────────────────────────────
    // Панель свойств (пункт 2: разделена вкладками от скрипта;
    // раскладка "имя слева — поле справа" в одну строку через Grid)
    // ─────────────────────────────────────────────────────────

    private TextBox? _xBox, _yBox;

    private void UpdatePropertyPanel()
    {
        _propertyPanel.Children.Clear();
        _xBox = _yBox = null;

        if (_selectedElement == null)
        {
            _propertyPanel.Children.Add(new TextBlock { Text = "Выберите элемент слева или на холсте", Foreground = JarvisTheme.ForegroundBrush(0.4), TextWrapping = TextWrapping.Wrap });
            return;
        }

        var el = _selectedElement;

        _propertyPanel.Children.Add(new TextBlock { Text = "СВОЙСТВА ЭЛЕМЕНТА", Foreground = JarvisTheme.ForegroundBrush(0.6), FontSize = 11, Margin = new Thickness(0, 0, 0, 10) });

        TextBox AddRow(string label, string value, Action<string> onApply)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = label,
                Foreground = JarvisTheme.ForegroundBrush(0.8),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var tb = new TextBox { Text = value };
            tb.TextChanged += (s, e) =>
            {
                try { onApply(tb.Text); _onChange?.Invoke(); DrawPreview(); }
                catch { /* пользователь ещё печатает — игнорируем частично невалидный ввод */ }
            };

            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(tb, 1);
            row.Children.Add(lbl);
            row.Children.Add(tb);
            _propertyPanel.Children.Add(row);
            return tb;
        }

        _xBox = AddRow("X", el.X.ToString("0"), v => el.X = double.Parse(v));
        _yBox = AddRow("Y", el.Y.ToString("0"), v => el.Y = double.Parse(v));
        AddRow("Ширина", el.Width.ToString("0"), v => el.Width = double.Parse(v));
        AddRow("Высота", el.Height.ToString("0"), v => el.Height = double.Parse(v));
        AddRow("Скругление", el.CornerRadius.ToString("0"), v => el.CornerRadius = double.Parse(v));
        AddRow("Текст / Содержание", el.Text, v => el.Text = v);
        AddRow("Размер шрифта", el.FontSize.ToString("0"), v => el.FontSize = double.Parse(v));

        // Источник данных — теперь стилизованный ComboBox (пункт 8: стиль подтягивается из JarvisStyles.xaml,
        // добавь туда Style TargetType="ComboBox" и "ComboBoxItem" по образцу ListBoxItem выше — своего стиля
        // для ComboBox в присланном ResourceDictionary нет, нужен либо файл с ним, либо добавлю ниже сам)
        var bindRow = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        bindRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        bindRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bindLbl = new TextBlock { Text = "Источник", Foreground = JarvisTheme.ForegroundBrush(0.8), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var bindCombo = new ComboBox { ItemsSource = new[] { "Нет", "Time", "CPU", "RAM" } };
        bindCombo.SelectedItem = string.IsNullOrEmpty(el.Binding) ? "Нет" : el.Binding;
        bindCombo.SelectionChanged += (s, e) =>
        {
            el.Binding = bindCombo.SelectedItem?.ToString() == "Нет" ? "" : bindCombo.SelectedItem?.ToString() ?? "";
            _onChange?.Invoke();
        };
        Grid.SetColumn(bindLbl, 0);
        Grid.SetColumn(bindCombo, 1);
        bindRow.Children.Add(bindLbl);
        bindRow.Children.Add(bindCombo);
        _propertyPanel.Children.Add(bindRow);

        AddRow("Цвет фона", el.Background, v => el.Background = v);
        AddRow("Цвет текста", el.Foreground, v => el.Foreground = v);

        _propertyPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });

        var deleteBtn = new Button { Content = "Удалить элемент", HorizontalAlignment = HorizontalAlignment.Stretch };
        deleteBtn.Click += (_, _) => DeleteElement(el);
        _propertyPanel.Children.Add(deleteBtn);
    }

    private void UpdatePositionFieldsOnly()
    {
        if (_selectedElement == null) return;
        if (_xBox != null) _xBox.Text = _selectedElement.X.ToString("0");
        if (_yBox != null) _yBox.Text = _selectedElement.Y.ToString("0");
    }

    // Пробная компиляция скрипта прямо в редакторе — та же логика, что в WidgetWindow.CompileScript(),
    // но без создания живого Script<object> в поле окна: тут нужен только текст ошибки для UI.
    // Возвращает null при успехе, иначе текст ошибки компиляции.
    private static string? TryCompileScript(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            var options = Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default
                .WithReferences(typeof(object).Assembly, typeof(Window).Assembly, typeof(Brush).Assembly)
                .WithImports("System", "System.Windows", "System.Windows.Media", "System.Collections.Generic");

            var script = Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.Create(
                source, options, globalsType: typeof(ScriptGlobals));
            script.Compile();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}