"""Tests for PSE RCE price parsing and fetching."""

from datetime import datetime, timedelta, timezone

import httpx
import pytest
import respx

from solax_cloud_mcp import prices
from solax_cloud_mcp.prices import PSE_TZ, PriceError, _parse_records


@pytest.fixture(autouse=True)
def clear_cache(monkeypatch):
    monkeypatch.setattr(prices, "_cache", None)


def test_dtime_is_period_end_in_local_time():
    slots = _parse_records([{"dtime": "2026-10-05 18:15:00", "rce_pln": 1234.5, "business_date": "2026-10-05"}])
    assert slots[0].start == datetime(2026, 10, 5, 18, 0, tzinfo=PSE_TZ)
    assert slots[0].end == datetime(2026, 10, 5, 18, 15, tzinfo=PSE_TZ)
    assert slots[0].price_pln_kwh == pytest.approx(1.2345)


def test_24_00_means_next_midnight():
    slots = _parse_records([{"dtime": "2026-10-05 24:00:00", "rce_pln": 300, "business_date": "2026-10-05"}])
    assert slots[0].end == datetime(2026, 10, 6, 0, 0, tzinfo=PSE_TZ)


def test_autumn_dst_repeated_hour_gets_distinct_instants():
    # 2026-10-25: 02:00-03:00 local occurs twice
    records = [
        {"dtime": "2026-10-25 02:15:00", "rce_pln": 100, "business_date": "2026-10-25"},
        {"dtime": "2026-10-25 02:15:00", "rce_pln": 200, "business_date": "2026-10-25"},
    ]
    a, b = _parse_records(records)
    assert b.end.astimezone(timezone.utc) - a.end.astimezone(timezone.utc) == timedelta(hours=1)


@respx.mock
async def test_fetch_uses_filter_and_caches():
    route = respx.get(prices.RCE_URL).mock(
        return_value=httpx.Response(200, json={"value": [{"dtime": "2026-10-05 18:15:00", "rce_pln": 1000, "business_date": "2026-10-05"}]})
    )
    now = datetime(2026, 10, 5, 12, tzinfo=PSE_TZ)
    first = await prices.get_rce_prices(now)
    await prices.get_rce_prices(now)
    assert route.call_count == 1
    assert "business_date ge '2026-10-05'" in route.calls[0].request.url.params["$filter"]
    assert first[0].price_pln_kwh == 1.0


@respx.mock
async def test_fetch_error_without_cache_raises():
    respx.get(prices.RCE_URL).mock(return_value=httpx.Response(503))
    with pytest.raises(PriceError):
        await prices.get_rce_prices()
