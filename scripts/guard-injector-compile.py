#!/usr/bin/env python3
"""Guard: the MelonLoader injector host still compiles. Replaces `guard-injector-compile.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
Two reasons, both about a tool that cannot be trusted to report what it did.

**It had no timeout.** `& dotnet build ... 2>&1` waits indefinitely, so a hung restore or a
satiated NuGet lock turned a guard into a hang with no report. Every external call here carries a
hard `--timeout`, and a timeout is a NAMED verdict rather than a stalled process.

**Its streams were unreadable, and its skip was indistinguishable from a pass.** The verdict and the
skip notice both went out through `Write-Host`, which writes the INFORMATION stream and is invisible
to a `2>&1` capture. Worse, BOTH exited 0: a caller that checked the exit code could not tell
"compiled" from "never tried because no game dir was configured", which is the difference between
evidence and the absence of it. The verdict set here is therefore three values - OK, SKIPPED,
FAILED - and `--json` carries which one happened even though SKIPPED keeps the original's exit 0 so
no caller has to change.

THE ONE SUBTLE RULE: EXIT 0 FROM THE COMPILER IS NOT PROOF OF A COMPILE
------------------------------------------------------------------------
The build is run with an explicit `-p:OutputPath` and `-p:MlGameDir`, and the project can decide it
has no interop references under that game dir and print `NOT COMPILED - skipping
FusionRpg.Injector.MelonLoader.39` while still exiting 0. The original treats that as a FAILURE, and
so does this: a guard that reports OK for a project that declined to build is worse than no guard,
because it is trusted. That is why the check exists at all, and it is why a build log is inspected
rather than just an exit code.

THE ENV PRECEDENCE IS DELIBERATE, so it is transcribed rather than tidied
------------------------------------------------------------------------
  1. FUSIONRPG_ML_GAMEDIR_PVZRH_3_9  the cell's own variable, the one a solution build reads
  2. FUSIONRPG_ML_GAMEDIR             loader-wide; this guard still WINS over it for the cell,
                                      because it passes the pack as an explicit -p: below
  3. FUSIONRPG_ML_GAMEDIR_DEFAULT     from the repo .env

Reading them "most specific first" is right here, but the reason the middle one loses is specific:
the cell must be pvzrh-3.9, and a caller that set only the loader-wide variable to a 3.8.1 pack
would otherwise compile the Int64 bridge against a 3.8.1 interop's Int32 fields. The build is for
ONE cell of the injector matrix - pvzrh-3.9 x MelonLoader, per docs/architecture/game-versioning.md
- and the pack is pinned per cell rather than per loader.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

GUARD_ID = "injector-compile"
VERDICT_OK = "INJECTOR COMPILE GUARD OK — MelonLoader host (cell pvzrh-3.9) compiled to {out}"
VERDICT_SKIPPED = ("INJECTOR COMPILE GUARD SKIPPED — no MelonLoader game dir for cell pvzrh-3.9 "
                   "(set FUSIONRPG_ML_GAMEDIR_PVZRH_3_9, or FUSIONRPG_ML_GAMEDIR for a single-cell "
                   "caller); injector NOT compiled")
VERDICT_FAILED = "INJECTOR COMPILE GUARD FAILED"
VERDICT_REFUSED = "INJECTOR COMPILE GUARD REFUSED"
VERDICT_TIMED_OUT = "INJECTOR COMPILE GUARD FAILED — the build did not finish inside --timeout"

EXIT_OK = 0
EXIT_FAILED = 1
# A skip keeps the ORIGINAL's exit 0 so no caller changes, and the verdict says which it was. A
# guard that cannot distinguish "compiled" from "never tried" is the defect being retired.
EXIT_SKIPPED = 0
EXIT_REFUSED = 64
EXIT_TIMED_OUT = 1

PROJECT = "src/FusionRpg.Injector.MelonLoader.39/FusionRpg.Injector.MelonLoader.39.csproj"
OUT_SUBPATH = ("fusionrpg-injector-compile",)
CELL = "pvzrh-3.9"
ENV_CELL = "FUSIONRPG_ML_GAMEDIR_PVZRH_3_9"
ENV_LOADER = "FUSIONRPG_ML_GAMEDIR"
ENV_DEFAULT = "FUSIONRPG_ML_GAMEDIR_DEFAULT"

# The project declining to build, matched on the message it prints. Verified with a real run rather
# than trusted: on this machine the MelonLoader host compiles, so the string is transcribed from the
# original and the check is proven by a test that feeds it the text, not by a live failure.
SKIPPED_BY_PROJECT = re.compile(
    r"NOT COMPILED\s+[—-]\s+skipping FusionRpg\.Injector\.MelonLoader\.39")
# Error lines for the failure excerpt, with the spaces the original's `' error '` had.
ERROR_LINE = re.compile(r" error ")
ENV_LINE = re.compile(r"^\s*FUSIONRPG_ML_GAMEDIR_DEFAULT\s*=")

# The original passed no timeout at all. A restore that stalls is a hang with no report, so the
# default is finite and generous: a cold restore of the MelonLoader host is the slow case, and a
# guard that times out on a legitimately slow build is its own false red.
DEFAULT_TIMEOUT = 900
MAX_ERROR_LINES = 20


class Refusal(Exception):
    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def resolve_game_dir(root: Path, env: dict | None = None) -> tuple[str | None, str]:
    """The MelonLoader pack for this cell, and where it came from.

    Returns the source alongside the value because "which variable decided this" is the question an
    operator has when a build is skipped, and a guard that cannot answer it makes the skip
    unactionable.
    """
    environ = os.environ if env is None else env
    for name in (ENV_CELL, ENV_LOADER):
        value = environ.get(name)
        if value:
            return value, f"env:{name}"
    env_file = root / ".env"
    if env_file.is_file():
        try:
            for line in env_file.read_text(encoding="utf-8", errors="replace").splitlines():
                if ENV_LINE.match(line):
                    # Split on the FIRST '=' only, so a Windows path or a value containing '='
                    # survives. The original's `-split '=', 2` did the same.
                    return line.split("=", 1)[1].strip(), f"file:.env:{ENV_DEFAULT}"
        except OSError as exc:
            raise Refusal("UNREADABLE_ENV_FILE", f"{env_file}: {exc}") from exc
    return None, "unset"


def build_argv(project: Path, out: Path, game_dir: str) -> list[str]:
    return [
        "dotnet", "build", str(project), "-c", "Release",
        f"-p:OutputPath={out}", f"-p:MlGameDir={game_dir}", "-nologo",
    ]


def run_build(project: Path, out: Path, game_dir: str, timeout: int) -> dict:
    argv = build_argv(project, out, game_dir)
    try:
        # No `cwd`: MSBuild resolves Directory.Build.props by walking UP FROM THE PROJECT, not from
        # the working directory, so passing an absolute project path is sufficient. An earlier
        # draft set `cwd=project.parents[2]`, which raises IndexError on a project path with fewer
        # than three segments - a crash in a guard, discovered by a test that passed a bare
        # relative path. Doing nothing is both correct and impossible to get wrong.
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as expired:
        # A named verdict, not a raised traceback and not a silent pass. A guard that hangs tells
        # the operator nothing; one that says TIMED OUT after N seconds tells them where to look.
        output = (expired.stdout or "") + (expired.stderr or "")
        return {"exit": EXIT_TIMED_OUT, "output": output, "timed_out": True}
    except FileNotFoundError as exc:
        raise Refusal("DOTNET-NOT-ON-PATH", str(exc)) from exc
    except OSError as exc:
        raise Refusal("BUILD-INVOCATION-FAILED", str(exc)) from exc
    return {"exit": proc.returncode, "output": (proc.stdout or "") + (proc.stderr or ""),
            "timed_out": False}


def scan(root: Path, timeout: int = DEFAULT_TIMEOUT, env: dict | None = None) -> dict:
    project = root / PROJECT
    game_dir, source = resolve_game_dir(root, env)

    if not game_dir or not (Path(game_dir) / "MelonLoader").is_dir():
        return {"guard": GUARD_ID, "verdict": "SKIPPED", "reason": "no-melonloader-pack",
                "cell": CELL, "game_dir": game_dir, "game_dir_source": source,
                "findings": [], "findings_by_rule": {}, "compiled": False, "timed_out": False}

    if not project.is_file():
        # Fail closed. A missing project with a game dir configured is a broken checkout, and
        # reporting SKIPPED there would read as "no pack" when the real answer is "no project".
        raise Refusal("MISSING_PROJECT", str(project))

    out = Path(tempfile.gettempdir()).joinpath(*OUT_SUBPATH)
    result = run_build(project, out, game_dir, timeout)
    output = result["output"]
    findings: list[dict] = []

    if result["timed_out"]:
        findings.append({"rule": "build-timeout", "file": PROJECT,
                         "message": f"the build did not finish within {timeout}s"})
    elif result["exit"] != 0:
        for line in [l for l in output.splitlines() if ERROR_LINE.search(l)][:MAX_ERROR_LINES]:
            findings.append({"rule": "build-failed", "file": PROJECT, "message": line.strip()})
        if not findings:
            # The build failed and printed nothing matching `' error '`. Reporting the tail is
            # better than reporting nothing, because a bare FAILED with no reason is the failure
            # mode this tool is being retired for.
            tail = [l for l in output.splitlines() if l.strip()][-MAX_ERROR_LINES:]
            for line in tail:
                findings.append({"rule": "build-failed", "file": PROJECT, "message": line.strip()})
    elif SKIPPED_BY_PROJECT.search(output):
        # Exit 0 AND a refusal to build. This is the case the guard exists for.
        findings.append({
            "rule": "project-skipped-itself", "file": PROJECT,
            "message": (f"project skipped itself (interop refs not found under {game_dir}); "
                        "dotnet build exited 0, which is not proof of a compile")})

    by_rule: dict[str, int] = {}
    for item in findings:
        by_rule[item["rule"]] = by_rule.get(item["rule"], 0) + 1
    return {"guard": GUARD_ID, "verdict": "FAILED" if findings else "OK",
            "cell": CELL, "game_dir": game_dir, "game_dir_source": source,
            "output_dir": str(out), "build_exit": result["exit"], "timed_out": result["timed_out"],
            "findings": findings, "findings_by_rule": by_rule, "compiled": not findings}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: the MelonLoader injector host compiles (replaces "
                    "guard-injector-compile.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds to allow the build (default: {DEFAULT_TIMEOUT}). "
                             "The original passed none, so a stalled restore hung forever.")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        print(f"{VERDICT_REFUSED} INVALID-TIMEOUT: --timeout must be positive, got {args.timeout}",
              file=sys.stderr)
        return EXIT_REFUSED

    try:
        result = scan(args.root.resolve(), args.timeout)
    except Refusal as refusal:
        print(f"{VERDICT_REFUSED} {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK.format(out=result["output_dir"]))
    elif result["verdict"] == "SKIPPED":
        # A skip is REPORTED, never passed off as a compile - the original's own words.
        print(VERDICT_SKIPPED)
    else:
        # Findings and the FAILED verdict on stderr; OK and SKIPPED verdicts on stdout.
        # docs/architecture/ps1-port-checklist.md item 4.
        if result["timed_out"]:
            print(VERDICT_TIMED_OUT, file=sys.stderr)
        else:
            print(VERDICT_FAILED, file=sys.stderr)
        for item in result["findings"]:
            print(f"  [{item['rule']}] {item['message']}", file=sys.stderr)
    return {"OK": EXIT_OK, "SKIPPED": EXIT_SKIPPED, "FAILED": EXIT_FAILED}[result["verdict"]]


if __name__ == "__main__":
    sys.exit(main())
