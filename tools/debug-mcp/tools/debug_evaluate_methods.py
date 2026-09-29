"""debug_evaluate_methods: list public instance methods of a resolved object.

Adapter over POST /api/debug/evaluate-methods. Read-only discovery for evaluate-call:
which methods exist, with what signatures. Only caller misuse raises.
"""
from tools import _control


def methods(ptr, timeout=60, transport=None, sleep=None):
    """Return per-component method tables for one live ptr."""
    if not ptr:
        raise ValueError("ptr is required (find it via search or inspect)")
    tag = _control.fresh_tag("evaluate-methods")
    ok, reply, fix = _control.trigger("/evaluate-methods", {"ptr": ptr, "tag": tag},
                                  transport=transport, timeout=timeout)
    if not ok:
        return {"ok": False, "error": fix, "fix": fix,
                "components": [], "scope": _control.SCOPE}
    after_id = reply.get("afterId", 0) if isinstance(reply, dict) else 0
    ready = _control.poll("debug.evaluate.methods", tag, after_id,
                          transport=transport, timeout=timeout, sleep=sleep)
    if ready is None:
        return {"ok": False,
                "error": "no debug.evaluate.methods for this ptr within timeout",
                "fix": "the object may have died -- re-resolve via search",
                "components": [], "scope": _control.SCOPE}
    return {
        "ok": True,
        "scope": _control.SCOPE,
        "ptr": ptr,
        "components": ready.get("components", []),
    }
