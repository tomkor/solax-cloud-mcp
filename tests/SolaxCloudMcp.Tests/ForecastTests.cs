using System.Net;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp.Tests;

/// <summary>Tests for the Solcast forecast client: aggregation, multi-site merge, caching.</summary>
public class ForecastTests : EnvTest
{
    const string UrlA = "https://api.solcast.com.au/rooftop_sites/site-a/forecasts";
    const string UrlB = "https://api.solcast.com.au/rooftop_sites/site-b/forecasts";

    public ForecastTests()
    {
        SetEnv("SOLCAST_API_KEY", "secret-key");
        SetEnv("SOLCAST_RESOURCE_IDS", "site-a");
        SetEnv("SOLAR_TIMEZONE", "Europe/Warsaw");
    }

    static string Forecasts(string end, double p50) =>
        new JsonObject
        {
            ["forecasts"] = new JsonArray(new JsonObject
            {
                ["period_end"] = end, ["period"] = "PT30M", ["pv_estimate"] = p50, ["pv_estimate10"] = p50 / 2, ["pv_estimate90"] = p50 * 2,
            }),
        }.ToJsonString();

    static double? P50(JsonObject forecast) => J.Num(forecast["days"]![0]!["energy_kWh"]!["p50"]);

    [Fact]
    public void Shape_groups_by_local_day_and_hour()
    {
        var now = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero); // 10:00 local
        ForecastPeriod Period(int month, int day, int hour, int minute, double p50, double p10, double p90) =>
            new(new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.Zero), p50, p10, p90);
        ForecastPeriod[] periods =
        [
            // 2026-06-30 22:00-22:30 UTC = 2026-07-01 00:00 local -> belongs to July 1st, already past
            Period(6, 30, 22, 30, 1.0, 0.5, 2.0),
            Period(7, 1, 8, 30, 4.0, 2.0, 5.0),
            Period(7, 1, 9, 0, 2.0, 1.0, 3.0),
            Period(7, 2, 10, 0, 6.0, 3.0, 7.0),
        ];

        var shaped = SolcastForecast.ShapeForecast(periods, TestData.Tz, now, hours: 2);

        Assert.Equal(["2026-07-01", "2026-07-02"], shaped["days"]!.AsArray().Select(d => J.Str(d!["date"])));
        Assert.Equal("""{"p10":1.75,"p50":3.5,"p90":5}""", shaped["days"]![0]!["energy_kWh"]!.ToJsonString());
        Assert.Equal(4.0, J.Num(shaped["days"]![0]!["peakPower_kW"]));
        Assert.Equal("2026-07-01T10:00+02:00", J.Str(shaped["days"]![0]!["peakTime"]));
        Assert.Equal("""{"p10":1.5,"p50":3,"p90":4}""", shaped["remainingToday_kWh"]!.ToJsonString());
        // Two half-hours inside 10:00 local are averaged into one hourly point
        JsonAssert.Equal(JsonNode.Parse("""[{"time": "2026-07-01T10:00+02:00", "power_kW": {"p10": 1.5, "p50": 3.0, "p90": 4.0}}]"""), shaped["hourly"]);
    }

    [Fact]
    public void Period_end_with_seven_fractional_digits_is_parsed() =>
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 30, 0, TimeSpan.Zero), SolcastForecast.ParsePeriodEnd("2026-07-01T08:30:00.0000000Z"));

    [Fact]
    public async Task Sums_sites_and_sends_the_key_in_a_header()
    {
        SetEnv("SOLCAST_RESOURCE_IDS", "site-a, site-b");
        const string end = "2099-01-01T12:00:00.0000000Z";
        var handler = new FakeHandler(request => FakeHandler.Json(Forecasts(end, request.RequestUri!.AbsolutePath.Contains("site-a") ? 1.0 : 2.0)));

        var result = await new SolcastForecast(handler).GetSolarForecastAsync(24);

        Assert.Equal(1.5, P50(result)); // (1 + 2) kW * 0.5 h
        Assert.False(J.IsTrue(result["stale"]));
        Assert.Equal("solcast", J.Str(result["source"]));
        var (requestA, requestB) = (handler.Calls[0].Request, handler.Calls[1].Request);
        Assert.StartsWith(UrlA, requestA.RequestUri!.ToString());
        Assert.StartsWith(UrlB, requestB.RequestUri!.ToString());
        Assert.Equal("Bearer secret-key", requestA.Headers.GetValues("Authorization").Single());
        Assert.DoesNotContain("secret-key", requestA.RequestUri.ToString());
    }

    [Fact]
    public async Task Cache_avoids_a_second_call()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(Forecasts("2099-01-01T12:00:00Z", 1.0)));
        var forecast = new SolcastForecast(handler);
        await forecast.GetSolarForecastAsync();
        await forecast.GetSolarForecastAsync();
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Serves_stale_cache_on_rate_limit()
    {
        SetEnv("SOLCAST_CACHE_MINUTES", "0");
        var forecast = new SolcastForecast(FakeHandler.Sequence(
            () => FakeHandler.Json(Forecasts("2099-01-01T12:00:00Z", 1.0)),
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        await forecast.GetSolarForecastAsync();
        var result = await forecast.GetSolarForecastAsync();
        Assert.True(J.IsTrue(result["stale"]));
        Assert.Equal(0.5, P50(result));
    }

    [Fact]
    public async Task Error_without_cache_throws()
    {
        var forecast = new SolcastForecast(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Contains("rate limit", (await Assert.ThrowsAsync<SolcastException>(() => forecast.GetSolarForecastAsync())).Message);
    }

    /// <summary>A restart must not spend one of the ~10 daily Solcast calls.</summary>
    [Fact]
    public async Task Cache_file_survives_a_restart()
    {
        var path = Path.Combine(TempDir(), "solcast.json");
        SetEnv("SOLCAST_CACHE_FILE", path);
        var handler = new FakeHandler(_ => FakeHandler.Json(Forecasts("2099-01-01T12:00:00Z", 1.0)));
        await new SolcastForecast(handler).GetSolarForecastAsync();

        // A new process: empty memory cache, file not loaded yet
        var result = await new SolcastForecast(handler).GetSolarForecastAsync();

        Assert.Single(handler.Calls);
        Assert.Equal(0.5, P50(result));
        // The file keeps the format of the Python server: {siteId: [epoch, periods]}
        var saved = JsonNode.Parse(File.ReadAllText(path))!["site-a"]!.AsArray();
        Assert.True(J.Num(saved[0]) > 1_700_000_000);
        Assert.Equal("2099-01-01T12:00:00Z", J.Str(saved[1]![0]!["period_end"]));
    }

    /// <summary>A cache file written by the Python server must still load after the switch to C#.</summary>
    [Fact]
    public async Task Cache_file_from_the_python_server_is_read()
    {
        var path = Path.Combine(TempDir(), "solcast.json");
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        File.WriteAllText(path, $$"""{"site-a": [{{epoch}}.123456, [{"period_end": "2099-01-01T12:00:00.0000000Z", "period": "PT30M", "pv_estimate": 1.0, "pv_estimate10": 0.5, "pv_estimate90": 2.0}]]}""");
        SetEnv("SOLCAST_CACHE_FILE", path);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not call Solcast"));

        var result = await new SolcastForecast(handler).GetSolarForecastAsync();

        Assert.Empty(handler.Calls);
        Assert.Equal(0.5, P50(result));
    }
}
