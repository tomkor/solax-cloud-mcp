using System.Text.Json.Nodes;

namespace SolaxCloudMcp.Tests;

/// <summary>Tests for data models and response shaping.</summary>
public class ModelsTests
{
    static readonly JsonObject Inverter = TestData.Load("fixtures/inverter_realtime_success.json")["result"]![0]!.AsObject();
    static readonly JsonObject Battery = TestData.Load("fixtures/battery_realtime_success.json")["result"]![0]!.AsObject();

    static JsonObject Shape(bool withBattery = false) => Models.ShapeRealtimeResponse(Inverter, withBattery ? Battery : null);

    static double? Num(JsonNode? node) => J.Num(node);

    [Fact]
    public void Shape_has_all_top_level_keys() => Assert.Equal(
        ["device", "status", "pv", "mppt", "ac", "energy", "meter1", "meter2", "battery", "eps", "temperature", "misc"],
        Shape(withBattery: true).Select(p => p.Key));

    [Fact]
    public void No_battery_gives_null()
    {
        var shaped = Shape();
        Assert.True(shaped.ContainsKey("battery"));
        Assert.Null(shaped["battery"]);
    }

    [Fact]
    public void Inverter_status_null_is_not_reported()
    {
        var shaped = Shape();
        Assert.Null(shaped["status"]!["code"]);
        Assert.Equal("Not reported", J.Str(shaped["status"]!["description"]));
    }

    [Fact]
    public void Battery_status_is_decoded()
    {
        var status = Shape(withBattery: true)["battery"]!["status"]!;
        Assert.Equal(1, Num(status["code"]));
        Assert.Equal("Work", J.Str(status["description"]));
    }

    [Fact]
    public void Pv_strings_are_parsed_from_pvMap()
    {
        // The fixture has pv1 and pv2 with data, pv3 and pv4 all null
        var pv = Shape()["pv"]!.AsArray();
        Assert.Equal(2, pv.Count);
        Assert.Equal(1, Num(pv[0]!["string"]));
        Assert.Equal(0.4, Num(pv[0]!["current_A"]));
        Assert.Equal(421.7, Num(pv[0]!["voltage_V"]));
        Assert.Equal(2, Num(pv[1]!["string"]));
        Assert.Equal(0.0, Num(pv[1]!["current_A"]));
    }

    [Fact]
    public void Mppt_trackers_are_parsed_from_mpptMap()
    {
        var mppt = Shape()["mppt"]!.AsObject();
        Assert.True(mppt.ContainsKey("trackers"));
        Assert.True(mppt.ContainsKey("totalPower_W"));
    }

    /// <summary>Regression: mppt.totalPower_W must read MPPTTotalInputPower (double 't').</summary>
    [Fact]
    public void Mppt_total_power_key_matches_raw_field() => Assert.Equal(360.0, Num(Shape()["mppt"]!["totalPower_W"]));

    [Fact]
    public void Ac_section_structure()
    {
        var ac = Shape()["ac"]!.AsObject();
        foreach (var key in (string[])["phases", "totalPower_W", "totalReactivePower", "powerFactor", "gridFrequency"]) Assert.True(ac.ContainsKey(key));
    }

    /// <summary>Regression: 'phase' must be the phase index (1/2/3), not the AC current value.</summary>
    [Fact]
    public void Ac_phase_numbers_are_not_current_values()
    {
        var phases = Shape()["ac"]!["phases"]!.AsArray();
        Assert.Equal([1, 2, 3], phases.Select(p => Num(p!["phase"])));
        Assert.NotEqual(phases.Select(p => Num(p!["phase"])), phases.Select(p => Num(p!["current_A"])));
    }

    [Fact]
    public void Energy_fields_pass_through()
    {
        var energy = Shape()["energy"]!;
        Assert.Equal(157.4, Num(energy["dailyYield_kWh"]));
        Assert.Equal(20465.3, Num(energy["totalYield_kWh"]));
    }

    [Fact]
    public void Battery_fields_pass_through()
    {
        var battery = Shape(withBattery: true)["battery"]!;
        Assert.Equal(85.5, Num(battery["soc_percent"]));
        Assert.Equal(409.6, Num(battery["voltage_V"]));
        Assert.Equal(28.3, Num(battery["temperature_C"]));
    }

    [Fact]
    public void Device_fields()
    {
        var device = Shape()["device"]!;
        Assert.Equal("X3ABCD0123", J.Str(device["deviceSn"]));
        Assert.Equal("SE123456SP", J.Str(device["registerNo"]));
        Assert.Equal("2025-05-22 15:13:10", J.Str(device["plantLocalTime"]));
    }

    /// <summary>The fixture uses upper-case EPSL1Voltage; lower-casing the keys must handle it.</summary>
    [Fact]
    public void Eps_casing_is_normalized()
    {
        var eps = Shape()["eps"]!;
        Assert.Equal(0.0, Num(eps["voltage_V"]![0]));
        Assert.Equal(0.0, Num(eps["current_A"]![0]));
    }

    [Fact]
    public void Error_code_descriptions_cover_documented_codes() => Assert.Equal(
        new[] { 10000, 10001, 11500, 10200, 10400, 10401, 10402, 10403, 10404, 10405, 10406, 10500, 10505, 10506 }.Order(),
        Models.ErrorCodeDescriptions.Keys.Order());

    [Fact]
    public void Inverter_status_descriptions()
    {
        Assert.Equal("Waiting", Models.InverterDeviceStatusDescriptions[100]);
        Assert.Equal("Normal", Models.InverterDeviceStatusDescriptions[102]);
        Assert.Equal("TOU-Self use", Models.InverterDeviceStatusDescriptions[131]);
        Assert.Equal("PV&BAT Individual Setting-Target SOC Mode", Models.InverterDeviceStatusDescriptions[1309]);
    }

    [Fact]
    public void Battery_status_descriptions()
    {
        Assert.Equal("Idle", Models.BatteryDeviceStatusDescriptions[0]);
        Assert.Equal("Work", Models.BatteryDeviceStatusDescriptions[1]);
    }

    [Fact]
    public void Device_status_null_is_handled() => Assert.Equal("Not reported", J.Str(Shape()["status"]!["description"]));

    /// <summary>The API field has a typo: totalDevicharg instead of totalDeviceCharge.</summary>
    [Fact]
    public void Battery_total_charge_reads_the_misspelled_field() =>
        Assert.Equal(4250.75, Num(Shape(withBattery: true)["battery"]!["totalCharge_kWh"]));
}
