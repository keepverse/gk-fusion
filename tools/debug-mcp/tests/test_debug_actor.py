"""debug_actor: composed snapshot, instanceId XOR ptr (todo T5).

DB record (normal query path) + Hub audit (derived-audit-coverage, real
actor) + recent events mentioning the id + per-link verdicts. The hub section
is passed through verbatim — the tool never refolds actor numbers (ActorHub
gate N/A by consumption, proven here, not asserted).
"""
import json

import pytest

from tools import debug_actor

IID = "11111111-2222-3333-4444-555555555555"

RECORD = {"instanceId": IID, "phase": "Roster", "level": 5}
HUB = {"instanceId": IID, "appliedCombat": {"atk": 42}}


def _transport(routes):
    def send(method, url, body, params=None):
        for prefix, payload in routes:
            if url.endswith(prefix):
                return {"status": 200, "body": payload}
        raise AssertionError(f"unexpected call {method} {url}")
    return send


def test_exactly_one_id_required():
    with pytest.raises(ValueError, match="[Ee]xactly one"):
        debug_actor.snapshot()
    with pytest.raises(ValueError, match="[Ee]xactly one"):
        debug_actor.snapshot(instance_id=IID, ptr="0x1")


def test_unknown_instance_returns_typed_miss():
    def send(method, url, body, params=None):
        return {"status": 404, "body": {"error": "no such specimen"}}

    out = debug_actor.snapshot(instance_id="bogus", transport=send)
    assert out["found"] is False
    assert out["instanceId"] == "bogus"
    fails = [link for link in out["links"] if link["verdict"] == "FAIL"]
    assert any("db-record" in link["detail"] for link in fails)


def test_real_specimen_composes_db_hub_events():
    payload = json.dumps({"note": f"deployed {IID}"})
    send = _transport([
        (f"/api/unique/actors/{IID}", RECORD),
        ("/api/debug/derived-audit-coverage", HUB),
        ("/api/debug/events", {"items": [
            {"id": 7, "kind": "summon.pulled", "payload": payload},
            {"id": 8, "kind": "board.start", "payload": "{}"},
        ]}),
    ])

    out = debug_actor.snapshot(instance_id=IID, transport=send)
    assert out["found"] is True
    assert out["record"] == RECORD
    assert out["hub"] == HUB  # verbatim pass-through: no refold, ever
    assert [e["id"] for e in out["events"]] == [7]
    assert all(link["verdict"] == "PASS" for link in out["links"])


def test_ptr_path_resolves_through_bindings():
    snap = {"match": {"phase": "InMatch",
                      "bindings": [{"instanceId": IID, "ptr": "0xabc"}],
                      "entities": [{"ptr": "0xabc", "side": "zombie",
                                    "living": True}]}}
    send = _transport([
        ("/api/debug/snapshot", {"ok": True}),
        ("/api/debug/events", {"items": [
            {"id": 9, "kind": "debug.snapshot", "payload": snap}]}),
        (f"/api/unique/actors/{IID}", RECORD),
        ("/api/debug/derived-audit-coverage", HUB),
    ])

    out = debug_actor.snapshot(ptr="0xabc", transport=send)
    assert out["found"] is True
    assert out["instanceId"] == IID
    assert out["live"] == {"ptr": "0xabc", "side": "zombie", "living": True}
    assert out["hub"] == HUB
