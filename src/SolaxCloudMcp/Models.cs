using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

/// <summary>Status code tables and response shaping.</summary>
public static class Models
{
    public static readonly IReadOnlyDictionary<int, string> ErrorCodeDescriptions = new Dictionary<int, string>
    {
        [10000] = "Operation successful",
        [10001] = "Operation failed",
        [11500] = "System busy, please try again later",
        [10200] = "Operation abnormality, please see the specific message content for details",
        [10400] = "Request not authenticated",
        [10401] = "Username or password incorrect",
        [10402] = "Request access_token authentication failed",
        [10403] = "Interface has no access rights",
        [10404] = "Callback function not configured",
        [10405] = "The number of API calls has been used up",
        [10406] = "The API call rate has reached the upper limit, please try again later",
        [10500] = "User has no device data permission",
        [10505] = "Device unauthorized",
        [10506] = "Plant unauthorized",
    };

    public static readonly IReadOnlyDictionary<int, string> InverterDeviceStatusDescriptions = new Dictionary<int, string>
    {
        [100] = "Waiting",
        [101] = "Self-check",
        [102] = "Normal",
        [103] = "Fault",
        [104] = "Permanent Fault Mode",
        [105] = "Update Mode",
        [106] = "EPS Check Mode",
        [107] = "EPS Mode",
        [108] = "Self-test",
        [109] = "Idle Mode",
        [110] = "Standby Mode",
        [111] = "Pv Wake Up Bat Mode",
        [112] = "Gen Check Mode",
        [113] = "Gen run Mode",
        [114] = "RSD Standby",
        [130] = "VPP mode",
        [131] = "TOU-Self use",
        [132] = "TOU-Charging",
        [133] = "TOU-Discharging",
        [134] = "TOU-Battery off",
        [135] = "TOU-Peak Shaving",
        [136] = "Normal Mode(Gen)",
        [137] = "Normal Mode(BAT-E)",
        [138] = "Normal Mode(BAT-H)",
        [139] = "EPS mode(BAT-H)",
        [140] = "Start Mode",
        [141] = "Normal Mode(R-1)",
        [142] = "Normal Mode(R-2)",
        [143] = "Normal Mode(R-3)",
        [144] = "Normal Mode(R-4)",
        [145] = "Normal Mode(R-5)",
        [146] = "Normal Mode(R-6)",
        [147] = "Normal Mode(R-7)",
        [150] = "Self Use",
        [151] = "Force Time Use",
        [152] = "Back Up Mode",
        [153] = "Feedin Priority",
        [154] = "Demand Mode",
        [155] = "ConstPowr Mode",
        [160] = "OpenAdr Mode",
        [170] = "STOP MODE",
        [171] = "DEBUG MODE",
        [174] = "Normal(Smart selfuse)",
        [175] = "Normal(Smart feedin)",
        [176] = "Normal(Smart Bat not discharge)",
        [177] = "Normal(WLV 0%)",
        [1301] = "Power Control Mode",
        [1302] = "Electric Quantity Target Control Mode",
        [1303] = "SOC Target Control Mode",
        [1304] = "Push Power -Positive/Negative Mode",
        [1305] = "Push Power - Zero Mode",
        [1306] = "Self-Consume -Charge/Discharge Mode",
        [1307] = "Self-Consume - Charge Only Mode",
        [1308] = "PV&BAT Individual Setting- Duration Mode",
        [1309] = "PV&BAT Individual Setting-Target SOC Mode",
    };

    public static readonly IReadOnlyDictionary<int, string> BatteryDeviceStatusDescriptions = new Dictionary<int, string>
    {
        [0] = "Idle",
        [1] = "Work",
    };

    /// <summary>Turn raw SolaX realtime responses into a grouped, decoded structure.</summary>
    /// <param name="inverterResult">The inverter data from the realtime_data endpoint.</param>
    /// <param name="batteryResult">The battery data, or null when there is no battery data.</param>
    public static JsonObject ShapeRealtimeResponse(JsonObject inverterResult, JsonObject? batteryResult)
    {
        // Keys are lower-cased: the SolaX docs mix casing (epsl1Voltage in the table, EPSL1Voltage in the example)
        var inverter = Lower(inverterResult);
        var mpptMap = Lower(inverter.GetValueOrDefault("mpptmap"));
        var pvMap = Lower(inverter.GetValueOrDefault("pvmap"));
        JsonNode? Inv(string key) => J.Copy(inverter.GetValueOrDefault(key));

        var acPhases = new JsonArray();
        for (var phase = 1; phase <= 3; phase++)
        {
            // A missing field counts as 0; a phase with no non-zero value is dropped
            JsonNode? Field(string name) => inverter.TryGetValue($"ac{name}{phase}", out var value) ? J.Copy(value) : JsonValue.Create(0);
            JsonNode? v = Field("voltage"), i = Field("current"), p = Field("power"), f = Field("frequency");
            if (IsTruthy(v) || IsTruthy(i) || IsTruthy(p) || IsTruthy(f))
                acPhases.Add(new JsonObject { ["phase"] = phase, ["voltage_V"] = v, ["current_A"] = i, ["power_W"] = p, ["frequency_Hz"] = f });
        }

        JsonObject? battery = null;
        if (batteryResult is not null)
        {
            var b = Lower(batteryResult);
            JsonNode? Bat(string key) => J.Copy(b.GetValueOrDefault(key));
            battery = new JsonObject
            {
                ["soc_percent"] = Bat("batterysoc"),
                ["remainingEnergy_kWh"] = Bat("batteryremainings"),
                ["soh_percent"] = Bat("batterysoh"),
                ["chargeDischargePower_W"] = Bat("chargedischargepower"),
                ["voltage_V"] = Bat("batteryvoltage"),
                ["current_A"] = Bat("batterycurrent"),
                ["temperature_C"] = Bat("batterytemperature"),
                ["cycleTimes"] = Bat("batterycycletimes"),
                ["totalCharge_kWh"] = Bat("totaldevicharg"), // sic: the API field name has this typo
                ["totalDischarge_kWh"] = Bat("totaldevicedischarge"),
                ["status"] = Status(b.GetValueOrDefault("devicestatus"), BatteryDeviceStatusDescriptions),
            };
        }

        JsonArray Eps(string suffix) => new(Inv($"epsl1{suffix}"), Inv($"epsl2{suffix}"), Inv($"epsl3{suffix}"));
        JsonObject Meter(string suffix) => new()
        {
            ["gridPower_W"] = Inv($"gridpower{suffix}"),
            ["todayImportEnergy_kWh"] = Inv($"todayimportenergy{suffix}"),
            ["totalImportEnergy_kWh"] = Inv($"totalimportenergy{suffix}"),
            ["todayExportEnergy_kWh"] = Inv($"todayexportenergy{suffix}"),
            ["totalExportEnergy_kWh"] = Inv($"totalexportenergy{suffix}"),
        };

        return new JsonObject
        {
            ["device"] = new JsonObject
            {
                ["deviceSn"] = Inv("devicesn"),
                ["registerNo"] = Inv("registerno"),
                ["dataTime"] = Inv("datatime"),
                ["plantLocalTime"] = Inv("plantlocaltime"),
            },
            ["status"] = Status(inverter.GetValueOrDefault("devicestatus"), InverterDeviceStatusDescriptions),
            ["pv"] = Strings(pvMap, "pv", "string"),
            ["mppt"] = new JsonObject
            {
                ["trackers"] = Strings(mpptMap, "mppt", "mppt"),
                ["totalPower_W"] = Inv("mppttotalinputpower"),
            },
            ["ac"] = new JsonObject
            {
                ["phases"] = acPhases,
                ["totalPower_W"] = Inv("totalactivepower"),
                ["totalReactivePower"] = Inv("totalreactivepower"),
                ["powerFactor"] = Inv("totalpowerfactor"),
                ["gridFrequency"] = Inv("gridfrequency"),
            },
            ["energy"] = new JsonObject
            {
                ["dailyYield_kWh"] = Inv("dailyyield"),
                ["totalYield_kWh"] = Inv("totalyield"),
                ["dailyACOutput_kWh"] = Inv("dailyacoutput"),
                ["totalACOutput_kWh"] = Inv("totalacoutput"),
            },
            ["meter1"] = Meter(""),
            ["meter2"] = Meter("m2"),
            ["battery"] = battery,
            ["eps"] = new JsonObject
            {
                ["voltage_V"] = Eps("voltage"),
                ["current_A"] = Eps("current"),
                ["activePower_W"] = Eps("activepower"),
                ["apparentPower_W"] = Eps("apparentpower"),
            },
            ["temperature"] = new JsonObject { ["inverter_C"] = Inv("invertertemperature") },
            ["misc"] = new JsonObject
            {
                ["l1l2Voltage_V"] = Inv("l1l2voltage"),
                ["l2l3Voltage_V"] = Inv("l2l3voltage"),
                ["l1l3Voltage_V"] = Inv("l1l3voltage"),
            },
        };
    }

    static Dictionary<string, JsonNode?> Lower(JsonNode? node)
    {
        var result = new Dictionary<string, JsonNode?>();
        if (node is JsonObject obj)
            foreach (var (key, value) in obj) result[key.ToLowerInvariant()] = value;
        return result;
    }

    /// <summary>PV strings / MPPT trackers: the count varies, so the indexes come from the keys ({prefix}{N}voltage, ...).</summary>
    static JsonArray Strings(Dictionary<string, JsonNode?> map, string prefix, string indexName)
    {
        var indexes = new SortedSet<int>();
        foreach (var key in map.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !(key.EndsWith("voltage") || key.EndsWith("current") || key.EndsWith("power"))) continue;
            // The suffix is cut as 7 characters ("voltage"/"current"), so "power" keys give no index. Kept as in the old server.
            var end = key.Length - 7;
            if (end > prefix.Length && int.TryParse(key.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                indexes.Add(index);
        }

        var result = new JsonArray();
        foreach (var index in indexes)
        {
            var voltage = map.GetValueOrDefault($"{prefix}{index}voltage");
            var current = map.GetValueOrDefault($"{prefix}{index}current");
            var power = map.GetValueOrDefault($"{prefix}{index}power");
            if (voltage is null && current is null && power is null) continue;
            result.Add(new JsonObject { [indexName] = index, ["voltage_V"] = J.Copy(voltage), ["current_A"] = J.Copy(current), ["power_W"] = J.Copy(power) });
        }
        return result;
    }

    static JsonObject Status(JsonNode? code, IReadOnlyDictionary<int, string> descriptions)
    {
        string description;
        if (code is null) description = "Not reported";
        else if (J.Num(code) is { } number && number == Math.Floor(number) && Math.Abs(number) < int.MaxValue && descriptions.TryGetValue((int)number, out var known)) description = known;
        else description = $"Unknown status code {J.Str(code) ?? code.ToJsonString()}";
        return new JsonObject { ["code"] = J.Copy(code), ["description"] = description };
    }

    static bool IsTruthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.Number => J.Num(value) != 0,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            _ => false,
        },
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        _ => true,
    };
}
