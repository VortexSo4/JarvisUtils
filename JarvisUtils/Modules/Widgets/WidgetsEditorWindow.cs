using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Action<WidgetModel>? _onAddWidget;
    private readonly Action<WidgetModel>? _onExport;
    private readonly Action<WidgetModel>? _onImport;

    private readonly Canvas _previewCanvas;
    private readonly Border _previewHost;
    private readonly ScrollViewer _previewScroll;
    private readonly TranslateTransform _panTransform = new();
    private readonly ListBox _elementsList;
    private readonly StackPanel _propertyPanel;
    private readonly TextBox _scriptBox;
    private readonly TabControl _rightTabs;

    private WidgetElement? _selectedElement;

    private Border? _draggingEl;
    private Point _dragOffset;
    private bool _isPanning;
    private Point _panStart;
    private Point _panOrigin;

    private const double CanvasPadding = 400;

    private TextBox? _xBox, _yBox, _wBox, _hBox;

    public WidgetsEditorWindow(
        WidgetModel model,
        LiveDataProvider data,
        Action onChange,
        Action<WidgetModel>? onAddWidget = null,
        Action<WidgetModel>? onExport = null,
        Action<WidgetModel>? onImport = null)
    {
        _model = model;
        _data = data;
        _onChange = onChange;
        _onAddWidget = onAddWidget;
        _onExport = onExport;
        _onImport = onImport;

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

        // ═══ ЛЕВАЯ ПАНЕЛЬ ═══
        var leftPanel = new DockPanel { Margin = new Thickness(10) };

        var elementsHeader = new TextBlock
        {
            Text = "ЭЛЕМЕНТЫ ВИДЖЕТА",
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = JarvisTheme.ForegroundBrush(0.6),
            FontSize = 11
        };
        DockPanel.SetDock(elementsHeader, Dock.Top);

        var addWidgetBtn = new Button
        {
            Content = "Добавить виджет",
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        addWidgetBtn.Click += (_, _) => _onAddWidget?.Invoke(_model);
        DockPanel.SetDock(addWidgetBtn, Dock.Top);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(addRow, Dock.Bottom);
        var addMenuBtn = new Button { Content = "+ Добавить элемент", HorizontalAlignment = HorizontalAlignment.Stretch };
        addMenuBtn.Click += (_, _) => ShowAddElementMenu(addMenuBtn);
        addRow.Children.Add(addMenuBtn);

        _elementsList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _elementsList.SelectionChanged += (s, e) =>
        {
            _selectedElement = _elementsList.SelectedItem as WidgetElement;
            DrawPreview();
            UpdatePropertyPanel();
        };
        _elementsList.MouseRightButtonUp += ElementsList_MouseRightButtonUp;

        leftPanel.Children.Add(elementsHeader);
        leftPanel.Children.Add(addWidgetBtn);
        leftPanel.Children.Add(addRow);
        leftPanel.Children.Add(_elementsList);

        // ═══ ЦЕНТР ═══
        _previewCanvas = new Canvas
        {
            RenderTransform = _panTransform,
            Width = _model.Width + CanvasPadding * 2,
            Height = _model.Height + CanvasPadding * 2,
            Background = Brushes.Transparent
        };

        _previewScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
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

        _previewHost.MouseRightButtonDown += PreviewHost_PanDown;
        _previewHost.MouseMove += PreviewHost_MouseMove;
        _previewHost.MouseRightButtonUp += PreviewHost_PanUp;
        _previewHost.MouseWheel += PreviewHost_MouseWheel;

        var centerDock = new DockPanel();
        var bottomBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var exportBtn = new Button { Content = "Экспорт в буфер", Margin = new Thickness(0, 0, 10, 0) };
        exportBtn.Click += (_, _) => _onExport?.Invoke(_model);
        var importBtn = new Button { Content = "Импорт из буфера" };
        importBtn.Click += (_, _) => _onImport?.Invoke(_model);

        bottomBar.Children.Add(exportBtn);
        bottomBar.Children.Add(importBtn);
        DockPanel.SetDock(bottomBar, Dock.Bottom);

        centerDock.Children.Add(bottomBar);
        centerDock.Children.Add(_previewHost);

        // ═══ ПРАВАЯ ПАНЕЛЬ ═══
        _rightTabs = new TabControl { Margin = new Thickness(10) };

        var propsTab = new TabItem { Header = "Свойства" };
        _propertyPanel = new StackPanel { Margin = new Thickness(4, 10, 4, 10) };
        var propScroll = new ScrollViewer
        {
            Content = _propertyPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent
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
        grid.Children.Add(centerDock);
        grid.Children.Add(_rightTabs);

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(centerDock, 1);
        Grid.SetColumn(_rightTabs, 2);

        Content = grid;

        Loaded += (_, _) =>
        {
            _previewScroll.ScrollToHorizontalOffset(CanvasPadding - 40);
            _previewScroll.ScrollToVerticalOffset(CanvasPadding - 40);
        };

        RefreshElementsList();
        DrawPreview();
        UpdatePropertyPanel();
    }

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

    private static string TypeIdPrefix(WidgetElementType type) => type switch
    {
        WidgetElementType.Panel => "panel",
        WidgetElementType.Text => "text",
        WidgetElementType.Button => "btn",
        WidgetElementType.Checkbox => "check",
        WidgetElementType.Note => "note",
        _ => "el"
    };

    /// <summary>Генерирует уникальный читаемый Id внутри виджета (btn_1, text_2, …).</summary>
    private string MakeUniqueId(string baseId)
    {
        if (string.IsNullOrWhiteSpace(baseId)) baseId = "el";
        baseId = baseId.Trim();
        if (!_model.Elements.Any(e => e.Id == baseId)) return baseId;

        int n = 2;
        while (_model.Elements.Any(e => e.Id == baseId + "_" + n)) n++;
        return baseId + "_" + n;
    }

    private void AddElement(WidgetElementType type)
    {
        var prefix = TypeIdPrefix(type);
        var el = new WidgetElement
        {
            Type = type,
            Text = TypeLabel(type),
            Width = 100,
            Height = 30,
            Id = MakeUniqueId(prefix + "_" + (_model.Elements.Count + 1))
        };
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
            CornerRadius = source.CornerRadius,
            Id = MakeUniqueId(source.Id + "_copy")
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
        ShowElementContextMenu(el);
    }

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
                CornerRadius = new CornerRadius(el.CornerRadius),
                ClipToBounds = true
            };
            Canvas.SetLeft(elBorder, CanvasPadding + el.X);
            Canvas.SetTop(elBorder, CanvasPadding + el.Y);

            elBorder.MouseLeftButtonDown += Element_MouseLeftButtonDown;
            elBorder.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                _selectedElement = el;
                _elementsList.SelectedItem = el;
                DrawPreview();
                UpdatePropertyPanel();
                ShowElementContextMenu(el);
            };

            UIElement child;
            if (el.Type == WidgetElementType.Button)
            {
                child = new Button
                {
                    Content = el.Text,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = SafeColor(el.Foreground),
                    FontSize = el.FontSize,
                    IsHitTestVisible = false
                };
            }
            else if (el.Type == WidgetElementType.Checkbox)
            {
                child = new CheckBox
                {
                    Content = el.Text,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = SafeColor(el.Foreground),
                    FontSize = el.FontSize,
                    IsHitTestVisible = false
                };
            }
            else if (el.Type == WidgetElementType.Note)
            {
                child = new TextBox
                {
                    Text = el.Text,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = SafeColor(el.Foreground),
                    FontSize = el.FontSize,
                    IsHitTestVisible = false
                };
            }
            else
            {
                child = new TextBlock
                {
                    Text = el.Text,
                    Foreground = SafeColor(el.Foreground),
                    Margin = new Thickness(4),
                    IsHitTestVisible = false,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
            }
            elBorder.Child = child;

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

    private void AddResizeHandle(Border elBorder, WidgetElement el)
    {
        var knob = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = JarvisTheme.AccentBrush,
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Bg, 0.8),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeNWSE,
            Tag = el
        };
        Canvas.SetLeft(knob, CanvasPadding + el.X + el.Width - 6);
        Canvas.SetTop(knob, CanvasPadding + el.Y + el.Height - 6);

        bool isResizing = false;
        Point resizeStart = new();
        double startW = 0, startH = 0;

        knob.MouseLeftButtonDown += (s, e) =>
        {
            isResizing = true;
            resizeStart = e.GetPosition(_previewCanvas);
            startW = el.Width;
            startH = el.Height;
            knob.CaptureMouse();
            e.Handled = true;
        };
        knob.MouseMove += (s, e) =>
        {
            if (!isResizing || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(_previewCanvas);
            el.Width = Math.Max(12, startW + (p.X - resizeStart.X));
            el.Height = Math.Max(12, startH + (p.Y - resizeStart.Y));

            elBorder.Width = el.Width;
            elBorder.Height = el.Height;
            Canvas.SetLeft(knob, CanvasPadding + el.X + el.Width - 6);
            Canvas.SetTop(knob, CanvasPadding + el.Y + el.Height - 6);

            _onChange?.Invoke();
            UpdatePositionFieldsOnly();
            e.Handled = true;
        };
        knob.MouseLeftButtonUp += (s, e) =>
        {
            isResizing = false;
            knob.ReleaseMouseCapture();
            DrawPreview();
            e.Handled = true;
        };

        _previewCanvas.Children.Add(knob);
    }

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
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            _previewScroll.ScrollToHorizontalOffset(_previewScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void Element_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not WidgetElement el) return;

        _selectedElement = el;
        _elementsList.SelectedItem = el;
        UpdatePropertyPanel();

        foreach (var child in _previewCanvas.Children.OfType<Border>())
        {
            if (child.Tag is WidgetElement we)
            {
                bool sel = we == _selectedElement;
                child.BorderBrush = sel ? JarvisTheme.AccentBrush : JarvisTheme.Solid(Colors.White, 0x22);
                child.BorderThickness = new Thickness(sel ? 2 : 1);
            }
        }

        RemoveResizeHandles();
        AddResizeHandle(b, el);

        _draggingEl = b;
        _dragOffset = e.GetPosition(b);
        b.CaptureMouse();
        b.MouseMove += DraggingElement_MouseMove;
        b.MouseLeftButtonUp += DraggingElement_MouseLeftButtonUp;
        e.Handled = true;
    }

    private void RemoveResizeHandles()
    {
        for (int i = _previewCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (_previewCanvas.Children[i] is Border b && b.Width == 12 && b.Height == 12)
                _previewCanvas.Children.RemoveAt(i);
        }
    }

    private void DraggingElement_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingEl == null || _selectedElement == null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(_previewCanvas);
        _selectedElement.X = pos.X - _dragOffset.X - CanvasPadding;
        _selectedElement.Y = pos.Y - _dragOffset.Y - CanvasPadding;

        Canvas.SetLeft(_draggingEl, CanvasPadding + _selectedElement.X);
        Canvas.SetTop(_draggingEl, CanvasPadding + _selectedElement.Y);

        foreach (var child in _previewCanvas.Children.OfType<Border>())
        {
            if (child.Width == 12 && child.Height == 12)
            {
                Canvas.SetLeft(child, CanvasPadding + _selectedElement.X + _selectedElement.Width - 6);
                Canvas.SetTop(child, CanvasPadding + _selectedElement.Y + _selectedElement.Height - 6);
            }
        }

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
        DrawPreview();
    }

    private void UpdatePropertyPanel()
    {
        _propertyPanel.Children.Clear();
        _xBox = _yBox = _wBox = _hBox = null;

        if (_selectedElement == null)
        {
            _propertyPanel.Children.Add(new TextBlock
            {
                Text = "Выберите элемент слева или на холсте",
                Foreground = JarvisTheme.ForegroundBrush(0.4),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var el = _selectedElement;

        _propertyPanel.Children.Add(new TextBlock
        {
            Text = "СВОЙСТВА ЭЛЕМЕНТА",
            Foreground = JarvisTheme.ForegroundBrush(0.6),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 10)
        });

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
                try { onApply(tb.Text); _onChange?.Invoke(); DrawPreview(); RefreshElementsList(); }
                catch { }
            };

            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(tb, 1);
            row.Children.Add(lbl);
            row.Children.Add(tb);
            _propertyPanel.Children.Add(row);
            return tb;
        }

        // Id — первым, с проверкой уникальности
        AddRow("Id", el.Id, v =>
        {
            v = (v ?? "").Trim();
            if (string.IsNullOrEmpty(v)) return;
            if (_model.Elements.Any(e => e != el && e.Id == v)) return; // занято — игнорируем
            el.Id = v;
        });

        _xBox = AddRow("X", el.X.ToString("0"), v => el.X = double.Parse(v));
        _yBox = AddRow("Y", el.Y.ToString("0"), v => el.Y = double.Parse(v));
        _wBox = AddRow("Ширина", el.Width.ToString("0"), v => el.Width = double.Parse(v));
        _hBox = AddRow("Высота", el.Height.ToString("0"), v => el.Height = double.Parse(v));
        AddRow("Скругление", el.CornerRadius.ToString("0"), v => el.CornerRadius = double.Parse(v));
        AddRow("Текст / Содержание", el.Text, v => el.Text = v);
        AddRow("Размер шрифта", el.FontSize.ToString("0"), v => el.FontSize = double.Parse(v));

        var bindRow = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        bindRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        bindRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bindLbl = new TextBlock
        {
            Text = "Источник",
            Foreground = JarvisTheme.ForegroundBrush(0.8),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
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
        if (_wBox != null) _wBox.Text = _selectedElement.Width.ToString("0");
        if (_hBox != null) _hBox.Text = _selectedElement.Height.ToString("0");
    }

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