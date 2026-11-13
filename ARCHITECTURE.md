# SolaX Cloud MCP - Architecture & How It Works

This document describes the internal architecture, data flows, and design patterns of the SolaX Cloud MCP server.

## Table of Contents

1. [System Overview](#system-overview)
2. [Component Architecture](#component-architecture)
3. [Authentication Flow](#authentication-flow)
4. [Data Flow](#data-flow)
5. [Rate Limiting](#rate-limiting)
6. [Response Shaping](#response-shaping)
7. [Error Handling](#error-handling)
8. [Battery Automation](#battery-automation-http-server-only)
9. [Deployment Architecture](#deployment-architecture)
10. [Key Design Patterns](#key-design-patterns)
11. [Testing Strategy](#testing-strategy)

---

## System Overview

The SolaX Cloud MCP server is a unified bridge between the SolaX Developer Platform API and two distinct consumption interfaces:

1. **MCP Mode (stdio)**: Protocol-based interface for Claude Desktop / Claude Code
2. **HTTP Mode**: REST API, dashboard and the battery automation (charge and export planners), run as a long-lived service (Proxmox LXC + systemd in production, or Docker)

The core logic (authentication, API calls, response shaping, forecast and prices) is shared between both modes. The automation scheduler runs only in HTTP mode.

```mermaid
graph TB
    subgraph "Clients"
        Claude["Claude Desktop<br/>Claude Code"]
        HTTP["HTTP Clients<br/>Python, JS, curl"]
    end
    
    subgraph "SolaX Cloud MCP Server"
        Transport["Transport Layer"]
        MCP["MCP Protocol<br/>stdio"]
        FastAPI["HTTP Server<br/>FastAPI"]
        
        subgraph "Core Logic (Shared)"
            Config["Configuration"]
            Auth["OAuth2 Token Mgmt"]
            Client["HTTP Client<br/>API Calls"]
            Models["Response Shaping"]
            Forecast["Solcast Forecast"]
            Prices["RCE Prices"]
        end
        Scheduler["Automation Scheduler<br/>(HTTP mode only)"]
    end
    
    subgraph "External"
        SolaxAPI["SolaX Developer<br/>Platform API"]
        SolcastAPI["Solcast API"]
        PSEAPI["PSE API (RCE)"]
    end
    
    Claude -->|MCP Messages| MCP
    HTTP -->|HTTP Requests| FastAPI
    MCP --> Transport
    FastAPI --> Transport
    Transport --> Config
    Transport --> Auth
    Transport --> Client
    Transport --> Models
    Client -->|OAuth2 + API Calls| SolaxAPI
    SolaxAPI -->|JSON Response| Client
    Forecast --> SolcastAPI
    Prices --> PSEAPI
    FastAPI --> Scheduler
    Scheduler --> Client
    Scheduler --> Forecast
    Scheduler --> Prices
```

---

## Component Architecture

### Component Diagram

```mermaid
graph LR
    subgraph "Configuration Layer"
        CONFIG["config.py<br/>Environment Variables"]
    end
    
    subgraph "Authentication Layer"
        AUTH["auth.py<br/>OAuth2 Token Lifecycle"]
    end
    
    subgraph "API Client Layer"
        CLIENT["client.py<br/>SolaX API Integration"]
    end
    
    subgraph "Data Transformation"
        MODELS["models.py<br/>Response Shaping"]
    end
    
    subgraph "Transport Interfaces"
        MCP["server.py<br/>MCP Tools"]
        HTTP["http_server.py<br/>FastAPI Routes"]
    end
    
    subgraph "Entry Point"
        MAIN["__main__.py<br/>Transport Router"]
    end
    
    MAIN -->|TRANSPORT=stdio| MCP
    MAIN -->|TRANSPORT=http| HTTP
    
    MCP --> CONFIG
    HTTP --> CONFIG
    
    MCP --> MODELS
    HTTP --> MODELS
    
    MODELS --> CLIENT
    
    CLIENT --> AUTH
    AUTH --> CONFIG
    
    CONFIG -->|Read| ENV["Environment"]
```

### File Structure

```
src/solax_cloud_mcp/
├── __main__.py          # Entry point: routes to MCP or HTTP based on TRANSPORT env var
├── __init__.py          # Package metadata
├── config.py            # Environment variable loading & validation
├── auth.py              # OAuth2 token lifecycle (fetch, cache, refresh, invalidate)
├── client.py            # SolaX API HTTP client (realtime data, battery control, rate limiting)
├── models.py            # Response shaping (normalize, decode status codes, group fields)
├── server.py            # MCP tools & shared business logic (*_impl functions)
├── http_server.py       # FastAPI HTTP server, routes, scheduler start/stop
├── automation.py        # Tariff windows, charge planner, export planner, AutomationScheduler
├── forecast.py          # Solcast forecast (multi-site sum, cache, optional cache file)
├── prices.py            # RCE prices from the PSE API (15-min slots, 30-min cache)
├── consumption.py       # CLI: hourly consumption profiles from SolaX history
└── dashboard.html       # Dashboard page served at /dashboard
```

---

## Authentication Flow

### OAuth2 Token Lifecycle

The server uses **client credentials** OAuth2 flow to obtain access tokens from SolaX.

```mermaid
sequenceDiagram
    participant App as Application
    participant Auth as auth.py
    participant Cache as Token Cache
    participant SolaxAuth as SolaX OAuth2<br/>Endpoint
    
    App->>Auth: get_access_token()
    Auth->>Cache: Check cached token<br/>within buffer period?
    
    alt Token Valid
        Cache-->>Auth: Return cached token
    else Token Expired or Missing
        Auth->>SolaxAuth: POST /oauth/token<br/>(client_id, client_secret)
        SolaxAuth-->>Auth: {access_token, expires_in}
        Auth->>Cache: Store token + expiry
        Auth-->>App: Return new token
    end
    
    Note over Auth: 5-minute buffer ensures<br/>token always valid
```

**Key Features:**

- **Token Caching**: Tokens are cached in memory and reused until expiry
- **Expiry Buffer**: 300-second (5-minute) buffer prevents edge-case token expiry during requests
- **Thread-Safe**: `asyncio.Lock()` ensures only one thread can refresh concurrently
- **Automatic Retry**: On 10402 (auth failure), the client automatically invalidates the cached token and retries once

### Credentials

Credentials are loaded from environment variables at startup:

```python
SOLAX_CLIENT_ID      # OAuth2 Client ID (required)
SOLAX_CLIENT_SECRET  # OAuth2 Client Secret (required)
SOLAX_DEVICE_SN      # Device serial number (optional, used as default)
```

---

## Data Flow

### Query Flow: Get Real-Time Data

```mermaid
sequenceDiagram
    participant User as Claude / HTTP Client
    participant Transport as Transport Layer
    participant Logic as Core Logic
    participant Auth as auth.py
    participant Client as client.py
    participant SolaxAPI as SolaX API
    participant Models as models.py
    
    User->>Transport: Request realtime_data(device_sn)
    Transport->>Logic: get_realtime_data_impl(device_sn)
    
    Logic->>Logic: Validate device_sn<br/>(use env var if not provided)
    
    Logic->>Client: fetch_realtime_data(device_sn)
    
    Client->>Client: Rate limiting check<br/>(0.7s min interval)
    
    par Parallel Fetch
        Client->>Auth: get_access_token()
        Auth-->>Client: access_token
    end
    
    Client->>SolaxAPI: GET /openapi/v2/device/realtime_data<br/>(snList, deviceType=1, bearer token)
    SolaxAPI-->>Client: Inverter JSON
    
    Client->>SolaxAPI: GET /openapi/v2/device/realtime_data<br/>(deviceType=2, battery query)
    SolaxAPI-->>Client: Battery JSON (or empty)
    
    Client->>Logic: (inverter_data, battery_data)
    
    Logic->>Models: shape_realtime_response(inverter, battery)
    
    Models->>Models: Normalize field names<br/>(to lowercase)
    Models->>Models: Parse dynamic PV strings<br/>Parse dynamic MPPT trackers<br/>Extract AC phases
    Models->>Models: Decode status codes<br/>(human-readable descriptions)
    Models->>Models: Group fields<br/>(device, status, pv, ac, battery, etc.)
    
    Models-->>Logic: Shaped response dict
    Logic-->>Transport: Shaped response
    Transport-->>User: JSON / MCP response
```

### Control Flow: Set Battery Mode

```mermaid
sequenceDiagram
    participant User as HTTP Client
    participant HTTP as http_server.py
    participant Logic as Core Logic
    participant Client as client.py
    participant Auth as auth.py
    participant SolaxAPI as SolaX API
    
    User->>HTTP: POST /api/battery/self-use-mode<br/>{min_soc, charge_upper_soc, ...}
    HTTP->>HTTP: Verify API key<br/>(Bearer token)
    
    HTTP->>Logic: set_battery_self_use_mode_impl(...)
    
    Logic->>Logic: Validate parameters<br/>(SOC ranges, min <= max)
    
    Logic->>Client: set_self_use_mode(device_sn, ...)
    
    Client->>Client: Rate limiting check
    
    Client->>Auth: get_access_token()
    Auth-->>Client: access_token
    
    Client->>SolaxAPI: POST /openapi/v2/device/inverter_work_mode/<br/>batch_set_spontaneity_self_use<br/>(snList, minSoc, chargeUpperSoc, ...bearer token)
    
    SolaxAPI-->>Client: {code: 10000, result: {status: OK}}
    
    Client-->>Logic: API response
    Logic-->>HTTP: API response
    HTTP-->>User: 200 OK + JSON
```

---

## Rate Limiting

The server implements **soft rate limiting** to respect SolaX API quotas.

### Rate Limit Strategy

```mermaid
graph TB
    subgraph "Rate Limiter"
        LastCall["Last Call Timestamp"]
        Lock["asyncio.Lock"]
        Sleep["Sleep if needed"]
    end
    
    Request["Incoming Request"]
    Check["Elapsed since<br/>last call?"]
    MinInterval["MIN_INTERVAL_SECONDS<br/>= 0.7 seconds"]
    
    Request --> Lock
    Lock --> Check
    Check -->|< 0.7s| Sleep
    Sleep --> Execute["Execute API Call"]
    Check -->|>= 0.7s| Execute
    Execute --> UpdateTime["Update Last Call<br/>Timestamp"]
    UpdateTime --> Release["Release Lock"]
```

**Configuration:**

- **Minimum Interval**: 0.7 seconds between API calls
- **Justification**: SolaX allows 100 calls/minute; 0.7s = 85-86 calls/minute (conservative buffer)
- **Enforcement**: Global `asyncio.Lock()` ensures thread-safe enforcement
- **Coverage**: Applied to both `fetch_realtime_data()` and `set_self_use_mode()`

**Behavior:**

```python
# If you make 3 requests immediately:
# Request 1: 0ms elapsed → execute immediately
# Request 2: happens at +0ms → sleep 0.7s, then execute at +0.7s
# Request 3: happens at +0.7ms → sleep 0.7s, then execute at +1.4s
```

---

## Response Shaping

The raw SolaX API response is complex and inconsistent. The response shaper transforms it into a clean, human-friendly structure.

### Normalization Pipeline

```mermaid
graph LR
    Raw["Raw API Response<br/>(mixed casing,<br/>flat structure)"]
    
    Normalize["1. Normalize<br/>Keys to lowercase"]
    DynPV["2. Parse Dynamic<br/>PV Strings<br/>(pv1voltage,<br/>pv2voltage, ...)"]
    DynMPPT["3. Parse Dynamic<br/>MPPT Trackers<br/>(mppt1voltage, ...)"]
    ExtractPhases["4. Extract AC Phases<br/>(3-phase fixed)"]
    DecodeStatus["5. Decode Status Codes<br/>(to human-readable)"]
    GroupFields["6. Group Fields<br/>(device, pv, ac,<br/>battery, etc.)"]
    
    Shaped["Shaped Response<br/>(clean, grouped,<br/>decoded)"]
    
    Raw --> Normalize
    Normalize --> DynPV
    DynPV --> DynMPPT
    DynMPPT --> ExtractPhases
    ExtractPhases --> DecodeStatus
    DecodeStatus --> GroupFields
    GroupFields --> Shaped
```

### Example Transformation

**Raw Response (from API):**
```json
{
  "deviceSn": "X3ABCD0123",
  "deviceStatus": 102,
  "pv1voltage": 421.7,
  "pv1current": 5.2,
  "pv1power": 2189.0,
  "pv2voltage": 418.0,
  "pv2current": 4.8,
  "pv2power": 2006.4,
  "acvoltage1": 224.1,
  "accurrent1": 18.2,
  "acpower1": 4078.0,
  ...
}
```

**Shaped Response:**
```json
{
  "device": {
    "deviceSn": "X3ABCD0123",
    "registerNo": "SE123456SP",
    ...
  },
  "status": {
    "code": 102,
    "description": "Normal"
  },
  "pv": [
    {"string": 1, "voltage_V": 421.7, "current_A": 5.2, "power_W": 2189.0},
    {"string": 2, "voltage_V": 418.0, "current_A": 4.8, "power_W": 2006.4}
  ],
  "ac": {
    "phases": [
      {"phase": 1, "voltage_V": 224.1, "current_A": 18.2, "power_W": 4078.0, ...}
    ],
    ...
  },
  ...
}
```

### Key Transformations

1. **Field Case Normalization**: Convert all keys to lowercase for consistent access
2. **Dynamic Parsing**: PV strings and MPPT trackers are variable-count; parser extracts indices and builds arrays
3. **Status Decoding**: Integer status codes (e.g., 102) are mapped to human-readable descriptions (e.g., "Normal")
4. **Logical Grouping**: Related fields are grouped into logical sections (device, pv, ac, battery, etc.)
5. **Unit Notation**: Field names include units (e.g., `voltage_V`, `power_W`) for clarity

---

## Error Handling

### Error Hierarchy

```mermaid
graph TB
    subgraph "SolaX Exceptions"
        Auth["SolaxAuthError<br/>(auth.py)"]
        API["SolaxApiError<br/>(client.py)"]
    end
    
    subgraph "Application Exceptions"
        Value["ValueError<br/>(validation)"]
        HTTP["HTTPException<br/>(HTTP mode)"]
    end
    
    subgraph "Error Triggers"
        NetFail["Network Failure"]
        JSONFail["Invalid JSON"]
        AuthFail["OAuth2 Failure"]
        APIErr["API Response Code != 10000"]
        BadParam["Invalid Parameters"]
    end
    
    NetFail --> Auth
    JSONFail --> Auth
    AuthFail --> Auth
    
    NetFail --> API
    JSONFail --> API
    APIErr --> API
    AuthFail --> API
    
    BadParam --> Value
    
    Auth -->|Caught in client.py| API
    API -->|Caught in server.py| Value
    Value -->|HTTP mode| HTTP
```

### Automatic Retry Logic

When the API returns **10402 (access_token authentication failed)**:

1. The cached token is invalidated
2. A fresh token is requested
3. The API call is retried once

```python
if code == 10402:
    auth.invalidate_token()          # Clear cache
    token = await auth.get_access_token()  # Fetch new token
    response = await client.get(...)  # Retry request
    code = response.json().get("code")
```

This handles cases where tokens expire between fetch and use, or credentials are briefly invalid.

### HTTP Error Responses

| Status Code | Scenario | Response |
|---|---|---|
| 200 | Success | `{...data...}` or `{code: 10000, result: {...}}` |
| 400 | Bad request (missing device_sn, invalid SOC) | `{detail: "error message"}` |
| 403 | Authentication failure (invalid/missing API key) | `{detail: "Invalid API key"}` |
| 500 | Unhandled server error | `{detail: "..."}` |

---

## Battery Automation (HTTP server only)

`automation.py` holds the planners as pure functions and one `AutomationScheduler`. `http_server.py` starts the scheduler when `AUTOMATION_ENABLED=1` and stops it on shutdown. MCP mode never starts it.

```mermaid
graph TB
    subgraph "AutomationScheduler (asyncio tasks)"
        Loop["Charge loop<br/>runs before each off-peak window"]
        Window["charge_window<br/>check every 15 min during the window"]
        Export["Export loop<br/>every 15 min"]
        Lock["Shared write lock"]
    end

    Solcast["Solcast<br/>(forecast.py)"]
    PSE["PSE RCE prices<br/>(prices.py)"]
    Solax["SolaX API<br/>(client.py)"]
    Files["DECISIONS_FILE<br/>SETTINGS_FILE"]

    Loop -->|compute_plan| Solcast
    Loop --> Window
    Window -->|realtime SOC| Solax
    Window -->|"charge_to_soc, exit"| Lock
    Export -->|compute_export_plan| Solcast
    Export --> PSE
    Export -->|realtime SOC + house load| Solax
    Export -->|"discharge_to_soc, exit"| Lock
    Lock --> Solax
    Loop --> Files
    Export --> Files
```

### Charge planner

1. `next_trigger` finds the next off-peak window start (`Tariff.windows_for`: G12w winter and summer hours, weekends and Polish public holidays) and runs `AUTOMATION_LEAD_MINUTES` before it.
2. `compute_plan` takes the peak hours after the window, sums consumption minus the PV forecast hour by hour, and turns the largest cumulative deficit into `targetSoc`.
3. `charge_window` runs from window start to window end. Every 15 minutes, if SOC is below the target, it sends `soc_target_control_mode` with a positive (charge) power of `AUTOMATION_CHARGE_POWER_KW`. The inverter stops by itself at the target. At the window end, and on errors or shutdown, it sends `exit_vpp_mode`.

The X3-NEO-LV ignores the Self Use grid-charge settings, so the planner uses remote control, not `batch_set_spontaneity_self_use`.

### Export planner

1. Every 15 minutes `compute_export_plan` computes the surplus: energy above `AUTOMATION_MIN_SOC` minus the house need until the next off-peak window.
2. It assigns the surplus to the most expensive 15-minute RCE slots whose net price is at or above `EXPORT_MIN_PRICE_PLN_KWH`.
3. `apply_export` sends `soc_target_control_mode` with a negative (discharge) power in an export slot. The setpoint is the planned export plus the live house load (`live_house_load_kw`), capped at `EXPORT_MAX_POWER_KW`. The target SOC is `recommendedExportFloorSoc`. Outside export slots it exits remote control.

### Safety rules

- Both planners are dry run by default (`AUTOMATION_DRY_RUN`, `EXPORT_DRY_RUN`).
- One `asyncio.Lock` serialises every inverter write. The export loop does nothing while a charge window is active.
- Export: after a failed command or planner error, outside export slots, and on server stop, the scheduler sends `exit_vpp_mode`. After a restart the remote control state is unknown, so the first export run exits it.
- Charge: a failed command is retried at the next 15-minute check. The exit is sent at the window end, also after errors and on server stop.
- `soc_target_control_mode` has no duration. If the server dies mid-command, the inverter still stops at the target SOC.
- The dashboard switch (`POST /api/export/active`) turns the export loop off and exits remote control at once. It is saved to `SETTINGS_FILE`.
- Every decision is logged and kept in memory (last 200), and in `DECISIONS_FILE` when it is set.

---

## Deployment Architecture

### Dual-Transport Architecture

```mermaid
graph TB
    subgraph "Entry Point"
        MAIN["__main__.py<br/>Check TRANSPORT env var"]
    end
    
    subgraph "MCP Mode (stdio)"
        MCP["server.py<br/>FastMCP server"]
        MCPProc["Process<br/>stdin/stdout"]
    end
    
    subgraph "HTTP Mode (containerized)"
        HTTP["http_server.py<br/>FastAPI + Uvicorn"]
        HTTPProc["Listening on<br/>0.0.0.0:8000"]
    end
    
    subgraph "Shared Core"
        Auth["OAuth2 Token Mgmt"]
        Client["SolaX API Client"]
        Models["Response Shaping"]
        Logic["Business Logic"]
    end
    
    MAIN -->|TRANSPORT=stdio| MCP
    MAIN -->|TRANSPORT=http| HTTP
    
    MCP --> MCPProc
    HTTP --> HTTPProc
    
    MCP --> Auth
    MCP --> Client
    MCP --> Models
    MCP --> Logic
    
    HTTP --> Auth
    HTTP --> Client
    HTTP --> Models
    HTTP --> Logic
```

### Container Deployment

```mermaid
graph LR
    subgraph "Docker Build"
        B1["Builder Stage"]
        B2["Runtime Stage"]
        B1 -->|Copy venv| B2
    end
    
    subgraph "Docker Run"
        Container["Container<br/>(python:3.11-slim)"]
        Env["Environment Variables"]
        Port["Port 8000"]
    end
    
    subgraph "Runtime"
        HC["Health Check<br/>(curl /health)"]
        Server["Uvicorn Server<br/>(HTTP mode)"]
    end
    
    B2 --> Container
    Env --> Container
    Port --> Container
    Container --> HC
    Container --> Server
```

**Multi-stage Build Benefits:**

- Builder stage: installs uv, compiles venv (large)
- Runtime stage: only includes minimal runtime dependencies + venv (small)
- Result: Lean image suitable for Raspberry Pi

---

## Key Design Patterns

### 1. Shared Impl Pattern

Both MCP and HTTP modes use the same **`*_impl()`** functions:

```python
# Shared implementation
async def get_realtime_data_impl(device_sn: str | None = None) -> dict:
    # Business logic here
    ...

# MCP wrapper
@server.tool()
async def get_realtime_data(device_sn: str | None = None) -> dict:
    return await get_realtime_data_impl(device_sn)

# HTTP wrapper
@app.post("/api/realtime-data")
async def get_realtime_data_endpoint(req: RealtimeDataRequest, authorization: str) -> dict:
    verify_api_key(authorization)
    return await get_realtime_data_impl(req.device_sn)
```

**Benefit**: Zero code duplication; business logic evolves in one place.

### 2. Global State with Lock

Token caching uses global state + asyncio.Lock for thread-safe access:

```python
_cached_token: str | None = None
_expires_at: float = 0.0
_auth_lock = asyncio.Lock()

async with _auth_lock:
    if _cached_token and time.monotonic() < _expires_at - BUFFER:
        return _cached_token
    # Otherwise fetch new token
```

**Benefit**: Minimal overhead; single token shared across all concurrent requests.

### 3. Transport-Agnostic Configuration

Configuration is loaded from environment variables, not hardcoded:

```python
SOLAX_CLIENT_ID = os.getenv("SOLAX_CLIENT_ID")
SOLAX_CLIENT_SECRET = os.getenv("SOLAX_CLIENT_SECRET")
SOLAX_DEVICE_SN = os.getenv("SOLAX_DEVICE_SN")
TRANSPORT = os.getenv("TRANSPORT", "stdio")
```

**Benefit**: Same codebase works in Docker, local development, and cloud environments.

### 4. Soft Rate Limiting

Rate limiting is soft — enforced per-process, not globally:

```python
_last_call_at = 0.0
_rate_limit_lock = asyncio.Lock()
MIN_INTERVAL_SECONDS = 0.7
```

**Benefit**: Simple, predictable, no distributed cache required.

---

## Data Consistency & Integrity

### API Call Resilience

```mermaid
graph TD
    Call["API Call"]
    HTTPFail["HTTP Error<br/>(timeout, connection)"]
    JSONFail["Invalid JSON"]
    Auth10402["API Error 10402<br/>(token expired)"]
    Other["Other API Error"]
    Success["Success"]
    
    Call --> HTTPFail
    Call --> JSONFail
    Call --> Auth10402
    Call --> Other
    Call --> Success
    
    HTTPFail -->|Raise SolaxApiError| Fail["Request Failed"]
    JSONFail -->|Raise SolaxApiError| Fail
    Auth10402 -->|Invalidate + Retry| Retry["Retry Once"]
    Retry --> Success
    Retry --> Fail2["Request Failed"]
    Other -->|Raise SolaxApiError| Fail
    Success -->|Return Data| Success2["Request Succeeded"]
    
    Fail --> App["Application<br/>Error Handling"]
    Fail2 --> App
    Success2 --> App
```

### Battery Data Graceful Degradation

If battery data is unavailable (battery-less system, offline, etc.):

```python
async def fetch_battery_data(device_sn: str) -> dict | None:
    try:
        return await _request_realtime(device_sn, DEVICE_TYPE_BATTERY, ...)
    except SolaxApiError:
        # No battery on this system; return None
        return None
```

The response shaper handles `None`:

```python
battery = None
if battery_result is not None:
    battery = {...build battery object...}
```

Result: `"battery": null` in shaped response (not an error).

---

## Performance Considerations

### Token Caching Impact

- **Without caching**: Every request would require an OAuth2 round-trip (~200ms)
- **With caching**: First request ~200ms, subsequent requests ~0ms (until expiry)
- **Result**: ~100x speedup for typical polling scenarios

### Rate Limiting Impact

- Enforces 0.7s minimum interval between calls
- For 100 calls/minute, roughly 1 call per 0.6 seconds → no breaches with buffer
- Prevents thundering herd and API throttling

### Response Shaping Impact

- Parsing & normalization: ~5-20ms per response
- Acceptable for typical 30-60 second poll intervals
- Only shapes data that will be consumed (lazy where possible)

### Memory Footprint

- Token cache: ~100 bytes per token
- Rate limiter state: ~16 bytes
- Per-request overhead: minimal (async-only, no threads)
- Suitable for Raspberry Pi (256MB+ RAM)

---

## Testing Strategy

### Unit Tests

Run with `uv run pytest -q` (148 tests). External APIs are mocked with `respx` or replaced with fakes; nothing reaches the inverter.

- `test_config.py`: Environment variable loading
- `test_models.py`: Response shaping, status decoding, dynamic parsing
- `test_client.py`: Token retry on 10402, remote control commands
- `test_forecast.py`: Solcast parsing, multi-site sum, cache and stale data
- `test_prices.py`: PSE RCE parsing (period end times, DST)
- `test_automation.py`: Tariff windows and holidays, charge and export plans, scheduler safety rules, export switch, decision history
- `test_http_server.py`: Auth, routes, error mapping, export test/stop, dashboard settings
- `test_consumption.py`: Consumption profiles from history

### Integration Tests

- Manual CLI test: `uv run python -c "...fetch_realtime_data()..."`
- HTTP server test: `curl http://localhost:8000/health`
- Docker test: `docker-compose up -d && curl http://localhost:8000/health`

### Test Coverage

- Edge cases: battery-less devices, null status codes, missing fields
- Error handling: network failures, auth failures, API errors
- Rate limiting: concurrent requests, interval enforcement
- Response shaping: PV string parsing, MPPT parsing, status decoding

---

## Conclusion

The SolaX Cloud MCP server is designed as a **lean, transport-agnostic bridge** between the complex SolaX API and clean consumer interfaces (MCP, HTTP). Key strengths:

- **Shared logic**: Business logic written once, used by both MCP and HTTP
- **Automatic resilience**: Token refresh, retries on auth failure
- **Clean responses**: Complex raw data transformed into intuitive structure
- **Soft rate limiting**: Respects API quotas without external coordination
- **Safe automation**: Dry run by default, one write lock, exit remote control on every error and on stop

The architecture prioritizes simplicity over sophistication — avoiding premature abstraction, caching only what's necessary, and failing fast with clear error messages.
