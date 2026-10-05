# Handoff: SolaX automation (for the local agent)

Context from a cloud session. The cloud sandbox could not reach SolaX, Solcast or PSE, so **nothing below has been run against real APIs or hardware**. Your job is to verify it on the real installation and finish the export execution.

## Installation

- Inverter: SolaX **X3-NEO-12K-LV**, 3-phase, 12 kW AC. Low-voltage (48 V) battery, inverter battery limit 280 A.
- Battery: **21.2 kWh**, 4 modules in parallel, 120 A continuous each (210 A for 10 s). In practice the inverter is the limit, not the battery.
- PV: **6.48 kWp**, 16 × JKM405M-72HL-TV, ground mount, tilt 30°, facing **south-west** (compass 215°; Solcast azimuth **145**, where north = 0, south = ±180 and west is positive). Grid connection: 21 kW (expandable to 30 kW).
- Production server: Proxmox LXC 109 `solax` at 192.168.100.50:8000, systemd unit `solax` (see DEPLOYMENT.md). The Raspberry Pi is not used.
- Tariff: **G12w**. Off-peak 22–06 and 13–15 on weekdays, all day on weekends and Polish public holidays. Hourly net-billing.
- Export valuation: **RCE** (15-min market price from PSE). The SolaX app uses a TGE price list as an approximation, because RCE cannot be configured there.
- Owner verified manually: battery discharge at **5 kW** works. The discharge setpoint is **battery output**: the house load is served first and only the rest is exported. So 3 kW with a 2.5 kW appliance running gives almost no export.
- The SolaX Cloud built-in rule currently used is "discharge to 50%, max 5 kW, if export price > 1 PLN". It supports **only one condition (no AND)**, so it cannot combine the price threshold with a dynamic SOC floor. This is why the server should execute exports (see Task 3).

## Branches and PRs (stacked, merge in order)

| PR | Branch | Content |
|----|--------|---------|
| #1 | `security-hardening` | Constant-time API key check; auth runs before body validation; `/docs` disabled; generic 502 on upstream errors; default bind `127.0.0.1`; required self-use fields with HH:MM validation; MCP write tool behind `SOLAX_ALLOW_WRITE`; Docker non-root, `uv sync --frozen` |
| #2 | `solcast-forecast` | Solcast forecast (multi-site, cache for the ~10 calls/day limit), `get_solar_forecast` MCP tool, `GET /api/solar-forecast` |
| #3 | `battery-automation` | G12w grid-charge planner inside the HTTP server: before each off-peak window it sets the target SOC from consumption − PV forecast; **dry run by default** |
| #4 | `export-planner` | RCE prices from PSE (`get_energy_prices`, `GET /api/prices`); export planner (surplus above house need, best slots ≥ threshold, accounts for house load); `recommendedExportFloorSoc`; export execution via `soc_target_control_mode` (dry run by default) |

Work on `export-planner`: it contains all four. Tests: `uv sync && uv run pytest -q` (102 pass).

## Task 1: verify live data (read-only, safe)

1. Copy `.env.example` to `.env` and fill in `SOLAX_*`, `HTTP_API_KEY`, `SOLCAST_*`, `SOLAR_TIMEZONE=Europe/Warsaw`, `BATTERY_CAPACITY_KWH=21.2`, `AUTOMATION_ENABLED=1`, the consumption profiles from the "Consumption" section, `EXPORT_ENABLED=1`, `EXPORT_MAX_POWER_KW=5`.
2. Start the server: `set -a; source .env; set +a; TRANSPORT=http uv run python -m solax_cloud_mcp`
3. Check each endpoint (`Authorization: Bearer $HTTP_API_KEY`):
   - `POST /api/realtime-data {}`: battery `soc_percent` is present and the sign of `chargeDischargePower_W` is known (discharge = negative or positive?).
   - `GET /api/solar-forecast?hours=24`: Solcast values look sane for 6.48 kWp.
   - `GET /api/prices?hours=24`: **verify PSE parsing** (`prices.py`). We assumed `dtime` = local period **end**, `"24:00:00"` = next midnight, `rce_pln` in PLN/MWh. Compare with https://www.pse.pl or the RCE chart.
   - `POST /api/automation/preview` and `POST /api/export/preview`: sanity-check the numbers.
4. Fix any parsing or format mismatch, with a test that uses a real captured response.

## Task 2: verify the Self Use assumption before disabling the charge dry run

The charge planner (#3) assumes `chargeUpperSoc` ("Charge battery to") limits **only grid charging** during the charge period and does not cap PV charging. Confirm on the inverter: set a low value (e.g. 30%) and check on a sunny day that PV still charges the battery above it. Only then set `AUTOMATION_DRY_RUN=0`. Also check whether the SolaX Cloud price rule stays active after `batch_set_spontaneity_self_use` is called through the API.

## Task 3 (main): implement export execution

The planner already computes, every 15 min, the current slot's `dischargeSetpoint_kW` / `expectedExport_kW` (`automation.py`: `compute_export_plan`, `AutomationScheduler._export_loop`). With `EXPORT_DRY_RUN=0` it now sends the commands (see Requirements).

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
4. Safety (**done**, tested with fakes in `tests/test_automation.py`):
   - abort if SOC <= `recommendedExportFloorSoc` (the inverter also stops there by itself);
   - on any error (command or planner), call exit VPP; also on server shutdown;
   - keep a kill switch (`EXPORT_DRY_RUN=1`);
   - log every command;
   - never let the charge planner and the export loop write at the same time (shared asyncio lock).
5. Once the server exports reliably, the owner should **disable the SolaX Cloud price rule** so the two do not fight.

## Consumption (from the owner)

About **15 kWh/day**, mostly **11:00–17:00**. Weekends are higher (laundry, cleaning). Starting profiles, to refine from real SolaX history data (hourly house load):

```bash
AUTOMATION_CONSUMPTION_PROFILE=0.3,0.3,0.3,0.3,0.3,0.3,0.5,0.5,0.5,0.5,0.5,1.2,1.2,1.2,1.2,1.2,1.2,0.6,0.6,0.6,0.6,0.6,0.25,0.25          # 15.0 kWh
AUTOMATION_WEEKEND_CONSUMPTION_PROFILE=0.3,0.3,0.3,0.3,0.3,0.3,0.5,0.5,0.5,0.5,0.5,1.9,1.9,1.9,1.9,1.9,1.9,0.6,0.6,0.6,0.6,0.6,0.25,0.25  # 19.2 kWh, estimate
```

Nice-to-have: derive the profiles automatically from SolaX history (if the Developer API exposes hourly load) instead of hand-written values.

## Open questions for the owner

- Microinstallation power declared to the DSO (OSD). It caps export power; target is 10 kW (the inverter allows 12).
- `EXPORT_PRICE_MULTIPLIER`: whether a coefficient (e.g. 1.23) applies to their net-billing deposit.
- Whether their DSO's G12w hours are exactly 22–06 and 13–15 (`TARIFF_OFFPEAK_WINDOWS`).

## Conventions

- Python 3.11+, `uv`, httpx async, FastAPI, pytest + respx. Match the existing style: impl functions in `server.py` are shared by MCP and HTTP.
- Errors from upstream APIs go to logs. Clients get generic 502s.
- Every hardware-writing feature ships in dry run first.
