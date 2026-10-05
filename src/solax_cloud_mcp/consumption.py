"""Hourly house consumption profiles from SolaX inverter history.

House load per sample = inverter AC output - grid power (SolaX: + export, - import), the same
formula the export planner uses live. Prints AUTOMATION_CONSUMPTION_PROFILE (weekdays) and
AUTOMATION_WEEKEND_CONSUMPTION_PROFILE (weekends and Polish public holidays) for .env.

    python -m solax_cloud_mcp.consumption --since 2026-10-02 [--until 2026-10-20]
"""

import argparse
import asyncio
from collections import defaultdict
from datetime import date, datetime, timedelta
from zoneinfo import ZoneInfo

from . import config
from .automation import polish_holidays
from .client import fetch_history

MAX_QUERY = timedelta(hours=12)  # SolaX history API limit per request


def house_load_w(sample: dict) -> float | None:
    phases = [sample.get(f"acPower{i}") for i in (1, 2, 3)]
    grid = sample.get("gridPower")
    if grid is None or any(p is None for p in phases):
        return None
    return max(0.0, sum(phases) - grid)


def is_weekend(d: date) -> bool:
    return d.weekday() >= 5 or d in polish_holidays(d.year)


def hourly_profiles(samples: list[tuple[datetime, float]]) -> dict[str, dict]:
    """Average house load per local hour (W averaged over the hour == kWh), split by day type."""
    by_type: dict[str, dict[int, list[float]]] = {"weekday": defaultdict(list), "weekend": defaultdict(list)}
    days: dict[str, set[date]] = {"weekday": set(), "weekend": set()}
    for local, watts in samples:
        kind = "weekend" if is_weekend(local.date()) else "weekday"
        by_type[kind][local.hour].append(watts)
        days[kind].add(local.date())
    result = {}
    for kind, hours in by_type.items():
        if not days[kind]:
            continue
        if len(hours) < 24:
            raise ValueError(f"No {kind} samples for some hours; use a longer period")
        profile = [round(sum(hours[h]) / len(hours[h]) / 1000, 2) for h in range(24)]
        result[kind] = {"days": len(days[kind]), "daily_kWh": round(sum(profile), 1), "profile": profile}
    return result


async def collect(device_sn: str, start: datetime, end: datetime) -> list[tuple[datetime, float]]:
    samples = []
    t = start
    while t < end:
        chunk_end = min(t + MAX_QUERY, end)
        for s in await fetch_history(device_sn, int(t.timestamp() * 1000), int(chunk_end.timestamp() * 1000)):
            watts = house_load_w(s)
            if watts is None or not s.get("plantLocalTime"):
                continue
            local = datetime.fromisoformat(s["plantLocalTime"])
            # The API includes both ends; keep [t, chunk_end) so boundary samples count once
            if t.replace(tzinfo=None) <= local < chunk_end.replace(tzinfo=None):
                samples.append((local, watts))
        t = chunk_end
    return samples


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--since", required=True, type=date.fromisoformat, help="first full day (YYYY-MM-DD)")
    parser.add_argument("--until", type=date.fromisoformat, help="first day NOT included (default: today)")
    args = parser.parse_args()

    tz = ZoneInfo(config.get_solar_timezone())
    device_sn = config.get_default_device_sn()
    if not device_sn:
        raise SystemExit("SOLAX_DEVICE_SN is not set")
    until = args.until or datetime.now(tz).date()
    start = datetime.combine(args.since, datetime.min.time(), tz)
    end = datetime.combine(until, datetime.min.time(), tz)

    profiles = hourly_profiles(asyncio.run(collect(device_sn, start, end)))
    env = {"weekday": "AUTOMATION_CONSUMPTION_PROFILE", "weekend": "AUTOMATION_WEEKEND_CONSUMPTION_PROFILE"}
    for kind, p in profiles.items():
        print(f"# {kind}: {p['days']} day(s), {p['daily_kWh']} kWh/day")
        print(f"{env[kind]}={','.join(str(x) for x in p['profile'])}")


if __name__ == "__main__":
    main()
