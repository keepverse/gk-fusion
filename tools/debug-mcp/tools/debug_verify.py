"""debug_verify: feature pipeline ladder, first broken link (todo T6).

Closed feature set (adding one amends the spec): actor (delegates to the
snapshot ladder) and summon (roster → codex → summon-state → atoms-preview,
all real query paths). The ladder stops at the first break; a fabricated
subject fails at the subject rung and never travels downstream (anti-cheat).
"""
import evidence
from tools.debug_actor import snapshot as actor_snapshot
from tools.debug_call import call as route_call

FEATURES = ("actor", "summon")

_CREATURES = "src/FusionRpg.Server/CreatureEndpoints.cs"
_DB_FILE = "src/FusionRpg.Server/UniqueActorEndpoints.cs:19"


def _profile_id(item):
    profile = item.get("profile", {}) if isinstance(item, dict) else {}
    return profile.get("instanceId")


def _verify_actor(instance_id, transport):
    out = actor_snapshot(instance_id=instance_id, transport=transport)
    if not out["found"]:
        rung = evidence.rung("FAIL", "subject: no such specimen",
                             file=_DB_FILE, expected="row", actual="404")
        return {"feature": "actor", "subject": instance_id, "ok": False,
                "broken_rung": "subject", "rungs": [rung],
                "scope": out["scope"]}
    broken = next((link for link in out["links"] if link["verdict"] == "FAIL"), None)
    return {"feature": "actor", "subject": instance_id,
            "ok": broken is None,
            "broken_rung": broken["detail"] if broken else None,
            "rungs": out["links"], "scope": out["scope"]}


def _get(path, transport, params=None):
    result = route_call("GET", path, params=params, transport=transport)
    if not result["ok"]:
        raise LookupError(f"{path} -> {result['status']}: {result['body']}")
    return result["body"]


def _verify_summon(instance_id, player_id=1, transport=None):
    """Summon ladder as a stage table: fetch, predicate, fail-shape per rung.

    Every stage treats a transport refusal the same as a failed predicate —
    no rung may crash the ladder (a 404 on codex is a FAIL rung, not a
    traceback).
    """
    stages = [
        ("roster-row", f"{_CREATURES}:45", f"/api/creatures/{player_id}",
         lambda body: any(_profile_id(i) == instance_id
                          for i in body.get("items", []))
         if isinstance(body, dict) else False,
         f"specimen {instance_id}", "absent"),
        ("codex", f"{_CREATURES}:51", f"/api/creatures/{player_id}/codex",
         lambda body: bool(body.get("entries", []))
         if isinstance(body, dict) else False,
         "entries", "empty"),
        ("summon-state", f"{_CREATURES}:58",
         f"/api/creatures/{player_id}/summon-state",
         lambda body: body.get("balance") is not None
         if isinstance(body, dict) else False,
         "balance", "missing"),
        ("atoms-preview", f"{_CREATURES}:157",
         f"/api/creatures/debug/atoms-preview/{instance_id}",
         lambda body: body.get("atoms") is not None
         if isinstance(body, dict) else False,
         "preview", "missing"),
    ]
    rungs = []
    for name, file, path, holds, expected, fail_actual in stages:
        try:
            body = _get(path, transport)
        except LookupError as ex:
            body = None
            fetch_error = str(ex)
        else:
            fetch_error = None
        if fetch_error is None and holds(body):
            rungs.append(evidence.rung("PASS", name, file=file))
            continue
        actual = fetch_error if fetch_error is not None else fail_actual
        rungs.append(evidence.rung("FAIL", name, file=file,
                                   expected=expected, actual=actual))
        return {"feature": "summon", "subject": instance_id, "ok": False,
                "broken_rung": name, "rungs": rungs,
                "scope": "rpg-server-debug"}
    return {"feature": "summon", "subject": instance_id, "ok": True,
            "broken_rung": None, "rungs": rungs, "scope": "rpg-server-debug"}


def verify(feature, subject, transport=None):
    """Run one feature ladder to the first broken link."""
    if feature == "actor":
        return _verify_actor(subject, transport)
    if feature == "summon":
        return _verify_summon(subject, transport=transport)
    raise ValueError(f"unknown feature: {feature} (closed: {', '.join(FEATURES)})")
