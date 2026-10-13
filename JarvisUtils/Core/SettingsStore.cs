using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisUtils.Core;

public sealed class SettingsStore : ISettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица читаема в пользовательских файлах
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() } // enum словами; числа из старых файлов тоже читаются
    };

    public T Load<T>(string name) where T : class, new()
    {
        try
        {
            var file = GetFilePath(name);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions) ?? new T()
                : new T();
        }
        catch (Exception ex) { Log.Error($"Load<{typeof(T).Name}>({name}): {ex.Message}"); return new T(); }
    }

    public void Save<T>(string name, T value) where T : class
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDir);
            var file = GetFilePath(name);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) { Log.Error($"Save<{typeof(T).Name}>({name}): {ex.Message}"); }
    }

    public string GetFilePath(string name) => Path.Combine(AppPaths.SettingsDir, name + ".json");
}