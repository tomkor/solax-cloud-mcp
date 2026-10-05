"""MCP server for SolaX Developer Platform real-time data and control."""

import re

from mcp.server.fastmcp import FastMCP

from .client import SolaxApiError, fetch_realtime_data, set_self_use_mode
from .config import get_client_id, get_client_secret, get_default_device_sn, is_write_enabled
from .models import shape_realtime_response

# Validate configuration at import time
CLIENT_ID = get_client_id()
CLIENT_SECRET = get_client_secret()

# Initialize the MCP server
server = FastMCP("solax-cloud")

HHMM_PATTERN = r"^([01]\d|2[0-3]):[0-5]\d$"
_HHMM_RE = re.compile(HHMM_PATTERN)


def _validate_hhmm(name: str, value: str | None) -> None:
    if value is not None and not _HHMM_RE.fullmatch(value):
        raise ValueError(f"{name} must be in HH:MM format (00:00-23:59), got {value!r}")


async def get_realtime_data_impl(device_sn: str | None = None) -> dict:
    """Core implementation of get_realtime_data, shared by MCP and HTTP modes."""
    effective_sn = device_sn or get_default_device_sn()
    if not effective_sn:
        raise ValueError(
            "No device_sn provided: either pass device_sn argument to this tool or set SOLAX_DEVICE_SN environment variable"
        )

    try:
        inverter_raw, battery_raw = await fetch_realtime_data(effective_sn)
    except SolaxApiError as e:
        raise ValueError(f"SolaX API error: {e}") from e

    shaped = shape_realtime_response(inverter_raw, battery_raw)
    return shaped


async def set_battery_self_use_mode_impl(
    *,
    min_soc: int,
    charge_upper_soc: int,
    charge_from_grid_enable: int,
    device_sn: str | None = None,
    charge_start_time_period1: str | None = None,
    charge_end_time_period1: str | None = None,
    discharge_start_time_period1: str | None = None,
    discharge_end_time_period1: str | None = None,
    enable_time_period2: int = 0,
    charge_start_time_period2: str | None = None,
    charge_end_time_period2: str | None = None,
    discharge_start_time_period2: str | None = None,
    discharge_end_time_period2: str | None = None,
) -> dict:
    """Core implementation of set_battery_self_use_mode, shared by MCP and HTTP modes."""
    effective_sn = device_sn or get_default_device_sn()
    if not effective_sn:
        raise ValueError(
            "No device_sn provided: either pass device_sn argument or set SOLAX_DEVICE_SN environment variable"
        )

    if not (10 <= min_soc <= 100):
        raise ValueError(f"min_soc must be between 10 and 100, got {min_soc}")
    if not (10 <= charge_upper_soc <= 100):
        raise ValueError(f"charge_upper_soc must be between 10 and 100, got {charge_upper_soc}")
    if min_soc > charge_upper_soc:
        raise ValueError(
            f"min_soc ({min_soc}) cannot be greater than charge_upper_soc ({charge_upper_soc})"
        )
    if charge_from_grid_enable not in (0, 1):
        raise ValueError(f"charge_from_grid_enable must be 0 or 1, got {charge_from_grid_enable}")
    if enable_time_period2 not in (0, 1):
        raise ValueError(f"enable_time_period2 must be 0 or 1, got {enable_time_period2}")
    for name, value in (
        ("charge_start_time_period1", charge_start_time_period1),
        ("charge_end_time_period1", charge_end_time_period1),
        ("discharge_start_time_period1", discharge_start_time_period1),
        ("discharge_end_time_period1", discharge_end_time_period1),
        ("charge_start_time_period2", charge_start_time_period2),
        ("charge_end_time_period2", charge_end_time_period2),
        ("discharge_start_time_period2", discharge_start_time_period2),
        ("discharge_end_time_period2", discharge_end_time_period2),
    ):
        _validate_hhmm(name, value)

    try:
        response = await set_self_use_mode(
            device_sn=effective_sn,
            min_soc=min_soc,
            charge_upper_soc=charge_upper_soc,
            charge_from_grid_enable=charge_from_grid_enable,
            charge_start_time_period1=charge_start_time_period1,
            charge_end_time_period1=charge_end_time_period1,
            discharge_start_time_period1=discharge_start_time_period1,
            discharge_end_time_period1=discharge_end_time_period1,
            enable_time_period2=enable_time_period2,
            charge_start_time_period2=charge_start_time_period2,
            charge_end_time_period2=charge_end_time_period2,
            discharge_start_time_period2=discharge_start_time_period2,
            discharge_end_time_period2=discharge_end_time_period2,
        )
    except SolaxApiError as e:
        raise ValueError(f"SolaX API error: {e}") from e

    return response


@server.tool()
async def get_realtime_data(device_sn: str | None = None) -> dict:
    """Fetch real-time solar inverter data from SolaX Developer Platform.

    Retrieves the latest readings including power output, energy yields, battery status,
    and grid import/export data from the SolaX Developer Platform API. All power values
    are in Watts (W), energy in kWh, voltage in Volts (V), current in Amps (A),
    and temperature in Celsius (°C).

    Args:
        device_sn: Serial number of the inverter to query (the device SN, not WiFi dongle SN).
                   If omitted, defaults to SOLAX_DEVICE_SN environment variable.

    Returns:
        A structured dictionary with decoded status values and grouped data fields:
        - device: inverter identifiers (SN, registration number, timestamps)
        - status: inverter operating status (code + human-readable description)
        - pv: list of active PV strings with voltage/current/power
        - mppt: MPPT tracker data (individual trackers + total power)
        - ac: AC output phases, total power, reactive power, power factor, grid frequency
        - energy: daily/total yield and AC output
        - meter1: grid power and energy (import/export)
        - meter2: secondary meter data (import/export)
        - battery: battery voltage/current/power/soc/status (None if no battery)
        - eps: emergency power supply data (3-phase voltage/current/power)
        - temperature: inverter temperature
        - misc: line-to-line voltages

    Raises:
        ToolError: if credentials are invalid, the device is not found, or the API fails.
    """
    return await get_realtime_data_impl(device_sn)


async def set_battery_self_use_mode(
    min_soc: int,
    charge_upper_soc: int,
    charge_from_grid_enable: int,
    device_sn: str | None = None,
    charge_start_time_period1: str | None = None,
    charge_end_time_period1: str | None = None,
    discharge_start_time_period1: str | None = None,
    discharge_end_time_period1: str | None = None,
    enable_time_period2: int = 0,
    charge_start_time_period2: str | None = None,
    charge_end_time_period2: str | None = None,
    discharge_start_time_period2: str | None = None,
    discharge_end_time_period2: str | None = None,
) -> dict:
    """Set inverter battery to Self Use Mode with configurable charging thresholds.

    Self Use Mode is ideal for automated control based on conditions like weather.
    You can set minimum SOC (don't discharge below), maximum charge SOC (don't charge above),
    and optionally configure time-based charge/discharge periods (e.g., charge during peak solar,
    discharge during high-rate periods).

    Example automation scenarios:
    - Reduce charging threshold on cloudy days to preserve grid import reserves
    - Increase charging threshold on clear days to maximize solar self-consumption
    - Prevent overnight charging from grid during expensive peak hours

    Args:
        min_soc: Minimum SOC (%), range [10, 100]. Battery won't discharge below this. Required.
        charge_upper_soc: Maximum charging SOC (%), range [10, 100]. Battery won't charge above this. Required.
        charge_from_grid_enable: Allow charging from grid (0=no, 1=yes). Required.
        device_sn: Serial number of the inverter. If omitted, uses SOLAX_DEVICE_SN.
        charge_start_time_period1: Optional start time for charging period 1 (HH:MM format, e.g., "06:00").
        charge_end_time_period1: Optional end time for charging period 1 (HH:MM format, e.g., "18:00").
        discharge_start_time_period1: Optional start time for discharge period 1 (HH:MM format).
        discharge_end_time_period1: Optional end time for discharge period 1 (HH:MM format).
        enable_time_period2: Enable second time period (0=disabled, 1=enabled). Default: 0.
        charge_start_time_period2: Optional start time for charging period 2 (HH:MM format).
        charge_end_time_period2: Optional end time for charging period 2 (HH:MM format).
        discharge_start_time_period2: Optional start time for discharge period 2 (HH:MM format).
        discharge_end_time_period2: Optional end time for discharge period 2 (HH:MM format).

    Returns:
        API response confirming the command was sent to the inverter.

    Raises:
        ToolError: if the inverter is offline, credentials are invalid, or the API fails.
    """
    return await set_battery_self_use_mode_impl(
        device_sn=device_sn,
        min_soc=min_soc,
        charge_upper_soc=charge_upper_soc,
        charge_from_grid_enable=charge_from_grid_enable,
        charge_start_time_period1=charge_start_time_period1,
        charge_end_time_period1=charge_end_time_period1,
        discharge_start_time_period1=discharge_start_time_period1,
        discharge_end_time_period1=discharge_end_time_period1,
        enable_time_period2=enable_time_period2,
        charge_start_time_period2=charge_start_time_period2,
        charge_end_time_period2=charge_end_time_period2,
        discharge_start_time_period2=discharge_start_time_period2,
        discharge_end_time_period2=discharge_end_time_period2,
    )


# Write tool changes physical inverter settings; expose it to the LLM only on explicit opt-in.
if is_write_enabled():
    server.tool()(set_battery_self_use_mode)


def main() -> None:
    """Run the MCP server."""
    server.run()


if __name__ == "__main__":
    main()
