using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SolaxCloudMcp;

/// <summary>MCP tools (stdio mode). Parameter names are snake_case on purpose: they are the tool argument names.</summary>
public class McpTools(SolaxService service)
{
    /// <summary>The tools this server exposes with the current environment.</summary>
    public static List<McpServerTool> Create(SolaxService service)
    {
        var target = new McpTools(service);
        McpServerTool Tool(string method, string name, string description) => McpServerTool.Create(
            typeof(McpTools).GetMethod(method)!, target, new McpServerToolCreateOptions { Name = name, Description = description });

        var tools = new List<McpServerTool>
        {
            Tool(nameof(GetRealtimeData), "get_realtime_data",
                """
                Fetch real-time solar inverter data from SolaX Developer Platform.

                Retrieves the latest readings including power output, energy yields, battery status,
                and grid import/export data from the SolaX Developer Platform API. All power values
                are in Watts (W), energy in kWh, voltage in Volts (V), current in Amps (A),
                and temperature in Celsius (°C).

                Returns a structured object with decoded status values and grouped data fields:
                - device: inverter identifiers (SN, registration number, timestamps)
                - status: inverter operating status (code + human-readable description)
                - pv: list of active PV strings with voltage/current/power
                - mppt: MPPT tracker data (individual trackers + total power)
                - ac: AC output phases, total power, reactive power, power factor, grid frequency
                - energy: daily/total yield and AC output
                - meter1: grid power and energy (import/export)
                - meter2: secondary meter data (import/export)
                - battery: battery voltage/current/power/soc/status (null if no battery)
                - eps: emergency power supply data (3-phase voltage/current/power)
                - temperature: inverter temperature
                - misc: line-to-line voltages
                """),
            Tool(nameof(GetEnergyPrices), "get_energy_prices",
                """
                Get Polish market energy prices (RCE, 15-minute periods) from PSE for the next hours.

                RCE is the price used to value energy exported to the grid under net-billing. Prices for
                the next day are published by PSE in the afternoon, so the list may end at midnight.

                Returns slots (start, end, price_PLN_kWh net), plus min and max price in the range.
                """),
        };

        // Forecast is read-only; expose it only when Solcast credentials are configured
        if (Config.IsSolcastConfigured())
        {
            tools.Add(Tool(nameof(GetSolarForecast), "get_solar_forecast",
                """
                Get the PV production forecast for the installation from Solcast.

                Use it together with get_realtime_data (battery SOC) to plan battery charging,
                e.g. lower charge_upper_soc / disable grid charging before a sunny day, raise it
                before a cloudy one. Values are the sum of all configured rooftop sites.

                p50 is the most likely value; p10 is a pessimistic (cloudier) and p90 an optimistic
                scenario. Energy is in kWh, power in kW, timestamps in the configured local timezone.
                Data is cached (Solcast free tier allows ~10 calls/day); `stale: true` means the
                latest refresh failed and older cached data is returned.

                Returns:
                - days: per-day forecast energy (p10/p50/p90) with peak power and its time
                - remainingToday_kWh: forecast energy still to come today
                - hourly: average PV power per hour for the next `hours` hours
                - fetchedAt, stale, timezone, source
                """));
        }

        // The write tool changes physical inverter settings; expose it to the LLM only on explicit opt-in
        if (Config.IsWriteEnabled())
        {
            tools.Add(Tool(nameof(SetBatterySelfUseMode), "set_battery_self_use_mode",
                """
                Set inverter battery to Self Use Mode with configurable charging thresholds.

                Self Use Mode is ideal for automated control based on conditions like weather.
                You can set minimum SOC (don't discharge below), maximum charge SOC (don't charge above),
                and optionally configure time-based charge/discharge periods (e.g., charge during peak solar,
                discharge during high-rate periods).

                Example automation scenarios:
                - Reduce charging threshold on cloudy days to preserve grid import reserves
                - Increase charging threshold on clear days to maximize solar self-consumption
                - Prevent overnight charging from grid during expensive peak hours

                Returns the API response confirming the command was sent to the inverter.
                """));
        }
        return tools;
    }

    /// <summary>The message of a ToolException is safe for the model; McpException sends it as the tool error.</summary>
    static async Task<JsonObject> Call(Task<JsonObject> work)
    {
        try
        {
            return await work;
        }
        catch (ToolException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    public Task<JsonObject> GetRealtimeData(
        [Description("Serial number of the inverter to query (the device SN, not WiFi dongle SN). If omitted, defaults to SOLAX_DEVICE_SN environment variable.")]
        string? device_sn = null) => Call(service.GetRealtimeDataAsync(device_sn));

    public Task<JsonObject> GetEnergyPrices(
        [Description("How many hours ahead to return (1-48). Default: 24.")] int hours = 24) => Call(service.GetEnergyPricesAsync(hours));

    public Task<JsonObject> GetSolarForecast(
        [Description("How many upcoming hours to include in the hourly profile (1-168). Default: 24.")] int hours = 24) => Call(service.GetSolarForecastAsync(hours));

    public Task<JsonObject> SetBatterySelfUseMode(
        [Description("Minimum SOC (%), range [10, 100]. Battery won't discharge below this.")] int min_soc,
        [Description("Maximum charging SOC (%), range [10, 100]. Battery won't charge above this.")] int charge_upper_soc,
        [Description("Allow charging from grid (0=no, 1=yes).")] int charge_from_grid_enable,
        [Description("Serial number of the inverter. If omitted, uses SOLAX_DEVICE_SN.")] string? device_sn = null,
        [Description("Optional start time for charging period 1 (HH:MM format, e.g., \"06:00\").")] string? charge_start_time_period1 = null,
        [Description("Optional end time for charging period 1 (HH:MM format, e.g., \"18:00\").")] string? charge_end_time_period1 = null,
        [Description("Optional start time for discharge period 1 (HH:MM format).")] string? discharge_start_time_period1 = null,
        [Description("Optional end time for discharge period 1 (HH:MM format).")] string? discharge_end_time_period1 = null,
        [Description("Enable second time period (0=disabled, 1=enabled). Default: 0.")] int enable_time_period2 = 0,
        [Description("Optional start time for charging period 2 (HH:MM format).")] string? charge_start_time_period2 = null,
        [Description("Optional end time for charging period 2 (HH:MM format).")] string? charge_end_time_period2 = null,
        [Description("Optional start time for discharge period 2 (HH:MM format).")] string? discharge_start_time_period2 = null,
        [Description("Optional end time for discharge period 2 (HH:MM format).")] string? discharge_end_time_period2 = null) =>
        Call(service.SetBatterySelfUseModeAsync(
            new SelfUseMode(min_soc, charge_upper_soc, charge_from_grid_enable,
                charge_start_time_period1, charge_end_time_period1, discharge_start_time_period1, discharge_end_time_period1,
                enable_time_period2,
                charge_start_time_period2, charge_end_time_period2, discharge_start_time_period2, discharge_end_time_period2),
            device_sn));

    /// <summary>Run the MCP server on stdin/stdout.</summary>
    public static async Task RunAsync()
    {
        var backends = Backends.Create();
        var builder = Host.CreateApplicationBuilder();
        // stdout carries the MCP protocol: all logs must go to stderr
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "solax-cloud", Version = "0.1.0" })
            .WithStdioServerTransport()
            .WithTools(Create(new SolaxService(backends.Client, backends.Forecast, backends.Prices)));
        await builder.Build().RunAsync();
    }
}
