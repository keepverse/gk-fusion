"""debug_evaluate_search: find controls by name/type/path (locator building block).

Adapter over POST /api/debug/evaluate-search. Read-only: matches carry ptrs for the
click module (or debug_click) to resolve; nothing is invoked here. Only caller
misuse raises.
"""
from tools import _control


def search(nameContains="", type="", pathContains="", limit=50, timeout=60,
           transport=None, sleep=None):
    """Return matching controls with ptrs and paths."""
    tag = _control.fresh_tag("evaluate-search")
    body = {"nameContains": nameContains, "type": type,
            "pathContains": pathContains, "limit": limit, "tag": tag}
    ok, reply, fix = _control.trigger("/evaluate-search", body,
                                  transport=transport, timeout=timeout)
    if not ok:
        return {"ok": False, "error": fix, "fix": fix,
                "matches": [], "scope": _control.SCOPE}
    after_id = reply.get("afterId", 0) if isinstance(reply, dict) else 0
    ready = _control.poll("debug.evaluate.result", tag, after_id,
                          transport=transport, timeout=timeout, sleep=sleep)
    if ready is None:
        return {"ok": False,
                "error": "no debug.evaluate.result within timeout",
                "fix": "the game may be closed or mid-load",
                "matches": [], "scope": _control.SCOPE}
    return {
        "ok": True,
        "scope": _control.SCOPE,
        "scanned": ready.get("scanned"),
        "matches": ready.get("matches", []),
    }
