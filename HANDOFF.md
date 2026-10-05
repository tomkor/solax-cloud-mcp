# Handoff: SolaX automation (for the local agent)

Context from a cloud session. The cloud sandbox could not reach SolaX, Solcast or PSE, so **nothing below has been run against real APIs or hardware**. Your job is to verify it on the real installation and finish the export execution.

## Installation

- Inverter: SolaX **X3-NEO-12K-LV**, 3-phase, 12 kW AC. Low-voltage (48 V) battery, inverter battery limit 280 A.
- Battery: **21.2 kWh**, 4 modules in parallel, 120 A continuous each (210 A for 10 s). In practice the inverter is the limit, not the battery.
- PV: **6.48 kWp**. Grid connection: 21 kW (expandable to 30 kW).
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
| #4 | `export-planner` | RCE prices from PSE (`get_energy_prices`, `GET /api/prices`); export planner (surplus above house need, best slots ≥ threshold, accounts for house load); `recommendedExportFloorSoc`; **dry run only, write path not implemented** |

Work on `export-planner`: it contains all four. Tests: `uv sync && uv run pytest -q` (102 pass).

## Task 1: verify live data (read-only, safe)

1. Copy `.env.example` to `.env` and fill in `SOLAX_*`, `HTTP_API_KEY`, `SOLCAST_*`, `SOLAR_TIMEZONE=Europe/Warsaw`, `BATTERY_CAPACITY_KWH=21.2`, `AUTOMATION_ENABLED=1`, `AUTOMATION_DAILY_CONSUMPTION_KWH=<ask owner>` (or a 24-value profile), `EXPORT_ENABLED=1`, `EXPORT_MAX_POWER_KW=5`.
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

The planner already computes, every 15 min, the current slot's `dischargeSetpoint_kW` / `expectedExport_kW` (`automation.py`: `compute_export_plan`, `AutomationScheduler._export_loop`). `ExportSettings.from_env` currently **rejects `EXPORT_DRY_RUN=0`**.

Candidate SolaX Developer API endpoints. Paths come from the open-source HA integration NoUsername10/Solax-Developer-API-for-Home-assistant (`const.py`), which itself only dry-runs them:

- `POST /openapi/v2/device/inverter_vpp_mode/push_power/positive_or_negative_mode`. Required: `snList` (list), `batteryPower` (int), `timeOfDuration` (int), `nextMotion` (int), `businessType` (int, 1 = residential).
- `POST /openapi/v2/device/inverter_vpp_mode/exit_vpp_mode`. Required: `snList`, `businessType`.
- Also listed: `inverter_work_mode/batch_set_manual_mode`, `device_control/strategy/set_export_control`, `inverter_vpp_mode/power_control_mode`.

**Unknown and must be read from the official docs** (developer.solaxcloud.com, logged in):
- sign and unit of `batteryPower`;
- unit of `timeOfDuration`;
- allowed `nextMotion` values (what happens after the duration ends);
- whether a grid-side target exists. A grid-side target would make export independent of house load, which is ideal given the owner's observation.

Also find out which endpoint the owner's manual 5 kW discharge used in the app, if it maps to the API.

Requirements:
1. Add a manual test endpoint, e.g. `POST /api/battery/export-test {power_kw<=2, minutes<=5}`. It must check `X-Confirm: yes`, enforce the hard caps, and always restore Self Use / exit VPP afterwards. Test it with the owner watching the app.
2. Execution in `_export_loop`: when `currentSlot.export`, send the command for one slot (15 min) with the setpoint capped at `EXPORT_MAX_POWER_KW`. Otherwise make sure the inverter is back in normal Self Use (exit VPP). It must be idempotent across restarts.
3. Improve the setpoint with live house load, if the command is battery-side: at slot start, setpoint = planned export + current house load (from realtime data), capped.
4. Safety:
   - abort if SOC < `recommendedExportFloorSoc`;
   - on any error, call exit VPP / restore Self Use;
   - keep a kill switch (`EXPORT_DRY_RUN=1`);
   - log every command;
   - never let the charge planner and the export loop write at the same time (shared asyncio lock).
5. Once the server exports reliably, the owner should **disable the SolaX Cloud price rule** so the two do not fight.

## Open questions for the owner

- Daily consumption, or better an hourly profile (`AUTOMATION_CONSUMPTION_PROFILE`).
- Microinstallation power declared to the DSO (OSD). It caps export power; target is 10 kW (the inverter allows 12).
- `EXPORT_PRICE_MULTIPLIER`: whether a coefficient (e.g. 1.23) applies to their net-billing deposit.
- Whether their DSO's G12w hours are exactly 22–06 and 13–15 (`TARIFF_OFFPEAK_WINDOWS`).

## Conventions

- Python 3.11+, `uv`, httpx async, FastAPI, pytest + respx. Match the existing style: impl functions in `server.py` are shared by MCP and HTTP.
- Errors from upstream APIs go to logs. Clients get generic 502s.
- Every hardware-writing feature ships in dry run first.
