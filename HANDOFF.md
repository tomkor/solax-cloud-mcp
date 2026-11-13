# Handoff: SolaX automation (for the local agent)

## Status summary (written 2026-11-13 from the dated notes below)

- All work up to PR #12 is merged into `master`. 2026-11-13: the server was rewritten from Python to C# (.NET 10) on the branch `csharp-rewrite`; routes, `.env` settings, state files and planner results are the same (see "Differences from the Python version" at the end). Tests: `dotnet test` (189 pass). The notes below were written for the Python version; file names are updated, function names in old notes may still be the Python ones.
- The HTTP server runs on the Proxmox LXC (see DEPLOYMENT.md) against the real SolaX, Solcast and PSE APIs.
- 2026-10-05: grid charging switched to live (`AUTOMATION_DRY_RUN=0`).
- 2026-10-11: the owner turned off the SolaX Cloud app rules.
- 2026-10-13: the X3-NEO-LV accepts `chargeFromGridEnable` / `chargeUpperSoc` but does not act on them. PR #12 changed grid charging to remote control (`soc_target_control_mode`, positive power).
- 2026-11-01: PGE switched settlement to hourly RCE and export went live (`EXPORT_DRY_RUN=0`). Since PR #12, `EXPORT_MIN_PRICE_PLN_KWH` is compared with the net RCE price.
- Dashboard at `/dashboard` (PRs #5-#11): plans, decision history (`DECISIONS_FILE`), export on/off switch (`SETTINGS_FILE`).
- Still open: Task 3 item 1 (run `export-test` once with the owner watching), which app endpoint the manual 5 kW discharge used, and the DSO question at the end.

The sections below are the original handoff and the work log. Where they disagree with this status, this status wins.

---

Context from a cloud session. When this was written, the cloud sandbox could not reach SolaX, Solcast or PSE, so nothing had been run against real APIs or hardware yet. The tasks below are marked **Done** where they were finished later on the real installation.

## Installation

- Inverter: SolaX **X3-NEO-12K-LV**, 3-phase, 12 kW AC. Low-voltage (48 V) battery, inverter battery limit 280 A.
- Battery: **21.2 kWh**, 4 modules in parallel, 120 A continuous each (210 A for 10 s). In practice the inverter is the limit, not the battery.
- PV: **6.48 kWp**, 16 × JKM405M-72HL-TV, ground mount, tilt 30°, facing **south-west** (compass 215°; Solcast azimuth **145**, where north = 0, south = ±180 and west is positive). Grid connection: 21 kW (expandable to 30 kW).
- Production server: Proxmox LXC 109 `solax` at 192.168.100.50:8000, systemd unit `solax` (see DEPLOYMENT.md). The Raspberry Pi is not used.
- Tariff: **G12w** (confirmed by the owner). Off-peak on weekdays: winter (1 Oct - 31 Mar) 22–06 and 13–15, summer (1 Apr - 30 Sep) 22–06 and 15–17. All day on weekends and Polish public holidays. Hourly net-billing. The code handles both seasons (`Tariff.windows_for`).
- Microinstallation power declared to the DSO (OSD): **6.48 kW** (= PV). Connection power: 21 kW. Keep `EXPORT_MAX_POWER_KW` at or below 6.48 (now 5).
- Net-billing deposit = RCE × **1.23** (confirmed by the owner): `EXPORT_PRICE_MULTIPLIER=1.23`, used for the deposit value and revenue in the plan. `EXPORT_MIN_PRICE_PLN_KWH` applies to the net RCE price, before the multiplier (since 2026-11-01).
- Export valuation: **RCE** (15-min market price from PSE). The SolaX app uses a TGE price list as an approximation, because RCE cannot be configured there.
- **State on 2026-10-11 (history):** PGE confirmed the contract settles at **monthly RCEm**, not RCE. The owner filed a request to switch to RCE. Until PGE confirms the switch, hourly prices (RCE, and the TGE list in the SolaX app) do not affect the bill, so export stays in dry run.
- **2026-11-01:** PGE switched the settlement to hourly **RCE** from 1 November 2026 (owner's report). Check the first November invoice: prices must match RCE, not RCEm.
- Owner verified manually: battery discharge at **5 kW** works. The discharge setpoint is **battery output**: the house load is served first and only the rest is exported. So 3 kW with a 2.5 kW appliance running gives almost no export.
- **2026-10-11:** the owner turned off all SolaX Cloud built-in rules (grid charging and grid export). Before that, the export rule was "discharge to 50%, max 5 kW, if export price > 1 PLN". It supports **only one condition (no AND)**, so it cannot combine the price threshold with a dynamic SOC floor. This is why the server should execute exports (see Task 3).

## Branches and PRs (stacked, merge in order)

| PR | Branch | Content |
|----|--------|---------|
| #1 | `security-hardening` | Constant-time API key check; auth runs before body validation; `/docs` disabled; generic 502 on upstream errors; default bind `127.0.0.1`; required self-use fields with HH:MM validation; MCP write tool behind `SOLAX_ALLOW_WRITE`; Docker non-root, `uv sync --frozen` |
| #2 | `solcast-forecast` | Solcast forecast (multi-site, cache for the ~10 calls/day limit), `get_solar_forecast` MCP tool, `GET /api/solar-forecast` |
| #3 | `battery-automation` | G12w grid-charge planner inside the HTTP server: before each off-peak window it sets the target SOC from consumption − PV forecast; **dry run by default** |
| #4 | `export-planner` | RCE prices from PSE (`get_energy_prices`, `GET /api/prices`); export planner (surplus above house need, best slots ≥ threshold, accounts for house load); `recommendedExportFloorSoc`; export execution via `soc_target_control_mode` (dry run by default) |

All four were merged, followed by #5-#12 (consumption profiles, dashboard, decision history, export switch, grid charging with remote control, net price threshold).

## Task 1: verify live data (read-only, safe)

1. Copy `.env.example` to `.env` and fill in `SOLAX_*`, `HTTP_API_KEY`, `SOLCAST_*`, `SOLAR_TIMEZONE=Europe/Warsaw`, `BATTERY_CAPACITY_KWH=21.2`, `AUTOMATION_ENABLED=1`, the consumption profiles from the "Consumption" section, `EXPORT_ENABLED=1`, `EXPORT_MAX_POWER_KW=5`.
2. Start the server: `set -a; source .env; set +a; TRANSPORT=http dotnet run -c Release --project src/SolaxCloudMcp`
3. Check each endpoint (`Authorization: Bearer $HTTP_API_KEY`):
   - `POST /api/realtime-data {}`: battery `soc_percent` is present and the sign of `chargeDischargePower_W` is known (discharge = negative or positive?).
   - `GET /api/solar-forecast?hours=24`: Solcast values look sane for 6.48 kWp.
   - `GET /api/prices?hours=24`: **verify PSE parsing** (`Prices.cs`). We assumed `dtime` = local period **end**, `"24:00:00"` = next midnight, `rce_pln` in PLN/MWh. Compare with https://www.pse.pl or the RCE chart.
   - `POST /api/automation/preview` and `POST /api/export/preview`: sanity-check the numbers.
4. Fix any parsing or format mismatch, with a test that uses a real captured response.

## Task 2: verify the Self Use assumption before disabling the charge dry run

**Superseded (2026-10-13):** the inverter ignores the Self Use grid-charge settings, so grid charging now uses remote control (`charge_to_soc`). The notes below are kept as history.

**Done 2026-10-05:** the inverter had "Charge Battery To" = 10% (Self Use, Charge From Grid enabled) all day, and PV still charged the battery to 94% (15.4 kWh charged, 0 kWh imported). So the limit applies only to grid charging. `AUTOMATION_DRY_RUN=0` and `AUTOMATION_MIN_SOC=10` (the owner's existing inverter value) are set on the LXC. The settings live in SolaX Cloud: Devices → X3-NEO-LV → Operation → Work Mode (not the TOU page).

The charge planner (#3) assumes `chargeUpperSoc` ("Charge battery to") limits **only grid charging** during the charge period and does not cap PV charging. Confirm on the inverter: set a low value (e.g. 30%) and check on a sunny day that PV still charges the battery above it. Only then set `AUTOMATION_DRY_RUN=0`. Also check whether the SolaX Cloud price rule stays active after `batch_set_spontaneity_self_use` is called through the API.

## Task 3 (main): implement export execution

The planner already computes, every 15 min, the current slot's `dischargeSetpoint_kW` / `expectedExport_kW` (`Automation.cs`: `compute_export_plan`, `AutomationScheduler._export_loop`). With `EXPORT_DRY_RUN=0` it now sends the commands (see Requirements).

Candidate SolaX Developer API endpoints. Paths come from the open-source HA integration NoUsername10/Solax-Developer-API-for-Home-assistant (`const.py`), which itself only dry-runs them:

- `POST /openapi/v2/device/inverter_vpp_mode/push_power/positive_or_negative_mode`. Required: `snList` (list), `batteryPower` (int), `timeOfDuration` (int), `nextMotion` (int), `businessType` (int, 1 = residential).
- `POST /openapi/v2/device/inverter_vpp_mode/exit_vpp_mode`. Required: `snList`, `businessType`.
- Also listed: `inverter_work_mode/batch_set_manual_mode`, `device_control/strategy/set_export_control`, `inverter_vpp_mode/power_control_mode`.

**Confirmed from the official docs** (developer.solaxcloud.com → Documents → "Inverter Remote Control Mode", read 2026-10-05; X3-NEO-LV is device type 33):
- `push_power/positive_or_negative_mode`: `batteryPower` in **W**, **positive = discharge**, negative = charge. Battery-side, so the house load is served first (matches the owner's observation). PV keeps running at max.
- `timeOfDuration` is in **seconds** in every VPP mode.
- `nextMotion`: **160 = exit remote control** (back to the normal work mode and its settings), **161 = back to "Self-Consume Charge/Discharge"**, which is still remote mode and charges from PV only, so it would block G12w grid charging. Use **160**.
- `exit_vpp_mode`: body `{snList, businessType}`.
- Success is `code == 10000`. `result[SN].status`: 1 offline, 2 issue failed, 3 issued, 4 device started, 5 execution failed, 6 timeout. Real execution results go only to the app's `callback_url`, which we cannot receive locally, so confirm execution from realtime battery power instead.
- **No grid-side target exists.** The other modes target the inverter **AC port** (house load is still on the grid side of the inverter):
  - `power_control_mode`: `activePowerTarget` (W), `wReactivePowerTarget` (Var), `timeOfDuration` (s). The sign is not documented.
  - `soc_target_control_mode`: `chargeDischargPower` (W, **negative = discharge**), `targetSoc`. It stops by itself at the target SOC, but it has **no duration**, so it keeps running if the server dies.
  - `electric_quantity_target_control_mode`: `chargeDischargPower` (W, negative = discharge), `targetEngergy` (Wh, sic).
- Note the API typos `chargeDischargPower` and `targetEngergy`; send them as spelled.

**Hardware test 2026-10-05 (SOC 91%, house ~0.85 kW, no PV):**
- `push_power/positive_or_negative_mode` (1 kW and 2 kW): status 4 (device received), but the inverter **ignored it** and stayed in Self Use.
- `soc_target_control_mode` (`chargeDischargPower=-2000`, `targetSoc=90`): **works**. Battery 1.99 kW, house 0.87 kW, export 1.12 kW. It matches the parameters of the SolaX Cloud automation action "Equipment Discharge (by Percentage)" (target %, power kW).
- `exit_vpp_mode`: status 4.
- Check a command with `POST /openapi/apiRequestLog/listByCondition {"requestId": ...}` (status 4 = started). The client logs every `requestId`.
- Each new access token invalidates the previous one. Two processes with the same client ID (e.g. the server and a script) make each other hit 10402; the client retries once.

Chosen design: `soc_target_control_mode` with `targetSoc = recommendedExportFloorSoc` and the setpoint at the AC port. It has **no duration**, so the server must send `exit_vpp_mode` at the end of each export slot. If the server dies, the inverter keeps discharging but stops at the floor SOC.

Also find out which endpoint the owner's manual 5 kW discharge used in the app, if it maps to the API.

Requirements:
1. **Done:** `POST /api/battery/export-test {power_kw<=2, minutes<=5, stop_soc>=30}` with `X-Confirm: yes`, and `POST /api/battery/export-stop` (see HTTP_API.md). The mode was verified on hardware with a script; run the endpoint itself once more with the owner watching.
2. **Done, ran live 2026-10-05 20:14 (5 kW: battery 4.7 kW, export 3.86 kW after ~1 min ramp; exit back to Self Use in ~30 s):** execution in `_export_loop` (`AutomationScheduler.apply_export`): when `currentSlot.export`, send `soc_target_control_mode` with the setpoint capped at `EXPORT_MAX_POWER_KW` and `targetSoc = recommendedExportFloorSoc`. Otherwise make sure the inverter is back in normal Self Use (exit VPP). It must be idempotent across restarts.
3. **Done:** improve the setpoint with live house load (`live_house_load_kw`: AC output minus `meter1.gridPower_W`, which is + export / - import per the SolaX docs; falls back to the profile when missing) (the AC-port target includes the house): at slot start, setpoint = planned export + current house load (from realtime data), capped.
4. Safety (**done**, tested with fakes in `tests/SolaxCloudMcp.Tests/AutomationTests.cs`):
   - abort if SOC <= `recommendedExportFloorSoc` (the inverter also stops there by itself);
   - on any error (command or planner), call exit VPP; also on server shutdown;
   - keep a kill switch (`EXPORT_DRY_RUN=1`);
   - log every command;
   - never let the charge planner and the export loop write at the same time (shared lock).
5. Once the server exports reliably, the owner should **disable the SolaX Cloud price rule** so the two do not fight.

## Consumption (from the owner)

About **15 kWh/day**, mostly **11:00–17:00**. Weekends are higher (laundry, cleaning). Starting profiles, to refine from real SolaX history data (hourly house load):

```bash
AUTOMATION_CONSUMPTION_PROFILE=0.3,0.3,0.3,0.3,0.3,0.3,0.5,0.5,0.5,0.5,0.5,1.2,1.2,1.2,1.2,1.2,1.2,0.6,0.6,0.6,0.6,0.6,0.25,0.25          # 15.0 kWh
AUTOMATION_WEEKEND_CONSUMPTION_PROFILE=0.3,0.3,0.3,0.3,0.3,0.3,0.5,0.5,0.5,0.5,0.5,1.9,1.9,1.9,1.9,1.9,1.9,0.6,0.6,0.6,0.6,0.6,0.25,0.25  # 19.2 kWh, estimate
```

**Tool (done):** `SolaxCloudMcp consumption --since 2026-10-02` computes both profiles from SolaX history. Real data starts on Fri 2026-10-02. First run (2026-10-05): weekday 11.7 kWh/day from 1 day only (evening peak 18-22, not 11-17), weekend 18.9 kWh/day from 2 days. Re-run after about 2 weeks of data and put the output into `.env`.

## Open questions for the owner

- Whether to ask the DSO to raise the declared microinstallation power above 6.48 kW, so the battery can export faster.

## Conventions

- C# on .NET 10, ASP.NET Core minimal API, the official `ModelContextProtocol` SDK, xUnit. Match the existing style: methods in `SolaxService.cs` are shared by MCP and HTTP; API payloads and plans are `JsonObject` with literal keys.
- Errors from upstream APIs go to logs. Clients get generic 502s.
- Every hardware-writing feature ships in dry run first.

## Differences from the Python version (2026-11-13)

The planners give the same results; `GoldenTests.cs` proves it against results recorded from the Python server. These differences are deliberate:

- PSE price slots in the repeated hour of the autumn DST night are 15 minutes long. Python made them 75 minutes long (slot start was one hour too early).
- The forecast `hourly` list has two entries for 02:00 on the autumn DST night. Python merged them into one.
- A 422 answer has `detail` as text (`"min_soc: an integer from 10 to 100 is required"`), not a list of objects.
- Request bodies are strict: numbers must be JSON numbers (`20`, not `"20"`) and `active` must be `true` or `false`.
- An unknown path under `/api/` answers 403 without a valid key (Python: 404).
- The dashboard is a Blazor Server page with Syncfusion Blazor components (`Components/Dashboard.razor`), not `dashboard.html` with Chart.js. It reads the planners on the server, needs a WebSocket connection, and uses `SYNCFUSION_LICENSE_KEY`. Hiding a panel is gone; moving and resizing panels stays (Syncfusion Dashboard Layout). Times are shown in `SOLAR_TIMEZONE`, not in the browser's time zone.
- No access log line per request. Warnings, errors and all inverter commands are logged.
- A charge window with no battery data records an `InvalidOperationException` in the history (Python: `AttributeError`). Error names in the history are the .NET names.
- Not run yet at the time of the rewrite: the Linux build on the server, the Docker image, and any real call to SolaX or Solcast from the C# code. The PSE call was tested against the real API.
