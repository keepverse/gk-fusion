#!/usr/bin/env python3
"""Guard: refuse building against a game pack whose fingerprints do not match. Replaces
`guard-game-profile.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
Two mandatory parameters with no defaults, and refusals delivered by `throw`.

A missing catalog or a missing `GameDir` aborted through PowerShell's exception machinery, which
prints a red record to the error stream and exits 1 - the SAME code as a genuine fingerprint
mismatch. So "you pointed me at a directory that does not exist" and "this pack is the wrong game
version" were indistinguishable by exit code, and the second is a refusal that should stop a deploy
while the first is a configuration mistake that should be reported as such. Here they are separate
named refusals with a separate exit code.

Its verdict ALSO arrived through `Write-Host`, which writes the INFORMATION stream and is invisible
to a `2>&1` capture, so a caller could not read the reason for a failure without capturing a stream
it had no reason to expect. Findings and the FAILED verdict go to stderr here; a clean verdict goes
to stdout. See `docs/architecture/ps1-port-checklist.md` item 4.

THE MATCH RULE IS OR-THROUGHOUT, AND THAT IS THE POINT
-----------------------------------------------------
A pack is accepted when ANY fingerprint signal matches: the `GameAssembly.dll` length, OR the length
of any catalogued `Assembly-CSharp.dll`. It does not have to be the same fingerprint that matched,
and it does not have to match on both signals. This is a "is this plausibly the right pack" check
that must not produce false negatives against a repack, so it is deliberately permissive in that
direction and strict only when NOTHING matches.

Three details of that rule are load-bearing and transcribed rather than tidied:

* **A MISSING `GameAssembly.dll` IS LENGTH -1, not 0.** A real fingerprint length is never
  negative, so a missing DLL can never satisfy the GameAssembly signal - but it may still satisfy an
  Assembly-CSharp one. Using 0 instead of -1 would make a zero-length or absent file look like a
  match against a catalog row that happened to carry 0.
* **THE PATH/LENGTH ZIP IS BOUNDED BY THE SHORTER LIST.** A fingerprint whose two arrays disagree in
  length compares only the pairs both lists have. That is deliberate: the arrays are hand-maintained
  and a missing trailing entry should not read as a mismatch for every earlier pair.
* **A FINGERPRINT WITH NO `gameAssemblyLength` SKIPS THAT SIGNAL** rather than matching against
  nothing, which in PowerShell's truthiness an absent, null and zero value all do the same thing.

AN UNKNOWN PROFILE IS ALLOWED, DELIBERATELY
-------------------------------------------
A profile id with no row in the catalog exits 0 and says so. It is not treated as a failure, because
this guard's question is "is this pack the version you said it was" and a profile nobody has
fingerprinted has nothing to disagree with. The message says there is no fingerprint row so the
allowance is visible rather than silent.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

GUARD_ID = "game-profile"
VERDICT_OK = "GAME-PROFILE GUARD OK — {profile} matches {game_dir}"
VERDICT_ALLOWED = ("GAME-PROFILE GUARD: unknown profile '{profile}' — allowing (no fingerprint "
                   "row).")
VERDICT_FAILED = "GAME-PROFILE GUARD FAILED — pack does not match profile '{profile}'."
VERDICT_REFUSED = "GAME-PROFILE GUARD REFUSED"

EXIT_OK = 0
# An unknown profile shares exit 0 with a match, for the reason above; the VERDICT is what
# distinguishes them, and a caller recording only the exit code records "nothing failed".
EXIT_ALLOWED = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_CATALOG = "game-profiles.json"
# A missing GameAssembly.dll is this length, never a real one. See the module docstring.
MISSING_LENGTH = -1


class Refusal(Exception):
    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def load_catalog(path: Path) -> dict:
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError as exc:
        raise Refusal("MISSING_CATALOG", str(path)) from exc
    except OSError as exc:
        raise Refusal("UNREADABLE_CATALOG", f"{path}: {exc}") from exc
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise Refusal("CATALOG-NOT-JSON", f"{path}: {exc}") from exc
    if not isinstance(data, dict) or not isinstance(data.get("profiles"), list):
        raise Refusal("CATALOG-SHAPE-UNEXPECTED", f"{path}: no 'profiles' array")
    return data


def find_profile(catalog: dict, profile_id: str) -> dict | None:
    """The first row whose id matches. `None` means the catalog has no row for it, which ALLOWS."""
    for profile in catalog.get("profiles", []):
        if isinstance(profile, dict) and profile.get("id") == profile_id:
            return profile
    return None


def file_length(path: Path) -> int:
    """A file's length, or MISSING_LENGTH. A directory or unreadable entry is also MISSING_LENGTH,
    because a fingerprint is a statement about a FILE and anything else is not evidence."""
    try:
        if not path.is_file():
            return MISSING_LENGTH
        return path.stat().st_size
    except OSError:
        return MISSING_LENGTH


def game_assembly_length(game_dir: Path) -> int:
    return file_length(game_dir / "GameAssembly.dll")


def fingerprint_matches(fingerprint: dict, game_dir: Path, ga_length: int) -> bool:
    """One fingerprint: the GameAssembly length OR any catalogued Assembly-CSharp length."""
    if not isinstance(fingerprint, dict):
        return False
    # PowerShell truthiness: absent, null and 0 all skip the signal rather than matching it.
    expected_ga = fingerprint.get("gameAssemblyLength")
    if expected_ga and ga_length == int(expected_ga):
        return True
    paths = fingerprint.get("assemblyCSharpPaths") or []
    lengths = fingerprint.get("assemblyCSharpLengths") or []
    # Bounded by the SHORTER list, so a hand-maintained pair that disagrees in length does not make
    # every earlier pair a mismatch.
    for index in range(min(len(paths), len(lengths))):
        # Catalog paths use forward slashes; resolve them SEGMENT-WISE so a Windows path is built
        # from parts. A textual `replace('/', sep)` would also rewrite a slash inside a filename,
        # which is a different path than the one the catalog means.
        candidate = game_dir.joinpath(*str(paths[index]).split("/"))
        if file_length(candidate) == int(lengths[index]):
            return True
    return False


def scan(game_dir: Path, expected_profile: str, catalog_path: Path) -> dict:
    if not game_dir.exists():
        raise Refusal("GAMEDIR-MISSING", str(game_dir))
    catalog = load_catalog(catalog_path)
    profile = find_profile(catalog, expected_profile)

    base = {"guard": GUARD_ID, "profile": expected_profile, "game_dir": str(game_dir),
            "catalog": str(catalog_path), "findings": [], "findings_by_rule": {}}
    if profile is None:
        return {**base, "verdict": "ALLOWED", "reason": "no-fingerprint-row",
                "game_assembly_length": game_assembly_length(game_dir), "fingerprints": 0}

    ga_length = game_assembly_length(game_dir)
    fingerprints = profile.get("fingerprints") or []
    matched = any(fingerprint_matches(fp, game_dir, ga_length) for fp in fingerprints)

    if not matched:
        # The report is the point. "FAILED" with no lengths leaves an operator guessing which of the
        # two signals was wrong, and the two have different fixes.
        return {**base, "verdict": "FAILED", "reason": "no-fingerprint-signal-matched",
                "game_assembly_length": ga_length, "fingerprints": len(fingerprints),
                "findings": [{
                    "rule": "fingerprint-mismatch", "file": str(game_dir),
                    "message": (f"GameAssembly.dll length={ga_length} (expected one of "
                                f"{len(fingerprints)} catalog fingerprint(s))"),
                }],
                "findings_by_rule": {"fingerprint-mismatch": 1}}
    return {**base, "verdict": "OK", "reason": "matched", "game_assembly_length": ga_length,
            "fingerprints": len(fingerprints)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: refuse a pack whose fingerprints do not match the profile "
                    "(replaces guard-game-profile.ps1).")
    parser.add_argument("--game-dir", required=True, type=Path,
                        help="the game pack to check")
    parser.add_argument("--profile", required=True,
                        help="the profile id the pack is claimed to be, e.g. pvzrh-3.9")
    parser.add_argument("--catalog", type=Path, default=None,
                        help=f"fingerprint catalog (default: <repo>/{DEFAULT_CATALOG})")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = Path(__file__).resolve().parent.parent
    catalog_path = args.catalog if args.catalog else root / DEFAULT_CATALOG
    try:
        result = scan(args.game_dir, args.profile, catalog_path)
    except Refusal as refusal:
        print(f"{VERDICT_REFUSED} {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK.format(profile=args.profile, game_dir=args.game_dir))
    elif result["verdict"] == "ALLOWED":
        # Reported, not silent: an allowance is a decision, and the reader should see it was made.
        print(VERDICT_ALLOWED.format(profile=args.profile))
    else:
        print(VERDICT_FAILED.format(profile=args.profile), file=sys.stderr)
        print(f"  GameDir={result['game_dir']}", file=sys.stderr)
        for item in result["findings"]:
            print(f"  {item['message']}", file=sys.stderr)
        print("  Set FUSIONRPG_GAME_PROFILE to the correct id or use the matching game pack.",
              file=sys.stderr)
    return {"OK": EXIT_OK, "ALLOWED": EXIT_ALLOWED, "FAILED": EXIT_FAILED}[result["verdict"]]


if __name__ == "__main__":
    sys.exit(main())
