"""debug_game_state: direct read of Board/InitBoard + real Unity entity counts.

Adapter over POST /api/debug/game-state. Never fabricates -- every field comes straight from the
injector's own live read.
"""
import httpx
import pytest

import tools.debug_call as debug_call_module
from tools import debug_game_state as game_state


@pytest.fixture(autouse=True)
def _game_state_in_allowlist(monkeypatch):
    """/game-state can live only in an unmerged worktree ahead of this checkout (see
    test_debug_ui_nav.py's identical fixture) -- these tests exercise debug_game_state's own
    adapter logic, not allowlist generation, so the lookup is stubbed."""
    monkeypatch.setattr(debug_call_module.registry, "load_allowlist",
                        lambda root=None: {"/game-state": "game-injector-debug"})


def _ok_stub(method, url, body, params=None):
    assert url.endswith("/api/debug/game-state")
    return {"status": 200, "body": {"ok": True, "live": {
        "ok": True, "hasBoard": True, "hasInitBoard": True, "theBoardTypeName": "Advanture",
        "sceneType": 0, "matchPhase": "InMatch", "phaseMismatch": False,
        "plantCount": 2, "zombieCount": 1, "liveState": "InMatch",
    }}}


def test_live_board_reports_real_counts():
    out = game_state.state(transport=_ok_stub)
    assert out["ok"] is True
    assert out["plantCount"] == 2
    assert out["zombieCount"] == 1
    assert out["liveState"] == "InMatch"
    assert out["phaseMismatch"] is False
    assert out["scope"] == "game-injector-debug"


def test_phase_mismatch_surfaces_verbatim():
    def send(method, url, body, params=None):
        return {"status": 200, "body": {"ok": True, "live": {
            "ok": True, "matchPhase": "InMatch", "phaseMismatch": True,
            "plantCount": 0, "zombieCount": 0, "liveState": "MatchEndedBoardStillAlive",
        }}}

    out = game_state.state(transport=send)
    assert out["phaseMismatch"] is True
    assert out["liveState"] == "MatchEndedBoardStillAlive"


def test_injector_not_connected_returns_not_ready_with_fix():
    def send(method, url, body, params=None):
        return {"status": 409, "body": {"ok": False, "error": "injector not connected"}}

    out = game_state.state(transport=send)
    assert out["ok"] is False
    assert "game" in out["fix"].lower()


def test_server_down_returns_not_ready_naming_start():
    def send(method, url, body, params=None):
        raise httpx.ConnectError("refused")

    out = game_state.state(transport=send)
    assert out["ok"] is False
    assert "FusionRpg.Server" in out["fix"]


def test_timeout_names_timeout():
    def send(method, url, body, params=None):
        raise httpx.TimeoutException("timed out")

    out = game_state.state(transport=send)
    assert out["ok"] is False
    assert "timed out" in out["error"]
