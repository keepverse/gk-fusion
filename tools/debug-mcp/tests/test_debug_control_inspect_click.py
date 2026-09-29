"""debug_inspect / debug_click: adapter dispatch over the control routes."""
from tools import debug_inspect as inspect
from tools import debug_click as click


def _mux(mapping):
    last_tag = None

    def send(method, url, body, params=None):
        nonlocal last_tag
        if method == "POST" and isinstance(body, dict) and body.get("tag"):
            last_tag = body["tag"]
        for suffix, response in mapping.items():
            if url.endswith(suffix):
                if suffix == "/api/debug/events" and last_tag:
                    if last_tag:
                        response = dict(response)
                        response["items"] = [
                            {**item, "payload": {**item.get("payload", {}), "tag": last_tag}}
                            for item in response.get("items", [])
                        ]
                return {"status": 200, "body": response,
                        "scope": "game-injector-debug", "route": suffix}
        raise AssertionError(f"unexpected route: {url}")
    return send


def test_inspect_returns_tree_with_refs():
    send = _mux({
        "/api/debug/inspect": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 7, "kind": "debug.inspect",
             "payload": {"snapshotId": "snap7",
                         "controls": [{"ref": "c0", "type": "Plant"}],
                         "truncated": False, "next_cursor": None}}]},
    })
    out = inspect.inspect(scope="menu", transport=send, sleep=lambda s: None)
    assert out["ok"] is True
    assert out["snapshotId"] == "snap7"
    assert out["controls"] == [{"ref": "c0", "type": "Plant"}]
    assert out["scope"] == "game-injector-debug"


def test_inspect_bad_scope_raises():
    import pytest
    with pytest.raises(ValueError):
        inspect.inspect(scope="everywhere")


def test_click_returns_receipt_with_snapshot():
    send = _mux({
        "/api/debug/click": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 8, "kind": "debug.click.done",
             "payload": {"snapshotId": "snap7", "ref": "c0",
                         "ptr": "AB12", "type": "Plant", "ok": True,
                         "snapshot": {"snapshotId": "snap7"}}}]},
    })
    out = click.click("snap7", "c0", transport=send, sleep=lambda s: None)
    assert out["ok"] is True
    assert out["ptr"] == "AB12"
    assert out["snapshot"] == {"snapshotId": "snap7"}


def test_click_missing_ref_raises():
    import pytest
    with pytest.raises(ValueError):
        click.click("snap7", "")
