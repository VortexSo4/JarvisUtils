using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JarvisUtils.Core;
using JarvisUtils.Modules.ScreenshotOcr;

namespace JarvisUtils.Modules.Widgets;

/// <summary>
/// Виджеты на рабочем столе: заметки, закреплённые скриншоты и «свои» (XAML + C#).
/// Модуль владеет списком виджетов и их окнами; всё сохранение — отложенное и в фоне.
/// </summary>
public sealed class WidgetsModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor, IQuickActions
{
    private const int SaveDelayMs = 500;
    private static readonly object FileGate = new();

    private WidgetsSettings _settings = null!;
    private readonly Dictionary<string, WidgetWindow> _windows = new();
    private DispatcherTimer? _saveTimer;
    private bool _editMode;
    private int _cascade;

    /// <summary>Список виджетов или их состояние изменились (страница настроек перерисовывается).</summary>
    public event Action? Changed;

    public override string DisplayName => "Виджеты";
    public override string Icon => FluentIcons.Widgets;

    public IReadOnlyList<QuickAction> QuickActions =>
    [
        new("Заметка", FluentIcons.Note, () => CreateNote(null)),
        new("Закрепить скриншот", FluentIcons.Screenshot, () => PinScreenshot()),
        new("Показать / скрыть виджеты", FluentIcons.Widgets, ToggleAll)
    ];

    internal IReadOnlyList<WidgetModel> Widgets => _settings.Widgets;
    internal bool AllHidden => _settings.AllHidden;
    internal bool EditMode => _editMode;

    public override void Initialize(IJarvisServices services)
    {
        base.Initialize(services);
        Services.TrayMenu.AddItem("Новая заметка", () => CreateNote(null));
        Services.TrayMenu.AddItem("Закрепить скриншот", () => PinScreenshot());
        Services.TrayMenu.AddItem("Показать / скрыть виджеты", ToggleAll);
    }

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<WidgetsSettings>();
        RegisterHotkey(_settings.HotkeyEditMode, ToggleEditMode);
        RegisterHotkey(_settings.HotkeyNewNote, () => CreateNote(null));
        RegisterHotkey(_settings.HotkeyPinScreenshot, () => PinScreenshot());
        RegisterHotkey(_settings.HotkeyToggleAll, ToggleAll);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Новая заметка", () => _settings.HotkeyNewNote, g => SetHotkey(s => s.HotkeyNewNote = g)),
        new("Закрепить скриншот области", () => _settings.HotkeyPinScreenshot, g => SetHotkey(s => s.HotkeyPinScreenshot = g)),
        new("Показать / скрыть все виджеты", () => _settings.HotkeyToggleAll, g => SetHotkey(s => s.HotkeyToggleAll = g)),
        new("Режим редактирования (рамки, сквозные клики выключены)", () => _settings.HotkeyEditMode, g => SetHotkey(s => s.HotkeyEditMode = g)),
    ];

    private void SetHotkey(Action<WidgetsSettings> apply)
    {
        apply(_settings);
        FlushSave();
        RestartHotkeys();
    }

    protected override void OnEnabled()
    {
        _settings ??= LoadSettings<WidgetsSettings>();
        if (_settings.AllHidden) return;
        foreach (var model in _settings.Widgets.Where(w => w.Visible))
            ShowWindow(model);
    }

    protected override void OnDisabled()
    {
        FlushSave();
        foreach (var win in _windows.Values.ToList()) win.Close();
        _windows.Clear();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        FlushSave(sync: true);
        foreach (var win in _windows.Values.ToList()) win.Close();
    }

    // ══════════════ создание ══════════════

    /// <summary>Новая заметка: рядом с заметкой-«родителем» или у курсора; сразу в режиме ввода.</summary>
    public void CreateNote(WidgetWindow? near)
    {
        if (!IsEnabled) return;
        var model = new WidgetModel
        {
            Kind = WidgetKind.Note,
            Name = "Заметка",
            Width = 240,
            Height = 190,
            Layer = WidgetLayer.Topmost,
            Color = near?.Model.Color ?? NotePalette.Default.Hex
        };
        if (near != null)
        {
            model.X = near.Left + near.ActualWidth + 10;
            model.Y = near.Top;
        }
        else PlaceAtCursor(model);

        var win = AddWidget(model);
        win?.Dispatcher.BeginInvoke(win.FocusNote, DispatcherPriority.Loaded);
    }

    /// <summary>Выделить область экрана и закрепить её поверх окон — ровно на том же месте.</summary>
    public void PinScreenshot(Action? done = null)
    {
        if (!IsEnabled) { done?.Invoke(); return; }
        ScreenCapture.SelectRegion(Services.Hotkeys, (_, crop, rect) =>
        {
            AddImage(crop, rect.X / DpiUtil.Scale, rect.Y / DpiUtil.Scale, $"Скриншот {DateTime.Now:HH:mm}");
            done?.Invoke();
        }, done);
    }

    public void PinClipboardImage()
    {
        var image = Services.Clipboard.GetImage();
        if (image == null)
        {
            Services.Overlay.ShowToast("В буфере обмена нет картинки", "Виджеты");
            return;
        }
        var model = new WidgetModel();
        PlaceAtCursor(model);
        AddImage(image, model.X, model.Y, $"Картинка {DateTime.Now:HH:mm}");
    }

    private void AddImage(BitmapSource image, double x, double y, string name)
    {
        if (image.CanFreeze && !image.IsFrozen) image.Freeze();
        var model = new WidgetModel
        {
            Kind = WidgetKind.Image,
            Name = name,
            X = x,
            Y = y,
            Width = Math.Max(48, image.PixelWidth / DpiUtil.Scale),  // 1:1 с экраном — без размытия
            Height = Math.Max(32, image.PixelHeight / DpiUtil.Scale),
            Layer = WidgetLayer.Topmost
        };
        model.ImageFile = model.Id + ".png";

        // PNG пишется в фоне; окно показывает картинку из памяти сразу
        var path = Path.Combine(WidgetPaths.ImagesDir, model.ImageFile);
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(WidgetPaths.ImagesDir);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var fs = File.Create(path);
                encoder.Save(fs);
            }
            catch (Exception ex) { Log.Error($"виджеты: сохранение картинки: {ex.Message}"); }
        });

        AddWidget(model, image);
    }

    public void CreateFromTemplate(WidgetTemplate template, bool openEditor = false)
    {
        if (!IsEnabled) return;
        var wa = SystemParameters.WorkArea;
        int step = _cascade++ % 6 * 28;
        var model = new WidgetModel
        {
            Kind = WidgetKind.Custom,
            Name = template.Name,
            Width = template.Width,
            Height = template.Height,
            Layer = template.Layer,
            Xaml = template.Xaml,
            Script = template.Script,
            X = wa.Left + (wa.Width - template.Width) / 2 + step,
            Y = wa.Top + wa.Height / 4 + step
        };
        AddWidget(model);
        if (openEditor) Edit(model);
    }

    private WidgetWindow? AddWidget(WidgetModel model, BitmapSource? preloaded = null)
    {
        _settings.Widgets.Add(model);
        if (_settings.AllHidden) ShowAllInternal(true);
        var win = ShowWindow(model, preloaded, animate: true);
        ScheduleSave();
        Changed?.Invoke();
        return win;
    }

    private void PlaceAtCursor(WidgetModel model)
    {
        WinApi.GetCursorPos(out var p);
        model.X = p.X / DpiUtil.Scale - 20;
        model.Y = p.Y / DpiUtil.Scale - 12;
    }

    // ══════════════ окна ══════════════

    private WidgetWindow? ShowWindow(WidgetModel model, BitmapSource? preloaded = null, bool animate = false)
    {
        if (_windows.TryGetValue(model.Id, out var existing)) return existing;
        try
        {
            var win = new WidgetWindow(model, this, preloaded, animate);
            _windows[model.Id] = win;
            win.Closed += (_, _) =>
            {
                if (_windows.TryGetValue(model.Id, out var w) && ReferenceEquals(w, win)) _windows.Remove(model.Id);
            };
            win.Show();
            if (_editMode) win.SetEditMode(true);
            return win;
        }
        catch (Exception ex)
        {
            Log.Error($"виджет «{model.Name}» не открылся: {ex}");
            return null;
        }
    }

    private void CloseWindow(WidgetModel model)
    {
        if (_windows.Remove(model.Id, out var win)) win.Close();
    }

    internal IEnumerable<IntPtr> OtherWidgetRects(WidgetWindow except) =>
        _windows.Values.Where(w => !ReferenceEquals(w, except) && w.IsVisible && w.Handle != IntPtr.Zero).Select(w => w.Handle);

    // ══════════════ управление ══════════════

    public void SetVisible(WidgetModel model, bool visible)
    {
        model.Visible = visible;
        if (visible)
        {
            if (_settings.AllHidden) ShowAllInternal(true);
            else ShowWindow(model, animate: true);
        }
        else CloseWindow(model);
        ScheduleSave();
        Changed?.Invoke();
    }

    public void Hide(WidgetModel model) => SetVisible(model, false);

    public void Delete(WidgetModel model)
    {
        _settings.Widgets.Remove(model);
        CloseWindow(model);
        WidgetEditorWindow.CloseFor(model);
        if (model.ImageFile is { } file && _settings.Widgets.All(w => w.ImageFile != file))
        {
            var path = Path.Combine(WidgetPaths.ImagesDir, file);
            Task.Run(() => { try { File.Delete(path); } catch { /* файл занят — не критично */ } });
        }
        ScheduleSave();
        Changed?.Invoke();
    }

    public void Duplicate(WidgetModel model)
    {
        var copy = model.Clone();
        copy.Name = model.Name;
        copy.X += 24;
        copy.Y += 24;
        copy.Visible = true;
        AddWidget(copy);
    }

    public void Rename(WidgetModel model, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == model.Name) return;
        model.Name = name.Trim();
        if (_windows.TryGetValue(model.Id, out var win)) win.ReloadContent();
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>«Редактировать»: свой виджет — редактор, заметка — фокус в текст, картинка — подсветка.</summary>
    public void Edit(WidgetModel model)
    {
        switch (model.Kind)
        {
            case WidgetKind.Custom:
                WidgetEditorWindow.Open(model, this);
                break;
            case WidgetKind.Note:
                if (!model.Visible || _settings.AllHidden) SetVisible(model, true);
                if (_windows.TryGetValue(model.Id, out var note)) note.FocusNote();
                break;
            default:
                Locate(model);
                break;
        }
    }

    public void Locate(WidgetModel model)
    {
        if (!model.Visible || _settings.AllHidden) SetVisible(model, true);
        if (_windows.TryGetValue(model.Id, out var win)) win.Flash();
    }

    /// <summary>Редактор сохранил изменения — перезагрузить окно.</summary>
    internal void ApplyEdit(WidgetModel model)
    {
        if (_windows.TryGetValue(model.Id, out var win)) win.ReloadContent();
        ScheduleSave();
        Changed?.Invoke();
    }

    public void ToggleEditMode()
    {
        _editMode = !_editMode;
        foreach (var win in _windows.Values) win.SetEditMode(_editMode);
        Services.Overlay.ShowToast(_editMode
            ? "Режим редактирования: рамки видны, сквозные клики и блокировка временно отключены"
            : "Режим редактирования выключен", "Виджеты");
        Changed?.Invoke();
    }

    public void ToggleAll()
    {
        ShowAllInternal(_settings.AllHidden);
        Services.Overlay.ShowToast(_settings.AllHidden ? "Виджеты скрыты" : "Виджеты показаны", "Виджеты", 1100);
    }

    private void ShowAllInternal(bool show)
    {
        _settings.AllHidden = !show;
        if (show)
            foreach (var model in _settings.Widgets.Where(w => w.Visible)) ShowWindow(model, animate: true);
        else
            foreach (var win in _windows.Values.ToList()) win.Close();
        ScheduleSave();
        Changed?.Invoke();
    }

    // ══════════════ сохранение ══════════════

    /// <summary>
    /// Отложенное сохранение: перетаскивание, набор текста и т.п. дёргают его постоянно,
    /// а на диск уходит один снимок через полсекунды тишины.
    /// </summary>
    internal void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDelayMs) };
            _saveTimer.Tick += (_, _) => FlushSave();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave(bool sync = false)
    {
        _saveTimer?.Stop();
        if (_settings == null) return;
        // сериализация — на UI-потоке (модели меняются только здесь), запись — в фоне
        string json;
        try { json = JsonSerializer.Serialize(_settings, SettingsStore.JsonOptions); }
        catch (Exception ex) { Log.Error($"виджеты: сериализация: {ex.Message}"); return; }

        var path = Services.Settings.GetFilePath(Name);
        void Write()
        {
            lock (FileGate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path + ".tmp", json);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
                catch (Exception ex) { Log.Error($"виджеты: запись настроек: {ex.Message}"); }
            }
        }
        if (sync) Write(); else Task.Run(Write);
    }

    // ══════════════ настройки ══════════════

    public FrameworkElement BuildEditor() => new WidgetsSettingsPage(this).Build();
}
