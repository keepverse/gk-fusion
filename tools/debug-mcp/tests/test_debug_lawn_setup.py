"""debug_lawn_setup: one-call live-board setup (todo T10).

Adapter over POST /api/debug/lawn/quick-start. Environment problems come
back as NOT-READY data (ready=False + fix command), never tracebacks; only
caller misuse raises. Never spawns test subjects; setup only.
"""
import httpx

from tools import debug_lawn_setup as lawn


def _ok_stub(method, url, body, params=None):
    assert url.endswith("/api/debug/lawn/quick-start")
    assert body["scenario"] == "lab-overlay"
    return {"status": 200, "body": {"ok": True, "entered": True,
                                   "levelType": "adventure",
                                   "targetPtr": "0xabc",
                                   "plantPtr": "0xdef",
                                   "liveEntities": {"plantCount": 1, "zombieCount": 1,
                                                    "liveState": "InMatch",
                                                    "phaseMismatch": False}}}


def test_healthy_board_returns_ptrs():
    out = lawn.setup(transport=_ok_stub)
    assert out["ready"] is True
    assert out["targetPtr"] == "0xabc"
    assert out["plantPtr"] == "0xdef"
    assert out["scope"] == "game-injector-debug"


def test_healthy_board_passes_through_live_entities():
    # The server folds a real debug.game-state read into quick-start's own response so a
    # caller sees actual plant/zombie counts without a separate debug_game_state round trip
    # (2026-09-15: every live probe this session needed that as an extra call because
    # entered:true/ready:true alone could not tell a stale seed-picker from a live board).
    out = lawn.setup(transport=_ok_stub)
    assert out["liveEntities"] == {"plantCount": 1, "zombieCount": 1,
                                    "liveState": "InMatch", "phaseMismatch": False}


def test_missing_live_entities_reads_as_none_not_a_failure():
    def send(method, url, body, params=None):
        return {"status": 200, "body": {"ok": True, "entered": True,
                                        "levelType": "adventure",
                                        "targetPtr": "0xabc", "plantPtr": "0xdef"}}
        # no liveEntities key at all -- the server-side game-state read can time out/fail
        # without failing the whole quick-start; the adapter must degrade to None, not KeyError.

    out = lawn.setup(transport=send)
    assert out["ready"] is True
    assert out["liveEntities"] is None


def test_injector_down_returns_not_ready_with_fix():
    def send(method, url, body, params=None):
        return {"status": 409,
                "body": {"ok": False, "error": "injector not connected"}}

    out = lawn.setup(transport=send)
    assert out["ready"] is False
    assert "game" in out["fix"].lower()


def test_server_down_returns_not_ready_naming_start():
    def send(method, url, body, params=None):
        raise httpx.ConnectError("refused")

    out = lawn.setup(transport=send)
    assert out["ready"] is False
    assert "FusionRpg.Server" in out["fix"]


def test_timeout_names_timeout_not_unreachable():
    def send(method, url, body, params=None):
        raise httpx.TimeoutException("timed out")

    out = lawn.setup(transport=send)
    assert out["ready"] is False
    assert "timed out" in out["error"]
    assert "unreachable" not in out["error"]


def test_bad_level_type_refusal_surfaces_verbatim():
    def send(method, url, body, params=None):
        return {"status": 409,
                "body": {"ok": False,
                         "error": "refusing lab on levelType=Explore"}}

    out = lawn.setup(transport=send)
    assert out["ready"] is False
    assert "Explore" in out["error"]
