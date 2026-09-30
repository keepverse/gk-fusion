#!/usr/bin/env python3
"""Guard: combat HP/ATK/position field writes live only in the pinned writer files.

Replaces `guard-single-writer.ps1`. Provenance for the deletion is here so it survives it.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
Its streams, and its shared scanner.

It emitted the verdict AND every finding with `Write-Host`, which writes the INFORMATION stream (6)
and is invisible to a `2>&1` capture, so its findings reached a C# test's `stdout` by accident of
redirection rather than by design. See `docs/architecture/ps1-port-checklist.md` item 4.

It also carried its OWN copy of the comment stripper — "a stand-alone guard script never
share-includes another script in this repo" — and three such copies have now drifted. The copy here
is not `cscan.strip_comments`: it blanks the CONTENTS of string literals so a pattern cannot match
text inside one, and it handles a doubled quote as an escaped quote. Both were verified
character-for-character against the original before this port; see
`cscan.strip_comments_and_literals` for why `strip_comments` is not a substitute.

THREE RULES, AND THEY DO NOT TREAT TEXT ALIKE
----------------------------------------------
  W1  raw text, across the injector, minus four writer files and three directory carve-outs
  W2  comment-AND-literal-stripped text, per-file pinned field lists, four files only
  W3  the same stripped text, and it applies to EVERY file - allowed or not

TWO CASE-SENSITIVITY RULES IN ONE GUARD, WHICH IS THE SUBTLE PART
---------------------------------------------------------------
The field PATTERNS are matched with .NET's `[regex]::IsMatch`, which is CASE-SENSITIVE: `theHealth`
does not catch `TheHealth`. But every MEMBERSHIP test around them is a PowerShell `-contains` or
`-match`, and those FOLD CASE: a file named `entitystatwriter.cs` is in the allowed list, and a
target named `ATTACKDAMAGE` is a retired field. So this guard is case-sensitive about the text it
scans and case-insensitive about the tables it consults. Both are transcribed; a port that
normalised them would quietly change which files and which fields are exempt.

W1 DOES NOT SKIP obj/ OR bin/; W2 AND W3 DO
--------------------------------------------
Only the second loop carries the build-output test. So a stale generated file under
`src/FusionRpg.Injector/obj/` is still scanned by W1 and is still allowed to fail the guard. That
reads like an oversight and may be one, but it is the shipped contract, and "fixing" it would
narrow a rule nobody asked to narrow.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import core_root  # noqa: E402
# cscan is gk-core's shared C# lexer and there is no copy of it in this repository. In the monorepo
# every tool shared one scripts/ directory, so "the sibling module" was literally true; the split
# moved the lexer a repository away and both of these guards have died with ModuleNotFoundError on
# every run - exit 1, the same code a real finding uses, inside a guard that no CI job invokes,
# which is why the crash went unnoticed rather than being fixed.
#
# Resolved, not vendored. A second copy of a comment-and-string lexer is a second implementation of
# what a finding MEANS - which lines exist, which are masked - and the two copies would drift into
# disagreeing about the same source. The resolver is a byte-identical copy of gk-core's, and this
# line is the link that policy asks for: a repository needing another repository's code uses a
# link, and a guard needing another repository's lexer is that case.
sys.path.insert(0, str(core_root(Path(__file__).resolve().parent.parent) / "scripts"))
from cscan import strip_comments_and_literals  # noqa: E402

GUARD_ID = "single-writer"
VERDICT_OK = ("SINGLE-WRITER GUARD OK — no combat field writes outside EntityStatWriter.cs, "
              "every writer's own field list holds, no retired field is written anywhere")
VERDICT_FAILED = "SINGLE-WRITER GUARD FAILED:"
EXIT_OK = 0
EXIT_FINDINGS = 1
EXIT_REFUSED = 64

INJECTOR = "src/FusionRpg.Injector"

# Case-SENSITIVE: .NET IsMatch, unlike the PowerShell `-match` operator.
SENSITIVE = 0
# Case-INSENSITIVE: PowerShell `-contains` and `-match`.
FOLDED = re.IGNORECASE

BUILD_OUTPUT = re.compile(r"[\\/](obj|bin)[\\/]", FOLDED)
# Three directory carve-outs, each with the reason it exists. `[\\/]Fx[\\/]` and `[\\/]Hud[\\/]`
# are the two the ADR's original enumeration missed; see decisions.md:105.
CARVE_OUTS = (
    (re.compile(r"[\\/]Bridges[\\/]", FOLDED), "version bridges, not a writer"),
    (re.compile(r"[\\/]Fx[\\/]", FOLDED), "VFX GameObjects move particle leases, never an actor"),
    (re.compile(r"[\\/]Hud[\\/]", FOLDED), "HUD objects position their own root and row quads"),
)

# W1. The lawn-reposition group is the fifth Unity write path, and `(?!=)` on each is what keeps a
# COMPARISON from reading as a write: LawnCoords.cs's `z.theZombieRow == row` is a read.
W1_PATTERNS = (
    r"thePlantHealth\s*=",
    r"thePlantMaxHealth\s*=",
    r"theHealth\s*=",
    r"theMaxHealth\s*=",
    r"\.attackDamage\s*=",
    r"theAttackDamage\s*=",
    r"theFirstArmorHealth\s*=",
    r"theFirstArmorMaxHealth\s*=",
    r"theSecondArmorHealth\s*=",
    r"theSecondArmorMaxHealth\s*=",
    r"thePlantRow\s*=(?!=)",
    r"thePlantColumn\s*=(?!=)",
    r"theZombieRow\s*=(?!=)",
    r"transform\.position\s*=(?!=)",
    r"\.localPosition\s*=(?!=)",
)

# Compiled once. The original recompiles nothing (it uses [regex]::IsMatch per file per pattern),
# but a Python port that calls re.search with a string pattern re-parses it for every file, and the
# flag is then easy to lose in a hurry — which is exactly the mistake this guard's dual
# case-sensitivity makes expensive. Compiling here also lets a test assert the flags.
W1_COMPILED = tuple(re.compile(p, SENSITIVE) for p in W1_PATTERNS)

# The four files allowed to write Unity fields at all, each with the reason it is allowed.
ALLOWED_FILES = {
    "EntityStatWriter.cs": "the writer",
    "ZombieCombatFields.cs": "version bridges - only HP width adapters; Writer still owns policy",
    "UniqueBoundLoadout.cs": "W5 ptr-only Bound apply; EntityStatWriter.ForceSet* still follows",
    "EntityPositionWriter.cs": "A-M2 lawn-reposition - sole Plant/Zombie transform and cell writer",
}

# W2: each allowed file's PINNED field list. A field absent here is a new writable Unity field, and
# adding one is a reviewed architecture change. UniqueBoundLoadout's list is EMPTY and that is the
# measured answer, not an oversight: every grant goes through the RPG-layer Funnel.
ALLOWED_FIELDS: dict[str, tuple[str, ...]] = {
    "EntityStatWriter.cs": (
        "thePlantHealth", "thePlantMaxHealth", "thePlantAttackCountDown", "thePlantAttackInterval",
        "thePlantProduceCountDown", "thePlantProduceInterval", "thePlantSpeed", "attackSpeedAdder",
        "moveSpeed", "theLevel", "shootingLevel",
        "SetHp", "SetMaxHp", "theFirstArmorHealth", "theFirstArmorMaxHealth", "theSecondArmorHealth",
        "theSecondArmorMaxHealth", "theSpeed", "theOriginSpeed", "uniqueSpeed", "butterSpeed",
        "coldSpeed", "freezeSpeed",
    ),
    "ZombieCombatFields.cs": ("theHealth", "theMaxHealth"),
    "UniqueBoundLoadout.cs": (),
    "EntityPositionWriter.cs": ("thePlantColumn", "thePlantRow", "theZombieRow", "transform.position"),
}

# W3: retired EVERYWHERE by the 2026-09-16 owner ruling - the RPG's own combat math already pays
# these, so a PvZ write double-pays. The commented-out lines beside them in EntityStatWriter.cs
# explain why and must never be deleted; W3 is what makes keeping them safe.
RETIRED_FIELDS = ("attackDamage", "theAttackDamage", "theShieldHealth", "theArmor",
                  "takeDmgMultiplier")

# A live write: a p/z receiver, a field or dotted field path, an assignment that is not `==`.
LIVE_FIELD = re.compile(r"\b[pz]\.(\w+(?:\.\w+)?)\s*=(?!=)", SENSITIVE)
# The version-bridge setter form, which writes without a receiver.
BRIDGE_SETTER = re.compile(r"ZombieCombatFields\.(Set\w+)\s*\(", SENSITIVE)


class Refusal(Exception):
    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def is_allowed_file(name: str) -> bool:
    """Case-INSENSITIVE, because the original's `-contains` is."""
    return canonical_writer(name) is not None


def canonical_writer(name: str) -> str | None:
    """The `ALLOWED_FIELDS` key for a file name, or None if it is not a writer.

    The lookup FOLDS CASE, because the original indexes a PowerShell hashtable and hashtable
    lookup is case-insensitive by default. Getting this wrong is not theoretical: a file named
    `entitystatwriter.cs` passes `is_allowed_file` and then misses every pinned list, so each of
    its writes is reported as a W2 violation of a list it was never judged against. The
    differential found exactly that.
    """
    lowered = name.lower()
    for candidate in ALLOWED_FIELDS:
        if candidate.lower() == lowered:
            return candidate
    return None


def carve_out(path: Path) -> str | None:
    for pattern, why in CARVE_OUTS:
        if pattern.search(str(path)):
            return why
    return None


def live_targets(code: str) -> list[str]:
    """Every Unity field this stripped code WRITES, in the order the original collected them.

    Order is the original's: all receiver-form matches first, then all bridge-setter matches. A
    finding list is read by a human, and two rules reporting the same field should appear in the
    sequence a reader expects.
    """
    targets = [m.group(1) for m in LIVE_FIELD.finditer(code)]
    targets.extend(m.group(1) for m in BRIDGE_SETTER.finditer(code))
    return targets


def is_retired(field: str) -> bool:
    """Case-INSENSITIVE, because the original's `-contains` is."""
    lowered = field.lower()
    return any(lowered == name.lower() for name in RETIRED_FIELDS)


def allowed_field(writer: str, field: str) -> bool:
    """Membership in a writer's pinned list. Case-INSENSITIVE: PowerShell's `-notcontains` is."""
    return any(field.lower() == candidate.lower() for candidate in ALLOWED_FIELDS[writer])


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise Refusal("UNREADABLE_SOURCE", f"{path}: {exc}") from exc


def w1(root: Path) -> list[dict]:
    """Raw text. No obj/bin carve-out here - only the second loop has one."""
    out: list[dict] = []
    for path in sorted((root / INJECTOR).rglob("*.cs")):
        if is_allowed_file(path.name):
            continue
        if carve_out(path):
            continue
        text = read(path)
        for source, compiled in zip(W1_PATTERNS, W1_COMPILED):
            if compiled.search(text):
                out.append({
                    "rule": "W1",
                    "file": path.relative_to(root).as_posix(),
                    "message": f"{path.relative_to(root).as_posix()}: matches /{source}/",
                })
    return out


def w2_w3(root: Path) -> list[dict]:
    """Stripped text, and W3 applies to every file while W2 applies to the four allowed ones."""
    out: list[dict] = []
    for path in sorted((root / INJECTOR).rglob("*.cs")):
        if BUILD_OUTPUT.search(str(path)):
            continue
        rel = path.relative_to(root).as_posix()
        code = strip_comments_and_literals(read(path))
        for target in live_targets(code):
            # W3 first, and it covers every file - a retired field is retired everywhere.
            if is_retired(target):
                out.append({
                    "rule": "W3",
                    "file": rel,
                    "field": target,
                    "message": (f"W3 {rel}: writes retired field '{target}' — retired everywhere by "
                                "the 2026-09-16 owner ruling; the RPG's own combat math already pays "
                                "it, a PvZ write double-pays"),
                })
                continue
            # W2 only applies where a pinned list exists, and never double-reports a W3 finding.
            writer = canonical_writer(path.name)
            if writer is None:
                continue
            if not allowed_field(writer, target):
                out.append({
                    "rule": "W2",
                    "file": rel,
                    "field": target,
                    "message": (f"W2 {rel}: writes '{target}', which is not in this file's pinned "
                                "field list — a new writable Unity field is a reviewed architecture "
                                "change (read AGENTS.md 'Every RPG feature lives in the RPG layer' "
                                "first)"),
                })
    return out


RULE_IDS = ("W1", "W2", "W3")


def scan(root: Path) -> dict:
    injector = root / INJECTOR
    if not injector.is_dir():
        # The original THREW here rather than reporting a clean tree, so this is a refusal and not
        # a pass. Exit 64 rather than the throw's 1: the refusal vocabulary is the port's own.
        raise Refusal("MISSING_INJECTOR", str(injector))

    # W2 and W3 come from ONE pass in the original and share its output, so they are scanned once
    # rather than walked twice - a second walk would duplicate every W3 finding under a W2 name.
    findings = w1(root) + w2_w3(root)
    by_rule: dict[str, int] = {}
    for item in findings:
        by_rule[item["rule"]] = by_rule.get(item["rule"], 0) + 1
    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if findings else "OK",
        "findings": findings,
        "findings_by_rule": by_rule,
        "rule_ids": list(RULE_IDS),
        "scanned_files": len(list((root / INJECTOR).rglob("*.cs"))),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: combat field writes live in the pinned writers "
                    "(replaces guard-single-writer.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = scan(args.root.resolve())
    except Refusal as refusal:
        print(f"SINGLE-WRITER GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        # Findings and the FAILED verdict on stderr; a clean verdict on stdout.
        # docs/architecture/ps1-port-checklist.md item 4.
        print(VERDICT_FAILED, file=sys.stderr)
        for item in result["findings"]:
            print(f"  {item['message']}", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FINDINGS


if __name__ == "__main__":
    sys.exit(main())
