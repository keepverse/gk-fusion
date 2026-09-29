"""debug_menu_home: walk the real UI back to the true main menu in one call.

The 3-step click flow proven live 2026-09-20 (operator-guided, screenshot-verified):
  1. open the pause menu (`enter-pause-menu` -- harmless to repeat),
  2. click the pause menu's own 主菜单 button -- a quit-confirm dialog appears,
  3. click its 确定 button -- the game lands on the true main menu.

This is the default menu recovery. `debug_ui_nav`'s `back-to-menu` UIMgr shortcut is
buggy (lands on stale menu layers; game-state keeps reporting the dead board as InMatch
from unreleased Board refs) -- do not use it. The click path walks the same UI stack
the player uses, so the layers actually unwind. Adapter-only: composes ui-nav +
evaluate-text + evaluate-call, no new endpoint, no logic of its own. Only caller
misuse raises; every environment failure is an ok:false envelope naming the failed step.
"""
import time

from tools.debug_ui_nav import nav as ui_nav
from tools.debug_evaluate_text import text as find_text
from tools.debug_evaluate_call import call as invoke

SCOPE = "game-injector-debug"
_METHOD = "OnMouseUp"
_POLL_EVERY_SEC = 2
_VERIFY_BUDGET_SEC = 20

# Injectable clock for hermetic tests (see test_debug_menu_home.py).
_clock = time.monotonic


def _failed(steps, error, fix):
    return {"ok": False, "error": error, "fix": fix, "steps": steps, "scope": SCOPE}


def _pick(matches, *path_hints):
    """First match carrying a clickable, preferring one whose clickable path hints match."""
    fallback = None
    for match in matches or ():
        if not isinstance(match, dict) or not match.get("clickablePtr"):
            continue
        if fallback is None:
            fallback = match
        path = str(match.get("clickablePath") or "")
        if any(hint in path for hint in path_hints):
            return match
    return fallback


def home(timeout=90, transport=None, sleep=None):
    """Run the 3-step menu return inside one total `timeout` budget (seconds).

    Returns ok:true only after the confirm dialog is observed gone AND a main-menu
    marker (冒险模式 tombstone text) is observed present -- a clicked button alone
    is never reported as proof.
    """
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or timeout < 1:
        raise ValueError("timeout must be a positive number of seconds")
    pause = sleep if sleep is not None else time.sleep
    deadline = _clock() + timeout
    steps = []

    def remaining():
        return deadline - _clock()

    def search(label, **kwargs):
        budget = max(1, min(int(remaining()), 30))
        found = find_text(label, transport=transport, timeout=budget, sleep=pause, **kwargs)
        if not found.get("ok"):
            return None
        return found.get("matches", [])

    # Step 1: pause menu. Harmless to repeat when already open (ui-nav contract).
    opened = ui_nav("enter-pause-menu", transport=transport, timeout=min(int(remaining()), 15))
    if not opened.get("ok"):
        return _failed(steps, opened.get("error", "pause menu did not open"),
                        opened.get("fix", "check the game is responsive"))
    steps.append({"step": "open-pause-menu", "action": "enter-pause-menu"})

    # Step 2: the pause menu's own 主菜单 button (not the settings-panel one -- that
    # only backs out to the pause layer without raising the quit-confirm).
    if remaining() <= 0:
        return _failed(steps, "budget exhausted before the main-menu button",
                        "retry with a larger timeout")
    main_menu = _pick(search("主菜单"), "PauseMenu", "mainmenu")
    if main_menu is None:
        if _pick(search("冒险模式")) is not None:
            steps.append({"step": "already-home",
                          "detail": "no pause menu to leave; main-menu marker already visible"})
            return {"ok": True, "steps": steps, "scope": SCOPE}
        return _failed(steps, "pause menu has no clickable 主菜单 button",
                        "screenshot the game -- it may be on a screen with no pause menu")
    clicked = invoke(main_menu["clickablePtr"], _METHOD,
                     type=main_menu.get("clickableType") or "",
                     transport=transport, timeout=min(int(remaining()), 15), sleep=pause)
    if not clicked.get("ok"):
        return _failed(steps, f"主菜单 click refused: {clicked.get('error')}",
                        clicked.get("fix") or "check the injector log")
    steps.append({"step": "click-main-menu-button", "ptr": main_menu["clickablePtr"]})

    # Step 3: the quit-confirm dialog's 确定 button (polled -- the dialog animates in).
    confirmed = None
    while remaining() > 0:
        confirm = _pick(search("确定"), "checkQuit", "Confirm")
        if confirm is not None:
            done = invoke(confirm["clickablePtr"], _METHOD,
                          type=confirm.get("clickableType") or "",
                          transport=transport, timeout=min(int(remaining()), 15), sleep=pause)
            if done.get("ok"):
                confirmed = confirm
                steps.append({"step": "click-confirm", "ptr": confirm["clickablePtr"]})
            else:
                return _failed(steps, f"confirm click refused: {done.get('error')}",
                                done.get("fix") or "check the injector log")
            break
        pause(min(_POLL_EVERY_SEC, max(0.1, remaining())))
    if confirmed is None:
        return _failed(steps, "quit-confirm dialog never appeared after clicking 主菜单",
                        "screenshot the game -- the click may have landed on the wrong layer")

    # Verify: dialog gone AND main-menu marker present. A clicked button is not proof.
    verify_deadline = min(deadline, _clock() + _VERIFY_BUDGET_SEC)
    while _clock() < verify_deadline:
        gone = _pick(search("确定"), "checkQuit", "Confirm") is None
        home_marker = _pick(search("冒险模式")) is not None
        if gone and home_marker:
            steps.append({"step": "verified-main-menu",
                          "detail": "confirm dialog gone, 冒险模式 marker visible"})
            return {"ok": True, "steps": steps, "scope": SCOPE}
        pause(min(_POLL_EVERY_SEC, max(0.1, verify_deadline - _clock())))
    return _failed(steps, "confirm clicked but the main menu was not observed",
                    "screenshot the game -- report what it actually shows")
