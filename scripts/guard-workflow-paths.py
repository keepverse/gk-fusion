#!/usr/bin/env python3
r"""Every path a workflow names must resolve inside a checkout THAT JOB DECLARED.

    python gk-fusion/scripts/guard-workflow-paths.py                       # this repository's workflows
    python gk-fusion/scripts/guard-workflow-paths.py --json
    python gk-fusion/scripts/guard-workflow-paths.py --workspace D:/ci/ws   # a runner's workspace
    python gk-fusion/scripts/guard-workflow-paths.py --workflow release.yml  # one file

WHY THIS EXISTS
---------------
The repository was split into several repositories, and `gk-fusion`'s release workflow was
left naming nine lines that resolve in none of the two checkouts its publish job declared.
It died at the first of them, so no release could publish, and it died *before* any test ran
-- so nothing had ever reported it. The same file had already removed the identical defect
class from its own test-matrix block, with the reason written down: "a gate that cannot
pass is worse than no gate, because it looks like coverage." This tool is the standing
refusal for that class.

THE TRAP, AND WHY A CONTENT CHECK AND NOT A DECLARATION CHECK
-------------------------------------------------------------
A workflow that references a path from a repository it never checked out cannot be caught by
asking whether the path is *inside a declared checkout* -- it is, syntactically: `./scripts/x.py`
at the job's base directory is under the workspace root, which every job has. What is missing
is the FILE, and only the checked-out trees can say whether it is there. So this tool resolves
each reference against the real tree the checkout would have produced.

That makes it unrunnable in a job that has only one checkout, BY CONSTRUCTION, and that is the
property to keep. It runs in two places, and both are places where the trees exist:

  * in a workspace (default: this repository's parent), where every sibling is on disk, and
  * as the FIRST verification step of a release job that has just declared its checkouts,
    where `--workspace $env:GITHUB_WORKSPACE` points the tool at the runner's own layout.

Running it in a single-repository CI job could only pass vacuously, so it is NOT wired there
for the workflows that need siblings. A repository's own `ci.yml`, which references nothing
outside itself, is a legitimate CI target via `--workflow`.

HOW A CHECKOUT IS MAPPED TO A LOCAL TREE
----------------------------------------
A checkout's content is a REPOSITORY, and this tool needs to know which one:

  * `repository:` absent  ->  the repository this workflow file lives in. With a `path:`, the
    tree is expected at <repo>/<path> or <workspace>/<path>; without one, at the repository root.
  * `repository:` present ->  <workspace>/<path>, and `path:` is REQUIRED. A checkout with a
    `repository:` and no `path:` lands at the workspace root, where it is indistinguishable
    from this repository's own checkout -- and a step naming a file there has no way to say
    which tree it meant. That is reported as NO-PATH, by name.
  * a checkout's `path:` MUST equal the last segment of its `repository:`. `path: gk-core` for
    `repository: keepverse/gk-web` is a reference that silently resolves against the wrong
    tree, and it is a declaration error no filesystem lookup can detect.

`--workspace` is the directory that plays the part of `github.workspace`. Its default is this
repository's parent, which is what makes the tool work in a sibling checkout on a developer's
machine with no arguments.

WHAT COUNTS AS A PATH REFERENCE, AND WHAT DOES NOT
--------------------------------------------------
The vocabulary is CLOSED and printed in every report, because a guard whose coverage is
unknown is a gate that looks like coverage:

  `python`/`python3`/`py -3` <script>      (a `-m` module and a `-c` string are not paths)
  `node` <script>          `bash`/`sh` <script>
  `pwsh`/`powershell` -File <script>
  `pip install -r <file>`  `pip install -e <dir>`
  `npm ... --prefix <dir>`
  `dotnet test|build|publish <project>`   `dotnet run --project <project>`
  a line whose command word is `./x` or `.\x`

Every one of these names something that must ALREADY EXIST when the step starts, which is why
the vocabulary is safe: an argument in this class is a reference, never an output.

Two classes are deliberately NOT examined, and saying so is part of the contract:

  * OUTPUT paths -- `dist/FusionRpg`, `artifacts/x.zip`, a `Set-Content` target, an
    `Out-File` destination. A step creates those; checking them would redden every release
    that builds something.
  * filesystem CMDLETS as path readers -- `Test-Path`, `Get-Content`, `Get-Item`. These are
    ambiguous in exactly the way that produces a lie: a release reads back
    `artifacts/player-pack-smoke.json` that an EARLIER step wrote, and a scan that cannot
    tell a read-back from a read of a tree would report it as a missing input.

ONE OUTPUT CLASS IS EXAMINED ANYWAY
-----------------------------------
A pwsh redirect whose parent directory the job never creates is reported
(`REDIRECT-PARENT-UNDECLARED`), because it is not a missing file -- it is a gate that cannot
run and cannot say so. Measured in gk-core's own ci.yml, which documents the failure: a pwsh
redirect to a path whose parent is missing raises "Could not find a part of the path", does
NOT run the command, and leaves `$LASTEXITCODE` at 0. So the `if ($LASTEXITCODE -ne 0)`
after such a redirect never fires, no artifact is produced, and the step reports success
having run nothing. The release IP gate had exactly this shape.

WHAT THIS TOOL MUST NOT DO
--------------------------
It must never report green over a workflow it could not read, and never over an EMPTY
selection of workflows -- both are named refusals, because "no findings" and "found nothing
to look at" are different claims and only one of them is a pass. And it must never invent a
checkout: a reference that resolves in no declared tree is a finding, never a reason to go
looking somewhere the job did not declare.

MEASURED: WHICH RULES THE CONTRACT SUITE ACTUALLY CONSTRAINS
------------------------------------------------------------
`scripts/test_workflow_paths.py` is only worth its runtime if removing a rule makes a case fail,
so that was measured rather than assumed. Six rules were disabled one at a time and the suite
re-run. All six turned it red:

  scan every python argument, not just the script ....... 5 failures
  treat a comment line as the end of a run block ........ 1 failure
  drop the pwsh redirect rule ........................... 1 failure
  drop the ./script command-word rule ................... 1 failure
  apply working-directory to a `uses:` step too ......... 2 failures
  drop the containment requirement on the resolved path . 1 failure

Three of those six were GREEN on the first attempt, and that is the number worth remembering: the
suite shipped with three rules it did not constrain -- the `./x` form, the containment requirement,
and the comment-inside-a-run-block rule that is this tool's own worst bug. The three cases that now
close them were added because the mutation run found the gap, not because the rule looked
undertested.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "workflow-paths"
EXIT_OK = 0
EXIT_FINDINGS = 1
EXIT_REFUSED = 2

# Extensions that make a bare token a path even without a separator, so `dotnet test X.csproj`
# and `python foo.py` are both caught. A token with no separator and no such extension is a
# module name, a command name or an argument VALUE, and treating it as a path is how a scan
# starts crying wolf.
PATH_SUFFIXES = (".py", ".ps1", ".sh", ".bash", ".csproj", ".slnx", ".sln", ".props", ".targets",
                 ".json", ".yml", ".yaml", ".lock", ".cfg", ".toml", ".js", ".mjs", ".ts", ".cmd",
                 ".bat", ".dll", ".exe")

# `with:` inputs that carry a path. A closed list on purpose: an input this tool does not know
# about is counted in the report's `withInputsNotExamined` so a reader can see the gap instead
# of having to assume there is none.
#
# `cache` is NOT here: `cache: npm` is a cache KEY, not a path, and treating it as one is how a
# guard learns to be ignored. A checkout's `path:` is not here either -- it is a DECLARATION of
# where a tree lands, and `references_in_inputs` skips it on the checkout step that owns it.
PATH_INPUTS = ("paths", "cache-dependency-path", "working-directory")

# A `>` redirect, with a MANDATORY `>`. An optional one matched any path-shaped token on any
# line, which reported `Get-Content x.json` as a redirect and invented a finding for a line that
# writes nothing.
REDIRECT = re.compile(r"(?:^|[^0-9>|&<>])>\s*([^\s>|&]+)")
# PowerShell/GitHub-Actions command separators. A segment after one of these starts a new
# command word, which is what makes `... | Out-Null` and `a && b` scan correctly.
SEGMENT_SPLIT = re.compile(r"\|\||&&|\||;|\{|\}")
# A command word that names a script relative to the current directory.
DOT_COMMAND = re.compile(r"^\.{1,2}[\\/]")

WRITE_CMDS = ("new-item", "mkdir", "md", "copy-item", "out-file", "set-content", "add-content")

VERBATIM = (
    "python <script>", "python -m <module> (not a path)", "python -c <code> (not a path)",
    "node <script>", "bash|sh <script>", "pwsh|powershell -File <script>",
    "pip install -r <file>", "pip install -e <dir>", "npm --prefix <dir>",
    "dotnet test|build|publish <project>", "dotnet run --project <project>",
    "./x or .\\x as a command word", "pwsh `>` redirect target's parent directory",
)


class Refusal(Exception):
    """A named precondition failure. The run says WHICH one, and never exits 0 having not looked."""

    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


@dataclass
class Reference:
    """One path a step names, and where it was found."""

    path: str
    line: int
    job: str
    step: str
    form: str
    kind: str = "reference"  # "reference" | "redirect"


@dataclass
class Checkout:
    """A declared `actions/checkout`, and the local tree it maps to."""

    path: str
    repository: str | None
    line: int


@dataclass
class Step:
    name: str
    line: int
    uses: str | None
    run: list[tuple[int, str]] = field(default_factory=list)
    inputs: list[tuple[str, str, int]] = field(default_factory=list)
    working_directory: str | None = None


@dataclass
class Job:
    name: str
    line: int
    uses: str | None = None
    uses_line: int = 0
    default_working_directory: str | None = None
    steps: list[Step] = field(default_factory=list)
    checkouts: list[Checkout] = field(default_factory=list)


@dataclass
class Workflow:
    path: Path
    rel: str
    jobs: list[Job] = field(default_factory=list)
    default_working_directory: str | None = None


# ------------------------------------------------------------------------------------------------
# Reading
# ------------------------------------------------------------------------------------------------

def strip_comment(line: str) -> str:
    """A `#` comment, honouring single and double quotes so a `#` inside a path is not cut."""
    out: list[str] = []
    quote: str | None = None
    for ch in line:
        if quote:
            out.append(ch)
            if ch == quote:
                quote = None
            continue
        if ch in "'\"":
            quote = ch
            out.append(ch)
            continue
        if ch == "#":
            break
        out.append(ch)
    return "".join(out)


def unquote(token: str) -> str:
    token = token.strip()
    if len(token) >= 2 and token[0] == token[-1] and token[0] in "'\"":
        return token[1:-1]
    return token


def clean_value(value: str) -> str:
    value = value.split(" #")[0] if " #" in value else value
    return unquote(value.strip())


def read_workflow(path: Path, rel: str) -> Workflow:
    """A workflow, read as STRUCTURED TEXT rather than as YAML.

    No YAML library: none is a declared dependency of any job in this workspace, and a guard
    that needs a `pip install` to run is a guard that silently stops running on the machine
    where the install was forgotten. The subset read here -- jobs, steps, `uses`, `run:`,
    `with:`, `working-directory:` and `defaults:` -- is exactly the subset the contract is
    about, and every finding carries the line it came from.
    """
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise Refusal("WORKFLOW-UNREADABLE", f"{rel}: {exc}") from exc

    workflow = Workflow(path=path, rel=rel)
    lines = text.splitlines()
    job: Job | None = None
    step: Step | None = None
    indent_with: int | None = None
    indent_defaults: int | None = None
    in_run = False
    in_jobs = False
    run_indent = 0

    for number, raw in enumerate(lines, start=1):
        line = strip_comment(raw)
        blank = not line.strip()
        if blank and in_run:
            # A comment or blank line INSIDE a run block belongs to the block, and this is not a
            # detail: clearing `in_run` here silently discarded the whole body of every run block
            # whose FIRST line was a comment, so such a block was scanned as empty and the tool
            # reported green having examined nothing. That is the precise blindness this tool
            # exists to remove, committed inside the tool that removes it.
            continue
        if blank:
            in_run = False
            continue
        indent = len(line) - len(line.lstrip())
        body = line.strip()

        # `defaults:` (job level at indent 4, workflow level at indent 2)
        if indent_defaults is not None and indent > indent_defaults and body.startswith("working-directory:"):
            value = clean_value(body.split(":", 1)[1])
            if job is not None:
                job.default_working_directory = value
            else:
                workflow.default_working_directory = value
            continue
        if body == "defaults:":
            indent_defaults = indent
            continue
        if indent_defaults is not None and indent <= indent_defaults:
            indent_defaults = None

        # A job header at indent 2 -- but ONLY under a top-level `jobs:` key. Without that gate the
        # `on:` block's own indent-2 keys (`push:`, `workflow_dispatch:`) are parsed as jobs named
        # after the workflow's triggers, and a tool that reports on a job called `push` is a tool
        # whose reader has stopped trusting it.
        if indent == 0 and body == "jobs:":
            in_jobs = True
            continue
        if in_jobs and indent == 2 and re.fullmatch(r"[A-Za-z0-9_-]+:", body):
            in_run = False
            name = body[:-1]
            job = Job(name=name, line=number)
            workflow.jobs.append(job)
            step = None
            indent_with = None
            continue
        if job is None:
            continue

        # A step at indent 6.
        if indent == 6 and body.startswith("- "):
            in_run = False
            rest = body[2:].strip()
            label = rest[5:].strip() if rest.startswith("name:") else rest
            step = Step(name=label or f"step@{number}", line=number, uses=None)
            job.steps.append(step)
            indent_with = None
            if rest.startswith("uses:"):
                step.uses = clean_value(rest.split(":", 1)[1])
            continue
        if step is None:
            # A job-level `uses:` is a reusable-workflow CALL, not a step.
            if indent == 4 and body.startswith("uses:"):
                job.uses = clean_value(body.split(":", 1)[1])
                job.uses_line = number
            continue

        # Step keys at indent 8.
        if indent == 8:
            in_run = False
            key = body.split(":", 1)[0].strip()
            value = body.split(":", 1)[1] if ":" in body else ""
            if key == "uses":
                step.uses = clean_value(value)
            elif key == "working-directory":
                step.working_directory = clean_value(value)
            elif key == "run":
                if value.strip() in ("|", ">", "|-", ">-", "|+", ">+"):
                    in_run = True
                    run_indent = indent
                else:
                    step.run.append((number, value.strip()))
            elif key == "with":
                indent_with = indent
            else:
                indent_with = None
            continue

        if in_run and indent > run_indent:
            step.run.append((number, line.strip()))
            continue
        in_run = False

        if indent_with is not None and indent == 10 and ":" in body:
            key, _, value = body.partition(":")
            step.inputs.append((key.strip(), clean_value(value), number))
            continue
        if indent_with is not None and indent <= indent_with:
            indent_with = None

    for job in workflow.jobs:
        for step in job.steps:
            if step.uses and re.match(r"^actions/checkout@", step.uses):
                path_value = "."
                repository = None
                for key, value, _ in step.inputs:
                    if key == "path":
                        path_value = value
                    elif key == "repository":
                        repository = value
                job.checkouts.append(Checkout(path=path_value, repository=repository,
                                              line=step.line))
    return workflow


def load_workflows(repo: Path, only: list[str] | None) -> list[Workflow]:
    directory = repo / ".github" / "workflows"
    if not directory.is_dir():
        if only:
            # The caller NAMED a workflow, so its absence is a failed assertion.
            raise Refusal("WORKFLOWS-MISSING",
                          f"{repo} has no .github/workflows directory, so "
                          f"{', '.join(only)} cannot be read")
        # The caller pointed at a repository that owns no workflows. That is a true statement
        # about the repository, not a failed assertion, and a workspace sweep has to be able to
        # pass over gk-data without pretending it was checked. It is reported, never silent:
        # `run_scan` records it so the summary names it.
        return []
    files = sorted(p for p in directory.iterdir()
                   if p.suffix in (".yml", ".yaml") and p.is_file())
    if only:
        wanted = {name if name.endswith((".yml", ".yaml")) else f"{name}.yml" for name in only}
        files = [p for p in files if p.name in wanted]
        if not files:
            raise Refusal("NO-WORKFLOWS", f"no workflow matched {sorted(wanted)} in {directory}")
    if not files:
        raise Refusal("NO-WORKFLOWS", f"{directory} holds no workflow files")
    return [read_workflow(p, p.relative_to(repo).as_posix()) for p in files]


# ------------------------------------------------------------------------------------------------
# Resolving a declared checkout to a local tree
# ------------------------------------------------------------------------------------------------

class Router:
    """Maps a workflow-relative path onto a LOCAL directory, for one job.

    THE OVERLAY, AND WHY IT IS NOT OPTIONAL
    ---------------------------------------
    On a runner, a checkout with no `path:` puts the repository AT the workspace root -- the
    workspace root and the repository tree are the SAME directory. On a developer's machine they
    are not: the workspace holds several repositories side by side and this one is a subdirectory
    of it. So `dotnet build FusionRpg.slnx` in a path-less checkout resolves against the
    workspace root on the runner and against this repository here, and a tool that uses one of
    those two for both is exactly the resolver this repository already shipped a bug in --
    correct in a clone, wrong in the workspace, and green in neither in a way nobody can see.

    So the mapping is stated rather than assumed:

      * a `working-directory:` is resolved against the WORKSPACE, which is GitHub's own rule and
        the reason gk-core's ci.yml can write `working-directory: gk-core` for a repository it
        checked out into a sibling;
      * a relative path whose FIRST segment is a declared checkout's `path:` routes into that
        checkout's tree -- the overlay, and the only place it is needed;
      * anything else is inside the tree the workspace root IS, which is this repository when the
        job checked out with no `path:`, and the bare workspace when it did not.

    The overlay is applied ONCE, at the workspace boundary. Below that boundary the rest of a
    path is ordinary joining inside one tree, so a sibling checkout can never capture a segment
    that happens to share a name with a file inside another repository.
    """

    def __init__(self, repo: Path, workspace: Path, checkouts: list[Checkout]) -> None:
        self.repo = repo
        self.workspace = workspace
        self.named: dict[str, bool] = {}  # checkout path -> is-this-repository
        self.implicit = False
        for checkout in checkouts:
            relative = checkout.path.replace("\\", "/").strip("/")
            if checkout.repository is None and relative in ("", "."):
                self.implicit = True
            elif relative not in ("", "."):
                self.named[relative] = checkout.repository is None

    def workspace_root(self) -> Path:
        """The tree the workspace root IS for this job."""
        return self.repo if self.implicit else self.workspace

    def named_tree(self, name: str) -> Path:
        """The local tree a NAMED checkout corresponds to.

        Another repository's checkout is `<workspace>/<name>`. This repository's own checkout into a
        named sibling is ambiguous locally -- that sibling may be a subdirectory of this
        repository, or this repository standing in for the sibling -- so whichever actually exists
        wins. Preferring a measurement over an assumption is the whole point: an assumption here
        reports a correct workflow broken on one machine and fine on another.
        """
        if not self.named[name]:
            return self.workspace / name
        own = self.repo / name
        sibling = self.workspace / name
        if own.is_dir():
            return own
        if sibling.is_dir():
            return sibling
        return own

    def resolve(self, relative: str | Path) -> Path:
        parts = Path(str(relative).replace("\\", "/")).parts
        if parts and parts[0] in self.named:
            rest = Path(*parts[1:]) if len(parts) > 1 else Path()
            return self.named_tree(parts[0]) / rest
        return self.workspace_root() / Path(*parts) if parts else self.workspace_root()

    def trees(self) -> list[Path]:
        """Every local tree this job's checkouts could correspond to."""
        found = [self.workspace_root()]
        for name in self.named:
            candidate = self.named_tree(name)
            if candidate not in found:
                found.append(candidate)
        return found


def checkout_declarations(workflow: Workflow, job: Job, trees: list[Path]) -> list[dict]:
    """Declaration errors on a job's `actions/checkout` steps, in the SAME shape as every other
    finding.

    The shape is not cosmetic: `--json` is the machine-readable surface a caller asserts on, and a
    report whose entries are sometimes objects and sometimes their `str()` is a report nothing can be
    written against.

    Two of these errors, both of which make every later reference in the job unanswerable rather than
    merely suspicious:

      * a `repository:` with no `path:` lands at the workspace root, where it is
        indistinguishable from this repository's own checkout, so a step naming a file there has
        no way to say which tree it meant;
      * a `path:` that does not equal the last segment of its `repository:` is a reference that
        silently resolves against the wrong tree, and no filesystem lookup can detect it.
    """
    problems: list[dict] = []
    for checkout in job.checkouts:
        if checkout.repository is None:
            continue
        if checkout.path.strip("/") in ("", "."):
            problems.append({
                "class": "CHECKOUT-NOT-NAMEABLE", "workflow": workflow.rel, "line": checkout.line,
                "job": job.name, "step": "actions/checkout",
                "path": f"repository: {checkout.repository} with no path:",
                "form": "checkout without path:",
                "searched": [str(tree) for tree in trees],
                "detail": ("a checkout with a `repository:` and no `path:` lands at the workspace "
                           "root, where it is indistinguishable from this repository's own checkout, "
                           "so no step can name a file inside it"),
            })
            continue
        leaf = checkout.repository.rstrip("/").split("/")[-1]
        if leaf != checkout.path.strip("/"):
            problems.append({
                "class": "CHECKOUT-PATH-NAME-MISMATCH", "workflow": workflow.rel,
                "line": checkout.line, "job": job.name, "step": "actions/checkout",
                "path": f"path: {checkout.path} for repository: {checkout.repository}",
                "form": "checkout path does not name its repository",
                "searched": [str(tree) for tree in trees],
                "detail": (f"this checkout lands in '{checkout.path}', so a reference that means "
                           f"'{leaf}' would silently resolve against the wrong tree, and no "
                           f"filesystem lookup can tell the two apart"),
            })
    return problems


def step_base(router: Router, job: Job, step: Step) -> tuple[Path, bool]:
    """Where a step's relative paths resolve, and whether that is the WORKSPACE boundary.

    See `Router` for the overlay rule. The flag is what tells the caller that a reference in
    this step still has to go through the overlay: a step with no `working-directory:` runs at
    the workspace root, so its paths are workspace-relative and a declared sibling checkout may
    own their first segment. Once a `working-directory:` has been applied the step is inside one
    tree, and everything below it is ordinary joining -- so a sibling must never capture a
    segment that merely shares a name with a file in another repository.

    `working-directory:` APPLIES ONLY TO A `run:` STEP. A step that `uses:` an action runs no
    shell, so there is no working directory for it to run in and its inputs resolve against
    `github.workspace`. Ignoring that made this tool report both gk-core's and gk-web's
    `cache-dependency-path: gk-web/web/fusion-rpg-web/package-lock.json` as broken: each job
    declares a job-level `working-directory:`, and joining the input under it produced
    `<workspace>/gk-core/gk-web/...` and `<workspace>/gk-web/web/gk-web/...`, neither of which
    exists -- while both workflows are correct.
    """
    declared = step.working_directory or job.default_working_directory
    if not step.run or declared is None:
        return router.workspace_root(), True
    relative = Path(declared.replace("\\", "/"))
    if relative.is_absolute():
        return relative, False
    return router.resolve(relative), False


# ------------------------------------------------------------------------------------------------
# The closed vocabulary
# ------------------------------------------------------------------------------------------------

def is_path_like(token: str) -> bool:
    if not token or token.startswith("-"):
        return False
    if "$" in token or "{{" in token or "%" in token:
        return False
    if token in (".", ".."):
        return False
    if any(sep in token for sep in ("/", "\\")):
        return True
    return token.lower().endswith(PATH_SUFFIXES)


def absolute(token: str) -> bool:
    return bool(re.match(r"^[A-Za-z]:[\\/]", token) or token.startswith("\\\\")
                or token.startswith("/"))


def references_in_run(job: Job, step: Step, name: str) -> list[Reference]:
    """Every path the run body names, per the closed vocabulary in the module docstring."""
    found: list[Reference] = []

    def add(token: str, line: int, form: str, kind: str = "reference") -> None:
        token = unquote(token)
        if not is_path_like(token) or absolute(token):
            return
        found.append(Reference(path=token.replace("\\", "/"), line=line, job=job.name,
                               step=name, form=form, kind=kind))

    def first_script(args: list[str]) -> str | None:
        """The SCRIPT an interpreter runs -- the FIRST non-option argument, and no more.

        Only the first. Every argument after it belongs to the script, not to this tool: reading
        `python scripts/smoke_player_pack.py --pack-dir dist/FusionRpg` as a reference to
        `dist/FusionRpg` is how a scan starts reporting the release's own output directory as a
        missing input, and a red that cries wolf trains its reader to skip the row that matters.
        """
        for token in args:
            if token.startswith("-"):
                continue
            return token
        return None

    for number, text in step.run:
        # Split off a redirect target FIRST. Nothing after `>` is a command argument, so leaving
        # it in the token list let the python rule pick up the redirect's path as a script.
        redirect = REDIRECT.search(text)
        redirect_target = unquote(redirect.group(1)) if redirect else None
        if redirect:
            text = text[:redirect.start()]

        for segment in SEGMENT_SPLIT.split(text):
            tokens = [unquote(t) for t in segment.strip().split()]
            if not tokens:
                continue
            command = tokens[0].strip()
            args = tokens[1:]
            base = Path(command).name.lower()

            if DOT_COMMAND.match(command):
                add(command, number, "./<script> as a command word")
                continue

            if base in ("python", "python3", "py"):
                if "-c" in args:
                    continue  # an inline code string is not a path
                if "-m" in args:
                    module_index = args.index("-m") + 1
                    module = args[module_index] if module_index < len(args) else ""
                    rest = args[module_index + 1:]
                    # `-m pip` is the one module whose own arguments are paths.
                    if Path(module).name.lower() == "pip":
                        for index, token in enumerate(rest):
                            if token in ("-r", "-e", "--requirement", "--editable") \
                                    and index + 1 < len(rest):
                                add(rest[index + 1], number, f"pip {token} <target>")
                    continue  # any other module name is not a path
                script = first_script(args)
                if script:
                    add(script, number, f"{command} <script>")
                continue

            if base == "node":
                script = first_script(args)
                if script:
                    add(script, number, "node <script>")
                continue

            if base in ("bash", "sh"):
                script = first_script(args)
                if script:
                    add(script, number, f"{base} <script>")
                continue

            if base in ("pwsh", "powershell"):
                for index, token in enumerate(args):
                    if token in ("-File", "-file") and index + 1 < len(args):
                        add(args[index + 1], number, f"{command} -File <script>")
                continue

            if base == "pip":
                for index, token in enumerate(args):
                    if token in ("-r", "-e", "--requirement", "--editable") and index + 1 < len(args):
                        add(args[index + 1], number, f"pip {token} <target>")
                continue

            if base == "npm":
                for index, token in enumerate(args):
                    if token == "--prefix" and index + 1 < len(args):
                        add(args[index + 1], number, "npm --prefix <dir>")
                continue

            if base == "dotnet":
                for index, token in enumerate(args):
                    if token in ("test", "build", "publish"):
                        project = first_script(args[index + 1:])
                        if project:
                            add(project, number, f"dotnet {token} <project>")
                    elif token == "--project" and index + 1 < len(args):
                        add(args[index + 1], number, "dotnet run --project <project>")
                continue

        if redirect_target and "$" not in redirect_target:
            add(redirect_target, number, "pwsh `>` redirect target", kind="redirect")
    return found


def references_in_inputs(job: Job, step: Step, name: str) -> tuple[list[Reference], list[str]]:
    found: list[Reference] = []
    unknown: list[str] = []
    # A checkout's own `path:` is where it PUTS a tree, not something it reads. Scanning it would
    # report every cross-repository checkout as a missing input, which is the opposite of the
    # finding: declaring a sibling correctly is how a reference resolves in the first place.
    declaring_tree = bool(step.uses and re.match(r"^actions/checkout@", step.uses))
    for key, value, number in step.inputs:
        if key == "path" and declaring_tree:
            continue
        if key in PATH_INPUTS:
            token = value
            if "$" in token or not token:
                continue
            if absolute(token):
                continue
            found.append(Reference(path=token.replace("\\", "/"), line=number, job=job.name,
                                   step=name, form=f"with: {key}"))
        elif key not in ("uses", "name", "token", "if", "shell", "run", "continue-on-error",
                         "id", "env", "with", "repository", "path", "cache"):
            unknown.append(key)
    return found, unknown


# ------------------------------------------------------------------------------------------------
# The check
# ------------------------------------------------------------------------------------------------

def created_dirs(job: Job) -> set[str]:
    """Directories a job's own steps create. A redirect target under one of these is fine."""
    made: set[str] = set()
    for step in job.steps:
        for _, text in step.run:
            lowered = text.lower()
            if not any(command in lowered for command in WRITE_CMDS):
                continue
            for token in re.findall(r"[A-Za-z0-9_./\\-]+", text):
                token = unquote(token).replace("\\", "/")
                if "/" in token:
                    made.add(token.rsplit("/", 1)[0])
    return made


def run_scan(repo: Path, workspace: Path, only: list[str] | None) -> tuple[list[dict], dict]:
    findings: list[dict] = []
    stats = {"workflows": 0, "jobs": 0, "steps": 0, "references": 0, "redirects": 0,
             "declarations": 0, "unexaminedInputs": set(), "noWorkflows": False}
    workflows = load_workflows(repo, only)
    if not workflows:
        # Named above rather than swallowed: a sweep must be able to say which repository
        # contributed nothing, so "no workflows" never reads as "checked, all clear".
        stats["noWorkflows"] = True
        return findings, stats
    for workflow in load_workflows(repo, only):
        stats["workflows"] += 1
        for job in workflow.jobs:
            stats["jobs"] += 1
            router = Router(repo, workspace, job.checkouts)
            trees = router.trees()
            declarations = checkout_declarations(workflow, job, trees)
            stats["declarations"] += len(declarations)
            findings.extend(declarations)

            # A job-level `uses: ./...` is a local reusable-workflow CALL. GitHub reads it from
            # the calling repository itself and does not need a checkout for it, so it resolves
            # against the repository root and never against the workspace.
            if job.uses and job.uses.startswith("./"):
                # `lstrip("./")` is a CHARACTER-SET strip, so it turns `./.github/x` into
                # `github/x` and the lookup fails on a workflow that is perfectly valid.
                local = (repo / job.uses[2:]).resolve()
                if not local.is_file():
                    findings.append({
                        "class": "PATH-NOT-IN-DECLARED-CHECKOUT", "workflow": workflow.rel,
                        "line": job.uses_line, "job": job.name, "step": job.name,
                        "path": job.uses, "form": "job-level local reusable workflow",
                        "searched": [str(local)],
                        "detail": ("a job-level `uses: ./...` is read from this repository's own "
                                   "root, and the file is not there"),
                    })

            made = created_dirs(job)
            for step in job.steps:
                stats["steps"] += 1
                base, at_workspace_root = step_base(router, job, step)
                references = references_in_run(job, step, step.name)
                from_inputs, unknown = references_in_inputs(job, step, step.name)
                stats["unexaminedInputs"].update(unknown)
                references.extend(from_inputs)
                for reference in references:
                    target = Path(reference.path.replace("\\", "/"))
                    # Resolve against the step's BASE first, then require the result to sit inside
                    # a tree a checkout declared. Joining the reference onto every checkout
                    # instead -- the obvious simplification -- is wrong in both directions: it
                    # lets `gk-web/x` resolve inside the gk-core tree when the job never checked
                    # gk-web out, and it makes a correct base-relative reference look broken.
                    resolved = (target if target.is_absolute()
                                else router.resolve(target) if at_workspace_root
                                else base / target)
                        # Lexical, never `resolve()`: `resolve()` follows symlinks and would let a
                        # reference escape the tree it was checked against, which is the one thing
                        # this tool exists to refuse. `normpath` collapses `..` so a reference that
                        # legitimately reaches a sibling (`--root ../gk-fusion`) compares equal to
                        # the tree it names, which a raw `Path` comparison would not.
                    resolved = Path(os.path.normpath(resolved))
                    if reference.kind == "redirect":
                        stats["redirects"] += 1
                        parent = str(Path(reference.path).parent).replace("\\", "/")
                        if parent in (".", ""):
                            continue
                        if any(parent == m or parent.startswith(m.rstrip("/") + "/") for m in made):
                            continue
                        if any((tree / parent).is_dir() for tree in trees):
                            continue
                        findings.append({
                            "class": "REDIRECT-PARENT-UNDECLARED",
                            "workflow": workflow.rel, "line": reference.line, "job": job.name,
                            "step": reference.step, "path": reference.path,
                            "form": reference.form,
                            "searched": [str(tree / parent) for tree in trees],
                            "detail": (f"the parent directory '{parent}' is in no checkout this job "
                                       f"declared, and no step in the job creates it; a pwsh "
                                       f"redirect to a missing parent raises, does NOT run the "
                                       f"command, and leaves $LASTEXITCODE at 0, so the step "
                                       f"reports success having run nothing"),
                        })
                        continue
                    stats["references"] += 1
                    inside = any(resolved == tree or resolved.is_relative_to(tree)
                                 for tree in trees)
                    if inside and resolved.exists():
                        continue
                    findings.append({
                        "class": "PATH-NOT-IN-DECLARED-CHECKOUT",
                        "workflow": workflow.rel, "line": reference.line, "job": job.name,
                        "step": reference.step, "path": reference.path, "form": reference.form,
                        "searched": [str(resolved)],
                        "detail": (f"resolves against the step's base '{base}' to '{resolved}', which "
                                   f"is {'inside' if inside else 'OUTSIDE'} every tree this job's "
                                   f"checkout steps declare "
                                   f"({', '.join(str(t) for t in trees) or 'NONE'})"
                                   f"{'' if inside else ', and does not exist there'}"),
                    })
    return findings, stats


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Every path a workflow names must resolve inside a checkout that job declared.")
    parser.add_argument("--repo", type=Path, default=None,
                        help="the repository whose workflows are scanned (default: this tool's repository)")
    parser.add_argument("--workspace", type=Path, default=None,
                        help="the directory that plays the part of github.workspace "
                             "(default: this repository's parent, so sibling checkouts resolve)")
    parser.add_argument("--workflow", action="append", default=None,
                        help="scan only these workflow files (repeatable)")
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    parser.add_argument("--quiet", action="store_true", help="print findings only")
    args = parser.parse_args(argv)

    here = Path(__file__).resolve()
    repo = (args.repo or here.parent.parent).resolve()
    workspace = (args.workspace or repo.parent).resolve()
    if not repo.is_dir():
        print(f"{TOOL_ID} REFUSED [REPO-MISSING]: {repo} is not a directory", file=sys.stderr)
        return EXIT_REFUSED
    # Whether the repository owns workflows is `load_workflows`' question, not this one's, and the
    # two answers have to stay different: a path that does not exist is a refusal, while a
    # repository with no `.github/workflows` is a measurement.

    try:
        findings, stats = run_scan(repo, workspace, args.workflow)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        else:
            print(f"{TOOL_ID} REFUSED [{refusal.reason}]: {refusal.detail}", file=sys.stderr)
        return EXIT_REFUSED

    unexamined = sorted(stats["unexaminedInputs"])
    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "RED" if findings else "OK",
                          "repository": str(repo), "workspace": str(workspace),
                          "noWorkflows": stats["noWorkflows"],
                          "vocabulary": list(VERBATIM),
                          "notExamined": unexamined,
                          "counts": {k: (len(v) if isinstance(v, set) else v)
                                     for k, v in stats.items()},
                          "findings": findings},
                         indent=2, default=str))
        return EXIT_FINDINGS if findings else EXIT_OK

    if not args.quiet:
        if stats["noWorkflows"]:
            print(f"workflow-paths: {repo} owns no .github/workflows, so it has nothing to check. "
                  f"That is a measurement, not a pass over anything.")
            return EXIT_OK
        print(f"workflow-paths: {stats['workflows']} workflow(s), {stats['jobs']} job(s), "
              f"{stats['steps']} step(s), {stats['references']} path reference(s) examined, "
              f"{stats['redirects']} redirect target(s)")
        print(f"  repository  : {repo}")
        print(f"  workspace   : {workspace}")
        print(f"  vocabulary  : {', '.join(VERBATIM)}")
        print("  not examined: output paths a step creates, and filesystem cmdlets as path readers "
              "(a read-back of an earlier step's output is not a missing input)")
        print(f"  unexamined `with:` inputs, by name: {', '.join(unexamined) or 'none'}")
    if findings:
        print()
        width = max(len(f["workflow"]) for f in findings)
        for finding in findings:
            print(f"  {finding['workflow']:<{width}}:{finding['line']}  [{finding['class']}]")
            print(f"      job   : {finding['job']}")
            print(f"      step  : {finding['step']}")
            print(f"      path  : {finding['path']}   ({finding['form']})")
            print(f"      looked for: {', '.join(finding['searched'])}")
            print(f"      {finding['detail']}")
            print()
        classes: dict[str, int] = {}
        for finding in findings:
            classes[finding["class"]] = classes.get(finding["class"], 0) + 1
        print(f"workflow-paths RED: {len(findings)} finding(s) -- "
              + ", ".join(f"{name} x{count}" for name, count in sorted(classes.items()))
              + ". A workflow that names a path which is not there is not a workflow.")
        return EXIT_FINDINGS
    if not args.quiet:
        print("workflow-paths OK: every path this repository's workflows name resolves inside a "
              "checkout the declaring job created.")
    return EXIT_OK


if __name__ == "__main__":
    raise SystemExit(main())
