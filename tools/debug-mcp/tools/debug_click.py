"""debug_click: act on a control ref from the current inspect snapshot.

Adapter over POST /api/debug/click. The injector re-verifies ptr+type+position before
invoking (native ptrs are reused after death); stale/reused/moved refs are refused with
the fresh-snapshot instruction, never retried blind. Only caller misuse raises.
"""
from tools import _control


def click(snapshotId, ref, timeout=60, transport=None, sleep=None):
    """Click one ref. Returns the receipt incl. the bundled snapshot."""
    if not snapshotId or not ref:
        raise ValueError("snapshotId and ref are required (take an inspect snapshot first)")
    tag = _control.fresh_tag("click")
    ok, body, fix = _control.trigger(
        "/click", {"snapshotId": snapshotId, "ref": ref, "tag": tag},
        transport=transport, timeout=timeout)
    if not ok:
        return {"ok": False, "error": fix, "fix": fix,
                "scope": _control.SCOPE}
    after_id = body.get("afterId", 0) if isinstance(body, dict) else 0
    ready = _control.poll("debug.click.done", tag, after_id,
                          transport=transport, timeout=timeout, sleep=sleep)
    if ready is None:
        return {"ok": False,
                "error": "no debug.click.done for this ref within timeout "
                         "(stale ref refusals arrive as errors, not silence -- "
                         "the game may be closed)",
                "fix": "take a fresh inspect snapshot and retry",
                "scope": _control.SCOPE}
    return {
        "ok": ready.get("ok", False),
        "scope": _control.SCOPE,
        "snapshotId": snapshotId,
        "ref": ref,
        "ptr": ready.get("ptr"),
        "type": ready.get("type"),
        "snapshot": ready.get("snapshot"),
    }
