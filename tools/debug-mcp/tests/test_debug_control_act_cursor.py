"""debug_act / debug_cursor: verb dispatch, receipts, and the opt-in rule."""
import pytest

from tools import debug_act as act
from tools import debug_cursor as cursor


def _mux(mapping):
    last_tag = None

    def send(method, url, body, params=None):
        nonlocal last_tag
        if method == "POST" and isinstance(body, dict) and body.get("tag"):
            last_tag = body["tag"]
        for suffix, response in mapping.items():
            if url.endswith(suffix):
                if suffix == "/api/debug/events" and last_tag:
                    if last_tag:
                        response = dict(response)
                        response["items"] = [
                            {**item, "payload": {**item.get("payload", {}), "tag": last_tag}}
                            for item in response.get("items", [])
                        ]
                return {"status": 200, "body": response,
                        "scope": "game-injector-debug", "route": suffix}
        raise AssertionError(f"unexpected route: {url}")
    return send


def test_act_place_returns_receipt():
    send = _mux({
        "/api/debug/act": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 9, "kind": "debug.act.done",
             "payload": {"verb": "place", "typeId": 3,
                         "snapshot": {"snapshotId": "s1"}}}]},
    })
    out = act.act("place", typeId=3, col=2, row=1, transport=send,
                  sleep=lambda s: None)
    assert out["ok"] is True
    assert out["receipt"]["typeId"] == 3


def test_act_place_receipt_failure_is_not_ok():
    # lawn-combat-wire L-N30: the injector said nothing was planted -- the tool must not report success.
    send = _mux({
        "/api/debug/act": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 11, "kind": "debug.act.done",
             "payload": {"verb": "place", "typeId": 0, "ok": False, "placed": False,
                         "error": "no new plant of that type in the target cell",
                         "snapshot": {"snapshotId": "s2"}}}]},
    })
    out = act.act("place", typeId=0, col=1, row=2, transport=send,
                  sleep=lambda s: None)
    assert out["ok"] is False
    assert "no new plant" in out["error"]
    assert out["receipt"]["placed"] is False


def test_act_unknown_verb_raises():
    with pytest.raises(ValueError):
        act.act("dance", transport=_mux({}))


def test_cursor_moves_with_opt_in():
    send = _mux({
        "/api/debug/cursor": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 10, "kind": "debug.cursor.done",
             "payload": {"x": 100, "y": 100, "clicked": False}}]},
    })
    out = cursor.cursor(100, 100, confirmed=True, transport=send,
                        sleep=lambda s: None)
    assert out["ok"] is True
    assert (out["x"], out["y"]) == (100, 100)
    assert out["clicked"] is False


def test_cursor_without_opt_in_raises():
    with pytest.raises(ValueError):
        cursor.cursor(100, 100, confirmed=False)


def test_cursor_bad_coords_raise():
    with pytest.raises(ValueError):
        cursor.cursor(-1, 100, confirmed=True)
