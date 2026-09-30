#!/usr/bin/env python3
"""Deploy the game + server + injector into an install (a DEPLOY, never a gate).

Python replacement for the retired `scripts/deploy-play.ps1` (owner ruling 2026-09-25: every new
tool is Python, and a `.ps1` this repo touches for a fix is a candidate to port rather than keep).

## Why the PowerShell was retired

* **It ran the whole boundary-guard suite on every deploy.** `run_guards.py --tier local
  -IncludeBacklog` selects the WIDEST tier (`local` = every `ci` guard plus the machine-only ones;
  `-IncludeBacklog` adds the report-only rows) — 28 guards, **203 seconds measured**. That is
  *verification*, and it belongs to the implement-phase gate (`verify-change.ps1`) and to CI/nightly
  (`run_guards.py --tier ci`). Paying it before every deploy is the exact deploy/release conflation
  AGENTS.md forbids: a 203s verification sweep in front of a deploy whose own measured stages come
  to 28.3s (2026-09-26, default MelonLoader install — see AGENTS.md for the per-stage breakdown and
  for which stages are still unmeasured).
* **It was BROKEN at HEAD.** Line 291 called the runner with `-Only game-profile` and no `-Tier`,
  and the runner defaults to `ci`, where a missing `-CiRange` **throws**. Every deploy died at the
  injector build with "CI guard range is required" — unrelated to anything the caller did.
* **`-NoGame` silently skipped the cfg write.** The cfg was written inside `if (-not $NoGame)`, so a
  pooled deploy — which MUST use `-NoGame` before the server owns the DLLs — left the slot's game
  pointed at the owner's `:5088` (the SSH4.9 incident class).
* **`Write-Host` output was capturable only with `*>&1`.** A caller reading `2>&1` received NOTHING
  from a deploy that was working correctly.

## What this tool does, in order (the order is load-bearing)

    1  preflight            read-only; every problem reported at once, nothing touched
    2  game lock            refuses an install another live session holds
    3  game-profile         the deploy's OWN precondition: right bridge into the right install
    4  FE build             npm run build                  (skip with --no-rebuild-ui)
    5  FE mirror            robocopy /MIR + index.html sha256 equality proof
    6  injector build       dotnet build -> <install>\\Mods
    7  freshness            each deployed DLL must be newer than its own source tree
    8  server publish       dotnet publish -> dist/FusionRpg.Server
    9  seed import          AtomImporter --db <dist>/data
    10 cfg write            ALWAYS for MelonLoader, and verified to name THIS url
    11 server start         then gate on /health
    12 game launch          with FUSIONRPG_SERVER_URL on the child only

Steps 10-12 are in that order because the install must be complete before a server owns the DLLs,
and because a pooled slot has to be told its own port before its game starts.

## Domains

**Deploy.** This tool runs NO repo guard suite and NO test suite by default. Its only gates are its
own preconditions (steps 1-3, 7). Verification is the implement phase's job — pass `--verify
--paths <files>` to bundle a scoped `verify-change.ps1` run, and `--full-suite` only at the three
AGENTS.md points (feature completion, cross-boundary change, immediately before a live probe).

## Configuration

Machine-specific values come from the environment or the flags, never from this file:

    FUSIONRPG_ML_GAMEDIR / --game-dir        the install to deploy into
    FUSIONRPG_GAME_DIR                       the same, for --loader-host BepInEx
    FUSIONRPG_ML_GAMEDIR_PVZRH_3_9 / _3_8_1  the per-cell packs (TVB-F39)
    FUSIONRPG_GAME_PROFILE / --game-profile  pvzrh-3.9 / pvzrh-3.8.1 (else auto-detected)
    FUSIONRPG_GAME_POOL                      when set, the target MUST be inside the pool

## Usage

    python gk-fusion/scripts/deploy-play.py                                    # default install, no launch
    python gk-fusion/scripts/deploy-play.py --no-server --no-game --skip-freshness
    python gk-fusion/scripts/deploy-play.py --game-dir <slot> --server-url http://127.0.0.1:5102 --no-server
    python gk-fusion/scripts/deploy-play.py --reuse-build                      # skip publish/build when fresh
    python gk-fusion/scripts/deploy-play.py --verify --paths <changed files>   # bundle scoped verification
    python gk-fusion/scripts/deploy-play.py --dry-run                          # print the plan, touch nothing
    python gk-fusion/scripts/deploy-play.py --json                             # machine-readable verdict
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = REPO_ROOT / "scripts"

# WHICH REPOSITORY OWNS EACH ARTEFACT. The monorepo had one root, so `REPO_ROOT / "src" / ...` and
# `REPO_ROOT / "web" / ...` were correct expressions for the server, the web app, the engine projects
# and the seed importer alike. The split gave each of those a different owner, and this tool went on
# addressing all of them from gk-fusion - so its plan named gk-fusion/dist/FusionRpg.Server,
# gk-fusion/web/fusion-rpg-web and gk-fusion/tools/AtomImporter, none of which exists. A `--dry-run`
# reported a twelve-stage plan that could not have completed at stage 4, and no CI or test covers the
# plan because a deploy is a deploy and not a gate.
#
# The injector host projects are the only ones that are genuinely gk-fusion's, and they are the ones
# still addressed from REPO_ROOT below. Everything else names its owner, which is the whole lesson
# of the split stated as a line of code: a repository boundary is a question every path has to ask.
sys.path.insert(0, str(SCRIPTS / "lib"))
from keepverse_roots import core_root, forge_root, web_root, workspace_root  # noqa: E402

CORE_ROOT = core_root(REPO_ROOT)
WEB_ROOT = web_root(REPO_ROOT)
FORGE_ROOT = forge_root(REPO_ROOT)
WORKSPACE_ROOT = workspace_root(REPO_ROOT)

# WHERE A TOOL IS LOOKED FOR, in order. gk-fusion's own scripts/ first, because that is where the
# tools it owns live and the local answer should win; then the repositories that own the rest. The
# guard RUNNER and the session LOCK are gk-core's, and the split left this resolver asking gk-fusion
# for them, so every deploy died at a precondition naming a tool that exists.
#
# That failure is the fail-closed design working - a skipped precondition would have reported a
# deploy nobody had actually guarded - and it is the same class as the path problem above, one level
# up: not a path built from the wrong root, but a NAME resolved against the wrong root.
TOOL_SEARCH_ROOTS = (SCRIPTS, CORE_ROOT / "scripts", WORKSPACE_ROOT / "scripts")

#: The owner's server, as DOCUMENTED (gk-core/src/FusionRpg.Server/Program.cs:15-16). It is a default, not a
#: fact: the launcher picks the real port and records it in %AppData%\FusionRpg\launcher.json, so this
#: value is only used for the pool guard's "is this the owner's url?" comparison and as a CLI default.
OWNER_SERVER_URL = "http://127.0.0.1:5088"

MELON_INJECTOR_DLL = "FusionRpg.Injector.MelonLoader.dll"
MELON39_INJECTOR_DLL = "FusionRpg.Injector.MelonLoader.39.dll"
BEPINEX_INJECTOR_DLL = "FusionRpg.Injector.dll"

#: `GameAssembly.dll` size that identifies the 3.9 pack (mirrors deploy-play.ps1's own test).
SIZE_39 = 57717248

#: Per-stage budgets (seconds). A stage that exceeds its budget is KILLED and refused by name.
BUDGETS = {
    "game_lock": 60.0,
    "game_profile": 120.0,
    "web_build": 900.0,
    "wwwroot": 300.0,
    "injector_build": 900.0,
    "freshness": 120.0,
    "server_publish": 1200.0,
    "seed_import": 1200.0,
    "cfg": 30.0,
    "server_start": 180.0,
    "game_launch": 120.0,
    "verify": 1200.0,
}


class Refusal(Exception):
    """A named precondition failure. Exit is non-zero and the stage is always named."""

    def __init__(self, stage: str, reason: str, detail: str = "") -> None:
        super().__init__(f"[{stage}] {reason}" + (f"\n{detail}" if detail else ""))
        self.stage, self.reason, self.detail = stage, reason, detail


# A logical tool name -> the file stem actually ON DISK. Registered in ONE place, because a port that
# renames a file puts the cost of that rename on every caller otherwise: `test-fast.ps1` became
# `test_fast.py`, after which `scripts/test-fast.py` and `scripts/test-fast.ps1` were BOTH absent and
# the dispatcher refused a tool that exists. A stem absent from this map is resolved literally, which is
# correct for every tool whose name has not changed.
TOOL_FILE_STEMS = {
    "run-guards": "run_guards",
    "test-fast": "test_fast",
    "game-lock": "game_lock",
}


def tool_argv(stem: str, tool_args: list[str], *, stage: str,
              py_args: list[str] | None = None) -> list[str]:
    """Build the argv that runs a `scripts/` tool, preferring the Python port over the PowerShell one.

    The repo is retiring every `.ps1` tool. A caller that hardcodes one extension puts the cost of a
    port on the CALLER, which is how a one-file port becomes a two-file port. Resolving the name here
    means a tool can be ported without editing every site that runs it.

    `py_args` is the ARGUMENT dialect, and it is a separate parameter from `tool_args` on purpose.
    RESOLVING THE FILE IS NOT RESOLVING THE ARGUMENTS: the two spellings disagree about how a flag is
    written, so a caller that hardcoded `-Paths`/`-Session` handed them to argparse the moment the port
    landed and argparse rejected every one -- which reads as "the deploy's own gate is broken" rather
    than "a caller passed the wrong spelling". Measured at HEAD: `--verify --paths <files>` exited 2 on
    `unrecognized arguments: -Paths ... -Session ...` while resolving the CORRECT file. The caller states
    both dialects and this picks the one matching the interpreter it found, which also keeps a revert
    (restoring the `.ps1`) working with the PowerShell spelling intact.

    Fails CLOSED, by `Refusal`, when neither extension exists: a deploy that skipped a precondition
    step would report success for a gate nobody ran, which is the exact shape of bug this port exists
    to remove. The chosen file is returned rather than hidden so the caller can LOG it - an
    interpreter switch that happens quietly is indistinguishable from a tool that changed behaviour.
    """
    file_stem = TOOL_FILE_STEMS.get(stem, stem)
    searched: list[str] = []
    for root in TOOL_SEARCH_ROOTS:
        for ext, dialect in ((".py", py_args), (".ps1", tool_args)):
            candidate = root / f"{file_stem}{ext}"
            searched.append(str(candidate))
            if not candidate.is_file():
                continue
            if ext == ".ps1":
                return ["pwsh", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", str(candidate), *tool_args]
            python = shutil.which("python")
            if not python:
                raise Refusal(stage, "TOOL-PYTHON-MISSING",
                              f"{candidate.name} exists but python is not on PATH; install python "
                              "or restore the .ps1 so this stage can run")
            return [python, str(candidate), *(dialect if dialect is not None else tool_args)]
    # The refusal names EVERY path it looked at. A message naming one directory sends the reader to
    # add a file to a directory the tool was never going to be in - which is exactly what happened
    # here: TOOL-MISSING said "neither scripts/run_guards.py nor scripts/run_guards.ps1 exists",
    # true of gk-fusion and false of the workspace, and the obvious response was to write a new tool
    # in the directory it named rather than to look for the one that already existed in gk-core.
    raise Refusal(stage, "TOOL-MISSING",
                  f"no {file_stem}.py or {file_stem}.ps1 exists in any repository that owns one "
                  f"(logical name '{stem}'); searched: " + ", ".join(searched))


class Log:
    """stdout AND a file: a deploy outlives one tool call, and PowerShell's stream-6 loss is the
    reason this port exists at all. Never write output that only `*>&1` could capture."""

    def __init__(self, path: Path | None) -> None:
        self.path, self._handle = path, None
        if path:
            path.parent.mkdir(parents=True, exist_ok=True)
            self._handle = path.open("a", encoding="utf-8")

    def __call__(self, message: str = "") -> None:
        line = f"[{time.strftime('%H:%M:%S')}] {message}"
        print(line, flush=True)
        if self._handle:
            self._handle.write(line + "\n")
            self._handle.flush()

    def close(self) -> None:
        if self._handle:
            self._handle.close()


def run(args: list[str], *, stage: str, timeout: float, log: Log, cwd: Path | None = None,
        env: dict[str, str] | None = None, check: bool = True) -> subprocess.CompletedProcess:
    """Run a child with a HARD timeout. On expiry, kill its whole tree and refuse by name."""
    merged = dict(os.environ)
    if env:
        merged.update(env)
    log(f"  $ {Path(args[0]).name} {' '.join(args[1:3])}{' …' if len(args) > 3 else ''}")
    proc = subprocess.Popen(  # noqa: S603 - fixed argv, no shell
        args, cwd=str(cwd or REPO_ROOT), env=merged,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        encoding="utf-8", errors="replace",
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
    try:
        out, err = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        _kill_tree(proc.pid)
        try:
            out, err = proc.communicate(timeout=15)
        except subprocess.TimeoutExpired:
            out, err = "", ""
        raise Refusal(stage, f"exceeded its {timeout:.0f}s budget and was killed (pid {proc.pid})",
                      (out or "")[-2000:] + (err or "")[-2000:]) from None
    for line in (out or "").splitlines()[-10:]:
        log("    " + line)
    for line in (err or "").splitlines()[-6:]:
        log("    ! " + line)
    if check and proc.returncode != 0:
        raise Refusal(stage, f"`{Path(args[0]).name}` exited {proc.returncode}",
                      (out or "")[-2500:] + (err or "")[-2500:])
    return subprocess.CompletedProcess(args, proc.returncode, out or "", err or "")


def _kill_tree(pid: int) -> None:
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],  # noqa: S603,S607
                       capture_output=True, text=True, timeout=30)
    else:
        import signal
        try:
            os.killpg(os.getpgid(pid), signal.SIGTERM)
        except Exception:  # noqa: BLE001 - best effort while already failing
            pass


def http_ok(url: str, timeout: float = 2.0) -> bool:
    import urllib.error
    import urllib.request
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:  # noqa: S310 - loopback only
            return int(resp.status) == 200
    except urllib.error.HTTPError as exc:
        return int(exc.code) == 200
    except Exception:  # noqa: BLE001 - "nothing answered" is the answer
        return False


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def newest_source(roots: list[Path]) -> Path | None:
    latest: Path | None = None
    for root in roots:
        if not root.exists():
            continue
        for cs in root.rglob("*.cs"):
            text = str(cs)
            if f"{os.sep}bin{os.sep}" in text or f"{os.sep}obj{os.sep}" in text:
                continue
            if latest is None or cs.stat().st_mtime > latest.stat().st_mtime:
                latest = cs
    return latest


@dataclass
class Config:
    loader_host: str
    game_dir: Path
    game_profile: str
    server_url: str
    plugin_dir: Path
    injector_proj: Path
    injector_dll: str
    cfg_path: Path | None
    no_game: bool
    no_server: bool
    no_rebuild_ui: bool
    restart_server: bool
    reuse_build: bool
    skip_freshness: bool
    verify: bool
    paths: list[str] = field(default_factory=list)
    full_suite: bool = False
    session: str = ""
    dry_run: bool = False
    log: Log = field(default_factory=lambda: Log(None))
    problems: list[str] = field(default_factory=list)


def detect_game_profile(game_dir: Path) -> str:
    """Which game profile this install carries, read from the install itself.

    ONE implementation, because this decision was written twice and one of the copies was MISSING. The
    MelonLoader branch detected from `GameAssembly.dll`; the BepInEx branch hardcoded `"pvzrh-3.8.1"`.
    So pointing the deploy at a 3.9 BepInEx install silently selected the 3.8.1 profile, the injector
    built against the wrong interop, and the build failed with 682 CS0246/CS0103 errors that name a
    type and say nothing about a version. `--game-profile`'s own help advertised "(auto-detected)" for a
    branch that detected nothing at all.

    An absent or unrecognised assembly falls back to 3.8.1, exactly what the hardcoded value did. A
    wrong guess that is VISIBLE beats a wrong guess that is silent, and that is the whole difference
    between this line and the one it replaced: the game-profile precondition refused the bad profile by
    name instead of the deploy proceeding against it. That guard is why this stayed a small defect.

    Measured here, so the fallback is not being taken on faith: the 3.9 install's `GameAssembly.dll` is
    57,717,248 bytes and the 3.8.1 install's is 47,964,672, and `SIZE_39` is the former.
    """
    ga = game_dir / "GameAssembly.dll"
    return "pvzrh-3.9" if ga.exists() and ga.stat().st_size == SIZE_39 else "pvzrh-3.8.1"



def resolve_config(args: argparse.Namespace, log: Log) -> Config:
    if args.loader_host == "MelonLoader":
        raw = args.game_dir or os.environ.get("FUSIONRPG_ML_GAMEDIR")
        if not raw:
            log("  no --game-dir / FUSIONRPG_ML_GAMEDIR: using this machine's default MelonLoader install")
            raw = r"H:\Games\PVZ-Fusion-3.9_MelonLoader"
        game_dir = Path(raw)
        plugin_dir = game_dir / "Mods"
        profile = args.game_profile or os.environ.get("FUSIONRPG_GAME_PROFILE") or ""
        if not profile:
            profile = detect_game_profile(game_dir)
        if profile == "pvzrh-3.9":
            injector_proj = REPO_ROOT / "src" / "FusionRpg.Injector.MelonLoader.39" / "FusionRpg.Injector.MelonLoader.39.csproj"
            injector_dll = MELON39_INJECTOR_DLL
        else:
            injector_proj = REPO_ROOT / "src" / "FusionRpg.Injector.MelonLoader" / "FusionRpg.Injector.MelonLoader.csproj"
            injector_dll = MELON_INJECTOR_DLL
        cfg_path: Path | None = plugin_dir / "fusionrpg.cfg"
    else:
        raw = args.game_dir or os.environ.get("FUSIONRPG_GAME_DIR") or str(REPO_ROOT.parent)
        game_dir = Path(raw)
        plugin_dir = game_dir / "BepInEx" / "plugins" / "FusionRpg"
        profile = (args.game_profile or os.environ.get("FUSIONRPG_GAME_PROFILE")
                   or detect_game_profile(game_dir))
        injector_proj = REPO_ROOT / "src" / "FusionRpg.Injector.BepInEx" / "FusionRpg.Injector.BepInEx.csproj"
        injector_dll = BEPINEX_INJECTOR_DLL
        cfg_path = None  # the BepInEx host reads FUSIONRPG_SERVER_URL, no per-install cfg

    config = Config(
        loader_host=args.loader_host, game_dir=game_dir, game_profile=profile,
        server_url=args.server_url.rstrip("/"), plugin_dir=plugin_dir,
        injector_proj=injector_proj, injector_dll=injector_dll, cfg_path=cfg_path,
        no_game=args.no_game, no_server=args.no_server, no_rebuild_ui=args.no_rebuild_ui,
        restart_server=args.restart_server, reuse_build=args.reuse_build,
        skip_freshness=args.skip_freshness, verify=args.verify, paths=list(args.paths or []),
        full_suite=args.full_suite, session=args.session or "", dry_run=args.dry_run, log=log)

    # ---- every precondition, reported together, BEFORE any side effect -------------------------
    if not re.match(r"^http://(127\.0\.0\.1|localhost):\d+$", config.server_url):
        config.problems.append(
            f"--server-url must be a loopback http URL with an explicit port, got {config.server_url}")
    if not (config.game_dir / "PlantsVsZombiesRH.exe").exists():
        config.problems.append(
            f"no game exe in --game-dir {config.game_dir} (expected PlantsVsZombiesRH.exe)")
    if config.paths and config.full_suite:
        config.problems.append("pick at most ONE test scope: --paths or --full-suite, not both")
    if config.verify and not (config.paths or config.full_suite):
        config.problems.append("--verify needs --paths <files> or --full-suite to know what to verify")
    if not config.verify and (config.paths or config.full_suite):
        config.problems.append("--paths / --full-suite require --verify (a deploy runs no tests by "
                               "default; that is the deploy/verification split)")
    if config.server_url != OWNER_SERVER_URL and (not config.no_server or config.restart_server):
        config.problems.append(
            f"--server-url {config.server_url} is not the owner's server: pass --no-server, and never "
            "--restart-server (it would stop whatever listens for the owner)")
    pool = os.environ.get("FUSIONRPG_GAME_POOL")
    if pool:
        if config.server_url == OWNER_SERVER_URL:
            config.problems.append(
                f"REFUSED: FUSIONRPG_GAME_POOL is set (a POOLED run) but --server-url is still the "
                f"owner's default ({OWNER_SERVER_URL}). A pooled deploy targets its OWN server on its "
                "own loopback port -- never the owner's.")
        try:
            target = str(config.game_dir.resolve()).lower()
            root = str(Path(pool).resolve()).lower()
            if not target.startswith(root):
                config.problems.append(
                    f"REFUSED: FUSIONRPG_GAME_POOL is set ({pool}) but the deploy target is "
                    f"{config.game_dir} -- a pooled run must deploy INTO the pool, never into the "
                    "owner's install")
        except OSError:
            config.problems.append(f"FUSIONRPG_GAME_POOL does not resolve: {pool}")
    return config


def deploy(config: Config) -> dict:
    log = config.log
    verdict: dict = {"stages": [], "stageSeconds": {}, "refusals": [], "dryRun": config.dry_run}
    started = time.monotonic()
    wwwroot_src = CORE_ROOT / "src" / "FusionRpg.Server" / "wwwroot"
    wwwroot_dist = CORE_ROOT / "dist" / "FusionRpg.Server" / "wwwroot"
    server_out = CORE_ROOT / "dist" / "FusionRpg.Server"
    server_exe = server_out / "FusionRpg.Server.exe"
    health = config.server_url + "/health"

    def stage(name: str) -> None:
        nonlocal started
        if verdict["stages"]:
            verdict["stageSeconds"][verdict["stages"][-1]] = round(time.monotonic() - started, 1)
        verdict["stages"].append(name)
        log(f"==> {name}")
        started = time.monotonic()

    def timed(name: str) -> None:
        """Record a completed stage without starting a new one (for steps with nothing to do)."""
        verdict["stageSeconds"][name] = round(time.monotonic() - started, 1)

    log(f"deploy-play (python)  host={config.loader_host} profile={config.game_profile}")
    log(f"  install={config.game_dir}")
    log(f"  server={config.server_url}")

    if config.dry_run:
        stage("PLAN (--dry-run: nothing is touched)")
        for line in (
            f"1 game lock check on {config.game_dir}",
            f"2 game-profile precondition ({config.game_profile} into {config.game_dir})",
            f"3 web UI build: {'skipped (--no-rebuild-ui)' if config.no_rebuild_ui else 'npm run build'}",
            f"4 FE mirror -> {wwwroot_dist} (index.html sha256 proven)",
            f"5 injector: dotnet build {config.injector_proj.name} -c Release -> {config.plugin_dir}",
            f"6 freshness: {config.injector_dll} + FusionRpg.Core.dll vs their source trees"
            + (" (skipped: --skip-freshness)" if config.skip_freshness else ""),
            f"7 server: {'skipped (--reuse-build and fresh)' if config.reuse_build else 'dotnet publish'}"
            f" -> {server_out}",
            f"8 seed import: AtomImporter --db {server_out / 'data'}",
            f"9 cfg: {config.cfg_path or '(BepInEx: no cfg write)'}"
            + (f"  ServerUrl={config.server_url}" if config.cfg_path else ""),
            f"10 server start: {'no (--no-server)' if config.no_server else 'yes unless already up'}",
            f"11 game launch: {'no (--no-game)' if config.no_game else 'yes'}",
            f"12 test scope: {'--full-suite' if config.full_suite else ('--paths ' + ', '.join(config.paths)) if config.paths else 'NONE (a deploy runs no tests; --verify opts in)'}",
        ):
            log("    " + line)
        timed(verdict["stages"][-1])
        # Same keys as a real verdict: a --json caller is another process and must not have to
        # branch on "the dry run happened to return early with a different shape".
        verdict["injector"] = str(config.plugin_dir)
        verdict["server"] = str(server_exe)
        verdict["ui"] = config.server_url
        verdict["ok"] = not verdict["refusals"]
        return verdict

    try:
        stage("preconditions (read-only: nothing has been touched)")
        timed(verdict["stages"][-1])

        stage("game lock (refuse an install another live session holds)")
        # BOTH dialects. `tool_argv` resolves the FILE by dialect and the ARGUMENTS by dialect, and
        # they disagree about how a flag is written -- `-GameDir` handed to argparse is an
        # `unrecognized arguments` exit 2, which reads as "the deploy's own precondition is broken"
        # rather than "a caller passed the wrong spelling". Resolving the file is not resolving the
        # arguments; that is the same rule the runner and verify-change both carry.
        lock_args = ["-Status", "-GameDir", str(config.game_dir)]
        lock_py_args = ["--status", "--game-dir", str(config.game_dir)]
        if config.session:
            lock_args += ["-Session", config.session]
            lock_py_args += ["--session", config.session]
        lock_argv = tool_argv("game-lock", lock_args, stage="game_lock", py_args=lock_py_args)
        log(f"  resolved: {Path(lock_argv[1]).name}")
        run(lock_argv, stage="game_lock", timeout=BUDGETS["game_lock"], log=log)

        stage(f"game-profile precondition ({config.game_profile} into {config.game_dir})")
        # The deploy's OWN precondition (right bridge into the right install), not the guard suite.
        # `-Tier local` is spelled out: the retired PowerShell called this with no -Tier, the runner
        # defaults to `ci`, and a `ci` run with no --ci-range REFUSES — the defect that broke every
        # deploy at HEAD — so this stays on `--tier local`, which needs no range.
        # ROUTED THROUGH `tool_argv`, now that it CAN be. The old call had to go through `pwsh
        # -Command` because `-LocalArgs` was a PowerShell HASHTABLE, which cannot be expressed in argv
        # at all; the runner is a Python tool and takes `--local-arg ID:KEY=VALUE`, a plain repeatable
        # flag, so the `-Command` wrapper, the `@{}` escaping and a pwsh spawn are all gone.
        # It is also the one site whose text GuardWiring.cs pins (it scans this file for the runner
        # name, `--only` and `--tier local` together), so the tokens below are a contract, not prose.
        profile_argv = tool_argv(
            "run-guards",
            ["-Only", "game-profile", "-Tier", "local",
             "-LocalArgs", f"@{{'game-profile'=@{{GameDir='{config.game_dir}';"
                           f"ExpectedProfile='{config.game_profile}'}}}}"],
            stage="game_profile",
            py_args=["--only", "game-profile", "--tier", "local",
                    # SPELLED THE GUARD'S WAY, not the retired PowerShell way. guard-game-profile.py
                    # declares --game-dir and --profile; GameDir and ExpectedProfile are the .ps1 flags,
                    # and a key whose real flag differs from its name cannot be derived from it.
                    "--local-arg", f"game-profile:game-dir={config.game_dir}",
                    "--local-arg", f"game-profile:profile={config.game_profile}"])
        log(f"  resolved: {Path(profile_argv[1]).name}")
        run(profile_argv, stage="game_profile", timeout=BUDGETS["game_profile"], log=log)

        stage("web UI build")
        if config.no_rebuild_ui:
            log("  skipped (--no-rebuild-ui): syncing whatever is already in src wwwroot")
        else:
            web = WEB_ROOT / "web" / "fusion-rpg-web"
            npm = _npm()
            if not (web / "node_modules").exists():
                run([npm, "install"], stage="web_build", timeout=BUDGETS["web_build"], log=log, cwd=web)
            run([npm, "run", "build"], stage="web_build", timeout=BUDGETS["web_build"], log=log, cwd=web)

        stage("FE mirror (index.html hash proven)")
        if wwwroot_src.exists():
            _mirror(wwwroot_src, wwwroot_dist, stage="wwwroot", log=log)
        else:
            log("  no src wwwroot yet — mirror deferred until after publish")

        stage(f"injector build ({config.loader_host} / {config.game_profile}) -> {config.plugin_dir}")
        if config.loader_host == "MelonLoader":
            run(["dotnet", "build", str(config.injector_proj), "-c", "Release",
                 f"-p:MlGameDir={config.game_dir}", f"-p:GameProfile={config.game_profile}",
                 f"-p:OutputPath={config.plugin_dir}{os.sep}"],
                stage="injector_build", timeout=BUDGETS["injector_build"], log=log)
        else:
            run(["dotnet", "build", str(config.injector_proj), "-c", "Release",
                 f"-p:GameDir={config.game_dir}", f"-p:GameProfile={config.game_profile}"],
                stage="injector_build", timeout=BUDGETS["injector_build"], log=log)
        for required in (config.injector_dll, "FusionRpg.Core.dll"):
            if not (config.plugin_dir / required).exists():
                raise Refusal("injector_build", f"{required} missing after build: {config.plugin_dir}",
                              "the build reported success but produced no artifact")

        stage("freshness (a 'Build succeeded' is not evidence the DLL was rebuilt)")
        if config.skip_freshness:
            log("  skipped (--skip-freshness)")
        else:
            host_root = (REPO_ROOT / "src" / ("FusionRpg.Injector.MelonLoader.39"
                                              if config.game_profile == "pvzrh-3.9"
                                              else "FusionRpg.Injector.MelonLoader")) \
                if config.loader_host == "MelonLoader" else (REPO_ROOT / "src" / "FusionRpg.Injector.BepInEx")
            injector_roots = [REPO_ROOT / "src" / n for n in
                              ("FusionRpg.Injector", "FusionRpg.Contracts", "FusionRpg.Core",
                               "FusionRpg.CheatCore")] + [host_root]
            core_roots = [CORE_ROOT / "src" / n for n in ("FusionRpg.Core", "FusionRpg.Contracts")]
            for dll, roots in ((config.injector_dll, injector_roots), ("FusionRpg.Core.dll", core_roots)):
                newest = newest_source(roots)
                if newest is None:
                    continue
                built = config.plugin_dir / dll
                if built.stat().st_mtime < newest.stat().st_mtime:
                    raise Refusal(
                        "freshness",
                        f"STALE DEPLOY: {dll} is older than the newest source in its dependency set",
                        f"  {dll}  {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(built.stat().st_mtime))}\n"
                        f"  newest .cs  {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(newest.stat().st_mtime))}  ({newest.name})\n"
                        "The build did not actually produce this DLL. Usual causes: the game is still "
                        "running and holds a lock on it, or the injector project skipped compiling.")
            log("  Freshness OK — deployed injector artifacts match their source trees")

        stage("server publish")
        server_was_up = http_ok(health)
        if server_was_up and config.restart_server:
            log(f"  stopping the server on {config.server_url} (--restart-server)")
            _stop_listener(config.server_url, log)
            server_was_up = False
        if server_was_up:
            log(f"  a server already answers {health} — skipping publish (it locks its own DLLs).")
            log("  Pass --restart-server to stop it and publish a fresh binary.")
            if wwwroot_src.exists():
                _mirror(wwwroot_src, wwwroot_dist, stage="wwwroot", log=log)
        elif config.reuse_build and server_exe.exists():
            log(f"  --reuse-build: keeping the published server at {server_exe}")
        else:
            run(["dotnet", "publish",
                 str(CORE_ROOT / "src" / "FusionRpg.Server" / "FusionRpg.Server.csproj"),
                 "-c", "Release", "-o", str(server_out), "--nologo", "-v", "q"],
                stage="server_publish", timeout=BUDGETS["server_publish"], log=log)
            if not server_exe.exists():
                raise Refusal("server_publish", f"server exe missing after publish: {server_exe}")
            if wwwroot_src.exists():
                _mirror(wwwroot_src, wwwroot_dist, stage="wwwroot", log=log)

        stage("seed import (the server boots on this, not code literals)")
        if config.reuse_build and (server_out / "data" / "rpg-hot.sqlite").exists():
            log("  --reuse-build: data already imported, skipping AtomImporter")
        else:
            run(["dotnet", "run", "--project",
                 str(FORGE_ROOT / "tools" / "AtomImporter" / "AtomImporter.csproj"),
                 "-c", "Release", "--", "--db", str(server_out / "data")],
                stage="seed_import", timeout=BUDGETS["seed_import"], log=log)

        # ALWAYS, before any server owns the DLLs and before the game starts. The retired PowerShell
        # wrote this inside `if (-not $NoGame)`, so a pooled deploy (which must use -NoGame) left the
        # slot's game pointed at the owner's server (SSH4.9 class).
        if config.cfg_path is not None:
            stage(f"injector cfg ({config.cfg_path.name})")
            config.cfg_path.parent.mkdir(parents=True, exist_ok=True)
            config.cfg_path.write_text(
                "# FusionRpg MelonLoader host config (written by deploy-play.py)\n"
                f"ServerUrl={config.server_url}\n"
                "PersistCheats=false\n"
                "EnableUnsafeHitPatches=false\n", encoding="utf-8")
            written = re.search(r"^ServerUrl=(\S+)\s*$", config.cfg_path.read_text(encoding="utf-8"),
                                re.MULTILINE)
            if not written or written.group(1).rstrip("/") != config.server_url:
                raise Refusal("cfg", f"cfg at {config.cfg_path} does not name this deploy's server",
                              f"expected ServerUrl={config.server_url}, found "
                              f"{written.group(1) if written else '(no ServerUrl line)'}")
            log(f"  wrote {config.cfg_path} (ServerUrl={config.server_url})")

        stage("server start")
        if config.no_server:
            log(f"  --no-server: published, NOT started. Start it with:")
            log(f"    Start-Process -FilePath \"{server_exe}\" -WorkingDirectory \"{server_out}\"")
        elif server_was_up or http_ok(health):
            log(f"  server already up at {health} (DLLs unchanged)")
        else:
            log(f"  starting {server_exe} (data beside the exe)")
            subprocess.Popen([str(server_exe)], cwd=str(server_out),  # noqa: S603
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline and not http_ok(health):
                time.sleep(1)
            if http_ok(health):
                log(f"  server up: {health}")
            else:
                log(f"  ! server did not answer {health} within 30s — check the server window")

        stage("game launch")
        if config.no_game:
            log("  --no-game: not launched")
        else:
            exe = config.game_dir / "PlantsVsZombiesRH.exe"
            running = _game_running_from(config.game_dir)
            if running:
                log(f"  game already running from this install (pid {running})")
            else:
                log(f"  launching {exe.name} (FUSIONRPG_SERVER_URL={config.server_url})")
                env = dict(os.environ)
                env["FUSIONRPG_SERVER_URL"] = config.server_url
                subprocess.Popen([str(exe)], cwd=str(config.game_dir), env=env,  # noqa: S603
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        stage("test scope")
        if config.full_suite:
            log("  --full-suite: the whole suite (~9 minutes) — feature-complete / cross-boundary only")
            fast_argv = tool_argv("test-fast", ["-AllDefault"], stage="verify",
                                  py_args=["--all-default"])
            log(f"  resolved: {Path(fast_argv[1]).name}")
            run(fast_argv, stage="verify", timeout=BUDGETS["verify"], log=log)
        elif config.verify and config.paths:
            log(f"  scoped verification for: {', '.join(config.paths)}")
            verify_args = ["-Paths", *config.paths]
            if config.session:
                verify_args += ["-Session", config.session]
            verify_py_args = ["--paths", *config.paths]
            if config.session:
                verify_py_args += ["--session", config.session]
            verify_argv = tool_argv("verify-change", verify_args, stage="verify",
                                    py_args=verify_py_args)
            # Logged because this is the site that SWITCHES: gk-core/scripts/verify-change.py ships today, so
            # the next --verify deploy runs the Python port rather than the PowerShell original. That
            # is the intended end state, but an interpreter change in the gate that judges a deploy
            # must be visible in the log rather than discovered later as a behaviour difference.
            log(f"  resolved: {Path(verify_argv[1]).name}"
                + ("  (interpreter switched from the retired .ps1)" if verify_argv[1].endswith(".py") else ""))
            run(verify_argv, stage="verify", timeout=BUDGETS["verify"], log=log)
        else:
            log("  NONE — a deploy runs no tests and no guard suite (that is the deploy/verification")
            log("  split). Pass --verify --paths <files> to bundle the implement-phase gate.")

    except Refusal as refusal:
        verdict["refusals"].append({"stage": refusal.stage, "reason": refusal.reason,
                                    "detail": refusal.detail})
        log(f"REFUSED [{refusal.stage}] {refusal.reason}")
        for line in (refusal.detail or "").splitlines()[-10:]:
            log("  " + line)
    finally:
        if verdict["stages"]:
            verdict["stageSeconds"][verdict["stages"][-1]] = round(time.monotonic() - started, 1)

    verdict["injector"] = str(config.plugin_dir)
    verdict["server"] = str(server_exe)
    verdict["ui"] = config.server_url
    verdict["ok"] = not verdict["refusals"]
    return verdict


def _npm() -> str:
    """The npm to invoke, as a path `subprocess` can actually execute.

    Measured 2026-09-26: `shutil.which("npm")` resolves to `C:\\nvm4w\\nodejs\\npm.CMD`, and the
    shim directory really does ship `npm.cmd` — but `subprocess.run(["npm", ...], shell=False)` on
    Windows goes through `CreateProcess`, which does **not** apply `PATHEXT`; it only tries `.exe`.
    With no `npm.exe` on disk that raises `FileNotFoundError: The system cannot find the file
    specified`, and it did so at stage 3 of 12 — so **every** deploy failed, and with the three-slot
    live pool configured, every pooled deploy failed too.

    Resolution therefore happens here rather than at the call site, the same way `robocopy` is
    resolved in `_mirror` and the way `verify-change.py` resolves its tools. `.cmd` is returned as-is
    rather than wrapped: `CreateProcess` cannot execute a `.cmd` without a shell, so the bare `cmd.exe
    /c` form would be the only way to run it, and the refusal below is the better shape than a
    confusing WinError 193.
    """
    found = shutil.which("npm") or shutil.which("npm.cmd")
    if found and Path(found).suffix.lower() in (".exe", ".cmd", ".bat"):
        return found
    raise Refusal(
        "web_build",
        f"npm is not executable by this process (shutil.which found {found!r})",
        "CreateProcess only tries .exe, so an extensionless \"npm\" cannot be launched without a "
        "shell. Install a real npm.exe on PATH, or run this deploy with --no-rebuild-ui if the "
        "web tree is already built.",
    )


def _mirror(source: Path, destination: Path, *, stage: str, log: Log) -> None:
    """Mirror source -> destination and PROVE it: robocopy 0-7, index.html sha256 must match."""
    robocopy = shutil.which("robocopy") or str(
        Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "robocopy.exe")
    if not Path(robocopy).exists():
        raise Refusal(stage, "robocopy not found (needed to mirror the FE tree)",
                      "expected it on PATH or in System32")
    proc = subprocess.run(  # noqa: S603 - fixed argv
        [robocopy, str(source), str(destination), "/MIR", "/NFL", "/NDL", "/NJH", "/NJS", "/nc",
         "/ns", "/np"], capture_output=True, text=True, timeout=BUDGETS["wwwroot"])
    if proc.returncode >= 8:
        raise Refusal(stage, f"robocopy exit {proc.returncode} mirroring {source}",
                      (proc.stdout or "")[-1500:])
    if not (destination / "index.html").exists():
        raise Refusal(stage, f"mirror left no index.html at {destination}",
                      "the FE build did not produce a served entry point")
    src_hash = sha256_of(source / "index.html")
    if sha256_of(destination / "index.html") != src_hash:
        raise Refusal(stage, "mirror mismatch on index.html",
                      f"src hash {src_hash} != destination hash")
    log(f"  FE synced (index.html sha256 {src_hash[:12]}…)")


def _stop_listener(url: str, log: Log) -> None:
    """Stop whatever LISTENS on the url's port (owner-port restarts only, never a pooled slot)."""
    import psutil
    port = int(url.rsplit(":", 1)[1])
    for conn in psutil.net_connections(kind="tcp"):
        if conn.status == psutil.CONN_LISTEN and conn.laddr and conn.laddr.port == port and conn.pid:
            try:
                psutil.Process(conn.pid).kill()
                log(f"  stopped pid {conn.pid} on :{port}")
            except Exception as exc:  # noqa: BLE001
                log(f"  ! could not stop pid {conn.pid}: {exc}")


def _game_running_from(install: Path) -> int | None:
    """A game pid whose REAL exe path is inside this install (another install's game is not ours)."""
    import psutil
    for proc in psutil.process_iter(["pid", "name", "exe"]):
        try:
            if (proc.info.get("name") or "").lower().startswith("plantsvszombiesrh"):
                exe = proc.info.get("exe") or ""
                if exe and Path(exe).is_relative_to(install):
                    return proc.info["pid"]
        except Exception:  # noqa: BLE001
            continue
    return None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Deploy the game + server + injector into an install (a DEPLOY, never a gate).",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--loader-host", choices=("MelonLoader", "BepInEx"), default="MelonLoader")
    parser.add_argument("--game-dir", default=None, help="or FUSIONRPG_ML_GAMEDIR / FUSIONRPG_GAME_DIR")
    parser.add_argument("--game-profile", default=None, help="pvzrh-3.9 / pvzrh-3.8.1 (auto-detected)")
    parser.add_argument("--server-url", default=OWNER_SERVER_URL,
                        help="the RPG server the game is pointed at (a slot passes its own port)")
    parser.add_argument("--no-game", action="store_true", help="install only, do not launch the game")
    parser.add_argument("--no-server", action="store_true",
                        help="do not LAUNCH a server (publish still happens)")
    parser.add_argument("--no-rebuild-ui", action="store_true", help="skip `npm run build`")
    parser.add_argument("--restart-server", action="store_true",
                        help="owner only: stop the server on --server-url and republish")
    parser.add_argument("--reuse-build", action="store_true",
                        help="skip publish / rebuild / seed import when the artifacts are fresh")
    parser.add_argument("--skip-freshness", action="store_true",
                        help="skip the DLL-vs-source freshness check (pooled deploys of an unchanged build)")
    parser.add_argument("--verify", action="store_true",
                        help="bundle the implement-phase gate (needs --paths or --full-suite)")
    parser.add_argument("--paths", nargs="*", default=[],
                        help="with --verify: the changed files to verify")
    parser.add_argument("--full-suite", action="store_true",
                        help="with --verify: the whole suite (~9 min) — the three AGENTS.md points only")
    parser.add_argument("--session", default="", help="the active session id (for the game lock)")
    parser.add_argument("--dry-run", action="store_true", help="print the plan and touch nothing")
    parser.add_argument("--log", default=None,
                        help="also append output here (default: a timestamped temp file)")
    parser.add_argument("--json", action="store_true", help="emit the verdict as JSON on stdout")
    args = parser.parse_args(argv)

    default_log = Path(os.environ.get("TEMP", ".")) / "fusionrpg-deploy" / \
        f"deploy-{time.strftime('%Y%m%d-%H%M%S')}.log"
    log = Log(Path(args.log) if args.log else default_log)
    config = resolve_config(args, log)

    if config.problems:
        for problem in config.problems:
            log(f"REFUSED: {problem}")
        log("(nothing was touched — the configuration is incomplete)")
        log.close()
        return 2

    log(f"log file: {log.path}")
    verdict = deploy(config)

    if args.json:
        print(json.dumps(verdict, indent=2))
    else:
        log("")
        log(f"Host:     {config.loader_host}")
        log(f"Injector: {config.plugin_dir}")
        log(f"Server:   {verdict.get('server')}")
        log(f"UI:       {config.server_url}")
        log(f"log:      {log.path}")
        for name, seconds in verdict["stageSeconds"].items():
            log(f"  {seconds:7.1f}s  {name}")
    log.close()

    if verdict["refusals"]:
        for refusal in verdict["refusals"]:
            print(f"\nFAILED STAGE [{refusal['stage']}]: {refusal['reason']}", file=sys.stderr)
            if refusal["detail"]:
                print(refusal["detail"][-2500:], file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
