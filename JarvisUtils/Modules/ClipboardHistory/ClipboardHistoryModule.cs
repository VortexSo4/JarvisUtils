using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisUtils.Core;
using JarvisUtils.UI.Settings;
using JarvisUtils.UI.Theme;

namespace JarvisUtils.Modules.ClipboardHistory;

public sealed class ClipboardHistorySettings
{
    // Ctrl+Alt+H — «History»: свободно у остальных модулей, не AltGr-символ в распространённых
    // раскладках и не перехватывает Ctrl+Shift+V (в браузерах/мессенджерах это «вставить без форматирования»).
    public string Hotkey { get; set; } = "Ctrl+Alt+H";
    // Два хоткея бывшего модуля «Избранное буфера обмена» (при первом запуске переносятся из его настроек)
    public string HotkeyFavorites { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyAdd { get; set; } = "Ctrl+Alt+B";
    public int MaxItems { get; set; } = 200; // только обычные записи — избранное в лимит не входит
    public bool CaptureImages { get; set; } = true;
    public bool LegacyHotkeysImported { get; set; }
    public bool LegacyFavoritesImported { get; set; }
}

/// <summary>
/// Буфер обмена: история и избранное в одном модуле. Слушает изменения буфера, хранит текст и
/// картинки; запись со звездой — избранное (не вытесняется лимитом, переживает очистку истории).
/// По хоткею открывается окно поиска — сразу во всей истории или только в избранном — и вставляет
/// выбранное в окно, которое было активно до этого. Содержимое с «приватными» метками (менеджеры
/// паролей, наши временные вставки) не записывается.
/// </summary>
public sealed class ClipboardHistoryModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor, IQuickActions
{
    private const int MaxTextChars = 1_000_000; // больше — скорее дамп/лог, чем то, что захочется вставить снова
    private const int MinLimit = 10, MaxLimit = 5000;
    private const int FocusSettleMs = 60;       // пауза после возврата фокуса: целевое окно успевает его принять
    private const int RestoreDelayMs = 500;     // картинки приложения забирают из буфера заметно дольше текста

    private ClipboardHistorySettings _settings = null!;
    private ClipboardHistoryStore? _store;
    private ClipboardMonitor? _monitor;
    private ClipboardHistoryWindow? _window;
    private Dispatcher _ui = null!;
    private IntPtr _target; // окно, активное в момент хоткея, — туда и вставляем

    public override string DisplayName => "Буфер обмена";
    public override string Icon => FluentIcons.ClipboardList;

    private ClipboardHistoryStore Store => _store ??= new ClipboardHistoryStore(_settings.MaxItems);

    /// <summary>Откуда импортировать старое избранное (в тестах подменяется).</summary>
    internal string LegacyIndexFile { get; set; } = LegacyFavorites.DefaultIndexFile;

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<ClipboardHistorySettings>();
        if (!_settings.LegacyHotkeysImported) ImportLegacyHotkeys();
        RegisterHotkey(_settings.Hotkey, () => Open(favoritesOnly: false));
        RegisterHotkey(_settings.HotkeyFavorites, () => Open(favoritesOnly: true));
        RegisterHotkey(_settings.HotkeyAdd, AddCurrentToFavorites);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Открыть историю буфера", () => _settings.Hotkey,
            g => { _settings.Hotkey = g; SaveSettings(_settings); RestartHotkeys(); }),
        new("Открыть избранное", () => _settings.HotkeyFavorites,
            g => { _settings.HotkeyFavorites = g; SaveSettings(_settings); RestartHotkeys(); }),
        new("Добавить текущий буфер в избранное", () => _settings.HotkeyAdd,
            g => { _settings.HotkeyAdd = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    /// <summary>Для радиального меню: оно не забирает фокус, поэтому окно запомнит ту же цель вставки, что и хоткей.</summary>
    public IReadOnlyList<QuickAction> QuickActions =>
    [
        new("История буфера", FluentIcons.ClipboardList, () => Open(favoritesOnly: false)),
        new("Избранное", FluentIcons.StarFill, () => Open(favoritesOnly: true))
    ];

    protected override void OnEnabled()
    {
        _ui = Dispatcher.CurrentDispatcher;
        // историю с диска не читаем: загрузится при первом копировании или открытии окна
        // (или сразу — если нужно один раз перенести старое избранное)
        var store = Store;
        if (!_settings.LegacyFavoritesImported) ImportLegacyFavorites();
        _monitor ??= new ClipboardMonitor(() => _settings.CaptureImages, MaxTextChars, store.Enqueue);
        _monitor.Start();
    }

    protected override void OnDisabled()
    {
        _monitor?.Stop(); // выключенный модуль ничего не записывает
        _window?.Close();
    }

    public override void Shutdown()
    {
        _monitor?.Stop();
        _window?.Close();
        _store?.Flush();
        base.Shutdown();
    }

    // ── перенос бывшего модуля «Избранное буфера обмена» ──

    /// <summary>Хоткеи старого модуля — один раз, до регистрации (файл маленький, читаем сразу).</summary>
    private void ImportLegacyHotkeys()
    {
        var legacy = Services.Settings.Load<LegacyFavorites.Settings>(LegacyFavorites.SettingsName);
        if (legacy.HotkeyShow != null) _settings.HotkeyFavorites = legacy.HotkeyShow;
        if (legacy.HotkeyAdd != null) _settings.HotkeyAdd = legacy.HotkeyAdd;
        _settings.LegacyHotkeysImported = true;
        SaveSettings(_settings);
    }

    /// <summary>
    /// Записи старого избранного → записи со звездой, в исходном порядке. Флаг ставится только
    /// после успешного импорта; повтор безопасен — хранилище не дублирует совпадающее содержимое.
    /// </summary>
    private void ImportLegacyFavorites()
    {
        var path = LegacyIndexFile;
        var store = Store;
        Task.Run(() => LegacyFavorites.Read(path))
            .ContinueWith(t => t.Result is { Count: > 0 } items ? store.ImportFavoritesAsync(items) : Task.FromResult(t.Result == null ? -1 : 0),
                TaskScheduler.Default)
            .Unwrap()
            .ContinueWith(t => _ui.BeginInvoke(() =>
            {
                if (!t.IsCompletedSuccessfully || t.Result < 0) return; // файл не прочитался — попробуем в следующий раз
                _settings.LegacyFavoritesImported = true;
                SaveSettings(_settings);
                if (t.Result > 0) Log.Info($"буфер обмена: перенесено избранное старого модуля — {t.Result}");
            }), TaskScheduler.Default);
    }

    // ── окно ──

    /// <summary>
    /// Открыть окно в нужном режиме. Повторный хоткей того же режима закрывает окно,
    /// другого — переключает режим в уже открытом.
    /// </summary>
    private void Open(bool favoritesOnly)
    {
        if (!IsEnabled) return;
        if (_window != null)
        {
            if (_window.FavoritesOnly == favoritesOnly) _window.CloseAnimated();
            else _window.SetFavoritesOnly(favoritesOnly);
            return;
        }
        _target = WinApi.GetForegroundWindow();
        var window = new ClipboardHistoryWindow(Store, Paste, favoritesOnly);
        _window = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
        window.Show();
    }

    /// <summary>Хоткей «текущий буфер → избранное»: чтение — на clipboard-потоке, итог — тостом.</summary>
    private void AddCurrentToFavorites()
    {
        var store = Store;
        StaClipboard.Post(() =>
        {
            ClipboardCapture? capture = null;
            try { capture = HistoryClipboard.Read(includeImages: true, MaxTextChars); }
            catch (Exception ex) { Log.Error($"буфер обмена: чтение для избранного: {ex.Message}"); }
            if (capture == null)
            {
                _ui.BeginInvoke(() => Services.Overlay.ShowToast("Буфер пуст или его содержимое помечено как приватное", DisplayName));
                return;
            }
            var what = capture.Text != null ? $"«{TextUtil.Preview(capture.Text.Length > 200 ? capture.Text[..200] : capture.Text, 50)}»" : "Картинка";
            store.EnqueueFavoriteAsync(capture).ContinueWith(t => _ui.BeginInvoke(() => Services.Overlay.ShowToast(t.Result switch
            {
                AddResult.AlreadyFavorite => $"{what} уже в избранном",
                AddResult.Failed => "Не удалось добавить в избранное",
                _ => $"{what} — в избранном"
            }, DisplayName, 1500)), TaskScheduler.Default);
        });
    }

    // ── вставка ──

    private void Paste(HistoryEntry entry, bool plain)
    {
        // окно уже закрыто, наш процесс ещё на переднем плане — ему SetForegroundWindow разрешён
        var target = _target;
        bool focused = target != IntPtr.Zero && IsWindow(target) && FocusWindow(target);
        Store.Touch(entry.Id);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FocusSettleMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (focused) PasteInto(entry, plain);
            else CopyOnly(entry);
        };
        timer.Start();
    }

    private static bool FocusWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        return WinApi.SetForegroundWindow(hwnd);
    }

    private void PasteInto(HistoryEntry entry, bool plain)
    {
        if (!entry.IsImage && (plain || !entry.HasRich))
        {
            Services.Input.PasteText(entry.Text!); // общий путь: буфер с восстановлением
            return;
        }
        _ = PrepareFormatsAsync(entry, plain).ContinueWith(t =>
        {
            if (t.Result is { Count: > 0 } formats) StaClipboard.Post(() => PasteFormatsCore(formats));
            else _ui.BeginInvoke(() => Services.Overlay.ShowToast("Не удалось подготовить элемент к вставке", DisplayName));
        }, TaskScheduler.Default);
    }

    /// <summary>Вставить некуда (окно закрыли) — кладём элемент в буфер обычным образом.</summary>
    private void CopyOnly(HistoryEntry entry)
    {
        _ = PrepareFormatsAsync(entry, plain: false).ContinueWith(t =>
        {
            var formats = t.Result;
            StaClipboard.Post(() =>
            {
                bool ok = formats.Count > 0 && HistoryClipboard.SetPlain(formats);
                _ui.BeginInvoke(() => Services.Overlay.ShowToast(
                    ok ? "Окно для вставки не найдено — элемент скопирован в буфер" : "Не удалось скопировать элемент", DisplayName));
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Форматы для буфера: текст (+HTML/RTF) или картинка (PNG + CF_DIB). Диск и кодеки — на пуле.</summary>
    private Task<List<(uint Format, byte[] Data)>> PrepareFormatsAsync(HistoryEntry entry, bool plain) => Task.Run(() =>
    {
        var formats = new List<(uint, byte[])>();
        try
        {
            if (entry.IsImage)
            {
                var png = File.ReadAllBytes(Store.ImagePath(entry));
                var image = HistoryImages.Decode(png, null);
                formats.Add((HistoryClipboard.FmtPng, png));                        // браузеры/Office — с альфой
                formats.Add((HistoryClipboard.CF_DIB, HistoryImages.ToDib(image))); // Paint, мессенджеры и все остальные
            }
            else
            {
                formats.Add((HistoryClipboard.CF_UNICODETEXT, HistoryClipboard.UnicodeBytes(entry.Text!)));
                if (!plain)
                    foreach (var (name, data) in Store.LoadRich(entry))
                        formats.Add((HistoryClipboard.RegisterFormat(name), data));
            }
        }
        catch (Exception ex) { Log.Error($"история буфера: подготовка вставки: {ex.Message}"); formats.Clear(); }
        return formats;
    });

    /// <summary>
    /// Clipboard-поток: снимок буфера пользователя → наши данные (Transient) → Ctrl+V → через паузу
    /// возвращаем снимок, если после нас никто ничего не скопировал. Та же схема, что у InputSimulator.PasteText.
    /// </summary>
    private void PasteFormatsCore(List<(uint Format, byte[] Data)> formats)
    {
        // в буфере уже чья-то временная вставка (её restore ещё впереди) — снимать нечего
        var backup = HistoryClipboard.HoldsTransient() ? null : NativeClipboard.Capture();
        if (!HistoryClipboard.SetTransient(formats))
        {
            Log.Warn("история буфера: не удалось положить элемент в буфер — вставка отменена");
            if (backup != null) NativeClipboard.Restore(backup);
            return;
        }
        _ui.BeginInvoke(Services.Input.SendCtrlV, DispatcherPriority.Send);

        if (backup == null) return;
        _ = new DispatcherTimer(TimeSpan.FromMilliseconds(RestoreDelayMs), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            if (HistoryClipboard.HoldsTransient() && !NativeClipboard.Restore(backup))
                Log.Warn("история буфера: прежнее содержимое буфера не восстановлено");
        }, StaClipboard.Dispatcher);
    }

    // ── настройки ──

    public FrameworkElement BuildEditor()
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(new TextBlock
        {
            Text = "Запоминает всё, что вы копируете: текст (с форматированием) и картинки. Звезда (Ctrl+P в окне) " +
                   "переносит запись в избранное — оно не вытесняется лимитом и не стирается при очистке истории. " +
                   "Содержимое из менеджеров паролей и других приложений, помечающих его как приватное, не записывается.",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(SettingsUi.Section("ХРАНЕНИЕ"));

        var limitBox = new TextBox { Text = _settings.MaxItems.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        var limitNote = Note($"от {MinLimit} до {MaxLimit}; избранное в лимит не входит");
        limitBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        limitBox.TextChanged += (_, _) =>
        {
            bool ok = int.TryParse(limitBox.Text, out int n) && n is >= MinLimit and <= MaxLimit;
            limitNote.Foreground = ok ? JarvisTheme.ForegroundBrush(0.4) : JarvisTheme.Solid(Color.FromRgb(0xFF, 0x7A, 0x7A));
            if (!ok || n == _settings.MaxItems) return;
            _settings.MaxItems = n;
            SaveSettings(_settings);
            Store.SetLimit(n);
        };
        root.Children.Add(Row("Сколько записей истории хранить", limitBox, limitNote));

        var imagesToggle = new JarvisToggleSwitch { IsChecked = _settings.CaptureImages };
        imagesToggle.Checked += (_, _) => { _settings.CaptureImages = true; SaveSettings(_settings); };
        imagesToggle.Unchecked += (_, _) => { _settings.CaptureImages = false; SaveSettings(_settings); };
        root.Children.Add(Row("Запоминать картинки", imagesToggle, Note("PNG-файлами в папке истории")));

        root.Children.Add(SettingsUi.Section("ДАННЫЕ"));
        var stats = Note("…");
        stats.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(stats);
        void RefreshStats() => _ = Store.EnsureLoadedAsync().ContinueWith(_ => _ui.BeginInvoke(() =>
        {
            int total = Store.Count, favorites = Store.FavoriteCount;
            stats.Text = $"В истории: {total - favorites} из {_settings.MaxItems} · в избранном: {favorites}";
        }), TaskScheduler.Default);
        RefreshStats();

        void Clear(Button sender, bool keepFavorites)
        {
            var owner = Window.GetWindow(sender);
            int favorites = Store.FavoriteCount;
            string question = keepFavorites
                ? $"Удалить историю буфера обмена? Избранное ({favorites}) останется."
                : $"Удалить ВСЁ, включая избранное ({favorites})? Это нельзя отменить.";
            if (owner != null && MessageBox.Show(owner, question, DisplayName, MessageBoxButton.YesNo,
                    keepFavorites ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _ = Store.ClearAsync(keepFavorites).ContinueWith(_ => _ui.BeginInvoke(() =>
            {
                RefreshStats();
                Services.Overlay.ShowToast(keepFavorites ? "История очищена, избранное сохранено" : "История и избранное удалены", DisplayName);
            }), TaskScheduler.Default);
        }

        var clear = new Button { Content = FluentIcons.WithLabel(FluentIcons.Delete, "Очистить историю") };
        clear.Click += (_, _) => Clear(clear, keepFavorites: true);
        var clearAll = new Button { Content = FluentIcons.WithLabel(FluentIcons.Warning, "Удалить всё, включая избранное") };
        clearAll.Click += (_, _) => Clear(clearAll, keepFavorites: false);
        var open = new Button { Content = FluentIcons.WithLabel(FluentIcons.Folder, "Открыть папку") };
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                Process.Start(new ProcessStartInfo("explorer.exe", Store.Dir) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error($"история буфера: открыть папку: {ex.Message}"); }
        };
        // отступ справа и снизу у каждой: при переносе строки кнопки не слипаются
        var buttons = new WrapPanel();
        foreach (var b in new[] { clear, clearAll, open })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(b);
        }
        root.Children.Add(buttons);

        if (_settings.LegacyFavoritesImported && File.Exists(LegacyIndexFile))
        {
            var legacy = Note("Избранное прежнего модуля перенесено сюда; его файлы (clipboard\\index.json, clipboard\\img) оставлены как резервная копия.");
            legacy.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(legacy);
        }
        return root;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = JarvisTheme.ForegroundBrush(0.4),
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap
    };

    private static FrameworkElement Row(string label, FrameworkElement editor, FrameworkElement? note = null)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = label,
            Foreground = JarvisTheme.ForegroundBrush(0.85),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        row.Children.Add(text);
        row.Children.Add(editor);
        if (note != null)
        {
            note.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(note, 2);
            row.Children.Add(note);
        }
        return row;
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
