using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.ClipboardHistory;

/// <summary>
/// Чтение данных бывшего модуля «Избранное буфера обмена» (clipboard\index.json + PNG в clipboard\img)
/// для разового импорта. Старые файлы не меняем и не удаляем — они остаются резервной копией.
/// </summary>
internal static class LegacyFavorites
{
    public static readonly string DefaultIndexFile = Path.Combine(AppPaths.ClipboardDir, "index.json");

    /// <summary>Имя файла настроек старого модуля (settings\ClipboardFavorites.json).</summary>
    public const string SettingsName = "ClipboardFavorites";

    /// <summary>Хоткеи старого модуля; null — файла/значения нет, переносить нечего.</summary>
    public sealed class Settings
    {
        public string? HotkeyShow { get; set; }
        public string? HotkeyAdd { get; set; }
    }

    private sealed class Item
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("created")] public DateTime Created { get; set; }
    }

    /// <summary>
    /// Элементы в исходном порядке (сверху — новые). Пустой список — файла нет или он пуст;
    /// null — файл есть, но не читается (импорт повторим при следующем запуске).
    /// </summary>
    public static List<LegacyFavorite>? Read(string indexFile)
    {
        try
        {
            if (!File.Exists(indexFile)) return [];
            var items = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(indexFile), SettingsStore.JsonOptions) ?? [];
            return items
                .Select(i => string.Equals(i.Type, "image", StringComparison.OrdinalIgnoreCase)
                    ? new LegacyFavorite(null, i.Path, i.Created)
                    : new LegacyFavorite(i.Text, null, i.Created))
                .Where(f => !string.IsNullOrEmpty(f.Text) || !string.IsNullOrEmpty(f.ImagePath))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error($"буфер обмена: старое избранное не прочитано: {ex.Message}");
            return null;
        }
    }
}
