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
G12W = Tariff(windows=((22, 6), (13, 15)), weekends=True, holidays=True, summer_windows=((22, 6), (15, 17)))


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
        (_local(2027, 4, 1, 13), False),  # summer: 13-15 is peak
        (_local(2027, 4, 1, 15), True),  # summer midday window 15-17
        (_local(2027, 4, 1, 23), True),
        (_local(2027, 9, 30, 16), True),
        (_local(2027, 10, 1, 16), False),  # winter again
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


def test_weekend_and_holiday_profile():
    s = _settings(weekend_profile=(2.0,) * 24)
    assert s.load_kwh(_local(2026, 10, 6, 12)) == 1.0  # Tuesday
    assert s.load_kwh(_local(2026, 10, 10, 12)) == 2.0  # Saturday
    assert s.load_kwh(_local(2026, 11, 11, 12)) == 2.0  # Independence Day (Wednesday)
    assert _settings().load_kwh(_local(2026, 10, 10, 12)) == 1.0  # no weekend profile -> weekday


def test_export_reserve_uses_weekend_profile():
    # Saturday 17:00, horizon = 22:00 window: 5 h * 2 kWh weekend load -> 10/0.9 + 1 = 12.11
    plan = compute_export_plan(_settings(weekend_profile=(2.0,) * 24), EXPORT, _local(2026, 10, 10, 17, 0), 100, {}, [])
    assert plan["reserveForHouse_kWh"] == 12.11


def test_settings_from_env(monkeypatch):
    monkeypatch.setenv("BATTERY_CAPACITY_KWH", "21.2")
    monkeypatch.setenv("AUTOMATION_DAILY_CONSUMPTION_KWH", "12")
    monkeypatch.setenv("SOLAR_TIMEZONE", "Europe/Warsaw")
    monkeypatch.delenv("AUTOMATION_DRY_RUN", raising=False)
    s = Settings.from_env()
    assert s.dry_run is True  # safe default
    assert s.consumption_profile == (0.5,) * 24
    assert s.tariff.windows == ((22, 6), (13, 15))
    assert s.tariff.summer_windows == ((22, 6), (15, 17))
    # Custom windows stay year-round unless summer windows are set too
    monkeypatch.setenv("TARIFF_OFFPEAK_WINDOWS", "23:00-07:00")
    assert Settings.from_env().tariff.windows_for(date(2027, 7, 1)) == ((23, 7),)
    monkeypatch.delenv("TARIFF_OFFPEAK_WINDOWS")
    assert s.weekend_profile is None
    monkeypatch.setenv("AUTOMATION_WEEKEND_DAILY_CONSUMPTION_KWH", "19.2")
    assert Settings.from_env().weekend_profile == pytest.approx((0.8,) * 24)


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


def test_export_dry_run_defaults_on(monkeypatch):
    monkeypatch.setenv("EXPORT_MAX_POWER_KW", "8")
    monkeypatch.delenv("EXPORT_DRY_RUN", raising=False)
    assert ExportSettings.from_env().dry_run is True
    monkeypatch.setenv("EXPORT_DRY_RUN", "0")
    assert ExportSettings.from_env().dry_run is False


@pytest.fixture
def remote(monkeypatch):
    """Capture remote-control commands instead of hitting the SolaX API."""
    calls = {"commands": [], "fail": None}

    async def fake_discharge(device_sn, discharge_w, stop_soc):
        calls["commands"].append(("discharge", discharge_w, stop_soc))
        if calls["fail"]:
            raise automation.SolaxApiError("boom")

    async def fake_exit(device_sn):
        calls["commands"].append(("exit",))

    monkeypatch.setenv("SOLAX_DEVICE_SN", "SN1")
    monkeypatch.setattr(automation, "discharge_to_soc", fake_discharge)
    monkeypatch.setattr(automation, "exit_vpp_mode", fake_exit)
    return calls


def _live_scheduler(max_power_kw=5.0):
    export = ExportSettings(min_price_pln_kwh=1.0, max_power_kw=max_power_kw, min_slot_kwh=0.2, price_multiplier=1.0, dry_run=False)
    return AutomationScheduler(_settings(), export)


def _plan(export, soc=60, floor=40, setpoint=5.0, house=None):
    slot = {"export": True, "dischargeSetpoint_kW": setpoint, "expectedExport_kW": setpoint - 0.5} if export else {"export": False}
    return {"currentSlot": slot, "socPercent": soc, "recommendedExportFloorSoc": floor, "liveHouseLoad_kW": house}


async def test_export_slot_discharges_to_floor_then_exits_once(remote):
    scheduler = _live_scheduler(max_power_kw=4.0)
    assert await scheduler.apply_export(_plan(True, setpoint=4.6)) == {"action": "discharge", "setpoint_W": 4000, "stopSoc": 40}
    assert (await scheduler.apply_export(_plan(False)))["action"] == "exit"
    assert (await scheduler.apply_export(_plan(False)))["action"] == "none"
    assert remote["commands"] == [("discharge", 4000, 40), ("exit",)]


async def test_unknown_state_after_restart_exits(remote):
    assert (await _live_scheduler().apply_export(_plan(False)))["action"] == "exit"


async def test_soc_at_floor_never_discharges(remote):
    await _live_scheduler().apply_export(_plan(True, soc=40, floor=40))
    assert remote["commands"] == [("exit",)]


async def test_failed_discharge_exits_remote_control(remote):
    remote["fail"] = True
    scheduler = _live_scheduler()
    with pytest.raises(automation.SolaxApiError):
        await scheduler.apply_export(_plan(True))
    assert remote["commands"] == [("discharge", 5000, 40), ("exit",)]


def test_house_load_above_setpoint_means_no_export_in_that_slot():
    # 3 kW evening load, 3 kW setpoint -> nothing left for the grid
    settings = _settings(consumption_profile=(3.0,) * 24)
    export = ExportSettings(min_price_pln_kwh=1.0, max_power_kw=3.0, min_slot_kwh=0.2, price_multiplier=1.0, dry_run=True)
    plan = compute_export_plan(settings, export, _local(2026, 10, 6, 20, 0), 100, {}, [_slot(20, 0, 2.0)])
    assert plan["slots"] == []


async def test_setpoint_uses_live_house_load(remote):
    scheduler = _live_scheduler(max_power_kw=5.0)
    # Planned 3.5 kW export (profile load 0.5); washing machine running -> 2.1 kW measured
    assert (await scheduler.apply_export(_plan(True, setpoint=4.0, house=0.9)))["setpoint_W"] == 4400
    assert (await scheduler.apply_export(_plan(True, setpoint=4.0, house=2.1)))["setpoint_W"] == 5000  # capped


def test_live_house_load_from_realtime():
    rt = {"ac": {"phases": [{"power_W": 1300}, {"power_W": 1300}, {"power_W": 1400}]}, "meter1": {"gridPower_W": 3100}}
    assert automation.live_house_load_kw(rt) == 0.9  # 4.0 kW out, 3.1 kW exported
    rt["meter1"]["gridPower_W"] = -500  # importing 0.5 kW on top
    assert automation.live_house_load_kw(rt) == 4.5
    rt["meter1"]["gridPower_W"] = None
    assert automation.live_house_load_kw(rt) is None


def test_next_trigger_uses_summer_windows():
    s = _settings()
    t = next_trigger(s, _local(2027, 4, 1, 10))  # Thursday in summer
    assert (t.window, t.window_start.hour) == ((15, 17), 15)


async def test_export_plan_without_solcast_assumes_no_pv(monkeypatch):
    async def no_forecast(hours):
        raise automation.SolcastError("rate limit")

    async def realtime(_):
        return {"battery": {"soc_percent": 90}}

    async def prices():
        return []

    monkeypatch.setattr(automation, "get_solar_forecast", no_forecast)
    monkeypatch.setattr(automation, "get_realtime_data_impl", realtime)
    monkeypatch.setattr(automation, "get_rce_prices", prices)
    plan = await AutomationScheduler(_settings(), EXPORT).export_plan(_local(2026, 10, 6, 18, 0))
    assert plan["pvForecastAvailable"] is False
    assert plan["reserveForHouse_kWh"] == 5.44  # 18-22: 4 h * 1 kWh / 0.9 + 1, no PV


def test_decision_history_persists_and_skips_idle_export(tmp_path, monkeypatch):
    path = tmp_path / "decisions.jsonl"
    monkeypatch.setenv("DECISIONS_FILE", str(path))
    scheduler = _live_scheduler()
    scheduler._record_export({**_plan(False), "dryRun": False, "execution": {"action": "none"}})
    scheduler._record_export({**_plan(True), "dryRun": False, "execution": {"action": "discharge", "setpoint_W": 5000, "stopSoc": 40}})
    scheduler._record_export({"error": "SolaxApiError (details in server logs)"})
    scheduler._record("charge", {"targetSoc": 45})
    assert [e["kind"] for e in scheduler.history] == ["export", "export", "charge"]
    assert scheduler.history[0]["setpoint_W"] == 5000

    path.write_text(path.read_text() + "not json\n")
    restored = _live_scheduler().history
    assert [e["kind"] for e in restored] == ["export", "export", "charge"]
