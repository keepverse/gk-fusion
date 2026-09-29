"""Budget envelopes for every list-shaped tool response.

Structural constants with stated reasons (spec Tunables): default limit 20
(reasonable reasoning window), hard cap 100 (prior-art session-killer
threshold), opaque cursor. A capped page ALWAYS carries truncated=true +
next_cursor — silent truncation is a test failure (test_envelope.py).
"""

DEFAULT_LIMIT = 20
HARD_CAP = 100


def _decode_cursor(cursor):
    try:
        offset = int(cursor)
    except (TypeError, ValueError):
        offset = 0
    return max(offset, 0)


def paginate(items, limit=DEFAULT_LIMIT, cursor=None):
    """Slice a list into a budget envelope. Never silently truncates."""
    items = list(items)
    if limit is None or limit <= 0:
        limit = DEFAULT_LIMIT
    limit = min(limit, HARD_CAP)
    offset = _decode_cursor(cursor)
    page = items[offset:offset + limit]
    remaining = len(items) - (offset + len(page))
    truncated = remaining > 0
    return {
        "items": page,
        "truncated": truncated,
        "next_cursor": str(offset + len(page)) if truncated else None,
    }
