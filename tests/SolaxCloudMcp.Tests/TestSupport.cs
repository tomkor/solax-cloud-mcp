using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Tests change environment variables, which are process-wide
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SolaxCloudMcp.Tests;

/// <summary>Clears every app environment variable for the test and restores the real values afterwards.</summary>
public abstract class EnvTest : IDisposable
{
    static readonly string[] Names =
    [
        "SOLAX_CLIENT_ID", "SOLAX_CLIENT_SECRET", "SOLAX_DEVICE_SN", "SOLAX_ALLOW_WRITE", "TRANSPORT",
        "HTTP_API_KEY", "HTTP_HOST", "HTTP_PORT",
        "SOLCAST_API_KEY", "SOLCAST_RESOURCE_IDS", "SOLCAST_CACHE_MINUTES", "SOLCAST_CACHE_FILE", "SOLAR_TIMEZONE",
        "DECISIONS_FILE", "SETTINGS_FILE", "AUTOMATION_ENABLED", "AUTOMATION_DRY_RUN", "BATTERY_CAPACITY_KWH",
        "AUTOMATION_CONSUMPTION_PROFILE", "AUTOMATION_DAILY_CONSUMPTION_KWH", "AUTOMATION_WEEKEND_CONSUMPTION_PROFILE",
        "AUTOMATION_WEEKEND_DAILY_CONSUMPTION_KWH", "AUTOMATION_FORECAST_PERCENTILE", "AUTOMATION_MIN_SOC",
        "AUTOMATION_MAX_GRID_SOC", "AUTOMATION_EFFICIENCY", "AUTOMATION_CHARGE_POWER_KW", "AUTOMATION_SAFETY_MARGIN_KWH",
        "AUTOMATION_LEAD_MINUTES", "TARIFF_OFFPEAK_WINDOWS", "TARIFF_OFFPEAK_WINDOWS_SUMMER", "TARIFF_OFFPEAK_WEEKENDS",
        "TARIFF_OFFPEAK_HOLIDAYS", "EXPORT_ENABLED", "EXPORT_DRY_RUN", "EXPORT_MAX_POWER_KW", "EXPORT_MIN_PRICE_PLN_KWH",
        "EXPORT_MIN_SLOT_KWH", "EXPORT_PRICE_MULTIPLIER",
    ];

    readonly Dictionary<string, string?> _saved = Names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
    readonly List<string> _tempDirs = [];

    protected EnvTest()
    {
        foreach (var name in Names) Environment.SetEnvironmentVariable(name, null);
        SetEnv("SOLAX_CLIENT_ID", "test-client-id");
        SetEnv("SOLAX_CLIENT_SECRET", "test-client-secret");
    }

    protected static void SetEnv(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    protected string TempDir()
    {
        var dir = Directory.CreateTempSubdirectory("solax-tests-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    public virtual void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        foreach (var dir in _tempDirs) Directory.Delete(dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Stands in for the network: records requests and answers from a function.</summary>
public class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

    /// <summary>Answers in order; the last answer repeats.</summary>
    public static FakeHandler Sequence(params Func<HttpResponseMessage>[] responses)
    {
        var index = 0;
        return new FakeHandler(_ => responses[Math.Min(index++, responses.Length - 1)]());
    }

    public static HttpResponseMessage Json(string json, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return respond(request);
    }
}

public class FakeAuth(params string[] tokens) : SolaxAuth
{
    readonly Queue<string> _tokens = new(tokens);

    public int Invalidated { get; private set; }

    public override Task<string> GetAccessTokenAsync() => Task.FromResult(_tokens.Dequeue());

    public override void InvalidateToken() => Invalidated++;
}

/// <summary>Captures inverter commands instead of calling the SolaX API.</summary>
public class FakeClient() : SolaxClient(new SolaxAuth())
{
    public List<string> Commands { get; } = [];
    public List<(string DeviceSn, SelfUseMode Mode)> SelfUseCalls { get; } = [];
    public bool Fail { get; set; }
    public Func<string, (JsonObject, JsonObject?)> Realtime { get; set; } = _ => (new JsonObject(), null);

    JsonObject Command(string text, string deviceSn, bool canFail = true)
    {
        Commands.Add(text);
        if (canFail && Fail) throw new SolaxApiException("boom");
        return new JsonObject { ["code"] = 10000, ["result"] = new JsonObject { [deviceSn] = new JsonObject { ["status"] = 3 } } };
    }

    public override Task<JsonObject> DischargeToSocAsync(string deviceSn, int dischargeW, int stopSoc) =>
        Task.FromResult(Command($"discharge {deviceSn} {dischargeW} {stopSoc}", deviceSn));

    public override Task<JsonObject> ChargeToSocAsync(string deviceSn, int chargeW, int stopSoc) =>
        Task.FromResult(Command($"charge {deviceSn} {chargeW} {stopSoc}", deviceSn));

    public override Task<JsonObject> ExitVppModeAsync(string deviceSn) => Task.FromResult(Command($"exit {deviceSn}", deviceSn, canFail: false));

    public override Task<JsonObject> SetSelfUseModeAsync(string deviceSn, SelfUseMode mode)
    {
        SelfUseCalls.Add((deviceSn, mode));
        return Task.FromResult(new JsonObject { ["code"] = 10000 });
    }

    public override Task<(JsonObject Inverter, JsonObject? Battery)> FetchRealtimeDataAsync(string deviceSn) => Task.FromResult(Realtime(deviceSn));
}

public class FakeForecast : SolcastForecast
{
    public Func<int, JsonObject> Respond { get; set; } = _ => throw new SolcastException("no forecast");

    public override Task<JsonObject> GetSolarForecastAsync(int hours = 24) => Task.FromResult(Respond(hours));
}

public class FakePrices : RcePrices
{
    public Func<List<PriceSlot>> Respond { get; set; } = () => [];

    public override Task<List<PriceSlot>> GetRcePricesAsync(DateTimeOffset? now = null) => Task.FromResult(Respond());
}

/// <summary>Realtime data with a battery SOC from a list of readings; the last reading repeats.</summary>
public class FakeService(SolaxClient client, SolcastForecast forecast, RcePrices prices) : SolaxService(client, forecast, prices)
{
    public List<double> SocReadings { get; } = [];

    public override Task<JsonObject> GetRealtimeDataAsync(string? deviceSn = null)
    {
        var soc = SocReadings[0];
        if (SocReadings.Count > 1) SocReadings.RemoveAt(0);
        return Task.FromResult(new JsonObject { ["battery"] = new JsonObject { ["soc_percent"] = soc } });
    }
}

public static class JsonAssert
{
    /// <summary>Deep equality. Numbers are compared by value, so 5 equals 5.0.</summary>
    public static void Equal(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        switch (expected)
        {
            case null:
                Assert.True(actual is null, $"{path}: expected null, got {actual?.ToJsonString()}");
                break;
            case JsonObject e:
                var a = Assert.IsType<JsonObject>(actual, exactMatch: false);
                Assert.True(e.Select(p => p.Key).Order().SequenceEqual(a.Select(p => p.Key).Order()),
                    $"{path}: keys differ: expected [{string.Join(",", e.Select(p => p.Key))}], got [{string.Join(",", a.Select(p => p.Key))}]");
                foreach (var (key, value) in e) Equal(value, a[key], $"{path}.{key}");
                break;
            case JsonArray e:
                var array = Assert.IsType<JsonArray>(actual, exactMatch: false);
                Assert.True(e.Count == array.Count, $"{path}: expected {e.Count} items, got {array.Count}: {array.ToJsonString()}");
                for (var i = 0; i < e.Count; i++) Equal(e[i], array[i], $"{path}[{i}]");
                break;
            default:
                Assert.True(actual is JsonValue, $"{path}: expected {expected.ToJsonString()}, got {actual?.ToJsonString() ?? "null"}");
                var same = expected.GetValueKind() == JsonValueKind.Number && actual!.GetValueKind() == JsonValueKind.Number
                    ? J.Num(expected) == J.Num(actual)
                    : expected.ToJsonString() == actual!.ToJsonString();
                Assert.True(same, $"{path}: expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
                break;
        }
    }
}

public static class TestData
{
    public static readonly TimeZoneInfo Tz = LocalTime.Zone("Europe/Warsaw");

    public static readonly Tariff G12w = new([new(22, 6), new(13, 15)], Weekends: true, Holidays: true, SummerWindows: [new(22, 6), new(15, 17)]);

    public static readonly Settings Base = new(
        Tz, G12w, CapacityKwh: 21.2, ConsumptionProfile: Flat(1.0), MinSoc: 15, MaxGridSoc: 100, SafetyMarginKwh: 1.0,
        Efficiency: 0.9, Percentile: "p50", LeadMinutes: 10, DryRun: true);

    public static double[] Flat(double kwh) => Enumerable.Repeat(kwh, 24).ToArray();

    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        LocalTime.At(new DateTime(year, month, day, hour, minute, 0), Tz);

    public static JsonNode Load(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, path)))!;
}
