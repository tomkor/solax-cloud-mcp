"""Tests for HTTP server auth, input validation and error mapping."""

import importlib
import os

import pytest
from fastapi.testclient import TestClient

# server.py validates OAuth2 credentials at import time
os.environ.setdefault("SOLAX_CLIENT_ID", "test-client-id")
os.environ.setdefault("SOLAX_CLIENT_SECRET", "test-client-secret")

from solax_cloud_mcp import http_server, server  # noqa: E402
from solax_cloud_mcp.client import SolaxApiError  # noqa: E402

API_KEY = "test-api-key"
AUTH = {"Authorization": f"Bearer {API_KEY}"}
VALID_BODY = {"device_sn": "SN1", "min_soc": 20, "charge_upper_soc": 80, "charge_from_grid_enable": 0}


@pytest.fixture
def calls(monkeypatch):
    """Capture set_self_use_mode calls instead of hitting the SolaX API."""
    recorded = []

    async def fake_set_self_use_mode(**kwargs):
        recorded.append(kwargs)
        return {"code": 10000}

    monkeypatch.setattr(server, "set_self_use_mode", fake_set_self_use_mode)
    return recorded


@pytest.fixture
def api(monkeypatch):
    monkeypatch.setenv("HTTP_API_KEY", API_KEY)
    return TestClient(http_server.create_app())


def test_health_is_public(api):
    assert api.get("/health").json() == {"status": "ok"}


@pytest.mark.parametrize("path", ["/docs", "/redoc", "/openapi.json"])
def test_api_docs_disabled(api, path):
    assert api.get(path).status_code == 404


@pytest.mark.parametrize(
    "headers",
    [{}, {"Authorization": "Bearer wrong"}, {"Authorization": f"Basic {API_KEY}"}, {"Authorization": API_KEY}],
)
def test_rejects_bad_auth_before_body_validation(api, calls, headers):
    # Invalid body on purpose: unauthenticated callers must get 403, not 422 with schema details
    resp = api.post("/api/battery/self-use-mode", json={}, headers=headers)
    assert resp.status_code == 403
    assert calls == []


def test_self_use_mode_requires_explicit_settings(api, calls):
    resp = api.post("/api/battery/self-use-mode", json={"device_sn": "SN1"}, headers=AUTH)
    assert resp.status_code == 422
    assert calls == []


def test_self_use_mode_passes_explicit_settings(api, calls):
    resp = api.post("/api/battery/self-use-mode", json=VALID_BODY, headers=AUTH)
    assert resp.status_code == 200
    assert calls[0]["min_soc"] == 20
    assert calls[0]["charge_upper_soc"] == 80
    assert calls[0]["charge_from_grid_enable"] == 0


@pytest.mark.parametrize("value", ["6:00", "24:00", "12:60", "12:00; rm", ""])
def test_self_use_mode_rejects_bad_time_format(api, calls, value):
    body = {**VALID_BODY, "charge_start_time_period1": value}
    resp = api.post("/api/battery/self-use-mode", json=body, headers=AUTH)
    assert resp.status_code == 422
    assert calls == []


def test_upstream_error_details_not_leaked(api, monkeypatch):
    async def failing_fetch(device_sn):
        raise SolaxApiError("HTTP 500: <internal upstream body>")

    monkeypatch.setattr(server, "fetch_realtime_data", failing_fetch)
    resp = api.post("/api/realtime-data", json={"device_sn": "SN1"}, headers=AUTH)
    assert resp.status_code == 502
    assert "internal upstream body" not in resp.text


def test_validation_error_detail_returned(api, calls):
    body = {**VALID_BODY, "min_soc": 90, "charge_upper_soc": 50}
    resp = api.post("/api/battery/self-use-mode", json=body, headers=AUTH)
    assert resp.status_code == 400
    assert "min_soc" in resp.json()["detail"]


def test_default_bind_host_is_loopback(monkeypatch):
    monkeypatch.setenv("HTTP_API_KEY", API_KEY)
    monkeypatch.delenv("HTTP_HOST", raising=False)
    captured = {}
    monkeypatch.setattr("uvicorn.run", lambda app, host, port: captured.update(host=host))
    http_server.main()
    assert captured["host"] == "127.0.0.1"


async def _tool_names():
    module = importlib.reload(server)
    return {t.name for t in await module.server.list_tools()}


async def test_write_tool_hidden_by_default(monkeypatch):
    monkeypatch.delenv("SOLAX_ALLOW_WRITE", raising=False)
    names = await _tool_names()
    assert "get_realtime_data" in names
    assert "set_battery_self_use_mode" not in names


async def test_write_tool_exposed_when_enabled(monkeypatch):
    monkeypatch.setenv("SOLAX_ALLOW_WRITE", "1")
    assert "set_battery_self_use_mode" in await _tool_names()
    monkeypatch.delenv("SOLAX_ALLOW_WRITE")
    importlib.reload(server)
