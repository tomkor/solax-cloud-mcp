# SolaX Cloud MCP Server

An MCP server that provides real-time access to solar inverter data from the SolaX Developer Platform API. Query your inverter's current power output, energy yields, battery status, and grid import/export data directly from Claude Code or Claude Desktop.

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
cd /Users/gary/mysrc/claude/solax-cloud-mcp

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
  -- uv run --directory /Users/gary/mysrc/claude/solax-cloud-mcp solax-cloud-mcp
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
      "/Users/gary/mysrc/claude/solax-cloud-mcp",
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

Once registered, the `get_realtime_data` tool is available in your MCP toolset. Use it to query real-time inverter data.

The `set_battery_self_use_mode` tool changes inverter settings and is **not exposed by default**, so an LLM (e.g. via prompt injection) cannot reconfigure the inverter. Set `SOLAX_ALLOW_WRITE=1` to enable it. Its `min_soc`, `charge_upper_soc` and `charge_from_grid_enable` arguments are required.

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

### Battery Automation (forecast-driven grid charging)

Built into the HTTP server and disabled by default. Designed for time-of-use tariffs such as Polish **G12w**: cheap at 22:00-06:00 and 13:00-15:00 on weekdays, all day on weekends and public holidays.

**How it works:** a few minutes before each daily off-peak window (`AUTOMATION_LEAD_MINUTES`), the planner:
1. Takes the peak-price hours right after the window, e.g. 06:00-13:00 after the night window and 15:00-22:00 after the midday one. When the window is followed by more off-peak time (Friday night, holidays), there is nothing to cover and grid charging is disabled.
2. For each of those hours, compares the consumption profile with the Solcast PV forecast (`AUTOMATION_FORECAST_PERCENTILE`). The largest cumulative deficit is the energy the battery must hold at the start of the peak, so PV surplus earlier in the segment offsets later deficits.
3. Converts that energy to a target SOC: `min_soc + (deficit / efficiency + margin) / capacity`, capped at `AUTOMATION_MAX_GRID_SOC`.
4. Calls Self Use Mode with `minSoc=AUTOMATION_MIN_SOC`, `chargeUpperSoc=target`, grid charging on only when target > reserve, and the charge period set to the off-peak window.

If the forecast or the SolaX call fails, the inverter keeps its previous settings and the error is logged.

**Safety:** `AUTOMATION_DRY_RUN=1` (default) only logs decisions. Watch `GET /api/automation` and the logs for a few days, check that the decisions match how your inverter behaves, then set `AUTOMATION_DRY_RUN=0`.

> Verify on your inverter that `chargeUpperSoc` ("Charge battery to") limits only **grid** charging during the charge period and does not cap PV charging. The planner relies on this.

**Endpoints** (bearer auth):
- `GET /api/automation`: enabled/dry-run status, next run, last result.
- `POST /api/automation/preview`: plan for the next window, computed now, never written.

House consumption comes from `AUTOMATION_CONSUMPTION_PROFILE`, 24 hourly kWh values (or a flat `AUTOMATION_DAILY_CONSUMPTION_KWH`). An optional `AUTOMATION_WEEKEND_CONSUMPTION_PROFILE` / `AUTOMATION_WEEKEND_DAILY_CONSUMPTION_KWH` applies on weekends and Polish public holidays.

See `.env.example` for all `AUTOMATION_*` and `TARIFF_*` settings.

### Market Prices and Export Planner (RCE, dry run)

**Prices:** the `get_energy_prices` MCP tool and `GET /api/prices?hours=24` return Polish market prices (RCE, 15-minute periods, PLN/kWh net) from the public PSE API (`api.raporty.pse.pl/api/rce-pln`, no key needed). Responses are cached for 30 minutes. PSE publishes the next day's prices in the afternoon.

**Export planner** (`EXPORT_ENABLED=1`, requires battery automation): every 15 minutes it reads the current SOC, the PV forecast and RCE prices, then:
1. Computes the battery **surplus**: energy above `AUTOMATION_MIN_SOC` minus what the house needs until the next off-peak window (consumption − PV, same model as the charge planner). Selling therefore never forces buying back at peak price.
2. Allocates the surplus to the most expensive slots at or above `EXPORT_MIN_PRICE_PLN_KWH` (default 1.0). `EXPORT_MAX_POWER_KW` is the **inverter AC output setpoint**: the house load is served first and only the rest goes to the grid. Per slot, `export = setpoint × 0.25 h − expected house load`. The plan shows `export_kWh`, `expectedHouseLoad_kWh` and the `dischargeSetpoint_kW` needed, which equals export plus load.

The plan also reports `recommendedExportFloorSoc`: the SOC floor that keeps enough energy for the house until the next off-peak window. Use it to tune a price-based export rule configured in SolaX Cloud (e.g. "discharge to X%, max 5 kW, if export price > 1 PLN"), which uses a fixed floor.

`POST /api/export/preview` shows the plan. `GET /api/automation` shows the last plan under `export`.

> **Dry run by default.** With `EXPORT_DRY_RUN=0` the server sends, at the start of each export slot, SolaX remote control `soc_target_control_mode` (discharge at the slot setpoint until `recommendedExportFloorSoc`). In any other slot it exits remote control, so the inverter goes back to its normal work mode. The mode has no duration: if the server stops mid-slot, the inverter keeps discharging but stops at the floor SOC. Charge-planner and export writes never run at the same time. Disable any SolaX Cloud price rule that also discharges, so the two do not fight. (`push_power` mode is accepted but ignored by the X3-NEO-LV.)

### Example Usage in Claude

> "What's the current power output of my solar inverter?"

Claude will call `get_realtime_data()` and report the results to you in human-friendly terms.

## Docker: Build, Deploy & HTTP Consumption

For containerized deployment on Raspberry Pi or any Docker-enabled system, you can run the server in HTTP mode:

### Build the Docker Image

```bash
# Build the image
docker build -t solax-cloud-mcp:latest .

# Verify the build
docker images | grep solax-cloud-mcp
```

### Deploy with Docker Compose

Configure your environment variables first:

```bash
# Copy and edit the environment file
cp .env.example .env
nano .env
```

Fill in your SolaX credentials and generate a strong API key:

```env
SOLAX_CLIENT_ID=your_client_id
SOLAX_CLIENT_SECRET=your_client_secret
SOLAX_DEVICE_SN=your_device_sn
HTTP_API_KEY=$(python -c "import secrets; print(secrets.token_urlsafe(32))")
```

Start the HTTP server:

```bash
# Build and start the container in the background
docker-compose up -d

# Check logs
docker-compose logs -f solax-http

# Verify it's running
curl http://localhost:8000/health
```

The server listens on port **8000** and is accessible at `http://YOUR_PI_IP:8000`.

### Consume the HTTP API

All endpoints (except `/health`) require a bearer token in the `Authorization` header.

#### Health Check (no authentication)

```bash
curl http://192.168.1.100:8000/health
```

Response:
```json
{"status": "ok"}
```

#### Get Real-Time Inverter Data

```bash
curl -X POST \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"device_sn": "X3ABCD0123"}' \
  http://192.168.1.100:8000/api/realtime-data
```

Returns current power output, battery SOC, grid export/import, and more.

#### Get Solar Forecast (Solcast)

```bash
curl -H "Authorization: Bearer YOUR_API_KEY" \
  "http://localhost:8000/api/solar-forecast?hours=24"
```

Returns `503` when Solcast is not configured.

#### Set Battery Self-Use Mode

Configure battery charging/discharging thresholds:

```bash
curl -X POST \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "device_sn": "X3ABCD0123",
    "min_soc": 20,
    "charge_upper_soc": 80,
    "charge_from_grid_enable": 1
  }' \
  http://192.168.1.100:8000/api/battery/self-use-mode
```

### Python / JavaScript Examples

Quick examples in Python and JavaScript are available in **[HTTP_API.md](HTTP_API.md)**, including:
- Polling for real-time updates
- Time-based battery scheduling
- Error handling patterns
- Rate limiting considerations

### Deployment on Raspberry Pi

Full deployment instructions (Docker installation, network setup, security, monitoring) are in **[DEPLOYMENT.md](DEPLOYMENT.md)**.

### Docker Management

```bash
# View logs
docker-compose logs -f solax-http

# Restart the server
docker-compose restart solax-http

# Stop the server
docker-compose down

# Rebuild after code changes
docker-compose up -d --build

# Check resource usage
docker stats solax-http
```

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
uv run pytest -v
```

Tests include:
- Response shaping and status decoding
- Environment variable validation
- Error handling and edge cases (battery-less devices, null status codes)
- Dynamic PV/MPPT parsing
- Case-insensitive field access

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
│   ├── __main__.py         # CLI entry point
│   ├── server.py           # MCP server and tool definitions
│   ├── client.py           # HTTP client and API calls
│   ├── auth.py             # OAuth2 token management
│   ├── config.py           # Environment variable handling
│   └── models.py           # Data models and response shaping
├── tests/
│   ├── fixtures/           # Test data
│   ├── test_config.py      # Configuration tests
│   └── test_models.py      # Response shaping tests
├── pyproject.toml          # Project metadata and dependencies
└── README.md               # This file
```

### Adding Features

The server is designed to be minimal and focused. To add more endpoints/tools:

1. Fetch the data from SolaX Developer Platform API (extend `client.py`)
2. Add a response shaping function if needed (extend `models.py`)
3. Define a new `@server.tool()` in `server.py`

## License

MIT

## Support

For issues with this MCP server, open an issue on the repository.

For SolaX Developer Platform API documentation, refer to [SolaX Developer Platform](https://developer.solaxcloud.com/doc).
