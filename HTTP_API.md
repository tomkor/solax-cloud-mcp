# SolaX Cloud HTTP API Reference

Complete guide for consuming real-time solar inverter data and controlling battery settings via HTTP endpoints.

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
http://YOUR_PI_IP:8000
```

Example: `http://192.168.1.100:8000`

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

Configure battery charging/discharging thresholds and time periods. Ideal for automation based on weather, time-of-use rates, or other conditions.

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

### 5. Export Test (manual battery discharge)

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

### 6. Export Stop

Stop any remote-control command now. The inverter returns to its normal work mode (e.g. Self Use).

```http
POST /api/battery/export-stop HTTP/1.1
Authorization: Bearer YOUR_API_KEY
```

**Response (200 OK):** `{"stopped": true}`

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
- **Power (AC, Grid, Battery):**
  - Positive = consuming/charging
  - Negative = exporting/discharging
- **Grid Power:**
  - Positive = importing from grid
  - Negative = exporting to grid

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
- Verify server is running: `docker-compose ps`
- Check IP/port: use local network IP (e.g., `192.168.1.100`), not `localhost`
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

## Examples Repository

For complete working examples in multiple languages, see the `/examples` directory (if available).

For more details on the underlying API and data fields, see [README.md](README.md).
