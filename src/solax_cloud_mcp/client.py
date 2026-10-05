"""HTTP client for SolaX Developer Platform API."""

import asyncio
import logging
import time

import httpx

from . import auth
from .models import ERROR_CODE_DESCRIPTIONS

logger = logging.getLogger(__name__)

BASE_URL = "https://openapi-eu.solaxcloud.com"
REALTIME_DATA_URL = f"{BASE_URL}/openapi/v2/device/realtime_data"
HISTORY_DATA_URL = f"{BASE_URL}/openapi/v2/device/history_data"
SET_SELF_USE_MODE_URL = f"{BASE_URL}/openapi/v2/device/inverter_work_mode/batch_set_spontaneity_self_use"
# push_power/positive_or_negative_mode is accepted but ignored by the X3-NEO-LV (tested 2026-10-05); this mode works
SOC_TARGET_URL = f"{BASE_URL}/openapi/v2/device/inverter_vpp_mode/soc_target_control_mode"
EXIT_VPP_URL = f"{BASE_URL}/openapi/v2/device/inverter_vpp_mode/exit_vpp_mode"
REQUEST_RESULT_URL = f"{BASE_URL}/openapi/apiRequestLog/listByCondition"

DEVICE_TYPE_INVERTER = 1
DEVICE_TYPE_BATTERY = 2
BUSINESS_TYPE_RESIDENTIAL = 1
REQUEST_SN_TYPE_INVERTER = 1
# result[SN].status: 3 = command issued, 4 = device received and started execution
COMMAND_DELIVERED_STATUSES = (3, 4)

_last_call_at = 0.0
_rate_limit_lock = asyncio.Lock()
MIN_INTERVAL_SECONDS = 0.7  # 60s / 100 calls per minute, conservative buffer


class SolaxApiError(Exception):
    """Raised when the SolaX API returns an error."""

    pass


async def _request_realtime(
    device_sn: str, device_type: int, request_sn_type: int | None
) -> dict | None:
    """Internal: make a single realtime data request to the API.

    Args:
        device_sn: Device serial number.
        device_type: Device type (1=Inverter, 2=Battery, etc.).
        request_sn_type: Optional, only used for deviceType=2 (battery).

    Returns:
        The first result dict if available, else None (empty result list).

    Raises:
        SolaxApiError: on network errors, non-10000 code, or other failures.
    """
    # Build query params
    params = {
        "snList": device_sn,
        "deviceType": device_type,
        "businessType": BUSINESS_TYPE_RESIDENTIAL,
    }
    if request_sn_type is not None:
        params["requestSnType"] = request_sn_type

    result = await _get_result(REALTIME_DATA_URL, params)
    if not result:
        # Empty result list most likely means device is offline/no data yet
        return None
    if isinstance(result, list) and len(result) > 0:
        return result[0]
    return None


async def _get_result(url: str, params: dict):
    """GET a data endpoint: rate limit, auth (with one retry on 10402), check code 10000, return `result`."""
    global _last_call_at

    # Rate limiting
    async with _rate_limit_lock:
        elapsed = time.monotonic() - _last_call_at
        if elapsed < MIN_INTERVAL_SECONDS:
            await asyncio.sleep(MIN_INTERVAL_SECONDS - elapsed)
        _last_call_at = time.monotonic()

    # Get access token and make request
    try:
        token = await auth.get_access_token()
    except auth.SolaxAuthError as e:
        raise SolaxApiError(f"Authentication failed: {e}") from e

    async with httpx.AsyncClient(timeout=10.0) as client:
        try:
            response = await client.get(
                url,
                params=params,
                headers={"Authorization": f"bearer {token}"},
            )
        except httpx.TimeoutException as e:
            raise SolaxApiError(f"Network timeout: {e}") from e
        except httpx.RequestError as e:
            raise SolaxApiError(f"Network error: {e}") from e

        # Check HTTP status
        if response.status_code != 200:
            raise SolaxApiError(
                f"HTTP {response.status_code}: {response.text}"
            )

        # Parse JSON
        try:
            body = response.json()
        except ValueError as e:
            raise SolaxApiError(f"Invalid JSON response: {e}") from e

        # Check API response code (10000 = success for data endpoints)
        code = body.get("code")
        if code == 10402:
            # Access token auth failed; invalidate and retry once
            auth.invalidate_token()
            try:
                token = await auth.get_access_token()
            except auth.SolaxAuthError as e:
                raise SolaxApiError(f"Re-authentication failed after 10402: {e}") from e

            try:
                response = await client.get(
                    url,
                    params=params,
                    headers={"Authorization": f"bearer {token}"},
                )
            except httpx.RequestError as e:
                raise SolaxApiError(f"Retry after 10402 failed: {e}") from e

            if response.status_code != 200:
                raise SolaxApiError(f"Retry: HTTP {response.status_code}: {response.text}")

            try:
                body = response.json()
            except ValueError as e:
                raise SolaxApiError(f"Retry: Invalid JSON: {e}") from e

            code = body.get("code")

    if code != 10000:
        description = ERROR_CODE_DESCRIPTIONS.get(code, f"Unknown code {code}")
        raise SolaxApiError(f"API error {code}: {description}")

    return body.get("result")


async def fetch_history(device_sn: str, start_ms: int, end_ms: int, interval_min: int = 5) -> list[dict]:
    """Inverter history samples (W for residential) between two UTC ms timestamps, at most 12 h apart."""
    params = {
        "snList": device_sn,
        "deviceType": DEVICE_TYPE_INVERTER,
        "businessType": BUSINESS_TYPE_RESIDENTIAL,
        "startTime": start_ms,
        "endTime": end_ms,
        "timeInterval": interval_min,
    }
    return await _get_result(HISTORY_DATA_URL, params) or []


async def fetch_inverter_data(device_sn: str) -> dict:
    """Fetch inverter real-time data.

    Args:
        device_sn: Inverter serial number.

    Returns:
        The inverter data dict.

    Raises:
        SolaxApiError: if the call fails or returns no data.
    """
    result = await _request_realtime(device_sn, DEVICE_TYPE_INVERTER, None)
    if result is None:
        raise SolaxApiError(f"No inverter data returned for device_sn={device_sn}")
    return result


async def fetch_battery_data(device_sn: str) -> dict | None:
    """Fetch battery real-time data for an inverter.

    Swallows errors and returns None if no battery data is available,
    as this is likely just a battery-less system.

    Args:
        device_sn: Inverter serial number (queries battery associated with this inverter).

    Returns:
        The battery data dict, or None if unavailable or an error occurred.
    """
    try:
        return await _request_realtime(device_sn, DEVICE_TYPE_BATTERY, REQUEST_SN_TYPE_INVERTER)
    except SolaxApiError:
        # Battery-specific failure likely means no battery on this system
        return None


async def fetch_realtime_data(device_sn: str) -> tuple[dict, dict | None]:
    """Fetch both inverter and battery real-time data.

    Args:
        device_sn: Inverter serial number.

    Returns:
        A tuple of (inverter_data, battery_data_or_none).

    Raises:
        SolaxApiError: if the inverter call fails (battery errors are swallowed).
    """
    inverter_data = await fetch_inverter_data(device_sn)
    battery_data = await fetch_battery_data(device_sn)
    return inverter_data, battery_data


async def set_self_use_mode(
    device_sn: str,
    min_soc: int,
    charge_upper_soc: int,
    charge_from_grid_enable: int = 1,
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
    """Set inverter to Self Use Mode with charging thresholds.

    Self Use Mode allows configurable charge/discharge time periods and SOC thresholds.
    This is ideal for automated control based on weather conditions.

    Args:
        device_sn: Inverter serial number.
        min_soc: Minimum SOC (%), range [10, 100]. Battery won't discharge below this.
        charge_upper_soc: Charging limit SOC (%), range [10, 100]. Battery won't charge above this.
        charge_from_grid_enable: Allow charging from grid (0=disabled, 1=enabled). Default: 1.
        charge_start_time_period1: Start time for charge period 1 (HH:MM format, optional).
        charge_end_time_period1: End time for charge period 1 (HH:MM format, optional).
        discharge_start_time_period1: Start time for discharge period 1 (HH:MM format, optional).
        discharge_end_time_period1: End time for discharge period 1 (HH:MM format, optional).
        enable_time_period2: Enable second time period (0=disabled, 1=enabled). Default: 0.
        charge_start_time_period2: Start time for charge period 2 (HH:MM format, optional).
        charge_end_time_period2: End time for charge period 2 (HH:MM format, optional).
        discharge_start_time_period2: Start time for discharge period 2 (HH:MM format, optional).
        discharge_end_time_period2: End time for discharge period 2 (HH:MM format, optional).

    Returns:
        The API response dict containing status for each device.

    Raises:
        SolaxApiError: if the API call fails.
    """
    # Build request body
    body = {
        "snList": [device_sn],
        "minSoc": min_soc,
        "chargeFromGridEnable": charge_from_grid_enable,
        "chargeUpperSoc": charge_upper_soc,
        "businessType": BUSINESS_TYPE_RESIDENTIAL,
    }

    # Add optional time period 1 parameters
    if charge_start_time_period1:
        body["chargeStartTimePeriod1"] = charge_start_time_period1
    if charge_end_time_period1:
        body["chargeEndTimePeriod1"] = charge_end_time_period1
    if discharge_start_time_period1:
        body["dischargeStartTimePeriod1"] = discharge_start_time_period1
    if discharge_end_time_period1:
        body["dischargeEndTimePeriod1"] = discharge_end_time_period1

    # Add optional time period 2 parameters
    if enable_time_period2:
        body["enableTimePeriod2"] = enable_time_period2
    if charge_start_time_period2:
        body["chargeStartTimePeriod2"] = charge_start_time_period2
    if charge_end_time_period2:
        body["chargeEndTimePeriod2"] = charge_end_time_period2
    if discharge_start_time_period2:
        body["dischargeStartTimePeriod2"] = discharge_start_time_period2
    if discharge_end_time_period2:
        body["dischargeEndTimePeriod2"] = discharge_end_time_period2

    return await _post_command(SET_SELF_USE_MODE_URL, body)


async def _post_command(url: str, body: dict) -> dict:
    """POST a control command: rate limit, auth (with one retry on 10402), check code 10000."""
    global _last_call_at

    async with _rate_limit_lock:
        elapsed = time.monotonic() - _last_call_at
        if elapsed < MIN_INTERVAL_SECONDS:
            await asyncio.sleep(MIN_INTERVAL_SECONDS - elapsed)
        _last_call_at = time.monotonic()

    # Get access token and make request
    try:
        token = await auth.get_access_token()
    except auth.SolaxAuthError as e:
        raise SolaxApiError(f"Authentication failed: {e}") from e

    async with httpx.AsyncClient(timeout=10.0) as client:
        try:
            response = await client.post(
                url,
                json=body,
                headers={"Authorization": f"bearer {token}"},
            )
        except httpx.TimeoutException as e:
            raise SolaxApiError(f"Network timeout: {e}") from e
        except httpx.RequestError as e:
            raise SolaxApiError(f"Network error: {e}") from e

        # Check HTTP status
        if response.status_code != 200:
            raise SolaxApiError(
                f"HTTP {response.status_code}: {response.text}"
            )

        # Parse JSON
        try:
            response_body = response.json()
        except ValueError as e:
            raise SolaxApiError(f"Invalid JSON response: {e}") from e

        # Check API response code (10000 = success)
        code = response_body.get("code")
        if code == 10402:
            # Access token auth failed; invalidate and retry once
            auth.invalidate_token()
            try:
                token = await auth.get_access_token()
            except auth.SolaxAuthError as e:
                raise SolaxApiError(f"Re-authentication failed after 10402: {e}") from e

            try:
                response = await client.post(
                    url,
                    json=body,
                    headers={"Authorization": f"bearer {token}"},
                )
            except httpx.RequestError as e:
                raise SolaxApiError(f"Retry after 10402 failed: {e}") from e

            if response.status_code != 200:
                raise SolaxApiError(f"Retry: HTTP {response.status_code}: {response.text}")

            try:
                response_body = response.json()
            except ValueError as e:
                raise SolaxApiError(f"Retry: Invalid JSON: {e}") from e

            code = response_body.get("code")

        if code != 10000:
            description = ERROR_CODE_DESCRIPTIONS.get(code, f"Unknown code {code}")
            raise SolaxApiError(f"API error {code}: {description}")

        return response_body


def _check_delivered(response: dict, device_sn: str) -> dict:
    """Raise unless the device command was issued (code 10000 only means the platform accepted the request)."""
    logger.warning("SolaX command for %s: requestId=%s result=%s", device_sn, response.get("requestId"), response.get("result"))
    result = response.get("result")
    device_status = result.get(device_sn, {}).get("status") if isinstance(result, dict) else None
    if device_status not in COMMAND_DELIVERED_STATUSES:
        raise SolaxApiError(f"Command not delivered to {device_sn}: status {device_status}")
    return response


async def discharge_to_soc(device_sn: str, discharge_w: int, stop_soc: int) -> dict:
    """Remote control: discharge at discharge_w (W, inverter AC port) until the battery reaches stop_soc (%).

    The mode has no duration: it runs until stop_soc is reached or exit_vpp_mode is called.
    The house load is served first and only the rest is exported. If stop_soc >= current SOC, the
    inverter exits the mode right away.
    """
    body = {
        "snList": [device_sn],
        "targetSoc": stop_soc,
        "chargeDischargPower": -discharge_w,  # API: negative = discharge (sic, "chargeDischargPower")
        "businessType": BUSINESS_TYPE_RESIDENTIAL,
    }
    return _check_delivered(await _post_command(SOC_TARGET_URL, body), device_sn)


async def exit_vpp_mode(device_sn: str) -> dict:
    """Exit remote control: the inverter returns to its normal work mode (e.g. Self Use)."""
    body = {"snList": [device_sn], "businessType": BUSINESS_TYPE_RESIDENTIAL}
    return _check_delivered(await _post_command(EXIT_VPP_URL, body), device_sn)


async def get_request_result(request_id: str) -> dict:
    """Execution result of a control command, by the requestId it returned (status 4 = started, 5 = failed)."""
    return await _post_command(REQUEST_RESULT_URL, {"requestId": request_id})
