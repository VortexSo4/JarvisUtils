using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Calculator;

/// <summary>Курсы на момент загрузки: сколько единиц валюты дают за 1 USD.</summary>
public sealed record RatesSnapshot(Dictionary<string, double> PerUsd, DateTime RatesDateUtc, DateTime FetchedUtc, string Source)
{
    [JsonIgnore] public bool IsExpired => DateTime.UtcNow - FetchedUtc > CurrencyRates.Ttl;
}

/// <summary>
/// Курсы валют для калькулятора. Источник — бесплатные API без ключа: open.er-api.com (база USD),
/// запасной — cbr-xml-daily.ru (курсы ЦБ РФ). Кэш на диске живёт <see cref="Ttl"/>; без сети
/// используется устаревший кэш (окно помечает дату курса).
/// Всё сетевое и дисковое — в фоне: калькулятор вызывается на UI-потоке, где висят хуки клавиатуры.
/// </summary>
public static class CurrencyRates
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(4);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1); // не долбить сеть на каждую букву

    private const string PrimaryUrl = "https://open.er-api.com/v6/latest/USD";
    private const string FallbackUrl = "https://www.cbr-xml-daily.ru/daily_json.js";

    /// <summary>Путь кэша; internal set — тесты подменяют, чтобы не трогать настоящий AppData.</summary>
    internal static string CacheFile { get; set; } = Path.Combine(AppPaths.DataDir, "currency-rates.json");

    private static readonly HttpClient Http = CreateClient();

    private static volatile RatesSnapshot? _current;
    private static int _loading;          // 0/1, Interlocked
    private static int _diskTried;        // кэш с диска читаем один раз за жизнь процесса
    private static long _lastFailureTicks; // 0 — неудач не было
    private static volatile bool _lastFetchFailed;

    /// <summary>Курсы обновились (или загрузка закончилась неудачей). Вызывается из фонового потока.</summary>
    public static event Action? Updated;

    public static RatesSnapshot? Current => _current;
    public static bool IsLoading => Volatile.Read(ref _loading) == 1;
    /// <summary>Последняя попытка обновить курсы не удалась (нет сети, API лежат).</summary>
    public static bool LastFetchFailed => _lastFetchFailed;

    public static bool HasCode(string code) => _current?.PerUsd.ContainsKey(code) == true;

    /// <summary>Запустить фоновую загрузку, если курсов нет или они устарели. Не блокирует, идемпотентен.</summary>
    public static void EnsureFresh()
    {
        var cur = _current;
        if (cur != null && !cur.IsExpired) return;
        long failedAt = Interlocked.Read(ref _lastFailureTicks);
        if (failedAt != 0 && Environment.TickCount64 - failedAt < RetryAfterFailure.TotalMilliseconds)
            return;
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        _ = Task.Run(LoadAsync);
    }

    private static async Task LoadAsync()
    {
        try
        {
            if (Interlocked.Exchange(ref _diskTried, 1) == 0 && _current == null)
            {
                var cached = await ReadCacheAsync().ConfigureAwait(false);
                if (cached != null)
                {
                    _current = cached;
                    if (!cached.IsExpired) return;
                    RaiseUpdated(); // устаревший кэш — показать сразу, пока идёт обновление
                }
            }

            var fresh = await FetchPrimaryAsync().ConfigureAwait(false)
                        ?? await FetchCbrAsync().ConfigureAwait(false);
            if (fresh != null)
            {
                _current = fresh;
                _lastFetchFailed = false;
                await WriteCacheAsync(fresh).ConfigureAwait(false);
            }
            else
            {
                _lastFetchFailed = true;
                Interlocked.Exchange(ref _lastFailureTicks, Environment.TickCount64);
            }
        }
        catch (Exception ex)
        {
            _lastFetchFailed = true;
            Interlocked.Exchange(ref _lastFailureTicks, Environment.TickCount64);
            Log.Warn($"Курсы валют: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _loading, 0); // до события: подписчик должен увидеть IsLoading == false
            RaiseUpdated();
        }
    }

    private static void RaiseUpdated()
    {
        try { Updated?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Курсы валют: обработчик обновления упал: {ex.Message}"); }
    }

    // ── источники ──

    private static async Task<RatesSnapshot?> FetchPrimaryAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(PrimaryUrl).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success") return null;
            var rates = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in root.GetProperty("rates").EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) rates[p.Name.ToUpperInvariant()] = p.Value.GetDouble();
            if (!rates.ContainsKey("RUB")) return null;
            rates["USD"] = 1;
            var date = root.TryGetProperty("time_last_update_unix", out var t) && t.TryGetInt64(out long unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : DateTime.UtcNow;
            return new RatesSnapshot(rates, date, DateTime.UtcNow, "open.er-api.com");
        }
        catch (Exception ex)
        {
            Log.Warn($"Курсы валют (open.er-api.com): {ex.Message}");
            return null;
        }
    }

    /// <summary>ЦБ РФ даёт рубли за Nominal единиц валюты — пересчитываем в «за 1 USD».</summary>
    private static async Task<RatesSnapshot?> FetchCbrAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(FallbackUrl).ConfigureAwait(false));
            var root = doc.RootElement;
            var rubPer = new Dictionary<string, double>(StringComparer.Ordinal) { ["RUB"] = 1 };
            foreach (var p in root.GetProperty("Valute").EnumerateObject())
            {
                double nominal = p.Value.GetProperty("Nominal").GetDouble();
                double value = p.Value.GetProperty("Value").GetDouble();
                if (nominal > 0 && value > 0) rubPer[p.Name.ToUpperInvariant()] = value / nominal;
            }
            if (!rubPer.TryGetValue("USD", out double rubPerUsd)) return null;

            var rates = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (code, rub) in rubPer) rates[code] = rubPerUsd / rub;
            var date = root.TryGetProperty("Date", out var d) && DateTimeOffset.TryParse(d.GetString(),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                ? dto.UtcDateTime
                : DateTime.UtcNow;
            return new RatesSnapshot(rates, date, DateTime.UtcNow, "ЦБ РФ");
        }
        catch (Exception ex)
        {
            Log.Warn($"Курсы валют (ЦБ РФ): {ex.Message}");
            return null;
        }
    }

    // ── дисковый кэш ──

    private static async Task<RatesSnapshot?> ReadCacheAsync()
    {
        try
        {
            string file = CacheFile;
            if (!File.Exists(file)) return null;
            await using var stream = File.OpenRead(file);
            var snap = await JsonSerializer.DeserializeAsync<RatesSnapshot>(stream).ConfigureAwait(false);
            return snap is { PerUsd.Count: > 0 } ? snap with { PerUsd = new(snap.PerUsd, StringComparer.Ordinal) } : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Курсы валют: кэш не прочитан: {ex.Message}");
            return null;
        }
    }

    private static async Task WriteCacheAsync(RatesSnapshot snap)
    {
        try
        {
            string file = CacheFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string tmp = file + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snap)).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"Курсы валют: кэш не записан: {ex.Message}"); }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisUtils/1.0");
        return http;
    }
}
