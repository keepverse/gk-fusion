"""debug_ui_nav: call one real UIMgr navigation method by name.

Adapter over POST /api/debug/ui-nav. Unknown actions refuse locally (no round trip); every other
outcome comes back as an envelope, never a traceback.
"""
import httpx
import pytest

import tools.debug_call as debug_call_module
from tools import debug_ui_nav as ui_nav


@pytest.fixture(autouse=True)
def _ui_nav_in_allowlist(monkeypatch):
    """/ui-nav is a route DebugEndpoints.cs may or may not carry in the checkout this test
    suite runs in (it can live only in a worktree ahead of merge) -- these tests exercise
    debug_ui_nav's own adapter logic, not the allowlist-generation step debug_call already
    covers, so the allowlist lookup is stubbed rather than depending on real repo state."""
    monkeypatch.setattr(debug_call_module.registry, "load_allowlist",
                        lambda root=None: {"/ui-nav": "game-injector-debug"})


def _ok_stub(method, url, body, params=None):
    assert url.endswith("/api/debug/ui-nav")
    assert body["action"] == "back-to-menu"
    return {"status": 200, "body": {"ok": True, "result": {"action": "back-to-menu", "ok": True}}}


def test_known_action_acks_ok():
    out = ui_nav.nav("back-to-menu", transport=_ok_stub)
    assert out["ok"] is True
    assert out["action"] == "back-to-menu"
    assert out["scope"] == "game-injector-debug"


def test_unknown_action_refuses_locally_no_transport_call():
    called = []

    def send(method, url, body, params=None):
        called.append(1)
        return {"status": 200, "body": {"ok": True}}

    with pytest.raises(ValueError, match="unknown ui-nav action"):
        ui_nav.nav("teleport-to-victory", transport=send)
    assert called == []


def test_reason_forwarded_only_for_enter_lose_menu():
    seen = {}

    def send(method, url, body, params=None):
        seen.update(body)
        return {"status": 200, "body": {"ok": True, "result": {"ok": True}}}

    ui_nav.nav("enter-lose-menu", reason="test", transport=send)
    assert seen == {"action": "enter-lose-menu", "reason": "test"}

    seen.clear()
    ui_nav.nav("back-to-menu", reason="ignored", transport=send)
    assert seen == {"action": "back-to-menu"}


def test_injector_not_connected_returns_not_ready_with_fix():
    def send(method, url, body, params=None):
        return {"status": 409, "body": {"ok": False, "error": "injector not connected"}}

    out = ui_nav.nav("back-to-menu", transport=send)
    assert out["ok"] is False
    assert "game" in out["fix"].lower()


def test_server_down_returns_not_ready_naming_start():
    def send(method, url, body, params=None):
        raise httpx.ConnectError("refused")

    out = ui_nav.nav("back-to-menu", transport=send)
    assert out["ok"] is False
    assert "FusionRpg.Server" in out["fix"]


def test_timeout_names_timeout():
    def send(method, url, body, params=None):
        raise httpx.TimeoutException("timed out")

    out = ui_nav.nav("back-to-menu", transport=send)
    assert out["ok"] is False
    assert "timed out" in out["error"]


def test_injector_side_error_surfaces_verbatim():
    def send(method, url, body, params=None):
        return {"status": 200,
                "body": {"ok": True, "result": {"ok": False, "error": "board is not a valid target"}}}

    out = ui_nav.nav("back-to-game", transport=send)
    assert out["ok"] is False
    assert out["error"] == "board is not a valid target"
