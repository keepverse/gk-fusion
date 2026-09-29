"""debug_match: budgeted match digest (todo T4).

Snapshot hash + capped entity digests, never a full dump. Sources: the
/snapshot route plus the latest debug.snapshot event envelope (nested match
per match-runtime W3-B). No live board = explicit not-live shape.
"""
import hashlib
import json

import budget
from tools.debug_call import call as route_call


def _get(mapping, *names):
    for name in names:
        if isinstance(mapping, dict) and name in mapping:
            return mapping[name]
    return None


def _entity_digest(ent):
    return {
        "ptr": _get(ent, "ptr", "Ptr"),
        "side": _get(ent, "side", "Side"),
        "living": _get(ent, "living", "Living"),
    }


def digest(match_key=None, entity_limit=budget.DEFAULT_LIMIT, cursor=None,
           transport=None):
    """Digest the latest live match snapshot. Budgeted entities."""
    route_call("GET", "/snapshot", transport=transport)
    result = route_call("GET", "/events",
                        params={"kinds": "debug.snapshot", "limit": 5},
                        transport=transport)
    body = result["body"]
    items = body.get("items", []) if isinstance(body, dict) else []
    snap_event = None
    for envelope in items:
        payload = envelope.get("payload", {})
        match = payload.get("match", payload) if isinstance(payload, dict) else {}
        if match_key is None or match.get("MatchKey", match.get("matchKey")) == match_key:
            snap_event = envelope
            break
    if snap_event is None:
        return {"live": False, "phase": None, "snapshot_hash": None,
                "entities": [], "truncated": False, "next_cursor": None,
                "scope": result["scope"]}
    match = snap_event["payload"].get("match", snap_event["payload"])
    snapshot_hash = hashlib.sha256(
        json.dumps(match, sort_keys=True, default=str).encode("utf-8")).hexdigest()
    entities = [_entity_digest(e) for e in match.get("Entities", match.get("entities", []))]
    page = budget.paginate(entities, limit=entity_limit, cursor=cursor)
    return {
        "live": True,
        "phase": match.get("Phase", match.get("phase")),
        "matchKey": match.get("MatchKey", match.get("matchKey")),
        "revision": match.get("Revision", match.get("revision")),
        "snapshot_hash": snapshot_hash,
        "entities": page["items"],
        "truncated": page["truncated"],
        "next_cursor": page["next_cursor"],
        "scope": result["scope"],
    }
