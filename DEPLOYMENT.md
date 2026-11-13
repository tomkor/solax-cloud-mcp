# Deployment Guide

This MCP server can be deployed in two modes:

1. **MCP Mode (stdio)**: Default for Claude integration
2. **HTTP Mode** (`TRANSPORT=http`): REST API, dashboard and the battery automation. Run it as a long-lived service: on a Proxmox LXC with systemd (the production setup, below) or in Docker (Raspberry Pi or any Docker host).

## Proxmox LXC with systemd (no Docker)

The production instance runs this way: a Debian 13 LXC (1 vCPU, 512 MB RAM, 4 GB disk, unprivileged, start on boot), the app in `/opt/solax/app` owned by a `solax` system user. The app is published self-contained on the dev machine, so the container needs no .NET install.

**Run only one instance.** Each new SolaX access token invalidates the previous one, and two servers would also fight over the inverter.

```bash
# In the container (as root)
apt-get install -y ca-certificates tzdata rsync
useradd --system --create-home --home-dir /opt/solax --shell /usr/sbin/nologin solax

# On the dev machine (needs the .NET 10 SDK): build for the container and copy it with .env
# (in .env set HTTP_HOST=0.0.0.0 and SOLCAST_CACHE_FILE=/opt/solax/solcast-cache.json and DECISIONS_FILE=/opt/solax/decisions.jsonl and SETTINGS_FILE=/opt/solax/settings.json)
dotnet publish src/SolaxCloudMcp -c Release -r linux-x64 --self-contained -o publish
rsync -a --delete --exclude .env publish/ root@<lxc-ip>:/opt/solax/app/
rsync -a .env root@<lxc-ip>:/opt/solax/app/.env

# In the container
chmod 600 /opt/solax/app/.env && chown -R solax:solax /opt/solax
```

`/etc/systemd/system/solax.service`:

```ini
[Unit]
Description=SolaX Cloud HTTP server (battery automation and export)
Wants=network-online.target
After=network-online.target

[Service]
User=solax
WorkingDirectory=/opt/solax/app
EnvironmentFile=/opt/solax/app/.env
ExecStart=/opt/solax/app/SolaxCloudMcp
Restart=on-failure
RestartSec=10
# On stop the server exits SolaX remote control before quitting (it takes up to 45 s for this)
TimeoutStopSec=60
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
ReadWritePaths=/opt/solax
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
```

```bash
systemctl daemon-reload && systemctl enable --now solax
journalctl -u solax -f
```

To update: run `dotnet publish` and the first `rsync` again, then `systemctl restart solax`. `--delete` removes everything else in `/opt/solax/app/`, so keep the state files (`*_FILE` paths) outside that directory.

### Switch from the Python version

The C# server reads the same `.env` and the same state files (`SOLCAST_CACHE_FILE`, `DECISIONS_FILE`, `SETTINGS_FILE`). The two versions must never run at the same time with the real SolaX credentials.

**Step 1. Copy the new build next to the old server.** The Python server keeps running.

```bash
# In the container: the build below is for x86_64. For aarch64 publish with -r linux-arm64.
uname -m
apt-get install -y ca-certificates tzdata

# On the dev machine
dotnet publish src/SolaxCloudMcp -c Release -r linux-x64 --self-contained -o publish
rsync -a --delete publish/ root@<lxc-ip>:/opt/solax/app-dotnet/
```

**Step 2. Preflight in the container.** This run uses dummy SolaX credentials, another port and no automation. It never asks SolaX for a token, so it does not disturb the running server. It proves that the binary starts on this machine and that HTTPS and the time zone data work.

```bash
chown -R solax:solax /opt/solax/app-dotnet
runuser -u solax -- env -i SOLAX_CLIENT_ID=dummy SOLAX_CLIENT_SECRET=dummy TRANSPORT=http \
  HTTP_API_KEY=preflight HTTP_PORT=8001 /opt/solax/app-dotnet/SolaxCloudMcp &
sleep 5
curl -s http://127.0.0.1:8001/health                                      # {"status":"ok"}
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8001/dashboard  # 200
curl -s -H 'Authorization: Bearer preflight' 'http://127.0.0.1:8001/api/prices?hours=2'  # RCE slots from PSE
kill %1
```

Stop here if one of the three checks fails.

**Step 3. Check where the state files are.** They must not be inside `/opt/solax/app/`, because step 4 moves that directory. If one is, move the file to `/opt/solax/` and change the path in `.env` after step 4. Without `SETTINGS_FILE` the export switch goes back to "on".

```bash
grep -E '^(SOLCAST_CACHE_FILE|DECISIONS_FILE|SETTINGS_FILE)=' /opt/solax/app/.env
```

**Step 4. Swap.** Stop the Python server, keep it for a rollback, start the C# server in dry run.

```bash
systemctl stop solax
cp -p /etc/systemd/system/solax.service /opt/solax/solax.service.python
mv /opt/solax/app /opt/solax/app-python && mv /opt/solax/app-dotnet /opt/solax/app
cp -p /opt/solax/app-python/.env /opt/solax/app/.env
# In /opt/solax/app/.env set AUTOMATION_DRY_RUN=1 and EXPORT_DRY_RUN=1 for the first run.
# In /etc/systemd/system/solax.service set ExecStart=/opt/solax/app/SolaxCloudMcp
chown -R solax:solax /opt/solax && systemctl daemon-reload && systemctl start solax
journalctl -u solax -f
```

**Step 5. Check, then go live.**

1. Open the dashboard. Check `GET /api/automation`, `POST /api/automation/preview` and `POST /api/export/preview`: the plans must look like the ones from before the switch.
2. Send one harmless command to prove that commands reach the inverter: `curl -X POST -H "Authorization: Bearer $HTTP_API_KEY" http://127.0.0.1:8000/api/battery/export-stop` must answer `{"stopped":true}`. It only tells the inverter to leave remote control.
3. Set the two dry-run variables back to their old values and run `systemctl restart solax`.

The dashboard is now a Blazor Server page. A reverse proxy or tunnel in front of it must pass WebSocket connections (Cloudflare Tunnel does). Add `SYNCFUSION_LICENSE_KEY=...` to `.env` to remove the Syncfusion licence banner.

**Rollback:** `systemctl stop solax`, then `mv /opt/solax/app /opt/solax/app-dotnet && mv /opt/solax/app-python /opt/solax/app`, copy `/opt/solax/solax.service.python` back to `/etc/systemd/system/solax.service`, then `systemctl daemon-reload && systemctl start solax`.

## Quick Start: HTTP Server on Raspberry Pi

### Prerequisites

- Raspberry Pi 3/4+ running 64-bit OS (Pi OS Lite recommended)
- Docker & Docker Compose installed
- SolaX Developer Platform OAuth2 credentials
- Your inverter's device serial number

### Installation Steps

#### 1. Install Docker & Docker Compose

```bash
# Install Docker
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh

# Add your user to docker group (optional, avoids sudo)
sudo usermod -aG docker $USER
newgrp docker

# Install Docker Compose
sudo curl -L "https://github.com/docker/compose/releases/latest/download/docker-compose-$(uname -s)-$(uname -m)" -o /usr/local/bin/docker-compose
sudo chmod +x /usr/local/bin/docker-compose
```

#### 2. Clone or Deploy the Repository

```bash
git clone <repo-url> solax-cloud-mcp
cd solax-cloud-mcp
```

#### 3. Configure Environment Variables

```bash
# Copy the example environment file
cp .env.example .env

# Edit with your credentials
nano .env
```

Fill in the following (at minimum):

```env
# SolaX OAuth2 credentials from https://developer.solaxcloud.com/
SOLAX_CLIENT_ID=your_client_id
SOLAX_CLIENT_SECRET=your_client_secret
SOLAX_DEVICE_SN=your_device_sn

# Generate a strong API key for HTTP authentication
# openssl rand -base64 32
HTTP_API_KEY=your_random_api_key
```

#### 4. Start the HTTP Server

```bash
# Build and start the container
docker-compose up -d

# Check logs
docker-compose logs -f solax-http

# Verify it's running
curl -H "Authorization: Bearer YOUR_API_KEY" http://localhost:8000/health
```

The server will be accessible at `http://YOUR_PI_IP:8000`

#### Docker Compose Configuration Reference

The service is defined in [docker-compose.yml](docker-compose.yml). It publishes port 8000 on `${HTTP_BIND_ADDR:-127.0.0.1}`, runs with `restart: unless-stopped` and has a health check on `/health`.

**Only the variables listed under `environment:` in `docker-compose.yml` reach the container.** It does not pass `EXPORT_DRY_RUN`, `AUTOMATION_CHARGE_POWER_KW`, `TARIFF_OFFPEAK_WINDOWS_SUMMER`, `SOLCAST_CACHE_FILE`, `DECISIONS_FILE`, `SETTINGS_FILE` or `SOLAX_ALLOW_WRITE`. Add a line there for each one you set in `.env`. The compose file has no volume, so the `*_FILE` paths must point into a volume you add (e.g. `./data:/data`), or the files are lost when the container is recreated. Also note that compose always sets `TARIFF_OFFPEAK_WINDOWS`, so without `TARIFF_OFFPEAK_WINDOWS_SUMMER` the winter windows apply all year.

**Key Configuration Options:**

| Setting | Purpose | Example |
|---------|---------|---------|
| `SOLAX_CLIENT_ID` | OAuth2 Client ID from SolaX | `abc123xyz` |
| `SOLAX_CLIENT_SECRET` | OAuth2 Client Secret from SolaX | `secret123xyz` |
| `SOLAX_DEVICE_SN` | Inverter serial number (default device) | `X3ABCD0123` |
| `HTTP_API_KEY` | Bearer token for API authentication | `YWJjMTIzaG...` |
| `HTTP_HOST` | Interface the server binds to (default `127.0.0.1`; the Docker image sets `0.0.0.0`) | `0.0.0.0` (all), `127.0.0.1` (localhost) |
| `HTTP_BIND_ADDR` | docker-compose: host address the port is published on (default `127.0.0.1`) | `192.168.1.10` |
| `SOLCAST_API_KEY` | Solcast API key (optional, enables forecast) | `abc123...` |
| `SOLCAST_RESOURCE_IDS` | Comma-separated Solcast rooftop site IDs | `abcd-1234-ef56-7890` |
| `SOLCAST_CACHE_MINUTES` | Forecast cache TTL (hobbyist: ~10 calls/day) | `180` |
| `SOLAR_TIMEZONE` | IANA timezone for forecast days/timestamps | `Europe/Warsaw` |
| `AUTOMATION_ENABLED` | Run forecast-driven battery planner in the HTTP server | `1` |
| `AUTOMATION_DRY_RUN` | Log decisions only, do not write to inverter (default `1`) | `0` |
| `BATTERY_CAPACITY_KWH` | Usable battery capacity | `21.2` |
| `AUTOMATION_CHARGE_POWER_KW` | Grid charge power in off-peak windows (default `5`) | `5` |
| `AUTOMATION_DAILY_CONSUMPTION_KWH` | Daily consumption (flat hourly profile) | `12` |
| `TARIFF_OFFPEAK_WINDOWS` | Daily off-peak windows (full hours), winter or all year | `22:00-06:00,13:00-15:00` |
| `TARIFF_OFFPEAK_WINDOWS_SUMMER` | Off-peak windows 1 Apr - 30 Sep | `22:00-06:00,15:00-17:00` |
| `EXPORT_ENABLED` | Run the export planner (needs `AUTOMATION_ENABLED=1`) | `1` |
| `EXPORT_DRY_RUN` | Log export decisions only (default `1`) | `0` |
| `EXPORT_MAX_POWER_KW` | Required with export: max inverter AC setpoint | `5` |
| `EXPORT_MIN_PRICE_PLN_KWH` | Minimum net RCE price for export (default `1.0`) | `1.0` |
| `EXPORT_PRICE_MULTIPLIER` | Multiplier for deposit value and revenue in the plan | `1.23` |
| `SOLCAST_CACHE_FILE` | Keep the Solcast cache across restarts | `/opt/solax/solcast-cache.json` |
| `DECISIONS_FILE` | Keep the decision history across restarts | `/opt/solax/decisions.jsonl` |
| `SETTINGS_FILE` | Keep the dashboard export switch across restarts | `/opt/solax/settings.json` |
| `SYNCFUSION_LICENSE_KEY` | Syncfusion Blazor licence key for the dashboard (optional; a licence banner shows without it) | `Ngo9BigB...` |
| `SOLAX_ALLOW_WRITE` | MCP mode: expose `set_battery_self_use_mode` tool to the LLM (default off) | `1` |
| `HTTP_PORT` | Port number | `8000` |
| `TRANSPORT` | Execution mode | `http` or `stdio` |

**Health Check Explanation:**

The health check runs every 30 seconds and:
- Runs `SolaxCloudMcp healthcheck`, which requests `http://127.0.0.1:8000/health` (there is no `curl` in the image)
- Expects a 200 HTTP response
- Waits up to 10 seconds for a response
- After 3 consecutive failures, marks the container as unhealthy (but doesn't stop it)
- Waits 5 seconds after container start before first check (startup grace period)

### HTTP API Endpoints

All endpoints except `/health` and `/dashboard` need `Authorization: Bearer YOUR_API_KEY`. See [HTTP_API.md](HTTP_API.md) for the full list with examples.

```bash
curl http://SERVER_IP:8000/health
curl -X POST -H "Authorization: Bearer YOUR_API_KEY" -H "Content-Type: application/json" -d '{}' http://SERVER_IP:8000/api/realtime-data
```

### Management Commands

```bash
# View logs
docker-compose logs -f solax-http

# Restart the server
docker-compose restart solax-http

# Stop the server
docker-compose down

# Rebuild the image (after code changes)
docker-compose up -d --build

# View resource usage
docker stats solax-http
```

## Advanced: Using MCP Mode in Docker

To run the MCP server in Docker instead (for Claude Desktop):

Edit `docker-compose.yml`:

```yaml
services:
  # Comment out or remove the solax-http service

  # Uncomment the solax-mcp service:
  solax-mcp:
    build: .
    environment:
      SOLAX_CLIENT_ID: ${SOLAX_CLIENT_ID}
      SOLAX_CLIENT_SECRET: ${SOLAX_CLIENT_SECRET}
      SOLAX_DEVICE_SN: ${SOLAX_DEVICE_SN}
      TRANSPORT: stdio
    stdin_open: true
    tty: true
```

Then configure Claude to use the Docker container as the MCP server. The exact configuration depends on your Claude client (Desktop, Code, etc.).

## Security Considerations

### Secrets Management

- **Never commit `.env` to version control** — it's excluded by `.gitignore` and `.dockerignore`
- Store `HTTP_API_KEY` in your `.env` file (loaded at runtime, not baked into image)
- Never pass secrets as command-line arguments or hardcode them

### Network Security

- Run directly, the HTTP server binds to `127.0.0.1` by default; set `HTTP_HOST=0.0.0.0` to listen on all interfaces
- docker-compose publishes the port on `127.0.0.1` by default; set `HTTP_BIND_ADDR` to the Pi's LAN IP to reach it from other machines
- Traffic is plain HTTP: the API key travels in cleartext on the LAN. Keep `HTTP_API_KEY` strong and prefer a TLS reverse proxy
- The container runs as the unprivileged `app` user of the .NET image
- `/docs`, `/redoc` and `/openapi.json` are disabled (no unauthenticated schema exposure)
- For public exposure, use a reverse proxy (nginx/Caddy) with HTTPS and proper firewall rules
- The API key is a bearer token in the `Authorization: Bearer <token>` header

### Rate Limiting

- The SolaX API allows 100 calls/minute per token
- The client enforces a 0.7-second minimum spacing between calls
- The HTTP server does NOT add additional rate limiting (assumes single consumer)
- If you're building a multi-user application, add rate limiting (e.g. the ASP.NET Core rate limiter middleware)

### Inverter Control

- `set_battery_self_use_mode`, `export-test`, `export-stop`, `export/active` and both planners write to the inverter. Only give the API key to trusted clients.
- The planners start in dry run (`AUTOMATION_DRY_RUN=1`, `EXPORT_DRY_RUN=1`). Watch `GET /api/automation`, the dashboard and the logs for a few days before you set them to `0`.
- Kill switch for export: the dashboard button (`POST /api/export/active {"active": false}`), or `EXPORT_DRY_RUN=1` and a restart.
- On stop, the server exits SolaX remote control. If it crashes mid-command, the inverter still stops at the target SOC.
- Run only one server instance per SolaX client ID. Each new access token invalidates the previous one.
- Turn off SolaX Cloud app rules that also charge or discharge, so they do not fight the server.
- Every decision is logged; set `DECISIONS_FILE` to keep the history.

## Troubleshooting

### Container won't start

```bash
# Check logs
docker-compose logs solax-http

# Verify environment variables
docker-compose config

# Rebuild the image
docker-compose up -d --build
```

### API key authentication fails

```bash
# Verify your API key is set in .env
grep HTTP_API_KEY .env

# Check the Authorization header format
curl -v -H "Authorization: Bearer YOUR_KEY" http://localhost:8000/health
```

### Inverter data not returned

- Check `SOLAX_CLIENT_ID` and `SOLAX_CLIENT_SECRET` are correct
- Verify `SOLAX_DEVICE_SN` is your device's SN (not WiFi dongle SN)
- Confirm your SolaX account has API permissions
- Check network connectivity to `openapi-eu.solaxcloud.com`

### High resource usage on Pi

- Reduce request frequency
- Monitor with `docker stats solax-http`
- Increase swap if available: `sudo dphys-swapfile swapon`
- Consider disabling debug logging in production

## Monitoring & Logging

### Health Check

```bash
# Monitor health endpoint
while true; do
  curl -s http://SERVER_IP:8000/health | jq .
  sleep 30
done
```

### Collect Logs

```bash
# Real-time logs
docker-compose logs -f solax-http

# Last 100 lines
docker-compose logs --tail=100 solax-http

# Export logs to file
docker-compose logs solax-http > server.log
```

## Production Checklist

- [ ] Secrets in `.env` (not hardcoded, not in git)
- [ ] Strong `HTTP_API_KEY` generated (min 32 characters)
- [ ] Firewall rules: only allow Pi's IP range
- [ ] Restart policy: `unless-stopped` (survives reboots)
- [ ] Health checks enabled in docker-compose
- [ ] Logging reviewed and rotated
- [ ] Network latency acceptable (test from your automation client)
- [ ] Rate limiting understood and monitored
- [ ] Regular backups of important configuration
- [ ] Only one server instance runs for the SolaX client ID
- [ ] `SOLCAST_CACHE_FILE`, `DECISIONS_FILE` and `SETTINGS_FILE` point to a writable path
- [ ] Planners checked in dry run before `AUTOMATION_DRY_RUN=0` / `EXPORT_DRY_RUN=0`

## Migration: MCP → HTTP or Vice Versa

### From MCP to HTTP

1. Set `TRANSPORT=http` in `.env`
2. Update your clients to use HTTP API instead of MCP protocol
3. Rebuild and restart: `docker-compose up -d --build`

### From HTTP to MCP

1. Set `TRANSPORT=stdio` in `.env` (or remove the env var to use default)
2. Configure your Claude client to use the server
3. Rebuild: `docker-compose up -d --build`

Both modes use the same code. The planners and the dashboard run only in HTTP mode.

---

For more information, see [README.md](README.md) for MCP usage and [HTTP_API.md](HTTP_API.md) for the HTTP API.
