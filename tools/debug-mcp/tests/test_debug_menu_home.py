"""debug_menu_home: one reusable 3-step menu return. Hermetic -- the game is never
touched; ui_nav/find_text/invoke are stubbed and the clock is faked.
"""
import pytest

from tools import debug_menu_home as home


def _match(ptr, path="CanvasUp/PauseMenu(Clone)/mainmenu", label="x", ctype="PauseMenu_Btn"):
    return {"ptr": ptr, "text": label, "path": path + "/text",
            "clickablePtr": ptr + "-click", "clickableName": "btn",
            "clickableType": ctype, "clickablePath": path}


def _wire(monkeypatch, script, clock):
    """script: label -> list of match-lists returned per successive call."""
    calls = {"ui_nav": [], "invoke": []}
    monkeypatch.setattr(home, "ui_nav", lambda action, **kw: (calls["ui_nav"].append(action),
                                                              {"ok": True})[1])
    monkeypatch.setattr(home, "invoke",
                        lambda ptr, method, **kw: (calls["invoke"].append((ptr, method)),
                                                  {"ok": True})[1])

    def fake_text(label, **kw):
        pending = script.get(label, [])
        return {"ok": True, "matches": pending.pop(0) if pending else []}

    monkeypatch.setattr(home, "find_text", fake_text)
    tick = [0.0]
    monkeypatch.setattr(home, "_clock", lambda: tick[0])
    if clock is not None:
        clock.append(tick)
    return calls


def test_happy_path_clicks_pause_mainmenu_confirm_and_verifies(monkeypatch):
    main = _match("m1")
    confirm = _match("c1", path="CanvasUp/PauseMenu_checkQuit(Clone)/Confirm",
                     label="确定", ctype="PauseMenu_Btn")
    marker = _match("t1", path="CanvasUp/Tombstone", label="冒险模式", ctype="")
    monkeypatch.setattr(home, "find_text", lambda *a, **k: None)  # replaced below
    seen = {"n": 0}

    def fake_text(label, **kw):
        seen["n"] += 1
        if label == "主菜单":
            return {"ok": True, "matches": [main]}
        if label == "确定":
            # First poll (step 3) finds the dialog; verify poll finds it gone.
            return {"ok": True, "matches": [confirm] if seen["n"] <= 2 else []}
        return {"ok": True, "matches": [marker]}

    calls = {"ui_nav": [], "invoke": []}
    monkeypatch.setattr(home, "ui_nav", lambda action, **kw: (calls["ui_nav"].append(action),
                                                              {"ok": True})[1])
    monkeypatch.setattr(home, "invoke",
                        lambda ptr, method, **kw: (calls["invoke"].append((ptr, method)),
                                                  {"ok": True})[1])
    monkeypatch.setattr(home, "find_text", fake_text)
    tick = [0.0]
    monkeypatch.setattr(home, "_clock", lambda: tick[0])
    out = home.home(timeout=90, sleep=lambda s: tick.__setitem__(0, tick[0] + s))
    assert out["ok"] is True, out
    assert calls["ui_nav"] == ["enter-pause-menu"]
    assert [ptr for ptr, _ in calls["invoke"]] == ["m1-click", "c1-click"]
    assert out["steps"][-1]["step"] == "verified-main-menu"


def test_already_home_short_circuits(monkeypatch):
    clock = []
    calls = _wire(monkeypatch, {"主菜单": [[]],
                                "冒险模式": [[_match("t", path="Tombstone")]]}, clock)
    out = home.home(timeout=30, sleep=lambda s: None)
    assert out["ok"] is True
    assert calls["invoke"] == []
    assert out["steps"][-1]["step"] == "already-home"


def test_pause_refused_fails_fast(monkeypatch):
    monkeypatch.setattr(home, "ui_nav", lambda action, **kw: {"ok": False, "error": "no game",
                                                              "fix": "start it"})
    out = home.home(timeout=30, sleep=lambda s: None)
    assert out["ok"] is False and "no game" in out["error"]


def test_missing_mainmenu_button_fails(monkeypatch):
    clock = []
    _wire(monkeypatch, {"主菜单": [[]], "冒险模式": [[]]}, clock)
    out = home.home(timeout=30, sleep=lambda s: None)
    assert out["ok"] is False and "主菜单" in out["error"]


def test_confirm_never_appears_fails_on_budget(monkeypatch):
    clock = []
    calls = _wire(monkeypatch, {"主菜单": [[_match("m1")]], "确定": []}, clock)
    tick = clock[0]
    out = home.home(timeout=10, sleep=lambda s: tick.__setitem__(0, tick[0] + s))
    assert out["ok"] is False and "never appeared" in out["error"]
    assert calls["invoke"] != []  # the 主菜单 click still happened -- steps show it


def test_clicked_but_unverified_is_not_success(monkeypatch):
    confirm = _match("c1", path="CanvasUp/PauseMenu_checkQuit(Clone)/Confirm", label="确定")
    clock = []
    calls = _wire(monkeypatch, {"主菜单": [[_match("m1")]],
                                "确定": [[confirm], [confirm], [confirm]],
                                "冒险模式": [[]]}, clock)
    tick = clock[0]
    out = home.home(timeout=60, sleep=lambda s: tick.__setitem__(0, tick[0] + s))
    assert out["ok"] is False and "not observed" in out["error"]


def test_bad_timeout_raises():
    with pytest.raises(ValueError, match="timeout"):
        home.home(timeout=0)
    with pytest.raises(ValueError, match="timeout"):
        home.home(timeout="soon")


def test_server_registers_debug_menu_home():
    import asyncio
    from server import mcp
    assert "debug_menu_home" in [t.name for t in asyncio.run(mcp.list_tools())]
