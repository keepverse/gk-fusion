"""debug_evaluate_call: invoke one public instance method on a resolved component.

Adapter over POST /api/debug/evaluate-call. Closed world: live ptr + public instance
method + JSON-primitive args (int/float/bool/string/enum/null). Anything else is a
loud refusal with the reason; the emit always says what ran, via which path
(invoke|sendmessage), and what it returned. Only caller misuse raises.
"""
from tools import _control


def call(ptr, method, type="", args=None, timeout=60, transport=None,
         sleep=None):
    """Invoke one method. Returns ok/result/via envelope."""
    if not ptr or not method:
        raise ValueError("ptr and method are required (discover via methods)")
    tag = _control.fresh_tag("evaluate-call")
    body = {"ptr": ptr, "type": type, "method": method, "tag": tag}
    if args is not None:
        if not isinstance(args, list):
            raise ValueError("args must be a JSON list (possibly empty)")
        body["args"] = args
    ok, reply, fix = _control.trigger("/evaluate-call", body,
                                  transport=transport, timeout=timeout)
    if not ok:
        return {"ok": False, "error": fix, "fix": fix,
                "scope": _control.SCOPE}
    after_id = reply.get("afterId", 0) if isinstance(reply, dict) else 0
    ready = _control.poll("debug.evaluate.called", tag, after_id,
                          transport=transport, timeout=timeout, sleep=sleep)
    if ready is None:
        return {"ok": False,
                "error": "no debug.evaluate.called for this call within timeout",
                "fix": "refusals name their reason in the injector log -- check it",
                "scope": _control.SCOPE}
    return {
        "ok": ready.get("ok", False),
        "scope": _control.SCOPE,
        "ptr": ptr,
        "type": ready.get("type"),
        "method": method,
        "via": ready.get("via"),
        "result": ready.get("result"),
    }
