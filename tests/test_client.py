"""Tests for the SolaX API client, focused on the 10402 retry path."""

import httpx
import pytest
import respx

from solax_cloud_mcp import auth, client


@pytest.fixture(autouse=True)
def reset_rate_limiter(monkeypatch):
    """Avoid inter-test rate-limit sleeps and state bleed via client's module globals."""
    monkeypatch.setattr(client, "_last_call_at", 0.0)
    monkeypatch.setattr(client, "MIN_INTERVAL_SECONDS", 0.0)


def _fake_token_source(tokens):
    """Return an async function that yields successive tokens on each call."""
    remaining = list(tokens)

    async def _get_access_token():
        return remaining.pop(0)

    return _get_access_token


@respx.mock
async def test_10402_triggers_invalidate_and_retries_on_same_client(monkeypatch):
    """Regression: the retry request used to run on an httpx.AsyncClient that the
    `async with` block had already closed, so retrying after a 10402 raised
    RuntimeError("Cannot send a request, as the client has been closed.") instead
    of succeeding. Both requests must complete when the client stays open."""
    monkeypatch.setattr(auth, "get_access_token", _fake_token_source(["expired-token", "fresh-token"]))
    invalidated = []
    monkeypatch.setattr(auth, "invalidate_token", lambda: invalidated.append(True))

    route = respx.get(client.REALTIME_DATA_URL)
    route.side_effect = [
        httpx.Response(200, json={"code": 10402, "message": "auth failed", "result": []}),
        httpx.Response(
            200,
            json={"code": 10000, "message": "ok", "result": [{"deviceSn": "X3ABCD0123"}]},
        ),
    ]

    result = await client.fetch_inverter_data("X3ABCD0123")

    assert result == {"deviceSn": "X3ABCD0123"}
    assert invalidated == [True]
    assert route.call_count == 2


@respx.mock
async def test_persistent_10402_after_retry_raises_api_error(monkeypatch):
    """If the retried request also comes back 10402, the caller should get a
    clear SolaxApiError rather than a client-closed RuntimeError."""
    monkeypatch.setattr(auth, "get_access_token", _fake_token_source(["expired-token", "still-bad-token"]))
    monkeypatch.setattr(auth, "invalidate_token", lambda: None)

    respx.get(client.REALTIME_DATA_URL).mock(
        return_value=httpx.Response(200, json={"code": 10402, "message": "auth failed", "result": []})
    )

    with pytest.raises(client.SolaxApiError, match="10402"):
        await client.fetch_inverter_data("X3ABCD0123")


@respx.mock
async def test_discharge_to_soc_sends_negative_power_and_stop_soc(monkeypatch):
    monkeypatch.setattr(auth, "get_access_token", _fake_token_source(["token"]))
    route = respx.post(client.SOC_TARGET_URL).mock(
        return_value=httpx.Response(200, json={"code": 10000, "result": {"SN1": {"status": 3}}})
    )

    await client.discharge_to_soc("SN1", 1500, 85)

    sent = route.calls[0].request.content.replace(b" ", b"")
    assert b'"chargeDischargPower":-1500' in sent
    assert b'"targetSoc":85' in sent


@respx.mock
@pytest.mark.parametrize("result", [{"SN1": {"status": 1}}, {"SN1": {"status": 2}}, {}, []])
async def test_command_not_delivered_raises(monkeypatch, result):
    """Code 10000 only means the platform accepted the request; an offline or failed device must raise."""
    monkeypatch.setattr(auth, "get_access_token", _fake_token_source(["token"]))
    respx.post(client.EXIT_VPP_URL).mock(return_value=httpx.Response(200, json={"code": 10000, "result": result}))

    with pytest.raises(client.SolaxApiError, match="not delivered"):
        await client.exit_vpp_mode("SN1")
