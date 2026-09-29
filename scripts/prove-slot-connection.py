#!/usr/bin/env python3
"""Prove a POOLED live path end to end, deterministically, with hard timeouts.

This is the Python port of `scripts/prove-slot-connection.ps1` (owner ruling 2026-09-25: new tools
are Python, never PowerShell; see AGENTS.md "Language for new tooling"). It keeps every contract the
PowerShell version established and fixes the class of defect that made that version unreliable for an
agent:

* **No stream-6 loss.** PowerShell's `Write-Host` writes the INFORMATION stream, so `2>&1` captured
  nothing from a script that was working correctly -- the reason an agent could run the probe and
  receive *no output at all*. `subprocess.run(capture_output=True)` here captures both streams and
  every child's output is printed.
* **A hard timeout on every external call, and a named refusal instead of a hang.** Each stage has
  its own budget; a stage that exceeds it fails BY NAME with the child's output so far.
* **Preflight before anything expensive.** Every precondition is checked first and a missing one is a
  refusal with a non-zero exit *before* any deploy, server start or process kill. The PowerShell
  version discovered these one at a time, mid-run, after minutes of work.
* **A machine-readable verdict** (`--json`) plus a non-zero exit naming the failing stage, so a
  caller never has to parse prose.

The evidence contract is unchanged, and both halves are still required, read from two different
processes on purpose (the 2026-09-13 incident: a probe read a feature's state back through the same
injector that fabricated it and reported ok:true for a feature that was broken):

  1. the injector's own line -- `<slot>\\MelonLoader\\Latest.log` contains
     `MelonMod host ready, server=<url>` naming THIS slot's url
  2. the slot SERVER's own log showing this client arriving (`Connection id`/`Request id`, or the
     server's own domain lines as a transport-independent witness)

A client that ARRIVED is not a client that was SERVED, so server-side failure lines are counted and
reported as a separate `data_path_healthy` verdict -- one combined line would have hidden the
2026-09-22 schema-drift defect.

Usage (machine-specific values come from the environment or the flags, never from this file):

    $env:FUSIONRPG_GAME_POOL   = '<pool root>'        # or --pool-root
    $env:FUSIONRPG_GAME_SOURCE = '<install to clone>' # or --source-install
    python gk-fusion/scripts/prove-slot-connection.py --session <id>

    python gk-fusion/scripts/prove-slot-connection.py --preflight        # config + pool + packs, no side effects
    python gk-fusion/scripts/prove-slot-connection.py --session x --json # machine-readable verdict
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = REPO_ROOT / "scripts"


# ------------------------------------------------------------------------------------------------
# Refusal: a precondition failed. Always fail CLOSED -- never continue and report empty.
# ------------------------------------------------------------------------------------------------

class Refusal(Exception):
    """A named precondition failure. `stage` names where; the exit code is non-zero by contract."""

    def __init__(self, stage: str, reason: str, detail: str = "") -> None:
        super().__init__(f"[{stage}] {reason}" + (f"\n{detail}" if detail else ""))
        self.stage = stage
        self.reason = reason
        self.detail = detail


# ------------------------------------------------------------------------------------------------
# Child processes: one place owns capture, timeout and tree-kill.
# ------------------------------------------------------------------------------------------------

def run(args: list[str], *, stage: str, timeout: float, echo: bool = True,
        env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    """Run a child with a HARD timeout; on expiry kill its whole tree and refuse by name.

    Windows note: `subprocess.run(timeout=...)` kills only the direct child, so a grandchild (a
    server the child spawned) would survive. We start a new process group and kill the tree.
    `env` is merged onto the CHILD only, so a stage-specific value never leaks to the next stage.
    """
    merged = dict(os.environ)
    if env:
        merged.update(env)
    started = time.monotonic()
    if echo:
        print(f"  $ {' '.join(args[:3])}{' …' if len(args) > 3 else ''}", flush=True)
    proc = subprocess.Popen(  # noqa: S603 - fixed argv, no shell
        args, cwd=str(REPO_ROOT), env=merged,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        encoding="utf-8", errors="replace",
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
    )
    try:
        out, err = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        kill_tree(proc.pid)
        try:
            out, err = proc.communicate(timeout=15)
        except subprocess.TimeoutExpired:
            out, err = "", ""
        raise Refusal(
            stage, f"exceeded its {timeout:.0f}s budget and was killed (pid {proc.pid})",
            (out or "")[-2000:] + (err or "")[-2000:],
        ) from None
    if echo:
        for line in (out or "").splitlines()[-14:]:
            print("    " + line, flush=True)
        for line in (err or "").splitlines()[-8:]:
            print("    ! " + line, flush=True)
    return subprocess.CompletedProcess(args, proc.returncode, out or "", err or "")


def kill_tree(pid: int) -> None:
    """Kill a pid and its descendants. taskkill /T on Windows, SIGTERM group elsewhere."""
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],  # noqa: S603,S607
                       capture_output=True, text=True, timeout=30)
    else:
        import signal
        try:
            os.killpg(os.getpgid(pid), signal.SIGTERM)
        except Exception:  # noqa: BLE001 - best effort while already failing
            pass


def powershell(script: str, *args: str, stage: str, timeout: float,
               env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    """Invoke an EXISTING repo .ps1 (deploy-play / lane-server).

    The owner ruling forbids NEW PowerShell; these two are the deployed pipeline and are called,
    not reimplemented. `env` is merged onto the CHILD only, so a value a stage needs (deploy-play's
    pooled target) never leaks into this process or the next stage.
    """
    return run(["pwsh", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", str(SCRIPTS / script), *args], stage=stage, timeout=timeout, env=env)


def live_slot(*args: str, stage: str, timeout: float,
              env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    """Invoke the live-probe pool manager, which is `gk-core/scripts/live_slot.py` (2026-09-26).

    This used to shell `live-slot.ps1`. That tool is ported, `live_slot.py` accepts both the
    PowerShell flag spelling and a GNU one, and the port carries the two fixes the `.ps1` lacked:
    a clone now stamps the slot's OWN server URL into its cfg instead of inheriting the source
    install's, and a `-Force` re-clone can delete a tree holding trailing-space directory names
    instead of wedging the slot `broken`. Calling the port is therefore not a rename — it is the
    difference between a slot that points at its own server and one that points at the owner's.
    """
    return run([sys.executable, str(SCRIPTS / "live_slot.py"), *args],
               stage=stage, timeout=timeout, env=env)


# ------------------------------------------------------------------------------------------------
# Small helpers
# ------------------------------------------------------------------------------------------------

def http_ok(url: str, timeout: float = 4.0) -> int | None:
    """GET a url; return the status code, or None when nothing answers. Never raises."""
    import urllib.error
    import urllib.request
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:  # noqa: S310 - loopback only
            return int(resp.status)
    except urllib.error.HTTPError as exc:
        return int(exc.code)
    except Exception:  # noqa: BLE001 - "nothing answered" is the answer
        return None


def port_listener_pid(port: int) -> int | None:
    """The pid LISTENING on a local port, or None. Uses psutil so this needs no shell."""
    import psutil
    try:
        for conn in psutil.net_connections(kind="tcp"):
            if conn.status == psutil.CONN_LISTEN and conn.laddr and conn.laddr.port == port:
                return conn.pid
    except Exception:  # noqa: BLE001 - an unreadable table is "unknown", never "free"
        return None
    return None


def wait_for_pattern(path: Path, pattern: re.Pattern, *, timeout: float, poll: float = 3.0) -> str | None:
    """Poll a file for a line matching `pattern` until `timeout`. Returns the line, or None."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.exists():
            try:
                for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
                    if pattern.search(line):
                        return line.strip()
            except OSError:
                pass
        time.sleep(poll)
    return None


def sha256_of(path: Path) -> str:
    import hashlib
    if not path.exists():
        return "(absent)"
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def tail(path: Path, lines: int = 20) -> str:
    if not path.exists():
        return f"(no file at {path})"
    try:
        data = path.read_text(encoding="utf-8", errors="replace").splitlines()
        return "\n".join("    " + ln for ln in data[-lines:])
    except OSError as exc:
        return f"(unreadable: {exc})"


def read_json(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001 - a corrupt registry is handled by the caller
        return {}


# ------------------------------------------------------------------------------------------------
# Configuration and preflight -- all of it BEFORE any side effect.
# ------------------------------------------------------------------------------------------------

@dataclass
class Config:
    pool_root: Path
    source_install: Path
    session: str
    slot: int
    slot_install: Path | None = None
    port: int | None = None
    keep_running: bool = False
    budgets: dict[str, float] = field(default_factory=dict)
    env_file: Path | None = None
    problems: list[str] = field(default_factory=list)
    #: The pool's port base (`live-slot.ps1 -BasePort`, default 5100). Shifted when the slot this run
    #: will take has a port another session's server holds, since `-Acquire` ignores `-Slot`.
    base_port: int = 5100


#: Per-stage budgets (seconds). Named, so a refusal says which budget blew.
DEFAULT_BUDGETS = {
    "acquire": 120.0,
    "deploy": 900.0,
    "server_start": 180.0,
    "server_health": 240.0,   # a fresh slot seeds its database first
    "game_launch": 60.0,
    "injector_evidence": 180.0,
    "server_evidence": 45.0,
    "cleanup": 180.0,
}

#: What a usable MelonLoader install must contain (mirrors live-slot.ps1's RequiredEntries).
REQUIRED_ENTRIES = ("PlantsVsZombiesRH.exe", "MelonLoader", "BepInEx", "Mods", "GameAssembly.dll")

#: The server's DOCUMENTED fallback port (`gk-core/src/FusionRpg.Server/Program.cs:15-16`). This is NOT
#: "the" owner port -- it is only the value the server binds when `FUSIONRPG_URLS` is unset, and it is
#: used here solely as the last resort when the launcher's own record cannot be read. The owner's real
#: port is whatever `%AppData%\FusionRpg\launcher.json` records, because the launcher is the port
#: picker (gk-fusion/docs/launcher/spec.md "Port picker": last good port, then 5088, scanning 5089-5188).
SERVER_DOCUMENTED_FALLBACK_PORT = 5088


def owner_port() -> tuple[int, str]:
    """The owner's port and where the value came from. Never assumes: reads the launcher's record.

    AGENTS.md "Ports are configuration, never a constant": `5088` is a default, not a fact -- the
    owner's install may be on any port the launcher picked. A probe that hardcodes 5088 measures the
    wrong server (or nothing), which is the class of bug fixed in 9e7b5c7f6.
    """
    launcher = Path(os.environ.get("APPDATA", "")) / "FusionRpg" / "launcher.json"
    recorded = read_json(launcher).get("lastPort")
    if isinstance(recorded, int) and recorded > 0:
        return recorded, f"launcher.json lastPort ({launcher})"
    return SERVER_DOCUMENTED_FALLBACK_PORT, (
        "documented server default (no readable %AppData%/FusionRpg/launcher.json -- the launcher "
        "is the port picker, so this value is a GUESS about the owner's install)")


def load_env_file(path: Path) -> dict[str, str]:
    """Read a KEY=VALUE file. Explicit, readable, and named -- never guessed from the shell."""
    values: dict[str, str] = {}
    if not path.exists():
        return values
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def resolve_config(args: argparse.Namespace) -> Config:
    """Resolve every machine-specific value, then CHECK it. Fail closed, all problems at once."""
    file_env = load_env_file(Path(args.env_file)) if args.env_file else {}

    def pick(flag: str | None, *names: str) -> str | None:
        if flag:
            return flag
        for name in names:
            value = os.environ.get(name) or file_env.get(name)
            if value:
                return value
        return None

    pool = pick(args.pool_root, "FUSIONRPG_GAME_POOL")
    source = pick(args.source_install, "FUSIONRPG_GAME_SOURCE", "FUSIONRPG_ML_GAMEDIR",
                  "FUSIONRPG_ML_GAMEDIR_DEFAULT")
    config = Config(
        pool_root=Path(pool) if pool else Path(""),
        source_install=Path(source) if source else Path(""),
        session=args.session,
        slot=args.slot,
        keep_running=args.keep_running,
        budgets=dict(DEFAULT_BUDGETS),
        env_file=Path(args.env_file) if args.env_file else None,
    )
    for key, value in (args.budget or {}).items():
        config.budgets[key] = float(value)

    # ---- every check below is a REFUSAL, reported together, before any side effect --------------
    if not pool:
        config.problems.append(
            "no pool root: pass --pool-root or set FUSIONRPG_GAME_POOL (machine-specific; never "
            "committed). The pool is the parent directory that holds slot-1, slot-2, …")
    elif not config.pool_root.is_dir():
        config.problems.append(f"pool root does not exist: {config.pool_root}")
    if not source:
        config.problems.append(
            "no source install: pass --source-install or set FUSIONRPG_GAME_SOURCE (the install a "
            "fresh slot is cloned from)")
    elif not config.source_install.is_dir():
        config.problems.append(f"source install does not exist: {config.source_install}")
    if not config.session.strip():
        config.problems.append("no --session: the pool identifies a holder by session id, so an "
                               "empty one cannot claim or release a slot")
    if pool:
        for entry in REQUIRED_ENTRIES:
            if not (config.source_install / entry).exists() and not config.problems:
                config.problems.append(
                    f"source install is missing '{entry}': {config.source_install} is not a usable "
                    f"MelonLoader install (needs {', '.join(REQUIRED_ENTRIES)})")
                break

    # The three injector pack cells (TVB-F39). A live probe only needs the cell it deploys, but a
    # missing one is worth naming up front rather than 10 minutes into a deploy.
    cell_39 = pick(None, "FUSIONRPG_ML_GAMEDIR_PVZRH_3_9") or (source or "")
    if cell_39 and not Path(cell_39).is_dir():
        config.problems.append(
            f"the pvzrh-3.9 MelonLoader cell does not exist: {cell_39} "
            "(set FUSIONRPG_ML_GAMEDIR_PVZRH_3_9)")
    return config


def preflight_report(config: Config, *, probe_pool: bool) -> dict:
    """Describe what the probe WOULD do, and what is missing. No side effects at all."""
    o_port, o_source = owner_port()
    report: dict = {
        "pool_root": str(config.pool_root),
        "source_install": str(config.source_install),
        "session": config.session,
        "owner_port": o_port,
        "owner_port_source": o_source,
        "owner_port_in_use": port_listener_pid(o_port) is not None,
        "problems": list(config.problems),
        "budgets": config.budgets,
    }
    if config.pool_root.is_dir() and probe_pool:
        registry = read_json(config.pool_root / "slots.json")
        slots = registry.get("slots") or []
        report["slots"] = [
            {"slot": s.get("slot"), "state": s.get("state"), "session": s.get("session"),
             "install": s.get("installPath"), "port": s.get("port")}
            for s in slots
        ]
        report["free_slots"] = [n for n in (1, 2, 3)
                                if not any(s.get("slot") == n and s.get("state") == "occupied"
                                           for s in slots)]
        servers = read_json(config.pool_root / "servers.json").get("servers") or []
        report["recorded_servers"] = servers
    return report


# ------------------------------------------------------------------------------------------------
# Pool interaction (via the existing live-slot.ps1 / lane-server.ps1 -- called, not reimplemented)
# ------------------------------------------------------------------------------------------------

def preview_target(config: Config) -> tuple[int, Path, int]:
    """Resolve the slot this run WOULD take, and refuse occupancy BEFORE claiming it.

    A refusal must not cost a slot: the first version checked the port AFTER `-Acquire` and left the
    claim held (measured 2026-09-25 -- `slots.json` still read occupied by this session after the
    refusal). Everything here is read-only, so a refused probe leaves the pool exactly as it found it.

    A slot whose port is already held by something the pool did not start is SKIPPED rather than
    fatal: another session's server on slot 1's `:5101` must not block a probe from using slot 2
    (measured 2026-09-25: pid 78664 from worktree `actor-hud-bottom-anchor-20260916` held `:5101`
    while `servers.json` was empty). When every candidate is blocked, the refusal names each one.
    """
    registry = read_json(config.pool_root / "slots.json")
    slots = registry.get("slots") or []
    recorded = read_json(config.pool_root / "servers.json").get("servers") or []
    recorded_pids = {int(s.get("pid") or 0) for s in recorded}
    o_port, o_source = owner_port()

    if config.slot:
        candidates = [config.slot]
    else:
        ready = [int(s["slot"]) for s in slots if s.get("state") == "ready"]
        occupied = {int(s["slot"]) for s in slots if s.get("state") == "occupied"}
        free = [n for n in (1, 2, 3) if n not in occupied]
        # Cloned-and-verified slots first (no 546 MB clone to pay), then any free slot. This MUST
        # mirror live-slot.ps1's own choice (line 262: the lowest-numbered ready slot, else the first
        # uncloned number), because `-Acquire` ignores `-Slot` entirely -- so the slot this run gets
        # is the one live-slot picks, and the port must be arranged for THAT slot.
        candidates = ready + [n for n in free if n not in ready]
        if not candidates:
            raise Refusal("preflight", "every slot is occupied by another session",
                          "wait for one to be released (all slots held is a wait, never a kill)")

    blocked: list[str] = []
    for chosen in candidates:
        entry = next((s for s in slots if int(s.get("slot") or -1) == chosen), None)
        install = Path((entry or {}).get("installPath") or (config.pool_root / f"slot-{chosen}"))
        # A slot with no recorded port yet will get `BasePort + slot` at claim time, so the base is
        # what has to be free -- and `-Acquire` ignores `-Slot`, so the slot that gets claimed is the
        # one live-slot.ps1 picks, not necessarily this candidate. Arrange a base whose port for THAT
        # slot is free, then let the loop below confirm.
        for base in (config.base_port, config.base_port + 10, config.base_port + 20, config.base_port + 30,
                     config.base_port + 100):
            port = base + chosen
            if port == o_port:
                blocked.append(f"slot {chosen}: base {base} resolves to the OWNER's port {o_port}")
                continue
            occupant = port_listener_pid(port)
            if occupant and occupant not in recorded_pids:
                recorded_text = ", ".join(str(p) for p in sorted(recorded_pids) if p) or "none"
                blocked.append(
                    f"slot {chosen}: port {port} (base {base}) held by pid {occupant}, which the pool "
                    f"did not start (servers.json records {recorded_text})")
                continue
            config.base_port = base
            return chosen, install, port

    raise Refusal(
        "preflight", "no usable slot: every candidate's port is held by something this pool did not start",
        "the pool's port range is blocked; free a port or stop the unrecorded server\n"
        + "\n".join("  - " + line for line in blocked))


def acquire_slot(config: Config) -> tuple[int, Path, int]:
    """Claim a slot; return (slot, install, port). Reads the REGISTRY, never the log text."""
    args = ["-Acquire", "-Session", config.session]
    # `live-slot.ps1 -Acquire` IGNORES `-Slot` (it takes the lowest ready slot, line 262) and pins the
    # port as `BasePort + slot` (line 285). When the slot it will take has an occupied port, the only
    # lever is -BasePort -- so the port is chosen to be free for the slot live-slot will actually pick.
    args += ["-BasePort", str(config.base_port)]
    result = live_slot(*args, stage="acquire", timeout=config.budgets["acquire"])

    registry = read_json(config.pool_root / "slots.json")
    held = [s for s in (registry.get("slots") or [])
            if s.get("session") == config.session and s.get("state") == "occupied"]
    if not held:
        raise Refusal(
            "acquire", f"no slot is held by session '{config.session}' after -Acquire",
            (result.stdout + result.stderr)[-2000:],
        )
    entry = held[0]
    slot = int(entry["slot"])
    install = Path(entry.get("installPath") or (config.pool_root / f"slot-{slot}"))
    port = int(entry.get("port") or (config.base_port + slot))
    # The occupancy/owner-port checks ran BEFORE the claim (preview_target); re-check the install
    # shape here because `-Acquire` may have cloned it, and release the claim if it is unusable --
    # a refusal must never leave a slot held.
    try:
        if not install.is_dir():
            raise Refusal("acquire", f"slot {slot} install does not exist: {install}")
        for entry_name in REQUIRED_ENTRIES:
            if not (install / entry_name).exists():
                raise Refusal("acquire",
                              f"slot {slot} install is not usable: missing '{entry_name}'",
                              f"install: {install}")
    except Refusal:
        release_slot(config)
        raise
    return slot, install, port


def release_slot(config: Config) -> None:
    live_slot("-Release", "-Session", config.session, stage="cleanup", timeout=60.0)


# ------------------------------------------------------------------------------------------------
# The probe itself
# ------------------------------------------------------------------------------------------------

def owner_fingerprint(config: Config, when: str) -> dict:
    """What must NOT move: the owner's cfg hash and the pid on the owner's port (read, not assumed)."""
    cfg = config.source_install / "Mods" / "fusionrpg.cfg"
    o_port, o_source = owner_port()
    mark = {"cfg_sha256": sha256_of(cfg), "owner_port": o_port,
            "owner_port_source": o_source, "owner_port_pid": port_listener_pid(o_port)}
    print(f"  owner {when}: cfg={mark['cfg_sha256'][:16]}…  :{o_port} pid={mark['owner_port_pid']}",
          flush=True)
    return mark


def probe(config: Config) -> dict:
    verdict: dict = {
        "session": config.session,
        "poolRoot": str(config.pool_root),
        "sourceInstall": str(config.source_install),
        "stages": [],
        "refusals": [],
    }
    stage_started = time.monotonic()

    def stage(name: str) -> None:
        nonlocal stage_started
        verdict["stages"].append(name)
        print(f"=== {name} ===", flush=True)
        stage_started = time.monotonic()

    print(f"prove-slot-connection (python)  session={config.session}", flush=True)
    print(f"  pool={config.pool_root}  source={config.source_install}", flush=True)

    stage("owner fingerprint BEFORE")
    before = owner_fingerprint(config, "before")

    slot = install = port = None
    game_pid = None
    try:
        stage("resolution + occupancy check (read-only: a refusal here costs no slot)")
        want_slot, want_install, want_port = preview_target(config)
        print(f"  target slot {want_slot} -> {want_install}  (server http://127.0.0.1:{want_port})",
              flush=True)

        stage("claim the slot (registry is the authority, not the log text)")
        slot, install, port = acquire_slot(config)
        slot_url = f"http://127.0.0.1:{port}"
        verdict.update(slot=slot, install=str(install), port=port, slotUrl=slot_url)
        print(f"  claimed slot {slot} -> {install}  (server {slot_url})", flush=True)

        server_log = config.pool_root / f"slot-{slot}-server.log"
        injector_log = install / "MelonLoader" / "Latest.log"
        verdict["serverLog"] = str(server_log)

        # Deploy BEFORE the server starts: the deploy skips `dotnet publish` when its health check
        # answers, and that once shipped a stale binary (the eight bogus `no such column` errors).
        # This calls the PYTHON deploy tool (deploy-play.py). The PowerShell original is retired: it
        # was broken at HEAD (`--only game-profile` without `--tier local` throws "CI
        # guard range is required") and it skipped the cfg write under -NoGame, which is the flag a
        # pooled deploy MUST use before the server owns the DLLs.
        stage("deploy into the slot (fresh publish + cfg + injector), no server running")
        deploy = run(
            [sys.executable, str(SCRIPTS / "deploy-play.py"),
             "--loader-host", "MelonLoader", "--game-dir", str(install),
             "--game-profile", "pvzrh-3.9", "--server-url", slot_url,
             "--no-server", "--no-game", "--no-rebuild-ui"],
            stage="deploy", timeout=config.budgets["deploy"], echo=True,
        )
        if deploy.returncode != 0:
            raise Refusal("deploy", f"deploy-play.py exited {deploy.returncode}",
                          (deploy.stdout + deploy.stderr)[-3000:])

        server_exe = REPO_ROOT / "dist" / "FusionRpg.Server" / "FusionRpg.Server.exe"
        if not server_exe.exists():
            raise Refusal("deploy", f"no server exe at {server_exe} after deploy",
                          "a data path cannot be proven without a server binary")
        stat = server_exe.stat()
        age_min = (time.time() - stat.st_mtime) / 60.0
        verdict["serverBinary"] = f"{stat.st_size} bytes, built {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(stat.st_mtime))}"
        print(f"  server binary: {verdict['serverBinary']}", flush=True)
        if age_min > 30:
            print(f"  WARNING: that binary is {age_min:.0f} minutes old — suspect it first if the "
                  "data path fails", flush=True)

        cfg = install / "Mods" / "fusionrpg.cfg"
        if not cfg.exists():
            raise Refusal("deploy", f"no fusionrpg.cfg in the slot after deploy ({cfg})",
                          "cannot prove a connection without a written target")
        cfg_text = cfg.read_text(encoding="utf-8", errors="replace")
        verdict["slotCfg"] = cfg_text
        # Both sides must name the SAME url (AGENTS.md "Ports are configuration, never a constant").
        # The check is equality against the slot's own url, not a substring hunt for the owner's port:
        # a `5088` substring test passes on `51088` and fails to notice a cfg naming some THIRD port.
        written = re.search(r"^\s*ServerUrl\s*=\s*(\S+)\s*$", cfg_text, re.MULTILINE)
        if not written:
            raise Refusal("deploy", "the slot cfg carries no ServerUrl= line after deploy",
                          f"refusing to launch a game whose target is unspecified\n{cfg_text}")
        if written.group(1).rstrip("/") != slot_url:
            o_port, o_source = owner_port()
            named = written.group(1).rstrip("/")
            extra = (" -- that is the OWNER's port" if named.endswith(f":{o_port}") else "")
            raise Refusal(
                "deploy",
                f"the slot cfg names {written.group(1)}, not this slot's {slot_url}{extra}",
                f"refusing to launch a game against a server this probe did not start"
                f" (owner port {o_port} via {o_source})")

        stage("start the slot's OWN server")
        start = powershell("lane-server.ps1", "-Start", "-Slot", str(slot), "-Force",
                           stage="server_start", timeout=config.budgets["server_start"])
        verdict["serverStartExit"] = start.returncode

        stage(f"gate on /health at {slot_url} (a fresh slot seeds its data first)")
        deadline = time.monotonic() + config.budgets["server_health"]
        health = None
        while time.monotonic() < deadline:
            health = http_ok(f"{slot_url}/health")
            if health:
                break
            time.sleep(3)
        if not health:
            raise Refusal("server_health",
                          f"the slot server never answered /health within "
                          f"{config.budgets['server_health']:.0f}s",
                          "server log tail:\n" + tail(server_log))
        verdict["slotHealth"] = health
        print(f"  health: HTTP {health}", flush=True)

        stage("clear the stale injector log (so the evidence below cannot be an earlier run's)")
        if injector_log.exists():
            injector_log.unlink()
            print(f"  removed {injector_log}", flush=True)

        stage("launch the SLOT game")
        exes = [p for p in install.glob("*.exe") if "CrashHandler" not in p.name]
        if not exes:
            raise Refusal("game_launch", f"no game exe found in {install}")
        game = exes[0]
        proc = subprocess.Popen([str(game)], cwd=str(install),  # noqa: S603 - fixed path
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        game_pid = proc.pid
        verdict["gamePid"] = game_pid
        print(f"  launched {game.name} (pid {game_pid}) from {install}", flush=True)

        stage("EVIDENCE (1/2): the injector's own line naming the server it talks to")
        line = wait_for_pattern(injector_log, re.compile(r"MelonMod host ready, server="),
                                timeout=config.budgets["injector_evidence"])
        if line:
            verdict["injectorLine"] = line
            verdict["injectorSaysSlotUrl"] = slot_url in line
            print(f"  FOUND: {line}", flush=True)
        else:
            verdict["injectorLine"] = None
            print("  NOT FOUND — injector log tail:", flush=True)
            print(tail(injector_log, 25), flush=True)

        stage("EVIDENCE (2/2): the slot SERVER's own log showing this client")
        client = wait_for_pattern(server_log, re.compile(r"Connection id |Request id "),
                                  timeout=config.budgets["server_evidence"])
        server_text = server_log.read_text(encoding="utf-8", errors="replace") \
            if server_log.exists() else ""
        if client:
            print(f"  FOUND: {client}", flush=True)
            verdict["serverSawClient"] = client
        else:
            # Distinguish "the server does not log transports" from "the client never arrived":
            # 2026-09-22, a 21-line log held zero request lines while the connection was live.
            all_lines = server_text.splitlines()
            reqish = sum(1 for ln in all_lines if re.search(r"Connection id|Request id|HTTP/1\.1", ln))
            if all_lines and reqish == 0:
                verdict["serverLogCanWitness"] = False
                print(f"  THE SERVER LOG CANNOT WITNESS A CONNECTION: {len(all_lines)} lines, "
                      "0 request lines", flush=True)
            else:
                print("  no client request found — server log tail:", flush=True)
                print(tail(server_log, 15), flush=True)
            # The server's own DOMAIN lines are a transport-independent witness.
            domain = [ln.strip() for ln in all_lines if re.match(r"^\[[a-z][a-z0-9_.-]*\] ", ln)]
            if domain:
                verdict["serverDomainLine"] = domain[0]
                verdict["serverSawClient"] = domain[0]
                print(f"  THE SERVER'S OWN DOMAIN LINES WITNESS IT ({len(domain)}):", flush=True)
                for ln in domain[:3]:
                    print("    " + ln, flush=True)
            else:
                verdict["serverSawClient"] = None
            # Client-side witness, labelled as client-side, never promoted to the server's.
            sig = wait_for_pattern(injector_log, re.compile(r"SignalR connected|SignalR reconnected|Hello"),
                                   timeout=5.0, poll=1.0)
            if sig:
                verdict["injectorSignalR"] = sig
                print(f"  client-side witness in the injector log (NOT the server's): {sig}", flush=True)

        conns = len(re.findall(r'Connection id "', server_text))
        fails = len(re.findall(r"unhandled exception|SQLite Error", server_text))
        verdict["serverConnections"] = conns
        verdict["serverFailures"] = fails

    except Refusal as refusal:
        verdict["refusals"].append({"stage": refusal.stage, "reason": refusal.reason,
                                    "detail": refusal.detail})
        print(f"\nREFUSED [{refusal.stage}] {refusal.reason}", flush=True)
        if refusal.detail:
            print(refusal.detail, flush=True)
    finally:
        if not config.keep_running:
            stage("cleanup: stop this slot's game + server, release the slot")
            if game_pid:
                stop_slot_game(install, game_pid)
            if slot:
                try:
                    powershell("lane-server.ps1", "-Stop", "-Slot", str(slot),
                               stage="cleanup", timeout=60.0)
                except Refusal as refusal:
                    print(f"  ! {refusal.reason}", flush=True)
            if slot and install:
                try:
                    release_slot(config)
                except Refusal as refusal:
                    print(f"  ! {refusal.reason}", flush=True)
        else:
            print(f"\n  --keep-running: slot {slot} and its server are STILL UP. Stop with:\n"
                  f"    pwsh -NoProfile -File scripts/lane-server.ps1 -Stop -Slot {slot}\n"
                  f"    python scripts/live_slot.py --release --session {config.session}",
                  flush=True)

        stage("owner fingerprint AFTER (must equal BEFORE)")
        after = owner_fingerprint(config, "after ")
        verdict["ownerUntouched"] = (before == after)
        verdict["ownerFingerprintBefore"] = before
        verdict["ownerFingerprintAfter"] = after

    verdict["connectionProven"] = (
        verdict.get("injectorSaysSlotUrl") is True and bool(verdict.get("serverSawClient"))
    )
    if "serverFailures" in verdict:
        verdict["dataPathHealthy"] = verdict["serverFailures"] == 0
    return verdict


def stop_slot_game(install: Path, game_pid: int) -> None:
    """Stop only processes whose REAL executable path is inside this slot's install."""
    import psutil
    killed = 0
    for proc in psutil.process_iter(["pid", "exe"]):
        try:
            exe = proc.info.get("exe") or ""
            if exe and Path(exe).is_relative_to(install):
                proc.kill()
                killed += 1
                print(f"  killed pid {proc.info['pid']} (from this slot)", flush=True)
        except Exception:  # noqa: BLE001 - access-denied means "not ours to touch"
            continue
    if killed == 0:
        print(f"  no live process from {install} to stop", flush=True)


# ------------------------------------------------------------------------------------------------
# Entry point
# ------------------------------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Prove a pooled slot's game actually talks to that slot's own server.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--session", default="prove-slot",
                        help="the pool holder id (used to claim and release the slot)")
    parser.add_argument("--slot", type=int, default=0, help="0 = let the pool pick a free slot")
    parser.add_argument("--pool-root", default=None, help="or FUSIONRPG_GAME_POOL")
    parser.add_argument("--source-install", default=None,
                        help="the install a fresh slot is cloned from (or FUSIONRPG_GAME_SOURCE)")
    parser.add_argument("--env-file", default=None,
                        help="a KEY=VALUE file to read the above from (explicit, never guessed)")
    parser.add_argument("--keep-running", action="store_true",
                        help="leave the slot's game and server up for inspection")
    parser.add_argument("--preflight", action="store_true",
                        help="print the resolved configuration and what is missing, with NO side "
                             "effects, and exit non-zero if a precondition fails")
    parser.add_argument("--json", action="store_true", help="emit the verdict as JSON on stdout")
    parser.add_argument("--budget", action="append", default=[],
                        metavar="STAGE=SECONDS", help="override one stage budget (repeatable)")
    args = parser.parse_args(argv)
    for item in args.budget:
        stage_name, _, seconds = item.partition("=")
        if stage_name not in DEFAULT_BUDGETS or not seconds:
            parser.error(f"--budget wants one of {sorted(DEFAULT_BUDGETS)}=SECONDS, got '{item}'")

    config = resolve_config(args)

    if args.preflight:
        report = preflight_report(config, probe_pool=True)
        if args.json:
            print(json.dumps(report, indent=2))
        else:
            print("prove-slot-connection --preflight")
            for key in ("pool_root", "source_install", "session", "owner_port", "owner_port_source",
                        "owner_port_in_use", "free_slots", "recorded_servers"):
                if key in report:
                    print(f"  {key:26} {report[key]}")
            for problem in report["problems"]:
                print(f"  PROBLEM  {problem}")
            print("\npreflight: " + ("READY" if not report["problems"] else "REFUSED"))
        return 0 if not report["problems"] else 2

    if config.problems:
        print("REFUSED before any side effect — the configuration is incomplete:", file=sys.stderr)
        for problem in config.problems:
            print(f"  - {problem}", file=sys.stderr)
        print("\nRun --preflight to see the whole picture, or set the values above.", file=sys.stderr)
        return 2

    verdict = probe(config)

    if args.json:
        print(json.dumps(verdict, indent=2))
    else:
        print("\n=== verdict ===")
        for key, value in verdict.items():
            if key in ("stages", "refusals"):
                continue
            print(f"  {key:24} {value}")
        print(f"  {'CONNECTION PROVEN':24} {verdict['connectionProven']}")
        if "dataPathHealthy" in verdict:
            print(f"  {'DATA PATH HEALTHY':24} {verdict['dataPathHealthy']}"
                  f"   (server-side failure lines: {verdict.get('serverFailures')})")

    if verdict["refusals"]:
        for refusal in verdict["refusals"]:
            print(f"\nFAILED STAGE [{refusal['stage']}]: {refusal['reason']}", file=sys.stderr)
            if refusal["detail"]:
                print(refusal["detail"], file=sys.stderr)
        return 1
    return 0 if verdict["connectionProven"] else 1


if __name__ == "__main__":
    sys.exit(main())
