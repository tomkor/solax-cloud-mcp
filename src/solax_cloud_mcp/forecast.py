"""Solcast PV power forecast client (rooftop sites API) with response caching."""

import asyncio
import json
import logging
import os
import time
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

import httpx

from . import config

logger = logging.getLogger(__name__)

BASE_URL = "https://api.solcast.com.au"
PERIOD_HOURS = 0.5  # Solcast rooftop forecasts use PT30M periods

# Hobbyist accounts get ~10 calls/day, so cache per-site responses and serve stale data on failure.
_cache: dict[str, tuple[float, list[dict]]] = {}
_cache_lock = asyncio.Lock()
_cache_loaded = False


def _load_cache_file() -> None:
    """Fill the in-memory cache from SOLCAST_CACHE_FILE once per process."""
    global _cache_loaded
    _cache_loaded = True
    path = config.get_solcast_cache_file()
    if not path or not os.path.exists(path):
        return
    try:
        with open(path) as f:
            data = json.load(f)
        for rid, (fetched_at, periods) in data.items():
            _cache.setdefault(rid, (float(fetched_at), periods))
    except (OSError, ValueError, TypeError) as e:
        logger.warning("Ignoring unreadable Solcast cache file %s: %s", path, e)


def _save_cache_file() -> None:
    path = config.get_solcast_cache_file()
    if not path:
        return
    try:
        tmp = f"{path}.tmp"
        with open(tmp, "w") as f:
            json.dump({rid: [t, periods] for rid, (t, periods) in _cache.items()}, f)
        os.replace(tmp, path)
    except OSError as e:
        logger.warning("Could not write Solcast cache file %s: %s", path, e)


class SolcastError(Exception):
    """Raised when the Solcast API call fails and no cached data is available."""

    pass


async def _fetch_site(client: httpx.AsyncClient, resource_id: str, api_key: str) -> list[dict]:
    """Fetch raw 30-minute forecast periods for one rooftop site."""
    try:
        response = await client.get(
            f"{BASE_URL}/rooftop_sites/{resource_id}/forecasts",
            params={"format": "json", "hours": 168},
            # Key in header, not query string, so it never ends up in URLs/logs
            headers={"Authorization": f"Bearer {api_key}"},
        )
    except httpx.RequestError as e:
        raise SolcastError(f"Network error: {e}") from e

    if response.status_code == 429:
        raise SolcastError("Solcast API rate limit reached (429)")
    if response.status_code != 200:
        raise SolcastError(f"HTTP {response.status_code}: {response.text[:200]}")

    try:
        forecasts = response.json()["forecasts"]
    except (ValueError, KeyError, TypeError) as e:
        raise SolcastError(f"Invalid forecast response: {e}") from e
    return forecasts


async def _get_site_forecast(client: httpx.AsyncClient, resource_id: str) -> tuple[list[dict], float, bool]:
    """Return (periods, fetched_at_epoch, stale) for a site, using the cache when fresh."""
    ttl = config.get_solcast_cache_minutes() * 60
    cached = _cache.get(resource_id)
    if cached and time.time() - cached[0] < ttl:
        return cached[1], cached[0], False

    try:
        periods = await _fetch_site(client, resource_id, config.get_solcast_api_key())
    except SolcastError as e:
        if cached:
            logger.warning("Solcast fetch failed for site %s, serving cached data: %s", resource_id, e)
            return cached[1], cached[0], True
        raise

    fetched_at = time.time()
    _cache[resource_id] = (fetched_at, periods)
    _save_cache_file()
    return periods, fetched_at, False


def _parse_period_end(value: str) -> datetime:
    # Solcast returns 7 fractional digits ("...00.0000000Z"); fromisoformat accepts at most 6
    value = value.rstrip("Z")
    if "." in value:
        head, frac = value.split(".", 1)
        value = f"{head}.{frac[:6]}"
    return datetime.fromisoformat(value).replace(tzinfo=timezone.utc)


def shape_forecast(
    periods: list[dict],
    tz: ZoneInfo,
    now: datetime,
    hours: int,
) -> dict:
    """Aggregate merged 30-min periods into per-day energy totals and an hourly power profile.

    Args:
        periods: Solcast periods (already summed across sites), pv_estimate* in kW (period average).
        tz: Local timezone used for day boundaries and output timestamps.
        now: Current time (aware), periods ending before it are treated as past.
        hours: Number of upcoming hours to include in the hourly profile.
    """
    days: dict[str, dict[str, float]] = defaultdict(lambda: {"p10": 0.0, "p50": 0.0, "p90": 0.0})
    peaks: dict[str, tuple[float, datetime]] = {}
    hourly: dict[datetime, dict[str, list[float]]] = defaultdict(lambda: {"p10": [], "p50": [], "p90": []})
    remaining_today = {"p10": 0.0, "p50": 0.0, "p90": 0.0}

    today = now.astimezone(tz).date()
    horizon = now + timedelta(hours=hours)

    for p in periods:
        end = p["period_end"]
        start = end - timedelta(hours=PERIOD_HOURS)
        local_start = start.astimezone(tz)
        day = local_start.date().isoformat()
        values = {"p10": p["pv_estimate10"], "p50": p["pv_estimate"], "p90": p["pv_estimate90"]}

        for k, kw in values.items():
            days[day][k] += kw * PERIOD_HOURS
        if day not in peaks or values["p50"] > peaks[day][0]:
            peaks[day] = (values["p50"], local_start)

        if end > now and local_start.date() == today:
            for k, kw in values.items():
                remaining_today[k] += kw * PERIOD_HOURS

        if end > now and start < horizon:
            hour = local_start.replace(minute=0, second=0, microsecond=0)
            for k, kw in values.items():
                hourly[hour][k].append(kw)

    def r(x: float) -> float:
        return round(x, 2)

    return {
        "timezone": str(tz),
        "days": [
            {
                "date": day,
                "energy_kWh": {k: r(v) for k, v in totals.items()},
                "peakPower_kW": r(peaks[day][0]),
                "peakTime": peaks[day][1].isoformat(timespec="minutes"),
            }
            for day, totals in sorted(days.items())
            if day >= today.isoformat()
        ],
        "remainingToday_kWh": {k: r(v) for k, v in remaining_today.items()},
        "hourly": [
            {"time": hour.isoformat(timespec="minutes"), "power_kW": {k: r(sum(v) / len(v)) for k, v in vals.items()}}
            for hour, vals in sorted(hourly.items())
        ],
    }


async def get_solar_forecast(hours: int = 24) -> dict:
    """Fetch (or serve cached) Solcast forecast for all configured sites and shape it.

    Raises:
        SolcastError: if a site cannot be fetched and has no cached data.
    """
    resource_ids = config.get_solcast_resource_ids()
    tz = ZoneInfo(config.get_solar_timezone())

    async with _cache_lock:
        if not _cache_loaded:
            _load_cache_file()
        async with httpx.AsyncClient(timeout=15.0) as client:
            results = [await _get_site_forecast(client, rid) for rid in resource_ids]

    # Sum sites (e.g. east/west arrays) per period
    merged: dict[datetime, dict] = {}
    for site_periods, _, _ in results:
        for p in site_periods:
            end = _parse_period_end(p["period_end"])
            m = merged.setdefault(end, {"period_end": end, "pv_estimate": 0.0, "pv_estimate10": 0.0, "pv_estimate90": 0.0})
            for k in ("pv_estimate", "pv_estimate10", "pv_estimate90"):
                m[k] += float(p.get(k) or 0.0)

    shaped = shape_forecast(list(merged.values()), tz, datetime.now(timezone.utc), hours)
    oldest = min(fetched_at for _, fetched_at, _ in results)
    return {
        "source": "solcast",
        "fetchedAt": datetime.fromtimestamp(oldest, tz).isoformat(timespec="seconds"),
        "stale": any(stale for _, _, stale in results),
        **shaped,
    }
