using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

// Forecast-driven battery grid-charging planner for time-of-use tariffs (e.g. Polish G12w).
//
// Before each daily off-peak window (e.g. 22:00-06:00 and 13:00-15:00) the planner looks at the
// peak-price hours that directly follow it, estimates how much energy the house will need from the
// battery there (consumption profile minus Solcast PV forecast) and charges the battery from the
// grid during the cheap window only up to that level.
//
// Charging uses remote control (charge to target SOC) during the window; the X3-NEO-LV ignores the
// Self Use grid-charge settings.

/// <summary>A daily off-peak window in full local hours. End &lt;= Start means it crosses midnight.</summary>
public readonly record struct Window(int Start, int End)
{
    public override string ToString() => $"{Start:00}:00-{End:00}:00";
}

public static class Holidays
{
    /// <summary>Gregorian Easter Sunday (anonymous Gregorian algorithm).</summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var n = h + l - 7 * m + 114;
        return new DateOnly(year, n / 31, n % 31 + 1);
    }

    /// <summary>Statutory public holidays in Poland (off-peak all day in G12w).</summary>
    public static HashSet<DateOnly> Polish(int year)
    {
        var easter = Easter(year);
        var days = new HashSet<DateOnly>
        {
            new(year, 1, 1), new(year, 1, 6), new(year, 5, 1), new(year, 5, 3), new(year, 8, 15),
            new(year, 11, 1), new(year, 11, 11), new(year, 12, 25), new(year, 12, 26),
            easter, easter.AddDays(1), easter.AddDays(49), easter.AddDays(60),
        };
        if (year >= 2025) days.Add(new DateOnly(year, 12, 24));
        return days;
    }

    public static bool IsWeekendOrHoliday(DateOnly day) =>
        day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || Polish(day.Year).Contains(day);
}

/// <param name="Windows">Winter windows (1 Oct - 31 Mar), or all year when there are no summer windows.</param>
/// <param name="SummerWindows">Windows for 1 Apr - 30 Sep.</param>
public record Tariff(IReadOnlyList<Window> Windows, bool Weekends, bool Holidays, IReadOnlyList<Window>? SummerWindows = null)
{
    public const string G12wWinterWindows = "22:00-06:00,13:00-15:00"; // 1 Oct - 31 Mar
    public const string G12wSummerWindows = "22:00-06:00,15:00-17:00"; // 1 Apr - 30 Sep

    public IReadOnlyList<Window> WindowsFor(DateOnly day) =>
        SummerWindows is { Count: > 0 } && day.Month is >= 4 and <= 9 ? SummerWindows : Windows;

    /// <param name="local">A local time; its own wall clock is used.</param>
    public bool IsOffpeak(DateTimeOffset local)
    {
        var day = DateOnly.FromDateTime(local.DateTime);
        if (Weekends && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        if (Holidays && SolaxCloudMcp.Holidays.Polish(day.Year).Contains(day)) return true;
        var h = local.Hour;
        // A window crossing midnight belongs to the day it starts on (same night hours in both seasons)
        return WindowsFor(day).Any(w => w.Start < w.End ? w.Start <= h && h < w.End : h >= w.Start || h < w.End);
    }

    /// <summary>Parse "22:00-06:00,13:00-15:00". Full hours only.</summary>
    public static IReadOnlyList<Window> ParseWindows(string raw)
    {
        var windows = new List<Window>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-');
            int sh = 0, sm = 0, eh = 0, em = 0;
            if (ends.Length != 2 || !TryTime(ends[0], out sh, out sm) || !TryTime(ends[1], out eh, out em))
                throw new ConfigException($"Invalid TARIFF_OFFPEAK_WINDOWS entry '{part}', expected HH:00-HH:00");
            if (sm != 0 || em != 0 || sh is < 0 or >= 24 || eh is < 0 or >= 24 || sh == eh)
                throw new ConfigException($"Invalid TARIFF_OFFPEAK_WINDOWS entry '{part}', expected full hours HH:00-HH:00");
            windows.Add(new Window(sh, eh));
        }
        if (windows.Count == 0) throw new ConfigException("TARIFF_OFFPEAK_WINDOWS must contain at least one window");
        return windows;

        static bool TryTime(string text, out int hour, out int minute)
        {
            hour = minute = 0;
            var parts = text.Split(':');
            return parts.Length == 2 && int.TryParse(parts[0], out hour) && int.TryParse(parts[1], out minute);
        }
    }
}

/// <param name="ConsumptionProfile">kWh per local hour 0..23.</param>
/// <param name="WeekendProfile">Used on weekends and Polish public holidays; falls back to ConsumptionProfile.</param>
public record Settings(
    TimeZoneInfo Tz,
    Tariff Tariff,
    double CapacityKwh,
    IReadOnlyList<double> ConsumptionProfile,
    int MinSoc,
    int MaxGridSoc,
    double SafetyMarginKwh,
    double Efficiency,
    string Percentile,
    int LeadMinutes,
    bool DryRun,
    IReadOnlyList<double>? WeekendProfile = null,
    double ChargePowerKw = 5.0)
{
    /// <summary>Expected house consumption (kWh) in the local hour that starts at <paramref name="localHour"/>.</summary>
    public double LoadKwh(DateTimeOffset localHour) =>
        WeekendProfile is { Count: > 0 } && Holidays.IsWeekendOrHoliday(DateOnly.FromDateTime(localHour.DateTime))
            ? WeekendProfile[localHour.Hour]
            : ConsumptionProfile[localHour.Hour];

    static IReadOnlyList<double> ParseProfile(string raw, string name)
    {
        double[] profile;
        try
        {
            profile = raw.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        catch (FormatException e)
        {
            throw new ConfigException($"{name} must have 24 comma-separated kWh values", e);
        }
        return profile.Length == 24 ? profile : throw new ConfigException($"{name} must have 24 comma-separated kWh values");
    }

    public static Settings FromEnv()
    {
        if (Config.Env("BATTERY_CAPACITY_KWH") is null) throw new ConfigException("BATTERY_CAPACITY_KWH is required when AUTOMATION_ENABLED=1");

        IReadOnlyList<double> profile;
        if (Config.Env("AUTOMATION_CONSUMPTION_PROFILE") is { } profileRaw) profile = ParseProfile(profileRaw, "AUTOMATION_CONSUMPTION_PROFILE");
        else if (Config.Env("AUTOMATION_DAILY_CONSUMPTION_KWH") is not null) profile = Enumerable.Repeat(Config.Double("AUTOMATION_DAILY_CONSUMPTION_KWH") / 24, 24).ToArray();
        else throw new ConfigException("Set AUTOMATION_DAILY_CONSUMPTION_KWH or AUTOMATION_CONSUMPTION_PROFILE when AUTOMATION_ENABLED=1");

        IReadOnlyList<double>? weekendProfile = null;
        if (Config.Env("AUTOMATION_WEEKEND_CONSUMPTION_PROFILE") is { } weekendRaw) weekendProfile = ParseProfile(weekendRaw, "AUTOMATION_WEEKEND_CONSUMPTION_PROFILE");
        else if (Config.Env("AUTOMATION_WEEKEND_DAILY_CONSUMPTION_KWH") is not null) weekendProfile = Enumerable.Repeat(Config.Double("AUTOMATION_WEEKEND_DAILY_CONSUMPTION_KWH") / 24, 24).ToArray();

        var percentile = Config.Env("AUTOMATION_FORECAST_PERCENTILE", "p50")!;
        if (percentile is not ("p10" or "p50" or "p90")) throw new ConfigException("AUTOMATION_FORECAST_PERCENTILE must be p10, p50 or p90");

        var minSoc = Config.Int("AUTOMATION_MIN_SOC", "15");
        var maxGridSoc = Config.Int("AUTOMATION_MAX_GRID_SOC", "100");
        if (!(10 <= minSoc && minSoc <= maxGridSoc && maxGridSoc <= 100))
            throw new ConfigException("Require 10 <= AUTOMATION_MIN_SOC <= AUTOMATION_MAX_GRID_SOC <= 100");

        var efficiency = Config.Double("AUTOMATION_EFFICIENCY", "0.9");
        if (efficiency is not (>= 0.5 and <= 1.0)) throw new ConfigException("AUTOMATION_EFFICIENCY must be between 0.5 and 1.0");

        var chargePowerKw = Config.Double("AUTOMATION_CHARGE_POWER_KW", "5");
        if (chargePowerKw is not (> 0 and <= 15)) throw new ConfigException("AUTOMATION_CHARGE_POWER_KW must be between 0 and 15");

        return new Settings(
            Tz: LocalTime.Zone(Config.SolarTimezone()),
            Tariff: new Tariff(
                Windows: Tariff.ParseWindows(Config.Env("TARIFF_OFFPEAK_WINDOWS", Tariff.G12wWinterWindows)!),
                // Custom year-round windows stay year-round unless summer windows are set explicitly
                SummerWindows: Tariff.ParseWindows(Config.Env("TARIFF_OFFPEAK_WINDOWS_SUMMER", Config.Env("TARIFF_OFFPEAK_WINDOWS", Tariff.G12wSummerWindows))!),
                Weekends: Config.Env("TARIFF_OFFPEAK_WEEKENDS", "1") == "1",
                Holidays: Config.Env("TARIFF_OFFPEAK_HOLIDAYS", "1") == "1"),
            CapacityKwh: Config.Double("BATTERY_CAPACITY_KWH"),
            ConsumptionProfile: profile,
            MinSoc: minSoc,
            MaxGridSoc: maxGridSoc,
            SafetyMarginKwh: Config.Double("AUTOMATION_SAFETY_MARGIN_KWH", "1.0"),
            Efficiency: efficiency,
            Percentile: percentile,
            LeadMinutes: Config.Int("AUTOMATION_LEAD_MINUTES", "10"),
            // Safe default: log decisions only, never write to the inverter unless explicitly disabled
            DryRun: Config.Env("AUTOMATION_DRY_RUN", "1") != "0",
            WeekendProfile: weekendProfile,
            ChargePowerKw: chargePowerKw);
    }

    /// <summary>For the dashboard. Keys are the ones the page reads. Holds no secrets.</summary>
    public JsonObject ToJson()
    {
        static JsonArray Windows(IReadOnlyList<Window> windows) => J.Array(windows.Select(w => new JsonArray(w.Start, w.End)));
        static JsonArray Numbers(IReadOnlyList<double> values) => J.Array(values.Select(v => (JsonNode)v));
        return new JsonObject
        {
            ["tz"] = Tz.Id,
            ["tariff"] = new JsonObject
            {
                ["windows"] = Windows(Tariff.Windows),
                ["weekends"] = Tariff.Weekends,
                ["holidays"] = Tariff.Holidays,
                ["summer_windows"] = Tariff.SummerWindows is null ? null : Windows(Tariff.SummerWindows),
            },
            ["capacity_kwh"] = CapacityKwh,
            ["consumption_profile"] = Numbers(ConsumptionProfile),
            ["min_soc"] = MinSoc,
            ["max_grid_soc"] = MaxGridSoc,
            ["safety_margin_kwh"] = SafetyMarginKwh,
            ["efficiency"] = Efficiency,
            ["percentile"] = Percentile,
            ["lead_minutes"] = LeadMinutes,
            ["dry_run"] = DryRun,
            ["weekend_profile"] = WeekendProfile is null ? null : Numbers(WeekendProfile),
            ["charge_power_kw"] = ChargePowerKw,
        };
    }
}

/// <param name="RunAt">When the planner runs (window start - lead).</param>
public record Trigger(DateTimeOffset RunAt, DateTimeOffset WindowStart, Window Window);

public record ExportSettings(double MinPricePlnKwh, double MaxPowerKw, double MinSlotKwh, double PriceMultiplier, bool DryRun)
{
    public static ExportSettings FromEnv()
    {
        if (Config.Env("EXPORT_MAX_POWER_KW") is null) throw new ConfigException("EXPORT_MAX_POWER_KW is required when EXPORT_ENABLED=1");
        return new ExportSettings(
            MinPricePlnKwh: Config.Double("EXPORT_MIN_PRICE_PLN_KWH", "1.0"),
            MaxPowerKw: Config.Double("EXPORT_MAX_POWER_KW"),
            MinSlotKwh: Config.Double("EXPORT_MIN_SLOT_KWH", "0.2"),
            PriceMultiplier: Config.Double("EXPORT_PRICE_MULTIPLIER", "1.0"),
            DryRun: Config.Env("EXPORT_DRY_RUN", "1") != "0");
    }

    public JsonObject ToJson() => new()
    {
        ["min_price_pln_kwh"] = MinPricePlnKwh,
        ["max_power_kw"] = MaxPowerKw,
        ["min_slot_kwh"] = MinSlotKwh,
        ["price_multiplier"] = PriceMultiplier,
        ["dry_run"] = DryRun,
    };
}

/// <summary>The planners as pure functions: no clock, no network, no inverter.</summary>
public static class Planner
{
    const int MaxSegmentHours = 24;

    /// <summary>First planner run strictly after <paramref name="after"/>: lead minutes before each daily off-peak window start.</summary>
    public static Trigger NextTrigger(Settings settings, DateTimeOffset after)
    {
        var localDay = DateOnly.FromDateTime(LocalTime.ToLocal(after, settings.Tz).DateTime);
        Trigger? best = null;
        for (var offset = 0; offset < 3; offset++)
        {
            var day = localDay.AddDays(offset);
            foreach (var window in settings.Tariff.WindowsFor(day))
            {
                var start = LocalTime.At(day.ToDateTime(new TimeOnly(window.Start, 0)), settings.Tz);
                var runAt = LocalTime.AddWall(start, TimeSpan.FromMinutes(-settings.LeadMinutes), settings.Tz);
                if (runAt > after && (best is null || runAt < best.RunAt)) best = new Trigger(runAt, start, window);
            }
        }
        return best!; // three days always hold a window start after `after`
    }

    /// <summary>Decide the grid-charge target SOC for the off-peak window of <paramref name="trigger"/>.
    ///
    /// The peak segment is the run of peak-price hours right after the window ends (empty when the
    /// window is followed by more off-peak time, e.g. Friday night before a weekend). The battery must
    /// start that segment with enough energy to cover the largest cumulative deficit (load - PV) inside
    /// it; PV surplus earlier in the segment offsets later deficits.</summary>
    public static JsonObject ComputePlan(Settings settings, Trigger trigger, IReadOnlyDictionary<DateTimeOffset, double> pvByHour)
    {
        var (s, e) = (trigger.Window.Start, trigger.Window.End);
        var endWall = trigger.WindowStart.DateTime.Date.AddHours(e);
        if (e <= s) endWall = endWall.AddDays(1);
        var windowEnd = LocalTime.At(endWall, settings.Tz);

        var hours = new List<DateTimeOffset>();
        for (var h = windowEnd; hours.Count < MaxSegmentHours && !settings.Tariff.IsOffpeak(h); h = LocalTime.AddWall(h, TimeSpan.FromHours(1), settings.Tz))
            hours.Add(h);

        double pvTotal = 0, loadTotal = 0, running = 0, peakDeficit = 0;
        foreach (var hour in hours)
        {
            var pv = pvByHour.GetValueOrDefault(hour, 0.0);
            var load = settings.LoadKwh(hour);
            pvTotal += pv;
            loadTotal += load;
            running += load - pv;
            peakDeficit = Math.Max(peakDeficit, running);
        }

        var need = peakDeficit > 0 ? peakDeficit / settings.Efficiency + settings.SafetyMarginKwh : 0.0;
        var target = need > 0 ? settings.MinSoc + (int)Math.Ceiling(need / settings.CapacityKwh * 100) : settings.MinSoc;
        target = Math.Min(settings.MaxGridSoc, Math.Max(settings.MinSoc, target));

        return new JsonObject
        {
            ["runAt"] = J.Iso(trigger.RunAt),
            ["window"] = trigger.Window.ToString(),
            ["windowStart"] = J.Iso(trigger.WindowStart),
            ["windowEnd"] = J.Iso(windowEnd),
            ["peakSegment"] = new JsonObject
            {
                ["start"] = hours.Count > 0 ? J.Iso(hours[0]) : null,
                ["end"] = hours.Count > 0 ? J.Iso(LocalTime.AddWall(hours[^1], TimeSpan.FromHours(1), settings.Tz)) : null,
                ["hours"] = hours.Count,
                ["pvForecast_kWh"] = J.Round(pvTotal),
                ["consumption_kWh"] = J.Round(loadTotal),
                ["maxDeficit_kWh"] = J.Round(peakDeficit),
            },
            ["percentile"] = settings.Percentile,
            ["needFromBattery_kWh"] = J.Round(need),
            ["minSoc"] = settings.MinSoc,
            ["targetSoc"] = target,
            ["gridCharge"] = target > settings.MinSoc,
        };
    }

    /// <summary>Energy (kWh) the battery must keep for the house between start and end (max cumulative deficit).</summary>
    static double ReserveNeed(Settings settings, DateTimeOffset start, DateTimeOffset end, IReadOnlyDictionary<DateTimeOffset, double> pvByHour)
    {
        var local = LocalTime.ToLocal(start, settings.Tz);
        var hour = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset);
        double running = 0, peak = 0;
        while (hour < end)
        {
            running += settings.LoadKwh(hour) - pvByHour.GetValueOrDefault(hour, 0.0);
            peak = Math.Max(peak, running);
            hour = LocalTime.AddWall(hour, TimeSpan.FromHours(1), settings.Tz);
        }
        return peak > 0 ? peak / settings.Efficiency + settings.SafetyMarginKwh : 0.0;
    }

    /// <summary>Pick the most expensive 15-min slots until the next off-peak window to sell surplus battery energy.
    ///
    /// Surplus = energy above the SOC reserve minus what the house needs until the next window (so selling
    /// never forces buying back at peak price). Surplus is allocated greedily to the highest-price slots at
    /// or above the threshold.
    ///
    /// The discharge setpoint is the inverter AC output (SolaX soc_target_control_mode): the house load
    /// is served first and only the rest is exported. So per slot export = setpoint * 0.25 h - house load
    /// in that slot (already covered by the reserve), and the setpoint for a partially used slot is
    /// export + expected house load.</summary>
    public static JsonObject ComputeExportPlan(
        Settings settings, ExportSettings export, DateTimeOffset now, double socPercent,
        IReadOnlyDictionary<DateTimeOffset, double> pvByHour, IReadOnlyList<PriceSlot> prices)
    {
        // The next daily off-peak window start: the next point where the charge planner refills the battery
        var horizon = NextTrigger(settings, now).WindowStart;
        var reserve = ReserveNeed(settings, now, horizon, pvByHour);
        var stored = Math.Max(0.0, (socPercent - settings.MinSoc) / 100 * settings.CapacityKwh);
        var surplus = Math.Max(0.0, stored - reserve);

        double SlotLoad(PriceSlot slot) => settings.LoadKwh(LocalTime.ToLocal(slot.Start, settings.Tz)) * 0.25;

        var remaining = surplus;
        var chosen = new List<(PriceSlot Slot, double Energy)>();
        // OrderByDescending is stable: equal prices keep their time order
        foreach (var slot in prices.Where(p => p.End > now && p.Start < horizon && p.PricePlnKwh >= export.MinPricePlnKwh).OrderByDescending(p => p.PricePlnKwh))
        {
            var energy = Math.Min(Math.Max(0.0, export.MaxPowerKw * 0.25 - SlotLoad(slot)), remaining);
            if (energy < export.MinSlotKwh) continue;
            chosen.Add((slot, energy));
            remaining -= energy;
            if (remaining < export.MinSlotKwh) break;
        }

        var tz = settings.Tz;
        var current = chosen.Where(c => c.Slot.Start <= now && now < c.Slot.End).Select(c => ((PriceSlot Slot, double Energy)?)c).FirstOrDefault();

        return new JsonObject
        {
            ["now"] = J.Iso(LocalTime.ToLocal(now, tz)),
            ["horizonEnd"] = J.Iso(horizon),
            ["socPercent"] = socPercent,
            ["storedAboveMinSoc_kWh"] = J.Round(stored),
            ["reserveForHouse_kWh"] = J.Round(reserve),
            ["surplus_kWh"] = J.Round(surplus),
            // SOC floor to use in a price-based export rule (e.g. SolaX Cloud "discharge to X% if price > Y")
            // so that selling never cuts into what the house needs until the next off-peak window
            ["recommendedExportFloorSoc"] = Math.Min(100, settings.MinSoc + (int)Math.Ceiling(reserve / settings.CapacityKwh * 100)),
            ["minPrice_PLN_kWh"] = export.MinPricePlnKwh,
            ["slots"] = J.Array(chosen.OrderBy(c => c.Slot.Start).Select(c => new JsonObject
            {
                ["start"] = J.Iso(LocalTime.ToLocal(c.Slot.Start, tz)),
                ["end"] = J.Iso(LocalTime.ToLocal(c.Slot.End, tz)),
                ["price_PLN_kWh"] = J.Round(c.Slot.PricePlnKwh * export.PriceMultiplier),
                ["export_kWh"] = J.Round(c.Energy),
                ["expectedHouseLoad_kWh"] = J.Round(SlotLoad(c.Slot)),
                ["dischargeSetpoint_kW"] = J.Round((c.Energy + SlotLoad(c.Slot)) / 0.25),
                ["revenue_PLN"] = J.Round(c.Energy * c.Slot.PricePlnKwh * export.PriceMultiplier),
            })),
            ["plannedExport_kWh"] = J.Round(surplus - remaining),
            ["currentSlot"] = current is { } cur
                ? new JsonObject
                {
                    ["export"] = true,
                    ["dischargeSetpoint_kW"] = J.Round((cur.Energy + SlotLoad(cur.Slot)) / 0.25),
                    ["expectedExport_kW"] = J.Round(cur.Energy / 0.25),
                    ["price_PLN_kWh"] = J.Round(cur.Slot.PricePlnKwh * export.PriceMultiplier),
                }
                : new JsonObject { ["export"] = false },
            ["dryRun"] = export.DryRun,
        };
    }

    /// <summary>House load from shaped realtime data: inverter AC output minus grid power (SolaX: + export, - import).
    /// Realtime data is up to ~5 minutes old. Returns null when a value is missing.</summary>
    public static double? LiveHouseLoadKw(JsonObject realtime)
    {
        var phases = (realtime["ac"] as JsonObject)?["phases"] as JsonArray;
        var gridW = J.Num((realtime["meter1"] as JsonObject)?["gridPower_W"]);
        if (phases is not { Count: > 0 } || gridW is null) return null;
        var powers = phases.Select(p => J.Num((p as JsonObject)?["power_W"])).ToList();
        if (powers.Any(p => p is null)) return null;
        return Math.Max(0.0, (powers.Aggregate(0.0, (sum, p) => sum + p!.Value) - gridW.Value) / 1000);
    }

    /// <summary>Hourly forecast entries are the average kW over the hour, which is kWh in that hour.</summary>
    public static Dictionary<DateTimeOffset, double> PvByHour(JsonObject forecast, string percentile)
    {
        var result = new Dictionary<DateTimeOffset, double>();
        foreach (var hour in forecast["hourly"]!.AsArray())
            result[J.ParseIso(J.Str(hour!["time"])!)] = J.Num(hour["power_kW"]![percentile]) ?? 0.0;
        return result;
    }
}
