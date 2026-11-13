using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

/// <summary>Raised when the Solcast API call fails and no cached data is available.</summary>
public class SolcastException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One Solcast forecast period; values are the average kW over the period.</summary>
public record ForecastPeriod(DateTimeOffset PeriodEnd, double P50, double P10, double P90);

/// <summary>Solcast PV power forecast (rooftop sites API) with response caching.</summary>
public class SolcastForecast
{
    public const string BaseUrl = "https://api.solcast.com.au";
    const double PeriodHours = 0.5; // Solcast rooftop forecasts use PT30M periods

    static ILogger Log => AppLog.For<SolcastForecast>();

    readonly HttpClient _http;
    // Hobbyist accounts get ~10 calls/day, so cache per-site responses and serve stale data on failure
    readonly Dictionary<string, (double FetchedAt, JsonArray Periods)> _cache = [];
    readonly SemaphoreSlim _cacheLock = new(1, 1);
    bool _cacheLoaded;

    public SolcastForecast(HttpMessageHandler? handler = null)
    {
        _http = Http.Create(handler, 15);
    }

    /// <summary>Fill the in-memory cache from SOLCAST_CACHE_FILE once per process. File format: {siteId: [epoch, periods]}.</summary>
    void LoadCacheFile()
    {
        _cacheLoaded = true;
        var path = Config.SolcastCacheFile();
        if (path is null || !File.Exists(path)) return;
        try
        {
            foreach (var (resourceId, entry) in JsonNode.Parse(File.ReadAllText(path))!.AsObject())
                _cache.TryAdd(resourceId, (J.Num(entry![0]) ?? throw new FormatException("no fetch time"), entry[1]!.AsArray()));
        }
        catch (Exception e) // an unreadable cache file must never stop the server
        {
            Log.LogWarning("Ignoring unreadable Solcast cache file {Path}: {Error}", path, e.Message);
        }
    }

    void SaveCacheFile()
    {
        if (Config.SolcastCacheFile() is not { } path) return;
        try
        {
            var data = new JsonObject();
            foreach (var (resourceId, (fetchedAt, periods)) in _cache) data[resourceId] = new JsonArray(fetchedAt, periods.DeepClone());
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, data.ToJsonString());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning("Could not write Solcast cache file {Path}: {Error}", path, e.Message);
        }
    }

    /// <summary>Raw 30-minute forecast periods for one rooftop site.</summary>
    async Task<JsonArray> FetchSiteAsync(string resourceId, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/rooftop_sites/{Uri.EscapeDataString(resourceId)}/forecasts?format=json&hours=168");
        // Key in a header, not the query string, so it never ends up in URLs or logs
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.SendAsync(request);
            text = await response.Content.ReadAsStringAsync();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new SolcastException($"Network error: {e.Message}", e);
        }

        if ((int)response.StatusCode == 429) throw new SolcastException("Solcast API rate limit reached (429)");
        if ((int)response.StatusCode != 200) throw new SolcastException($"HTTP {(int)response.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        try
        {
            return (JsonNode.Parse(text) as JsonObject)?["forecasts"] as JsonArray ?? throw new JsonException("no 'forecasts' list");
        }
        catch (JsonException e)
        {
            throw new SolcastException($"Invalid forecast response: {e.Message}", e);
        }
    }

    /// <summary>Periods for a site, from the cache when it is fresh. Stale = the refresh failed and old data is served.</summary>
    async Task<(JsonArray Periods, double FetchedAt, bool Stale)> GetSiteForecastAsync(string resourceId)
    {
        var ttl = Config.SolcastCacheMinutes() * 60;
        var hasCache = _cache.TryGetValue(resourceId, out var cached);
        if (hasCache && LocalTime.Epoch - cached.FetchedAt < ttl) return (cached.Periods, cached.FetchedAt, false);

        JsonArray periods;
        try
        {
            periods = await FetchSiteAsync(resourceId, Config.SolcastApiKey());
        }
        catch (SolcastException e) when (hasCache)
        {
            Log.LogWarning("Solcast fetch failed for site {Site}, serving cached data: {Error}", resourceId, e.Message);
            return (cached.Periods, cached.FetchedAt, true);
        }

        var fetchedAt = LocalTime.Epoch;
        _cache[resourceId] = (fetchedAt, periods);
        SaveCacheFile();
        return (periods, fetchedAt, false);
    }

    /// <summary>Solcast sends 7 fractional digits and "Z" ("...00.0000000Z"). The value is UTC.</summary>
    public static DateTimeOffset ParsePeriodEnd(string value) => new(
        DateTime.Parse(value.TrimEnd('Z'), CultureInfo.InvariantCulture, DateTimeStyles.None).Ticks, TimeSpan.Zero);

    /// <summary>Aggregate merged 30-min periods into per-day energy totals and an hourly power profile.</summary>
    /// <param name="periods">Solcast periods, already summed across sites.</param>
    /// <param name="tz">Local time zone for day boundaries and output timestamps.</param>
    /// <param name="now">Periods that end before this are in the past.</param>
    /// <param name="hours">Number of upcoming hours in the hourly profile.</param>
    public static JsonObject ShapeForecast(IEnumerable<ForecastPeriod> periods, TimeZoneInfo tz, DateTimeOffset now, int hours)
    {
        // Index 0, 1, 2 = p10, p50, p90
        var days = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        var peaks = new Dictionary<string, (double Kw, DateTimeOffset Time)>();
        var hourly = new SortedDictionary<DateTimeOffset, List<double>[]>();
        var remainingToday = new double[3];

        var today = LocalTime.ToLocal(now, tz).Date;
        var horizon = now.AddHours(hours);

        foreach (var p in periods)
        {
            var start = p.PeriodEnd.AddHours(-PeriodHours);
            var localStart = LocalTime.ToLocal(start, tz);
            var day = localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            double[] values = [p.P10, p.P50, p.P90];

            if (!days.TryGetValue(day, out var totals)) days[day] = totals = new double[3];
            for (var k = 0; k < 3; k++) totals[k] += values[k] * PeriodHours;
            if (!peaks.TryGetValue(day, out var peak) || p.P50 > peak.Kw) peaks[day] = (p.P50, localStart);

            if (p.PeriodEnd > now && localStart.Date == today)
                for (var k = 0; k < 3; k++) remainingToday[k] += values[k] * PeriodHours;

            if (p.PeriodEnd > now && start < horizon)
            {
                var hour = new DateTimeOffset(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0, localStart.Offset);
                if (!hourly.TryGetValue(hour, out var lists)) hourly[hour] = lists = [[], [], []];
                for (var k = 0; k < 3; k++) lists[k].Add(values[k]);
            }
        }

        static JsonObject Percentiles(Func<int, double> value) => new()
        {
            ["p10"] = J.Round(value(0)), ["p50"] = J.Round(value(1)), ["p90"] = J.Round(value(2)),
        };
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new JsonObject
        {
            ["timezone"] = tz.Id,
            ["days"] = J.Array(days.Where(d => string.CompareOrdinal(d.Key, todayText) >= 0).Select(d => new JsonObject
            {
                ["date"] = d.Key,
                ["energy_kWh"] = Percentiles(k => d.Value[k]),
                ["peakPower_kW"] = J.Round(peaks[d.Key].Kw),
                ["peakTime"] = J.Iso(peaks[d.Key].Time),
            })),
            ["remainingToday_kWh"] = Percentiles(k => remainingToday[k]),
            ["hourly"] = J.Array(hourly.Select(h => new JsonObject
            {
                ["time"] = J.Iso(h.Key),
                // Sum() adds in list order from 0, like the old server, so the rounding is the same
                ["power_kW"] = Percentiles(k => h.Value[k].Aggregate(0.0, (sum, kw) => sum + kw) / h.Value[k].Count),
            })),
        };
    }

    /// <summary>Fetch (or serve cached) the forecast for all configured sites and shape it.</summary>
    /// <exception cref="SolcastException">A site cannot be fetched and has no cached data.</exception>
    public virtual async Task<JsonObject> GetSolarForecastAsync(int hours = 24)
    {
        var tz = LocalTime.Zone(Config.SolarTimezone());
        var results = new List<(JsonArray Periods, double FetchedAt, bool Stale)>();

        await _cacheLock.WaitAsync();
        try
        {
            if (!_cacheLoaded) LoadCacheFile();
            foreach (var resourceId in Config.SolcastResourceIds()) results.Add(await GetSiteForecastAsync(resourceId));
        }
        finally
        {
            _cacheLock.Release();
        }

        // Sum sites (e.g. east/west arrays) per period, in first-seen order
        var merged = new List<double[]>();
        var ends = new List<DateTimeOffset>();
        var index = new Dictionary<DateTimeOffset, int>();
        foreach (var (periods, _, _) in results)
        {
            foreach (var p in periods)
            {
                var end = ParsePeriodEnd(J.Str(p!["period_end"]) ?? throw new SolcastException("Invalid forecast response: no period_end"));
                if (!index.TryGetValue(end, out var i))
                {
                    index[end] = i = merged.Count;
                    merged.Add(new double[3]);
                    ends.Add(end);
                }
                merged[i][0] += J.Num(p["pv_estimate"]) ?? 0.0;
                merged[i][1] += J.Num(p["pv_estimate10"]) ?? 0.0;
                merged[i][2] += J.Num(p["pv_estimate90"]) ?? 0.0;
            }
        }

        var shaped = ShapeForecast(merged.Select((m, i) => new ForecastPeriod(ends[i], m[0], m[1], m[2])), tz, LocalTime.Now, hours);
        var oldest = results.Min(r => r.FetchedAt);
        var result = new JsonObject
        {
            ["source"] = "solcast",
            ["fetchedAt"] = J.Iso(LocalTime.ToLocal(DateTimeOffset.FromUnixTimeMilliseconds((long)(oldest * 1000)), tz), seconds: true),
            ["stale"] = results.Any(r => r.Stale),
        };
        foreach (var (key, value) in shaped.ToList())
        {
            shaped.Remove(key);
            result[key] = value;
        }
        return result;
    }
}
