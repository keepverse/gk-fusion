"""debug_events: budgeted event tail (todo T3).

Cursor = server afterId watermark (last seen envelope id). Limit+1 fetch
detects truncation: a full over-cap page returns truncated=true + next_cursor.
match_key filters the fetched page (server has no match filter; the cursor
always advances so paging terminates).
"""
from tools import debug_events


def _env(i, kind="board.start", match="m1"):
    return {"id": i, "t": "t", "game": "g", "kind": kind,
            "matchKey": match, "payload": {}}


def test_short_page_not_truncated():
    def transport(method, url, body, params=None):
        assert url.endswith("/api/debug/events")
        assert params["kinds"] == "board.start"
        return {"status": 200, "body": {"items": [_env(1), _env(2)]}}

    out = debug_events.tail("board.start", transport=transport)
    assert [e["id"] for e in out["items"]] == [1, 2]
    assert out["truncated"] is False
    assert out["next_cursor"] is None


def test_over_cap_page_truncates_with_cursor():
    seen = {}

    def transport(method, url, body, params=None):
        seen["params"] = params
        return {"status": 200,
                "body": {"items": [_env(i) for i in range(1, 23)]}}  # 20 + 1 probe

    out = debug_events.tail("board.start", limit=20, transport=transport)
    assert seen["params"]["limit"] == 21
    assert [e["id"] for e in out["items"]] == list(range(1, 21))
    assert out["truncated"] is True
    assert out["next_cursor"] == "20"


def test_cursor_resumes_after_watermark():
    seen = {}

    def transport(method, url, body, params=None):
        seen["params"] = params
        return {"status": 200, "body": {"items": [_env(21)]}}

    out = debug_events.tail("board.start", cursor="20", transport=transport)
    assert seen["params"]["afterId"] == "20"
    assert [e["id"] for e in out["items"]] == [21]


def test_match_key_filters_page():
    def transport(method, url, body, params=None):
        return {"status": 200, "body": {"items": [_env(1, match="m1"),
                                                  _env(2, match="m2")]}}

    out = debug_events.tail("board.start", match_key="m2", transport=transport)
    assert [e["id"] for e in out["items"]] == [2]
