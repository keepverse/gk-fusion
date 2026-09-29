"""debug_evaluate_text: find visible text, resolve the clickable behind it.

Adapter over POST /api/debug/evaluate-text. Engine text content is read directly off
Text/TextMeshProUGUI components (no OCR); each match carries its node plus the nearest
clickable ancestor (or neither, with the nearby tree, when the text is bare paint).
Only caller misuse raises.
"""
from tools import _control


def text(text, limit=50, timeout=60, transport=None, sleep=None):
    """Search visible text; each match names its clickable (or none)."""
    if not isinstance(text, str) or not text.strip():
        raise ValueError("text must be a non-empty string")
    tag = _control.fresh_tag("evaluate-text")
    ok, reply, fix = _control.trigger(
        "/evaluate-text", {"text": text, "limit": limit, "tag": tag},
                                  transport=transport, timeout=timeout)
    if not ok:
        return {"ok": False, "error": fix, "fix": fix,
                "matches": [], "scope": _control.SCOPE}
    after_id = reply.get("afterId", 0) if isinstance(reply, dict) else 0
    ready = _control.poll("debug.evaluate.text", tag, after_id,
                          transport=transport, timeout=timeout, sleep=sleep)
    if ready is None:
        return {"ok": False,
                "error": f"no debug.evaluate.text for {text!r} within timeout",
                "fix": "the text may be baked into a texture (not a Text component) "
                       "-- check a screenshot",
                "matches": [], "scope": _control.SCOPE}
    return {
        "ok": True,
        "scope": _control.SCOPE,
        "scanned": ready.get("scanned"),
        "matches": ready.get("matches", []),
    }
