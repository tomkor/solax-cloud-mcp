"""Tests for consumption profiles from SolaX history."""

import os
from datetime import datetime, timedelta

import pytest

os.environ.setdefault("SOLAX_CLIENT_ID", "test-client-id")
os.environ.setdefault("SOLAX_CLIENT_SECRET", "test-client-secret")

from solax_cloud_mcp.consumption import house_load_w, hourly_profiles  # noqa: E402


def test_house_load_is_ac_output_minus_grid():
    # Real sample, Sat 2026-10-03 12:50: 2084 W out, 1370 W exported
    assert house_load_w({"acPower1": 696, "acPower2": 690, "acPower3": 698, "gridPower": 1370.0}) == 714.0
    assert house_load_w({"acPower1": 0, "acPower2": 0, "acPower3": 0, "gridPower": -900}) == 900  # importing
    assert house_load_w({"acPower1": 100, "acPower2": None, "acPower3": 100, "gridPower": 0}) is None


def _day(start: datetime, watts_by_hour) -> list[tuple[datetime, float]]:
    return [(start + timedelta(minutes=m), watts_by_hour(m // 60)) for m in range(0, 24 * 60, 5)]


def test_profiles_split_weekdays_weekends_and_holidays():
    samples = (
        _day(datetime(2026, 10, 2), lambda h: 1200 if 11 <= h < 17 else 300)  # Friday
        + _day(datetime(2026, 10, 3), lambda h: 2000)  # Saturday
        + _day(datetime(2026, 11, 11), lambda h: 1000)  # Wednesday, Independence Day
    )
    p = hourly_profiles(samples)
    assert p["weekday"]["days"] == 1
    assert p["weekday"]["profile"][10:12] == [0.3, 1.2]
    assert p["weekday"]["daily_kWh"] == 12.6  # 6 * 1.2 + 18 * 0.3
    assert p["weekend"]["days"] == 2
    assert p["weekend"]["profile"][0] == 1.5  # average of 2.0 and 1.0


def test_missing_hours_raise():
    with pytest.raises(ValueError, match="longer period"):
        hourly_profiles([(datetime(2026, 10, 2, 12), 500.0)])
