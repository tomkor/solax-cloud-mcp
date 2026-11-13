using System.Globalization;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp.Tests;

/// <summary>The files in golden/ hold inputs and outputs recorded from the Python server before it was
/// replaced. These tests prove the C# planners give the same answers, also on DST change days.</summary>
public class GoldenTests
{
    static readonly JsonObject SettingsFile = TestData.Load("golden/settings.json").AsObject();

    static readonly List<Settings> AllSettings = SettingsFile["settings"]!.AsArray().Select(j => new Settings(
        Tz: LocalTime.Zone(J.Str(j!["tz"])!),
        Tariff: new Tariff(Windows(j["windows"])!, J.IsTrue(j["weekends"]), J.IsTrue(j["holidays"]), Windows(j["summerWindows"])),
        CapacityKwh: J.Num(j["capacity"])!.Value,
        ConsumptionProfile: Numbers(j["profile"])!,
        MinSoc: (int)J.Num(j["minSoc"])!.Value,
        MaxGridSoc: (int)J.Num(j["maxGridSoc"])!.Value,
        SafetyMarginKwh: J.Num(j["margin"])!.Value,
        Efficiency: J.Num(j["efficiency"])!.Value,
        Percentile: J.Str(j["percentile"])!,
        LeadMinutes: (int)J.Num(j["lead"])!.Value,
        DryRun: true,
        WeekendProfile: Numbers(j["weekendProfile"]),
        ChargePowerKw: J.Num(j["chargePower"])!.Value)).ToList();

    static readonly List<ExportSettings> AllExports = SettingsFile["exports"]!.AsArray().Select(j => new ExportSettings(
        J.Num(j!["minPrice"])!.Value, J.Num(j["maxPower"])!.Value, J.Num(j["minSlot"])!.Value, J.Num(j["multiplier"])!.Value, J.IsTrue(j["dryRun"]))).ToList();

    static List<Window>? Windows(JsonNode? node) => node?.AsArray().Select(w => new Window((int)J.Num(w![0])!.Value, (int)J.Num(w[1])!.Value)).ToList();

    static double[]? Numbers(JsonNode? node) => node?.AsArray().Select(v => J.Num(v)!.Value).ToArray();

    static Settings SettingsOf(JsonNode test) => AllSettings[(int)J.Num(test["settings"])!.Value];

    static Dictionary<DateTimeOffset, double> Pv(JsonNode test) =>
        test["pv"]!.AsArray().ToDictionary(p => J.ParseIso(J.Str(p![0])!), p => J.Num(p![1])!.Value);

    static Trigger TriggerOf(JsonNode test) => new(
        J.ParseIso(J.Str(test["runAt"])!), J.ParseIso(J.Str(test["windowStart"])!),
        new Window((int)J.Num(test["window"]![0])!.Value, (int)J.Num(test["window"]![1])!.Value));

    [Fact]
    public void Polish_holidays()
    {
        foreach (var (year, days) in TestData.Load("golden/holidays.json").AsObject())
        {
            var actual = Holidays.Polish(int.Parse(year)).Order().Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Assert.Equal(days!.AsArray().Select(d => J.Str(d)), actual);
        }
    }

    [Fact]
    public void Offpeak_zones_and_house_load_for_every_hour()
    {
        foreach (var test in TestData.Load("golden/offpeak.json").AsArray())
        {
            var settings = SettingsOf(test!);
            var start = DateTime.ParseExact(J.Str(test!["start"])!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var offpeak = J.Str(test["offpeak"])!;
            var load = test["load"]!.AsArray();
            for (var hour = 0; hour < offpeak.Length; hour++)
            {
                var local = LocalTime.At(start.AddHours(hour), settings.Tz);
                Assert.True(offpeak[hour] == '1' == settings.Tariff.IsOffpeak(local), $"is_offpeak differs at {local}");
                Assert.Equal(J.Num(load[hour]), settings.LoadKwh(local));
            }
        }
    }

    [Fact]
    public void Next_trigger()
    {
        foreach (var test in TestData.Load("golden/next_trigger.json").AsArray())
        {
            var trigger = Planner.NextTrigger(SettingsOf(test!), J.ParseIso(J.Str(test!["after"])!));
            var context = $"settings {test["settings"]}, after {test["after"]}";
            Assert.True(J.Str(test["runAt"]) == J.Iso(trigger.RunAt, seconds: true), $"{context}: runAt {J.Iso(trigger.RunAt, true)}, expected {test["runAt"]}");
            Assert.True(J.Str(test["windowStart"]) == J.Iso(trigger.WindowStart, seconds: true), $"{context}: windowStart {J.Iso(trigger.WindowStart, true)}, expected {test["windowStart"]}");
            Assert.Equal(TriggerOf(test).Window, trigger.Window);
        }
    }

    [Fact]
    public void Charge_plan()
    {
        var tests = TestData.Load("golden/compute_plan.json").AsArray();
        for (var i = 0; i < tests.Count; i++)
            JsonAssert.Equal(tests[i]!["plan"], Planner.ComputePlan(SettingsOf(tests[i]!), TriggerOf(tests[i]!), Pv(tests[i]!)), $"plan[{i}]");
    }

    [Fact]
    public void Export_plan()
    {
        var tests = TestData.Load("golden/export_plan.json").AsArray();
        for (var i = 0; i < tests.Count; i++)
        {
            var test = tests[i]!;
            var prices = test["prices"]!.AsArray().Select(p =>
            {
                var start = J.ParseIso(J.Str(p![0])!);
                return new PriceSlot(start, start.AddMinutes(15), J.Num(p[1])!.Value);
            }).ToList();
            var plan = Planner.ComputeExportPlan(
                SettingsOf(test), AllExports[(int)J.Num(test["export"])!.Value], J.ParseIso(J.Str(test["now"])!), J.Num(test["soc"])!.Value, Pv(test), prices);
            JsonAssert.Equal(test["plan"], plan, $"export[{i}] now {test["now"]}");
        }
    }

    [Fact]
    public void Forecast_shape()
    {
        var tests = TestData.Load("golden/shape_forecast.json").AsArray();
        for (var i = 0; i < tests.Count; i++)
        {
            var test = tests[i]!;
            var periods = test["periods"]!.AsArray().Select(p => new ForecastPeriod(J.ParseIso(J.Str(p![0])!), J.Num(p[1])!.Value, J.Num(p[2])!.Value, J.Num(p[3])!.Value));
            var shaped = SolcastForecast.ShapeForecast(periods, LocalTime.Zone(J.Str(test["tz"])!), J.ParseIso(J.Str(test["now"])!), (int)J.Num(test["hours"])!.Value);
            // Deliberate difference: Python merged the two 02:00 hours of the autumn DST night into one
            // entry; C# keeps them apart. Leave that hour out of the comparison.
            foreach (var hourly in new[] { test["shaped"]!["hourly"]!.AsArray(), shaped["hourly"]!.AsArray() })
                foreach (var entry in hourly.Where(h => J.Str(h!["time"])!.StartsWith("2026-10-25T02:00")).ToList())
                    hourly.Remove(entry);
            JsonAssert.Equal(test["shaped"], shaped, $"forecast[{i}]");
        }
    }

    [Fact]
    public void Realtime_shape()
    {
        var tests = TestData.Load("golden/shape_realtime.json").AsArray();
        for (var i = 0; i < tests.Count; i++)
            JsonAssert.Equal(tests[i]!["shaped"], Models.ShapeRealtimeResponse(tests[i]!["inverter"]!.AsObject(), tests[i]!["battery"] as JsonObject), $"realtime[{i}]");
    }

    [Fact]
    public void Pse_records()
    {
        foreach (var test in TestData.Load("golden/pse_records.json").AsArray())
        {
            var slots = RcePrices.ParseRecords(test!["records"]!.AsArray());
            var expected = test["slots"]!.AsArray();
            Assert.Equal(expected.Count, slots.Count);
            for (var i = 0; i < slots.Count; i++)
            {
                Assert.Equal(J.ParseIso(J.Str(expected[i]![1])!), slots[i].End);
                Assert.Equal(J.Num(expected[i]![2]), slots[i].PricePlnKwh);
                // Deliberate difference: Python made the slots of the repeated autumn hour 75 minutes long
                Assert.Equal(slots[i].End.AddMinutes(-15), slots[i].Start);
            }
        }
    }

    [Fact]
    public void Consumption_profiles()
    {
        var test = TestData.Load("golden/consumption.json");
        var samples = test["samples"]!.AsArray().Select(s => (DateTime.Parse(J.Str(s![0])!, CultureInfo.InvariantCulture), J.Num(s[1])!.Value));
        JsonAssert.Equal(test["profiles"], Consumption.HourlyProfiles(samples));
        foreach (var load in test["loads"]!.AsArray())
            Assert.Equal(J.Num(load![1]), Consumption.HouseLoadW(load[0]!.AsObject()));
    }
}
