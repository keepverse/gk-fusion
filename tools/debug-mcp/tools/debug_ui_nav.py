"""debug_ui_nav: call one real UIMgr static navigation method by name.

Adapter over POST /api/debug/ui-nav (DebugEndpoints.cs). Added 2026-09-14 to close a named gap:
debug_call can already reach /ui-nav generically once the route is in the allowlist, but this
game's menu stack is not flat -- a single guessed method rarely reached the intended screen, and an
agent should not have to rediscover the working sequence by trial and error each session.
Default recovery is ONE `back-to-menu` call: since the 2026-09-15 injector fix
(`DebugActions.UiNav`, `gk-fusion/src/FusionRpg.Injector/DebugActions.cs`) it chains `BackToMenu()` +
`EnterMainMenu()` itself and lands on the true main menu -- the old two-call dance
(`back-to-menu` then a separate `enter-main-menu`) is folded into the action, not a caller
sequence to replay. Proven live 2026-09-20 via the CLI: one call left the lab lawn for the real
main menu (operator's own screenshot).

Never spawns test subjects, never fabricates a result -- every action is the literal real UIMgr
static method call; the injector doing nothing (`ok: true` when the game is unresponsive) or
already-there (harmless to repeat) is possible and is not caught here. Only a genuinely wrong or
unreachable `action` name, or the injector not being connected, are refused/surfaced as errors.
"""
import httpx

from tools.debug_call import call as route_call

_ROUTE = "/ui-nav"

# Kept in sync with DebugActions.UiNav's own switch (gk-fusion/src/FusionRpg.Injector/DebugActions.cs).
# Not authoritative -- the server is -- but lets debug_ui_nav refuse an obviously wrong action
# locally instead of spending a round trip to learn the same thing.
ACTIONS = (
    "back-to-menu", "enter-main-menu", "back-to-game", "enter-pause-menu", "enter-lose-menu",
    "enter-challenge-menu", "enter-classic-travel", "enter-travel-adv", "enter-travel-game",
    "enter-travel-challenge", "enter-treasure-menu", "enter-tower-menu", "enter-iz-menu",
    "enter-survival-e-menu", "menu-normal-settings", "enter-help-menu", "enter-other-menu",
    "enter-option-menu", "enter-explore-menu", "enter-almanac", "enter-garden", "enter-zuma",
)


def _not_ready(error, fix):
    return {"ok": False, "error": error, "fix": fix, "scope": "game-injector-debug"}


def nav(action, reason=None, transport=None, timeout=15):
    """Call one real UIMgr navigation method. Returns an envelope, never raises on env problems.

    `reason` is forwarded only for action="enter-lose-menu" (UIMgr.EnterLoseMenu(reason)); ignored
    for every other action, matching the injector's own DebugActions.UiNav switch.
    """
    if action not in ACTIONS:
        raise ValueError(f"unknown ui-nav action: {action!r} (known: {', '.join(ACTIONS)})")
    body = {"action": action}
    if action == "enter-lose-menu" and reason is not None:
        body["reason"] = reason
    try:
        result = route_call("POST", _ROUTE, body=body, transport=transport, timeout=timeout)
    except httpx.TimeoutException as ex:
        return _not_ready(f"ui-nav timed out: {ex}", "retry, or check the game is responsive")
    except (httpx.ConnectError, ConnectionError, TimeoutError) as ex:
        return _not_ready(f"server unreachable: {ex}",
                          "Start-Process dist\\FusionRpg.Server\\FusionRpg.Server.exe")
    if not result["ok"]:
        error = result["body"].get("error", "ui-nav refused") \
            if isinstance(result["body"], dict) else "ui-nav refused"
        if "not connected" in error:
            return _not_ready(error, "launch the game with the FusionRpg injector loaded, then retry")
        return _not_ready(error, error)
    payload = result["body"] if isinstance(result["body"], dict) else {}
    inner = payload.get("result", {}) if isinstance(payload.get("result"), dict) else {}
    return {
        "ok": bool(inner.get("ok", payload.get("ok", True))),
        "action": action,
        "error": inner.get("error"),
        "scope": result["scope"],
    }
