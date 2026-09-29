"""debug_inspect: budgeted control tree of the current game screen.

Adapter over POST /api/debug/inspect. Menus come back as named controls, the lawn as
cells/cards/entities; only interactables get refs (snapshot-scoped — a ref dies with
its snapshot, re-resolve after any screen change). Over-budget output truncates with a
marker and a next_cursor. Only caller misuse raises.
"""
from tools import _control


def inspect(scope="all", limit=50, cursor=None, timeout=60, transport=None,
            sleep=None):
    """Return the control tree envelope (never raises on env)."""
    if scope not in ("menu", "lawn", "all"):
        raise ValueError("scope must be menu, lawn, or all")
    if not isinstance(limit, int) or limit < 1:
        raise ValueError("limit must be a positive int")
    tag = _control.fresh_tag("inspect")
    ok, body, fix = _control.trigger(
        "/inspect", {"scope": scope, "limit": limit, "cursor": cursor,
                     "tag": tag},
        transport=transport, timeout=timeout)
    if not ok:
        return {"ok": False, "error": fix, "fix": fix,
                "controls": [], "scope": _control.SCOPE}
    after_id = body.get("afterId", 0) if isinstance(body, dict) else 0
    ready = _control.poll("debug.inspect", tag, after_id,
                          transport=transport, timeout=timeout, sleep=sleep)
    if ready is None:
        return {"ok": False,
                "error": "no debug.inspect snapshot arrived within timeout",
                "fix": "the game may be closed or mid-load -- check the injector log",
                "controls": [], "scope": _control.SCOPE}
    return {
        "ok": True,
        "scope": _control.SCOPE,
        "snapshotId": ready.get("snapshotId"),
        "controls": ready.get("controls", []),
        "truncated": ready.get("truncated", False),
        "next_cursor": ready.get("next_cursor"),
    }
