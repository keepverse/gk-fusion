"""Debug MCP server: registration only, no logic (spec Project Structure).

Transports: stdio default (per-session child, commit-tool precedent);
local-HTTP mode behind --transport http (port 8899, localhost bind only —
non-loopback is refused: Auth=None row, localhost only).
"""
import argparse
import sys
from typing import Any, Optional

from fastmcp import FastMCP

from tools.debug_call import call as debug_call_impl
from tools.debug_events import tail as debug_events_impl
from tools.debug_match import digest as debug_match_impl
from tools.debug_actor import snapshot as debug_actor_impl
from tools.debug_verify import verify as debug_verify_impl
from tools.debug_preflight import audit as debug_preflight_impl
from tools.debug_lawn_setup import setup as debug_lawn_setup_impl
from tools.debug_ui_nav import nav as debug_ui_nav_impl, ACTIONS as _UI_NAV_ACTIONS
from tools.debug_menu_home import home as debug_menu_home_impl
from tools.debug_restart_game import restart as debug_restart_game_impl
from tools.debug_game_state import state as debug_game_state_impl
from tools.debug_screenshot import screenshot as debug_screenshot_impl
from tools.debug_inspect import inspect as debug_inspect_impl
from tools.debug_click import click as debug_click_impl
from tools.debug_act import act as debug_act_impl
from tools.debug_cursor import cursor as debug_cursor_impl
from tools.debug_evaluate_search import search as debug_evaluate_search_impl
from tools.debug_evaluate_methods import methods as debug_evaluate_methods_impl
from tools.debug_evaluate_call import call as debug_evaluate_call_impl
from tools.debug_evaluate_text import text as debug_evaluate_text_impl

mcp = FastMCP("debug-mcp")

_LOOPBACK = ("127.0.0.1", "localhost", "::1")
_DEFAULT_PORT = 8899


@mcp.tool(description="Invoke one allowlisted debug/sim/test route and return its body scope-stamped.")
def debug_call(method: str, route: str, body: Optional[dict[str, Any]] = None) -> dict:
    """Scope: stamped per route from guard-debug-scope derivation."""
    return debug_call_impl(method, route, body)


@mcp.tool(description="Tail event envelopes by kind, budgeted with cursor paging.")
def debug_events(kind: Optional[str] = None, match_key: Optional[str] = None, limit: int = 20,
                 cursor: Optional[str] = None) -> dict:
    """Scope: rpg-server-debug (server event query path)."""
    return debug_events_impl(kind, match_key, limit, cursor)


@mcp.tool(description="Digest the live match snapshot: phase, hash, capped entity digests.")
def debug_match(match_key: Optional[str] = None, entity_limit: int = 20,
                cursor: Optional[str] = None) -> dict:
    """Scope: stamped from the snapshot route derivation."""
    return debug_match_impl(match_key, entity_limit, cursor)


@mcp.tool(description="Snapshot one actor by instanceId XOR ptr: record, Hub audit, events, verdicts.")
def debug_actor(instance_id: Optional[str] = None, ptr: Optional[str] = None) -> dict:
    """Scope: stamped from the Hub-audit route derivation. Read-only Hub consume."""
    return debug_actor_impl(instance_id, ptr)


@mcp.tool(description="Run one feature pipeline ladder to the first broken link, with evidence.")
def debug_verify(feature: str, subject: str) -> dict:
    """Closed feature set (actor, summon). Fabricated subjects fail at the subject rung."""
    return debug_verify_impl(feature, subject)


@mcp.tool(description="Audit deploy readiness and live state: server, injector, fresh board observation, artifacts.")
def debug_preflight() -> dict:
    """Read-only. Separates deploy readiness from server/injector/board liveness."""
    return debug_preflight_impl(include_live=True)


@mcp.tool(description="Set up a live lab board: enter level, freeze waves, poll target ptrs.")
def debug_lawn_setup(scenario: str = "lab-overlay", level: int = 1,
                     timeout: int = 60) -> dict:
    """Scope: game-injector-debug orchestration. Setup only, never a proof."""
    return debug_lawn_setup_impl(scenario, level, timeout)


@mcp.tool(description=(
    "DEFAULT menu recovery: walk the real UI to the true main menu in one call -- open the "
    "pause menu, click its own main-menu button, click the quit-confirm. Verified-only success: "
    "the confirm dialog must be observed gone AND a main-menu marker present. "
    "debug_ui_nav back-to-menu is buggy (stale layers) -- do not use it."
))
def debug_menu_home(timeout: int = 90) -> dict:
    """Scope: game-injector-debug. Adapter composing ui-nav + evaluate-text + evaluate-call."""
    return debug_menu_home_impl(timeout)


@mcp.tool(description=(
    "Call one real UIMgr navigation method (enter-main-menu, back-to-game, etc.) "
    "for single-menu hops. NOT for returning to the main menu: back-to-menu lands on stale "
    "layers (buggy) -- use debug_menu_home instead. Actions: " + ", ".join(_UI_NAV_ACTIONS)
))
def debug_ui_nav(action: str, reason: Optional[str] = None) -> dict:
    """Scope: game-injector-debug. Real UIMgr call, never fabricated; refuses an unknown action
    locally before any round trip."""
    return debug_ui_nav_impl(action, reason=reason)


@mcp.tool(description=(
    "Close and relaunch the game process, poll for a fresh injector connection. "
    "Allowed by default on the session's OWN game install only: the script closes just "
    "the game running from the target install and refuses an install held by another "
    "live session's game lock -- it never kills by bare process name. Pass game_dir + "
    "base_url + session for a clone/own-server setup; omit them only for the default "
    "single-game setup. Ends whatever that game was showing."
))
def debug_restart_game(timeout_sec: int = 120, game_dir: Optional[str] = None,
                       base_url: Optional[str] = None,
                       session: Optional[str] = None) -> dict:
    """Scope: local-machine. Adapter over scripts/restart-game.ps1 -- not reimplemented here."""
    return debug_restart_game_impl(timeout_sec, game_dir=game_dir, base_url=base_url,
                                   session=session)


@mcp.tool(description=(
    "Read the current live game state directly: Board/InitBoard, real Unity Plant/Zombie counts, "
    "and liveState (SeedPicker/InMatch/Paused/MatchEndedBoardStillAlive/etc). Use this, not debug_preflight's "
    "matchPhase reading, when you need to know 'is a match really running right now' -- flags "
    "phaseMismatch when the tracked FSM and the real entity counts disagree instead of trusting "
    "either silently."
))
def debug_game_state() -> dict:
    """Scope: game-injector-debug. Synchronous read of live Unity objects, no event-log history."""
    return debug_game_state_impl()


@mcp.tool(description=(
    "Capture the live Unity game frame and return it as base64 PNG (Playwright "
    "browser_screenshot analogue): trigger POST /api/debug/screenshot, poll for "
    "debug.screenshot.ready, fetch the stored frame. Shows what the engine "
    "renders -- proves nothing about the server. Optional save_to writes the "
    "PNG to disk as well."
))
def debug_screenshot(tag: str = "probe", timeout: int = 30,
                     save_to: Optional[str] = None) -> dict:
    """Scope: game-injector-debug. Adapter over the screenshot endpoints only."""
    return debug_screenshot_impl(tag=tag, timeout=timeout, save_to=save_to)


@mcp.tool(description=(
    "Inspect the live game screen as a budgeted control tree (Playwright "
    "browser_snapshot analogue): menus as named controls, lawn as "
    "cells/cards/entities. Only interactables get refs; over-budget output "
    "truncates with a marker and a cursor."
))
def debug_inspect(scope: str = "all", limit: int = 50,
                  cursor: Optional[str] = None) -> dict:
    """Scope: game-injector-debug. Adapter over POST /api/debug/inspect."""
    return debug_inspect_impl(scope=scope, limit=limit, cursor=cursor)


@mcp.tool(description=(
    "Click a control ref from the current inspect snapshot (Playwright "
    "browser_click analogue). Refs die with their snapshot; stale refs are "
    "refused, never retried blind."
))
def debug_click(snapshotId: str, ref: str) -> dict:
    """Scope: game-injector-debug. Adapter over POST /api/debug/click."""
    return debug_click_impl(snapshotId, ref)


@mcp.tool(description=(
    "Run one lawn verb with a named receipt: place (card to cell), shovel, "
    "hammer. Verbs compose click primitives; failures name the broken link."
))
def debug_act(verb: str, typeId: Optional[int] = None,
              col: Optional[int] = None, row: Optional[int] = None) -> dict:
    """Scope: game-injector-debug. Adapter over POST /api/debug/act."""
    params: dict[str, Any] = {}
    if typeId is not None:
        params["typeId"] = typeId
    if col is not None:
        params["col"] = col
    if row is not None:
        params["row"] = row
    return debug_act_impl(verb, **params)


@mcp.tool(description=(
    "DISRUPTIVE: move the REAL OS cursor, optionally click. Per-call opt-in "
    "(`confirmed=True`), game-foreground check, throttled, clamped to the game "
    "window. Ends whatever the operator was pointing at."
))
def debug_cursor(x: int, y: int, click: bool = False,
                 confirmed: bool = False) -> dict:
    """Scope: game-injector-debug. Adapter over POST /api/debug/cursor."""
    return debug_cursor_impl(x, y, click=click, confirmed=confirmed)


@mcp.tool(description="Find controls by name/type/path (locator building block).")
def debug_evaluate_search(nameContains: str = "", type: str = "",
                          pathContains: str = "", limit: int = 50) -> dict:
    """Scope: game-injector-debug. Read-only; matches carry ptrs."""
    return debug_evaluate_search_impl(
        nameContains=nameContains, type=type, pathContains=pathContains,
        limit=limit)


@mcp.tool(description="List public instance methods of a resolved object.")
def debug_evaluate_methods(ptr: str) -> dict:
    """Scope: game-injector-debug. Read-only discovery for evaluate-call."""
    return debug_evaluate_methods_impl(ptr)


@mcp.tool(description=(
    "Invoke one public instance method on a resolved component "
    "(Playwright browser_evaluate analogue, bounded: live ptr + allowlisted "
    "shape + JSON primitives only)."
))
def debug_evaluate_call(ptr: str, method: str, type: str = "",
                        args: Optional[list] = None) -> dict:
    """Scope: game-injector-debug. Reports what ran, via which path."""
    return debug_evaluate_call_impl(ptr, method, type=type, args=args)


@mcp.tool(description=(
    "Find visible text and resolve the clickable behind it (no OCR — engine "
    "text content read directly). Bare paint returns the nearby tree instead."
))
def debug_evaluate_text(text: str, limit: int = 50) -> dict:
    """Scope: game-injector-debug. Read-only search with clickable resolution."""
    return debug_evaluate_text_impl(text, limit=limit)


def resolve_transport(argv=None):
    """Parse transport flags. Non-loopback bind is refused (never served)."""
    parser = argparse.ArgumentParser(prog="debug-mcp")
    parser.add_argument("--transport", choices=("stdio", "http"), default="stdio")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=_DEFAULT_PORT)
    args = parser.parse_args(argv)
    if args.host not in _LOOPBACK:
        raise ValueError(f"refusing non-loopback bind: {args.host} (localhost only)")
    return {"transport": args.transport, "host": args.host, "port": args.port}


def main(argv=None):
    cfg = resolve_transport(argv)
    if cfg["transport"] == "http":
        mcp.run(transport="http", host=cfg["host"], port=cfg["port"])
    else:
        mcp.run()


if __name__ == "__main__":
    main(sys.argv[1:])
