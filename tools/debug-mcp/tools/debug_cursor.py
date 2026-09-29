"""debug_cursor: move the REAL OS cursor, optionally click (DISRUPTIVE tier).

Adapter over POST /api/debug/cursor. This moves the operator's actual mouse: per-call
opt-in (`confirmed=True`), game-foreground check, 2 s throttle, and client-rect clamp
all enforced injector-side, each refusal distinct. Only caller misuse raises.
"""
from tools import _control


def cursor(x, y, click=False, confirmed=False, timeout=60, transport=None,
           sleep=None):
    """Move (and optionally click) the real cursor. Returns the receipt."""
    if not isinstance(x, int) or not isinstance(y, int) or x < 0 or y < 0:
        raise ValueError("x/y must be non-negative client pixels (see inspect screen)")
    if not confirmed:
        raise ValueError("confirmed=True is required: this moves the real mouse")
    tag = _control.fresh_tag("cursor")
    ok, reply, fix = _control.trigger(
        "/cursor", {"x": x, "y": y, "click": bool(click),
                    "confirmedLiveCursor": True, "tag": tag},
        transport=transport, timeout=timeout)
    if not ok:
        return {"ok": False, "error": fix, "fix": fix,
                "scope": _control.SCOPE}
    after_id = reply.get("afterId", 0) if isinstance(reply, dict) else 0
    ready = _control.poll("debug.cursor.done", tag, after_id,
                          transport=transport, timeout=timeout, sleep=sleep)
    if ready is None:
        return {"ok": False,
                "error": "no debug.cursor.done within timeout",
                "fix": "refusals (background window, throttle, out-of-rect) land in "
                       "the injector log, not here -- check it",
                "scope": _control.SCOPE}
    return {
        "ok": True,
        "scope": _control.SCOPE,
        "x": ready.get("x"),
        "y": ready.get("y"),
        "clicked": ready.get("clicked", False),
    }
