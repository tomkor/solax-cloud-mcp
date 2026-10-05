"""Forecast-driven battery grid-charging planner for time-of-use tariffs (e.g. Polish G12w).

Before each daily off-peak window (e.g. 22:00-06:00 and 13:00-15:00) the planner looks at the
peak-price hours that directly follow it, estimates how much energy the house will need from the
battery there (consumption profile minus Solcast PV forecast) and sets Self Use Mode so the
battery is charged from the grid during the cheap window only up to that level.
"""

import asyncio
import logging
import math
import os
from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone
from zoneinfo import ZoneInfo

from . import config
from .forecast import get_solar_forecast
from .prices import PriceSlot, get_rce_prices
from .server import get_realtime_data_impl, set_battery_self_use_mode_impl

logger = logging.getLogger(__name__)

MAX_SEGMENT_HOURS = 24


def _easter(year: int) -> date:
    """Gregorian Easter Sunday (anonymous Gregorian algorithm)."""
    a = year % 19
    b, c = divmod(year, 100)
    d, e = divmod(b, 4)
    f = (b + 8) // 25
    g = (b - f + 1) // 3
    h = (19 * a + b - d - g + 15) % 30
    i, k = divmod(c, 4)
    l = (32 + 2 * e + 2 * i - h - k) % 7
    m = (a + 11 * h + 22 * l) // 451
    month, day = divmod(h + l - 7 * m + 114, 31)
    return date(year, month, day + 1)


def polish_holidays(year: int) -> set[date]:
    """Statutory public holidays in Poland (off-peak all day in G12w)."""
    easter = _easter(year)
    fixed = [(1, 1), (1, 6), (5, 1), (5, 3), (8, 15), (11, 1), (11, 11), (12, 25), (12, 26)]
    days = {date(year, m, d) for m, d in fixed}
    days |= {easter, easter + timedelta(days=1), easter + timedelta(days=49), easter + timedelta(days=60)}
    if year >= 2025:
        days.add(date(year, 12, 24))
    return days


def _parse_windows(raw: str) -> tuple[tuple[int, int], ...]:
    """Parse "22:00-06:00,13:00-15:00" into ((22, 6), (13, 15)). Full hours only."""
    windows = []
    for part in raw.split(","):
        part = part.strip()
        if not part:
            continue
        try:
            start, end = part.split("-")
            sh, sm = (int(x) for x in start.split(":"))
            eh, em = (int(x) for x in end.split(":"))
        except ValueError as e:
            raise RuntimeError(f"Invalid TARIFF_OFFPEAK_WINDOWS entry {part!r}, expected HH:00-HH:00") from e
        if sm or em or not (0 <= sh < 24 and 0 <= eh < 24) or sh == eh:
            raise RuntimeError(f"Invalid TARIFF_OFFPEAK_WINDOWS entry {part!r}, expected full hours HH:00-HH:00")
        windows.append((sh, eh))
    if not windows:
        raise RuntimeError("TARIFF_OFFPEAK_WINDOWS must contain at least one window")
    return tuple(windows)


@dataclass(frozen=True)
class Tariff:
    windows: tuple[tuple[int, int], ...]
    weekends: bool
    holidays: bool

    def is_offpeak(self, local: datetime) -> bool:
        d = local.date()
        if self.weekends and d.weekday() >= 5:
            return True
        if self.holidays and d in polish_holidays(d.year):
            return True
        h = local.hour
        return any((s <= h < e) if s < e else (h >= s or h < e) for s, e in self.windows)


@dataclass(frozen=True)
class Settings:
    tz: ZoneInfo
    tariff: Tariff
    capacity_kwh: float
    consumption_profile: tuple[float, ...]  # kWh per local hour 0..23
    min_soc: int
    max_grid_soc: int
    safety_margin_kwh: float
    efficiency: float
    percentile: str
    lead_minutes: int
    dry_run: bool

    @classmethod
    def from_env(cls) -> "Settings":
        def env(name: str, default: str | None = None) -> str | None:
            return os.getenv(name) or default

        capacity = env("BATTERY_CAPACITY_KWH")
        if not capacity:
            raise RuntimeError("BATTERY_CAPACITY_KWH is required when AUTOMATION_ENABLED=1")

        profile_raw = env("AUTOMATION_CONSUMPTION_PROFILE")
        daily_raw = env("AUTOMATION_DAILY_CONSUMPTION_KWH")
        if profile_raw:
            profile = tuple(float(x) for x in profile_raw.split(","))
            if len(profile) != 24:
                raise RuntimeError("AUTOMATION_CONSUMPTION_PROFILE must have 24 comma-separated kWh values")
        elif daily_raw:
            profile = (float(daily_raw) / 24,) * 24
        else:
            raise RuntimeError(
                "Set AUTOMATION_DAILY_CONSUMPTION_KWH or AUTOMATION_CONSUMPTION_PROFILE when AUTOMATION_ENABLED=1"
            )

        percentile = env("AUTOMATION_FORECAST_PERCENTILE", "p50")
        if percentile not in ("p10", "p50", "p90"):
            raise RuntimeError("AUTOMATION_FORECAST_PERCENTILE must be p10, p50 or p90")

        min_soc = int(env("AUTOMATION_MIN_SOC", "15"))
        max_grid_soc = int(env("AUTOMATION_MAX_GRID_SOC", "100"))
        if not (10 <= min_soc <= max_grid_soc <= 100):
            raise RuntimeError("Require 10 <= AUTOMATION_MIN_SOC <= AUTOMATION_MAX_GRID_SOC <= 100")

        efficiency = float(env("AUTOMATION_EFFICIENCY", "0.9"))
        if not (0.5 <= efficiency <= 1.0):
            raise RuntimeError("AUTOMATION_EFFICIENCY must be between 0.5 and 1.0")

        return cls(
            tz=ZoneInfo(config.get_solar_timezone()),
            tariff=Tariff(
                windows=_parse_windows(env("TARIFF_OFFPEAK_WINDOWS", "22:00-06:00,13:00-15:00")),
                weekends=env("TARIFF_OFFPEAK_WEEKENDS", "1") == "1",
                holidays=env("TARIFF_OFFPEAK_HOLIDAYS", "1") == "1",
            ),
            capacity_kwh=float(capacity),
            consumption_profile=profile,
            min_soc=min_soc,
            max_grid_soc=max_grid_soc,
            safety_margin_kwh=float(env("AUTOMATION_SAFETY_MARGIN_KWH", "1.0")),
            efficiency=efficiency,
            percentile=percentile,
            lead_minutes=int(env("AUTOMATION_LEAD_MINUTES", "10")),
            # Safe default: log decisions only, never write to the inverter unless explicitly disabled
            dry_run=env("AUTOMATION_DRY_RUN", "1") != "0",
        )


@dataclass(frozen=True)
class Trigger:
    run_at: datetime  # when the planner runs (window start - lead)
    window_start: datetime
    window: tuple[int, int]


def next_trigger(settings: Settings, after: datetime) -> Trigger:
    """First planner run strictly after `after`: lead minutes before each daily off-peak window start."""
    local_day = after.astimezone(settings.tz).date()
    candidates = []
    for offset in range(0, 3):
        d = local_day + timedelta(days=offset)
        for window in settings.tariff.windows:
            start = datetime(d.year, d.month, d.day, window[0], tzinfo=settings.tz)
            run_at = start - timedelta(minutes=settings.lead_minutes)
            if run_at > after:
                candidates.append(Trigger(run_at, start, window))
    return min(candidates, key=lambda t: t.run_at)


def compute_plan(settings: Settings, trigger: Trigger, pv_by_hour: dict[datetime, float]) -> dict:
    """Decide the grid-charge target SOC for the off-peak window of `trigger`.

    The peak segment is the run of peak-price hours right after the window ends (empty when the
    window is followed by more off-peak time, e.g. Friday night before a weekend). The battery must
    start that segment with enough energy to cover the largest cumulative deficit (load - PV) inside
    it; PV surplus earlier in the segment offsets later deficits.
    """
    s, e = trigger.window
    window_end = trigger.window_start.replace(hour=e)
    if e <= s:
        window_end += timedelta(days=1)

    hours = []
    h = window_end
    while len(hours) < MAX_SEGMENT_HOURS and not settings.tariff.is_offpeak(h):
        hours.append(h)
        h += timedelta(hours=1)

    pv_total = load_total = running = peak_deficit = 0.0
    for hour in hours:
        pv = pv_by_hour.get(hour, 0.0)
        load = settings.consumption_profile[hour.hour]
        pv_total += pv
        load_total += load
        running += load - pv
        peak_deficit = max(peak_deficit, running)

    need = peak_deficit / settings.efficiency + settings.safety_margin_kwh if peak_deficit > 0 else 0.0
    target = settings.min_soc + math.ceil(need / settings.capacity_kwh * 100) if need > 0 else settings.min_soc
    target = min(settings.max_grid_soc, max(settings.min_soc, target))

    return {
        "runAt": trigger.run_at.isoformat(timespec="minutes"),
        "window": f"{s:02d}:00-{e:02d}:00",
        "windowStart": trigger.window_start.isoformat(timespec="minutes"),
        "windowEnd": window_end.isoformat(timespec="minutes"),
        "peakSegment": {
            "start": hours[0].isoformat(timespec="minutes") if hours else None,
            "end": (hours[-1] + timedelta(hours=1)).isoformat(timespec="minutes") if hours else None,
            "hours": len(hours),
            "pvForecast_kWh": round(pv_total, 2),
            "consumption_kWh": round(load_total, 2),
            "maxDeficit_kWh": round(peak_deficit, 2),
        },
        "percentile": settings.percentile,
        "needFromBattery_kWh": round(need, 2),
        "minSoc": settings.min_soc,
        "targetSoc": target,
        "gridCharge": target > settings.min_soc,
    }


@dataclass(frozen=True)
class ExportSettings:
    min_price_pln_kwh: float
    max_power_kw: float
    min_slot_kwh: float
    price_multiplier: float
    dry_run: bool

    @classmethod
    def from_env(cls) -> "ExportSettings":
        def env(name: str, default: str | None = None) -> str | None:
            return os.getenv(name) or default

        max_power = env("EXPORT_MAX_POWER_KW")
        if not max_power:
            raise RuntimeError("EXPORT_MAX_POWER_KW is required when EXPORT_ENABLED=1")
        if env("EXPORT_DRY_RUN", "1") == "0":
            # The SolaX push-power command semantics (sign/units of batteryPower, nextMotion) are not
            # verified yet, so the planner may only log decisions.
            raise RuntimeError("EXPORT_DRY_RUN=0 is not supported yet: battery export commands are not implemented")
        return cls(
            min_price_pln_kwh=float(env("EXPORT_MIN_PRICE_PLN_KWH", "1.0")),
            max_power_kw=float(max_power),
            min_slot_kwh=float(env("EXPORT_MIN_SLOT_KWH", "0.2")),
            price_multiplier=float(env("EXPORT_PRICE_MULTIPLIER", "1.0")),
            dry_run=True,
        )


def _horizon_end(settings: Settings, now: datetime) -> datetime:
    """Next daily off-peak window start: the next point where the charge planner refills the battery."""
    return next_trigger(settings, now).window_start


def _reserve_need(settings: Settings, start: datetime, end: datetime, pv_by_hour: dict[datetime, float]) -> float:
    """Energy (kWh) the battery must keep for the house between start and end (max cumulative deficit)."""
    hour = start.astimezone(settings.tz).replace(minute=0, second=0, microsecond=0)
    running = peak = 0.0
    while hour < end:
        running += settings.consumption_profile[hour.hour] - pv_by_hour.get(hour, 0.0)
        peak = max(peak, running)
        hour += timedelta(hours=1)
    return peak / settings.efficiency + settings.safety_margin_kwh if peak > 0 else 0.0


def compute_export_plan(
    settings: Settings,
    export: ExportSettings,
    now: datetime,
    soc_percent: float,
    pv_by_hour: dict[datetime, float],
    prices: list[PriceSlot],
) -> dict:
    """Pick the most expensive 15-min slots until the next off-peak window to sell surplus battery energy.

    Surplus = energy above the SOC reserve minus what the house needs until the next window (so selling
    never forces buying back at peak price). Surplus is allocated greedily to the highest-price slots at
    or above the threshold, limited by max export power per slot.
    """
    horizon = _horizon_end(settings, now)
    reserve = _reserve_need(settings, now, horizon, pv_by_hour)
    stored = max(0.0, (soc_percent - settings.min_soc) / 100 * settings.capacity_kwh)
    surplus = max(0.0, stored - reserve)

    candidates = [
        s for s in prices
        if s.end > now and s.start < horizon and s.price_pln_kwh * export.price_multiplier >= export.min_price_pln_kwh
    ]
    remaining = surplus
    per_slot_max = export.max_power_kw * 0.25
    chosen = []
    for slot in sorted(candidates, key=lambda s: s.price_pln_kwh, reverse=True):
        energy = min(per_slot_max, remaining)
        if energy < export.min_slot_kwh:
            break
        chosen.append((slot, energy))
        remaining -= energy

    current = next(((s, e) for s, e in chosen if s.start <= now < s.end), None)
    tz = settings.tz

    def r(x: float) -> float:
        return round(x, 2)

    return {
        "now": now.astimezone(tz).isoformat(timespec="minutes"),
        "horizonEnd": horizon.isoformat(timespec="minutes"),
        "socPercent": soc_percent,
        "storedAboveMinSoc_kWh": r(stored),
        "reserveForHouse_kWh": r(reserve),
        "surplus_kWh": r(surplus),
        "minPrice_PLN_kWh": export.min_price_pln_kwh,
        "slots": [
            {
                "start": s.start.astimezone(tz).isoformat(timespec="minutes"),
                "end": s.end.astimezone(tz).isoformat(timespec="minutes"),
                "price_PLN_kWh": r(s.price_pln_kwh * export.price_multiplier),
                "energy_kWh": r(e),
                "power_kW": r(e / 0.25),
                "revenue_PLN": r(e * s.price_pln_kwh * export.price_multiplier),
            }
            for s, e in sorted(chosen, key=lambda c: c[0].start)
        ],
        "plannedExport_kWh": r(surplus - remaining),
        "currentSlot": (
            {"export": True, "power_kW": r(current[1] / 0.25), "price_PLN_kWh": r(current[0].price_pln_kwh * export.price_multiplier)}
            if current
            else {"export": False}
        ),
        "dryRun": export.dry_run,
    }


def _pv_by_hour(forecast: dict, percentile: str) -> dict[datetime, float]:
    # Hourly entries are average kW over the hour == kWh in that hour
    return {datetime.fromisoformat(h["time"]): h["power_kW"][percentile] for h in forecast["hourly"]}


class AutomationScheduler:
    """Background task that runs the planner before each off-peak window."""

    def __init__(self, settings: Settings, export: ExportSettings | None = None):
        self.settings = settings
        self.export = export
        self.last_result: dict | None = None
        self.last_export: dict | None = None
        self.next_run: Trigger | None = None
        self._task: asyncio.Task | None = None
        self._export_task: asyncio.Task | None = None

    async def run(self, trigger: Trigger, apply: bool) -> dict:
        """Compute a plan for `trigger` and optionally write it to the inverter."""
        forecast = await get_solar_forecast(hours=48)
        plan = compute_plan(self.settings, trigger, _pv_by_hour(forecast, self.settings.percentile))
        plan["forecastFetchedAt"] = forecast["fetchedAt"]
        plan["forecastStale"] = forecast["stale"]
        plan["dryRun"] = self.settings.dry_run
        plan["applied"] = False

        if apply:
            s, e = trigger.window
            await set_battery_self_use_mode_impl(
                min_soc=plan["minSoc"],
                charge_upper_soc=plan["targetSoc"],
                charge_from_grid_enable=int(plan["gridCharge"]),
                charge_start_time_period1=f"{s:02d}:00",
                charge_end_time_period1=f"{e:02d}:00",
            )
            plan["applied"] = True
        return plan

    async def export_plan(self, now: datetime | None = None) -> dict:
        """Compute the export plan from current SOC, PV forecast and RCE prices (never writes)."""
        if not self.export:
            raise RuntimeError("Export planner is not enabled")
        now = now or datetime.now(timezone.utc)
        realtime = await get_realtime_data_impl(None)
        battery = realtime.get("battery") or {}
        soc = battery.get("soc_percent")
        if soc is None:
            raise ValueError("Battery SOC not available from SolaX realtime data")
        forecast = await get_solar_forecast(hours=48)
        prices = await get_rce_prices()
        return compute_export_plan(
            self.settings, self.export, now, float(soc), _pv_by_hour(forecast, self.settings.percentile), prices
        )

    async def _export_loop(self) -> None:
        while True:
            # Run just after each 15-minute slot boundary
            now = datetime.now(timezone.utc)
            next_slot = now.replace(second=0, microsecond=0) + timedelta(minutes=15 - now.minute % 15)
            await asyncio.sleep((next_slot - now).total_seconds() + 5)
            try:
                plan = await self.export_plan()
                if plan["currentSlot"]["export"]:
                    logger.info("Export plan (dry run): would export %s", plan["currentSlot"])
                result = plan
            except Exception as e:
                logger.exception("Export planner run failed")
                result = {"error": f"{type(e).__name__} (details in server logs)"}
            self.last_export = result

    async def preview(self) -> dict:
        trigger = next_trigger(self.settings, datetime.now(timezone.utc))
        return await self.run(trigger, apply=False)

    async def _loop(self) -> None:
        after = datetime.now(timezone.utc)
        while True:
            trigger = next_trigger(self.settings, after)
            self.next_run = trigger
            delay = (trigger.run_at - datetime.now(timezone.utc)).total_seconds()
            await asyncio.sleep(max(0.0, delay))
            after = trigger.run_at
            try:
                result = await self.run(trigger, apply=not self.settings.dry_run)
                logger.info("Battery plan: %s", result)
            except Exception as e:  # never let one failed run stop the scheduler
                # Keep the inverter's previous settings when the forecast or SolaX call fails
                logger.exception("Battery automation run failed")
                result = {
                    "runAt": trigger.run_at.isoformat(timespec="minutes"),
                    "error": f"{type(e).__name__} (details in server logs)",
                    "applied": False,
                }
            self.last_result = result

    def start(self) -> None:
        self._task = asyncio.create_task(self._loop())
        if self.export:
            self._export_task = asyncio.create_task(self._export_loop())

    async def stop(self) -> None:
        for task in (self._task, self._export_task):
            if task:
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass

    def status(self) -> dict:
        return {
            "enabled": True,
            "dryRun": self.settings.dry_run,
            "nextRun": self.next_run.run_at.isoformat(timespec="minutes") if self.next_run else None,
            "nextWindow": f"{self.next_run.window[0]:02d}:00-{self.next_run.window[1]:02d}:00" if self.next_run else None,
            "lastResult": self.last_result,
            "export": (
                {"enabled": True, "dryRun": self.export.dry_run, "lastPlan": self.last_export}
                if self.export
                else {"enabled": False}
            ),
        }
