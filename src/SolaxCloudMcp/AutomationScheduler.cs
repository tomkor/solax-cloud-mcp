using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

/// <summary>Background tasks: the charge planner before each off-peak window, and the export loop every 15 minutes.</summary>
public class AutomationScheduler
{
    /// <summary>Decisions kept in memory and returned by the history endpoint.</summary>
    const int HistorySize = 200;

    enum Remote { Unknown, On, Off }

    static ILogger Log => AppLog.For<AutomationScheduler>();

    readonly SolaxService _service;
    readonly SolaxClient _client;
    readonly SolcastForecast _forecast;
    readonly RcePrices _prices;
    readonly CancellationTokenSource _stop = new();
    // Charge planner and export loop must never write to the inverter at the same time
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly List<JsonObject> _history = [];
    Task? _task;
    Task? _exportTask;
    // Whether the inverter is in remote control (export). Unknown e.g. after a restart
    volatile Remote _remoteControl = Remote.Unknown;
    volatile TaskCompletionSource _exportWake = new();

    public Settings Settings { get; }
    public ExportSettings? Export { get; }
    public JsonObject? LastResult { get; private set; }
    public JsonObject? LastExport { get; private set; }
    /// <summary>Latest shaped realtime data read by the export loop, so the dashboard needs no extra SolaX call.</summary>
    public JsonObject? LastRealtime { get; private set; }
    public Trigger? NextRun { get; private set; }
    /// <summary>True during an off-peak window with grid charging; the export loop then keeps off the inverter.</summary>
    public bool Charging { get; set; }
    /// <summary>Export on/off switch from the dashboard; EXPORT_ENABLED only decides whether the planner exists.</summary>
    public bool ExportActive { get; private set; }
    /// <summary>How often SOC is checked during a charge window (re-sends the charge command while below target).</summary>
    public TimeSpan ChargeCheck { get; set; } = TimeSpan.FromMinutes(15);

    public AutomationScheduler(Settings settings, ExportSettings? export, SolaxService service, SolaxClient client, SolcastForecast forecast, RcePrices prices)
    {
        (Settings, Export, _service, _client, _forecast, _prices) = (settings, export, service, client, forecast, prices);
        ExportActive = LoadSettings()["exportActive"] is not JsonValue value || value.GetValueKind() != JsonValueKind.False;
        if (Config.DecisionsFile() is { } path && File.Exists(path))
        {
            // A broken history file must never stop the server: skip bad lines
            foreach (var line in File.ReadLines(path).TakeLast(HistorySize))
            {
                try
                {
                    if (JsonNode.Parse(line) is JsonObject entry) _history.Add(entry);
                }
                catch (JsonException)
                {
                }
            }
        }
    }

    static string DeviceSn => Config.DefaultDeviceSn() ?? "";

    /// <summary>Decisions, oldest first. A copy, so callers can read it while the loops add entries.</summary>
    public List<JsonObject> History
    {
        get
        {
            lock (_history) return _history.Select(e => e.DeepClone().AsObject()).ToList();
        }
    }

    /// <summary>Read SETTINGS_FILE. A missing or broken file must never stop the server: use defaults.</summary>
    static JsonObject LoadSettings()
    {
        var path = Config.SettingsFile();
        if (path is null || !File.Exists(path)) return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.LogError(e, "Cannot read settings from {Path}, using defaults", path);
            return [];
        }
    }

    /// <summary>Turn the export loop on or off and save it to SETTINGS_FILE.
    /// Returns whether the setting was saved to a file. Throws IOException if the file cannot be
    /// written; the switch is then not changed. Turning export off also leaves remote control now.</summary>
    public async Task<bool> SetExportActiveAsync(bool active)
    {
        var path = Config.SettingsFile();
        if (path is not null)
        {
            var data = LoadSettings();
            data["exportActive"] = active;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, data.ToJsonString());
            File.Move(tmp, path, overwrite: true);
        }
        ExportActive = active;
        Record("export", new JsonObject { ["action"] = active ? "enabled" : "disabled" });
        if (!active && Export is { DryRun: false } && _remoteControl != Remote.Off) await ExitRemoteControlLockedAsync();
        // Run the loop now, so the dashboard shows the new state without waiting for the next slot
        _exportWake.TrySetResult();
        return path is not null;
    }

    /// <summary>Keep a decision in memory and append it to DECISIONS_FILE. Never throws.</summary>
    public void Record(string kind, JsonObject entry)
    {
        var full = new JsonObject { ["time"] = J.Iso(LocalTime.Now, seconds: true), ["kind"] = kind };
        foreach (var (key, value) in entry.ToList())
        {
            entry.Remove(key);
            full[key] = value;
        }
        lock (_history)
        {
            _history.Add(full);
            if (_history.Count > HistorySize) _history.RemoveAt(0);
        }
        if (Config.DecisionsFile() is not { } path) return;
        // shortcut: the file is never rotated (~100 B per decision, a few dozen a day); rotate it if it grows too big
        try
        {
            File.AppendAllText(path, full.ToJsonString() + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogError(e, "Cannot write decision history to {Path}", path);
        }
    }

    /// <summary>Compute the grid-charge plan for <paramref name="trigger"/> (never writes; ChargeWindowAsync executes it).</summary>
    public async Task<JsonObject> RunAsync(Trigger trigger)
    {
        var forecast = await _forecast.GetSolarForecastAsync(48);
        var plan = Planner.ComputePlan(Settings, trigger, Planner.PvByHour(forecast, Settings.Percentile));
        plan["forecastFetchedAt"] = J.Copy(forecast["fetchedAt"]);
        plan["forecastStale"] = J.Copy(forecast["stale"]);
        plan["dryRun"] = Settings.DryRun;
        return plan;
    }

    /// <summary>From window start to window end: charge from the grid whenever SOC is below the target.
    ///
    /// The inverter stops by itself at the target SOC. The command is re-sent every ChargeCheck while
    /// SOC is below target (house load may pull it back down), and remote control is always left at
    /// the window end, also on errors and on server stop.</summary>
    public async Task ChargeWindowAsync(JsonObject plan, CancellationToken stop = default)
    {
        var start = J.ParseIso(J.Str(plan["windowStart"])!);
        var end = J.ParseIso(J.Str(plan["windowEnd"])!);
        var target = (int)J.Num(plan["targetSoc"])!.Value;
        var watts = (int)Math.Round(Settings.ChargePowerKw * 1000, MidpointRounding.ToEven);
        if (start - LocalTime.Now is var wait && wait > TimeSpan.Zero) await Task.Delay(wait, stop);
        Charging = true;
        try
        {
            while (end - LocalTime.Now is var left && left > TimeSpan.Zero)
            {
                try
                {
                    var battery = (await _service.GetRealtimeDataAsync()) ["battery"] as JsonObject ?? throw new InvalidOperationException("No battery data from SolaX");
                    if (J.Num(battery["soc_percent"]) is { } soc && soc < target)
                    {
                        await _writeLock.WaitAsync();
                        try
                        {
                            await _client.ChargeToSocAsync(DeviceSn, watts, target);
                        }
                        finally
                        {
                            _writeLock.Release();
                        }
                        Log.LogWarning("Grid charge: {Watts} W until {Target}% SOC (SOC now {Soc}%)", watts, target, soc);
                        Record("charge", new JsonObject { ["action"] = "grid charge", ["setpoint_W"] = watts, ["stopSoc"] = target, ["socPercent"] = soc });
                    }
                }
                catch (Exception e) // retry at the next check; never leave the window early
                {
                    Log.LogError(e, "Grid charge command failed");
                    Record("charge", new JsonObject { ["error"] = $"{e.GetType().Name} (details in server logs)" });
                }
                await Task.Delay(left < ChargeCheck ? left : ChargeCheck, stop);
            }
        }
        finally
        {
            // Not cancellable: on server stop the inverter must still leave remote control
            await ExitRemoteControlLockedAsync();
            Charging = false;
        }
    }

    /// <summary>Compute the export plan from current SOC, PV forecast and RCE prices (never writes).</summary>
    public async Task<JsonObject> ExportPlanAsync(DateTimeOffset? now = null)
    {
        if (Export is null) throw new InvalidOperationException("Export planner is not enabled");
        var realtime = await _service.GetRealtimeDataAsync();
        LastRealtime = realtime;
        var soc = J.Num((realtime["battery"] as JsonObject)?["soc_percent"]) ?? throw new ToolException("Battery SOC not available from SolaX realtime data");
        Dictionary<DateTimeOffset, double>? pvByHour = null;
        try
        {
            pvByHour = Planner.PvByHour(await _forecast.GetSolarForecastAsync(48), Settings.Percentile);
        }
        catch (SolcastException e)
        {
            // Assume no PV: the house reserve only grows, so selling stays safe (e.g. Solcast daily limit)
            Log.LogWarning("Export planner without PV forecast (assuming 0 kW): {Error}", e.Message);
        }
        var prices = await _prices.GetRcePricesAsync();
        var plan = Planner.ComputeExportPlan(Settings, Export, now ?? LocalTime.Now, soc, pvByHour ?? [], prices);
        plan["pvForecastAvailable"] = pvByHour is not null;
        plan["liveHouseLoad_kW"] = Planner.LiveHouseLoadKw(realtime) is { } house ? J.Round(house) : null;
        return plan;
    }

    /// <summary>Send the current slot's export command, or leave remote control when not exporting.
    ///
    /// Uses soc_target_control_mode with the export floor as target: it has no duration, so the next
    /// slot must always re-send or exit. If the server dies, the inverter still stops at the floor SOC.</summary>
    public async Task<JsonObject> ApplyExportAsync(JsonObject plan)
    {
        var slot = plan["currentSlot"]!.AsObject();
        var floor = (int)J.Num(plan["recommendedExportFloorSoc"])!.Value;
        // ExportActive is checked under the lock: a switch-off during planning must not discharge
        await _writeLock.WaitAsync();
        try
        {
            if (Charging) return new JsonObject { ["action"] = "none" };
            if (ExportActive && J.IsTrue(slot["export"]) && J.Num(plan["socPercent"]) > floor)
            {
                // AC-port setpoint: planned export plus the house load measured now (profile if unknown)
                var setpointKw = J.Num(plan["liveHouseLoad_kW"]) is { } house
                    ? J.Num(slot["expectedExport_kW"])!.Value + house
                    : J.Num(slot["dischargeSetpoint_kW"])!.Value;
                var watts = (int)Math.Round(Math.Min(setpointKw, Export!.MaxPowerKw) * 1000, MidpointRounding.ToEven);
                Log.LogWarning("Export: discharge {Watts} W until {Floor}% SOC on {DeviceSn}", watts, floor, DeviceSn);
                _remoteControl = Remote.Unknown;
                try
                {
                    await _client.DischargeToSocAsync(DeviceSn, watts, floor);
                }
                catch (SolaxApiException)
                {
                    await ExitRemoteControlAsync();
                    throw;
                }
                _remoteControl = Remote.On;
                return new JsonObject { ["action"] = "discharge", ["setpoint_W"] = watts, ["stopSoc"] = floor };
            }
            if (_remoteControl != Remote.Off)
            {
                if (!await ExitRemoteControlAsync()) throw new SolaxApiException("Exit remote control failed");
                return new JsonObject { ["action"] = "exit" };
            }
            return new JsonObject { ["action"] = "none" };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Back to the normal work mode. Never throws on an API error; on failure the next slot retries.
    /// The caller must hold the write lock.</summary>
    async Task<bool> ExitRemoteControlAsync()
    {
        var deviceSn = DeviceSn;
        try
        {
            await _client.ExitVppModeAsync(deviceSn);
        }
        catch (SolaxApiException e)
        {
            Log.LogError(e, "Export: exit remote control failed on {DeviceSn}", deviceSn);
            return false;
        }
        Log.LogWarning("Export: exit remote control on {DeviceSn}", deviceSn);
        _remoteControl = Remote.Off;
        return true;
    }

    async Task ExitRemoteControlLockedAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            await ExitRemoteControlAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task ExportLoopAsync(CancellationToken stop)
    {
        // First run right away (a restart mid-slot must act on the current slot), then just after
        // each 15-minute slot boundary
        while (true)
        {
            if (!ExportActive)
            {
                // SetExportActiveAsync already left remote control; retry here only if that failed
                if (!Export!.DryRun && _remoteControl != Remote.Off && !Charging) await ExitRemoteControlLockedAsync();
                LastExport = new JsonObject { ["paused"] = true };
                await SleepUntilNextSlotAsync(stop);
                continue;
            }
            JsonObject result;
            try
            {
                var plan = await ExportPlanAsync();
                if (Export!.DryRun)
                {
                    if (J.IsTrue(plan["currentSlot"]!["export"])) Log.LogInformation("Export plan (dry run): would export {Slot}", plan["currentSlot"]!.ToJsonString());
                }
                else
                {
                    plan["execution"] = await ApplyExportAsync(plan);
                }
                result = plan;
            }
            catch (Exception e)
            {
                Log.LogError(e, "Export planner run failed");
                result = new JsonObject { ["error"] = $"{e.GetType().Name} (details in server logs)" };
                // Without a fresh plan, do not keep discharging
                if (!Export!.DryRun && _remoteControl != Remote.Off && !Charging) await ExitRemoteControlLockedAsync();
            }
            LastExport = result;
            RecordExport(result);
            await SleepUntilNextSlotAsync(stop);
        }
    }

    /// <summary>Sleep until just after the next 15-minute slot boundary, or until the export switch changes.</summary>
    async Task SleepUntilNextSlotAsync(CancellationToken stop)
    {
        var now = LocalTime.Now;
        var nextSlot = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(15 - now.Minute % 15);
        var wake = _exportWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAny(wake.Task, Task.Delay(nextSlot - now + TimeSpan.FromSeconds(5), stop));
        stop.ThrowIfCancellationRequested();
    }

    /// <summary>Log export runs that did or would do something; idle 15-minute runs are skipped.</summary>
    public void RecordExport(JsonObject plan)
    {
        if (plan.ContainsKey("error"))
        {
            Record("export", new JsonObject { ["error"] = J.Copy(plan["error"]) });
            return;
        }
        var execution = plan["execution"] as JsonObject;
        var slot = plan["currentSlot"]!.AsObject();
        var action = J.Str(execution?["action"]);
        if ((action ?? "none") == "none" && !(J.IsTrue(plan["dryRun"]) && J.IsTrue(slot["export"]))) return;
        Record("export", new JsonObject
        {
            ["dryRun"] = J.Copy(plan["dryRun"]),
            ["socPercent"] = J.Copy(plan["socPercent"]),
            ["action"] = action ?? "discharge (dry run)",
            ["setpoint_W"] = J.Copy(execution?["setpoint_W"]),
            ["stopSoc"] = J.Copy(execution is not null && execution.ContainsKey("stopSoc") ? execution["stopSoc"] : plan["recommendedExportFloorSoc"]),
            ["price_PLN_kWh"] = J.Copy(slot["price_PLN_kWh"]),
        });
    }

    public Task<JsonObject> PreviewAsync() => RunAsync(Planner.NextTrigger(Settings, LocalTime.Now));

    async Task LoopAsync(CancellationToken stop)
    {
        var after = LocalTime.Now;
        // shortcut: a restart inside an off-peak window skips that window's charging; handle the current window if it matters
        // State unknown after a restart (e.g. a crash mid-charge): back to normal Self Use
        if (!Settings.DryRun) await ExitRemoteControlLockedAsync();
        while (true)
        {
            var trigger = Planner.NextTrigger(Settings, after);
            NextRun = trigger;
            if (trigger.RunAt - LocalTime.Now is var delay && delay > TimeSpan.Zero) await Task.Delay(delay, stop);
            stop.ThrowIfCancellationRequested();
            after = trigger.RunAt;
            JsonObject result;
            try
            {
                result = await RunAsync(trigger);
                Log.LogInformation("Battery plan: {Plan}", result.ToJsonString());
            }
            catch (Exception e) // never let one failed run stop the scheduler
            {
                // Keep the inverter's previous settings when the forecast or SolaX call fails
                Log.LogError(e, "Battery automation run failed");
                result = new JsonObject { ["runAt"] = J.Iso(trigger.RunAt), ["error"] = $"{e.GetType().Name} (details in server logs)" };
            }
            LastResult = result;
            var entry = new JsonObject();
            foreach (var key in (string[])["runAt", "window", "targetSoc", "gridCharge", "needFromBattery_kWh", "dryRun", "error"])
                if (result.ContainsKey(key)) entry[key] = J.Copy(result[key]);
            Record("charge", entry);
            if (J.IsTrue(result["gridCharge"]) && !Settings.DryRun) await ChargeWindowAsync(result, stop);
        }
    }

    /// <summary>A loop that dies must be loud: without it the battery is not managed any more.</summary>
    static async Task GuardAsync(Func<Task> loop, string name)
    {
        try
        {
            await loop();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.LogCritical(e, "The {Name} loop stopped on an unexpected error. Restart the server.", name);
        }
    }

    public void Start()
    {
        // Set here too, so the status shows the next run before the loop task gets CPU time
        NextRun = Planner.NextTrigger(Settings, LocalTime.Now);
        _task = Task.Run(() => GuardAsync(() => LoopAsync(_stop.Token), "charge planner"));
        if (Export is not null) _exportTask = Task.Run(() => GuardAsync(() => ExportLoopAsync(_stop.Token), "export"));
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        foreach (var task in (Task?[])[_task, _exportTask])
            if (task is not null) await task;
        if (Export is { DryRun: false } && _remoteControl != Remote.Off) await ExitRemoteControlLockedAsync();
    }

    public JsonObject Status() => new()
    {
        ["enabled"] = true,
        ["dryRun"] = Settings.DryRun,
        ["nextRun"] = NextRun is { } next ? J.Iso(next.RunAt) : null,
        ["nextWindow"] = NextRun?.Window.ToString(),
        ["lastResult"] = J.Copy(LastResult),
        ["charging"] = Charging,
        ["export"] = Export is not null
            ? new JsonObject
            {
                ["enabled"] = true,
                ["active"] = ExportActive,
                ["persisted"] = Config.SettingsFile() is not null,
                ["dryRun"] = Export.DryRun,
                ["lastPlan"] = J.Copy(LastExport),
                ["lastRealtime"] = J.Copy(LastRealtime),
            }
            : new JsonObject { ["enabled"] = false },
    };
}
