"""debug_lawn_setup: one-call live-board setup (todo T10).

Adapter over POST /api/debug/lawn/quick-start (DebugEndpoints.cs:173):
enter level, lab-overlay scenario, wave-freeze, snapshot poll. Environment
problems return NOT-READY data (ready=False + fix command); only caller
misuse raises. Never spawns test subjects; setup only, never a feature proof.
"""
import httpx

from tools.debug_call import call as route_call

_ROUTE = "/lawn/quick-start"

_FIXES = (
    ("injector not connected",
     "launch the game with the FusionRpg injector loaded, then retry"),
    ("did not ack",
     "return to the main menu and re-run (snapshot poll missed the ack)"),
    ("board is cycling",
     "the board is losing every wave in a tight loop (no real plants placed) -- place real "
     "plants before retrying, or use scripts/restart-game.ps1 for a clean board"),
)


def _not_ready(error, fix):
    return {"ready": False, "error": error, "fix": fix,
            "targetPtr": None, "plantPtr": None,
            "scope": "game-injector-debug"}


def setup(scenario="lab-overlay", level=1, timeout=60, transport=None):
    """Set up a live lab board. Returns ready envelope (never raises on env)."""
    if not isinstance(level, int) or level < 1:
        raise ValueError("level must be a positive int")
    if not isinstance(timeout, int) or timeout < 1:
        raise ValueError("timeout must be a positive int")
    body = {"scenario": scenario, "levelNumber": level, "timeoutSec": timeout}
    try:
        result = route_call("POST", _ROUTE, body=body, transport=transport,
                            timeout=timeout + 30)
    except httpx.TimeoutException as ex:
        return _not_ready(f"board setup timed out: {ex}",
                          "retry with a smaller timeout, or check the game is responsive")
    except (httpx.ConnectError, ConnectionError, TimeoutError) as ex:
        return _not_ready(f"server unreachable: {ex}",
                          "Start-Process dist\\FusionRpg.Server\\FusionRpg.Server.exe")
    if not result["ok"]:
        error = result["body"].get("error", "quick-start refused") \
            if isinstance(result["body"], dict) else "quick-start refused"
        for marker, fix in _FIXES:
            if marker in error:
                return _not_ready(error, fix)
        return _not_ready(error, error)
    payload = result["body"] if isinstance(result["body"], dict) else {}
    return {
        "ready": True,
        "entered": payload.get("entered"),
        "levelType": payload.get("levelType"),
        "targetPtr": payload.get("targetPtr"),
        "plantPtr": payload.get("plantPtr"),
        # Real Unity plant/zombie counts + liveState, read by the server the same request
        # (debug.game-state) -- so a caller sees whether the board is ACTUALLY live without a
        # separate debug_game_state round trip. None when that read itself failed/timed out;
        # never a substitute for calling debug_game_state again if this comes back null.
        "liveEntities": payload.get("liveEntities"),
        "note": payload.get("note"),
        "scope": result["scope"],
    }
