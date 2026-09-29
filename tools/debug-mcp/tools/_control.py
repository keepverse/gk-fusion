"""Shared trigger-and-poll for the game-control tools (inspect/click/act/cursor).

One shape, eight callers: POST the verb route with a per-call unique tag, poll for
the event carrying that tag, return its payload. Tag correlation (not recency) is
what makes the poll correct: /events reads forward from afterId, so newest-first
assumptions silently match stale rows on long-lived servers. Only caller misuse
raises; environment failures come back as (ok, payload-or-None, error, fix).
"""
import time
import uuid

from tools.debug_call import call as route_call

SCOPE = "game-injector-debug"
_POLL_EVERY_SEC = 2
_FETCH_TIMEOUT = 30


def fresh_tag(prefix):
    """Unique tag for one call; the injector echoes it in the emit."""
    return f"{prefix}-{uuid.uuid4().hex[:8]}"


def trigger(route, body, transport=None, timeout=60):
    """POST one control route. Returns (ok, body-or-error, fix-or-None)."""
    try:
        result = route_call("POST", route, body=body,
                            transport=transport, timeout=timeout)
    except ValueError as ex:
        return False, None, (
            f"{ex} -- the MCP process is reading a DebugEndpoints.cs generation "
            "without the control routes; restart it from a merged tree")
    except Exception as ex:
        return False, None, (
            f"server unreachable: {ex} -- "
            "Start-Process dist\\FusionRpg.Server\\FusionRpg.Server.exe")
    if not isinstance(result, dict) or not result.get("ok"):
        body = result.get("body") if isinstance(result, dict) else None
        error = body.get("error", "route refused") \
            if isinstance(body, dict) else "route refused"
        return False, None, error
    payload = result.get("body")
    return True, payload if isinstance(payload, dict) else {}, None


def poll(kind, tag=None, after_id=0, transport=None, timeout=30, sleep=None,
         match=None):
    """Poll events after a known id for the payload carrying this call's tag.

    Both anchors matter: afterId bounds the window (the table is append-only and
    forward-paged), while tag or the caller-supplied match predicate disambiguates
    concurrent same-kind emits. Locator tools use match because their event payload
    carries the resolved ptr rather than the request tag.
    """
    do_sleep = sleep or time.sleep
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            page = route_call("GET", "/events",
                              params={"kinds": kind, "limit": 100,
                                      "afterId": after_id},
                              transport=transport, timeout=_FETCH_TIMEOUT)
        except Exception:
            break
        body = page.get("body", {}) if isinstance(page, dict) else {}
        items = body.get("items", []) if isinstance(body, dict) else []
        for envelope in items:
            payload = envelope.get("payload", {}) \
                if isinstance(envelope, dict) else {}
            correlated = (match(payload) if match is not None
                          else payload.get("tag") == tag)
            if correlated:
                return payload
            try:
                if isinstance(envelope.get("id"), int):
                    after_id = max(after_id, envelope["id"])
            except Exception:
                pass
        do_sleep(_POLL_EVERY_SEC)
    return None
