using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Syncfusion.Blazor;

namespace SolaxCloudMcp;

/// <summary>The API clients the server uses. Tests replace them with fakes.</summary>
public record Backends(SolaxClient Client, SolcastForecast Forecast, RcePrices Prices)
{
    public static Backends Create() => new(new SolaxClient(new SolaxAuth()), new SolcastForecast(), new RcePrices());
}

/// <summary>What the dashboard page (Components/Dashboard.razor) reads. The page runs on the server.</summary>
public record DashboardData(SolaxService Service, AutomationScheduler? Scheduler, byte[] ApiKey)
{
    /// <summary>Constant-time check of the HTTP_API_KEY that the browser sent.</summary>
    public bool IsKey(string? candidate) => candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), ApiKey);
}

/// <summary>An HTTP error answer: status code and a `{"detail": ...}` body.</summary>
public class HttpError(int status, string detail) : Exception(detail)
{
    public int Status => status;
}

/// <summary>HTTP server: REST API, dashboard and the battery automation.</summary>
public static class HttpServer
{
    // Hard caps for the manual export test (POST /api/battery/export-test)
    const double ExportTestMaxKw = 2.0;
    const int ExportTestMaxMinutes = 5;
    // The inverter stops by itself at stop_soc even if the server dies; never let a test go below this
    const int ExportTestMinStopSoc = 30;

    static ILogger Log => AppLog.Factory.CreateLogger("SolaxCloudMcp.HttpServer");

    public static string ApiKey() => Config.Env("HTTP_API_KEY") ?? throw new ConfigException("HTTP_API_KEY environment variable not set");

    /// <summary>Loopback by default; set HTTP_HOST=0.0.0.0 to listen on all interfaces (e.g. in Docker).</summary>
    public static string ListenUrl() => $"http://{Config.Env("HTTP_HOST", "127.0.0.1")}:{Config.Int("HTTP_PORT", "8000")}";

    /// <summary>Validate the automation config at startup (fail fast) and build the scheduler if enabled.</summary>
    static AutomationScheduler? CreateScheduler(Backends backends, SolaxService service)
    {
        if (!Config.IsAutomationEnabled())
            return Config.IsExportEnabled() ? throw new ConfigException("EXPORT_ENABLED=1 requires AUTOMATION_ENABLED=1") : null;
        if (!Config.IsSolcastConfigured()) throw new ConfigException("AUTOMATION_ENABLED=1 requires SOLCAST_API_KEY and SOLCAST_RESOURCE_IDS");
        if (Config.DefaultDeviceSn() is null) throw new ConfigException("AUTOMATION_ENABLED=1 requires SOLAX_DEVICE_SN");
        var export = Config.IsExportEnabled() ? ExportSettings.FromEnv() : null;
        return new AutomationScheduler(Settings.FromEnv(), export, service, backends.Client, backends.Forecast, backends.Prices);
    }

    sealed class SchedulerHost(AutomationScheduler scheduler) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            scheduler.Start();
            Log.LogInformation("Battery automation started (dry_run={DryRun})", scheduler.Settings.DryRun);
            return Task.CompletedTask;
        }

        // On stop the scheduler exits SolaX remote control before the process quits
        public Task StopAsync(CancellationToken cancellationToken) => scheduler.StopAsync();
    }

    /// <summary>A JSON request body with validated fields. A bad or missing field is a 422, never a silent default.</summary>
    sealed class Body
    {
        readonly JsonObject _json;

        Body(JsonObject json) => _json = json;

        public static async Task<Body> ReadAsync(HttpRequest request)
        {
            try
            {
                return new Body(await JsonNode.ParseAsync(request.Body) as JsonObject ?? throw new JsonException());
            }
            catch (JsonException)
            {
                throw new HttpError(422, "Request body must be a JSON object");
            }
        }

        static HttpError Invalid(string name, string rule) => new(422, $"{name}: {rule}");

        public double Number(string name, Func<double, bool> isValid, string rule) =>
            J.Num(_json[name]) is { } value && isValid(value) ? value : throw Invalid(name, rule);

        public int Int(string name, int min, int max, int? fallback = null)
        {
            if (_json[name] is null && fallback is { } value) return value;
            return (int)Number(name, v => v == Math.Floor(v) && v >= min && v <= max, $"an integer from {min} to {max} is required");
        }

        public string? Text(string name, Func<string, bool>? isValid = null, string rule = "must be text")
        {
            if (_json[name] is null) return null;
            return J.Str(_json[name]) is { } value && isValid?.Invoke(value) != false ? value : throw Invalid(name, rule);
        }

        public bool Bool(string name) => _json[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : throw Invalid(name, "true or false is required");
    }

    static int Hours(HttpRequest request, int max)
    {
        if (!request.Query.TryGetValue("hours", out var raw)) return 24;
        return int.TryParse(raw, out var hours) && hours >= 1 && hours <= max ? hours : throw new HttpError(422, $"hours: an integer from 1 to {max} is required");
    }

    /// <summary>Validation error: 400 with the message. Upstream API failure: 502, details only in the logs.</summary>
    public static HttpError ToHttpError(ToolException e)
    {
        var upstream = e.InnerException switch
        {
            SolaxApiException => "SolaX API",
            PriceException => "PSE price API",
            SolcastException => "Solcast API",
            _ => null,
        };
        if (upstream is null) return new HttpError(400, e.Message);
        Log.LogError("{Upstream} call failed: {Error}", upstream, e.Message);
        return new HttpError(502, $"{upstream} request failed");
    }

    /// <summary>Planner settings and upcoming scheduler runs, for the dashboard. The settings hold no secrets.</summary>
    public static JsonObject DashboardSettings(AutomationScheduler? scheduler)
    {
        if (scheduler is null) return new JsonObject { ["enabled"] = false };
        var now = LocalTime.Now;
        var runs = new JsonArray();
        var after = now;
        for (var i = 0; i < 6; i++)
        {
            var trigger = Planner.NextTrigger(scheduler.Settings, after);
            runs.Add(new JsonObject { ["runAt"] = J.Iso(trigger.RunAt), ["windowStart"] = J.Iso(trigger.WindowStart), ["window"] = trigger.Window.ToString() });
            after = trigger.RunAt;
        }
        // Same boundary as the export loop of the scheduler
        var nextTick = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(15 - now.Minute % 15);
        return new JsonObject
        {
            ["enabled"] = true,
            ["automation"] = scheduler.Settings.ToJson(),
            ["export"] = scheduler.Export?.ToJson(),
            ["upcomingRuns"] = runs,
            ["nextExportTick"] = scheduler.Export is not null ? J.Iso(nextTick) : null,
        };
    }

    /// <summary>Create and configure the web application.</summary>
    public static WebApplication Create(Backends? backends = null, Action<WebApplicationBuilder>? configure = null)
    {
        backends ??= Backends.Create();
        var service = new SolaxService(backends.Client, backends.Forecast, backends.Prices);
        var scheduler = CreateScheduler(backends, service);
        var apiKey = Encoding.UTF8.GetBytes(ApiKey());

        // Without a key the Syncfusion components work, but show a licence banner
        if (Config.Env("SYNCFUSION_LICENSE_KEY") is { } license) Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(license);

        // The fixed name finds the static files (Blazor script, Syncfusion theme) also when a test host runs the app
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(HttpServer).Assembly.GetName().Name });
        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSyncfusionBlazor();
        builder.Services.AddSingleton(new DashboardData(service, scheduler, apiKey));
        builder.WebHost.UseUrls(ListenUrl());
        // The dashboard polls every minute: per-request info lines would flood the journal
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // Leaving remote control on stop takes a few seconds; systemd waits 60 s (TimeoutStopSec)
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
        if (scheduler is not null) builder.Services.AddHostedService(_ => new SchedulerHost(scheduler));
        configure?.Invoke(builder);
        var app = builder.Build();

        // Auth runs before body validation, so callers with no key get 403, not 422 with field details
        app.Use(async (context, next) =>
        {
            try
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    var space = header.IndexOf(' ');
                    var (scheme, credentials) = space < 0 ? (header, "") : (header[..space], header[(space + 1)..]);
                    if (!scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase))
                        throw new HttpError(403, "Invalid authorization header format. Use: Bearer <token>");
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(credentials), apiKey))
                        throw new HttpError(403, "Invalid API key");
                }
                await next(context);
            }
            catch (Exception e) when (!context.Response.HasStarted)
            {
                var error = e as HttpError ?? (e is ToolException tool ? ToHttpError(tool) : null);
                if (error is null) Log.LogError(e, "Unhandled error in {Method} {Path}", context.Request.Method, context.Request.Path);
                context.Response.StatusCode = error?.Status ?? 500;
                await context.Response.WriteAsJsonAsync(new JsonObject { ["detail"] = error?.Message ?? "Internal Server Error" });
            }
        });

        // The dashboard page at /dashboard (Blazor Server). The page is public; it shows data only after the
        // browser sends the API key over the Blazor connection (see Dashboard.razor)
        app.UseAntiforgery();
        var staticAssets = Path.Combine(AppContext.BaseDirectory, $"{builder.Environment.ApplicationName}.staticwebassets.endpoints.json");
        if (File.Exists(staticAssets)) app.MapStaticAssets(staticAssets);
        app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();

        AutomationScheduler RequireExport() =>
            scheduler is { Export: not null } ? scheduler : throw new HttpError(503, "Export planner is not enabled");
        string RequireDeviceSn() => Config.DefaultDeviceSn() ?? throw new HttpError(400, "SOLAX_DEVICE_SN is not set");

        app.MapGet("/health", () => Results.Json(new JsonObject { ["status"] = "ok" }));

        app.MapPost("/api/realtime-data", async (HttpRequest request) =>
            Results.Json(await service.GetRealtimeDataAsync((await Body.ReadAsync(request)).Text("device_sn"))));

        // PV production forecast from Solcast (cached)
        app.MapGet("/api/solar-forecast", async (HttpRequest request) =>
        {
            var hours = Hours(request, 168);
            if (!Config.IsSolcastConfigured()) throw new HttpError(503, "Solcast is not configured");
            return Results.Json(await service.GetSolarForecastAsync(hours));
        });

        // RCE market prices (15-min, PLN/kWh net) from PSE
        app.MapGet("/api/prices", async (HttpRequest request) => Results.Json(await service.GetEnergyPricesAsync(Hours(request, 48))));

        // Export plan from current SOC, PV forecast and RCE prices. Never writes to the inverter
        app.MapPost("/api/export/preview", async () =>
        {
            try
            {
                return Results.Json(await RequireExport().ExportPlanAsync());
            }
            catch (Exception e) when (e is SolcastException or PriceException or SolaxApiException)
            {
                Log.LogError("Export preview failed: {Error}", e.Message);
                throw new HttpError(502, "Upstream API request failed");
            }
        });

        // Turn the export loop on or off (saved to SETTINGS_FILE). Off also exits remote control now
        app.MapPost("/api/export/active", async (HttpRequest request) =>
        {
            var active = (await Body.ReadAsync(request)).Bool("active");
            bool persisted;
            try
            {
                persisted = await RequireExport().SetExportActiveAsync(active);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.LogError("Cannot save settings: {Error}", e.Message);
                throw new HttpError(500, "Cannot save SETTINGS_FILE");
            }
            Log.LogWarning("Export loop turned {State} from the dashboard", active ? "on" : "off");
            return Results.Json(new JsonObject { ["active"] = active, ["persisted"] = persisted });
        });

        // Battery automation status: dry-run flag, next planned run and last result
        app.MapGet("/api/automation", () => Results.Json(scheduler?.Status() ?? new JsonObject { ["enabled"] = false }));

        app.MapGet("/api/dashboard/settings", () => Results.Json(DashboardSettings(scheduler)));

        // Charge and export decisions, newest first
        app.MapGet("/api/automation/history", () => Results.Json(J.Array(scheduler?.History.AsEnumerable().Reverse() ?? [])));

        // Compute the plan for the next off-peak window without writing to the inverter
        app.MapPost("/api/automation/preview", async () =>
        {
            if (scheduler is null) throw new HttpError(503, "Automation is not enabled");
            try
            {
                return Results.Json(await scheduler.PreviewAsync());
            }
            catch (SolcastException e)
            {
                Log.LogError("Solcast API call failed: {Error}", e.Message);
                throw new HttpError(502, "Solcast API request failed");
            }
        });

        // Set the inverter to Self Use Mode. The three main fields are required: no silent defaults that would overwrite inverter settings
        app.MapPost("/api/battery/self-use-mode", async (HttpRequest request) =>
        {
            var body = await Body.ReadAsync(request);
            string? Time(string name) => body.Text(name, SolaxService.IsHhmm, "HH:MM is required (00:00-23:59)");
            var deviceSn = body.Text("device_sn");
            var mode = new SelfUseMode(
                MinSoc: body.Int("min_soc", 10, 100),
                ChargeUpperSoc: body.Int("charge_upper_soc", 10, 100),
                ChargeFromGridEnable: body.Int("charge_from_grid_enable", 0, 1),
                ChargeStartTimePeriod1: Time("charge_start_time_period1"),
                ChargeEndTimePeriod1: Time("charge_end_time_period1"),
                DischargeStartTimePeriod1: Time("discharge_start_time_period1"),
                DischargeEndTimePeriod1: Time("discharge_end_time_period1"),
                EnableTimePeriod2: body.Int("enable_time_period2", 0, 1, fallback: 0),
                ChargeStartTimePeriod2: Time("charge_start_time_period2"),
                ChargeEndTimePeriod2: Time("charge_end_time_period2"),
                DischargeStartTimePeriod2: Time("discharge_start_time_period2"),
                DischargeEndTimePeriod2: Time("discharge_end_time_period2"));
            return Results.Json(await service.SetBatterySelfUseModeAsync(mode, deviceSn));
        });

        CancellationTokenSource? pendingRestore = null; // at most one scheduled exit of remote control
        void CancelPendingRestore() => Interlocked.Exchange(ref pendingRestore, null)?.Cancel();

        // Exit remote control so the inverter returns to its normal work mode. Never throws
        async Task<bool> RestoreNormalModeAsync(string deviceSn)
        {
            try
            {
                await backends.Client.ExitVppModeAsync(deviceSn);
            }
            catch (SolaxApiException e)
            {
                Log.LogError("Exit remote control failed for {DeviceSn}: {Error}", deviceSn, e.Message);
                return false;
            }
            Log.LogWarning("Exit remote control sent to {DeviceSn}", deviceSn);
            return true;
        }

        // Manually discharge the battery for a few minutes to test export control. Needs the header `X-Confirm: yes`.
        // The server exits remote control after `minutes`; the inverter also stops by itself at `stop_soc`, in case the server is down
        app.MapPost("/api/battery/export-test", async (HttpRequest request) =>
        {
            var body = await Body.ReadAsync(request);
            var powerKw = body.Number("power_kw", v => v > 0 && v <= ExportTestMaxKw, $"a number above 0 and up to {ExportTestMaxKw} is required");
            var minutes = body.Int("minutes", 1, ExportTestMaxMinutes);
            var stopSoc = body.Int("stop_soc", ExportTestMinStopSoc, 100);
            if (request.Headers["X-Confirm"] != "yes") throw new HttpError(400, "Header X-Confirm: yes is required");
            var deviceSn = RequireDeviceSn();
            var dischargeW = (int)Math.Round(powerKw * 1000, MidpointRounding.ToEven);
            var durationS = minutes * 60;

            CancelPendingRestore();
            Log.LogWarning("Export test: discharge {Watts} W for {Seconds} s, stop at {StopSoc}% SOC, on {DeviceSn}", dischargeW, durationS, stopSoc, deviceSn);
            JsonObject response;
            try
            {
                response = await backends.Client.DischargeToSocAsync(deviceSn, dischargeW, stopSoc);
            }
            catch (SolaxApiException e)
            {
                Log.LogError("Export test command failed: {Error}", e.Message);
                await RestoreNormalModeAsync(deviceSn);
                throw new HttpError(502, "SolaX API request failed");
            }

            var restore = new CancellationTokenSource();
            pendingRestore = restore;
            _ = Task.Delay(TimeSpan.FromSeconds(durationS), restore.Token)
                .ContinueWith(_ => RestoreNormalModeAsync(deviceSn), TaskContinuationOptions.OnlyOnRanToCompletion);
            return Results.Json(new JsonObject
            {
                ["dischargeSetpoint_W"] = dischargeW,
                ["stopSoc"] = stopSoc,
                ["durationSeconds"] = durationS,
                ["result"] = J.Copy(response["result"]),
            });
        });

        // Stop any remote-control command now: the inverter returns to its normal work mode
        app.MapPost("/api/battery/export-stop", async () =>
        {
            var deviceSn = RequireDeviceSn();
            CancelPendingRestore();
            if (!await RestoreNormalModeAsync(deviceSn)) throw new HttpError(502, "SolaX API request failed");
            return Results.Json(new JsonObject { ["stopped"] = true });
        });

        return app;
    }

    public static void Run() => Create().Run();
}
