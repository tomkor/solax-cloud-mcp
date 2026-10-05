"""Tests for the G12w battery grid-charging planner."""

import os
from datetime import date, datetime, timedelta, timezone
from zoneinfo import ZoneInfo

import pytest

os.environ.setdefault("SOLAX_CLIENT_ID", "test-client-id")
os.environ.setdefault("SOLAX_CLIENT_SECRET", "test-client-secret")

from solax_cloud_mcp import automation  # noqa: E402
from solax_cloud_mcp.automation import (  # noqa: E402
    AutomationScheduler,
    Settings,
    Tariff,
    Trigger,
    compute_plan,
    next_trigger,
    polish_holidays,
)

TZ = ZoneInfo("Europe/Warsaw")
G12W = Tariff(windows=((22, 6), (13, 15)), weekends=True, holidays=True)


def _settings(**overrides) -> Settings:
    base = dict(
        tz=TZ,
        tariff=G12W,
        capacity_kwh=21.2,
        consumption_profile=(1.0,) * 24,
        min_soc=15,
        max_grid_soc=100,
        safety_margin_kwh=1.0,
        efficiency=0.9,
        percentile="p50",
        lead_minutes=10,
        dry_run=True,
    )
    base.update(overrides)
    return Settings(**base)


def _local(y, mo, d, h, mi=0):
    return datetime(y, mo, d, h, mi, tzinfo=TZ)


def _trigger(start: datetime, window: tuple[int, int]) -> Trigger:
    return Trigger(run_at=start, window_start=start, window=window)


def test_polish_holidays_2026():
    days = polish_holidays(2026)
    assert date(2026, 4, 5) in days  # Easter
    assert date(2026, 4, 6) in days  # Easter Monday
    assert date(2026, 6, 4) in days  # Corpus Christi
    assert date(2026, 12, 24) in days  # Christmas Eve (since 2025)
    assert date(2026, 4, 7) not in days


@pytest.mark.parametrize(
    "when,offpeak",
    [
        (_local(2026, 10, 5, 23), True),  # Monday night
        (_local(2026, 10, 6, 5), True),  # Tuesday early morning (wraps midnight)
        (_local(2026, 10, 6, 6), False),  # Tuesday 06:00 peak
        (_local(2026, 10, 6, 13), True),  # midday window
        (_local(2026, 10, 6, 15), False),
        (_local(2026, 10, 10, 18), True),  # Saturday
        (_local(2026, 11, 11, 18), True),  # Independence Day (Wednesday)
    ],
)
def test_g12w_zones(when, offpeak):
    assert G12W.is_offpeak(when) is offpeak


def test_cloudy_night_charges_for_morning_peak():
    # Tuesday 22:00 window -> Wednesday 06:00-13:00 peak, no PV: 7 h * 1 kWh
    plan = compute_plan(_settings(), _trigger(_local(2026, 10, 6, 22), (22, 6)), {})
    assert plan["peakSegment"]["hours"] == 7
    assert plan["peakSegment"]["maxDeficit_kWh"] == 7.0
    # 7 / 0.9 + 1.0 = 8.78 kWh -> ceil(41.4%) = 42 -> 15 + 42
    assert plan["needFromBattery_kWh"] == 8.78
    assert plan["targetSoc"] == 57
    assert plan["gridCharge"] is True


def test_sunny_morning_skips_grid_charging():
    pv = {_local(2026, 10, 7, h): 0.0 if h < 8 else 3.0 for h in range(6, 13)}
    plan = compute_plan(_settings(), _trigger(_local(2026, 10, 6, 22), (22, 6)), pv)
    # Deficit only 06-08 (2 kWh), surplus afterwards does not help those first hours
    assert plan["peakSegment"]["maxDeficit_kWh"] == 2.0
    assert plan["targetSoc"] == 15 + 16  # (2/0.9+1)/21.2 = 15.2% -> 16


def test_pv_covering_everything_means_no_grid_charge():
    pv = {_local(2026, 10, 7, h): 2.0 for h in range(6, 13)}
    plan = compute_plan(_settings(), _trigger(_local(2026, 10, 6, 22), (22, 6)), pv)
    assert plan["needFromBattery_kWh"] == 0.0
    assert plan["targetSoc"] == 15
    assert plan["gridCharge"] is False


def test_friday_night_before_weekend_does_not_charge():
    plan = compute_plan(_settings(), _trigger(_local(2026, 10, 9, 22), (22, 6)), {})
    assert plan["peakSegment"]["hours"] == 0
    assert plan["gridCharge"] is False


def test_midday_window_covers_evening_peak_and_caps_at_max():
    plan = compute_plan(
        _settings(consumption_profile=(4.0,) * 24, max_grid_soc=90),
        _trigger(_local(2026, 10, 6, 13), (13, 15)),
        {},
    )
    assert plan["peakSegment"]["hours"] == 7  # 15:00-22:00
    assert plan["targetSoc"] == 90


def test_next_trigger_order_and_lead():
    after = datetime(2026, 10, 6, 10, 0, tzinfo=timezone.utc)  # 12:00 local
    trig = next_trigger(_settings(), after)
    assert trig.window == (13, 15)
    assert trig.run_at == _local(2026, 10, 6, 12, 50)
    trig2 = next_trigger(_settings(), trig.run_at)
    assert trig2.window == (22, 6)
    assert trig2.run_at == _local(2026, 10, 6, 21, 50)


@pytest.fixture
def fake_io(monkeypatch):
    writes = []

    async def fake_forecast(hours):
        return {
            "fetchedAt": "2026-10-06T21:00:00+02:00",
            "stale": False,
            "hourly": [{"time": "2026-10-07T06:00+02:00", "power_kW": {"p10": 0.0, "p50": 0.5, "p90": 1.0}}],
        }

    async def fake_set(**kwargs):
        writes.append(kwargs)
        return {"code": 10000}

    monkeypatch.setattr(automation, "get_solar_forecast", fake_forecast)
    monkeypatch.setattr(automation, "set_battery_self_use_mode_impl", fake_set)
    return writes


async def test_run_applies_plan_to_inverter(fake_io):
    scheduler = AutomationScheduler(_settings(dry_run=False))
    result = await scheduler.run(_trigger(_local(2026, 10, 6, 22), (22, 6)), apply=True)
    assert result["applied"] is True
    assert result["peakSegment"]["pvForecast_kWh"] == 0.5
    assert fake_io == [
        {
            "min_soc": 15,
            "charge_upper_soc": result["targetSoc"],
            "charge_from_grid_enable": 1,
            "charge_start_time_period1": "22:00",
            "charge_end_time_period1": "06:00",
        }
    ]


async def test_preview_never_writes(fake_io):
    result = await AutomationScheduler(_settings(dry_run=False)).preview()
    assert result["applied"] is False
    assert fake_io == []


def test_settings_from_env(monkeypatch):
    monkeypatch.setenv("BATTERY_CAPACITY_KWH", "21.2")
    monkeypatch.setenv("AUTOMATION_DAILY_CONSUMPTION_KWH", "12")
    monkeypatch.setenv("SOLAR_TIMEZONE", "Europe/Warsaw")
    monkeypatch.delenv("AUTOMATION_DRY_RUN", raising=False)
    s = Settings.from_env()
    assert s.dry_run is True  # safe default
    assert s.consumption_profile == (0.5,) * 24
    assert s.tariff.windows == ((22, 6), (13, 15))


@pytest.mark.parametrize(
    "env",
    [
        {},  # no capacity
        {"BATTERY_CAPACITY_KWH": "21.2"},  # no consumption
        {"BATTERY_CAPACITY_KWH": "21.2", "AUTOMATION_DAILY_CONSUMPTION_KWH": "12", "TARIFF_OFFPEAK_WINDOWS": "22:30-06:00"},
        {"BATTERY_CAPACITY_KWH": "21.2", "AUTOMATION_CONSUMPTION_PROFILE": "1,2,3"},
        {"BATTERY_CAPACITY_KWH": "21.2", "AUTOMATION_DAILY_CONSUMPTION_KWH": "12", "AUTOMATION_MIN_SOC": "5"},
    ],
)
def test_settings_from_env_rejects_invalid(monkeypatch, env):
    for name in ("BATTERY_CAPACITY_KWH", "AUTOMATION_DAILY_CONSUMPTION_KWH", "AUTOMATION_CONSUMPTION_PROFILE"):
        monkeypatch.delenv(name, raising=False)
    for k, v in env.items():
        monkeypatch.setenv(k, v)
    with pytest.raises(RuntimeError):
        Settings.from_env()


from solax_cloud_mcp.automation import ExportSettings, compute_export_plan  # noqa: E402
from solax_cloud_mcp.prices import PriceSlot  # noqa: E402

EXPORT = ExportSettings(min_price_pln_kwh=1.0, max_power_kw=8.0, min_slot_kwh=0.2, price_multiplier=1.0, dry_run=True)


def _slot(h, m, price):
    start = _local(2026, 10, 6, h, m)
    return PriceSlot(start=start, end=start + timedelta(minutes=15), price_pln_kwh=price)


def test_export_sells_only_surplus_in_best_slots():
    # Tuesday 17:00, horizon = 22:00 window; 5 h * 1 kWh house need, no PV -> reserve 5/0.9+1 = 6.56
    now = _local(2026, 10, 6, 17, 0)
    prices = [_slot(17, 0, 0.8), _slot(18, 0, 1.2), _slot(18, 15, 1.5), _slot(19, 0, 1.1), _slot(23, 0, 2.0)]
    # SOC 60% -> (60-15)% * 21.2 = 9.54 kWh stored; surplus = 9.54 - 6.56 = 2.98
    plan = compute_export_plan(_settings(), EXPORT, now, 60, {}, prices)
    assert plan["reserveForHouse_kWh"] == 6.56
    assert plan["recommendedExportFloorSoc"] == 15 + 31  # 6.56 / 21.2 = 30.9% -> 31
    assert plan["surplus_kWh"] == 2.98
    # 8 kW * 0.25 h = 2 kWh from battery, house takes 0.25 -> 1.75 exported per slot.
    # Best slot 18:15 gets 1.75, then 18:00 the remaining 1.23; 23:00 is beyond horizon
    slots = [(s["start"][11:16], s["export_kWh"], s["dischargeSetpoint_kW"]) for s in plan["slots"]]
    assert slots == [("18:00", 1.23, 5.94), ("18:15", 1.75, 8.0)]
    assert plan["plannedExport_kWh"] == 2.98
    assert plan["currentSlot"] == {"export": False}


def test_export_current_slot_and_threshold():
    now = _local(2026, 10, 6, 18, 5)
    plan = compute_export_plan(_settings(), EXPORT, now, 100, {}, [_slot(18, 0, 1.3), _slot(18, 15, 0.99)])
    assert plan["currentSlot"]["export"] is True
    assert plan["currentSlot"]["dischargeSetpoint_kW"] == 8.0
    assert plan["currentSlot"]["expectedExport_kW"] == 7.0  # 1 kW house load served first
    assert len(plan["slots"]) == 1  # 0.99 below threshold


def test_no_export_when_battery_needed_for_house():
    now = _local(2026, 10, 6, 15, 0)
    plan = compute_export_plan(_settings(), EXPORT, now, 30, {}, [_slot(18, 0, 3.0)])
    assert plan["surplus_kWh"] == 0.0
    assert plan["slots"] == []


def test_export_dry_run_off_is_rejected(monkeypatch):
    monkeypatch.setenv("EXPORT_MAX_POWER_KW", "8")
    monkeypatch.setenv("EXPORT_DRY_RUN", "0")
    with pytest.raises(RuntimeError, match="not supported"):
        ExportSettings.from_env()


def test_house_load_above_setpoint_means_no_export_in_that_slot():
    # 3 kW evening load, 3 kW setpoint -> nothing left for the grid
    settings = _settings(consumption_profile=(3.0,) * 24)
    export = ExportSettings(min_price_pln_kwh=1.0, max_power_kw=3.0, min_slot_kwh=0.2, price_multiplier=1.0, dry_run=True)
    plan = compute_export_plan(settings, export, _local(2026, 10, 6, 20, 0), 100, {}, [_slot(20, 0, 2.0)])
    assert plan["slots"] == []
