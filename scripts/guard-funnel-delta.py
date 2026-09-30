#!/usr/bin/env python3
"""Guard: HP deltas only via the Funnel. Replaces `guard-funnel-delta.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
Not a style preference. The original found a real false positive on 2026-09-04: a comment in
`ModifierOp.cs` that *documents* the boundary ("EntityStatWriter.cs writes the entity that has
it") matched the guard, and the guard then failed `deploy-play.py` outright - a deploy blocked by
a comment describing the rule the comment obeyed.

It also emitted the verdict AND every finding with `Write-Host`, which writes the INFORMATION
stream (6) and is invisible to `2>&1`. That is why its findings reached a C# test's `stdout` only
by accident of redirection, and why every port in this migration has had to move assertions: a
PowerShell script whose success text and failure text share a stream cannot be consumed
reliably by anything, including CI. See `docs/architecture/ps1-port-checklist.md`.

THREE CONTRACT DETAILS PRESERVED VERBATIM
-----------------------------------------
A port is a translation with a contract. Each of these was read out of the original and is here
because a tidy-up would silently change what the guard permits:

1. Pattern matching is CASE-SENSITIVE. .NET's `[regex]::IsMatch` is case-sensitive by default, so
   `SetHp` does not catch `setHp`. That is a narrower rule, and it is the rule that shipped.
2. The Injector rule scans RAW text, while the Secondary and Core rules scan comment-stripped
   text. That asymmetry is in the original (`$text` at one call site, `$code` at the others) and
   is NOT harmonised here: harmonising it would make the Injector rule stop flagging trailing
   comments, which is a rule change nobody asked for.
3. `Bag.Grant` and `ctx.Bag.Grant` are BOTH listed, and `ctx.Bag.Grant` is subsumed by the
   former. A file calling `ctx.Bag.Grant(x)` therefore produces TWO findings, not one. The
   redundancy is preserved because findings are per-pattern, and collapsing it would change the
   finding COUNT - a number operators read.

ONE DELIBERATE DIVERGENCE: A MISSING TREE IS A REFUSAL, NOT A PASS
-------------------------------------------------------------------
The original wrapped every scan in `if (Test-Path ...)`, so pointing it at the wrong directory
reported "OK" and exited 0 - the silent-green shape this program exists to remove. Here a missing
`src/` or missing scoped directory is a named refusal with exit 64. All four paths are
load-bearing in this repo, so their absence means a broken checkout or a wrong `--root`, and both
should stop the run rather than pass it.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import core_root, owning_base  # noqa: E402
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
from cscan import line_of, strip_whole_line_comments  # noqa: E402

GUARD_ID = "funnel-delta"
VERDICT_OK = "FUNNEL DELTA GUARD OK — Secondary enqueue via Funnel only"
VERDICT_FAILED = ("FUNNEL DELTA GUARD FAILED — Secondary TakeDamage/SetHp/Bag.Grant "
                  "or multi-ptr Writer:")
EXIT_OK = 0
EXIT_FINDINGS = 1
EXIT_REFUSED = 64

# A build output directory is not source. Checked on the whole path, as a path SEGMENT, so a
# directory named `binding` does not match `bin` and a file named `Combine.cs` does not either.
BUILD_OUTPUT = re.compile(r"(^|[\\/])(obj|bin)([\\/]|$)")

# A file that DECLARES a secondary grant plugin is in scope even outside the plugin directory.
# Matched against RAW text, as the original does, and case-sensitively.
DECLARES_SECONDARY = re.compile(r":\s*.*IEffectGrantPlugin")

# A secondary plugin reaches combat state directly instead of enqueueing on the Funnel. Note
# `SetHp` is deliberately unanchored, so it also flags a local named `setHpCounter` - narrower
# scope in the other direction, and the shipped behaviour.
SECONDARY_PATTERNS = (
    "TakeDamage",
    "SetHp",
    r"thePlantHealth\s*=",
    r"theHealth\s*=",
    r"Bag\.Grant",
    r"ctx\.Bag\.Grant",
)

# Core must not fan out to the writer directly; it composes and hands the delta to the Funnel.
CORE_PATTERNS = (
    "EntityStatWriter",
    "AddPlantHp",
    "AddZombieHp",
    "targetPtrs",
)

# The injector owns the FA10 sink, so the sink's own file is exempt - but ONLY that file. The
# exemption is by FILE NAME anywhere under the injector, exactly as the original's hashtable was.
INJECTOR_PATTERNS = (
    r"EntityStatWriter\.AddPlantHp",
    r"EntityStatWriter\.AddZombieHp",
    "targetPtrs",
)
INJECTOR_EXEMPT_FILES = frozenset({"EntityStatWriter.cs", "InjectorEffectActionSink.cs"})

# EffectFunnel.cs is the funnel: it legitimately names everything it must not do.
FUNNEL_FILE = "EffectFunnel.cs"


@dataclass(frozen=True)
class Rule:
    """One scope, its patterns, and what the original did to the text before matching."""

    rule: str
    scope: tuple[str, ...]
    patterns: tuple[str, ...]
    exclude_names: frozenset[str] = frozenset()
    strip_comments: bool = True
    declared_by: re.Pattern[str] | None = None
    hint: str = ""
    compiled: tuple[re.Pattern[str], ...] = field(default=(), compare=False, repr=False)

    def __post_init__(self) -> None:
        # No re.IGNORECASE: the .NET original was case-sensitive, and narrowing a rule to match
        # fewer things is a different guard wearing the same name.
        object.__setattr__(self, "compiled", tuple(re.compile(p) for p in self.patterns))


RULES = (
    Rule(
        rule="secondary-bypass",
        scope=("src/FusionRpg.Core/Effects/Plugins",),
        patterns=SECONDARY_PATTERNS,
        exclude_names=frozenset({FUNNEL_FILE}),
        declared_by=DECLARES_SECONDARY,
        hint="a secondary grant plugin must enqueue, not write combat state",
    ),
    Rule(
        rule="core-fanout",
        scope=("src/FusionRpg.Core",),
        patterns=CORE_PATTERNS,
        exclude_names=frozenset({FUNNEL_FILE}),
        hint="Core must fan out via CombatDamageDispatcher + Funnel",
    ),
    Rule(
        rule="injector-multi-ptr",
        scope=("src/FusionRpg.Injector",),
        patterns=INJECTOR_PATTERNS,
        exclude_names=INJECTOR_EXEMPT_FILES,
        # Raw text, deliberately - see the module docstring, contract detail 2.
        strip_comments=False,
        hint="HP Add only via FA10 sink / dispatcher fan-out",
    ),
)


class Refusal(Exception):
    """A precondition failed. The run stops; it does not report a clean tree."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


# WHICH REPOSITORY OWNS A SCOPE. Two of this guard's three rule scopes are gk-core's -
# src/FusionRpg.Core and src/FusionRpg.Core/Effects/Plugins - and the third, src/FusionRpg.Injector,
# is this repository's. The module docstring says "All four paths are load-bearing in this repo",
# which was true of the monorepo and is now false for two of them, so the docstring is corrected
# where the correction belongs: at the code that resolves them.
#
# The guard refused with MISSING_SCOPE on every run rather than reporting green, which is the
# right failure and the wrong subject: the scope exists, in a sibling. The named refusal is not
# being made to pass by widening what counts as a scope - it is being given the owner it lost.
_CROSS_REPO_SCOPE = ("src/FusionRpg.Core",)


def owning_root(root: Path, scope: str) -> Path:
    """The root that OWNS `scope`, which is not always this repository.

    `owning_base`, not `core_root`, and the difference is the whole reason this suite could not run.
    This function is asked with a PLANTED FIXTURE root by the guard's own contract tests, because a
    planted violation is the only mechanism by which the rule is proven to fire - and `core_root()`
    raises RootNotFound for a temporary directory, since no legacy repo and no workspace is above it.
    So the guard refused before inspecting a single file and 21 tests reported a missing contract
    instead of the rule they were written to prove.

    `owning_base` asks exactly the right question and asks it in the right order: does the root handed
    in CARRY the scope? If yes, that root is demonstrably its owner - and a fixture that plants
    `src/FusionRpg.Core` is its own owner. Only if it does not, are the workspace siblings consulted,
    which is what resolves the real tree to gk-core.

    Falling back to `root` rather than raising keeps the refusal where the caller expects it:
    `source_files` raises `MISSING_SCOPE` naming the scope, which is the honest verdict for a root that
    does not have it.
    """
    norm = scope.replace("\\", "/").rstrip("/")
    if any(norm == s or norm.startswith(s + "/") for s in _CROSS_REPO_SCOPE):
        return owning_base(norm, root) or root
    return root


def source_files(root: Path, scope: str) -> list[Path]:
    base = owning_root(root, scope) / scope
    if not base.is_dir():
        raise Refusal("MISSING_SCOPE", scope)
    return sorted(p for p in base.rglob("*.cs") if not BUILD_OUTPUT.search(str(p)))


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise Refusal("UNREADABLE_SOURCE", f"{path}: {exc}") from exc


def relative(root: Path, path: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        # A finding in gk-core, reported from this guard. It names the repository rather than
        # falling back to an absolute path: a finding has to be locatable from a clean checkout,
        # and an absolute path locates it only on the machine that produced it.
        # Same reason as owning_root: a planted fixture is its own owner, so resolving through
        # core_root() here raised RootNotFound instead of the ValueError this already handles.
        try:
            base = owning_base("src/FusionRpg.Core", root) or root
            return f"gk-core/{path.relative_to(base).as_posix()}"
        except ValueError:
            return path.name


def scan_rule(root: Path, rule: Rule, already: set[str]) -> tuple[list[dict], set[str]]:
    """Apply one rule. `already` is shared across rules so a file is never reported twice."""
    findings: list[dict] = []
    seen: set[str] = set()

    candidates: list[Path] = []
    for scope in rule.scope:
        candidates.extend(source_files(root, scope))

    if rule.declared_by is not None:
        # A file that declares a secondary grant plugin is in scope wherever it lives.
        for path in source_files(root, "src"):
            if rule.declared_by.search(read(path)):
                candidates.append(path)

    for path in candidates:
        key = str(path)
        if key in already:
            continue
        already.add(key)
        if path.name in rule.exclude_names:
            continue
        text = read(path)
        if not text.strip():
            continue
        code = strip_whole_line_comments(text) if rule.strip_comments else text
        for pattern, compiled in zip(rule.patterns, rule.compiled):
            if not compiled.search(code):
                continue
            seen.add(pattern)
            match = compiled.search(code)
            findings.append({
                "rule": rule.rule,
                "file": relative(root, path),
                "line": line_of(code, match.start()) if match else 0,
                "pattern": pattern,
                "message": f"{relative(root, path)}: matches /{pattern}/",
                "hint": rule.hint,
            })
    return findings, seen


def scan(root: Path) -> dict:
    if not (root / "src").is_dir():
        raise Refusal("MISSING_SOURCE_TREE", str(root / "src"))

    findings: list[dict] = []
    touched: set[str] = set()
    per_rule: dict[str, int] = {}
    for rule in RULES:
        rule_findings, seen = scan_rule(root, rule, touched)
        findings.extend(rule_findings)
        per_rule[rule.rule] = len(rule_findings)
        del seen

    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if findings else "OK",
        "findings": findings,
        "findings_by_rule": per_rule,
        "scanned_files": len(touched),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: HP deltas only via the Funnel (replaces guard-funnel-delta.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = scan(args.root.resolve())
    except Refusal as refusal:
        print(f"FUNNEL DELTA GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED",
                              "reason": refusal.reason, "detail": refusal.detail}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        # Findings on stderr, verdict on stdout. The contract, stated once:
        # docs/architecture/ps1-port-checklist.md item 4.
        print(VERDICT_FAILED, file=sys.stderr)
        for finding in result["findings"]:
            print(f"  {finding['file']}:{finding['line']} matches /{finding['pattern']}/"
                  f" [{finding['rule']}] {finding['hint']}", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FINDINGS


if __name__ == "__main__":
    sys.exit(main())
