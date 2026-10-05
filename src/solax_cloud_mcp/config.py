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
