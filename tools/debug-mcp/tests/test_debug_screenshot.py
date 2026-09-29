"""debug_screenshot: Playwright-shaped live frame capture (live-probe lawn-screenshot).

Adapter over POST /api/debug/screenshot + ready-poll + latest fetch. Environment
problems come back as NOT-READY envelopes (ok=False + fix); only caller misuse
raises. Stubs inject transport/fetch/sleep so no test touches the network, the
disk (except tmp_path), or the clock.
"""
import base64

import httpx
import pytest

from tools import debug_screenshot as shot

_PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 64
_READY = {"tag": "probe", "width": 960, "height": 538, "bytes": 72,
          "validPng": True, "primitive": "screen-readback"}


def _trigger_ok(method, url, body, params=None):
    assert url.endswith("/api/debug/screenshot")
    assert body["tag"] == "probe"
    return {"status": 200, "body": {"ok": True, "tag": "probe"}}


def _tail_ready(method, url, body, params=None):
    assert url.endswith("/api/debug/events")
    assert params["kinds"] == "debug.screenshot.ready"
    return {"status": 200, "body": {"items": [
        {"id": 99, "kind": "debug.screenshot.ready", "payload": dict(_READY)}]}}


def _tail_empty(method, url, body, params=None):
    return {"status": 200, "body": {"items": []}}


def _fetch_png(url, timeout):
    assert url.startswith("http://127.0.0.1:5088/api/debug/screenshot/latest")
    return _PNG


def test_happy_path_with_save_to_omits_inline_base64(tmp_path):
    # save_to already puts the PNG on disk -- inlining base64 too blows the
    # tool-result token budget for nothing, so the envelope must not carry it.
    target = tmp_path / "frame.png"
    out = shot.screenshot(transport=_mux_ok(),
                          fetch=_fetch_png, sleep=lambda s: None,
                          save_to=str(target))
    assert out["ok"] is True
    assert out["tag"] == "probe"
    assert (out["width"], out["height"]) == (960, 538)
    assert out["bytes"] == len(_PNG)
    assert out["primitive"] == "screen-readback"
    assert out["pngBase64"] is None
    assert out["path"] == str(target)
    assert target.read_bytes() == _PNG
    assert out["scope"] == "game-injector-debug"


def test_happy_path_without_save_to_inlines_base64():
    out = shot.screenshot(transport=_mux_ok(),
                          fetch=_fetch_png, sleep=lambda s: None)
    assert out["ok"] is True
    assert base64.b64decode(out["pngBase64"]) == _PNG
    assert out["path"] is None


def _mux_ok():
    def send(method, url, body, params=None):
        if url.endswith("/api/debug/screenshot"):
            return _trigger_ok(method, url, body, params)
        if url.endswith("/api/debug/screenshot/info"):
            return {"status": 200, "body": {"fileName": "f.png",
                                            "tag": "probe", "bytes": 72,
                                            "takenAtUtc": "2026-09-14T00:00:00Z"}}
        return _tail_ready(method, url, body, params)
    return send


def test_trigger_refusal_returns_not_ready_with_fix():
    def send(method, url, body, params=None):
        return {"status": 409, "body": {"ok": False,
                                        "error": "injector not connected"}}
    out = shot.screenshot(transport=send, fetch=_fetch_png,
                          sleep=lambda s: None)
    assert out["ok"] is False
    assert "injector" in out["fix"]
    assert out["pngBase64"] is None
    assert out["scope"] == "game-injector-debug"


def test_ready_timeout_returns_not_ready_without_sleeping():
    sleeps = []
    out = shot.screenshot(tag="probe", timeout=1, transport=_tail_empty,
                          fetch=_fetch_png, sleep=sleeps.append)
    assert out["ok"] is False
    assert "no debug.screenshot.ready" in out["error"]
    assert len(sleeps) >= 1


def test_non_png_bytes_return_not_ready():
    out = shot.screenshot(transport=_mux_ok(),
                          fetch=lambda url, timeout: b"not an image",
                          sleep=lambda s: None)
    assert out["ok"] is False
    assert "not a PNG" in out["error"]


def test_server_down_returns_not_ready_naming_start():
    def send(method, url, body, params=None):
        raise httpx.ConnectError("refused")
    out = shot.screenshot(transport=send, fetch=_fetch_png,
                          sleep=lambda s: None)
    assert out["ok"] is False
    assert "FusionRpg.Server" in out["fix"]


def test_caller_misuse_raises():
    with pytest.raises(ValueError):
        shot.screenshot(tag="  ")
    with pytest.raises(ValueError):
        shot.screenshot(timeout=0)
