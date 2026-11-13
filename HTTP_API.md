# SolaX Cloud HTTP API Reference

Every endpoint of the HTTP server (`TRANSPORT=http`): inverter data, PV forecast, RCE prices, the charge and export planners, manual battery control and the dashboard.

## Authentication

All endpoints (except `/health`) require a **bearer token** in the `Authorization` header:

```
Authorization: Bearer YOUR_API_KEY
```

Generate your API key and store it in the `HTTP_API_KEY` environment variable. To generate a strong key:

```bash
# Linux/Mac
openssl rand -base64 32

# Python
python -c "import secrets; print(secrets.token_urlsafe(32))"
```

## Base URL

```
http://SERVER_IP:8000
```

Example: `http://192.168.1.100:8000`. The server listens on `127.0.0.1` unless `HTTP_HOST=0.0.0.0` is set (Docker: `HTTP_BIND_ADDR`).

---

## Endpoints

### 1. Health Check

No authentication required. Use this to verify the server is running.

**Request:**
```http
GET /health
```

**Response (200 OK):**
```json
{
  "status": "ok"
}
```

**Example:**
```bash
curl http://192.168.1.100:8000/health
```

---

### 2. Get Real-Time Data

Fetch current inverter status, power output, battery state, and energy metrics.

**Request:**
```http
POST /api/realtime-data
Authorization: Bearer YOUR_API_KEY
Content-Type: application/json

{
  "device_sn": "X3ABCD0123"
}
```

**Parameters:**
- `device_sn` (string, optional): Inverter serial number. If omitted, uses `SOLAX_DEVICE_SN` environment variable.

**Response (200 OK):**
```json
{
  "device": {
    "deviceSn": "X3ABCD0123",
    "registerNo": "SE123456SP",
    "dataTime": "2025-07-14 15:30:45",
    "plantLocalTime": "2025-07-14 15:30:45"
  },
  "status": {
    "code": 102,
    "description": "Normal"
  },
  "pv": [
    {
      "string": 1,
      "voltage_V": 421.7,
      "current_A": 5.2,
      "power_W": 2189.0
    },
    {
      "string": 2,
      "voltage_V": 418.0,
      "current_A": 4.8,
      "power_W": 2006.4
    }
  ],
  "ac": {
    "phases": [
      {
        "phase": 1,
        "voltage_V": 224.1,
        "current_A": 18.2,
        "power_W": 4078.0,
        "frequency_Hz": 50.0
      },
      {
        "phase": 2,
        "voltage_V": 226.5,
        "current_A": 17.3,
        "power_W": 3914.5,
        "frequency_Hz": 50.0
      }
    ],
    "totalPower_W": 7992.5,
    "totalReactivePower": 142,
    "powerFactor": 0.99,
    "gridFrequency": 50.0
  },
  "energy": {
    "dailyYield_kWh": 28.4,
    "totalYield_kWh": 20465.3,
    "dailyACOutput_kWh": 28.9,
    "totalACOutput_kWh": 19907.5
  },
  "meter1": {
    "gridPower_W": -1500,
    "todayImportEnergy_kWh": 0.07,
    "totalImportEnergy_kWh": 23.28,
    "todayExportEnergy_kWh": 12.3,
    "totalExportEnergy_kWh": 64.98
  },
  "meter2": {
    "gridPower_W": -150,
    "todayImportEnergy_kWh": 0.08,
    "totalImportEnergy_kWh": 1.39,
    "todayExportEnergy_kWh": 0.2,
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
    "status": {
      "code": 1,
      "description": "Work"
    }
  },
  "temperature": {
    "inverter_C": 40.4
  }
}
```

**Error Response (400 Bad Request):**
```json
{
  "detail": "No device_sn provided: either pass device_sn argument or set SOLAX_DEVICE_SN environment variable"
}
```

**Error Response (403 Forbidden):**
```json
{
  "detail": "Invalid API key"
}
```

**Examples:**

```bash
# Using curl
curl -X POST \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"device_sn": "X3ABCD0123"}' \
  http://192.168.1.100:8000/api/realtime-data
```

```python
# Python with requests
import requests

api_key = "YOUR_API_KEY"
headers = {"Authorization": f"Bearer {api_key}"}
payload = {"device_sn": "X3ABCD0123"}

response = requests.post(
    "http://192.168.1.100:8000/api/realtime-data",
    json=payload,
    headers=headers
)
data = response.json()
print(f"Current power: {data['ac']['totalPower_W']} W")
print(f"Battery SOC: {data['battery']['soc_percent']}%")
```

```javascript
// JavaScript / Node.js with fetch
const apiKey = "YOUR_API_KEY";
const response = await fetch("http://192.168.1.100:8000/api/realtime-data", {
  method: "POST",
  headers: {
    "Authorization": `Bearer ${apiKey}`,
    "Content-Type": "application/json"
  },
  body: JSON.stringify({ device_sn: "X3ABCD0123" })
});

const data = await response.json();
console.log(`Current power: ${data.ac.totalPower_W} W`);
console.log(`Battery SOC: ${data.battery.soc_percent}%`);
```

---

### 3. Set Battery Self-Use Mode

Configure battery charging/discharging thresholds and time periods.

> **X3-NEO-LV:** the inverter accepts `charge_from_grid_enable` and `charge_upper_soc` but does not act on them, so they do not start grid charging. Use the charge planner (`AUTOMATION_ENABLED=1`, see README) for forecast-driven grid charging; it uses SolaX remote control.

**Request:**
```http
POST /api/battery/self-use-mode
Authorization: Bearer YOUR_API_KEY
Content-Type: application/json

{
  "device_sn": "X3ABCD0123",
  "min_soc": 20,
  "charge_upper_soc": 80,
  "charge_from_grid_enable": 1,
  "charge_start_time_period1": "06:00",
  "charge_end_time_period1": "18:00",
  "discharge_start_time_period1": "18:00",
  "discharge_end_time_period1": "06:00"
}
```

**Parameters:**
- `device_sn` (string, optional): Inverter serial number. If omitted, uses `SOLAX_DEVICE_SN` env var.
- `min_soc` (integer, 10-100, **required**): Minimum SOC (%). Battery won't discharge below this.
- `charge_upper_soc` (integer, 10-100, **required**): Maximum charging SOC (%). Battery won't charge above this.
- `charge_from_grid_enable` (integer, 0/1, **required**): Allow charging from grid. `0`=disabled, `1`=enabled.

`min_soc`, `charge_upper_soc` and `charge_from_grid_enable` have no defaults, so a request without them is rejected (`422`) instead of silently overwriting inverter settings. Time fields must match `HH:MM` (`00:00`–`23:59`). Upstream SolaX errors are returned as `502` with a generic message; details are logged on the server only.
- `charge_start_time_period1` (string, optional): HH:MM format (e.g., `"06:00"`)
- `charge_end_time_period1` (string, optional): HH:MM format
- `discharge_start_time_period1` (string, optional): HH:MM format
- `discharge_end_time_period1` (string, optional): HH:MM format
- `enable_time_period2` (integer, 0/1): Enable a second time period. Default: `0`
- `charge_start_time_period2` (string, optional): Second period start
- `charge_end_time_period2` (string, optional): Second period end
- `discharge_start_time_period2` (string, optional): Second period start
- `discharge_end_time_period2` (string, optional): Second period end

**Response (200 OK):**
```json
{
  "code": 10000,
  "message": "Operation successful",
  "result": {
    "status": "OK"
  }
}
```

**Error Response (400 Bad Request):**
```json
{
  "detail": "min_soc (50) cannot be greater than charge_upper_soc (40)"
}
```

**Error Response (403 Forbidden):**
```json
{
  "detail": "Invalid API key"
}
```

**Examples:**

```bash
# Simple: Set SOC limits only (20% min, 80% max)
curl -X POST \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "device_sn": "X3ABCD0123",
    "min_soc": 20,
    "charge_upper_soc": 80
  }' \
  http://192.168.1.100:8000/api/battery/self-use-mode
```

```bash
# Advanced: With time-based periods (charge during peak solar, discharge during peak rates)
curl -X POST \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "device_sn": "X3ABCD0123",
    "min_soc": 15,
    "charge_upper_soc": 90,
    "charge_from_grid_enable": 0,
    "charge_start_time_period1": "06:00",
    "charge_end_time_period1": "14:00",
    "discharge_start_time_period1": "17:00",
    "discharge_end_time_period1": "21:00"
  }' \
  http://192.168.1.100:8000/api/battery/self-use-mode
```

```python
# Python: Automation example - adjust SOC based on weather forecast
import requests
from datetime import datetime

api_key = "YOUR_API_KEY"
headers = {"Authorization": f"Bearer {api_key}"}

def adjust_battery_for_weather(forecast_condition):
    """Adjust battery thresholds based on weather forecast."""
    if forecast_condition == "sunny":
        # Sunny day: charge more to capture solar
        config = {
            "device_sn": "X3ABCD0123",
            "min_soc": 10,
            "charge_upper_soc": 100
        }
    elif forecast_condition == "cloudy":
        # Cloudy: preserve battery, don't overcharge
        config = {
            "device_sn": "X3ABCD0123",
            "min_soc": 30,
            "charge_upper_soc": 70
        }
    else:  # rainy
        # Rainy: keep high reserve for nighttime
        config = {
            "device_sn": "X3ABCD0123",
            "min_soc": 40,
            "charge_upper_soc": 90
        }
    
    response = requests.post(
        "http://192.168.1.100:8000/api/battery/self-use-mode",
        json=config,
        headers=headers
    )
    return response.json()

# Use it
result = adjust_battery_for_weather("sunny")
print(f"Battery configured: {result}")
```

```javascript
// Node.js: Scheduled adjustment for time-of-use rates
const schedule = require('node-schedule');

const apiKey = "YOUR_API_KEY";

// During peak rate hours (17:00-21:00): discharge battery
schedule.scheduleJob('0 17 * * *', async () => {
  await fetch("http://192.168.1.100:8000/api/battery/self-use-mode", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${apiKey}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      device_sn: "X3ABCD0123",
      min_soc: 20,
      charge_upper_soc: 50,
      discharge_start_time_period1: "17:00",
      discharge_end_time_period1: "21:00"
    })
  });
  console.log("Switched to peak rate discharge mode");
});

// During off-peak hours (23:00-06:00): allow grid charging
schedule.scheduleJob('0 23 * * *', async () => {
  await fetch("http://192.168.1.100:8000/api/battery/self-use-mode", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${apiKey}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      device_sn": "X3ABCD0123",
      min_soc": 10,
      charge_upper_soc": 100,
      charge_from_grid_enable": 1,
      charge_start_time_period1": "23:00",
      charge_end_time_period1": "06:00"
    })
  });
  console.log("Switched to off-peak charging mode");
});
```

---

### 4. Get Solar Forecast (Solcast)

Get the PV production forecast for the installation. Requires `SOLCAST_API_KEY` and `SOLCAST_RESOURCE_IDS` (see README); otherwise returns `503`.

**Request:**
```http
GET /api/solar-forecast?hours=24 HTTP/1.1
Authorization: Bearer YOUR_API_KEY
```

**Query parameters:**
- `hours` (integer, 1-168, optional): Length of the hourly profile. Default: `24`

**Response (200 OK):**
```json
{
  "source": "solcast",
  "fetchedAt": "2026-07-01T09:12:03+02:00",
  "stale": false,
  "timezone": "Europe/Warsaw",
  "days": [
    {
      "date": "2026-07-01",
      "energy_kWh": {"p10": 18.2, "p50": 31.4, "p90": 36.0},
      "peakPower_kW": 6.8,
      "peakTime": "2026-07-01T13:00+02:00"
    }
  ],
  "remainingToday_kWh": {"p10": 15.1, "p50": 27.9, "p90": 32.2},
  "hourly": [
    {"time": "2026-07-01T10:00+02:00", "power_kW": {"p10": 2.1, "p50": 4.3, "p90": 5.0}}
  ]
}
```

- `p50` is the most likely value, `p10` a pessimistic and `p90` an optimistic scenario.
- Data is cached (`SOLCAST_CACHE_MINUTES`, default 180) because Solcast hobbyist accounts allow ~10 calls/day. `stale: true` means the latest refresh failed and cached data is returned.
- Solcast failures return `502` with a generic message; details are logged on the server.

---

### 5. Get Energy Prices (RCE)

Polish market prices (RCE, 15-minute slots, PLN/kWh net) from the public PSE API. No PSE key needed; cached for 30 minutes.

```http
GET /api/prices?hours=24 HTTP/1.1
Authorization: Bearer YOUR_API_KEY
```

- `hours` (integer, 1-48, optional, default `24`): how far ahead. PSE publishes the next day's prices in the afternoon, so the list can be shorter.

**Response (200 OK):**
```json
{
  "source": "PSE RCE",
  "unit": "PLN/kWh (net)",
  "slots": [
    {"start": "2026-11-13T17:00+01:00", "end": "2026-11-13T17:15+01:00", "price_PLN_kWh": 1.1532}
  ],
  "max": 1.1532,
  "min": 0.3120
}
```

Times are Polish local time (PSE publishes them that way). A PSE failure returns `502`.

---

### 6. Automation Status

Status of the charge planner and the export loop. Returns `{"enabled": false}` when `AUTOMATION_ENABLED` is not set.

```http
GET /api/automation HTTP/1.1
Authorization: Bearer YOUR_API_KEY
```

**Response (200 OK), shortened:**
```json
{
  "enabled": true,
  "dryRun": false,
  "nextRun": "2026-11-13T21:50+01:00",
  "nextWindow": "22:00-06:00",
  "lastResult": {"window": "13:00-15:00", "targetSoc": 42, "gridCharge": true, "dryRun": false},
  "charging": false,
  "export": {
    "enabled": true,
    "active": true,
    "persisted": true,
    "dryRun": false,
    "lastPlan": {"socPercent": 78, "currentSlot": {"export": false}},
    "lastRealtime": {"battery": {"soc_percent": 78}}
  }
}
```

- `lastResult`: the last charge plan (same shape as the preview below), or an `error` entry.
- `charging`: `true` while an off-peak window with grid charging is running.
- `export.active`: the dashboard switch. `export.persisted`: whether `SETTINGS_FILE` is set.
- `export.lastPlan`: the last export plan (same shape as the export preview). `export.lastRealtime`: the realtime data that run read, so you do not need an extra SolaX call.

---

### 7. Charge Plan Preview

Computes the grid-charge plan for the next off-peak window now. **Never writes to the inverter.** Returns `503` when automation is off and `502` when Solcast fails.

```http
POST /api/automation/preview HTTP/1.1
Authorization: Bearer YOUR_API_KEY
```

**Response (200 OK):**
```json
{
  "runAt": "2026-11-13T21:50+01:00",
  "window": "22:00-06:00",
  "windowStart": "2026-11-13T22:00+01:00",
  "windowEnd": "2026-11-14T06:00+01:00",
  "peakSegment": {
    "start": "2026-11-14T06:00+01:00",
    "end": "2026-11-14T13:00+01:00",
    "hours": 7,
    "pvForecast_kWh": 3.1,
    "consumption_kWh": 4.9,
    "maxDeficit_kWh": 2.4
  },
  "percentile": "p50",
  "needFromBattery_kWh": 3.67,
  "minSoc": 10,
  "targetSoc": 28,
  "gridCharge": true,
  "forecastFetchedAt": "2026-11-13T18:02+01:00",
  "forecastStale": false,
  "dryRun": false
}
```

- All times are local (`SOLAR_TIMEZONE`) with their UTC offset.
- `peakSegment`: the peak-price hours after the window that the battery must cover.
- `targetSoc`: SOC to reach from the grid during the window. `gridCharge: false` means no grid charging.

---

### 8. Export Plan Preview

Computes the export plan now from the current SOC, the PV forecast and RCE prices. **Never writes to the inverter.** Returns `503` when `EXPORT_ENABLED` is not set and `502` when an upstream API fails.

```http
POST /api/export/preview HTTP/1.1
Authorization: Bearer YOUR_API_KEY
```

**Response (200 OK), one slot shown:**
```json
{
  "now": "2026-11-13T16:05+01:00",
  "horizonEnd": "2026-11-13T22:00+01:00",
  "socPercent": 78,
  "storedAboveMinSoc_kWh": 14.42,
  "reserveForHouse_kWh": 5.1,
  "surplus_kWh": 9.32,
  "recommendedExportFloorSoc": 35,
  "minPrice_PLN_kWh": 1.0,
  "slots": [
    {
      "start": "2026-11-13T17:00+01:00",
      "end": "2026-11-13T17:15+01:00",
      "price_PLN_kWh": 1.42,
      "export_kWh": 1.0,
      "expectedHouseLoad_kWh": 0.25,
      "dischargeSetpoint_kW": 5.0,
      "revenue_PLN": 1.42
    }
  ],
  "plannedExport_kWh": 9.32,
  "currentSlot": {"export": false},
  "dryRun": false
}
```

- `horizonEnd`: start of the next off-peak window. The battery keeps `reserveForHouse_kWh` for the house until then.
- `recommendedExportFloorSoc`: the export stops at this SOC.
- `minPrice_PLN_kWh` is compared with the **net** RCE price. Slot `price_PLN_kWh` and `revenue_PLN` include `EXPORT_PRICE_MULTIPLIER`, so they can be higher than the threshold.
- `dischargeSetpoint_kW` = planned export + expected house load, at the inverter AC port.
- `currentSlot`: `{"export": true, "dischargeSetpoint_kW", "expectedExport_kW", "price_PLN_kWh"}` when the current slot is an export slot.

---

### 9. Export Test (manual battery discharge)

Discharge the battery for a few minutes to test export control. **This writes to the inverter.** It uses SolaX remote control (`soc_target_control_mode`) on `SOLAX_DEVICE_SN`.

**Request:**
```http
POST /api/battery/export-test HTTP/1.1
Authorization: Bearer YOUR_API_KEY
X-Confirm: yes
Content-Type: application/json

{"power_kw": 1.5, "minutes": 2, "stop_soc": 85}
```

- `power_kw` (number, > 0, max 2): discharge power at the inverter AC output. The house load is served first and only the rest is exported.
- `minutes` (integer, 1-5): test duration. The server exits remote control after this time.
- `stop_soc` (integer, 30-100): the inverter stops discharging at this SOC by itself, even if the server is down. If it is not below the current SOC, the inverter exits the mode right away.
- Without `X-Confirm: yes` the server returns `400` and sends nothing.

**Response (200 OK):**
```json
{"dischargeSetpoint_W": 1500, "stopSoc": 85, "durationSeconds": 120, "result": {"X3******01": {"status": 3}}}
```

- If the command fails, the server sends the exit right away and returns `502`.

### 10. Export Stop

Stop any remote-control command now. The inverter returns to its normal work mode (e.g. Self Use).

```http
POST /api/battery/export-stop HTTP/1.1
Authorization: Bearer YOUR_API_KEY
```

**Response (200 OK):** `{"stopped": true}`

### 11. Dashboard

`GET /dashboard` serves an HTML page (no auth: the page holds no data). It asks once for `HTTP_API_KEY`, keeps it in the browser's `localStorage` and calls the API with it. It shows the export loop's latest inverter reading; a fresh SolaX call happens only when you press **Odczyt z falownika** (SolaX rate limit). Its only write action is the export switch below.

`GET /api/dashboard/settings` (bearer token) returns the planner and export settings (no secrets), the next 6 charge-planner runs and the next export-loop tick, or `{"enabled": false}`.

`POST /api/export/active` (bearer token, body `{"active": false}`) turns the export loop off or on; the dashboard's "Wyłącz/Włącz eksport" button calls it. Off also exits remote control at once. The switch is saved to `SETTINGS_FILE` (JSON) and survives restarts; the response `{"active": ..., "persisted": true|false}` says whether it was saved. Without `SETTINGS_FILE` export is on again after a restart. Returns 503 when `EXPORT_ENABLED` is not set.

`GET /api/automation/history` (bearer token) returns the last 200 charge and export decisions, newest first. Idle export runs are not recorded. Set `DECISIONS_FILE` to keep them across restarts.

## Common Patterns

### Polling for Real-Time Updates

Poll the `/api/realtime-data` endpoint at regular intervals to monitor inverter status:

```python
import requests
import time

api_key = "YOUR_API_KEY"
headers = {"Authorization": f"Bearer {api_key}"}

while True:
    response = requests.post(
        "http://192.168.1.100:8000/api/realtime-data",
        json={"device_sn": "X3ABCD0123"},
        headers=headers
    )
    data = response.json()
    
    print(f"[{time.strftime('%H:%M:%S')}] "
          f"Power: {data['ac']['totalPower_W']:.0f}W, "
          f"Battery: {data['battery']['soc_percent']:.1f}%, "
          f"Grid: {data['meter1']['gridPower_W']:.0f}W")
    
    time.sleep(30)  # Poll every 30 seconds
```

### Rate Limiting

The SolaX API allows **100 calls/minute** per token. The HTTP server enforces a **0.7-second minimum spacing** between calls automatically. If you exceed this:

```python
# This will respect the rate limiter automatically
for i in range(10):
    response = requests.post(
        "http://192.168.1.100:8000/api/realtime-data",
        json={"device_sn": "X3ABCD0123"},
        headers=headers
    )
    # Each call is spaced 0.7s apart, even if you call immediately
```

### Error Handling

```python
import requests

api_key = "YOUR_API_KEY"
headers = {"Authorization": f"Bearer {api_key}"}

try:
    response = requests.post(
        "http://192.168.1.100:8000/api/realtime-data",
        json={"device_sn": "X3ABCD0123"},
        headers=headers,
        timeout=10
    )
    response.raise_for_status()  # Raise on 4xx/5xx
    data = response.json()
    
except requests.exceptions.Timeout:
    print("Request timed out (server is slow or unreachable)")
    
except requests.exceptions.ConnectionError:
    print("Cannot connect to server (check IP/port)")
    
except requests.exceptions.HTTPError as e:
    if response.status_code == 403:
        print("Invalid API key")
    elif response.status_code == 400:
        print(f"Bad request: {response.json()['detail']}")
    else:
        print(f"HTTP {response.status_code}: {response.text}")
        
except requests.exceptions.JSONDecodeError:
    print(f"Invalid JSON response: {response.text}")
```

### Field Reference

**Power & Energy (SI Units)**
- Power: Watts (W) — instantaneous
- Energy: Kilowatt-hours (kWh) — cumulative
- Reactive Power: VAR (volt-ampere reactive)
- Power Factor: 0.0-1.0 (unitless)

**Electrical**
- Voltage: Volts (V)
- Current: Amps (A)
- Frequency: Hertz (Hz)

**Battery**
- SOC: State of Charge, 0-100%
- SOH: State of Health, 0-100%
- Cycle Times: Number of charge/discharge cycles

**Sign Conventions**
- **Grid power (`meter1.gridPower_W`):** positive = export to the grid, negative = import (SolaX docs). The planner computes house load as AC output minus grid power.
- **AC phases (`ac.phases[].power_W`):** inverter AC output.
- **Battery (`battery.chargeDischargePower_W`):** sign as reported by SolaX. Check it on your device before you use it in scripts.

### Status Codes

**Inverter Status (status.code)**
- 102 = Normal
- 103 = Fault
- 107 = EPS Mode
- 131-133 = TOU modes (Time-Of-Use)

See README.md Appendix 6 for complete list.

**Battery Status (battery.status.code)**
- 0 = Idle
- 1 = Working

---

## Troubleshooting

### "Invalid API key"
- Verify `HTTP_API_KEY` is set in `.env`
- Check Authorization header format: `Bearer YOUR_KEY` (space required)
- Confirm the header is present in all requests (except `/health`)

### "No device_sn provided"
- Either pass `device_sn` in request body
- Or set `SOLAX_DEVICE_SN` environment variable in `.env`

### "Connection refused"
- Verify the server is running: `systemctl status solax` (LXC) or `docker-compose ps` (Docker)
- From another machine, use the server's LAN IP and make sure it listens on it (`HTTP_HOST=0.0.0.0`, or `HTTP_BIND_ADDR` in Docker)
- Firewall: ensure port 8000 is not blocked

### "SolaX API error 10402"
- Your OAuth2 credentials are invalid or revoked
- Update `SOLAX_CLIENT_ID` and `SOLAX_CLIENT_SECRET` in `.env`
- Regenerate OAuth2 app at https://developer.solaxcloud.com/

### "SolaX API error 10505: Device unauthorized"
- Device serial number is incorrect
- Verify `SOLAX_DEVICE_SN` against your SolaX account
- Ensure you have API permissions for the device

### Request hangs or times out
- Server may be restarting: wait a few seconds
- Check Pi memory/CPU: `docker stats solax-http`
- Add explicit timeout to requests (recommended: 30-60 seconds for production)

---

## Best Practices

1. **Generate a strong API key** — use `openssl rand -base64 32`
2. **Store in environment variable** — never hardcode in scripts
3. **Use connection pooling** — reuse HTTP connections in long-running apps
4. **Set timeouts** — recommend 30-60 seconds for SolaX API calls
5. **Implement retry logic** — transient network failures are normal on IoT
6. **Log requests** — track API calls for debugging
7. **Respect rate limits** — 100 calls/minute is enforced server-side
8. **Monitor server health** — periodically call `/health` endpoint
9. **Use appropriate intervals** — 30-60 second polling is typical for solar data

---

## More

For more details on the underlying API and data fields, see [README.md](README.md).
