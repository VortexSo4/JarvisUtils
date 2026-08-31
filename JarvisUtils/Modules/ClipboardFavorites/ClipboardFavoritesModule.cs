using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ClipboardFavorites;

public sealed class FavoriteItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("type")] public string Type { get; set; } = "text"; // text | image
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("created")] public DateTime Created { get; set; } = DateTime.Now;
    public bool IsImage => string.Equals(Type, "image", StringComparison.OrdinalIgnoreCase);
}

public static class FavoriteStore
{
    private static string IndexFile => Path.Combine(AppPaths.ClipboardDir, "index.json");

    public static List<FavoriteItem> Load()
    {
        try
        {
            if (!File.Exists(IndexFile)) return [];
            return JsonSerializer.Deserialize<List<FavoriteItem>>(File.ReadAllText(IndexFile), SettingsStore.JsonOptions) ??
                   [];
        }
        catch (Exception ex) { Log.Error($"favorites: {ex.Message}"); return []; }
    }

    public static void Save(IEnumerable<FavoriteItem> items)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ClipboardDir);
            var tmp = IndexFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, SettingsStore.JsonOptions));
            File.Move(tmp, IndexFile, true);
        }
        catch (Exception ex) { Log.Error($"favorites save: {ex.Message}"); }
    }

    public static string SaveImage(BitmapSource image)
    {
        Directory.CreateDirectory(AppPaths.ClipboardImagesDir);
        var path = Path.Combine(AppPaths.ClipboardImagesDir, $"{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    public static BitmapSource LoadImage(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return img;
    }
}

public sealed class ClipboardFavoritesModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor
{
    public sealed class Settings
    {
        public string HotkeyShow { get; set; } = "Ctrl+Alt+Space";
        public string HotkeyAdd { get; set; } = "Ctrl+Alt+B";
        public int MaxItems { get; set; } = 30;
    }

    private Settings _settings = null!;
    private List<FavoriteItem> _items = [];

    public override string DisplayName => "Избранное буфера обмена";

    protected override void RegisterHotkeys()
    {
        _settings ??= LoadSettings<Settings>();
        _items = FavoriteStore.Load();
        RegisterHotkey(_settings.HotkeyShow, ShowList);
        RegisterHotkey(_settings.HotkeyAdd, AddCurrent);
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new HotkeySlot("Показать список избранного", () => _settings.HotkeyShow,
            g => { _settings.HotkeyShow = g; SaveSettings(_settings); RestartHotkeys(); }),
        new HotkeySlot("Добавить текущий буфер в избранное", () => _settings.HotkeyAdd,
            g => { _settings.HotkeyAdd = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    private void ShowList()
    {
        if (_items.Count == 0)
        {
            Services.Overlay.ShowToast($"Список пуст. {_settings.HotkeyAdd} добавит текущий буфер.", "Clipboard Favorites");
            return;
        }
        Services.Overlay.ShowListPopup(new ListPopupRequest
        {
            Title = "Clipboard Favorites",
            Items = _items.Select(ToItem).ToList(),
            OnPick = PasteFavorite,
            OnDelete = DeleteFavorite
        });
    }

    private static OverlayListItem ToItem(FavoriteItem f) => f.IsImage
        ? new OverlayListItem("[Изображение]", $"{f.Created:dd.MM.yyyy HH:mm} · PNG")
        : new OverlayListItem(TextUtil.Preview(f.Text, 70), $"{f.Created:dd.MM.yyyy HH:mm} · {(f.Text ?? "").Length} симв.");

    private void PasteFavorite(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        try
        {
            if (item.IsImage) Services.Clipboard.SetImage(FavoriteStore.LoadImage(item.Path!));
            else Services.Clipboard.SetText(item.Text!);
        }
        catch (Exception ex)
        {
            Log.Error($"paste favorite: {ex.Message}");
            Services.Overlay.ShowToast("Не удалось подготовить элемент", "Clipboard Favorites");
            return;
        }
        // попап фокус не крал, целевое приложение всё ещё активно — вставляем
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { timer.Stop(); Services.Input.SendCtrlV(); };
        timer.Start();
    }

    private void AddCurrent()
    {
        try
        {
            if (Services.Clipboard.ContainsImage && Services.Clipboard.GetImage() is { } img)
            {
                var path = FavoriteStore.SaveImage(img);
                _items.Insert(0, new FavoriteItem { Type = "image", Path = path });
            }
            else if (Services.Clipboard.ContainsText && !string.IsNullOrEmpty(Services.Clipboard.GetText()))
            {
                _items.Insert(0, new FavoriteItem { Type = "text", Text = Services.Clipboard.GetText() });
            }
            else
            {
                Services.Overlay.ShowToast("Буфер обмена пуст", "Clipboard Favorites");
                return;
            }
            TrimOverflow();
            FavoriteStore.Save(_items);
            Services.Overlay.ShowToast("Добавлено в избранное (переживёт перезагрузку)", "Clipboard Favorites");
        }
        catch (Exception ex) { Log.Error($"add favorite: {ex.Message}"); }
    }

    private void DeleteFavorite(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        _items.RemoveAt(index);
        if (item.IsImage && item.Path != null) { try { File.Delete(item.Path); } catch { } }
        FavoriteStore.Save(_items);
        Services.Overlay.ShowToast("Удалено из избранного", "Clipboard Favorites");
    }

    private void TrimOverflow()
    {
        while (_items.Count > Math.Max(1, _settings.MaxItems))
        {
            var removed = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            if (removed.IsImage && removed.Path != null) { try { File.Delete(removed.Path); } catch { } }
        }
    }

    public FrameworkElement BuildEditor()
    {
        var open = new Button { Content = "Открыть папку хранилища" };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.ClipboardDir) { UseShellExecute = true });

        var clear = new Button { Content = "Очистить избранное", Margin = new Thickness(8, 0, 0, 0) };
        clear.Click += (_, _) =>
        {
            _items.Clear();
            FavoriteStore.Save(_items);
            try { if (Directory.Exists(AppPaths.ClipboardImagesDir)) Directory.Delete(AppPaths.ClipboardImagesDir, true); } catch { }
            Services.Overlay.ShowToast("Избранное очищено", "Clipboard Favorites");
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(open);
        buttons.Children.Add(clear);

        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock
        {
            Text = $"Текст — в index.json, картинки — PNG-файлами. Максимум элементов: {_settings.MaxItems}.",
            Foreground = JarvisTheme.ForegroundBrush(0.5),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return panel;
    }
}