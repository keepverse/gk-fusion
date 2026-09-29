"""debug_call: one allowlisted route invoker, scope-stamped (todo T2).

Scope derivation mirrors guard-debug-scope semantics: any injector relay in
the handler span makes the route game-injector-debug-shaped; a persisted /
domain write with no relay makes it rpg-server-debug-shaped.
"""
import asyncio

import pytest

import registry
from tools import debug_call


def _routes():
    return registry.load_allowlist()


def test_relay_route_labels_injector():
    # DebugEndpoints.cs /lawn/quick-start relays via Send(hub/inbox, ...) — verified.
    routes = _routes()
    assert "/lawn/quick-start" in routes
    assert registry.scope_for("/lawn/quick-start", routes) == "game-injector-debug"


def test_store_route_labels_server():
    # /reforge-world touches store.* with no relay — verified lines 457+.
    routes = _routes()
    assert "/reforge-world" in routes
    assert registry.scope_for("/reforge-world", routes) == "rpg-server-debug"


def test_non_allowlisted_route_refused_without_touching_transport():
    calls = []

    def transport(method, url, body, params=None):
        calls.append((method, url, body))
        raise AssertionError("transport must not run for refused routes")

    with pytest.raises(ValueError, match="[Aa]llowlist"):
        debug_call.call("GET", "/api/nope", transport=transport)
    assert calls == []


def test_stub_transport_returns_body_plus_scope():
    def transport(method, url, body, params=None):
        assert method == "POST"
        assert url.endswith("/api/debug/lawn/quick-start")
        return {"status": 200, "body": {"ok": True}}

    out = debug_call.call("POST", "/lawn/quick-start", {"levelNumber": 1},
                          transport=transport)
    assert out["status"] == 200
    assert out["body"] == {"ok": True}
    assert out["scope"] == "game-injector-debug"
    assert out["route"] == "/lawn/quick-start"


def test_unclassified_route_serves_with_null_scope():
    # /effects/contract is a static read (no relay, no store write): the guard
    # rule leaves it unclassified, and so does the tool — served, flagged null.
    routes = _routes()
    assert "/effects/contract" in routes
    assert registry.scope_for("/effects/contract", routes) is None

    def transport(method, url, body, params=None):
        return {"status": 200, "body": {"contractVersion": 2}}

    out = debug_call.call("GET", "/effects/contract", transport=transport)
    assert out["status"] == 200
    assert out["scope"] is None


def test_server_registers_debug_call():
    from server import mcp
    tools = asyncio.run(mcp.list_tools())
    assert "debug_call" in [t.name for t in tools]


# --- server URL resolution (creative mode: a sub-program's own server on another loopback port) ---

def test_base_url_defaults_to_the_owners_server_when_unset():
    assert debug_call.base_url(env={}) == debug_call.BASE_URL


def test_base_url_follows_fusionrpg_server_url_on_loopback():
    assert debug_call.base_url(env={"FUSIONRPG_SERVER_URL": "http://127.0.0.1:5190/"}) == \
        "http://127.0.0.1:5190"


def test_base_url_refuses_a_non_loopback_host():
    with pytest.raises(ValueError, match="loopback"):
        debug_call.base_url(env={"FUSIONRPG_SERVER_URL": "http://10.0.0.5:5088"})


def test_call_sends_to_the_resolved_server(monkeypatch):
    monkeypatch.setenv("FUSIONRPG_SERVER_URL", "http://127.0.0.1:5190")
    seen = []

    def transport(method, url, body, params=None):
        seen.append(url)
        return {"status": 200, "body": {}}

    debug_call.call("GET", "/lawn/quick-start", transport=transport)
    assert seen and seen[0].startswith("http://127.0.0.1:5190/api/debug/")
