using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

/// <summary>
/// Окно-хост виджета: перемещение (нативный DragMove с прилипанием к краям экрана и
/// соседним виджетам), ресайз (нативный цикл WM_SIZING), слои, блокировка, сквозные
/// клики, панель на ховере и контекстное меню. Содержимое — IWidgetContent.
/// </summary>
public sealed class WidgetWindow : Window
{
    private const double SnapDistance = 6;  // DIP: лёгкое прилипание, не магнит
    private const double SnapGap = 8;  // зазор между прилипшими виджетами, DIP
    private const double GripSize = 6;

    private readonly WidgetsModule _module;
    private readonly Grid _root;
    private readonly Border _contentHost;
    private readonly Grid _grips;
    private readonly StackPanel _toolbar;
    private readonly Grid _editOverlay;
    private readonly Rectangle _outline;
    private readonly TextBlock _lockButtonGlyph;
    private IWidgetContent? _content;
    private IntPtr _hwnd;
    private IntPtr _previousForeground;
    private bool _editMode;
    private bool _lightTone;
    private bool _animateIn;
    private bool _flashing;

    public WidgetModel Model { get; }
    internal IJarvisServices Services => _module.Services;
    internal IntPtr Handle => _hwnd;

    internal WidgetWindow(WidgetModel model, WidgetsModule module, BitmapSource? preloadedImage = null, bool animateIn = false)
    {
        Model = model;
        _module = module;
        _animateIn = animateIn;

        Title = model.Name;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; // ресайз — свой нативный (SC_SIZE), без системной рамки
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = 48;
        MinHeight = 32;
        Left = model.X;
        Top = model.Y;
        Width = model.Width;
        Height = model.Height;
        Opacity = animateIn ? 0 : model.Opacity;
        EnsureOnScreen();

        _contentHost = new Border();

        _outline = new Rectangle
        {
            Stroke = JarvisTheme.AccentBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            RadiusX = 10, RadiusY = 10,
            IsHitTestVisible = false
        };
        var nameTag = new Border
        {
            Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.9),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock { Text = model.Name, FontSize = 11, Foreground = JarvisTheme.AccentBrush }
        };
        _editOverlay = new Grid { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _editOverlay.Children.Add(_outline);
        _editOverlay.Children.Add(nameTag);

        _lockButtonGlyph = FluentIcons.Glyph(model.Locked ? FluentIcons.Lock : FluentIcons.Unlock, 12);
        _toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 4, 0),
            Opacity = 0,
            IsHitTestVisible = false
        };

        _grips = BuildGrips();

        _root = new Grid();
        _root.Children.Add(_contentHost);
        _root.Children.Add(_editOverlay);
        _root.Children.Add(_grips);
        _root.Children.Add(_toolbar);
        Content = _root;
        Resources.MergedDictionaries.Add(WidgetUi.Theme); // меню, подсказки, полосы прокрутки — в стиле приложения

        LoadContent(preloadedImage);
        UpdateGrips();

        _root.MouseLeftButtonDown += OnRootMouseDown;
        _root.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(); };
        MouseEnter += (_, _) => FadeToolbar(true);
        MouseLeave += (_, _) => FadeToolbar(false);
        Loaded += (_, _) =>
        {
            if (!_animateIn) return;
            _animateIn = false;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, Model.Opacity, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };
    }

    // ══════════════ содержимое ══════════════

    private void LoadContent(BitmapSource? preloadedImage = null)
    {
        _content?.Dispose();
        _content = Model.Kind switch
        {
            WidgetKind.Note => new NoteContent(Model, this),
            WidgetKind.Image => new ImageContent(Model, this, preloadedImage),
            _ => new CustomContent(Model, Services, ModelChanged)
        };
        _contentHost.Child = _content.View;
        BuildToolbar();
        if (_hwnd != IntPtr.Zero) ApplyExStyle();
    }

    /// <summary>Перезагрузить содержимое (после правки в редакторе).</summary>
    internal void ReloadContent()
    {
        Title = Model.Name;
        ((TextBlock)((Border)_editOverlay.Children[1]).Child).Text = Model.Name;
        LoadContent();
    }

    internal void FocusNote()
    {
        WindowHelper.ForceForeground(this);
        (_content as NoteContent)?.FocusText();
    }

    // ══════════════ Win32 ══════════════

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyExStyle();
        ApplyLayer();
    }

    private void ApplyExStyle()
    {
        int ex = WinApi.GetWindowLong(_hwnd, WinApi.GWL_EXSTYLE) | WinApi.WS_EX_TOOLWINDOW; // не в Alt+Tab

        // Клик не должен красть фокус у рабочего окна — кроме заметок (в них печатают)
        // и обычного слоя (кликом окно поднимается наверх, как любое другое).
        bool noActivate = !(_content?.NeedsKeyboard ?? false) && Model.Layer != WidgetLayer.Normal;
        ex = noActivate ? ex | WinApi.WS_EX_NOACTIVATE : ex & ~WinApi.WS_EX_NOACTIVATE;

        bool clickThrough = Model.ClickThrough && !_editMode;
        ex = clickThrough ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~WS_EX_TRANSPARENT;
        WinApi.SetWindowLong(_hwnd, WinApi.GWL_EXSTYLE, ex);
    }

    private void ApplyLayer()
    {
        Topmost = Model.Layer == WidgetLayer.Topmost;
        if (_hwnd == IntPtr.Zero) return;
        if (Model.Layer == WidgetLayer.Desktop)
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        ApplyExStyle();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING when Model.Layer == WidgetLayer.Desktop && !_flashing:
            {
                // «под окнами»: любое изменение z-порядка (клик, активация) — обратно на дно
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & SWP_NOZORDER) == 0)
                {
                    pos.hwndInsertAfter = HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;
            }
            case WM_MOVING:
            {
                var rect = Marshal.PtrToStructure<RECT>(lParam);
                SnapMove(ref rect);
                Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
                return (IntPtr)1;
            }
            case WM_SIZING:
            {
                var rect = Marshal.PtrToStructure<RECT>(lParam);
                AdjustSizing(ref rect, wParam.ToInt32());
                Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
                return (IntPtr)1;
            }
            case WM_EXITSIZEMOVE:
                SyncGeometry();
                break;
            case WM_ACTIVATE when (wParam.ToInt32() & 0xFFFF) != 0:
                // lParam — окно, у которого забрали фокус: на Esc вернём его туда
                if (lParam != IntPtr.Zero && lParam != _hwnd) _previousForeground = lParam;
                break;
        }
        return IntPtr.Zero;
    }

    // ══════════════ перемещение и ресайз ══════════════

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left) return;
        if (Model.Locked && !_editMode) return;
        e.Handled = true;
        try { DragMove(); } // нативный цикл перемещения: WM_MOVING → прилипание
        catch (InvalidOperationException) { /* кнопку уже отпустили */ }
        SyncGeometry();
        RestoreFocusAfterSizeMove();
    }

    private Grid BuildGrips()
    {
        var grid = new Grid();
        void Add(HorizontalAlignment h, VerticalAlignment v, double w, double hgt, Cursor cursor, int direction)
        {
            var grip = new Border
            {
                Background = Brushes.Transparent, // прозрачный, но кликабельный
                HorizontalAlignment = h, VerticalAlignment = v,
                Width = w, Height = hgt, Cursor = cursor
            };
            grip.MouseLeftButtonDown += (_, e) => { e.Handled = true; BeginResize(direction); };
            grid.Children.Add(grip);
        }

        double nan = double.NaN, corner = GripSize * 2;
        Add(HorizontalAlignment.Left, VerticalAlignment.Stretch, GripSize, nan, Cursors.SizeWE, 1);
        Add(HorizontalAlignment.Right, VerticalAlignment.Stretch, GripSize, nan, Cursors.SizeWE, 2);
        Add(HorizontalAlignment.Stretch, VerticalAlignment.Top, nan, GripSize, Cursors.SizeNS, 3);
        Add(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, nan, GripSize, Cursors.SizeNS, 6);
        Add(HorizontalAlignment.Left, VerticalAlignment.Top, corner, corner, Cursors.SizeNWSE, 4);
        Add(HorizontalAlignment.Right, VerticalAlignment.Top, corner, corner, Cursors.SizeNESW, 5);
        Add(HorizontalAlignment.Left, VerticalAlignment.Bottom, corner, corner, Cursors.SizeNESW, 7);
        Add(HorizontalAlignment.Right, VerticalAlignment.Bottom, corner, corner, Cursors.SizeNWSE, 8);
        return grid;
    }

    private void UpdateGrips() =>
        _grips.Visibility = Model.Locked && !_editMode ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Нативный цикл ресайза: плавно, с курсорами системы, WM_SIZING даёт пропорции и прилипание.</summary>
    private void BeginResize(int direction)
    {
        ReleaseCapture();
        SendMessage(_hwnd, WM_SYSCOMMAND, (IntPtr)(SC_SIZE + direction), IntPtr.Zero);
        SyncGeometry();
        RestoreFocusAfterSizeMove();
    }

    /// <summary>
    /// Системный цикл перемещения/ресайза активирует окно даже с WS_EX_NOACTIVATE.
    /// Виджету без ввода фокус не нужен — возвращаем его окну, где пользователь работал.
    /// </summary>
    private void RestoreFocusAfterSizeMove()
    {
        bool noActivate = (WinApi.GetWindowLong(_hwnd, WinApi.GWL_EXSTYLE) & WinApi.WS_EX_NOACTIVATE) != 0;
        if (noActivate && IsActive && _previousForeground != IntPtr.Zero)
            WinApi.SetForegroundWindow(_previousForeground);
    }

    private void SnapMove(ref RECT rect)
    {
        if (SnapDisabled) return;
        var t = SnapTargets(rect);
        int threshold = (int)(SnapDistance * DpiUtil.Scale);
        int dx = Closest(Delta(rect.Left, t.Left, threshold), Delta(rect.Right, t.Right, threshold));
        int dy = Closest(Delta(rect.Top, t.Top, threshold), Delta(rect.Bottom, t.Bottom, threshold));
        rect.Left += dx; rect.Right += dx;
        rect.Top += dy; rect.Bottom += dy;
    }

    private void AdjustSizing(ref RECT rect, int edge)
    {
        bool left = edge is 1 or 4 or 7, right = edge is 2 or 5 or 8, top = edge is 3 or 4 or 5, bottom = edge is 6 or 7 or 8;
        if (!SnapDisabled)
        {
            var t = SnapTargets(rect);
            int threshold = (int)(SnapDistance * DpiUtil.Scale);
            if (left) rect.Left += Delta(rect.Left, t.Left, threshold) ?? 0;
            if (right) rect.Right += Delta(rect.Right, t.Right, threshold) ?? 0;
            if (top) rect.Top += Delta(rect.Top, t.Top, threshold) ?? 0;
            if (bottom) rect.Bottom += Delta(rect.Bottom, t.Bottom, threshold) ?? 0;
        }

        int minW = (int)(MinWidth * DpiUtil.Scale), minH = (int)(MinHeight * DpiUtil.Scale);
        if (_content?.AspectRatio is double aspect && aspect > 0)
        {
            // пропорции картинки: ведущая сторона — та, которую тянут
            int w = Math.Max(minW, rect.Right - rect.Left), h = Math.Max(minH, rect.Bottom - rect.Top);
            if (left || right) h = (int)Math.Round(w / aspect);
            else w = (int)Math.Round(h * aspect);
            if (left) rect.Left = rect.Right - w; else rect.Right = rect.Left + w;
            if (top) rect.Top = rect.Bottom - h; else rect.Bottom = rect.Top + h;
        }
        if (rect.Right - rect.Left < minW) { if (left) rect.Left = rect.Right - minW; else rect.Right = rect.Left + minW; }
        if (rect.Bottom - rect.Top < minH) { if (top) rect.Top = rect.Bottom - minH; else rect.Bottom = rect.Top + minH; }
    }

    /// <summary>Shift при перетаскивании/ресайзе — свободное движение без прилипания.</summary>
    private static bool SnapDisabled => (WinApi.GetAsyncKeyState(0x10) & 0x8000) != 0;

    private sealed record Targets(List<int> Left, List<int> Right, List<int> Top, List<int> Bottom);

    /// <summary>
    /// Цели для КАЖДОГО края отдельно: левый край — к левому краю экрана, вплотную справа
    /// от соседа (с зазором) или вровень с левым краем виджета сверху/снизу. Раньше оба края
    /// сверялись со всеми целями сразу — рядом оказывались цели в 8 px друг от друга, и окно
    /// металось между ними.
    /// </summary>
    private Targets SnapTargets(RECT rect)
    {
        int gap = (int)Math.Round(SnapGap * DpiUtil.Scale);
        int near = gap * 3; // «стоят рядом» — для выравнивания краёв
        var area = System.Windows.Forms.Screen.FromRectangle(
            System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)).WorkingArea;
        var t = new Targets([area.Left], [area.Right], [area.Top], [area.Bottom]);

        foreach (var other in _module.OtherWidgetRects(this))
        {
            if (!GetWindowRect(other, out var r)) continue;
            bool overlapV = r.Top < rect.Bottom && rect.Top < r.Bottom;   // соседи по горизонтали
            bool overlapH = r.Left < rect.Right && rect.Left < r.Right;   // соседи по вертикали
            int distV = Math.Max(r.Top - rect.Bottom, rect.Top - r.Bottom); // зазор по вертикали
            int distH = Math.Max(r.Left - rect.Right, rect.Left - r.Right);

            if (overlapV) { t.Left.Add(r.Right + gap); t.Right.Add(r.Left - gap); }
            if (overlapH) { t.Top.Add(r.Bottom + gap); t.Bottom.Add(r.Top - gap); }
            if (overlapH || distV <= near) { t.Left.Add(r.Left); t.Right.Add(r.Right); }
            if (overlapV || distH <= near) { t.Top.Add(r.Top); t.Bottom.Add(r.Bottom); }
        }
        return t;
    }

    private static int? Delta(int edge, List<int> targets, int threshold)
    {
        int? best = null;
        foreach (var target in targets)
        {
            int d = target - edge;
            if (Math.Abs(d) <= threshold && (best == null || Math.Abs(d) < Math.Abs(best.Value))) best = d;
        }
        return best;
    }

    private static int Closest(int? a, int? b) =>
        a == null ? b ?? 0 : b == null ? a.Value : Math.Abs(a.Value) <= Math.Abs(b.Value) ? a.Value : b.Value;

    private void SyncGeometry()
    {
        if (Math.Abs(Model.X - Left) < 0.1 && Math.Abs(Model.Y - Top) < 0.1 &&
            Math.Abs(Model.Width - ActualWidth) < 0.1 && Math.Abs(Model.Height - ActualHeight) < 0.1) return;
        Model.X = Left;
        Model.Y = Top;
        Model.Width = ActualWidth;
        Model.Height = ActualHeight;
        ModelChanged();
    }

    /// <summary>Масштаб относительно точки под курсором (колесо на картинке).</summary>
    internal void ZoomAroundCursor(double factor)
    {
        WinApi.GetCursorPos(out var p);
        double cx = p.X / DpiUtil.Scale, cy = p.Y / DpiUtil.Scale;
        double fx = (cx - Left) / ActualWidth, fy = (cy - Top) / ActualHeight;
        double w = Math.Max(MinWidth, ActualWidth * factor);
        double h = _content?.AspectRatio is double a && a > 0 ? w / a : Math.Max(MinHeight, ActualHeight * factor);
        if (h < MinHeight) return;
        Left = cx - fx * w;
        Top = cy - fy * h;
        Width = w;
        Height = h;
        SyncGeometry();
    }

    internal void ResizeTo(double width, double height)
    {
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        UpdateLayout();
        SyncGeometry();
    }

    internal void SetOpacity(double opacity)
    {
        Model.Opacity = Math.Clamp(Math.Round(opacity, 2), 0.2, 1.0);
        BeginAnimation(OpacityProperty, null);
        Opacity = Model.Opacity;
        ModelChanged();
    }

    /// <summary>Вернуть видимость, если монитор, где был виджет, отключён.</summary>
    private void EnsureOnScreen()
    {
        double s = DpiUtil.Scale;
        var rect = System.Drawing.Rectangle.FromLTRB((int)(Left * s), (int)(Top * s), (int)((Left + Width) * s), (int)((Top + Height) * s));
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var inter = System.Drawing.Rectangle.Intersect(screen.WorkingArea, rect);
            if (inter.Width >= 40 && inter.Height >= 30) return;
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
        Model.X = Left;
        Model.Y = Top;
    }

    // ══════════════ состояние, вызываемое содержимым и модулем ══════════════

    internal void ModelChanged() => _module.ScheduleSave();

    internal void SetEditMode(bool enabled)
    {
        _editMode = enabled;
        _editOverlay.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateGrips();
        if (_hwnd != IntPtr.Zero) ApplyExStyle();
    }

    /// <summary>Esc в заметке: вернуть фокус окну, из которого пришли.</summary>
    internal void ReleaseFocus()
    {
        Keyboard.ClearFocus();
        if (_previousForeground != IntPtr.Zero) WinApi.SetForegroundWindow(_previousForeground);
    }

    /// <summary>Подсветить виджет (кнопка «найти» в настройках).</summary>
    internal void Flash()
    {
        // на время подсветки — поверх всего, какой бы ни был слой (иначе виджет «под окнами» не найти)
        _flashing = true;
        Topmost = true;
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _editOverlay.Visibility = Visibility.Visible;
        var pulse = new DoubleAnimation(1, 0.15, TimeSpan.FromMilliseconds(260)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(4) };
        pulse.Completed += (_, _) =>
        {
            _editOverlay.BeginAnimation(OpacityProperty, null);
            if (!_editMode) _editOverlay.Visibility = Visibility.Collapsed;
            _flashing = false;
            if (Model.Layer != WidgetLayer.Topmost && _hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            ApplyLayer();
        };
        _editOverlay.BeginAnimation(OpacityProperty, pulse);
    }

    // ══════════════ панель на ховере ══════════════

    internal void SetToolbarTone(bool light)
    {
        _lightTone = light;
        BuildToolbar();
    }

    private void BuildToolbar()
    {
        _toolbar.Children.Clear();
        if (Model.Kind == WidgetKind.Note)
            _toolbar.Children.Add(ToolButton(FluentIcons.Add, "Новая заметка", () => _module.CreateNote(this)));
        if (Model.Kind == WidgetKind.Custom)
            _toolbar.Children.Add(ToolButton(FluentIcons.Edit, "Редактировать", () => _module.Edit(Model)));
        _lockButtonGlyph.Text = Model.Locked ? FluentIcons.Lock : FluentIcons.Unlock;
        _toolbar.Children.Add(ToolButton(_lockButtonGlyph, Model.Locked ? "Открепить" : "Закрепить на месте", ToggleLock));
        _toolbar.Children.Add(ToolButton(FluentIcons.More, "Меню", ShowMenu));
        _toolbar.Children.Add(ToolButton(FluentIcons.Close, "Скрыть (вернуть — в настройках)", () => _module.Hide(Model)));
    }

    private Border ToolButton(string glyph, string tip, Action onClick) => ToolButton(FluentIcons.Glyph(glyph, 11), tip, onClick);

    private Border ToolButton(TextBlock glyph, string tip, Action onClick)
    {
        if (glyph.Parent is Border oldHost) oldHost.Child = null; // глиф замка переиспользуется между перестройками
        var idle = _lightTone ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x55, 0x10, 0x12, 0x18);
        var hover = _lightTone ? Color.FromArgb(0x45, 0, 0, 0) : Color.FromArgb(0xCC, 0x24, 0x28, 0x34);
        glyph.Foreground = _lightTone ? new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x28)) : JarvisTheme.ForegroundBrush(0.85);
        var brush = new SolidColorBrush(idle);
        var button = new Border
        {
            Width = 22, Height = 22, Margin = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(6), Background = brush, Child = glyph,
            Cursor = Cursors.Hand, ToolTip = tip
        };
        button.MouseEnter += (_, _) => brush.Color = hover;
        button.MouseLeave += (_, _) => brush.Color = idle;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true; // не начинать перетаскивание
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    private void FadeToolbar(bool show)
    {
        _toolbar.IsHitTestVisible = show;
        _toolbar.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 200)));
    }

    private void ToggleLock()
    {
        Model.Locked = !Model.Locked;
        UpdateGrips();
        BuildToolbar();
        ModelChanged();
        Services.Overlay.ShowToast(Model.Locked ? "Виджет закреплён на месте" : "Виджет можно двигать", Model.Name, 1100);
    }

    // ══════════════ контекстное меню ══════════════

    private void ShowMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        _content?.BuildMenu(menu);
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        if (Model.Kind == WidgetKind.Custom)
            menu.Items.Add(WidgetUi.Item("Редактировать…", FluentIcons.Edit, () => _module.Edit(Model)));

        var layer = new MenuItem { Header = "Слой", Icon = FluentIcons.Glyph(FluentIcons.Layers, 14) };
        foreach (var (value, title) in new[] { (WidgetLayer.Topmost, "Поверх всех окон"), (WidgetLayer.Normal, "Как обычное окно"), (WidgetLayer.Desktop, "Под всеми окнами") })
        {
            var v = value;
            layer.Items.Add(WidgetUi.Check(title, Model.Layer == v, _ => { Model.Layer = v; ApplyLayer(); ModelChanged(); }));
        }
        menu.Items.Add(layer);

        var opacity = new MenuItem { Header = "Непрозрачность", Icon = FluentIcons.Glyph(FluentIcons.View, 14) };
        foreach (var value in new[] { 1.0, 0.9, 0.75, 0.6, 0.4 })
        {
            var v = value;
            opacity.Items.Add(WidgetUi.Check($"{v:P0}", Math.Abs(Model.Opacity - v) < 0.01, _ => SetOpacity(v)));
        }
        menu.Items.Add(opacity);

        menu.Items.Add(WidgetUi.Check("Закрепить на месте", Model.Locked, _ => ToggleLock()));
        var through = WidgetUi.Check("Сквозные клики", Model.ClickThrough, on =>
        {
            Model.ClickThrough = on;
            ApplyExStyle();
            ModelChanged();
            if (on) Services.Overlay.ShowToast("Клики проходят сквозь виджет. Вернуть — режим редактирования или настройки.", Model.Name, 2600);
        });
        through.ToolTip = "Виджет не перехватывает мышь (например, полупрозрачные часы поверх игр)";
        menu.Items.Add(through);

        menu.Items.Add(new Separator());
        menu.Items.Add(WidgetUi.Item("Дублировать", FluentIcons.Duplicate, () => _module.Duplicate(Model)));
        menu.Items.Add(WidgetUi.Item("Скрыть", FluentIcons.Hide, () => _module.Hide(Model)));

        // удаление в два клика — без модальных диалогов
        var delete = WidgetUi.Item("Удалить", FluentIcons.Delete, () => { });
        delete.StaysOpenOnClick = true;
        bool armed = false;
        delete.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                delete.Header = "Нажмите ещё раз, чтобы удалить";
                delete.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
                return;
            }
            menu.IsOpen = false;
            _module.Delete(Model);
        };
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _content?.Dispose();
        _content = null;
        base.OnClosed(e);
    }

    // ══════════════ P/Invoke ══════════════

    private const int WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGING = 0x0046, WM_SYSCOMMAND = 0x0112,
        WM_SIZING = 0x0214, WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232;
    private const int SC_SIZE = 0xF000;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_BOTTOM = new(1), HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
