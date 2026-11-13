using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SolaxCloudMcp;

/// <summary>An error whose message is safe to show to the caller. If <see cref="Exception.InnerException"/> is an
/// upstream API error, the HTTP server hides the message and answers 502.</summary>
public class ToolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Business logic shared by the MCP tools and the HTTP routes.</summary>
public partial class SolaxService(SolaxClient client, SolcastForecast forecast, RcePrices prices)
{
    public const string HhmmPattern = @"^([01][0-9]|2[0-3]):[0-5][0-9]\z";

    [GeneratedRegex(HhmmPattern)]
    private static partial Regex HhmmRegex();

    public static bool IsHhmm(string value) => HhmmRegex().IsMatch(value);

    static string RequireDeviceSn(string? deviceSn, string hint) =>
        deviceSn is { Length: > 0 } ? deviceSn
        : Config.DefaultDeviceSn() ?? throw new ToolException($"No device_sn provided: either pass device_sn argument{hint} or set SOLAX_DEVICE_SN environment variable");

    public virtual async Task<JsonObject> GetRealtimeDataAsync(string? deviceSn = null)
    {
        var sn = RequireDeviceSn(deviceSn, " to this tool");
        try
        {
            var (inverter, battery) = await client.FetchRealtimeDataAsync(sn);
            return Models.ShapeRealtimeResponse(inverter, battery);
        }
        catch (SolaxApiException e)
        {
            throw new ToolException($"SolaX API error: {e.Message}", e);
        }
    }

    public async Task<JsonObject> SetBatterySelfUseModeAsync(SelfUseMode mode, string? deviceSn = null)
    {
        var sn = RequireDeviceSn(deviceSn, "");
        if (mode.MinSoc is < 10 or > 100) throw new ToolException($"min_soc must be between 10 and 100, got {mode.MinSoc}");
        if (mode.ChargeUpperSoc is < 10 or > 100) throw new ToolException($"charge_upper_soc must be between 10 and 100, got {mode.ChargeUpperSoc}");
        if (mode.MinSoc > mode.ChargeUpperSoc)
            throw new ToolException($"min_soc ({mode.MinSoc}) cannot be greater than charge_upper_soc ({mode.ChargeUpperSoc})");
        if (mode.ChargeFromGridEnable is not (0 or 1)) throw new ToolException($"charge_from_grid_enable must be 0 or 1, got {mode.ChargeFromGridEnable}");
        if (mode.EnableTimePeriod2 is not (0 or 1)) throw new ToolException($"enable_time_period2 must be 0 or 1, got {mode.EnableTimePeriod2}");
        foreach (var (name, value) in new[]
                 {
                     ("charge_start_time_period1", mode.ChargeStartTimePeriod1),
                     ("charge_end_time_period1", mode.ChargeEndTimePeriod1),
                     ("discharge_start_time_period1", mode.DischargeStartTimePeriod1),
                     ("discharge_end_time_period1", mode.DischargeEndTimePeriod1),
                     ("charge_start_time_period2", mode.ChargeStartTimePeriod2),
                     ("charge_end_time_period2", mode.ChargeEndTimePeriod2),
                     ("discharge_start_time_period2", mode.DischargeStartTimePeriod2),
                     ("discharge_end_time_period2", mode.DischargeEndTimePeriod2),
                 })
        {
            if (value is not null && !IsHhmm(value)) throw new ToolException($"{name} must be in HH:MM format (00:00-23:59), got '{value}'");
        }

        try
        {
            return await client.SetSelfUseModeAsync(sn, mode);
        }
        catch (SolaxApiException e)
        {
            throw new ToolException($"SolaX API error: {e.Message}", e);
        }
    }

    public async Task<JsonObject> GetSolarForecastAsync(int hours = 24)
    {
        if (!Config.IsSolcastConfigured()) throw new ToolException("Solcast is not configured: set SOLCAST_API_KEY and SOLCAST_RESOURCE_IDS");
        if (hours is < 1 or > 168) throw new ToolException($"hours must be between 1 and 168, got {hours}");
        try
        {
            return await forecast.GetSolarForecastAsync(hours);
        }
        catch (SolcastException e)
        {
            throw new ToolException($"Solcast API error: {e.Message}", e);
        }
    }

    public async Task<JsonObject> GetEnergyPricesAsync(int hours = 24)
    {
        if (hours is < 1 or > 48) throw new ToolException($"hours must be between 1 and 48, got {hours}");
        List<PriceSlot> slots;
        try
        {
            slots = await prices.GetRcePricesAsync();
        }
        catch (PriceException e)
        {
            throw new ToolException($"PSE price API error: {e.Message}", e);
        }

        var now = LocalTime.Now;
        var until = now.AddHours(hours);
        var upcoming = slots.Where(s => s.End > now && s.Start < until).ToList();
        var rounded = upcoming.Select(s => J.Round(s.PricePlnKwh, 4)).ToList();
        return new JsonObject
        {
            ["source"] = "PSE RCE",
            ["unit"] = "PLN/kWh (net)",
            ["slots"] = J.Array(upcoming.Select((s, i) => new JsonObject
            {
                ["start"] = J.Iso(s.Start),
                ["end"] = J.Iso(s.End),
                ["price_PLN_kWh"] = rounded[i],
            })),
            ["max"] = rounded.Count > 0 ? rounded.Max() : null,
            ["min"] = rounded.Count > 0 ? rounded.Min() : null,
        };
    }
}
