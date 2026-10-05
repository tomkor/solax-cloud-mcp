"""Tests for configuration and environment handling."""

import os
import pytest

from solax_cloud_mcp.config import (
    get_client_id,
    get_client_secret,
    get_default_device_sn,
)


def test_get_client_id_success(monkeypatch):
    """Test that get_client_id returns the ID when set."""
    monkeypatch.setenv("SOLAX_CLIENT_ID", "test-client-id-12345")
    assert get_client_id() == "test-client-id-12345"


def test_get_client_id_missing(monkeypatch):
    """Test that get_client_id raises RuntimeError when not set."""
    monkeypatch.delenv("SOLAX_CLIENT_ID", raising=False)
    with pytest.raises(RuntimeError) as exc_info:
        get_client_id()
    assert "SOLAX_CLIENT_ID" in str(exc_info.value)


def test_get_client_secret_success(monkeypatch):
    """Test that get_client_secret returns the secret when set."""
    monkeypatch.setenv("SOLAX_CLIENT_SECRET", "test-client-secret-12345")
    assert get_client_secret() == "test-client-secret-12345"


def test_get_client_secret_missing(monkeypatch):
    """Test that get_client_secret raises RuntimeError when not set."""
    monkeypatch.delenv("SOLAX_CLIENT_SECRET", raising=False)
    with pytest.raises(RuntimeError) as exc_info:
        get_client_secret()
    assert "SOLAX_CLIENT_SECRET" in str(exc_info.value)


def test_get_default_device_sn_success(monkeypatch):
    """Test that get_default_device_sn returns the value when set."""
    monkeypatch.setenv("SOLAX_DEVICE_SN", "X3ABCD0123")
    assert get_default_device_sn() == "X3ABCD0123"


def test_get_default_device_sn_missing(monkeypatch):
    """Test that get_default_device_sn returns None when not set."""
    monkeypatch.delenv("SOLAX_DEVICE_SN", raising=False)
    assert get_default_device_sn() is None


@pytest.mark.parametrize("value,expected", [(None, False), ("", False), ("0", False), ("no", False), ("1", True), ("true", True), ("YES", True)])
def test_is_write_enabled(monkeypatch, value, expected):
    from solax_cloud_mcp.config import is_write_enabled

    if value is None:
        monkeypatch.delenv("SOLAX_ALLOW_WRITE", raising=False)
    else:
        monkeypatch.setenv("SOLAX_ALLOW_WRITE", value)
    assert is_write_enabled() is expected
