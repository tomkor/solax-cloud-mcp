using System.Text.Json.Nodes;
using static SolaxCloudMcp.Tests.TestData;

namespace SolaxCloudMcp.Tests;

/// <summary>Tests for the G12w battery grid-charging planner, the export planner and the scheduler safety rules.</summary>
public class AutomationTests : EnvTest
{
    static readonly ExportSettings Export = new(MinPricePlnKwh: 1.0, MaxPowerKw: 8.0, MinSlotKwh: 0.2, PriceMultiplier: 1.0, DryRun: true);
    static readonly Dictionary<DateTimeOffset, double> NoPv = [];

    readonly FakeClient _client = new();
    readonly FakeForecast _forecast = new();
    readonly FakePrices _prices = new();
    readonly FakeService _service;

    public AutomationTests()
    {
        _service = new FakeService(_client, _forecast, _prices);
        SetEnv("SOLAX_DEVICE_SN", "SN1");
    }

    AutomationScheduler Scheduler(Settings settings, ExportSettings? export = null) =>
        new(settings, export, _service, _client, _forecast, _prices) { ChargeCheck = TimeSpan.FromMilliseconds(20) };

    AutomationScheduler LiveScheduler(double maxPowerKw = 5.0) => Scheduler(Base, Export with { MaxPowerKw = maxPowerKw, DryRun = false });

    static Trigger Trigger(DateTimeOffset start, int from, int to) => new(start, start, new Window(from, to));

    static PriceSlot Slot(int hour, int minute, double price)
    {
        var start = Local(2026, 10, 6, hour, minute);
        return new PriceSlot(start, start.AddMinutes(15), price);
    }

    static JsonObject Plan(bool export, double soc = 60, int floor = 40, double setpoint = 5.0, double? house = null) => new()
    {
        ["currentSlot"] = export
            ? new JsonObject { ["export"] = true, ["dischargeSetpoint_kW"] = setpoint, ["expectedExport_kW"] = setpoint - 0.5 }
            : new JsonObject { ["export"] = false },
        ["socPercent"] = soc,
        ["recommendedExportFloorSoc"] = floor,
        ["liveHouseLoad_kW"] = house,
    };

    static JsonObject ChargePlan(double seconds, int target = 40)
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonObject { ["windowStart"] = now.AddSeconds(-1).ToString("O"), ["windowEnd"] = now.AddSeconds(seconds).ToString("O"), ["targetSoc"] = target };
    }

    static double? Num(JsonNode? node) => J.Num(node);

    [Fact]
    public void Polish_holidays_2026()
    {
        var days = Holidays.Polish(2026);
        Assert.Contains(new DateOnly(2026, 4, 5), days); // Easter
        Assert.Contains(new DateOnly(2026, 4, 6), days); // Easter Monday
        Assert.Contains(new DateOnly(2026, 6, 4), days); // Corpus Christi
        Assert.Contains(new DateOnly(2026, 12, 24), days); // Christmas Eve (since 2025)
        Assert.DoesNotContain(new DateOnly(2026, 4, 7), days);
    }

    [Theory]
    [InlineData(2026, 10, 5, 23, true)] // Monday night
    [InlineData(2026, 10, 6, 5, true)] // Tuesday early morning (wraps midnight)
    [InlineData(2026, 10, 6, 6, false)] // Tuesday 06:00 peak
    [InlineData(2026, 10, 6, 13, true)] // midday window
    [InlineData(2026, 10, 6, 15, false)]
    [InlineData(2026, 10, 10, 18, true)] // Saturday
    [InlineData(2026, 11, 11, 18, true)] // Independence Day (Wednesday)
    [InlineData(2027, 4, 1, 13, false)] // summer: 13-15 is peak
    [InlineData(2027, 4, 1, 15, true)] // summer midday window 15-17
    [InlineData(2027, 4, 1, 23, true)]
    [InlineData(2027, 9, 30, 16, true)]
    [InlineData(2027, 10, 1, 16, false)] // winter again
    public void G12w_zones(int year, int month, int day, int hour, bool offpeak) =>
        Assert.Equal(offpeak, G12w.IsOffpeak(Local(year, month, day, hour)));

    [Fact]
    public void Cloudy_night_charges_for_the_morning_peak()
    {
        // Tuesday 22:00 window -> Wednesday 06:00-13:00 peak, no PV: 7 h * 1 kWh
        var plan = Planner.ComputePlan(Base, Trigger(Local(2026, 10, 6, 22), 22, 6), NoPv);
        Assert.Equal(7, Num(plan["peakSegment"]!["hours"]));
        Assert.Equal(7.0, Num(plan["peakSegment"]!["maxDeficit_kWh"]));
        // 7 / 0.9 + 1.0 = 8.78 kWh -> ceil(41.4%) = 42 -> 15 + 42
        Assert.Equal(8.78, Num(plan["needFromBattery_kWh"]));
        Assert.Equal(57, Num(plan["targetSoc"]));
        Assert.True(J.IsTrue(plan["gridCharge"]));
    }

    [Fact]
    public void Sunny_morning_skips_grid_charging()
    {
        var pv = Enumerable.Range(6, 7).ToDictionary(h => Local(2026, 10, 7, h), h => h < 8 ? 0.0 : 3.0);
        var plan = Planner.ComputePlan(Base, Trigger(Local(2026, 10, 6, 22), 22, 6), pv);
        // Deficit only 06-08 (2 kWh), surplus afterwards does not help those first hours
        Assert.Equal(2.0, Num(plan["peakSegment"]!["maxDeficit_kWh"]));
        Assert.Equal(15 + 16, Num(plan["targetSoc"])); // (2/0.9+1)/21.2 = 15.2% -> 16
    }

    [Fact]
    public void Pv_covering_everything_means_no_grid_charge()
    {
        var pv = Enumerable.Range(6, 7).ToDictionary(h => Local(2026, 10, 7, h), _ => 2.0);
        var plan = Planner.ComputePlan(Base, Trigger(Local(2026, 10, 6, 22), 22, 6), pv);
        Assert.Equal(0.0, Num(plan["needFromBattery_kWh"]));
        Assert.Equal(15, Num(plan["targetSoc"]));
        Assert.False(J.IsTrue(plan["gridCharge"]));
    }

    [Fact]
    public void Friday_night_before_the_weekend_does_not_charge()
    {
        var plan = Planner.ComputePlan(Base, Trigger(Local(2026, 10, 9, 22), 22, 6), NoPv);
        Assert.Equal(0, Num(plan["peakSegment"]!["hours"]));
        Assert.False(J.IsTrue(plan["gridCharge"]));
    }

    [Fact]
    public void Midday_window_covers_the_evening_peak_and_caps_at_max()
    {
        var plan = Planner.ComputePlan(Base with { ConsumptionProfile = Flat(4.0), MaxGridSoc = 90 }, Trigger(Local(2026, 10, 6, 13), 13, 15), NoPv);
        Assert.Equal(7, Num(plan["peakSegment"]!["hours"])); // 15:00-22:00
        Assert.Equal(90, Num(plan["targetSoc"]));
    }

    [Fact]
    public void Next_trigger_order_and_lead()
    {
        var after = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero); // 12:00 local
        var first = Planner.NextTrigger(Base, after);
        Assert.Equal(new Window(13, 15), first.Window);
        Assert.Equal(Local(2026, 10, 6, 12, 50), first.RunAt);
        var second = Planner.NextTrigger(Base, first.RunAt);
        Assert.Equal(new Window(22, 6), second.Window);
        Assert.Equal(Local(2026, 10, 6, 21, 50), second.RunAt);
    }

    [Fact]
    public async Task Run_plans_the_window_with_the_forecast()
    {
        _forecast.Respond = _ => JsonNode.Parse(
            """{"fetchedAt": "2026-10-06T21:00:00+02:00", "stale": false, "hourly": [{"time": "2026-10-07T06:00+02:00", "power_kW": {"p10": 0.0, "p50": 0.5, "p90": 1.0}}]}""")!.AsObject();
        var result = await Scheduler(Base with { DryRun = false }).RunAsync(Trigger(Local(2026, 10, 6, 22), 22, 6));
        Assert.Equal(0.5, Num(result["peakSegment"]!["pvForecast_kWh"]));
        Assert.Equal("2026-10-07T06:00+02:00", J.Str(result["windowEnd"]));
        Assert.True(J.IsTrue(result["gridCharge"]));
        Assert.Equal("2026-10-06T21:00:00+02:00", J.Str(result["forecastFetchedAt"]));
        Assert.False(J.IsTrue(result["dryRun"]));
    }

    [Fact]
    public void Weekend_and_holiday_profile()
    {
        var settings = Base with { WeekendProfile = Flat(2.0) };
        Assert.Equal(1.0, settings.LoadKwh(Local(2026, 10, 6, 12))); // Tuesday
        Assert.Equal(2.0, settings.LoadKwh(Local(2026, 10, 10, 12))); // Saturday
        Assert.Equal(2.0, settings.LoadKwh(Local(2026, 11, 11, 12))); // Independence Day (Wednesday)
        Assert.Equal(1.0, Base.LoadKwh(Local(2026, 10, 10, 12))); // no weekend profile -> weekday
    }

    [Fact]
    public void Export_reserve_uses_the_weekend_profile()
    {
        // Saturday 17:00, horizon = 22:00 window: 5 h * 2 kWh weekend load -> 10/0.9 + 1 = 12.11
        var plan = Planner.ComputeExportPlan(Base with { WeekendProfile = Flat(2.0) }, Export, Local(2026, 10, 10, 17), 100, NoPv, []);
        Assert.Equal(12.11, Num(plan["reserveForHouse_kWh"]));
    }

    [Fact]
    public void Settings_from_env()
    {
        SetEnv("BATTERY_CAPACITY_KWH", "21.2");
        SetEnv("AUTOMATION_DAILY_CONSUMPTION_KWH", "12");
        SetEnv("SOLAR_TIMEZONE", "Europe/Warsaw");
        var settings = SolaxCloudMcp.Settings.FromEnv();
        Assert.True(settings.DryRun); // safe default
        Assert.Equal(Flat(0.5), settings.ConsumptionProfile);
        Assert.Equal([new Window(22, 6), new Window(13, 15)], settings.Tariff.Windows);
        Assert.Equal([new Window(22, 6), new Window(15, 17)], settings.Tariff.SummerWindows);
        Assert.Null(settings.WeekendProfile);
        Assert.Equal(5.0, settings.ChargePowerKw);
        Assert.Equal("Europe/Warsaw", settings.Tz.Id);
        // Custom windows stay year-round unless summer windows are set too
        SetEnv("TARIFF_OFFPEAK_WINDOWS", "23:00-07:00");
        Assert.Equal([new Window(23, 7)], SolaxCloudMcp.Settings.FromEnv().Tariff.WindowsFor(new DateOnly(2027, 7, 1)));
        SetEnv("TARIFF_OFFPEAK_WINDOWS", null);
        SetEnv("AUTOMATION_WEEKEND_DAILY_CONSUMPTION_KWH", "19.2");
        Assert.All(SolaxCloudMcp.Settings.FromEnv().WeekendProfile!, kwh => Assert.Equal(0.8, kwh, 12));
    }

    /// <summary>Only an explicit "0" turns dry run off. Dry run is the safety default.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("false", true)]
    [InlineData("0", false)]
    public void Dry_run_stays_on_unless_set_to_0(string? value, bool dryRun)
    {
        SetEnv("BATTERY_CAPACITY_KWH", "21.2");
        SetEnv("AUTOMATION_DAILY_CONSUMPTION_KWH", "12");
        SetEnv("EXPORT_MAX_POWER_KW", "5");
        SetEnv("AUTOMATION_DRY_RUN", value);
        SetEnv("EXPORT_DRY_RUN", value);
        Assert.Equal(dryRun, SolaxCloudMcp.Settings.FromEnv().DryRun);
        Assert.Equal(dryRun, ExportSettings.FromEnv().DryRun);
    }

    [Theory]
    [InlineData("")] // no capacity
    [InlineData("BATTERY_CAPACITY_KWH=21.2")] // no consumption
    [InlineData("BATTERY_CAPACITY_KWH=21.2;AUTOMATION_DAILY_CONSUMPTION_KWH=12;TARIFF_OFFPEAK_WINDOWS=22:30-06:00")]
    [InlineData("BATTERY_CAPACITY_KWH=21.2;AUTOMATION_CONSUMPTION_PROFILE=1,2,3")]
    [InlineData("BATTERY_CAPACITY_KWH=21.2;AUTOMATION_DAILY_CONSUMPTION_KWH=12;AUTOMATION_MIN_SOC=5")]
    [InlineData("BATTERY_CAPACITY_KWH=21.2;AUTOMATION_DAILY_CONSUMPTION_KWH=12;AUTOMATION_CHARGE_POWER_KW=0")]
    public void Settings_from_env_rejects_invalid(string env)
    {
        foreach (var pair in env.Split(';', StringSplitOptions.RemoveEmptyEntries)) SetEnv(pair.Split('=')[0], pair.Split('=')[1]);
        Assert.Throws<ConfigException>(SolaxCloudMcp.Settings.FromEnv);
    }

    [Fact]
    public void Export_sells_only_the_surplus_in_the_best_slots()
    {
        // Tuesday 17:00, horizon = 22:00 window; 5 h * 1 kWh house need, no PV -> reserve 5/0.9+1 = 6.56
        var now = Local(2026, 10, 6, 17);
        PriceSlot[] prices = [Slot(17, 0, 0.8), Slot(18, 0, 1.2), Slot(18, 15, 1.5), Slot(19, 0, 1.1), Slot(23, 0, 2.0)];
        // SOC 60% -> (60-15)% * 21.2 = 9.54 kWh stored; surplus = 9.54 - 6.56 = 2.98
        var plan = Planner.ComputeExportPlan(Base, Export, now, 60, NoPv, prices);
        Assert.Equal(6.56, Num(plan["reserveForHouse_kWh"]));
        Assert.Equal(15 + 31, Num(plan["recommendedExportFloorSoc"])); // 6.56 / 21.2 = 30.9% -> 31
        Assert.Equal(2.98, Num(plan["surplus_kWh"]));
        // 8 kW * 0.25 h = 2 kWh from battery, house takes 0.25 -> 1.75 exported per slot.
        // Best slot 18:15 gets 1.75, then 18:00 the remaining 1.23; 23:00 is beyond horizon
        var slots = plan["slots"]!.AsArray().Select(s => (J.Str(s!["start"])![11..16], Num(s["export_kWh"]), Num(s["dischargeSetpoint_kW"])));
        Assert.Equal([("18:00", 1.23, 5.94), ("18:15", 1.75, 8.0)], slots);
        Assert.Equal(2.98, Num(plan["plannedExport_kWh"]));
        Assert.Equal("""{"export":false}""", plan["currentSlot"]!.ToJsonString());
    }

    [Fact]
    public void Export_current_slot_and_threshold()
    {
        var plan = Planner.ComputeExportPlan(Base, Export, Local(2026, 10, 6, 18, 5), 100, NoPv, [Slot(18, 0, 1.3), Slot(18, 15, 0.99)]);
        Assert.True(J.IsTrue(plan["currentSlot"]!["export"]));
        Assert.Equal(8.0, Num(plan["currentSlot"]!["dischargeSetpoint_kW"]));
        Assert.Equal(7.0, Num(plan["currentSlot"]!["expectedExport_kW"])); // 1 kW house load served first
        Assert.Single(plan["slots"]!.AsArray()); // 0.99 below threshold
    }

    [Fact]
    public void Export_threshold_uses_the_net_price()
    {
        // 0.9 * 1.23 = 1.107 >= 1.0, but the threshold compares the net RCE price
        var plan = Planner.ComputeExportPlan(Base, Export with { PriceMultiplier = 1.23 }, Local(2026, 10, 6, 18, 5), 100, NoPv, [Slot(18, 0, 1.1), Slot(18, 15, 0.9)]);
        Assert.Equal(["18:00"], plan["slots"]!.AsArray().Select(s => J.Str(s!["start"])![11..16]));
        Assert.Equal(1.35, Num(plan["slots"]![0]!["price_PLN_kWh"])); // deposit value shown after the multiplier
    }

    [Fact]
    public void No_export_when_the_battery_is_needed_for_the_house()
    {
        var plan = Planner.ComputeExportPlan(Base, Export, Local(2026, 10, 6, 15), 30, NoPv, [Slot(18, 0, 3.0)]);
        Assert.Equal(0.0, Num(plan["surplus_kWh"]));
        Assert.Empty(plan["slots"]!.AsArray());
    }

    [Fact]
    public void Export_dry_run_defaults_on()
    {
        SetEnv("EXPORT_MAX_POWER_KW", "8");
        Assert.True(ExportSettings.FromEnv().DryRun);
        SetEnv("EXPORT_DRY_RUN", "0");
        Assert.False(ExportSettings.FromEnv().DryRun);
    }

    [Fact]
    public async Task Charge_window_charges_below_target_then_exits()
    {
        _service.SocReadings.AddRange([30, 45]);
        var scheduler = Scheduler(Base with { DryRun = false, ChargePowerKw = 5.0 });
        await scheduler.ChargeWindowAsync(ChargePlan(0.1));
        Assert.Equal(["charge SN1 5000 40", "exit SN1"], _client.Commands);
        Assert.False(scheduler.Charging);
        Assert.Equal("grid charge", J.Str(scheduler.History[^1]["action"]));
    }

    [Fact]
    public async Task Charge_window_resends_when_soc_drops()
    {
        _service.SocReadings.AddRange([41, 38, 41]);
        await Scheduler(Base with { DryRun = false }).ChargeWindowAsync(ChargePlan(0.1));
        Assert.Equal(["charge SN1 5000 40", "exit SN1"], _client.Commands);
    }

    [Fact]
    public async Task Failed_charge_still_exits_at_the_window_end()
    {
        _service.SocReadings.Add(20);
        _client.Fail = true;
        var scheduler = Scheduler(Base with { DryRun = false });
        await scheduler.ChargeWindowAsync(ChargePlan(0.05));
        Assert.Equal("exit SN1", _client.Commands[^1]);
        Assert.True(scheduler.History[^1].ContainsKey("error"));
    }

    /// <summary>Server stop in the middle of a charge window: the inverter must still leave remote control.</summary>
    [Fact]
    public async Task Stop_during_a_charge_window_still_exits_remote_control()
    {
        _service.SocReadings.Add(20);
        var scheduler = Scheduler(Base with { DryRun = false });
        scheduler.ChargeCheck = TimeSpan.FromSeconds(30);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.ChargeWindowAsync(ChargePlan(60), stop.Token));
        Assert.Equal(["charge SN1 5000 40", "exit SN1"], _client.Commands);
        Assert.False(scheduler.Charging);
    }

    [Fact]
    public async Task Export_keeps_off_the_inverter_while_charging()
    {
        var scheduler = LiveScheduler();
        scheduler.Charging = true;
        Assert.Equal("""{"action":"none"}""", (await scheduler.ApplyExportAsync(Plan(true))).ToJsonString());
        Assert.Empty(_client.Commands);
    }

    [Fact]
    public async Task Export_slot_discharges_to_the_floor_then_exits_once()
    {
        var scheduler = LiveScheduler(maxPowerKw: 4.0);
        Assert.Equal("""{"action":"discharge","setpoint_W":4000,"stopSoc":40}""", (await scheduler.ApplyExportAsync(Plan(true, setpoint: 4.6))).ToJsonString());
        Assert.Equal("exit", J.Str((await scheduler.ApplyExportAsync(Plan(false)))["action"]));
        Assert.Equal("none", J.Str((await scheduler.ApplyExportAsync(Plan(false)))["action"]));
        Assert.Equal(["discharge SN1 4000 40", "exit SN1"], _client.Commands);
    }

    [Fact]
    public async Task Unknown_state_after_a_restart_exits() =>
        Assert.Equal("exit", J.Str((await LiveScheduler().ApplyExportAsync(Plan(false)))["action"]));

    [Fact]
    public async Task Soc_at_the_floor_never_discharges()
    {
        await LiveScheduler().ApplyExportAsync(Plan(true, soc: 40, floor: 40));
        Assert.Equal(["exit SN1"], _client.Commands);
    }

    [Fact]
    public async Task Failed_discharge_exits_remote_control()
    {
        _client.Fail = true;
        await Assert.ThrowsAsync<SolaxApiException>(() => LiveScheduler().ApplyExportAsync(Plan(true)));
        Assert.Equal(["discharge SN1 5000 40", "exit SN1"], _client.Commands);
    }

    [Fact]
    public void House_load_above_the_setpoint_means_no_export_in_that_slot()
    {
        // 3 kW evening load, 3 kW setpoint -> nothing left for the grid
        var plan = Planner.ComputeExportPlan(Base with { ConsumptionProfile = Flat(3.0) }, Export with { MaxPowerKw = 3.0 }, Local(2026, 10, 6, 20), 100, NoPv, [Slot(20, 0, 2.0)]);
        Assert.Empty(plan["slots"]!.AsArray());
    }

    [Fact]
    public async Task Setpoint_uses_the_live_house_load()
    {
        var scheduler = LiveScheduler(maxPowerKw: 5.0);
        // Planned 3.5 kW export (profile load 0.5); washing machine running -> 2.1 kW measured
        Assert.Equal(4400, Num((await scheduler.ApplyExportAsync(Plan(true, setpoint: 4.0, house: 0.9)))["setpoint_W"]));
        Assert.Equal(5000, Num((await scheduler.ApplyExportAsync(Plan(true, setpoint: 4.0, house: 2.1)))["setpoint_W"])); // capped
    }

    [Fact]
    public void Live_house_load_from_realtime()
    {
        JsonObject Realtime(double? grid) => new()
        {
            ["ac"] = new JsonObject { ["phases"] = new JsonArray(new JsonObject { ["power_W"] = 1300 }, new JsonObject { ["power_W"] = 1300 }, new JsonObject { ["power_W"] = 1400 }) },
            ["meter1"] = new JsonObject { ["gridPower_W"] = grid },
        };
        Assert.Equal(0.9, Planner.LiveHouseLoadKw(Realtime(3100))); // 4.0 kW out, 3.1 kW exported
        Assert.Equal(4.5, Planner.LiveHouseLoadKw(Realtime(-500))); // importing 0.5 kW on top
        Assert.Null(Planner.LiveHouseLoadKw(Realtime(null)));
    }

    [Fact]
    public void Next_trigger_uses_summer_windows()
    {
        var trigger = Planner.NextTrigger(Base, Local(2027, 4, 1, 10)); // Thursday in summer
        Assert.Equal((new Window(15, 17), 15), (trigger.Window, trigger.WindowStart.Hour));
    }

    [Fact]
    public async Task Export_plan_without_solcast_assumes_no_pv()
    {
        _forecast.Respond = _ => throw new SolcastException("rate limit");
        _service.SocReadings.Add(90);
        var scheduler = Scheduler(Base, Export);
        var plan = await scheduler.ExportPlanAsync(Local(2026, 10, 6, 18));
        Assert.False(J.IsTrue(plan["pvForecastAvailable"]));
        Assert.True(plan.ContainsKey("liveHouseLoad_kW"));
        Assert.Equal("""{"battery":{"soc_percent":90}}""", scheduler.Status()["export"]!["lastRealtime"]!.ToJsonString());
        Assert.Equal(5.44, Num(plan["reserveForHouse_kWh"])); // 18-22: 4 h * 1 kWh / 0.9 + 1, no PV
    }

    [Fact]
    public void Decision_history_persists_and_skips_idle_export()
    {
        var path = Path.Combine(TempDir(), "decisions.jsonl");
        SetEnv("DECISIONS_FILE", path);
        JsonObject Ran(bool export, JsonObject execution)
        {
            var plan = Plan(export);
            plan["dryRun"] = false;
            plan["execution"] = execution;
            return plan;
        }
        var scheduler = LiveScheduler();
        scheduler.RecordExport(Ran(false, new JsonObject { ["action"] = "none" }));
        scheduler.RecordExport(Ran(true, new JsonObject { ["action"] = "discharge", ["setpoint_W"] = 5000, ["stopSoc"] = 40 }));
        scheduler.RecordExport(new JsonObject { ["error"] = "SolaxApiException (details in server logs)" });
        scheduler.Record("charge", new JsonObject { ["targetSoc"] = 45 });
        Assert.Equal(["export", "export", "charge"], scheduler.History.Select(e => J.Str(e["kind"])));
        Assert.Equal(5000, Num(scheduler.History[0]["setpoint_W"]));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+00:00$", J.Str(scheduler.History[0]["time"]));

        File.AppendAllText(path, "not json\n");
        Assert.Equal(["export", "export", "charge"], LiveScheduler().History.Select(e => J.Str(e["kind"])));
    }

    /// <summary>A history file written by the Python server must still load after the switch to C#.</summary>
    [Fact]
    public void Decision_history_from_the_python_server_is_read()
    {
        var path = Path.Combine(TempDir(), "decisions.jsonl");
        File.WriteAllText(path,
            """
            {"time": "2026-11-12T21:50:00+00:00", "kind": "charge", "runAt": "2026-11-12T22:50+01:00", "window": "22:00-06:00", "targetSoc": 57, "gridCharge": true, "needFromBattery_kWh": 8.78, "dryRun": false}
            {"time": "2026-11-13T06:15:07+00:00", "kind": "export", "dryRun": false, "socPercent": 62.0, "action": "discharge", "setpoint_W": 4400, "stopSoc": 46, "price_PLN_kWh": 1.35}

            """);
        SetEnv("DECISIONS_FILE", path);
        var history = LiveScheduler().History;
        Assert.Equal(["charge", "export"], history.Select(e => J.Str(e["kind"])));
        Assert.Equal(4400, Num(history[1]["setpoint_W"]));
    }

    [Fact]
    public async Task Export_switch_persists_and_stops_discharge()
    {
        SetEnv("SETTINGS_FILE", Path.Combine(TempDir(), "settings.json"));
        var scheduler = LiveScheduler();
        Assert.True(scheduler.ExportActive); // no file yet: EXPORT_ENABLED alone decides
        await scheduler.ApplyExportAsync(Plan(true));

        Assert.True(await scheduler.SetExportActiveAsync(false));
        Assert.Equal(["discharge SN1 5000 40", "exit SN1"], _client.Commands);
        // A plan computed before the switch-off must not discharge again
        Assert.Equal("none", J.Str((await scheduler.ApplyExportAsync(Plan(true)))["action"]));
        Assert.False(LiveScheduler().ExportActive); // survives a restart
        Assert.Equal("""{"exportActive":false}""", File.ReadAllText(Config.SettingsFile()!));

        await scheduler.SetExportActiveAsync(true);
        Assert.True(LiveScheduler().ExportActive);
    }

    [Fact]
    public void Broken_settings_file_keeps_export_on()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        File.WriteAllText(path, "not json");
        SetEnv("SETTINGS_FILE", path);
        Assert.True(LiveScheduler().ExportActive);
    }

    /// <summary>The whole export loop, live: plan, discharge, then exit remote control on server stop.</summary>
    [Fact]
    public async Task Export_loop_discharges_in_an_export_slot_and_exits_on_stop()
    {
        var now = DateTimeOffset.UtcNow;
        _service.SocReadings.Add(100);
        _prices.Respond = () => [new PriceSlot(now.AddMinutes(-20), now.AddMinutes(20), 5.0)];
        var scheduler = Scheduler(Base with { ConsumptionProfile = Flat(0.1) }, Export with { DryRun = false, MaxPowerKw = 5.0 });
        scheduler.Start();
        for (var i = 0; i < 100 && scheduler.LastExport is null; i++) await Task.Delay(20);
        await scheduler.StopAsync();

        Assert.Equal("discharge", J.Str(scheduler.LastExport!["execution"]!["action"]));
        Assert.StartsWith("discharge SN1 ", _client.Commands[0]);
        Assert.Equal("exit SN1", _client.Commands[^1]);
        Assert.Equal("discharge", J.Str(scheduler.History[^1]["action"]));
    }

    /// <summary>Dry run must never send a command to the inverter, also not at start or stop.</summary>
    [Fact]
    public async Task Dry_run_loops_send_no_commands()
    {
        var now = DateTimeOffset.UtcNow;
        _service.SocReadings.Add(100);
        _prices.Respond = () => [new PriceSlot(now.AddMinutes(-20), now.AddMinutes(20), 5.0)];
        var scheduler = Scheduler(Base with { ConsumptionProfile = Flat(0.1) }, Export);
        scheduler.Start();
        for (var i = 0; i < 100 && scheduler.LastExport is null; i++) await Task.Delay(20);
        await scheduler.StopAsync();

        Assert.True(J.IsTrue(scheduler.LastExport!["currentSlot"]!["export"]));
        Assert.Empty(_client.Commands);
        Assert.Equal("discharge (dry run)", J.Str(scheduler.History[^1]["action"]));
    }
}
