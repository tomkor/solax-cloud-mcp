"""Polish market energy prices (RCE, 15-minute resolution) from the public PSE API."""

import asyncio
import logging
import time
from dataclasses import dataclass
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

import httpx

logger = logging.getLogger(__name__)

RCE_URL = "https://api.raporty.pse.pl/api/rce-pln"
PSE_TZ = ZoneInfo("Europe/Warsaw")  # PSE publishes local (Polish) time
CACHE_SECONDS = 30 * 60

_cache: tuple[float, list["PriceSlot"]] | None = None
_cache_lock = asyncio.Lock()


class PriceError(Exception):
    """Raised when RCE prices cannot be fetched and no cached data is available."""

    pass


@dataclass(frozen=True)
class PriceSlot:
    start: datetime
    end: datetime
    price_pln_kwh: float  # net RCE, PLN/kWh


def _parse_records(records: list[dict]) -> list[PriceSlot]:
    """Parse PSE records; `dtime` is the local END of the 15-min period, possibly "YYYY-MM-DD 24:00:00"."""
    slots = []
    prev_end: datetime | None = None
    for rec in sorted(records, key=lambda r: (r["business_date"], r["dtime"])):
        date_part, time_part = rec["dtime"].split(" ", 1)
        if time_part.startswith("24:"):
            naive = datetime.fromisoformat(f"{date_part} 00:00:00") + timedelta(days=1)
        else:
            naive = datetime.fromisoformat(rec["dtime"])
        end = naive.replace(tzinfo=PSE_TZ)
        # Autumn DST change repeats local times; the second occurrence is the later (fold=1) instant
        if prev_end is not None and end <= prev_end:
            end = naive.replace(tzinfo=PSE_TZ, fold=1)
        prev_end = end
        slots.append(PriceSlot(start=end - timedelta(minutes=15), end=end, price_pln_kwh=float(rec["rce_pln"]) / 1000))
    return slots


async def get_rce_prices(now: datetime | None = None) -> list[PriceSlot]:
    """RCE prices from today (local) onwards; tomorrow's appear once PSE publishes them (afternoon)."""
    global _cache
    async with _cache_lock:
        if _cache and time.time() - _cache[0] < CACHE_SECONDS:
            return _cache[1]

        today = (now or datetime.now(PSE_TZ)).astimezone(PSE_TZ).date().isoformat()
        try:
            async with httpx.AsyncClient(timeout=15.0) as client:
                response = await client.get(
                    RCE_URL,
                    params={
                        "$select": "dtime,period,rce_pln,business_date",
                        "$filter": f"business_date ge '{today}'",
                        "$first": 200,
                    },
                )
            if response.status_code != 200:
                raise PriceError(f"HTTP {response.status_code}: {response.text[:200]}")
            slots = _parse_records(response.json()["value"])
        except (httpx.RequestError, ValueError, KeyError, TypeError) as e:
            error = PriceError(f"Failed to fetch RCE prices: {e}")
            if _cache:
                logger.warning("%s; serving cached prices", error)
                return _cache[1]
            raise error from e
        except PriceError:
            if _cache:
                logger.warning("RCE fetch failed; serving cached prices", exc_info=True)
                return _cache[1]
            raise

        _cache = (time.time(), slots)
        return slots
