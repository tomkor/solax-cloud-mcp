# SolaX Cloud MCP Server

An MCP server and HTTP server for a SolaX hybrid inverter with a battery, built on the SolaX Developer Platform API.

- **MCP tools** for Claude Code / Claude Desktop: real-time inverter data, Solcast PV forecast, Polish RCE market prices, and (opt-in) battery Self Use settings.
- **HTTP server** (`TRANSPORT=http`) with the same data as a REST API, plus:
  - **Grid-charge planner**: before each cheap tariff window (e.g. G12w), charges the battery from the grid only as much as the next peak hours need (consumption minus PV forecast).
  - **Export planner**: sells the battery surplus in the most expensive 15-minute RCE slots.
  - **Dashboard** (`/dashboard`): live data, plans, decision history and an export on/off switch.

Both planners send SolaX remote control commands and run in **dry run** by default. The production setup runs the HTTP server on a Proxmox LXC with systemd (see [DEPLOYMENT.md](DEPLOYMENT.md)).

| Document | Content |
|----------|---------|
| [HTTP_API.md](HTTP_API.md) | Every HTTP endpoint with request and response examples |
| [DEPLOYMENT.md](DEPLOYMENT.md) | Proxmox LXC + systemd (production), Docker / Raspberry Pi |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Modules, data flow, planners, design decisions |
| [HANDOFF.md](HANDOFF.md) | Installation details, hardware test log and project history |
| [.env.example](.env.example) | All settings with comments |

## Prerequisites

- **Python 3.10+** (automatically provisioned by `uv`)
- **uv** package manager ([install here](https://astral.sh/uv/))
- A SolaX Developer Platform account with OAuth2 application registered
- Device (inverter) serial number (`deviceSn`) for your inverter

## Getting Started

### 1. Register an OAuth2 Application

1. Log in to [SolaX Developer Platform](https://developer.solaxcloud.com/)
2. Navigate to **Application** section
3. Create a new application and enable **client_credentials** grant type
4. Copy your **Client ID** and **Client Secret** (keep these secret!)

### 2. Identify Your Device Serial Number

1. Log in to [SolaX Developer Platform](https://developer.solaxcloud.com/)
2. Navigate to **My Account** or device management section
3. Find your inverter's **device serial number** (e.g., `X3ABCD0123`)
   - This is NOT the old WiFi dongle registration number used by the legacy SolaX Cloud API

### 3. Install the Server

```bash
# Clone or navigate to the repo
cd /path/to/solax-cloud-mcp

# Install uv if needed
curl -LsSf https://astral.sh/uv/install.sh | sh

# Install dependencies and create virtual environment
uv sync
```

### 4. Configure Credentials

```bash
# Copy the example environment file
cp .env.example .env

# Edit .env and fill in your credentials
nano .env
```

Add your OAuth2 credentials and device serial number:
```
SOLAX_CLIENT_ID=your_client_id
SOLAX_CLIENT_SECRET=your_client_secret
SOLAX_DEVICE_SN=X3ABCD0123
```

### 5. Register with Claude Code

To use this server with Claude Code or Claude Desktop:

```bash
claude mcp add solax-cloud \
  --env SOLAX_CLIENT_ID=your_client_id \
  --env SOLAX_CLIENT_SECRET=your_client_secret \
  --env SOLAX_DEVICE_SN=your_device_sn \
  --scope user \
  -- uv run --directory /path/to/solax-cloud-mcp solax-cloud-mcp
```

If the above doesn't work (environment variable propagation issues), edit your MCP config JSON directly:
- **Claude Code:** `~/.claude/mcp.json` or project settings
- **Claude Desktop:** `~/.config/Claude/claude_desktop_config.json` (macOS) or equivalent

Add this entry:
```json
{
  "solax-cloud": {
    "command": "uv",
    "args": [
      "run",
      "--directory",
      "/path/to/solax-cloud-mcp",
      "solax-cloud-mcp"
    ],
    "env": {
      "SOLAX_CLIENT_ID": "your_client_id",
      "SOLAX_CLIENT_SECRET": "your_client_secret",
      "SOLAX_DEVICE_SN": "your_device_sn"
    }
  }
}
```

## Usage

Once registered, these MCP tools are available:

| Tool | When it is available | What it does |
|------|----------------------|--------------|
| `get_realtime_data` | always | Live inverter, PV, grid and battery readings |
| `get_energy_prices` | always | Polish RCE market prices (15-minute slots, PSE, no key needed) |
| `get_solar_forecast` | `SOLCAST_API_KEY` and `SOLCAST_RESOURCE_IDS` set | Solcast PV forecast |
| `set_battery_self_use_mode` | `SOLAX_ALLOW_WRITE=1` | Changes the inverter Self Use settings |

The `set_battery_self_use_mode` tool changes inverter settings and is **not exposed by default**, so an LLM (e.g. via prompt injection) cannot reconfigure the inverter. Its `min_soc`, `charge_upper_soc` and `charge_from_grid_enable` arguments are required.

The charge and export planners and the dashboard exist only in the HTTP server, not in MCP mode.

### Example Usage in Claude

> "What's the current power output of my solar inverter?"

Claude will call `get_realtime_data()` and report the results to you in human-friendly terms.

### Tool: `get_realtime_data`

**Arguments:**
- `device_sn` (optional): Inverter device serial number. If omitted, defaults to `SOLAX_DEVICE_SN` environment variable.

**Returns:** A structured dictionary containing:

```json
{
  "device": {
    "deviceSn": "X3ABCD0123",
    "registerNo": "SE123456SP",
    "dataTime": "2025-05-22 15:13:10",
    "plantLocalTime": "2025-05-22 15:13:10"
  },
  "status": {
    "code": 102,
    "description": "Normal"
  },
  "pv": [
    {"string": 1, "voltage_V": 421.7, "current_A": 0.4, "power_W": 189.0},
    {"string": 2, "voltage_V": 418.0, "current_A": 0.0, "power_W": 0.0}
  ],
  "mppt": {
    "trackers": [
      {"mppt": 1, "voltage_V": 0.0, "current_A": 0.0, "power_W": 0.0}
    ],
    "totalPower_W": null
  },
  "ac": {
    "phases": [
      {"phase": 1, "voltage_V": 224.1, "current_A": 0.9, "power_W": 189.0, "frequency_Hz": 50.0},
      {"phase": 2, "voltage_V": 226.5, "current_A": 0.8, "power_W": 171.0, "frequency_Hz": 50.0}
    ],
    "totalPower_W": 360.0,
    "totalReactivePower": 0,
    "powerFactor": 1.0,
    "gridFrequency": 50.0
  },
  "energy": {
    "dailyYield_kWh": 157.4,
    "totalYield_kWh": 20465.3,
    "dailyACOutput_kWh": 160.9,
    "totalACOutput_kWh": 19907.5
  },
  "meter1": {
    "gridPower_W": 0,
    "todayImportEnergy_kWh": 0.07,
    "totalImportEnergy_kWh": 23.28,
    "todayExportEnergy_kWh": 0.0,
    "totalExportEnergy_kWh": 64.98
  },
  "meter2": {
    "gridPower_W": 0,
    "todayImportEnergy_kWh": 0.08,
    "totalImportEnergy_kWh": 1.39,
    "todayExportEnergy_kWh": 0.0,
    "totalExportEnergy_kWh": 0.75
  },
  "battery": {
    "soc_percent": 85.5,
    "remainingEnergy_kWh": 1024.8,
    "soh_percent": 99.2,
    "chargeDischargePower_W": -150.5,
    "voltage_V": 409.6,
    "current_A": -15.2,
    "temperature_C": 28.3,
    "cycleTimes": 142,
    "totalCharge_kWh": 4250.75,
    "totalDischarge_kWh": 4100.25,
    "status": {"code": 1, "description": "Work"}
  },
  "eps": {
    "voltage_V": [0.0, 0.0, 0.0],
    "current_A": [0.0, 0.0, 0.0],
    "activePower_W": [0.0, 0.0, 0.0],
    "apparentPower_W": [0.0, 0.0, 0.0]
  },
  "temperature": {
    "inverter_C": 40.4
  },
  "misc": {
    "l1l2Voltage_V": null,
    "l2l3Voltage_V": null,
    "l1l3Voltage_V": null
  }
}
```

### Tool: `get_solar_forecast` (optional, Solcast)

Available when `SOLCAST_API_KEY` and `SOLCAST_RESOURCE_IDS` are set. Read-only PV production forecast from [Solcast](https://solcast.com/) rooftop sites, summed across all configured sites (e.g. east + west arrays).

**Setup:**
1. Create a free hobbyist account at https://toolkit.solcast.com.au/ and add your rooftop site(s) (location, azimuth, tilt, kWp).
2. Copy the API key and each site's Resource ID into `.env`:
   ```bash
   SOLCAST_API_KEY=your_api_key
   SOLCAST_RESOURCE_IDS=abcd-1234-ef56-7890,1234-abcd-7890-ef56
   SOLAR_TIMEZONE=Europe/Warsaw
   # SOLCAST_CACHE_MINUTES=180
   ```

**Arguments:** `hours` (1-168, default 24) — length of the hourly profile.

**Returns:** per-day forecast energy (`p10`/`p50`/`p90` kWh, peak power and time), `remainingToday_kWh`, an hourly power profile (kW) in `SOLAR_TIMEZONE`, plus `fetchedAt` and `stale`.

**API limits:** hobbyist accounts allow ~10 calls/day, and every refresh costs one call per site. Responses are cached for `SOLCAST_CACHE_MINUTES` (default 180) and fetched only on demand; if a refresh fails (e.g. `429`), the last cached data is returned with `stale: true`. With two sites, consider `SOLCAST_CACHE_MINUTES=360`.

The API key is sent in the `Authorization` header only, never in the URL.

Set `SOLCAST_CACHE_FILE` to keep the cache across restarts, so a restart does not spend an API call.

### Tool: `get_energy_prices`

**Arguments:** `hours` (1-48, default 24).

**Returns:** the RCE price for each 15-minute slot in the next `hours` (`price_PLN_kWh`, net), plus `min` and `max`. Data comes from the public PSE API (`api.raporty.pse.pl`, no key needed) and is cached for 30 minutes. PSE publishes the next day's prices in the afternoon.

## Battery Automation, Export and Dashboard (HTTP server)

### Battery Automation (forecast-driven grid charging)

Built into the HTTP server and disabled by default. Enable it with `AUTOMATION_ENABLED=1`; it needs `SOLAX_DEVICE_SN`, the `SOLCAST_*` settings and `BATTERY_CAPACITY_KWH`. Designed for time-of-use tariffs such as Polish **G12w**: cheap at 22:00-06:00 and 13:00-15:00 on weekdays in winter (1 Oct - 31 Mar), 22:00-06:00 and 15:00-17:00 in summer (1 Apr - 30 Sep), and all day on weekends and public holidays. Set `TARIFF_OFFPEAK_WINDOWS` (winter or all year) and `TARIFF_OFFPEAK_WINDOWS_SUMMER` for other tariffs.

**How it works:** a few minutes before each daily off-peak window (`AUTOMATION_LEAD_MINUTES`), the planner:
1. Takes the peak-price hours right after the window, e.g. 06:00-13:00 after the night window and 15:00-22:00 after the midday one. When the window is followed by more off-peak time (Friday night, holidays), there is nothing to cover and grid charging is disabled.
2. For each of those hours, compares the consumption profile with the Solcast PV forecast (`AUTOMATION_FORECAST_PERCENTILE`). The largest cumulative deficit is the energy the battery must hold at the start of the peak, so PV surplus earlier in the segment offsets later deficits.
3. Converts that energy to a target SOC: `min_soc + (deficit / efficiency + margin) / capacity`, capped at `AUTOMATION_MAX_GRID_SOC`.
4. During the window, when grid charging is needed: every 15 minutes, if SOC is below the target, sends remote control "charge at `AUTOMATION_CHARGE_POWER_KW` (default 5) until target SOC". The inverter stops by itself at the target. At the window end (and on server stop or restart) it exits remote control, back to normal Self Use. The export loop does not touch the inverter during the window.

The X3-NEO-LV accepts the Self Use grid-charge settings (`chargeFromGridEnable`, `chargeUpperSoc`) but does not act on them, so the planner does not use them.

If the forecast fails, nothing is charged in that window and the error is logged. A failed charge command is retried at the next 15-minute check. A restart inside a window skips charging in that window.

**Safety:** `AUTOMATION_DRY_RUN=1` (default) only logs decisions. Watch `GET /api/automation` and the logs for a few days, check that the decisions match how your inverter behaves, then set `AUTOMATION_DRY_RUN=0`.

**Endpoints** (bearer auth):
- `GET /api/automation`: enabled/dry-run status, next run, last result.
- `POST /api/automation/preview`: plan for the next window, computed now, never written.

House consumption comes from `AUTOMATION_CONSUMPTION_PROFILE`, 24 hourly kWh values (or a flat `AUTOMATION_DAILY_CONSUMPTION_KWH`). An optional `AUTOMATION_WEEKEND_CONSUMPTION_PROFILE` / `AUTOMATION_WEEKEND_DAILY_CONSUMPTION_KWH` applies on weekends and Polish public holidays. To compute both from real data (house load = AC output minus grid power, 5-minute samples from SolaX history), run `python -m solax_cloud_mcp.consumption --since YYYY-MM-DD` with the `.env` loaded; it prints the two lines for `.env`.

See `.env.example` for all `AUTOMATION_*` and `TARIFF_*` settings.

### Export Planner (RCE prices)

The export planner (`EXPORT_ENABLED=1`, requires battery automation and `EXPORT_MAX_POWER_KW`) uses the RCE prices from `get_energy_prices` / `GET /api/prices`. Every 15 minutes it reads the current SOC, the PV forecast and RCE prices, then:
1. Computes the battery **surplus**: energy above `AUTOMATION_MIN_SOC` minus what the house needs until the next off-peak window (consumption − PV, same model as the charge planner). Selling therefore never forces buying back at peak price.
2. Allocates the surplus to the most expensive slots whose **net** RCE price is at or above `EXPORT_MIN_PRICE_PLN_KWH` (default 1.0). `EXPORT_PRICE_MULTIPLIER` (e.g. 1.23 for Polish net-billing) does not change this check. It applies only to the slot `price_PLN_kWh` and `revenue_PLN` shown in the plan, so a slot can show a price above the threshold while its net price is below it. `EXPORT_MAX_POWER_KW` is the **inverter AC output setpoint**: the house load is served first and only the rest goes to the grid. Per slot, `export = setpoint × 0.25 h − expected house load`. When exporting live, the setpoint sent is the planned export plus the house load measured now (AC output minus grid power), capped at `EXPORT_MAX_POWER_KW`. The plan shows `export_kWh`, `expectedHouseLoad_kWh` and the `dischargeSetpoint_kW` needed, which equals export plus load.

The plan also reports `recommendedExportFloorSoc`: the SOC floor that keeps enough energy for the house until the next off-peak window. Use it to tune a price-based export rule configured in SolaX Cloud (e.g. "discharge to X%, max 5 kW, if export price > 1 PLN"), which uses a fixed floor.

`POST /api/export/preview` shows the plan. `GET /api/automation` shows the last plan under `export`, and the realtime data that run read under `export.lastRealtime` (no extra SolaX call).

> **Dry run by default.** With `EXPORT_DRY_RUN=0` the server sends, at the start of each export slot, SolaX remote control `soc_target_control_mode` (discharge at the slot setpoint until `recommendedExportFloorSoc`). In any other slot it exits remote control, so the inverter goes back to its normal work mode. The mode has no duration: if the server stops mid-slot, the inverter keeps discharging but stops at the floor SOC. Charge-planner and export writes never run at the same time. Disable any SolaX Cloud price rule that also discharges, so the two do not fight. (`push_power` mode is accepted but ignored by the X3-NEO-LV.)

**Kill switch:** the dashboard button (or `POST /api/export/active {"active": false}`) turns the export loop off and exits remote control at once. Set `SETTINGS_FILE` to keep this switch across restarts; without it, export is on again after a restart. `EXPORT_DRY_RUN=1` is the permanent off switch.

### Dashboard

`GET /dashboard` on the HTTP server opens a web page (Polish UI) with movable, resizable panels:
- battery and inverter data (the export loop's latest reading; a fresh SolaX call only when you press **Odczyt z falownika**),
- the next charge-planner runs and the last charge plan,
- the export plan for the current 15-minute slots, RCE prices and the PV forecast,
- the decision history (charge and export decisions, newest first),
- the export on/off switch.

The page itself holds no data. It asks once for `HTTP_API_KEY`, keeps it in the browser's `localStorage` and calls the API with it. Set `DECISIONS_FILE` (JSON lines) to keep the decision history across restarts. Do not expose the port to the internet without an extra login layer in front (VPN or an authenticating reverse proxy).

## HTTP Server

The HTTP server serves the REST API, the dashboard and the two planners. Run it directly:

```bash
set -a; source .env; set +a
TRANSPORT=http uv run python -m solax_cloud_mcp
curl http://localhost:8000/health
```

`HTTP_API_KEY` is required. Generate one with `python -c "import secrets; print(secrets.token_urlsafe(32))"` and paste the result into `.env`. The server listens on `127.0.0.1:8000` by default; set `HTTP_HOST=0.0.0.0` to reach it from the LAN.

**Run only one instance.** Each new SolaX access token invalidates the previous one, and two servers would also fight over the inverter.

### Endpoints

All endpoints except `/health` and `/dashboard` need `Authorization: Bearer <HTTP_API_KEY>`. Full request and response examples are in **[HTTP_API.md](HTTP_API.md)**.

| Method and path | What it does | Writes to the inverter |
|-----------------|--------------|------------------------|
| `GET /health` | Health check | no |
| `POST /api/realtime-data` | Live inverter data | no |
| `GET /api/solar-forecast?hours=24` | Solcast PV forecast (`503` if not configured) | no |
| `GET /api/prices?hours=24` | RCE prices, 15-minute slots | no |
| `GET /api/automation` | Planner status, last charge plan, last export plan and reading | no |
| `GET /api/automation/history` | Charge and export decisions, newest first | no |
| `POST /api/automation/preview` | Charge plan for the next off-peak window | no |
| `POST /api/export/preview` | Export plan computed now | no |
| `GET /api/dashboard/settings` | Planner settings and next runs | no |
| `GET /dashboard` | Web dashboard | no |
| `POST /api/export/active` | Export loop on/off switch | only exits remote control |
| `POST /api/battery/export-test` | Manual test discharge (header `X-Confirm: yes`) | yes |
| `POST /api/battery/export-stop` | Exit remote control now | yes |
| `POST /api/battery/self-use-mode` | Self Use settings | yes |

### Deployment

- **Proxmox LXC + systemd** (production setup): see [DEPLOYMENT.md](DEPLOYMENT.md#proxmox-lxc-with-systemd-no-docker). On stop, the server exits SolaX remote control before it quits.
- **Docker / Raspberry Pi**: `docker-compose up -d`, details in [DEPLOYMENT.md](DEPLOYMENT.md). The compose file publishes the port on `127.0.0.1` only (set `HTTP_BIND_ADDR` for LAN access) and passes only the variables listed in `docker-compose.yml`. Add any other variable from `.env.example` there before you use it.

## Manual Testing

### Quick Smoke Test

Before registering with Claude, you can test the client directly:

```bash
uv run --env-file .env python -c \
  "import asyncio; from solax_cloud_mcp.client import fetch_realtime_data; \
   from solax_cloud_mcp.config import get_default_device_sn; \
   print(asyncio.run(fetch_realtime_data(get_default_device_sn())))"
```

### Run Tests

```bash
uv run pytest -q
```

The tests use `respx` mocks and fakes instead of the real APIs (SolaX, Solcast, PSE), so they need no credentials and never touch the inverter. They cover:
- response shaping, status decoding and config validation,
- the SolaX client (token refresh on 10402, remote control commands),
- the Solcast forecast and its cache, RCE price parsing,
- the charge and export planners, and the scheduler safety rules (exit remote control after errors, after a restart and at the window end; no export while grid charging),
- the HTTP routes (auth, error mapping, export switch, dashboard settings),
- the consumption profile tool.

### Interactive Inspection

If you have the MCP CLI tools installed:

```bash
uv run --env-file .env mcp dev src/solax_cloud_mcp/server.py
```

## Rate Limiting

This server respects SolaX Developer Platform's documented rate limits:
- **100 calls per minute** per token
- **1,000,000 calls per day** per token

The client automatically enforces a 0.7-second minimum spacing between calls, which keeps typical usage well within both limits. No manual rate limiting is needed.

## Data Fields Reference

All values are in SI units:
- **Power:** Watts (W)
- **Energy:** Kilowatt-hours (kWh)
- **Voltage:** Volts (V)
- **Current:** Amps (A)
- **Temperature:** Celsius (°C)
- **Frequency:** Hertz (Hz)

### Inverter Status Codes

The inverter's `status.code` is an integer from the table below (Appendix 6 from the SolaX Developer Platform API docs). Not all possible codes are listed; consult the full Appendix 6 table on `developer.solaxcloud.com/doc` for the complete set.

| Code | Status | Description |
|------|--------|-------------|
| 100 | Waiting | Waiting |
| 101 | Self-check | Self-check |
| 102 | Normal | Normal |
| 103 | Fault | Fault |
| 104 | Permanent Fault Mode | Permanent Fault Mode |
| 105 | Update Mode | Update Mode |
| 106 | EPS Check Mode | EPS Check Mode |
| 107 | EPS Mode | EPS Mode |
| 108 | Self-test | Self-test |
| 109 | Idle Mode | Idle Mode |
| 110 | Standby Mode | Standby Mode |
| 130 | VPP mode | VPP mode |
| 131 | TOU-Self use | TOU-Self use |
| 132 | TOU-Charging | TOU-Charging |
| 133 | TOU-Discharging | TOU-Discharging |
| 1301-1309 | Advanced control modes | Power/SOC target control, self-consume modes, etc. |

### Battery Status Codes (Residential)

The battery's `battery.status.code` is an integer:

| Code | Status |
|------|--------|
| 0 | Idle |
| 1 | Work |

### API Error Codes (Appendix 1)

| Code | Message |
|------|---------|
| 10000 | Operation successful |
| 10001 | Operation failed |
| 11500 | System busy, please try again later |
| 10200 | Operation abnormality, please see the specific message content for details |
| 10400 | Request not authenticated |
| 10401 | Username or password incorrect |
| 10402 | Request access_token authentication failed |
| 10403 | Interface has no access rights |
| 10404 | Callback function not configured |
| 10405 | The number of API calls has been used up |
| 10406 | The API call rate has reached the upper limit, please try again later |
| 10500 | User has no device data permission |
| 10505 | Device unauthorized |
| 10506 | Plant unauthorized |

## Troubleshooting

**"SOLAX_CLIENT_ID environment variable not set"**
- Set the `SOLAX_CLIENT_ID` environment variable or register the server with the correct credentials

**"SOLAX_CLIENT_SECRET environment variable not set"**
- Set the `SOLAX_CLIENT_SECRET` environment variable or register the server with the correct credentials

**"No device_sn provided"**
- Either pass the `device_sn` argument to the tool or set the `SOLAX_DEVICE_SN` environment variable

**"SolaX API error 10402: Request access_token authentication failed"**
- The server will automatically attempt to refresh the access token once; if this persists, your Client Secret may be invalid or revoked. Re-register your OAuth2 application at https://developer.solaxcloud.com/

**"SolaX API error 10505: Device unauthorized"**
- The device serial number you provided is invalid or not associated with your account. Double-check the device SN in your SolaX Developer Platform account.

**"SolaX API error 10406: The API call rate has reached the upper limit"**
- The rate limiter is correctly enforced; this should rarely occur under normal usage. If it does, the server automatically backs off. Reduce tool call frequency or wait a few seconds and retry.

**"Network timeout"**
- SolaX Developer Platform API is unreachable. Check your internet connection and confirm the service is online at `https://developer.solaxcloud.com/`.

## Development

### Project Structure

```
solax-cloud-mcp/
├── src/solax_cloud_mcp/
│   ├── __init__.py         # Package metadata
│   ├── __main__.py         # Entry point: MCP (stdio) or HTTP, by TRANSPORT
│   ├── server.py           # MCP tools and the *_impl functions shared with HTTP
│   ├── http_server.py      # FastAPI routes, auth, scheduler start/stop
│   ├── automation.py       # Tariff windows, charge planner, export planner, scheduler
│   ├── client.py           # SolaX API calls, rate limit, remote control commands
│   ├── auth.py             # OAuth2 token management
│   ├── config.py           # Environment variable handling
│   ├── models.py           # Response shaping
│   ├── forecast.py         # Solcast forecast and cache
│   ├── prices.py           # RCE prices from PSE
│   ├── consumption.py      # Consumption profiles from SolaX history (CLI)
│   └── dashboard.html      # Dashboard page
├── tests/
│   ├── fixtures/           # Captured API responses
│   ├── test_automation.py  # Planners and scheduler
│   ├── test_client.py      # SolaX client
│   ├── test_config.py      # Configuration
│   ├── test_consumption.py # Consumption profiles
│   ├── test_forecast.py    # Solcast
│   ├── test_http_server.py # HTTP routes
│   ├── test_models.py      # Response shaping
│   └── test_prices.py      # RCE prices
├── Dockerfile, docker-compose.yml
├── .env.example            # All settings with comments
└── pyproject.toml          # Project metadata and dependencies
```

### Adding Features

To add a new data source or tool:

1. Call the external API in its own module (like `client.py`, `forecast.py`, `prices.py`).
2. Write a `*_impl` function in `server.py`, so MCP and HTTP share it.
3. Register the MCP tool in `server.py` and the route in `http_server.py`.
4. Anything that writes to the inverter ships with a dry-run switch that is on by default.

## License

MIT

## Support

For issues with this MCP server, open an issue on the repository.

For SolaX Developer Platform API documentation, refer to [SolaX Developer Platform](https://developer.solaxcloud.com/doc).
