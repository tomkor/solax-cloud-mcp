using System.Globalization;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

/// <summary>Hourly house consumption profiles from SolaX inverter history.
///
/// House load per sample = inverter AC output - grid power (SolaX: + export, - import), the same
/// formula the export planner uses live. Prints AUTOMATION_CONSUMPTION_PROFILE (weekdays) and
/// AUTOMATION_WEEKEND_CONSUMPTION_PROFILE (weekends and Polish public holidays) for .env.
///
///     SolaxCloudMcp consumption --since 2026-10-02 [--until 2026-10-20]
/// </summary>
public static class Consumption
{
    static readonly TimeSpan MaxQuery = TimeSpan.FromHours(12); // SolaX history API limit per request

    public static double? HouseLoadW(JsonObject sample)
    {
        var grid = J.Num(sample["gridPower"]);
        var phases = new[] { J.Num(sample["acPower1"]), J.Num(sample["acPower2"]), J.Num(sample["acPower3"]) };
        if (grid is null || phases.Any(p => p is null)) return null;
        return Math.Max(0.0, phases.Aggregate(0.0, (sum, p) => sum + p!.Value) - grid.Value);
    }

    /// <summary>Average house load per local hour (W averaged over the hour == kWh), split by day type.</summary>
    public static JsonObject HourlyProfiles(IEnumerable<(DateTime Local, double Watts)> samples)
    {
        string[] kinds = ["weekday", "weekend"];
        var byKind = kinds.ToDictionary(k => k, _ => new Dictionary<int, List<double>>());
        var days = kinds.ToDictionary(k => k, _ => new HashSet<DateOnly>());
        foreach (var (local, watts) in samples)
        {
            var day = DateOnly.FromDateTime(local);
            var kind = Holidays.IsWeekendOrHoliday(day) ? "weekend" : "weekday";
            if (!byKind[kind].TryGetValue(local.Hour, out var list)) byKind[kind][local.Hour] = list = [];
            list.Add(watts);
            days[kind].Add(day);
        }

        var result = new JsonObject();
        foreach (var kind in kinds)
        {
            if (days[kind].Count == 0) continue;
            var hours = byKind[kind];
            if (hours.Count < 24) throw new ArgumentException($"No {kind} samples for some hours; use a longer period");
            var profile = Enumerable.Range(0, 24).Select(h => J.Round(hours[h].Aggregate(0.0, (sum, w) => sum + w) / hours[h].Count / 1000)).ToList();
            result[kind] = new JsonObject
            {
                ["days"] = days[kind].Count,
                ["daily_kWh"] = J.Round(profile.Aggregate(0.0, (sum, kwh) => sum + kwh), 1),
                ["profile"] = J.Array(profile.Select(kwh => (JsonNode)kwh)),
            };
        }
        return result;
    }

    static async Task<List<(DateTime Local, double Watts)>> CollectAsync(SolaxClient client, string deviceSn, DateTime start, DateTime end, TimeZoneInfo tz)
    {
        var samples = new List<(DateTime, double)>();
        for (var t = start; t < end;)
        {
            var chunkEnd = t + MaxQuery < end ? t + MaxQuery : end;
            var history = await client.FetchHistoryAsync(deviceSn, LocalTime.At(t, tz).ToUnixTimeMilliseconds(), LocalTime.At(chunkEnd, tz).ToUnixTimeMilliseconds());
            foreach (var node in history)
            {
                if (node is not JsonObject sample || HouseLoadW(sample) is not { } watts || J.Str(sample["plantLocalTime"]) is not { Length: > 0 } time) continue;
                var local = DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.None);
                // The API includes both ends; keep [t, chunkEnd) so boundary samples count once
                if (t <= local && local < chunkEnd) samples.Add((local, watts));
            }
            t = chunkEnd;
        }
        return samples;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        DateOnly? Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && DateOnly.TryParseExact(args[i + 1], "yyyy-MM-dd", out var day) ? day : null;
        }

        if (Option("--since") is not { } since)
        {
            Console.Error.WriteLine("usage: SolaxCloudMcp consumption --since YYYY-MM-DD [--until YYYY-MM-DD]\n" +
                                    "  --since  first full day\n  --until  first day NOT included (default: today)");
            return 2;
        }
        var tz = LocalTime.Zone(Config.SolarTimezone());
        if (Config.DefaultDeviceSn() is not { } deviceSn)
        {
            Console.Error.WriteLine("SOLAX_DEVICE_SN is not set");
            return 1;
        }
        var until = Option("--until") ?? DateOnly.FromDateTime(LocalTime.ToLocal(LocalTime.Now, tz).DateTime);

        var samples = await CollectAsync(new SolaxClient(new SolaxAuth()), deviceSn, since.ToDateTime(TimeOnly.MinValue), until.ToDateTime(TimeOnly.MinValue), tz);
        var names = new Dictionary<string, string> { ["weekday"] = "AUTOMATION_CONSUMPTION_PROFILE", ["weekend"] = "AUTOMATION_WEEKEND_CONSUMPTION_PROFILE" };
        foreach (var (kind, profile) in HourlyProfiles(samples))
        {
            Console.WriteLine($"# {kind}: {profile!["days"]} day(s), {profile["daily_kWh"]} kWh/day");
            Console.WriteLine($"{names[kind]}={string.Join(",", profile["profile"]!.AsArray().Select(x => x!.ToJsonString()))}");
        }
        return 0;
    }
}
