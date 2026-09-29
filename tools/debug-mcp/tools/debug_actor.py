"""debug_actor: composed snapshot, instanceId XOR ptr (todo T5).

DB record via the normal query path + Hub audit via derived-audit-coverage
(real actor) + recent envelopes mentioning the id + per-link verdicts. The hub
section passes through verbatim: the tool never refolds actor numbers
(ActorHub gate N/A by consumption).
"""
import json

import evidence
from tools.debug_call import call as route_call
from tools.debug_events import tail as event_tail
from tools.debug_match import _entity_digest

_DB_FILE = "src/FusionRpg.Server/UniqueActorEndpoints.cs:19"
_HUB_FILE = "src/FusionRpg.Server/DebugEndpoints.cs:536"


def _links_db(record):
    if record is None:
        return [evidence.rung("FAIL", "db-record: specimen row missing",
                              file=_DB_FILE, expected="row", actual="404")]
    return [evidence.rung("PASS", "db-record: specimen row present",
                          file=_DB_FILE)]


def _links_hub(hub_result):
    if hub_result["ok"]:
        return [evidence.rung("PASS", "hub-audit: coverage computed",
                              file=_HUB_FILE)]
    return [evidence.rung("FAIL", "hub-audit: coverage refused",
                          file=_HUB_FILE, expected="ok",
                          actual=hub_result["body"])]


def _mentioning(events_body, needle):
    items = events_body.get("items", []) if isinstance(events_body, dict) else []
    return [{"id": e.get("id"), "kind": e.get("kind")} for e in items
            if needle in json.dumps(e, sort_keys=True, default=str)]


def _by_instance(instance_id, transport):
    record_call = route_call("GET", f"/api/unique/actors/{instance_id}",
                             transport=transport)
    if record_call["status"] == 404:
        return {"found": False, "instanceId": instance_id,
                "links": _links_db(None), "scope": record_call["scope"]}
    record = record_call["body"]
    hub_call = route_call("GET", "/derived-audit-coverage",
                          params={"instanceId": instance_id},
                          transport=transport)
    hub_body = hub_call["body"]
    hub = json.loads(hub_body) if isinstance(hub_body, str) else hub_body
    events = event_tail(limit=20, transport=transport)
    mentioned = _mentioning({"items": events["items"]}, instance_id)
    links = _links_db(record) + _links_hub(hub_call)
    return {"found": True, "instanceId": instance_id, "record": record,
            "hub": hub, "events": mentioned, "links": links,
            "scope": hub_call["scope"]}


def _binding_for(bindings, ptr):
    for binding in bindings or []:
        keys = {str(binding.get(k, "")).lower()
                for k in ("ptr", "Ptr", "entityPtr", "EntityPtr")}
        if ptr.lower() in keys:
            for key in ("instanceId", "InstanceId", "instance_id"):
                if binding.get(key):
                    return binding[key]
    return None


def _by_ptr(ptr, transport):
    events = event_tail(limit=5, transport=transport)
    for envelope in events["items"]:
        payload = envelope.get("payload", {})
        match = payload.get("match", payload) if isinstance(payload, dict) else {}
        if not isinstance(match, dict):
            continue
        iid = _binding_for(match.get("Bindings", match.get("bindings")), ptr)
        if iid is None:
            continue
        entities = match.get("Entities", match.get("entities", []))
        live = next((e for e in entities
                     if str(e.get("ptr", e.get("Ptr", ""))).lower() == ptr.lower()), None)
        out = _by_instance(iid, transport)
        out["live"] = _entity_digest(live) if live else None
        out["ptr"] = ptr
        return out
    return {"found": False, "ptr": ptr,
            "links": [evidence.rung("FAIL", "binding: no live binding for ptr",
                                    file="src/FusionRpg.Core/Match/MatchState.cs:56",
                                    expected="binding", actual="none")],
            "scope": "game-injector-debug"}


def snapshot(instance_id=None, ptr=None, transport=None):
    """Snapshot one actor by exactly one id. Typed miss when absent."""
    if (instance_id is None) == (ptr is None):
        raise ValueError("exactly one of instanceId/ptr is required")
    if ptr is not None:
        return _by_ptr(ptr, transport)
    return _by_instance(instance_id, transport)
