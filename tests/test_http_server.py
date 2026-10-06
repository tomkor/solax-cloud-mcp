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


def test_forecast_endpoint_requires_auth(api):
    assert api.get("/api/solar-forecast").status_code == 403


def test_forecast_endpoint_not_configured(api, monkeypatch):
    monkeypatch.delenv("SOLCAST_API_KEY", raising=False)
    assert api.get("/api/solar-forecast", headers=AUTH).status_code == 503


def test_forecast_endpoint_hides_upstream_error(api, monkeypatch):
    from solax_cloud_mcp.forecast import SolcastError

    monkeypatch.setenv("SOLCAST_API_KEY", "k")
    monkeypatch.setenv("SOLCAST_RESOURCE_IDS", "site-a")

    async def failing(hours):
        raise SolcastError("HTTP 500: <solcast body>")

    monkeypatch.setattr(server, "get_solar_forecast", failing)
    resp = api.get("/api/solar-forecast", headers=AUTH)
    assert resp.status_code == 502
    assert "solcast body" not in resp.text


def test_forecast_endpoint_validates_hours(api, monkeypatch):
    monkeypatch.setenv("SOLCAST_API_KEY", "k")
    monkeypatch.setenv("SOLCAST_RESOURCE_IDS", "site-a")
    assert api.get("/api/solar-forecast?hours=500", headers=AUTH).status_code == 422


async def test_forecast_tool_registered_only_when_configured(monkeypatch):
    monkeypatch.delenv("SOLCAST_API_KEY", raising=False)
    assert "get_solar_forecast" not in await _tool_names()
    monkeypatch.setenv("SOLCAST_API_KEY", "k")
    monkeypatch.setenv("SOLCAST_RESOURCE_IDS", "site-a")
    assert "get_solar_forecast" in await _tool_names()
    monkeypatch.delenv("SOLCAST_API_KEY")
    importlib.reload(server)


def test_automation_status_disabled(api, monkeypatch):
    assert api.get("/api/automation", headers=AUTH).json() == {"enabled": False}
    assert api.post("/api/automation/preview", headers=AUTH).status_code == 503
    assert api.get("/api/automation").status_code == 403


def test_automation_enabled_requires_solcast(monkeypatch):
    monkeypatch.setenv("HTTP_API_KEY", API_KEY)
    monkeypatch.setenv("AUTOMATION_ENABLED", "1")
    monkeypatch.delenv("SOLCAST_API_KEY", raising=False)
    with pytest.raises(RuntimeError, match="SOLCAST"):
        http_server.create_app()


def test_automation_status_enabled_runs_scheduler(monkeypatch):
    monkeypatch.setenv("HTTP_API_KEY", API_KEY)
    monkeypatch.setenv("AUTOMATION_ENABLED", "1")
    monkeypatch.setenv("SOLCAST_API_KEY", "k")
    monkeypatch.setenv("SOLCAST_RESOURCE_IDS", "site-a")
    monkeypatch.setenv("SOLAX_DEVICE_SN", "SN1")
    monkeypatch.setenv("BATTERY_CAPACITY_KWH", "21.2")
    monkeypatch.setenv("AUTOMATION_DAILY_CONSUMPTION_KWH", "12")
    with TestClient(http_server.create_app()) as client:  # context manager runs lifespan
        body = client.get("/api/automation", headers=AUTH).json()
    assert body["enabled"] is True
    assert body["dryRun"] is True
    assert body["nextRun"] is not None


def test_prices_endpoint(api, monkeypatch):
    from datetime import datetime, timedelta, timezone

    from solax_cloud_mcp.prices import PriceSlot

    start = datetime.now(timezone.utc).replace(second=0, microsecond=0)

    async def fake_prices():
        return [PriceSlot(start=start, end=start + timedelta(minutes=15), price_pln_kwh=1.1)]

    monkeypatch.setattr(server, "get_rce_prices", fake_prices)
    body = api.get("/api/prices?hours=2", headers=AUTH).json()
    assert body["max"] == 1.1
    assert len(body["slots"]) == 1
    assert api.get("/api/prices").status_code == 403


def test_export_preview_disabled(api):
    assert api.post("/api/export/preview", headers=AUTH).status_code == 503


def test_export_requires_automation(monkeypatch):
    monkeypatch.setenv("HTTP_API_KEY", API_KEY)
    monkeypatch.setenv("EXPORT_ENABLED", "1")
    monkeypatch.delenv("AUTOMATION_ENABLED", raising=False)
    with pytest.raises(RuntimeError, match="AUTOMATION_ENABLED"):
        http_server.create_app()


@pytest.fixture
def vpp(monkeypatch):
    """Capture remote-control commands instead of hitting the SolaX API."""
    recorded = {"push": [], "exit": [], "push_error": None}

    async def fake_push(device_sn, discharge_w, stop_soc):
        recorded["push"].append((device_sn, discharge_w, stop_soc))
        if recorded["push_error"]:
            raise recorded["push_error"]
        return {"code": 10000, "result": {device_sn: {"status": 3}}}

    async def fake_exit(device_sn):
        recorded["exit"].append(device_sn)
        return {"code": 10000}

    monkeypatch.setenv("SOLAX_DEVICE_SN", "SN1")
    monkeypatch.setattr(http_server, "discharge_to_soc", fake_push)
    monkeypatch.setattr(http_server, "exit_vpp_mode", fake_exit)
    return recorded


CONFIRM = {**AUTH, "X-Confirm": "yes"}


def test_export_test_requires_confirm_header(api, vpp):
    resp = api.post("/api/battery/export-test", json={"power_kw": 1, "minutes": 1, "stop_soc": 80}, headers=AUTH)
    assert resp.status_code == 400
    assert vpp["push"] == []


@pytest.mark.parametrize("body", [
        {"power_kw": 2.5, "minutes": 1, "stop_soc": 80},
        {"power_kw": 1, "minutes": 6, "stop_soc": 80},
        {"power_kw": 0, "minutes": 1, "stop_soc": 80},
        {"power_kw": 1, "minutes": 1, "stop_soc": 29},
        {"power_kw": 1, "minutes": 1},
    ])
def test_export_test_enforces_hard_caps(api, vpp, body):
    assert api.post("/api/battery/export-test", json=body, headers=CONFIRM).status_code == 422
    assert vpp["push"] == []


def test_export_test_sends_capped_discharge(api, vpp):
    resp = api.post("/api/battery/export-test", json={"power_kw": 1.5, "minutes": 2, "stop_soc": 85}, headers=CONFIRM)
    assert resp.status_code == 200
    assert vpp["push"] == [("SN1", 1500, 85)]
    assert resp.json()["durationSeconds"] == 120


def test_export_test_failure_restores_normal_mode(api, vpp):
    vpp["push_error"] = SolaxApiError("boom")
    resp = api.post("/api/battery/export-test", json={"power_kw": 1, "minutes": 1, "stop_soc": 80}, headers=CONFIRM)
    assert resp.status_code == 502
    assert "boom" not in resp.text
    assert vpp["exit"] == ["SN1"]


def test_export_stop_exits_remote_control(api, vpp):
    assert api.post("/api/battery/export-stop", headers=AUTH).json() == {"stopped": True}
    assert vpp["exit"] == ["SN1"]


def test_dashboard_page_is_public(api):
    resp = api.get("/dashboard")
    assert resp.status_code == 200
    assert "text/html" in resp.headers["content-type"]


def test_dashboard_settings_disabled(api):
    assert api.get("/api/dashboard/settings").status_code == 403
    assert api.get("/api/dashboard/settings", headers=AUTH).json() == {"enabled": False}


def test_dashboard_settings_enabled(monkeypatch):
    monkeypatch.setenv("HTTP_API_KEY", API_KEY)
    monkeypatch.setenv("AUTOMATION_ENABLED", "1")
    monkeypatch.setenv("SOLCAST_API_KEY", "k")
    monkeypatch.setenv("SOLCAST_RESOURCE_IDS", "site-a")
    monkeypatch.setenv("SOLAX_DEVICE_SN", "SN1")
    monkeypatch.setenv("BATTERY_CAPACITY_KWH", "21.2")
    monkeypatch.setenv("AUTOMATION_DAILY_CONSUMPTION_KWH", "12")
    body = TestClient(http_server.create_app()).get("/api/dashboard/settings", headers=AUTH).json()
    assert body["enabled"] is True
    assert len(body["upcomingRuns"]) == 6
    assert body["automation"]["capacity_kwh"] == 21.2
    assert isinstance(body["automation"]["tz"], str)
    assert body["export"] is None
