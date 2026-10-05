"""FastAPI HTTP server for SolaX Developer Platform API."""

import logging
import os
import secrets
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, Query, status
from pydantic import BaseModel, Field

from .client import SolaxApiError
from .automation import AutomationScheduler, ExportSettings, Settings
from .config import get_default_device_sn, is_automation_enabled, is_export_enabled, is_solcast_configured
from .prices import PriceError
from .forecast import SolcastError
from .server import (
    HHMM_PATTERN,
    get_energy_prices_impl,
    get_realtime_data_impl,
    get_solar_forecast_impl,
    set_battery_self_use_mode_impl,
)

logger = logging.getLogger(__name__)


def get_api_key() -> str:
    """Get HTTP API key from environment, raise if not set."""
    key = os.getenv("HTTP_API_KEY")
    if not key:
        raise RuntimeError("HTTP_API_KEY environment variable not set")
    return key


def _create_scheduler() -> AutomationScheduler | None:
    """Validate automation config at startup (fail fast) and build the scheduler if enabled."""
    if not is_automation_enabled():
        if is_export_enabled():
            raise RuntimeError("EXPORT_ENABLED=1 requires AUTOMATION_ENABLED=1")
        return None
    if not is_solcast_configured():
        raise RuntimeError("AUTOMATION_ENABLED=1 requires SOLCAST_API_KEY and SOLCAST_RESOURCE_IDS")
    if not get_default_device_sn():
        raise RuntimeError("AUTOMATION_ENABLED=1 requires SOLAX_DEVICE_SN")
    export = ExportSettings.from_env() if is_export_enabled() else None
    return AutomationScheduler(Settings.from_env(), export)


def create_app() -> FastAPI:
    """Create and configure FastAPI application."""
    scheduler = _create_scheduler()

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        if scheduler:
            scheduler.start()
            logger.info("Battery automation started (dry_run=%s)", scheduler.settings.dry_run)
        yield
        if scheduler:
            await scheduler.stop()

    app = FastAPI(
        lifespan=lifespan,
        title="SolaX Cloud API",
        description="HTTP API for SolaX solar inverter data and control",
        version="0.1.0",
        # No unauthenticated API schema/docs exposure
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )
    api_key = get_api_key().encode()

    async def verify_api_key(authorization: Annotated[str, Header()] = "") -> None:
        """Verify bearer token matches HTTP_API_KEY."""
        scheme, _, credentials = authorization.partition(" ")
        if scheme.lower() != "bearer":
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="Invalid authorization header format. Use: Bearer <token>",
            )
        if not secrets.compare_digest(credentials.encode(), api_key):
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="Invalid API key",
            )

    # Request/Response models
    class RealtimeDataRequest(BaseModel):
        device_sn: str | None = Field(
            None,
            description="Inverter device serial number. If omitted, uses SOLAX_DEVICE_SN env var.",
        )

    class SetBatterySelfUseModeRequest(BaseModel):
        device_sn: str | None = Field(
            None,
            description="Inverter device serial number. If omitted, uses SOLAX_DEVICE_SN env var.",
        )
        # Required: no silent defaults that would overwrite inverter settings
        min_soc: int = Field(..., ge=10, le=100, description="Minimum SOC (%)")
        charge_upper_soc: int = Field(..., ge=10, le=100, description="Maximum charging SOC (%)")
        charge_from_grid_enable: int = Field(..., ge=0, le=1, description="Allow grid charging (0=no, 1=yes)")
        charge_start_time_period1: str | None = Field(None, pattern=HHMM_PATTERN, description="Start time (HH:MM format, e.g. 06:00)")
        charge_end_time_period1: str | None = Field(None, pattern=HHMM_PATTERN, description="End time (HH:MM format, e.g. 18:00)")
        discharge_start_time_period1: str | None = Field(None, pattern=HHMM_PATTERN, description="Start time (HH:MM format)")
        discharge_end_time_period1: str | None = Field(None, pattern=HHMM_PATTERN, description="End time (HH:MM format)")
        enable_time_period2: int = Field(0, ge=0, le=1, description="Enable second time period (0=no, 1=yes)")
        charge_start_time_period2: str | None = Field(None, pattern=HHMM_PATTERN, description="Start time (HH:MM format)")
        charge_end_time_period2: str | None = Field(None, pattern=HHMM_PATTERN, description="End time (HH:MM format)")
        discharge_start_time_period2: str | None = Field(None, pattern=HHMM_PATTERN, description="Start time (HH:MM format)")
        discharge_end_time_period2: str | None = Field(None, pattern=HHMM_PATTERN, description="End time (HH:MM format)")

    def to_http_error(e: ValueError) -> HTTPException:
        """Map impl errors: validation -> 400 with detail; upstream SolaX failure -> 502, details only in logs."""
        if isinstance(e.__cause__, SolaxApiError):
            logger.error("SolaX API call failed: %s", e)
            return HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="SolaX API request failed")
        if isinstance(e.__cause__, PriceError):
            logger.error("PSE price API call failed: %s", e)
            return HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="PSE price API request failed")
        if isinstance(e.__cause__, SolcastError):
            logger.error("Solcast API call failed: %s", e)
            return HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Solcast API request failed")
        return HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=str(e))

    # Routes
    @app.get("/health")
    async def health_check() -> dict:
        """Health check endpoint (no auth required)."""
        return {"status": "ok"}

    # Auth as a dependency: runs before body validation, so unauthenticated callers get 403, not 422 schema details
    @app.post("/api/realtime-data", dependencies=[Depends(verify_api_key)])
    async def get_realtime_data_endpoint(req: RealtimeDataRequest) -> dict:
        """Fetch real-time solar inverter data.

        Requires bearer token in Authorization header.
        """
        try:
            return await get_realtime_data_impl(req.device_sn)
        except ValueError as e:
            raise to_http_error(e) from e

    @app.get("/api/solar-forecast", dependencies=[Depends(verify_api_key)])
    async def get_solar_forecast_endpoint(
        hours: Annotated[int, Query(ge=1, le=168, description="Hours of hourly profile")] = 24,
    ) -> dict:
        """PV production forecast from Solcast (cached). Requires bearer token."""
        if not is_solcast_configured():
            raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="Solcast is not configured")
        try:
            return await get_solar_forecast_impl(hours)
        except ValueError as e:
            raise to_http_error(e) from e

    @app.get("/api/prices", dependencies=[Depends(verify_api_key)])
    async def get_prices_endpoint(
        hours: Annotated[int, Query(ge=1, le=48, description="Hours ahead")] = 24,
    ) -> dict:
        """RCE market prices (15-min, PLN/kWh net) from PSE. Requires bearer token."""
        try:
            return await get_energy_prices_impl(hours)
        except ValueError as e:
            raise to_http_error(e) from e

    @app.post("/api/export/preview", dependencies=[Depends(verify_api_key)])
    async def preview_export() -> dict:
        """Export plan from current SOC, PV forecast and RCE prices. Never writes to the inverter."""
        if not scheduler or not scheduler.export:
            raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="Export planner is not enabled")
        try:
            return await scheduler.export_plan()
        except (SolcastError, PriceError, SolaxApiError) as e:
            logger.error("Export preview failed: %s", e)
            raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Upstream API request failed") from e
        except ValueError as e:
            raise to_http_error(e) from e

    @app.get("/api/automation", dependencies=[Depends(verify_api_key)])
    async def get_automation_status() -> dict:
        """Battery automation status: dry-run flag, next planned run and last result. Requires bearer token."""
        return scheduler.status() if scheduler else {"enabled": False}

    @app.post("/api/automation/preview", dependencies=[Depends(verify_api_key)])
    async def preview_automation() -> dict:
        """Compute the plan for the next off-peak window without writing to the inverter."""
        if not scheduler:
            raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="Automation is not enabled")
        try:
            return await scheduler.preview()
        except SolcastError as e:
            logger.error("Solcast API call failed: %s", e)
            raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Solcast API request failed") from e

    @app.post("/api/battery/self-use-mode", dependencies=[Depends(verify_api_key)])
    async def set_battery_self_use_mode_endpoint(req: SetBatterySelfUseModeRequest) -> dict:
        """Set inverter battery to Self Use Mode with configurable thresholds.

        Requires bearer token in Authorization header.
        """
        try:
            return await set_battery_self_use_mode_impl(
                device_sn=req.device_sn,
                min_soc=req.min_soc,
                charge_upper_soc=req.charge_upper_soc,
                charge_from_grid_enable=req.charge_from_grid_enable,
                charge_start_time_period1=req.charge_start_time_period1,
                charge_end_time_period1=req.charge_end_time_period1,
                discharge_start_time_period1=req.discharge_start_time_period1,
                discharge_end_time_period1=req.discharge_end_time_period1,
                enable_time_period2=req.enable_time_period2,
                charge_start_time_period2=req.charge_start_time_period2,
                charge_end_time_period2=req.charge_end_time_period2,
                discharge_start_time_period2=req.discharge_start_time_period2,
                discharge_end_time_period2=req.discharge_end_time_period2,
            )
        except ValueError as e:
            raise to_http_error(e) from e

    return app


def main() -> None:
    """Run the HTTP server."""
    import uvicorn

    app = create_app()
    port = int(os.getenv("HTTP_PORT", "8000"))
    # Loopback by default; set HTTP_HOST=0.0.0.0 explicitly to listen on all interfaces (e.g. in Docker)
    host = os.getenv("HTTP_HOST", "127.0.0.1")
    uvicorn.run(app, host=host, port=port)


if __name__ == "__main__":
    main()
