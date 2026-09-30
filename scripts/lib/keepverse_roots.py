"""The roots a path is resolved from: content, authored content, core, fusion, forge, web, workspace.

Resolver contract: tasks/keepverse-split-plan.md "Resolver contract". Before the Keepverse split all
of them are the legacy repo root. After it, content is a gk-data pack (which mirrors the legacy repo
layout, so `gk-data/packs/fusion/data/seed/...` literals stay valid), core is gk-core, fusion is
gk-fusion, forge is gk-forge, web is gk-web, authored content is gk-content, and workspace is the
Keepverse root that holds docs/ and tasks/.

WHY THE SIBLINGS EXIST, and why that matters for the walkers. The workspace root is an ANCESTOR of
gk-core, so a walk upward reaches it by construction. gk-fusion, gk-forge and gk-web are SIBLINGS:
no number of `..` hops from a gk-core subdirectory arrives at one, which is why every "walk up until
you find it" loop that reaches for them fails. gk-content is a sibling too, and it holds exactly ONE
file, so nothing else refers to it and its absence is invisible until a path asks for it.

Order: the KEEPVERSE_*_ROOT environment override wins; otherwise walk up from `start` (default: this
file) to the first legacy repo (FusionRpg.slnx next to gk-data/packs/fusion/data/seed/) or Keepverse workspace (gk-core/
next to gk-data/). Nothing found raises; a root is never guessed.

THIS MODULE HAS FOUR IMPLEMENTATIONS and they are one contract, not four:
    FusionRpg.Core/Workspace/KeepverseRoots.cs      (C#, production, seven accessors)
    gk-core/scripts/lib/keepverse_roots.py         (this file)
    gk-fusion/scripts/lib/keepverse_roots.py
    gk-forge/tools/seedsmith/seedsmith/workspace_roots.py
They have already drifted twice - the C# half gained AuthoredContent and Fusion while this one had
neither, and an audit later found KEEPVERSE_*_ROOT in gk-fusion's copy and not in gk-core's, with the
divergence invisible until something read it - so "keep them in step" is a real requirement with a
real cost, not a formality.

The THREE Python copies are byte-identical, with no exception to record: SHA-256 over the three files
yields ONE distinct digest, measured rather than assumed, and ResolverCopyParityTests fails when that
stops being true. An earlier version of this paragraph claimed an exception for "the line naming
where the copy lives" and said a test asserted it. No test did. The copies were made identical
instead, and the claim was removed rather than left to be rediscovered - a documented exception that
nothing enforces is worse than no exception, because it reads as permission.
"""

from __future__ import annotations

import os
from pathlib import Path

PACK_ENV = "KEEPVERSE_PACK"
DEFAULT_PACK = "fusion"  # structural: the pack the current corpus ships as (decision D7)


class RootNotFound(RuntimeError):
    pass


def _layout(start: Path) -> tuple[str, Path]:
    here = start.resolve()
    for d in (here, *here.parents):
        if (d / "FusionRpg.slnx").is_file() and (d / "data" / "seed").is_dir():
            return "legacy", d
        if (d / "gk-core").is_dir() and (d / "gk-data").is_dir():
            return "workspace", d
    raise RootNotFound(f"no legacy repo or Keepverse workspace above {here}")


def _start(start: Path | None) -> Path:
    return Path(start) if start is not None else Path(__file__).parent


def _env(name: str) -> Path | None:
    """The override for `name`, VALIDATED - or a refusal.

    An unchecked override is not an override; it is a way to switch the refusal off. The whole
    reason this module names an absent repository instead of inventing a path is that a caller must
    never be handed a directory which does not exist - and the discovered-sibling path below
    enforces exactly that. Trusting the environment without the same check defeated the rule it was
    added to serve, and it failed in the worst direction: the override named a directory that was
    not there, the guard carried on as though it were, and the absence surfaced several frames
    later as findings about files the caller had never supplied.

    Measured, with every sibling override pointed at a directory that does not exist: four guards
    reported ABSENCE as findings - nine "stale allowlist entry" from clock-seam, nine "mirror did
    not resolve" from vocabulary-mirror, twelve "baseline entry no longer violated" from
    test-substrate, and two "missing required file" from actor-hub. Every one of those was a guard
    reporting its own blindness as a verdict against the code. With the check below they refuse by
    name instead, which is the same condition stated honestly rather than as an accusation.

    The message names the variable and the path because the variable is what the caller set, and
    unsetting it is the first thing to try.
    """
    v = os.environ.get(name)
    if not v:
        return None
    p = Path(v)
    if not p.is_dir():
        raise RootNotFound(
            f"{name}={p} does not name a directory. An override is a CLAIM about where a repository "
            f"is, so it is checked exactly as a discovered sibling is: unset the variable to return "
            f"to discovery, or point it at the repository."
        )
    return p


def _sibling(start: Path | None, env: str, name: str) -> Path:
    """A repository that is a SIBLING of gk-core, so a walk upward can never reach it.

    It REFUSES when the repository is absent, which is what this module's contract says ("a root is
    never guessed") and what content_root already does for its pack. Without the check a standalone
    gk-core clone - the layout ADDITION 9 asks us to support - gets a confident path to a gk-forge
    that is not there, and the failure surfaces frames later as a FileNotFoundError naming a
    directory nobody can create by following the error's own advice. A refusal names the repository
    that is missing instead.
    """
    if (p := _env(env)) is not None:
        return p
    kind, d = _layout(_start(start))
    target = d if kind == "legacy" else d / name
    if not target.is_dir():
        raise RootNotFound(
            f"{name} is not present at {target} (detected {kind} layout at {d}); "
            "a sibling repository cannot be reached by walking upward, so set the matching "
            "KEEPVERSE_*_ROOT override or place it beside gk-core"
        )
    return target


def content_root(start: Path | None = None) -> Path:
    """Root that repo-relative content paths (gk-data/packs/fusion/data/seed, gk-data/packs/fusion/data/generated) resolve from."""
    if (p := _env("KEEPVERSE_CONTENT_ROOT")) is not None:
        return p
    kind, d = _layout(_start(start))
    if kind == "legacy":
        return d
    pack = d / "gk-data" / "packs" / os.environ.get(PACK_ENV, DEFAULT_PACK)
    if not pack.is_dir():
        raise RootNotFound(f"content pack {pack} does not exist")
    return pack


def authored_content_root(start: Path | None = None) -> Path:
    """Root of the AUTHORED content tree, gk-content.

    A sibling, and easy to leave out: it holds exactly ONE file
    (gk-content/content/display/en.json), so nothing else refers to it and its absence is invisible
    until a path asks for it by name.
    """
    return _sibling(start, "KEEPVERSE_AUTHORED_CONTENT_ROOT", "gk-content")


def core_root(start: Path | None = None) -> Path:
    """Root of the engine repo: src/, tests/, gk-core/data/tuning/."""
    if (p := _env("KEEPVERSE_CORE_ROOT")) is not None:
        return p
    kind, d = _layout(_start(start))
    return d if kind == "legacy" else d / "gk-core"


def fusion_root(start: Path | None = None) -> Path:
    """Root of gk-fusion: the loader hosts, the Launcher, and the Injector's sources."""
    return _sibling(start, "KEEPVERSE_FUSION_ROOT", "gk-fusion")


def forge_root(start: Path | None = None) -> Path:
    """Root of gk-forge: the generators and audit tools, and the Python tree under tools/seedsmith/."""
    return _sibling(start, "KEEPVERSE_FORGE_ROOT", "gk-forge")


def web_root(start: Path | None = None) -> Path:
    """Root of gk-web. The npm package sits one level down at web/fusion-rpg-web/."""
    return _sibling(start, "KEEPVERSE_WEB_ROOT", "gk-web")


def workspace_root(start: Path | None = None) -> Path:
    """Root holding docs/ and tasks/. In a workspace this is gk-workflow, and it is an ANCESTOR of
    gk-core, which is why this one is reachable by walking up and the siblings above are not."""
    if (p := _env("KEEPVERSE_WORKSPACE_ROOT")) is not None:
        return p
    return _layout(_start(start))[1]


def fusion_root_or_owner(start: Path | None = None) -> Path:
    """`fusion_root`, or the nearest root that demonstrably OWNS the engine sources.

    <para><b>Why this exists.</b> `fusion_root` names the engine repository by looking for a legacy repo
    or a Keepverse workspace ABOVE the given start - which is right in the workspace and unanswerable
    inside a planted test fixture, because a temporary directory has no ancestor that qualifies. A guard
    test that plants `src/FusionRpg.Injector/...` in a temp root and then runs the guard against it
    therefore got `RootNotFound` and the guard refused with `FUSION-ROOT-MISSING` before inspecting a
    single file: 26 failures in `test_guard_actor_hub.py` and 18 in `test_guard_clock_seam.py`, none of
    which visible while a collection error was aborting the pytest run.

    <para><b>Why the fallback is evidence, not a guess.</b> A root that itself carries
    `src/FusionRpg.Injector` is not being assumed to own anything - that directory IS the evidence, and
    it is the very marker `fusion_root` would have used one level up. So the walk stops at the first
    root that carries it, and if NO root does, the original `RootNotFound` is re-raised unchanged. A
    blanket "assume the start directory" would have let a guard report a clean verdict about a
    repository that does not exist, which is the failure a guard exists to prevent.

    <para>The `OR_OWNER` suffix is the contract: the resolver is still asked first, so a real workspace
    is always resolved by the shared rule, and only a root that cannot be placed in a workspace falls
    back. This lives here rather than in each guard because the three copies of this contract are held
    byte-identical by `ResolverCopyParityTests` - a rule duplicated per caller is a rule that will drift.
    </para>
    """
    try:
        return fusion_root(start)
    except RootNotFound:
        here = Path(start or Path.cwd()).resolve()
        for parent in (here, *here.parents):
            if (parent / "src" / "FusionRpg.Injector").is_dir():
                return parent
        raise


def root_carrying(start: Path | None, relative: str) -> Path | None:
    """The nearest root - `start` included - that actually CARRIES `relative`, or None.

    WHAT THIS IS FOR. A guard that reads a document the workspace owns - the power inventory at
    `docs/architecture/power/inventory.json` is gk-workflow's and gk-core has no such file at all -
    resolves that document's root with `workspace_root()`. Correct in the workspace, and unanswerable
    inside a planted fixture, because a temporary directory has no qualifying ancestor. So the guard
    refused before reading anything, and four tests read that as a broken contract.

    WHY THE MARKER IS A PATH RATHER THAN A NAME. The fallback asks whether a root carries the SUBJECT
    the caller cares about. That is the same evidence `workspace_root` would have used one level up,
    so a root that has it is demonstrably the owner of that document, rather than a directory that
    merely happens to exist. None is returned rather than `start` when nothing matches, so a caller
    that forgets to check cannot read "no evidence" as "this is the owner".

    This is the general form of what `fusion_root_or_owner` does for the engine sources, and it lives
    here for the same reason: the copies of this contract are held byte-identical by
    `ResolverCopyParityTests`, so a rule written per caller is a rule that will drift.
    """
    here = Path(start or Path.cwd()).resolve()
    rel = str(relative).replace("\\", "/").strip("/")
    if not rel:
        return None
    for parent in (here, *here.parents):
        if (parent / rel).exists():
            return parent
    return None
