"""debug_evaluate_search/methods/call/text: locator building blocks over game objects."""
import pytest

from tools import debug_evaluate_search as search
from tools import debug_evaluate_methods as methods
from tools import debug_evaluate_call as call
from tools import debug_evaluate_text as text


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


def test_search_returns_matches():
    send = _mux({
        "/api/debug/evaluate-search": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 11, "kind": "debug.evaluate.result",
             "payload": {"scanned": 91, "matches": [
                 {"ptr": "AB", "name": "Almanac", "path": "Canvas/x"}]}}]},
    })
    out = search.search(nameContains="Almanac", transport=send,
                        sleep=lambda s: None)
    assert out["ok"] is True
    assert out["matches"][0]["ptr"] == "AB"


def test_methods_returns_tables():
    send = _mux({
        "/api/debug/evaluate-methods": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 12, "kind": "debug.evaluate.methods",
             "payload": {"ptr": "AB", "components": [
                 {"type": "PauseMenu_Btn", "typeResolved": True,
                  "methods": [{"name": "OnMouseUp", "params": [],
                               "returns": "Void"}]}]}}]},
    })
    out = methods.methods("AB", transport=send, sleep=lambda s: None)
    assert out["ok"] is True
    assert out["components"][0]["methods"][0]["name"] == "OnMouseUp"


def test_methods_missing_ptr_raises():
    with pytest.raises(ValueError):
        methods.methods("")


def test_call_returns_result_and_path():
    send = _mux({
        "/api/debug/evaluate-call": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 13, "kind": "debug.evaluate.called",
             "payload": {"ptr": "AB", "type": "PauseMenu_Btn",
                         "method": "OnMouseUp", "via": "sendmessage",
                         "ok": True}}]},
    })
    out = call.call("AB", "OnMouseUp", type="PauseMenu_Btn", transport=send,
                    sleep=lambda s: None)
    assert out["ok"] is True
    assert out["via"] == "sendmessage"


def test_call_missing_method_raises():
    with pytest.raises(ValueError):
        call.call("AB", "")


def test_text_returns_clickables():
    send = _mux({
        "/api/debug/evaluate-text": {"ok": True},
        "/api/debug/events": {"items": [
            {"id": 14, "kind": "debug.evaluate.text",
             "payload": {"text": "确定", "scanned": 91, "matches": [
                 {"ptr": "CD", "text": "确定",
                  "clickableType": "PauseMenu_Btn",
                  "clickablePtr": "CE"}]}}]},
    })
    out = text.text("确定", transport=send, sleep=lambda s: None)
    assert out["ok"] is True
    assert out["matches"][0]["clickablePtr"] == "CE"


def test_text_empty_raises():
    with pytest.raises(ValueError):
        text.text("  ")
