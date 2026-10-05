# Build stage
FROM python:3.11-slim AS builder

WORKDIR /build

# Install uv from its pinned official image (no curl | sh)
COPY --from=ghcr.io/astral-sh/uv:0.8 /uv /bin/uv

# Copy project files (excluding secrets via .dockerignore)
COPY pyproject.toml uv.lock README.md ./
COPY src/ ./src/

# Install exactly the versions pinned in uv.lock (fails if lockfile is out of date)
ENV UV_PROJECT_ENVIRONMENT=/app/.venv \
    UV_PYTHON_DOWNLOADS=never \
    UV_LINK_MODE=copy
RUN uv sync --frozen --no-dev --no-editable

# Runtime stage - ARM64 optimized for Raspberry Pi 3
FROM python:3.11-slim

WORKDIR /app

# Base image already ships ca-certificates; httpx uses certifi. Only add an unprivileged user.
RUN useradd --system --uid 10001 --no-create-home app

# Copy virtual environment (with the package installed non-editable)
COPY --from=builder /app/.venv /app/.venv

ENV PATH="/app/.venv/bin:$PATH"
ENV PYTHONUNBUFFERED=1
# Inside the container listen on all interfaces; exposure is controlled by the port mapping
ENV HTTP_HOST=0.0.0.0

USER app

# Health check for HTTP mode
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8000/health').read()" || exit 1

# Default to HTTP mode for Pi deployment; override TRANSPORT env var to use MCP mode
ENV TRANSPORT=http

EXPOSE 8000

# Run the MCP/HTTP server
CMD ["python", "-m", "solax_cloud_mcp"]
