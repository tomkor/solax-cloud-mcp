using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace SolaxCloudMcp;

/// <summary>One logger factory for the whole app, set once at startup. Tests keep the silent default.</summary>
public static class AppLog
{
    public static ILoggerFactory Factory { get; set; } = NullLoggerFactory.Instance;

    public static ILogger For<T>() => Factory.CreateLogger<T>();
}

public static class Http
{
    /// <summary>HttpClient with a timeout. Tests pass a fake handler instead of the network.</summary>
    public static HttpClient Create(HttpMessageHandler? handler, double timeoutSeconds)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        // Some API gateways reject requests that have no User-Agent
        http.DefaultRequestHeaders.UserAgent.ParseAdd("solax-cloud-mcp/0.1.0");
        return http;
    }
}

/// <summary>JSON and number helpers. API payloads stay as JSON nodes, so the output keys match the old server 1:1.</summary>
public static class J
{
    public static double? Num(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.Deserialize<double>() : null;

    public static string? Str(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool IsTrue(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    public static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

    public static JsonArray Array(IEnumerable<JsonNode?> items) => new(items.ToArray());

    /// <summary>Round half to even on the exact decimal value of the double, like Python round(x, digits).
    /// Math.Round(x, digits) multiplies first and gives another result for values such as 2.675.</summary>
    public static double Round(double x, int digits = 2)
    {
        if (!double.IsFinite(x)) return x;
        // An exact tie has the form odd / 2^(digits+1); multiplication by a power of two is exact
        var scaled = x * Math.Pow(2, digits + 1);
        if (scaled == Math.Floor(scaled) && Math.Abs(scaled % 2) == 1) return Math.Round(x, digits, MidpointRounding.ToEven);
        return double.Parse(x.ToString("F" + digits, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>ISO 8601 with offset, like Python isoformat(timespec="minutes" / "seconds").</summary>
    public static string Iso(DateTimeOffset t, bool seconds = false) =>
        t.ToString(seconds ? "yyyy-MM-dd'T'HH:mm:sszzz" : "yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture);

    static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    public static DateTimeOffset ParseIso(string text) =>
        DateTimeOffset.ParseExact(text, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}

/// <summary>Local wall-clock time in a time zone. It follows the rules the old Python server used
/// (aware datetime arithmetic is done on the wall clock), so plans are the same on DST change days too.</summary>
public static class LocalTime
{
    public static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    /// <summary>Wall-clock time to an instant. A repeated hour (autumn) gives the first occurrence;
    /// a skipped hour (spring) uses the offset from before the change.</summary>
    public static DateTimeOffset At(DateTime wall, TimeZoneInfo tz)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        var offset = tz.IsAmbiguousTime(wall) ? tz.GetAmbiguousTimeOffsets(wall).Max()
            : tz.IsInvalidTime(wall) ? tz.GetUtcOffset(wall.AddHours(-1))
            : tz.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }

    public static DateTimeOffset ToLocal(DateTimeOffset instant, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(instant, tz);

    /// <summary>Add to the wall clock of a local time (22:00 + 8 h is 06:00, also across a DST change).</summary>
    public static DateTimeOffset AddWall(DateTimeOffset local, TimeSpan delta, TimeZoneInfo tz) => At(local.DateTime + delta, tz);

    public static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public static double Epoch => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
