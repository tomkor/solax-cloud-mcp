# Deployment Guide

This MCP server can be deployed in two modes:

1. **MCP Mode (stdio)**: Default for Claude integration
2. **HTTP Mode**: Lightweight containerized server for Raspberry Pi

## Proxmox LXC with systemd (no Docker)

The production instance runs this way: a Debian 13 LXC (1 vCPU, 512 MB RAM, 4 GB disk, unprivileged, start on boot), the app in `/opt/solax/app` owned by a `solax` system user, dependencies installed with `uv`.

**Run only one instance.** Each new SolaX access token invalidates the previous one, and two servers would also fight over the inverter.

```bash
# In the container (as root)
apt-get install -y curl ca-certificates rsync
useradd --system --create-home --home-dir /opt/solax --shell /usr/sbin/nologin solax
curl -LsSf https://astral.sh/uv/install.sh | env UV_INSTALL_DIR=/usr/local/bin UV_NO_MODIFY_PATH=1 sh

# From the dev machine: copy tracked files and .env (set HTTP_HOST=0.0.0.0 in it)
git ls-files | rsync -a --files-from=- ./ root@<lxc-ip>:/opt/solax/app/
rsync -a .env root@<lxc-ip>:/opt/solax/app/.env

# In the container
chmod 600 /opt/solax/app/.env && chown -R solax:solax /opt/solax
runuser -u solax -- env UV_CACHE_DIR=/opt/solax/.cache/uv sh -c 'cd /opt/solax/app && uv sync --frozen --no-dev'
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
ExecStart=/opt/solax/app/.venv/bin/python -m solax_cloud_mcp
Restart=on-failure
RestartSec=10
# On stop the server exits SolaX remote control before quitting
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

To update: repeat the `rsync` of tracked files, run `uv sync --frozen --no-dev` again as `solax`, then `systemctl restart solax`.

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
# Linux: openssl rand -base64 32
# Python: python -c "import secrets; print(secrets.token_urlsafe(32))"
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

Here's the complete `docker-compose.yml` configuration:

```yaml
version: '3.8'

services:
  solax-http:
    # Build the image from the project Dockerfile
    build:
      context: .
      dockerfile: Dockerfile
      # Optional: for Raspberry Pi 64-bit
      args:
        BUILDKIT_INLINE_CACHE: 1
    
    # Container name for easy reference
    container_name: solax-cloud-http
    
    # Port mapping: localhost only by default; set HTTP_BIND_ADDR to the Pi's LAN IP to expose it
    ports:
      - "${HTTP_BIND_ADDR:-127.0.0.1}:8000:8000"
    
    # Environment variables (loaded from .env file)
    environment:
      # SolaX API credentials
      SOLAX_CLIENT_ID: ${SOLAX_CLIENT_ID}
      SOLAX_CLIENT_SECRET: ${SOLAX_CLIENT_SECRET}
      SOLAX_DEVICE_SN: ${SOLAX_DEVICE_SN}
      
      # HTTP server configuration
      TRANSPORT: http                    # Use HTTP mode (not MCP/stdio)
      HTTP_HOST: 0.0.0.0                # All interfaces inside the container; host exposure via ports
      HTTP_PORT: 8000                   # Port number
      HTTP_API_KEY: ${HTTP_API_KEY}     # Bearer token for authentication
      # Solcast PV forecast (optional)
      SOLCAST_API_KEY: ${SOLCAST_API_KEY:-}
      SOLCAST_RESOURCE_IDS: ${SOLCAST_RESOURCE_IDS:-}
      SOLCAST_CACHE_MINUTES: ${SOLCAST_CACHE_MINUTES:-180}
      SOLAR_TIMEZONE: ${SOLAR_TIMEZONE:-UTC}
    
    # Restart policy: restart unless manually stopped
    restart: unless-stopped
    
    # Health check: verify the server is running
    healthcheck:
      test: ["CMD", "python", "-c", "import urllib.request; urllib.request.urlopen('http://localhost:8000/health')"]
      interval: 30s           # Check every 30 seconds
      timeout: 10s            # Wait up to 10 seconds for response
      retries: 3              # Mark unhealthy after 3 failed checks
      start_period: 5s        # Give container 5s to start before first check

# Optional: Uncomment below to run MCP mode instead of HTTP mode
# (Not recommended for Raspberry Pi; HTTP is lighter-weight)
#
#  solax-mcp:
#    build:
#      context: .
#      dockerfile: Dockerfile
#    container_name: solax-cloud-mcp
#    environment:
#      SOLAX_CLIENT_ID: ${SOLAX_CLIENT_ID}
#      SOLAX_CLIENT_SECRET: ${SOLAX_CLIENT_SECRET}
#      SOLAX_DEVICE_SN: ${SOLAX_DEVICE_SN}
#      TRANSPORT: stdio        # Use MCP mode (stdio-based)
#    stdin_open: true
#    tty: true
#    restart: unless-stopped
```

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
| `AUTOMATION_DAILY_CONSUMPTION_KWH` | Daily consumption (flat hourly profile) | `12` |
| `TARIFF_OFFPEAK_WINDOWS` | Daily off-peak windows (full hours) | `22:00-06:00,13:00-15:00` |
| `SOLAX_ALLOW_WRITE` | MCP mode: expose `set_battery_self_use_mode` tool to the LLM (default off) | `1` |
| `HTTP_PORT` | Port number | `8000` |
| `TRANSPORT` | Execution mode | `http` or `stdio` |

**Health Check Explanation:**

The health check runs every 30 seconds and:
- Requests `http://localhost:8000/health` with Python's `urllib` (no `curl` in the image)
- Expects a 200 HTTP response
- Waits up to 10 seconds for a response
- After 3 consecutive failures, marks the container as unhealthy (but doesn't stop it)
- Waits 5 seconds after container start before first check (startup grace period)

### HTTP API Endpoints

All endpoints (except `/health`) require a bearer token:

```bash
curl -H "Authorization: Bearer YOUR_API_KEY" http://pi-ip:8000/...
```

#### Health Check (no auth required)

```bash
curl http://pi-ip:8000/health
```

Response:
```json
{"status": "ok"}
```

#### Get Real-Time Data

```bash
curl -X POST \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"device_sn": "X3ABCD0123"}' \
  http://pi-ip:8000/api/realtime-data
```

If `device_sn` is omitted, uses `SOLAX_DEVICE_SN` from environment.

Response: Complete inverter data (same as MCP tool)

#### Set Battery Self-Use Mode

```bash
curl -X POST \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "device_sn": "X3ABCD0123",
    "min_soc": 20,
    "charge_upper_soc": 80,
    "charge_from_grid_enable": 1,
    "charge_start_time_period1": "06:00",
    "charge_end_time_period1": "18:00",
    "discharge_start_time_period1": "18:00",
    "discharge_end_time_period1": "06:00"
  }' \
  http://pi-ip:8000/api/battery/self-use-mode
```

Response: API confirmation from inverter

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
- The container runs as an unprivileged user (`uid 10001`)
- `/docs`, `/redoc` and `/openapi.json` are disabled (no unauthenticated schema exposure)
- For public exposure, use a reverse proxy (nginx/Caddy) with HTTPS and proper firewall rules
- The API key is a bearer token in the `Authorization: Bearer <token>` header

### Rate Limiting

- The SolaX API allows 100 calls/minute per token
- The client enforces a 0.7-second minimum spacing between calls
- The HTTP server does NOT add additional rate limiting (assumes single consumer)
- If you're building a multi-user application, add rate limiting (e.g., `slowapi`)

### Inverter Control

- `set_battery_self_use_mode` is a mutating operation — it changes inverter settings
- Only grant access to trusted clients with the API key
- Consider implementing audit logging if multiple users have access

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
  curl -s http://pi-ip:8000/health | jq .
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

## Migration: MCP → HTTP or Vice Versa

### From MCP to HTTP

1. Set `TRANSPORT=http` in `.env`
2. Update your clients to use HTTP API instead of MCP protocol
3. Rebuild and restart: `docker-compose up -d --build`

### From HTTP to MCP

1. Set `TRANSPORT=stdio` in `.env` (or remove the env var to use default)
2. Configure your Claude client to use the server
3. Rebuild: `docker-compose up -d --build`

Both modes use the same underlying client library, so behavior is identical.

---

For more information, see [README.md](README.md) for MCP usage and API details.
