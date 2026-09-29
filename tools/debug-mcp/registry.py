"""Tool registry: scope labels + adapter mapping (spec: adapter-only).

SCOPES is closed (live-probe-standard two scopes). check_mapped() enforces
the adapter rule: every tool names an existing endpoint/service/CLI/file as
its source; an orphan (source None/empty) is reported, never passed.

Route allowlist is GENERATED at build from DebugEndpoints.cs registrations
(spec decision) and re-checked live by test_registry. Scope derivation mirrors
guard-debug-scope semantics: a 3-arg MapPost(g, path, "command") is a relay
wrapper by construction; otherwise any Send(hub/inbox, ...) in the handler
span makes the route game-injector-debug-shaped, while a persisted/domain
write with no relay makes it rpg-server-debug-shaped.
"""
import re
from pathlib import Path

SCOPES = ("game-injector-debug", "rpg-server-debug")

_ROUTE_RE = re.compile(
    r"(?:Map(?:Post|Get)\(\s*g\s*,\s*\"([^\"]+)\"|g\.Map(?:Post|Get)\(\s*\"([^\"]+)\")"
)
_GROUP_RE = re.compile(r"MapGroup\(\"([^\"]+)\"\)")
_GET_RE = re.compile(r"g\.MapGet\(\s*\"([^\"]+)\"")
# Note: registrations with a variable path (e.g. the MapPost helper's own
# definition, or g.MapPost(path, ...) with a computed path) cannot be listed
# statically and are deliberately excluded — the allowlist is literal routes.
_RELAY_RE = re.compile(r"Send\s*\(\s*(hub|inbox)\b")
_STORE_RE = re.compile(r"\b(store|grants|ua|ingest)\s*\.")

_allowlist_cache = None
_allowlist_cache_key = None


def check_mapped(tools):
    """Return names of tools with no mapped source. Empty roster passes."""
    return [t["name"] for t in tools if not t.get("source")]


def find_repo_root(start=None):
    """Nearest ancestor containing gk-core/src/FusionRpg.Server/DebugEndpoints.cs."""
    here = Path(start or Path.cwd()).resolve()
    for parent in (here, *here.parents):
        if (parent / "src" / "FusionRpg.Server" / "DebugEndpoints.cs").is_file():
            return parent
    raise FileNotFoundError("repo root not found (no src/FusionRpg.Server above cwd)")


def _handler_span(text, match_start):
    """Paren-matched span of the MapPost/MapGet call starting at match_start."""
    open_paren = text.index("(", match_start)
    depth = 0
    for i in range(open_paren, len(text)):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return text[match_start:i + 1]
    raise ValueError("unbalanced parens in route registration")


def _classify(span):
    if span.count(",") >= 2 and "=>" not in span and "async" not in span:
        return "game-injector-debug"  # 3-arg relay wrapper
    if _RELAY_RE.search(span):
        return "game-injector-debug"
    if _STORE_RE.search(span):
        return "rpg-server-debug"
    return None


def _endpoints_files(root):
    base = find_repo_root(root) / "src" / "FusionRpg.Server"
    return sorted(base.glob("*Endpoints.cs"))


def _cache_key(paths):
    """(path, mtime) per *Endpoints.cs file -- changes the instant any one is edited."""
    return tuple((str(p), p.stat().st_mtime) for p in paths)


def load_allowlist(root=None):
    """Map route path -> scope (or None when unclassifiable).

    Cached, but the cache is invalidated the moment any *Endpoints.cs file's
    mtime changes -- a plain "cache forever" cache was proven live 2026-09-14
    to hide newly-added routes (/game-state, /ui-nav) for the lifetime of this
    long-running stdio process: routes added mid-session to DebugEndpoints.cs
    stayed invisible to debug_call ("route not in debug allowlist") until the
    whole MCP server process was restarted, even though debug_preflight and
    every other tool kept working against the live server the whole time.
    Re-reading on every call would be wrong for a different reason (a partial
    edit mid-save could parse as unbalanced parens); mtime comparison is the
    same cheap freshness check `commit-tool`'s `_reload_tool_modules` already
    uses for exactly this class of "stale in-process state" problem.

    DebugEndpoints.cs contributes MapPost + MapGet (the debug surface,
    including writes). Every other *Endpoints.cs contributes MapGet only:
    reads ride the normal query paths (live-probe read-back rule); writes
    stay on the debug surface. Keys are full paths; /api/debug routes are
    additionally keyed relative for backward compatibility.
    """
    global _allowlist_cache, _allowlist_cache_key
    paths = _endpoints_files(root)
    key = _cache_key(paths)
    if _allowlist_cache is not None and _allowlist_cache_key == key:
        return _allowlist_cache
    routes = {}
    for path in paths:
        text = path.read_text(encoding="utf-8-sig")
        debug_file = path.name == "DebugEndpoints.cs"
        group_match = _GROUP_RE.search(text)
        group = group_match.group(1) if group_match else ""
        pattern = _ROUTE_RE if debug_file else _GET_RE
        for match in pattern.finditer(text):
            route = match.group(1) or match.group(2) if debug_file else match.group(1)
            span = _handler_span(text, match.start())
            scope = _classify(span)
            routes[group + route] = scope
            if debug_file and group == "/api/debug":
                routes[route] = scope
    _allowlist_cache = routes
    _allowlist_cache_key = key
    return routes


def scope_for(route, routes=None):
    """Scope label for a registered route (None when unclassifiable)."""
    return (routes if routes is not None else load_allowlist()).get(route)

