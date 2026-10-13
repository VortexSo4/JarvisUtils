using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ClipboardHistory;

internal sealed class HistoryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("kind")] public string Kind { get; set; } = "text"; // text | image
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("preview")] public string Preview { get; set; } = ""; // готовая однострочная выжимка: Regex по мегабайтному тексту на UI-потоке — фриз
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("time")] public DateTime Time { get; set; } = DateTime.Now;
    [JsonPropertyName("app")] public string? App { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
    [JsonPropertyName("rich")] public bool HasRich { get; set; }
    [JsonPropertyName("fav")] public bool Favorite { get; set; } // избранное: лимит его не вытесняет, «Очистить историю» не трогает

    [JsonIgnore] public bool IsImage => string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Итог добавления: новая запись, поднятый дубликат или дубликат, уже бывший в избранном.</summary>
internal enum AddResult { Added, Existing, AlreadyFavorite, Failed }

/// <summary>Запись старого модуля «Избранное буфера обмена» для импорта: текст или путь к PNG.</summary>
internal sealed record LegacyFavorite(string? Text, string? ImagePath, DateTime Created);

/// <summary>
/// Хранилище истории: index.json + PNG/миниатюры/HTML-RTF отдельными файлами в clipboard\history.
/// Список защищён _gate (операции над ним — микросекунды), весь диск и разбор картинок — на пуле
/// потоков. Захваты обрабатываются строго по очереди (_chain), чтобы порядок истории совпадал
/// с порядком копирования. Избранное — те же записи с флагом Favorite: лимит считается только
/// по обычным записям, а очистка истории по умолчанию избранное не трогает.
/// </summary>
internal sealed class ClipboardHistoryStore
{
    public static readonly string DefaultDir = Path.Combine(AppPaths.ClipboardDir, "history");

    // Миниатюра с запасом под 150–200% масштаба: в списке она ~64×44 DIP
    private const int ThumbMaxW = 192, ThumbMaxH = 132;
    private const int SaveDelayMs = 700;

    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private List<HistoryEntry> _items = [];
    private Task? _loadTask;
    private Task _chain = Task.CompletedTask;
    private Timer? _saveTimer;
    private bool _dirty;
    private int _limit;

    /// <summary>Список изменился. Поднимается с любого потока — подписчик сам уходит на свой диспетчер.</summary>
    public event Action? Changed;

    public bool IsLoaded { get; private set; }

    public string Dir { get; }
    private string IndexFile => Path.Combine(Dir, "index.json");
    private string ImgDir => Path.Combine(Dir, "img");
    private string ThumbDir => Path.Combine(Dir, "thumb");
    private string RichDir => Path.Combine(Dir, "rich");

    /// <param name="dir">Папка хранилища; по умолчанию — clipboard\history в данных приложения.</param>
    public ClipboardHistoryStore(int limit, string? dir = null)
    {
        _limit = Math.Max(1, limit);
        Dir = dir ?? DefaultDir;
    }

    public string ImagePath(HistoryEntry e) => Path.Combine(ImgDir, e.Id + ".png");
    public string ThumbPath(HistoryEntry e) => Path.Combine(ThumbDir, e.Id + ".png");
    private string RichPath(HistoryEntry e) => Path.Combine(RichDir, e.Id + ".bin");

    /// <summary>Ленивая загрузка с диска (один раз, в фоне).</summary>
    public Task EnsureLoadedAsync()
    {
        lock (_gate) return _loadTask ??= Task.Run(LoadCore);
    }

    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public int FavoriteCount
    {
        get { lock (_gate) return _items.Count(e => e.Favorite); }
    }

    /// <summary>Поставить захват в очередь обработки. Можно звать с clipboard-потока — возврат мгновенный.</summary>
    public void Enqueue(ClipboardCapture capture) => Chain(() => Add(capture, favorite: false), AddResult.Failed);

    /// <summary>Добавить захват сразу в избранное (хоткей «текущий буфер → избранное»).</summary>
    public Task<AddResult> EnqueueFavoriteAsync(ClipboardCapture capture) => Chain(() => Add(capture, favorite: true), AddResult.Failed);

    /// <summary>
    /// Импорт избранного старого модуля: в конец списка, в исходном порядке, со звездой.
    /// Запись с тем же содержимым уже есть — только ставим звезду, поэтому повтор ничего не дублирует.
    /// </summary>
    public Task<int> ImportFavoritesAsync(IReadOnlyList<LegacyFavorite> items) => Chain(() =>
    {
        int imported = 0;
        foreach (var item in items)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.Text))
                {
                    var hash = TextHash(item.Text);
                    if (!MarkFavorite(hash)) Append(CreateTextEntry(item.Text, hash, null, []), item.Created);
                    imported++;
                }
                else if (item.ImagePath != null && File.Exists(item.ImagePath))
                {
                    var png = File.ReadAllBytes(item.ImagePath);
                    var image = HistoryImages.Decode(png, null);
                    var hash = ImageHash(image);
                    if (!MarkFavorite(hash)) Append(CreateImageEntry(image, png, hash, null), item.Created);
                    imported++;
                }
            }
            catch (Exception ex) { Log.Warn($"история буфера: импорт избранного: {ex.Message}"); }
        }
        ScheduleSave();
        Changed?.Invoke();
        return imported;
    }, 0);

    /// <summary>Дождаться обработки всех поставленных захватов (для тестов и выхода).</summary>
    public Task WhenIdle()
    {
        lock (_gate) return _chain;
    }

    /// <summary>Поднять запись наверх (после вставки из истории — как в Win+V).</summary>
    public void Touch(string id)
    {
        lock (_gate)
        {
            int i = _items.FindIndex(e => e.Id == id);
            if (i < 0) return;
            var e = _items[i];
            _items.RemoveAt(i);
            e.Time = DateTime.Now;
            _items.Insert(0, e);
        }
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>
    /// Поставить или снять звезду. Снятая звезда делает запись обычной: сразу её не удаляем,
    /// лимит вытеснит её при следующем копировании, если она окажется самой старой.
    /// </summary>
    public void SetFavorite(string id, bool favorite)
    {
        lock (_gate)
        {
            var e = _items.FirstOrDefault(x => x.Id == id);
            if (e == null || e.Favorite == favorite) return;
            e.Favorite = favorite;
        }
        ScheduleSave();
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        HistoryEntry? removed;
        lock (_gate)
        {
            removed = _items.FirstOrDefault(e => e.Id == id);
            if (removed == null) return;
            _items.Remove(removed);
        }
        Task.Run(() => DeleteFiles([removed]));
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>
    /// Очистить историю; keepFavorites — удалить только обычные записи. Если загрузка ещё идёт —
    /// после неё, иначе прочитанный файл вернул бы записи.
    /// </summary>
    public Task ClearAsync(bool keepFavorites) => EnsureLoadedAsync().ContinueWith(_ =>
    {
        List<HistoryEntry> removed;
        lock (_gate)
        {
            removed = _items.Where(e => !keepFavorites || !e.Favorite).ToList();
            _items.RemoveAll(e => !keepFavorites || !e.Favorite);
        }
        if (keepFavorites) DeleteFiles(removed);
        else
            lock (_ioGate)
                foreach (var dir in new[] { ImgDir, ThumbDir, RichDir })
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* файл занят — останется мусором до следующей очистки */ }
        ScheduleSave();
        Changed?.Invoke();
    }, TaskScheduler.Default);

    public void SetLimit(int limit)
    {
        List<HistoryEntry> removed;
        lock (_gate)
        {
            _limit = Math.Max(1, limit);
            removed = TrimLocked();
        }
        if (removed.Count == 0) return;
        Task.Run(() => DeleteFiles(removed));
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>HTML/RTF записи (имя формата → байты). Читает диск — звать вне UI-потока.</summary>
    public List<(string Name, byte[] Data)> LoadRich(HistoryEntry e)
    {
        var result = new List<(string, byte[])>();
        try
        {
            var path = RichPath(e);
            if (!e.HasRich || !File.Exists(path)) return result;
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var name = reader.ReadString();
                var data = reader.ReadBytes(reader.ReadInt32());
                result.Add((name, data));
            }
        }
        catch (Exception ex) { Log.Warn($"история буфера: rich-данные не прочитаны: {ex.Message}"); }
        return result;
    }

    /// <summary>Синхронно дописать отложенное сохранение (выход из приложения).</summary>
    public void Flush()
    {
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (_dirty) SaveNow();
    }

    // ── обработка (пул потоков, строго по очереди) ──

    /// <summary>Работа в общей очереди — после загрузки с диска и после ранее поставленных захватов.</summary>
    private Task<T> Chain<T>(Func<T> work, T onError)
    {
        lock (_gate)
        {
            var task = _chain.ContinueWith(_ =>
            {
                try
                {
                    EnsureLoadedAsync().Wait();
                    return work();
                }
                catch (Exception ex)
                {
                    Log.Error($"история буфера: обработка: {ex}");
                    return onError;
                }
            }, TaskScheduler.Default);
            _chain = task;
            return task;
        }
    }

    private AddResult Add(ClipboardCapture c, bool favorite)
    {
        var app = AppNames.Resolve(c.OwnerPid);
        if (c.Text != null)
        {
            var hash = TextHash(c.Text);
            if (MoveExistingToTop(hash, app, favorite) is { } existing) return existing;
            Insert(CreateTextEntry(c.Text, hash, app, c.Rich), favorite);
        }
        else
        {
            var image = HistoryImages.Decode(c.Png, c.Dib);
            var hash = ImageHash(image);
            if (MoveExistingToTop(hash, app, favorite) is { } existing) return existing;
            Insert(CreateImageEntry(image, c.Png, hash, app), favorite);
        }
        return AddResult.Added;
    }

    private static string TextHash(string text) => "t:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Хэш по пикселям, а не по байтам формата: одна картинка из разных приложений приходит
    /// разными PNG/DIB, но должна схлопнуться в одну запись.
    /// </summary>
    private static string ImageHash(BitmapSource image) =>
        "i:" + HistoryImages.Hash(HistoryImages.Pixels(image, out _), image.PixelWidth, image.PixelHeight);

    private HistoryEntry CreateTextEntry(string text, string hash, string? app, List<(string Name, byte[] Data)> rich)
    {
        var entry = new HistoryEntry
        {
            Kind = "text",
            Text = text,
            Preview = TextUtil.Preview(text.Length > 4000 ? text[..4000] : text, 300),
            Hash = hash,
            App = app
        };
        if (rich.Count == 0) return entry;
        try
        {
            lock (_ioGate)
            {
                Directory.CreateDirectory(RichDir);
                using var writer = new BinaryWriter(File.Create(RichPath(entry)), Encoding.UTF8);
                writer.Write(rich.Count);
                foreach (var (name, data) in rich)
                {
                    writer.Write(name);
                    writer.Write(data.Length);
                    writer.Write(data);
                }
            }
            entry.HasRich = true;
        }
        catch (Exception ex) { Log.Warn($"история буфера: rich-данные не сохранены: {ex.Message}"); }
        return entry;
    }

    /// <summary>Запись-картинка; PNG и миниатюра пишутся на диск сразу.</summary>
    private HistoryEntry CreateImageEntry(BitmapSource image, byte[]? png, string hash, string? app)
    {
        var entry = new HistoryEntry
        {
            Kind = "image",
            Hash = hash,
            App = app,
            Width = image.PixelWidth,
            Height = image.PixelHeight,
            Preview = $"Изображение {image.PixelWidth}×{image.PixelHeight}"
        };
        lock (_ioGate)
        {
            Directory.CreateDirectory(ImgDir);
            Directory.CreateDirectory(ThumbDir);
            // исходный PNG пишем как есть — без перекодирования и потерь альфы
            File.WriteAllBytes(ImagePath(entry), png ?? HistoryImages.EncodePng(image));
            File.WriteAllBytes(ThumbPath(entry), HistoryImages.EncodePng(HistoryImages.Thumbnail(image, ThumbMaxW, ThumbMaxH)));
        }
        return entry;
    }

    /// <summary>Дубликат — наверх (и в избранное, если просили). null — такой записи ещё нет.</summary>
    private AddResult? MoveExistingToTop(string hash, string? app, bool favorite)
    {
        AddResult result;
        lock (_gate)
        {
            int i = _items.FindIndex(e => e.Hash == hash);
            if (i < 0) return null;
            var e = _items[i];
            _items.RemoveAt(i);
            e.Time = DateTime.Now;
            if (app != null) e.App = app;
            result = favorite && e.Favorite ? AddResult.AlreadyFavorite : AddResult.Existing;
            if (favorite) e.Favorite = true;
            _items.Insert(0, e);
        }
        ScheduleSave();
        Changed?.Invoke();
        return result;
    }

    /// <summary>Импорт: запись с таким содержимым уже есть — только ставим звезду.</summary>
    private bool MarkFavorite(string hash)
    {
        lock (_gate)
        {
            var e = _items.FirstOrDefault(x => x.Hash == hash);
            if (e == null) return false;
            e.Favorite = true;
            return true;
        }
    }

    private void Append(HistoryEntry entry, DateTime time)
    {
        entry.Favorite = true;
        entry.Time = time;
        lock (_gate) _items.Add(entry);
    }

    private void Insert(HistoryEntry entry, bool favorite)
    {
        entry.Favorite = favorite;
        List<HistoryEntry> removed;
        lock (_gate)
        {
            _items.Insert(0, entry);
            removed = TrimLocked();
        }
        DeleteFiles(removed);
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>Лимит — только для обычных записей: вытесняются самые старые из них, избранное остаётся.</summary>
    private List<HistoryEntry> TrimLocked()
    {
        int regular = _items.Count(e => !e.Favorite);
        if (regular <= _limit) return [];
        var removed = new List<HistoryEntry>();
        for (int i = _items.Count - 1; i >= 0 && regular > _limit; i--)
        {
            if (_items[i].Favorite) continue;
            removed.Add(_items[i]);
            _items.RemoveAt(i);
            regular--;
        }
        return removed;
    }

    private void DeleteFiles(IEnumerable<HistoryEntry> entries)
    {
        lock (_ioGate)
            foreach (var e in entries)
            {
                TryDelete(ImagePath(e));
                TryDelete(ThumbPath(e));
                TryDelete(RichPath(e));
            }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* занят — не критично */ }
    }

    // ── диск ──

    private void LoadCore()
    {
        List<HistoryEntry> loaded = [];
        try
        {
            if (File.Exists(IndexFile))
                loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(IndexFile), SettingsStore.JsonOptions) ?? [];
            // картинку могли удалить руками — такую запись не показываем
            loaded.RemoveAll(e => string.IsNullOrEmpty(e.Hash) || (e.IsImage ? !File.Exists(ImagePath(e)) : string.IsNullOrEmpty(e.Text)));
        }
        catch (Exception ex) { Log.Error($"история буфера: загрузка: {ex.Message}"); }

        List<HistoryEntry> removed;
        lock (_gate)
        {
            _items = loaded;
            removed = TrimLocked();
            IsLoaded = true;
        }
        DeleteFiles(removed);
        Changed?.Invoke();
    }

    private void ScheduleSave()
    {
        _dirty = true;
        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => SaveNow());
            _saveTimer.Change(SaveDelayMs, Timeout.Infinite); // дребезг: серия копирований = одна запись
        }
    }

    private void SaveNow()
    {
        if (!IsLoaded) return; // не затираем файл, который ещё не прочитали
        HistoryEntry[] snapshot;
        lock (_gate)
        {
            _dirty = false;
            snapshot = _items.ToArray();
        }
        try
        {
            lock (_ioGate)
            {
                Directory.CreateDirectory(Dir);
                var tmp = IndexFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, SettingsStore.JsonOptions));
                File.Move(tmp, IndexFile, true);
            }
        }
        catch (Exception ex) { Log.Error($"история буфера: сохранение: {ex.Message}"); }
    }
}

/// <summary>Человеческое имя приложения-источника («Google Chrome», а не chrome). Кэш по PID.</summary>
internal static class AppNames
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly Dictionary<uint, string?> Cache = new();

    public static string? Resolve(uint pid)
    {
        if (pid == 0) return null;
        lock (Cache)
            if (Cache.TryGetValue(pid, out var cached)) return cached;

        string? name = null;
        try
        {
            var path = ImagePath(pid);
            if (path != null)
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                name = !string.IsNullOrEmpty(description) ? description : Path.GetFileNameWithoutExtension(path);
            }
        }
        catch { /* процесс уже завершился */ }

        lock (Cache)
        {
            if (Cache.Count > 128) Cache.Clear(); // PID переиспользуются — кэш не должен жить вечно
            Cache[pid] = name;
        }
        return name;
    }

    private static string? ImagePath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid); // работает и для процессов «от админа»
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
