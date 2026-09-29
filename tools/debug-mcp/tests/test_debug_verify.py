"""debug_verify: feature pipeline ladder, first broken link (todo T6).

Rungs cite real routes (CreatureEndpoints.cs:45/51/58/157,
UniqueActorEndpoints.cs:19, DebugEndpoints.cs:536). Anti-cheat: a fabricated
subject fails at the subject rung and the ladder stops there.
"""
from tools import debug_verify

IID = "11111111-2222-3333-4444-555555555555"
SPECIMEN = {"profile": {"instanceId": IID}}


def _transport(routes):
    def send(method, url, body, params=None):
        for prefix, payload in routes:
            if url.endswith(prefix):
                status, body = payload
                return {"status": status, "body": body}
        raise AssertionError(f"unexpected call {method} {url}")
    return send


def _healthy():
    return _transport([
        ("/api/creatures/1", (200, {"items": [SPECIMEN]})),
        ("/api/creatures/1/codex", (200, {"entries": [{"id": "x"}]})),
        ("/api/creatures/1/summon-state",
         (200, {"balance": {"balance": 100}})),
        (f"/api/creatures/debug/atoms-preview/{IID}", (200, {"atoms": []})),
    ])


def test_healthy_summon_reports_all_rungs_green_with_citations():
    out = debug_verify.verify("summon", IID, transport=_healthy())
    assert out["ok"] is True
    assert out["broken_rung"] is None
    assert len(out["rungs"]) == 4
    assert all(r["verdict"] == "PASS" for r in out["rungs"])
    assert all(r["file"] is not None for r in out["rungs"])


def test_missing_roster_row_breaks_first_rung():
    send = _transport([
        ("/api/creatures/1", (200, {"items": []})),
        ("/api/creatures/1/codex", (200, {"entries": [{"id": "x"}]})),
        ("/api/creatures/1/summon-state",
         (200, {"balance": {"balance": 100}})),
        (f"/api/creatures/debug/atoms-preview/{IID}", (200, {"atoms": []})),
    ])
    out = debug_verify.verify("summon", IID, transport=send)
    assert out["ok"] is False
    assert out["broken_rung"] == "roster-row"
    assert len(out["rungs"]) == 1


def test_refused_fetch_is_a_fail_rung_not_a_crash():
    send = _transport([
        ("/api/creatures/1", (200, {"items": [SPECIMEN]})),
        ("/api/creatures/1/codex", (404, {"error": "no codex"})),
        ("/api/creatures/1/summon-state",
         (200, {"balance": {"balance": 100}})),
        (f"/api/creatures/debug/atoms-preview/{IID}", (200, {"atoms": []})),
    ])
    out = debug_verify.verify("summon", IID, transport=send)
    assert out["ok"] is False
    assert out["broken_rung"] == "codex"
    assert len(out["rungs"]) == 2
    assert out["rungs"][0]["verdict"] == "PASS"  # ladder stops at the first break


def test_fabricated_subject_fails_at_subject_rung():
    send = _transport([
        ("/api/unique/actors/bogus", (404, {"error": "no such specimen"})),
    ])
    out = debug_verify.verify("actor", "bogus", transport=send)
    assert out["ok"] is False
    assert out["broken_rung"] == "subject"
    assert len(out["rungs"]) == 1


def test_unknown_feature_refused():
    try:
        debug_verify.verify("teleport", IID, transport=_healthy())
        raise AssertionError("unknown feature must be refused")
    except ValueError:
        pass
