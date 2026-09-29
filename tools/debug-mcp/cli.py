"""Debug MCP CLI: stdio-free access to the same 19 adapters for MCP-less agents.

Same functions as server.py (no logic here either) — `python
gk-fusion/tools/debug-mcp/cli.py <tool> --json '{...}'` prints the result envelope as
JSON on stdout. `python gk-fusion/tools/debug-mcp/cli.py --list` names the tools.

Why this exists: agents without MCP support (e.g. pi) cannot spawn the MCP
server, but they can run a shell. One subprocess per call keeps it
stateless; the server at FUSIONRPG_SERVER_URL (default 127.0.0.1:5088)
holds all state, so nothing is lost between calls — except snapshotId/ref
pairs, which die with their inspect snapshot by design (re-inspect).

Examples (repo root):
  python gk-fusion/tools/debug-mcp/cli.py --list
  python gk-fusion/tools/debug-mcp/cli.py debug_preflight
  python gk-fusion/tools/debug-mcp/cli.py debug_game_state
  python gk-fusion/tools/debug-mcp/cli.py debug_call --json '{"method":"GET","route":"/effects/contract"}'
  python gk-fusion/tools/debug-mcp/cli.py debug_act --json '{"verb":"shovel","col":2,"row":2}'
  python gk-fusion/tools/debug-mcp/cli.py debug_click --param snapshotId=snap1 --param ref=c0
  python gk-fusion/tools/debug-mcp/cli.py debug_screenshot --json '{"tag":"probe"}' --pretty

Exit codes: 0 = adapter answered (even ok:false — that is a valid envelope,
read its fix); 1 = unexpected failure (JSON error envelope still on stdout);
2 = usage error (unknown tool, bad JSON, unknown argument).
"""
import argparse
import json
import sys

from tools.debug_call import call as debug_call_impl
from tools.debug_events import tail as debug_events_impl
from tools.debug_match import digest as debug_match_impl
from tools.debug_actor import snapshot as debug_actor_impl
from tools.debug_verify import verify as debug_verify_impl
from tools.debug_preflight import audit as debug_preflight_impl
from tools.debug_lawn_setup import setup as debug_lawn_setup_impl
from tools.debug_ui_nav import nav as debug_ui_nav_impl
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


def _preflight(**params):
    return debug_preflight_impl(include_live=True, **params)


TOOLS = {
    "debug_call": (debug_call_impl, "Invoke one allowlisted debug/sim/test route."),
    "debug_events": (debug_events_impl, "Tail event envelopes by kind, budgeted with cursor paging."),
    "debug_match": (debug_match_impl, "Digest the live match snapshot."),
    "debug_actor": (debug_actor_impl, "Snapshot one actor by instanceId XOR ptr."),
    "debug_verify": (debug_verify_impl, "Run one feature pipeline ladder to the first broken link."),
    "debug_preflight": (_preflight, "Audit deploy readiness and live state (read-only)."),
    "debug_lawn_setup": (debug_lawn_setup_impl, "Set up a live lab board (mutates the live lawn)."),
    "debug_ui_nav": (debug_ui_nav_impl, "Call one real UIMgr navigation method."),
    "debug_menu_home": (debug_menu_home_impl, "Walk the real UI to the true main menu in one call (default recovery)."),
    "debug_restart_game": (debug_restart_game_impl, "DISRUPTIVE: close and relaunch the game process."),
    "debug_game_state": (debug_game_state_impl, "Read live Board/InitBoard state and entity counts."),
    "debug_screenshot": (debug_screenshot_impl, "Capture the live Unity frame as base64 PNG."),
    "debug_inspect": (debug_inspect_impl, "Inspect the live screen as a budgeted control tree."),
    "debug_click": (debug_click_impl, "Click a control ref from the current inspect snapshot."),
    "debug_act": (debug_act_impl, "Run one lawn verb with a named receipt."),
    "debug_cursor": (debug_cursor_impl, "DISRUPTIVE: move the REAL OS cursor (needs confirmed=true)."),
    "debug_evaluate_search": (debug_evaluate_search_impl, "Find controls by name/type/path."),
    "debug_evaluate_methods": (debug_evaluate_methods_impl, "List public instance methods of a resolved object."),
    "debug_evaluate_call": (debug_evaluate_call_impl, "Invoke one public instance method on a resolved component."),
    "debug_evaluate_text": (debug_evaluate_text_impl, "Find visible text and resolve the clickable behind it."),
}


def _coerce(value):
    """Parse a --param value as JSON, falling back to the raw string.

    Numbers, booleans, null, objects, and arrays pass through as their real
    types; bare words stay strings (so --param ref=c0 needs no quoting).
    """
    try:
        return json.loads(value)
    except (json.JSONDecodeError, ValueError):
        return value


def parse_params(json_text=None, param_list=None):
    """Merge --json and repeatable --param key=value into one kwargs dict."""
    params = {}
    if json_text:
        try:
            loaded = json.loads(json_text)
        except json.JSONDecodeError as ex:
            raise ValueError(f"--json is not valid JSON: {ex}")
        if not isinstance(loaded, dict):
            raise ValueError("--json must be an object of tool arguments")
        params.update(loaded)
    for item in param_list or ():
        if "=" not in item:
            raise ValueError(f"--param must be key=value, got: {item!r}")
        key, _, value = item.partition("=")
        if not key.strip():
            raise ValueError(f"--param must be key=value, got: {item!r}")
        params[key.strip()] = _coerce(value)
    return params


def build_parser():
    parser = argparse.ArgumentParser(
        prog="debug-mcp-cli",
        description="Call one debug-mcp adapter tool and print its JSON envelope.")
    parser.add_argument("--list", action="store_true",
                        help="print the tool catalogue as JSON and exit")
    parser.add_argument("--pretty", action="store_true",
                        help="indent the output JSON")
    parser.add_argument("tool", nargs="?", help="adapter tool name (see --list)")
    parser.add_argument("--json", default=None,
                        help="tool arguments as a JSON object, e.g. '{\"route\": \"/effects/contract\"}'")
    parser.add_argument("--param", action="append", default=[],
                        help="one tool argument as key=value (repeatable; value parsed as JSON when possible)")
    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.list:
        catalogue = [{"name": name, "description": desc} for name, (_, desc) in sorted(TOOLS.items())]
        print(json.dumps(catalogue, ensure_ascii=False, indent=2 if args.pretty else None))
        return 0
    if not args.tool:
        parser.print_usage(sys.stderr)
        print(json.dumps({"ok": False, "error": "no tool named (see --list)",
                          "scope": None}, ensure_ascii=False))
        return 2
    entry = TOOLS.get(args.tool)
    if entry is None:
        print(json.dumps({"ok": False, "error": f"unknown tool: {args.tool} (see --list)",
                          "scope": None}, ensure_ascii=False))
        return 2
    func, _ = entry
    try:
        params = parse_params(args.json, args.param)
    except ValueError as ex:
        print(json.dumps({"ok": False, "error": str(ex), "scope": None}, ensure_ascii=False))
        return 2
    try:
        result = func(**params)
    except TypeError as ex:
        # Wrong argument names — caller misuse, say so with the signature hint.
        print(json.dumps({"ok": False, "error": f"bad arguments for {args.tool}: {ex}",
                          "scope": None}, ensure_ascii=False))
        return 2
    except ValueError as ex:
        # Allowlist refusals, non-loopback base URL, unknown ui-nav action, ...
        print(json.dumps({"ok": False, "error": str(ex), "scope": None}, ensure_ascii=False))
        return 1
    except Exception as ex:  # noqa: BLE001 — the CLI must never die without a parseable envelope
        print(json.dumps({"ok": False, "error": f"{type(ex).__name__}: {ex}",
                          "scope": None}, ensure_ascii=False))
        return 1
    print(json.dumps(result, ensure_ascii=False, default=str,
                     indent=2 if args.pretty else None))
    return 0


if __name__ == "__main__":
    sys.exit(main())
