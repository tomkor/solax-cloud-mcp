using System.Text.Json.Nodes;

namespace SolaxCloudMcp.Tests;

/// <summary>Tests for configuration and environment handling.</summary>
public class ConfigTests : EnvTest
{
    [Fact]
    public void Client_id_is_returned_when_set()
    {
        SetEnv("SOLAX_CLIENT_ID", "test-client-id-12345");
        Assert.Equal("test-client-id-12345", Config.ClientId());
    }

    [Fact]
    public void Client_id_missing_throws()
    {
        SetEnv("SOLAX_CLIENT_ID", null);
        Assert.Contains("SOLAX_CLIENT_ID", Assert.Throws<ConfigException>(() => Config.ClientId()).Message);
    }

    [Fact]
    public void Client_secret_is_returned_when_set()
    {
        SetEnv("SOLAX_CLIENT_SECRET", "test-client-secret-12345");
        Assert.Equal("test-client-secret-12345", Config.ClientSecret());
    }

    [Fact]
    public void Client_secret_missing_throws()
    {
        SetEnv("SOLAX_CLIENT_SECRET", null);
        Assert.Contains("SOLAX_CLIENT_SECRET", Assert.Throws<ConfigException>(() => Config.ClientSecret()).Message);
    }

    [Fact]
    public void Default_device_sn_is_returned_when_set()
    {
        SetEnv("SOLAX_DEVICE_SN", "X3ABCD0123");
        Assert.Equal("X3ABCD0123", Config.DefaultDeviceSn());
    }

    [Fact]
    public void Default_device_sn_missing_is_null() => Assert.Null(Config.DefaultDeviceSn());

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    public void Write_tools_need_an_explicit_opt_in(string? value, bool expected)
    {
        SetEnv("SOLAX_ALLOW_WRITE", value);
        Assert.Equal(expected, Config.IsWriteEnabled());
    }

    /// <summary>Python round() rounds the exact value of the float, half to even. The planners depend on it.</summary>
    [Theory]
    [InlineData(2.675, 2, 2.67)] // stored as 2.67499999...
    [InlineData(0.125, 2, 0.12)] // exact tie -> even
    [InlineData(0.375, 2, 0.38)]
    [InlineData(1.005, 2, 1.0)]
    [InlineData(8.777777, 2, 8.78)]
    [InlineData(-0.125, 2, -0.12)]
    [InlineData(12.25, 1, 12.2)]
    [InlineData(1.23456, 4, 1.2346)]
    public void Round_matches_python(double value, int digits, double expected) => Assert.Equal(expected, J.Round(value, digits));
}

/// <summary>Tests for consumption profiles from SolaX history.</summary>
public class ConsumptionTests
{
    static JsonObject Sample(double? p1, double? p2, double? p3, double? grid) =>
        new() { ["acPower1"] = p1, ["acPower2"] = p2, ["acPower3"] = p3, ["gridPower"] = grid };

    [Fact]
    public void House_load_is_ac_output_minus_grid()
    {
        // Real sample, Sat 2026-10-03 12:50: 2084 W out, 1370 W exported
        Assert.Equal(714.0, Consumption.HouseLoadW(Sample(696, 690, 698, 1370.0)));
        Assert.Equal(900, Consumption.HouseLoadW(Sample(0, 0, 0, -900))); // importing
        Assert.Null(Consumption.HouseLoadW(Sample(100, null, 100, 0)));
    }

    static IEnumerable<(DateTime, double)> Day(DateTime start, Func<int, double> wattsByHour) =>
        Enumerable.Range(0, 24 * 12).Select(i => (start.AddMinutes(i * 5), wattsByHour(i * 5 / 60)));

    [Fact]
    public void Profiles_split_weekdays_weekends_and_holidays()
    {
        var samples = Day(new DateTime(2026, 10, 2), h => h is >= 11 and < 17 ? 1200 : 300) // Friday
            .Concat(Day(new DateTime(2026, 10, 3), _ => 2000)) // Saturday
            .Concat(Day(new DateTime(2026, 11, 11), _ => 1000)); // Wednesday, Independence Day
        var profiles = Consumption.HourlyProfiles(samples);
        Assert.Equal(1, J.Num(profiles["weekday"]!["days"]));
        Assert.Equal([0.3, 1.2], profiles["weekday"]!["profile"]!.AsArray().Skip(10).Take(2).Select(J.Num));
        Assert.Equal(12.6, J.Num(profiles["weekday"]!["daily_kWh"])); // 6 * 1.2 + 18 * 0.3
        Assert.Equal(2, J.Num(profiles["weekend"]!["days"]));
        Assert.Equal(1.5, J.Num(profiles["weekend"]!["profile"]![0])); // average of 2.0 and 1.0
    }

    [Fact]
    public void Missing_hours_throw() => Assert.Contains("longer period",
        Assert.Throws<ArgumentException>(() => Consumption.HourlyProfiles([(new DateTime(2026, 10, 2, 12, 0, 0), 500.0)])).Message);
}

/// <summary>Tests for PSE RCE price parsing and fetching.</summary>
public class PricesTests
{
    static JsonArray Records(params (string Dtime, double Price, string Day)[] records) =>
        J.Array(records.Select(r => new JsonObject { ["dtime"] = r.Dtime, ["rce_pln"] = r.Price, ["business_date"] = r.Day }));

    static DateTimeOffset Warsaw(int month, int day, int hour, int minute = 0) =>
        LocalTime.At(new DateTime(2026, month, day, hour, minute, 0), RcePrices.PseTz);

    [Fact]
    public void Dtime_is_the_period_end_in_local_time()
    {
        var slot = RcePrices.ParseRecords(Records(("2026-10-05 18:15:00", 1234.5, "2026-10-05")))[0];
        Assert.Equal(Warsaw(10, 5, 18), slot.Start);
        Assert.Equal(Warsaw(10, 5, 18, 15), slot.End);
        Assert.Equal(1.2345, slot.PricePlnKwh, 10);
    }

    [Fact]
    public void Hour_24_means_next_midnight() =>
        Assert.Equal(Warsaw(10, 6, 0), RcePrices.ParseRecords(Records(("2026-10-05 24:00:00", 300, "2026-10-05")))[0].End);

    [Fact]
    public void Autumn_dst_repeated_hour_gets_distinct_instants()
    {
        // 2026-10-25: 02:00-03:00 local occurs twice
        var slots = RcePrices.ParseRecords(Records(("2026-10-25 02:15:00", 100, "2026-10-25"), ("2026-10-25 02:15:00", 200, "2026-10-25")));
        Assert.Equal(TimeSpan.FromHours(1), slots[1].End - slots[0].End);
        Assert.Equal(TimeSpan.FromMinutes(15), slots[1].End - slots[1].Start);
    }

    const string OneRecord = """{"value": [{"dtime": "2026-10-05 18:15:00", "rce_pln": 1000, "business_date": "2026-10-05"}]}""";

    [Fact]
    public async Task Fetch_uses_the_date_filter_and_caches()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(OneRecord));
        var prices = new RcePrices(handler);
        var now = Warsaw(10, 5, 12);
        var first = await prices.GetRcePricesAsync(now);
        await prices.GetRcePricesAsync(now);
        Assert.Single(handler.Calls);
        var url = Uri.UnescapeDataString(handler.Calls[0].Request.RequestUri!.Query);
        Assert.StartsWith(RcePrices.RceUrl, handler.Calls[0].Request.RequestUri!.ToString());
        Assert.Contains("$filter=business_date ge '2026-10-05'", url);
        Assert.Equal(1.0, first[0].PricePlnKwh);
    }

    [Fact]
    public async Task Fetch_error_without_cache_throws() =>
        await Assert.ThrowsAsync<PriceException>(() => new RcePrices(new FakeHandler(_ => FakeHandler.Json("", 503))).GetRcePricesAsync());
}

/// <summary>Tests for the SolaX API client, focused on the 10402 retry path and remote control.</summary>
public class ClientTests
{
    static SolaxClient Client(FakeAuth auth, FakeHandler handler) => new(auth, handler) { MinInterval = TimeSpan.Zero };

    /// <summary>A rejected token (10402) must be dropped, and the same call retried once with a fresh token.</summary>
    [Fact]
    public async Task Code_10402_invalidates_the_token_and_retries()
    {
        var auth = new FakeAuth("expired-token", "fresh-token");
        var handler = FakeHandler.Sequence(
            () => FakeHandler.Json("""{"code": 10402, "message": "auth failed", "result": []}"""),
            () => FakeHandler.Json("""{"code": 10000, "message": "ok", "result": [{"deviceSn": "X3ABCD0123"}]}"""));

        var result = await Client(auth, handler).FetchInverterDataAsync("X3ABCD0123");

        Assert.Equal("""{"deviceSn":"X3ABCD0123"}""", result.ToJsonString());
        Assert.Equal(1, auth.Invalidated);
        Assert.Equal(2, handler.Calls.Count);
        Assert.StartsWith(SolaxClient.RealtimeDataUrl, handler.Calls[0].Request.RequestUri!.ToString());
        Assert.Equal(["bearer expired-token", "bearer fresh-token"], handler.Calls.Select(c => c.Request.Headers.GetValues("Authorization").Single()));
    }

    [Fact]
    public async Task Persistent_10402_after_retry_throws_api_error()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"code": 10402, "message": "auth failed", "result": []}"""));
        var error = await Assert.ThrowsAsync<SolaxApiException>(() => Client(new FakeAuth("expired-token", "still-bad-token"), handler).FetchInverterDataAsync("X3ABCD0123"));
        Assert.Contains("10402", error.Message);
    }

    [Fact]
    public async Task Discharge_sends_negative_power_and_stop_soc()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"code": 10000, "result": {"SN1": {"status": 3}}}"""));

        await Client(new FakeAuth("token"), handler).DischargeToSocAsync("SN1", 1500, 85);

        Assert.Equal(SolaxClient.SocTargetUrl, handler.Calls[0].Request.RequestUri!.ToString());
        Assert.Contains("\"chargeDischargPower\":-1500", handler.Calls[0].Body);
        Assert.Contains("\"targetSoc\":85", handler.Calls[0].Body);
        Assert.Contains("\"snList\":[\"SN1\"]", handler.Calls[0].Body);
    }

    [Fact]
    public async Task Charge_sends_positive_power()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"code": 10000, "result": {"SN1": {"status": 4}}}"""));
        await Client(new FakeAuth("token"), handler).ChargeToSocAsync("SN1", 5000, 60);
        Assert.Contains("\"chargeDischargPower\":5000", handler.Calls[0].Body);
    }

    /// <summary>Code 10000 only means the platform accepted the request; an offline or failed device must throw.</summary>
    [Theory]
    [InlineData("""{"SN1": {"status": 1}}""")]
    [InlineData("""{"SN1": {"status": 2}}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task Command_not_delivered_throws(string result)
    {
        var handler = new FakeHandler(_ => FakeHandler.Json($$"""{"code": 10000, "result": {{result}}}"""));
        var error = await Assert.ThrowsAsync<SolaxApiException>(() => Client(new FakeAuth("token"), handler).ExitVppModeAsync("SN1"));
        Assert.Contains("not delivered", error.Message);
        Assert.Equal(SolaxClient.ExitVppUrl, handler.Calls[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Calls_are_spaced_by_the_minimum_interval()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"code": 10000, "result": []}"""));
        var client = new SolaxClient(new FakeAuth("a", "b"), handler) { MinInterval = TimeSpan.FromMilliseconds(150) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await client.FetchHistoryAsync("SN1", 0, 1);
        await client.FetchHistoryAsync("SN1", 0, 1);
        Assert.True(watch.ElapsedMilliseconds >= 140, $"second call came after {watch.ElapsedMilliseconds} ms");
    }
}

/// <summary>Tests for the OAuth2 token cache.</summary>
public class AuthTests : EnvTest
{
    [Fact]
    public async Task Token_is_fetched_once_then_cached_until_invalidated()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"code": 0, "result": {"access_token": "tok", "expires_in": 3600}}"""));
        var auth = new SolaxAuth(handler);
        Assert.Equal("tok", await auth.GetAccessTokenAsync());
        await auth.GetAccessTokenAsync();
        Assert.Single(handler.Calls);
        Assert.Equal(SolaxAuth.TokenUrl, handler.Calls[0].Request.RequestUri!.ToString());
        Assert.Contains("client_id=test-client-id", handler.Calls[0].Body);
        Assert.Contains("grant_type=client_credentials", handler.Calls[0].Body);

        auth.InvalidateToken();
        await auth.GetAccessTokenAsync();
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData("""{"code": 10401, "message": "bad secret"}""", 200)] // the token endpoint uses code 0 for success
    [InlineData("""{"code": 0, "result": {"expires_in": 3600}}""", 200)]
    [InlineData("oops", 500)]
    public async Task Bad_token_answers_throw(string body, int status) =>
        await Assert.ThrowsAsync<SolaxAuthException>(() => new SolaxAuth(new FakeHandler(_ => FakeHandler.Json(body, status))).GetAccessTokenAsync());
}
