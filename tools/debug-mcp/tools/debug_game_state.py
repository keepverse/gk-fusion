"""debug_game_state: the direct "where are we, right now" read.

Adapter over POST /api/debug/game-state (DebugEndpoints.cs). This is the primitive the whole
2026-09-14 live-probe session was built around: `debug.snapshot`'s tracked `matchPhase` (also read
by `debug_preflight.live_state`) is a Match.MatchHost.Runtime FSM that can desync from the real
game -- proven live the same day, reporting "InMatch" with living entities while the operator's
screen showed a genuine defeat. `/game-state` instead reads Board.Instance/InitBoard.Instance/
GameAPP.theBoardType and real Unity FindObjectsOfType<Plant/Zombie> counts directly, synchronously,
no event-log history involved, and flags `phaseMismatch` when the tracked phase and the real counts
disagree rather than silently trusting either one.

Known blind spot (see docs/architecture/live-probe/lawn-run-state-machine.md): `hasBoard`/
`hasInitBoard` do not clear after a genuine return to the main menu -- read `liveState`/
`theBoardType`/`sceneType` for "did we actually leave the board", never `hasBoard` alone.
"""
import httpx

from tools.debug_call import call as route_call

_ROUTE = "/game-state"


def _not_ready(error, fix):
    return {"ok": False, "error": error, "fix": fix, "scope": "game-injector-debug"}


def state(transport=None, timeout=15):
    """Read the current live game state. Returns an envelope, never raises on env problems."""
    try:
        result = route_call("POST", _ROUTE, body={}, transport=transport, timeout=timeout)
    except httpx.TimeoutException as ex:
        return _not_ready(f"game-state timed out: {ex}", "retry, or check the game is responsive")
    except (httpx.ConnectError, ConnectionError, TimeoutError) as ex:
        return _not_ready(f"server unreachable: {ex}",
                          "Start-Process dist\\FusionRpg.Server\\FusionRpg.Server.exe")
    if not result["ok"]:
        error = result["body"].get("error", "game-state refused") \
            if isinstance(result["body"], dict) else "game-state refused"
        if "not connected" in error:
            return _not_ready(error, "launch the game with the FusionRpg injector loaded, then retry")
        return _not_ready(error, error)
    payload = result["body"] if isinstance(result["body"], dict) else {}
    live = payload.get("live", {}) if isinstance(payload.get("live"), dict) else {}
    return {
        "ok": bool(live.get("ok", payload.get("ok", True))),
        "hasBoard": live.get("hasBoard"),
        "hasInitBoard": live.get("hasInitBoard"),
        "theBoardType": live.get("theBoardTypeName"),
        "sceneType": live.get("sceneType"),
        "matchPhase": live.get("matchPhase"),
        "phaseMismatch": live.get("phaseMismatch"),
        "plantCount": live.get("plantCount"),
        "zombieCount": live.get("zombieCount"),
        "liveState": live.get("liveState"),
        "scope": result["scope"],
    }
