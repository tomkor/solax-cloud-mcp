using System.Globalization;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

/// <summary>Raised when RCE prices cannot be fetched and no cached data is available.</summary>
public class PriceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One 15-minute price period. The price is net RCE in PLN/kWh.</summary>
public record PriceSlot(DateTimeOffset Start, DateTimeOffset End, double PricePlnKwh);

/// <summary>Polish market energy prices (RCE, 15-minute resolution) from the public PSE API.</summary>
public class RcePrices
{
    public const string RceUrl = "https://api.raporty.pse.pl/api/rce-pln";
    public static readonly TimeZoneInfo PseTz = LocalTime.Zone("Europe/Warsaw"); // PSE publishes local (Polish) time
    const double CacheSeconds = 30 * 60;

    static ILogger Log => AppLog.For<RcePrices>();

    readonly HttpClient _http;
    readonly SemaphoreSlim _cacheLock = new(1, 1);
    (double FetchedAt, List<PriceSlot> Slots)? _cache;

    public RcePrices(HttpMessageHandler? handler = null)
    {
        _http = Http.Create(handler, 15);
    }

    /// <summary>Parse PSE records. `dtime` is the local END of the 15-min period, possibly "YYYY-MM-DD 24:00:00".</summary>
    public static List<PriceSlot> ParseRecords(JsonArray records)
    {
        var slots = new List<PriceSlot>();
        DateTime? previousWall = null;
        var sorted = records
            .Select(r => (BusinessDate: J.Str(r!["business_date"])!, Time: J.Str(r["dtime"])!, Price: J.Num(r["rce_pln"]) ?? throw new FormatException("rce_pln is not a number")))
            .OrderBy(r => r.BusinessDate, StringComparer.Ordinal).ThenBy(r => r.Time, StringComparer.Ordinal);
        foreach (var (_, time, price) in sorted)
        {
            var parts = time.Split(' ', 2);
            var wall = parts[1].StartsWith("24:")
                ? DateTime.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1)
                : DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.None);
            var end = LocalTime.At(wall, PseTz);
            // Autumn DST change repeats local times; the second occurrence is the later instant
            if (previousWall is { } previous && wall <= previous && PseTz.IsAmbiguousTime(wall))
                end = new DateTimeOffset(wall, PseTz.GetAmbiguousTimeOffsets(wall).Min());
            previousWall = wall;
            slots.Add(new PriceSlot(end.AddMinutes(-15), end, price / 1000));
        }
        return slots;
    }

    /// <summary>RCE prices from today (local) onwards; tomorrow's appear once PSE publishes them (afternoon).</summary>
    public virtual async Task<List<PriceSlot>> GetRcePricesAsync(DateTimeOffset? now = null)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (_cache is { } fresh && LocalTime.Epoch - fresh.FetchedAt < CacheSeconds) return fresh.Slots;

            var today = LocalTime.ToLocal(now ?? LocalTime.Now, PseTz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = $"{RceUrl}?{Uri.EscapeDataString("$select")}={Uri.EscapeDataString("dtime,period,rce_pln,business_date")}" +
                      $"&{Uri.EscapeDataString("$filter")}={Uri.EscapeDataString($"business_date ge '{today}'")}&{Uri.EscapeDataString("$first")}=200";
            List<PriceSlot> slots;
            try
            {
                using var response = await _http.GetAsync(url);
                var text = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode != 200) throw new PriceException($"HTTP {(int)response.StatusCode}: {text[..Math.Min(200, text.Length)]}");
                slots = ParseRecords((JsonNode.Parse(text) as JsonObject)?["value"] as JsonArray ?? throw new FormatException("no 'value' list"));
            }
            catch (Exception e)
            {
                // A cancelled request here is the HttpClient timeout: no cancellation token is passed in
                var error = e as PriceException ?? new PriceException($"Failed to fetch RCE prices: {e.Message}", e);
                if (_cache is not { } stale) throw error;
                Log.LogWarning("{Error}; serving cached prices", error.Message);
                return stale.Slots;
            }

            _cache = (LocalTime.Epoch, slots);
            return slots;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
