"""debug_match: budgeted match digest (todo T4).

Snapshot hash + capped entity digests, never a full dump. No live board =
explicit not-live shape, not an error. (Decisions surface has no debug
route — verified by grep — so no decisions key is emitted, documented here
instead of guessed.)
"""
from tools import debug_match


def _snap_payload(phase="InMatch", key="mk1", ents=()):
    return {"match": {"phase": phase, "matchKey": key, "revision": 7,
                      "entities": list(ents)}}


def _ent(ptr, side="zombie", living=True):
    return {"ptr": ptr, "side": side, "living": living}


def test_idle_server_reports_not_live():
    def transport(method, url, body, params=None):
        if url.endswith("/snapshot"):
            return {"status": 200, "body": {"ok": True, "server": {}}}
        return {"status": 200, "body": {"items": []}}

    out = debug_match.digest(transport=transport)
    assert out["live"] is False
    assert out["entities"] == []
    assert out["snapshot_hash"] is None
    assert out["scope"] in ("game-injector-debug", "rpg-server-debug")


def test_snapshot_digests_entities_with_cap():
    ents = [_ent(f"0x{i}") for i in range(1, 6)]

    def transport(method, url, body, params=None):
        if url.endswith("/snapshot"):
            return {"status": 200, "body": {"ok": True}}
        return {"status": 200,
                "body": {"items": [{"id": 9, "kind": "debug.snapshot",
                                    "payload": _snap_payload(ents=ents)}]}}

    out = debug_match.digest(entity_limit=3, transport=transport)
    assert out["live"] is True
    assert out["phase"] == "InMatch"
    assert [e["ptr"] for e in out["entities"]] == ["0x1", "0x2", "0x3"]
    assert out["truncated"] is True
    assert out["snapshot_hash"] is not None


def test_snapshot_hash_stable_for_identical_payload():
    def transport(method, url, body, params=None):
        if url.endswith("/snapshot"):
            return {"status": 200, "body": {"ok": True}}
        return {"status": 200,
                "body": {"items": [{"id": 9, "kind": "debug.snapshot",
                                    "payload": _snap_payload()}]},
                }

    a = debug_match.digest(transport=transport)["snapshot_hash"]
    b = debug_match.digest(transport=transport)["snapshot_hash"]
    assert a == b and len(a) == 64
