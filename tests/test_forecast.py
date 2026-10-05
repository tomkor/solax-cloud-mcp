"""Tests for the Solcast forecast client: aggregation, multi-site merge, caching."""

import os
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

import httpx
import pytest
import respx

os.environ.setdefault("SOLAX_CLIENT_ID", "test-client-id")
os.environ.setdefault("SOLAX_CLIENT_SECRET", "test-client-secret")

from solax_cloud_mcp import forecast  # noqa: E402
from solax_cloud_mcp.forecast import SolcastError, shape_forecast  # noqa: E402

URL_A = "https://api.solcast.com.au/rooftop_sites/site-a/forecasts"
URL_B = "https://api.solcast.com.au/rooftop_sites/site-b/forecasts"


def _period(end: str, p50: float, p10: float | None = None, p90: float | None = None) -> dict:
    return {
        "period_end": end,
        "period": "PT30M",
        "pv_estimate": p50,
        "pv_estimate10": p10 if p10 is not None else p50 / 2,
        "pv_estimate90": p90 if p90 is not None else p50 * 2,
    }


@pytest.fixture(autouse=True)
def solcast_env(monkeypatch):
    monkeypatch.setenv("SOLCAST_API_KEY", "secret-key")
    monkeypatch.setenv("SOLCAST_RESOURCE_IDS", "site-a")
    monkeypatch.setenv("SOLAR_TIMEZONE", "Europe/Warsaw")
    monkeypatch.setattr(forecast, "_cache", {})
    monkeypatch.setattr(forecast, "_cache_loaded", False)
    monkeypatch.delenv("SOLCAST_CACHE_FILE", raising=False)


def test_shape_groups_by_local_day_and_hour():
    tz = ZoneInfo("Europe/Warsaw")
    now = datetime(2026, 7, 1, 8, 0, tzinfo=timezone.utc)  # 10:00 local
    periods = [
        # 2026-06-30 22:00-22:30 UTC = 2026-07-01 00:00 local -> belongs to July 1st, already past
        {"period_end": datetime(2026, 6, 30, 22, 30, tzinfo=timezone.utc), "pv_estimate": 1.0, "pv_estimate10": 0.5, "pv_estimate90": 2.0},
        {"period_end": datetime(2026, 7, 1, 8, 30, tzinfo=timezone.utc), "pv_estimate": 4.0, "pv_estimate10": 2.0, "pv_estimate90": 5.0},
        {"period_end": datetime(2026, 7, 1, 9, 0, tzinfo=timezone.utc), "pv_estimate": 2.0, "pv_estimate10": 1.0, "pv_estimate90": 3.0},
        {"period_end": datetime(2026, 7, 2, 10, 0, tzinfo=timezone.utc), "pv_estimate": 6.0, "pv_estimate10": 3.0, "pv_estimate90": 7.0},
    ]

    shaped = shape_forecast(periods, tz, now, hours=2)

    assert [d["date"] for d in shaped["days"]] == ["2026-07-01", "2026-07-02"]
    assert shaped["days"][0]["energy_kWh"] == {"p10": 1.75, "p50": 3.5, "p90": 5.0}
    assert shaped["days"][0]["peakPower_kW"] == 4.0
    assert shaped["days"][0]["peakTime"] == "2026-07-01T10:00+02:00"
    assert shaped["remainingToday_kWh"] == {"p10": 1.5, "p50": 3.0, "p90": 4.0}
    # Two half-hours inside 10:00 local are averaged into one hourly point
    assert shaped["hourly"] == [{"time": "2026-07-01T10:00+02:00", "power_kW": {"p10": 1.5, "p50": 3.0, "p90": 4.0}}]


def test_parse_period_end_handles_seven_fractional_digits():
    parsed = forecast._parse_period_end("2026-07-01T08:30:00.0000000Z")
    assert parsed == datetime(2026, 7, 1, 8, 30, tzinfo=timezone.utc)


@respx.mock
async def test_sums_sites_and_sends_key_in_header(monkeypatch):
    monkeypatch.setenv("SOLCAST_RESOURCE_IDS", "site-a, site-b")
    end = "2099-01-01T12:00:00.0000000Z"
    route_a = respx.get(URL_A).mock(return_value=httpx.Response(200, json={"forecasts": [_period(end, 1.0)]}))
    respx.get(URL_B).mock(return_value=httpx.Response(200, json={"forecasts": [_period(end, 2.0)]}))

    result = await forecast.get_solar_forecast(hours=24)

    assert result["days"][0]["energy_kWh"]["p50"] == 1.5  # (1 + 2) kW * 0.5 h
    assert result["stale"] is False
    request = route_a.calls[0].request
    assert request.headers["Authorization"] == "Bearer secret-key"
    assert "secret-key" not in str(request.url)


@respx.mock
async def test_cache_avoids_second_call():
    route = respx.get(URL_A).mock(
        return_value=httpx.Response(200, json={"forecasts": [_period("2099-01-01T12:00:00Z", 1.0)]})
    )
    await forecast.get_solar_forecast()
    await forecast.get_solar_forecast()
    assert route.call_count == 1


@respx.mock
async def test_serves_stale_cache_on_rate_limit(monkeypatch):
    monkeypatch.setenv("SOLCAST_CACHE_MINUTES", "0")
    respx.get(URL_A).mock(
        side_effect=[
            httpx.Response(200, json={"forecasts": [_period("2099-01-01T12:00:00Z", 1.0)]}),
            httpx.Response(429),
        ]
    )
    await forecast.get_solar_forecast()
    result = await forecast.get_solar_forecast()
    assert result["stale"] is True
    assert result["days"][0]["energy_kWh"]["p50"] == 0.5


@respx.mock
async def test_error_without_cache_raises():
    respx.get(URL_A).mock(return_value=httpx.Response(429))
    with pytest.raises(SolcastError, match="rate limit"):
        await forecast.get_solar_forecast()


@respx.mock
async def test_cache_file_survives_restart(monkeypatch, tmp_path):
    """A restart must not spend one of the ~10 daily Solcast calls."""
    monkeypatch.setenv("SOLCAST_CACHE_FILE", str(tmp_path / "solcast.json"))
    route = respx.get(URL_A).mock(
        return_value=httpx.Response(200, json={"forecasts": [_period("2099-01-01T12:00:00Z", 1.0)]})
    )
    await forecast.get_solar_forecast()

    # Simulate a new process: empty memory cache, file not loaded yet
    monkeypatch.setattr(forecast, "_cache", {})
    monkeypatch.setattr(forecast, "_cache_loaded", False)
    result = await forecast.get_solar_forecast()

    assert route.call_count == 1
    assert result["days"][0]["energy_kWh"]["p50"] == 0.5
