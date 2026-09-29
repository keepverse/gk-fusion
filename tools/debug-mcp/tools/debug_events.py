"""debug_events: budgeted event-envelope tail (todo T3).

Cursor is the server afterId watermark (last seen envelope id). A limit+1
probe fetch detects truncation. match_key filters the fetched page — the
server has no match filter, so the cursor always advances and paging
terminates. Truncation describes the stream (pre-filter), so a full probe
page flags even when the filter kept few.
"""
import budget
from tools.debug_call import call as route_call


def tail(kind=None, match_key=None, limit=budget.DEFAULT_LIMIT, cursor=None,
         transport=None):
    """Tail envelopes, optionally of one kind. Returns budgeted envelope + scope."""
    want = limit if limit and limit > 0 else budget.DEFAULT_LIMIT
    want = min(want, budget.HARD_CAP)
    params = {"limit": want + 1}
    if kind is not None:
        params["kinds"] = kind
    if cursor is not None:
        params["afterId"] = cursor
    result = route_call("GET", "/events", params=params, transport=transport)
    body = result["body"]
    fetched = body.get("items", []) if isinstance(body, dict) else []
    stream_full = len(fetched) >= want + 1
    kept = [e for e in fetched
            if match_key is None or e.get("matchKey") == match_key][:want]
    if stream_full:
        anchor = kept[-1] if kept else fetched[-1]
        next_cursor = str(anchor["id"])
    else:
        next_cursor = None
    return {
        "items": kept,
        "truncated": stream_full,
        "next_cursor": next_cursor,
        "scope": "rpg-server-debug",
        "kind": kind,
        "route": "/events",
    }
