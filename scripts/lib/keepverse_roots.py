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

THIS MODULE HAS THREE IMPLEMENTATIONS and they are one contract, not three:
    FusionRpg.Core/Workspace/KeepverseRoots.cs      (C#, production, seven accessors)
    gk-core/scripts/lib/keepverse_roots.py         (this file)
    gk-forge/tools/seedsmith/seedsmith/workspace_roots.py
They have already drifted once - the C# half gained AuthoredContent and Fusion while this one had
neither - so "keep them in step" is a real requirement with a real cost, not a formality. The two
Python copies are byte-identical except for the line naming where the copy lives, which cannot be
otherwise without the sentence contradicting itself; a test asserts that, so the exception is
recorded rather than left to be rediscovered.
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
    v = os.environ.get(name)
    return Path(v) if v else None


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
