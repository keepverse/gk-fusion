"""debug_restart_game: close and relaunch the game process, poll for a fresh injector connection.

Adapter over scripts/restart-game.ps1 -- deliberately NOT reimplemented in Python. The script is
the single source of truth for this recovery path (real ground-truth polling: /health +
injectorConnected + a heartbeat newer than the pre-restart snapshot, output-flushed so progress is
visible even when piped -- both fixed live 2026-09-14 after real incidents). Duplicating that logic
here would fork it the same way a second debug surface would (AGENTS.md: adapter-wrap existing
services, never re-implement them).

DISRUPTIVE: this closes the running PlantsVsZombiesRH.exe process and starts a new one. It is the
easiest, most reliable recovery for a stuck/defeated board (a fresh process cannot carry over a
stale Board/InitBoard reference the way in-place `debug_ui_nav` navigation might -- see
lawn-run-state-machine.md), but it does end whatever the operator was looking at. Prefer this over
chasing `debug_ui_nav` in place when a provably clean board matters more than speed.

Multi-session safety lives in the script, not here: restart-game.ps1 closes ONLY a game running
from the target install (path-scoped, never kill-by-image-name) and refuses an install held by
another live session's game lock. Pass this session's game install (`game_dir`), its server
(`base_url`, loopback only) and its session id (`session`) so the restart hits the right game.
"""
import subprocess
import tempfile
from pathlib import Path
from urllib.parse import urlparse

import registry

_LOOPBACK_HOSTS = ("127.0.0.1", "localhost", "::1")


def _default_runner(args, timeout):
    """Capture into temporary files, never pipes (lawn-combat-wire L-N26, live 2026-09-15).

    The script launches the game, and a launched process can inherit the script's output handles. With
    pipes, `subprocess.run` waits for end-of-file that never comes while the game runs, and after a
    timeout it waits again with no limit — `debug_restart_game` ran 30 minutes without answering. A file
    has no end-of-file to wait for, so the call returns when the script exits.
    """
    with tempfile.TemporaryFile() as out_file, tempfile.TemporaryFile() as err_file:
        completed = subprocess.run(args, stdout=out_file, stderr=err_file, timeout=timeout, check=False)
        out_file.seek(0)
        err_file.seek(0)
        return (completed.returncode,
                out_file.read().decode("utf-8", errors="replace"),
                err_file.read().decode("utf-8", errors="replace"))


def restart(timeout_sec=120, root=None, runner=None, game_dir=None, base_url=None,
            session=None):
    """Run scripts/restart-game.ps1. Returns an envelope; only caller misuse raises.

    `timeout_sec` is passed through to the script's own bounded poll; the subprocess itself is
    given `timeout_sec + 30` headroom for process-spawn overhead before this call gives up on it.
    `game_dir` selects which installed game to restart (default: the script's own default);
    `base_url` selects which server to poll (loopback only, default :5088); `session` lets the
    script's game-lock check recognise its own holder. Omit all three only when probing the
    default single-game setup.
    """
    if not isinstance(timeout_sec, int) or timeout_sec < 1:
        raise ValueError("timeout_sec must be a positive int")
    if game_dir is not None and (not isinstance(game_dir, str) or not game_dir.strip()):
        raise ValueError("game_dir must be a non-empty path string")
    if session is not None and (not isinstance(session, str) or not session.strip()):
        raise ValueError("session must be a non-empty session id string")
    if base_url is not None:
        parsed = urlparse(base_url)
        if parsed.scheme not in ("http", "https") or parsed.hostname not in _LOOPBACK_HOSTS:
            raise ValueError(f"base_url must be a loopback http URL, got: {base_url}")
        base_url = base_url.rstrip("/")
    root = Path(root) if root else registry.find_repo_root()
    script = root / "scripts" / "restart-game.ps1"
    if not script.is_file():
        return {"ok": False, "error": f"missing {script}",
                "fix": "this script should already exist -- check the worktree/branch",
                "scope": "local-machine"}

    args = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
            "-TimeoutSec", str(timeout_sec)]
    if game_dir is not None:
        args += ["-GameDir", game_dir]
    if base_url is not None:
        args += ["-BaseUrl", base_url]
    if session is not None:
        args += ["-Session", session]
    run = runner or _default_runner
    subprocess_timeout = timeout_sec + 30
    try:
        code, out, err = run(args, subprocess_timeout)
    except subprocess.TimeoutExpired:
        return {"ok": False,
                "error": f"restart-game.ps1 did not finish within {subprocess_timeout}s",
                "fix": "check the game/server manually -- the script or game may still be starting",
                "scope": "local-machine"}
    return {
        "ok": code == 0,
        "exitCode": code,
        "stdout": out[-4000:] if out else "",
        "stderr": err[-2000:] if err else "",
        "scope": "local-machine",
    }
