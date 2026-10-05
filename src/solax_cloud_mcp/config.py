"""Configuration and environment variable handling."""

import os


def get_client_id() -> str:
    """Get the SolaX Developer Platform OAuth2 client ID from environment.

    Raises:
        RuntimeError: if SOLAX_CLIENT_ID is not set.
    """
    client_id = os.getenv("SOLAX_CLIENT_ID")
    if not client_id:
        raise RuntimeError(
            "SOLAX_CLIENT_ID environment variable not set. "
            "Register an OAuth2 application at https://developer.solaxcloud.com/ "
            "and obtain your Client ID from the Application page."
        )
    return client_id


def get_client_secret() -> str:
    """Get the SolaX Developer Platform OAuth2 client secret from environment.

    Raises:
        RuntimeError: if SOLAX_CLIENT_SECRET is not set.
    """
    client_secret = os.getenv("SOLAX_CLIENT_SECRET")
    if not client_secret:
        raise RuntimeError(
            "SOLAX_CLIENT_SECRET environment variable not set. "
            "Register an OAuth2 application at https://developer.solaxcloud.com/ "
            "and obtain your Client Secret from the Application page."
        )
    return client_secret


def get_default_device_sn() -> str | None:
    """Get the default device (inverter) serial number from environment.

    Returns:
        The SOLAX_DEVICE_SN value if set, None otherwise.

    Note:
        This is the inverter's serial number, NOT the old WiFi dongle registration
        number (wifiSn) used by the legacy SolaX Cloud API.
    """
    return os.getenv("SOLAX_DEVICE_SN")


def is_write_enabled() -> bool:
    """Whether the MCP server should expose tools that change inverter settings.

    Controlled by SOLAX_ALLOW_WRITE (1/true/yes). Disabled by default so an LLM
    (e.g. via prompt injection) cannot reconfigure the inverter unless opted in.
    """
    return os.getenv("SOLAX_ALLOW_WRITE", "").strip().lower() in ("1", "true", "yes")


def is_solcast_configured() -> bool:
    """Whether Solcast forecast integration is enabled (API key and at least one site set)."""
    return bool(os.getenv("SOLCAST_API_KEY")) and bool(get_solcast_resource_ids())


def get_solcast_api_key() -> str:
    """Get the Solcast API key from environment.

    Raises:
        RuntimeError: if SOLCAST_API_KEY is not set.
    """
    key = os.getenv("SOLCAST_API_KEY")
    if not key:
        raise RuntimeError(
            "SOLCAST_API_KEY environment variable not set. "
            "Create a (free hobbyist) account at https://toolkit.solcast.com.au/ and copy the API key."
        )
    return key


def get_solcast_resource_ids() -> list[str]:
    """Get Solcast rooftop site resource IDs (comma-separated, e.g. east and west arrays)."""
    raw = os.getenv("SOLCAST_RESOURCE_IDS", "")
    return [r.strip() for r in raw.split(",") if r.strip()]


def get_solcast_cache_minutes() -> int:
    """How long a Solcast response is reused before refetching (default 180 min).

    Hobbyist accounts allow ~10 API calls/day; each refresh costs one call per site.
    """
    return int(os.getenv("SOLCAST_CACHE_MINUTES") or "180")


def get_solar_timezone() -> str:
    """IANA timezone used for forecast day boundaries and timestamps (default UTC)."""
    return os.getenv("SOLAR_TIMEZONE") or "UTC"


def is_automation_enabled() -> bool:
    """Whether the forecast-driven battery planner runs inside the HTTP server (AUTOMATION_ENABLED=1)."""
    return os.getenv("AUTOMATION_ENABLED", "").strip().lower() in ("1", "true", "yes")


def is_export_enabled() -> bool:
    """Whether the price-driven battery export planner runs (EXPORT_ENABLED=1, requires automation)."""
    return os.getenv("EXPORT_ENABLED", "").strip().lower() in ("1", "true", "yes")
