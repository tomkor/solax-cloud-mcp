using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace SolaxCloudMcp.Tests;

/// <summary>Tests for HTTP server auth, input validation and error mapping, and for the MCP tool list.</summary>
public class HttpServerTests : EnvTest
{
    const string ApiKey = "test-api-key";
    const string ValidBody = """{"device_sn": "SN1", "min_soc": 20, "charge_upper_soc": 80, "charge_from_grid_enable": 0}""";

    readonly FakeClient _client = new();
    readonly FakeForecast _forecast = new();
    readonly FakePrices _prices = new();
    WebApplication? _app;

    public HttpServerTests() => SetEnv("HTTP_API_KEY", ApiKey);

    public override void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        base.Dispose();
    }

    WebApplication CreateApp() => _app = HttpServer.Create(new Backends(_client, _forecast, _prices), builder => builder.WebHost.UseTestServer());

    /// <summary>Send a request to a started app. auth: "key" = the right key, null = no header, other = that header value.</summary>
    async Task<(int Status, string Text, JsonNode? Json, string? ContentType)> Send(string method, string path, string? body = null, string? auth = "key", bool confirm = false)
    {
        if (_app is null) await CreateApp().StartAsync();
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (auth is not null) request.Headers.TryAddWithoutValidation("Authorization", auth == "key" ? $"Bearer {ApiKey}" : auth);
        if (confirm) request.Headers.Add("X-Confirm", "yes");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _app!.GetTestClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        return ((int)response.StatusCode, text, contentType == "application/json" ? JsonNode.Parse(text) : null, contentType);
    }

    void EnableAutomation()
    {
        SetEnv("AUTOMATION_ENABLED", "1");
        SetEnv("SOLCAST_API_KEY", "k");
        SetEnv("SOLCAST_RESOURCE_IDS", "site-a");
        SetEnv("SOLAX_DEVICE_SN", "SN1");
        SetEnv("BATTERY_CAPACITY_KWH", "21.2");
        SetEnv("AUTOMATION_DAILY_CONSUMPTION_KWH", "12");
    }

    List<string?> ToolNames() => McpTools.Create(new SolaxService(_client, _forecast, _prices)).Select(t => t.ProtocolTool.Name).ToList<string?>();

    [Fact]
    public async Task Health_is_public() => Assert.Equal("""{"status":"ok"}""", (await Send("GET", "/health", auth: null)).Text);

    [Theory]
    [InlineData("/docs")]
    [InlineData("/redoc")]
    [InlineData("/openapi.json")]
    public async Task Api_docs_are_not_served(string path) => Assert.Equal(404, (await Send("GET", path)).Status);

    /// <summary>Invalid body on purpose: callers with no valid key must get 403, not 422 with field details.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("Basic " + ApiKey)]
    [InlineData(ApiKey)]
    public async Task Rejects_bad_auth_before_body_validation(string? auth)
    {
        Assert.Equal(403, (await Send("POST", "/api/battery/self-use-mode", "{}", auth)).Status);
        Assert.Empty(_client.SelfUseCalls);
    }

    [Fact]
    public async Task Self_use_mode_requires_explicit_settings()
    {
        Assert.Equal(422, (await Send("POST", "/api/battery/self-use-mode", """{"device_sn": "SN1"}""")).Status);
        // A missing field must never pass as 0
        Assert.Equal(422, (await Send("POST", "/api/battery/self-use-mode", """{"device_sn": "SN1", "min_soc": 20, "charge_upper_soc": 80}""")).Status);
        Assert.Equal(422, (await Send("POST", "/api/battery/self-use-mode", "not json")).Status);
        Assert.Empty(_client.SelfUseCalls);
    }

    [Fact]
    public async Task Self_use_mode_passes_explicit_settings()
    {
        var response = await Send("POST", "/api/battery/self-use-mode", ValidBody);
        Assert.Equal(200, response.Status);
        var (deviceSn, mode) = Assert.Single(_client.SelfUseCalls);
        Assert.Equal(("SN1", 20, 80, 0, 0), (deviceSn, mode.MinSoc, mode.ChargeUpperSoc, mode.ChargeFromGridEnable, mode.EnableTimePeriod2));
        Assert.Null(mode.ChargeStartTimePeriod1);
    }

    [Fact]
    public async Task Self_use_mode_passes_time_periods()
    {
        var body = ValidBody.Replace("}", """, "charge_start_time_period1": "06:00", "charge_end_time_period1": "18:30", "enable_time_period2": 1}""");
        Assert.Equal(200, (await Send("POST", "/api/battery/self-use-mode", body)).Status);
        var mode = _client.SelfUseCalls[0].Mode;
        Assert.Equal(("06:00", "18:30", 1), (mode.ChargeStartTimePeriod1, mode.ChargeEndTimePeriod1, mode.EnableTimePeriod2));
    }

    [Theory]
    [InlineData("6:00")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("12:00; rm")]
    [InlineData("12:00\\n")]
    [InlineData("")]
    public async Task Self_use_mode_rejects_bad_time_format(string value)
    {
        var body = ValidBody.Replace("}", $$""", "charge_start_time_period1": "{{value}}"}""");
        Assert.Equal(422, (await Send("POST", "/api/battery/self-use-mode", body)).Status);
        Assert.Empty(_client.SelfUseCalls);
    }

    [Fact]
    public async Task Upstream_error_details_are_not_leaked()
    {
        _client.Realtime = _ => throw new SolaxApiException("HTTP 500: <internal upstream body>");
        var response = await Send("POST", "/api/realtime-data", """{"device_sn": "SN1"}""");
        Assert.Equal(502, response.Status);
        Assert.DoesNotContain("internal upstream body", response.Text);
    }

    [Fact]
    public async Task Realtime_data_is_shaped()
    {
        _client.Realtime = sn => (new JsonObject { ["deviceSn"] = sn, ["deviceStatus"] = 102 }, new JsonObject { ["batterySOC"] = 61 });
        // The dashboard posts an empty object and relies on SOLAX_DEVICE_SN
        SetEnv("SOLAX_DEVICE_SN", "SN9");
        var response = await Send("POST", "/api/realtime-data", "{}");
        Assert.Equal(200, response.Status);
        Assert.Equal("SN9", J.Str(response.Json!["device"]!["deviceSn"]));
        Assert.Equal("Normal", J.Str(response.Json["status"]!["description"]));
        Assert.Equal(61, J.Num(response.Json["battery"]!["soc_percent"]));
    }

    [Fact]
    public async Task Validation_error_detail_is_returned()
    {
        var response = await Send("POST", "/api/battery/self-use-mode", ValidBody.Replace("20", "90").Replace("80", "50"));
        Assert.Equal(400, response.Status);
        Assert.Contains("min_soc", J.Str(response.Json!["detail"]));
    }

    [Fact]
    public void Default_bind_host_is_loopback()
    {
        Assert.Equal("http://127.0.0.1:8000", HttpServer.ListenUrl());
        SetEnv("HTTP_HOST", "0.0.0.0");
        SetEnv("HTTP_PORT", "9000");
        Assert.Equal("http://0.0.0.0:9000", HttpServer.ListenUrl());
    }

    [Fact]
    public void Http_api_key_is_required() => Assert.Throws<ConfigException>(() =>
    {
        SetEnv("HTTP_API_KEY", null);
        CreateApp();
    });

    [Fact]
    public void Write_tool_is_hidden_by_default()
    {
        Assert.Contains("get_realtime_data", ToolNames());
        Assert.Contains("get_energy_prices", ToolNames());
        Assert.DoesNotContain("set_battery_self_use_mode", ToolNames());
    }

    [Fact]
    public void Write_tool_is_exposed_when_enabled()
    {
        SetEnv("SOLAX_ALLOW_WRITE", "1");
        Assert.Contains("set_battery_self_use_mode", ToolNames());
    }

    /// <summary>The MCP argument names are part of the contract with the model: they must stay snake_case.</summary>
    [Fact]
    public void Tool_arguments_keep_their_names()
    {
        SetEnv("SOLAX_ALLOW_WRITE", "1");
        var tool = McpTools.Create(new SolaxService(_client, _forecast, _prices)).Single(t => t.ProtocolTool.Name == "set_battery_self_use_mode");
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
        Assert.Equal(13, schema["properties"]!.AsObject().Count);
        Assert.Contains("charge_start_time_period1", schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["min_soc", "charge_upper_soc", "charge_from_grid_enable"], schema["required"]!.AsArray().Select(J.Str));
    }

    [Fact]
    public async Task Forecast_endpoint_requires_auth() => Assert.Equal(403, (await Send("GET", "/api/solar-forecast", auth: null)).Status);

    [Fact]
    public async Task Forecast_endpoint_not_configured() => Assert.Equal(503, (await Send("GET", "/api/solar-forecast")).Status);

    [Fact]
    public async Task Forecast_endpoint_hides_the_upstream_error()
    {
        SetEnv("SOLCAST_API_KEY", "k");
        SetEnv("SOLCAST_RESOURCE_IDS", "site-a");
        _forecast.Respond = _ => throw new SolcastException("HTTP 500: <solcast body>");
        var response = await Send("GET", "/api/solar-forecast");
        Assert.Equal(502, response.Status);
        Assert.DoesNotContain("solcast body", response.Text);
    }

    [Fact]
    public async Task Forecast_endpoint_validates_hours()
    {
        SetEnv("SOLCAST_API_KEY", "k");
        SetEnv("SOLCAST_RESOURCE_IDS", "site-a");
        _forecast.Respond = hours => new JsonObject { ["hours"] = hours };
        Assert.Equal(422, (await Send("GET", "/api/solar-forecast?hours=500")).Status);
        Assert.Equal(422, (await Send("GET", "/api/solar-forecast?hours=abc")).Status);
        Assert.Equal("""{"hours":168}""", (await Send("GET", "/api/solar-forecast?hours=168")).Text);
        Assert.Equal("""{"hours":24}""", (await Send("GET", "/api/solar-forecast")).Text);
    }

    [Fact]
    public void Forecast_tool_is_registered_only_when_configured()
    {
        Assert.DoesNotContain("get_solar_forecast", ToolNames());
        SetEnv("SOLCAST_API_KEY", "k");
        SetEnv("SOLCAST_RESOURCE_IDS", "site-a");
        Assert.Contains("get_solar_forecast", ToolNames());
    }

    [Fact]
    public async Task Automation_status_disabled()
    {
        Assert.Equal("""{"enabled":false}""", (await Send("GET", "/api/automation")).Text);
        Assert.Equal(503, (await Send("POST", "/api/automation/preview")).Status);
        Assert.Equal(403, (await Send("GET", "/api/automation", auth: null)).Status);
    }

    [Fact]
    public void Automation_enabled_requires_solcast()
    {
        SetEnv("AUTOMATION_ENABLED", "1");
        Assert.Contains("SOLCAST", Assert.Throws<ConfigException>(CreateApp).Message);
    }

    [Fact]
    public async Task Automation_status_enabled_runs_the_scheduler()
    {
        EnableAutomation();
        var body = (await Send("GET", "/api/automation")).Json!;
        Assert.True(J.IsTrue(body["enabled"]));
        Assert.True(J.IsTrue(body["dryRun"]));
        Assert.NotNull(body["nextRun"]);
        Assert.Equal("""{"enabled":false}""", body["export"]!.ToJsonString());
        Assert.Empty(_client.Commands); // dry run: nothing is sent to the inverter
    }

    /// <summary>Live mode through the web host: commands go out, and server stop leaves remote control.</summary>
    [Fact]
    public async Task Live_automation_exits_remote_control_when_the_server_stops()
    {
        EnableAutomation();
        SetEnv("AUTOMATION_DRY_RUN", "0");
        SetEnv("EXPORT_ENABLED", "1");
        SetEnv("EXPORT_DRY_RUN", "0");
        SetEnv("EXPORT_MAX_POWER_KW", "5");
        var now = DateTimeOffset.UtcNow;
        _client.Realtime = _ => (new JsonObject(), new JsonObject { ["batterySOC"] = 100 });
        _forecast.Respond = _ => JsonNode.Parse("""{"fetchedAt": "2026-10-06T21:00:00+02:00", "stale": false, "hourly": []}""")!.AsObject();
        _prices.Respond = () => [new PriceSlot(now.AddMinutes(-20), now.AddMinutes(20), 5.0)];

        JsonNode? status = null;
        for (var i = 0; i < 100 && status?["export"]?["lastPlan"] is null; i++)
        {
            await Task.Delay(20);
            status = (await Send("GET", "/api/automation")).Json;
        }
        await _app!.StopAsync();

        Assert.False(J.IsTrue(status!["dryRun"]));
        Assert.Equal("discharge", J.Str(status["export"]!["lastPlan"]!["execution"]!["action"]));
        // Start: leave remote control first (state unknown after a restart). Stop: leave it again.
        Assert.Equal("exit SN1", _client.Commands[0]);
        Assert.Contains(_client.Commands, c => c.StartsWith("discharge SN1 "));
        Assert.Equal("exit SN1", _client.Commands[^1]);
    }

    [Fact]
    public async Task Prices_endpoint()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        _prices.Respond = () => [new PriceSlot(start, start.AddMinutes(15), 1.1)];
        var body = (await Send("GET", "/api/prices?hours=2")).Json!;
        Assert.Equal(1.1, J.Num(body["max"]));
        Assert.Single(body["slots"]!.AsArray());
        Assert.Equal(403, (await Send("GET", "/api/prices", auth: null)).Status);
        Assert.Equal(422, (await Send("GET", "/api/prices?hours=49")).Status);
    }

    [Fact]
    public async Task Prices_endpoint_hides_the_upstream_error()
    {
        _prices.Respond = () => throw new PriceException("HTTP 503: <pse body>");
        var response = await Send("GET", "/api/prices");
        Assert.Equal(502, response.Status);
        Assert.DoesNotContain("pse body", response.Text);
    }

    [Fact]
    public async Task Export_preview_disabled() => Assert.Equal(503, (await Send("POST", "/api/export/preview")).Status);

    [Fact]
    public async Task Export_switch_disabled()
    {
        Assert.Equal(403, (await Send("POST", "/api/export/active", """{"active": false}""", auth: null)).Status);
        Assert.Equal(503, (await Send("POST", "/api/export/active", """{"active": false}""")).Status);
    }

    [Fact]
    public void Export_requires_automation()
    {
        SetEnv("EXPORT_ENABLED", "1");
        Assert.Contains("AUTOMATION_ENABLED", Assert.Throws<ConfigException>(CreateApp).Message);
    }

    /// <summary>The dashboard switch, end to end: off is saved, shown in the status, and written to the history.</summary>
    [Fact]
    public async Task Export_switch_turns_the_export_loop_off()
    {
        EnableAutomation();
        SetEnv("EXPORT_ENABLED", "1");
        SetEnv("EXPORT_MAX_POWER_KW", "5");
        SetEnv("SETTINGS_FILE", Path.Combine(TempDir(), "settings.json"));
        _client.Realtime = _ => (new JsonObject(), new JsonObject { ["batterySOC"] = 50 });

        Assert.Equal(422, (await Send("POST", "/api/export/active", """{"active": "no"}""")).Status);
        Assert.Equal("""{"active":false,"persisted":true}""", (await Send("POST", "/api/export/active", """{"active": false}""")).Text);
        var export = (await Send("GET", "/api/automation")).Json!["export"]!;
        Assert.True(J.IsTrue(export["enabled"]));
        Assert.False(J.IsTrue(export["active"]));
        Assert.True(J.IsTrue(export["dryRun"]));
        var history = (await Send("GET", "/api/automation/history")).Json!.AsArray();
        Assert.Contains("disabled", history.Select(e => J.Str(e!["action"])));
        Assert.Empty(_client.Commands); // export dry run: nothing is sent to the inverter
    }

    [Fact]
    public async Task Export_test_requires_the_confirm_header()
    {
        SetEnv("SOLAX_DEVICE_SN", "SN1");
        Assert.Equal(400, (await Send("POST", "/api/battery/export-test", """{"power_kw": 1, "minutes": 1, "stop_soc": 80}""")).Status);
        Assert.Empty(_client.Commands);
    }

    [Theory]
    [InlineData("""{"power_kw": 2.5, "minutes": 1, "stop_soc": 80}""")]
    [InlineData("""{"power_kw": 1, "minutes": 6, "stop_soc": 80}""")]
    [InlineData("""{"power_kw": 0, "minutes": 1, "stop_soc": 80}""")]
    [InlineData("""{"power_kw": 1, "minutes": 1, "stop_soc": 29}""")]
    [InlineData("""{"power_kw": 1, "minutes": 1}""")]
    public async Task Export_test_enforces_hard_caps(string body)
    {
        SetEnv("SOLAX_DEVICE_SN", "SN1");
        Assert.Equal(422, (await Send("POST", "/api/battery/export-test", body, confirm: true)).Status);
        Assert.Empty(_client.Commands);
    }

    [Fact]
    public async Task Export_test_sends_a_capped_discharge()
    {
        SetEnv("SOLAX_DEVICE_SN", "SN1");
        var response = await Send("POST", "/api/battery/export-test", """{"power_kw": 1.5, "minutes": 2, "stop_soc": 85}""", confirm: true);
        Assert.Equal(200, response.Status);
        Assert.Equal(["discharge SN1 1500 85"], _client.Commands);
        Assert.Equal(120, J.Num(response.Json!["durationSeconds"]));
        Assert.Equal(3, J.Num(response.Json["result"]!["SN1"]!["status"]));
    }

    [Fact]
    public async Task Export_test_failure_restores_normal_mode()
    {
        SetEnv("SOLAX_DEVICE_SN", "SN1");
        _client.Fail = true;
        var response = await Send("POST", "/api/battery/export-test", """{"power_kw": 1, "minutes": 1, "stop_soc": 80}""", confirm: true);
        Assert.Equal(502, response.Status);
        Assert.DoesNotContain("boom", response.Text);
        Assert.Equal(["discharge SN1 1000 80", "exit SN1"], _client.Commands);
    }

    [Fact]
    public async Task Export_stop_exits_remote_control()
    {
        SetEnv("SOLAX_DEVICE_SN", "SN1");
        Assert.Equal("""{"stopped":true}""", (await Send("POST", "/api/battery/export-stop")).Text);
        Assert.Equal(["exit SN1"], _client.Commands);
    }

    [Fact]
    public async Task Dashboard_page_is_public()
    {
        var response = await Send("GET", "/dashboard", auth: null);
        Assert.Equal(200, response.Status);
        Assert.Equal("text/html", response.ContentType);
        // The Blazor shell. It holds no data: the page loads data only after the browser sends the API key
        Assert.Contains("_framework/blazor.web.js", response.Text);
        Assert.Contains("syncfusion-blazor.min.js", response.Text);
        Assert.DoesNotContain(ApiKey, response.Text);
    }

    [Fact]
    public async Task Dashboard_settings_disabled()
    {
        Assert.Equal(403, (await Send("GET", "/api/dashboard/settings", auth: null)).Status);
        Assert.Equal("""{"enabled":false}""", (await Send("GET", "/api/dashboard/settings")).Text);
    }

    [Fact]
    public async Task Dashboard_settings_enabled()
    {
        EnableAutomation();
        var body = (await Send("GET", "/api/dashboard/settings")).Json!;
        Assert.True(J.IsTrue(body["enabled"]));
        Assert.Equal(6, body["upcomingRuns"]!.AsArray().Count);
        Assert.Equal(21.2, J.Num(body["automation"]!["capacity_kwh"]));
        Assert.Equal("UTC", J.Str(body["automation"]!["tz"]));
        Assert.Null(body["export"]);
        Assert.Null(body["nextExportTick"]);
        // The keys the dashboard page reads
        Assert.Equal(
            ["tz", "tariff", "capacity_kwh", "consumption_profile", "min_soc", "max_grid_soc", "safety_margin_kwh", "efficiency", "percentile", "lead_minutes", "dry_run", "weekend_profile", "charge_power_kw"],
            body["automation"]!.AsObject().Select(p => p.Key));
        Assert.Equal("""{"windows":[[22,6],[13,15]],"weekends":true,"holidays":true,"summer_windows":[[22,6],[15,17]]}""", body["automation"]!["tariff"]!.ToJsonString());
        Assert.Matches(@"^\d\d:00-\d\d:00$", J.Str(body["upcomingRuns"]![0]!["window"]));
    }

    [Fact]
    public async Task Automation_history_disabled()
    {
        Assert.Equal(403, (await Send("GET", "/api/automation/history", auth: null)).Status);
        Assert.Equal("[]", (await Send("GET", "/api/automation/history")).Text);
    }
}
