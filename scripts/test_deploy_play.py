#!/usr/bin/env python3
"""Deterministic tests for gk-fusion/scripts/deploy-play.py — no game, no network, no real build.

    python gk-fusion/scripts/test_deploy_play.py          # or: python -m unittest discover -s scripts

Every case drives the REAL tool with its external effects stubbed (the child-process runner,
the robocopy mirror, the /health probe, the game/server launch). What is under test is the
tool's own contract: that it refuses the configurations it must refuse, in the order it must
refuse them, and that it never touches an install it was told not to.

The stage ORDER is the contract that two separate defects came from, so it is asserted
directly (cfg before server start, deploy complete before a server owns the DLLs) rather
than inferred from behaviour.

A RESOLVED TOOL is the other contract, and it arrived later: nothing asserted that a tool the
dispatcher resolves is a tool that exists, or that the arguments addressed to it are accepted by it.
Two ports shipped under that gap and left `deploy-play.py --full-suite` and `--verify --paths` dead,
green here the whole time. `TestToolDispatcher` is the class that closes it; its own docstring carries
the two measured failures, so nobody has to rediscover them to believe it.

The temp substrate rule from docs/contributing/testing-standard.md applies: everything below
runs under tempfile.mkdtemp and is removed in tearDown; a failed delete fails the test.
"""

from __future__ import annotations

import argparse
import ast
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
DEPLOY = SCRIPTS / "deploy-play.py"

# A `--help` probe of a tool that takes real arguments. Bounded because an unbounded
# probe of a wedged tool is the defect this program exists to remove.
HELP_TIMEOUT = 120


def _load():
    spec = importlib.util.spec_from_file_location("deploy_play", DEPLOY)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    # @dataclass resolves its own module through sys.modules, so register BEFORE exec.
    sys.modules["deploy_play"] = module
    spec.loader.exec_module(module)
    return module


dp = _load()

OWNER = dp.OWNER_SERVER_URL
SLOT_URL = "http://127.0.0.1:5102"


def make_args(**over) -> argparse.Namespace:
    """A complete argparse.Namespace shaped exactly like the real parser's output."""
    base = dict(
        loader_host="MelonLoader", game_dir=None, game_profile="pvzrh-3.9",
        server_url=SLOT_URL, no_game=True, no_server=True, no_rebuild_ui=True,
        restart_server=False, reuse_build=False, skip_freshness=False,
        verify=False, paths=[], full_suite=False, session="test-session", dry_run=False,
    )
    base.update(over)
    return argparse.Namespace(**base)


class TempTreeMixin(unittest.TestCase):
    """A disposable repo-shaped tree. A failed temp delete is a FAILURE, never swallowed."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="deploy-play-test-"))
        self.addCleanup(self._rmtree, self.tmp)
        self.repo = self.tmp / "repo"
        self.pool = self.tmp / "pool"
        self.slot = self.pool / "slot-1"
        self.outside = self.tmp / "not-the-pool"
        for p in (self.repo, self.slot, self.outside):
            p.mkdir(parents=True)
        (self.slot / "PlantsVsZombiesRH.exe").write_bytes(b"MZ")
        (self.outside / "PlantsVsZombiesRH.exe").write_bytes(b"MZ")
        self.calls: list[list[str]] = []

    def _rmtree(self, path: Path) -> None:
        # A temp delete that fails is a defect, not noise (the 65.5 GB leak standard). No `try`:
        # an exception here FAILS the test, which is the point of the rule.
        if path.exists():
            shutil.rmtree(path)
        self.assertFalse(path.exists(), f"temp dir survived teardown: {path}")

    # -- stubs -------------------------------------------------------------------------

    def fake_run(self, *, create_artifacts: bool = True, fail_stage: str | None = None):
        """Stand in for the child-process runner; records argv and fabricates build output.

        Artifacts are created only when ABSENT, so a test that pre-seeds a deliberately stale
        DLL keeps its timestamp. (`Path.touch` would REFRESH an existing file's mtime, which
        silently makes a stale artifact fresh — the exact condition under test.)
        """

        def _fabricate(directory: Path, names: tuple[str, ...]) -> None:
            directory.mkdir(parents=True, exist_ok=True)
            for name in names:
                target = directory / name
                if not target.exists():
                    target.write_bytes(b"artifact")

        def _run(args, *, stage, timeout, log, cwd=None, env=None, check=True):
            self.calls.append(list(args))
            if fail_stage == stage:
                raise dp.Refusal(stage, f"stubbed failure in {stage}", "detail")
            if create_artifacts and stage == "injector_build":
                _fabricate(self.slot / "Mods",
                           (dp.MELON39_INJECTOR_DLL, "FusionRpg.Core.dll"))
            if create_artifacts and stage == "server_publish":
                _fabricate(self.repo / "dist" / "FusionRpg.Server", ("FusionRpg.Server.exe",))
            return subprocess.CompletedProcess(args, 0, "", "")

        return _run

    def staged(self, *, http_ok=None, **kw):
        """Patches every external effect; returns the fake `run` recorder.

        Every external boundary is stubbed — the child-process runner, the robocopy mirror, the
        /health probe, the game-liveness scan and the server/game launch. A stub that returns a
        bare MagicMock would read as "yes, something is running", so each is given a real answer.
        """
        return mock.patch.multiple(
            dp,
            REPO_ROOT=self.repo, SCRIPTS=self.repo / "scripts",
            run=self.fake_run(**kw),
            http_ok=http_ok or (lambda *a, **k: False),
            _mirror=mock.DEFAULT,
            _stop_listener=lambda url, log: None,
            _game_running_from=lambda install: None,
        )

    def mirror_stub(self, source: Path, destination: Path, *, stage: str, log) -> None:
        destination.mkdir(parents=True, exist_ok=True)
        (destination / "index.html").write_text("<html>ok</html>", encoding="utf-8")

    def config(self, **over):
        """resolve_config with the machine-local env this test owns (never the real one)."""
        args = make_args(**over)
        env = {"FUSIONRPG_GAME_POOL": str(self.pool)} if over.pop("pooled", True) else {}
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub), \
                mock.patch.dict(os.environ, env, clear=False):
            for name in ("FUSIONRPG_ML_GAMEDIR", "FUSIONRPG_GAME_DIR", "FUSIONRPG_GAME_PROFILE"):
                os.environ.pop(name, None)
            return dp.resolve_config(args, dp.Log(None))


# --------------------------------------------------------------------------------------
# 1-3. Preflight refusals. All reported together, exit 2, and NOTHING is touched.
# --------------------------------------------------------------------------------------
class TestPreflightRefusals(TempTreeMixin):

    def test_pooled_run_at_owner_url_is_refused(self):
        """A pooled run pointed at the owner's :5088 is the SSH4.9 incident class."""
        cfg = self.config(game_dir=str(self.slot), server_url=OWNER, no_server=True)
        self.assertTrue(cfg.problems, "a pooled run at the owner url must be refused")
        self.assertTrue(any("FUSIONRPG_GAME_POOL" in p for p in cfg.problems), cfg.problems)
        self.assertEqual(self.calls, [], "preflight must run nothing")

    def test_pooled_run_targeting_outside_the_pool_is_refused(self):
        cfg = self.config(game_dir=str(self.outside), server_url=SLOT_URL, no_server=True)
        self.assertTrue(any("a pooled run must deploy INTO the pool" in p for p in cfg.problems),
                        cfg.problems)

    def test_missing_game_exe_is_refused_before_any_build(self):
        cfg = self.config(game_dir=str(self.tmp / "nowhere"), no_server=True)
        self.assertTrue(any("no game exe" in p for p in cfg.problems), cfg.problems)
        self.assertEqual(self.calls, [], "no build may run when preflight failed")

    def test_all_problems_are_reported_together_not_one_at_a_time(self):
        """The contract is 'every problem listed at once' — a fix-and-rerun loop is the cost
        this replaces."""
        cfg = self.config(game_dir=str(self.tmp / "nowhere"), server_url="http://10.0.0.5:1",
                          no_server=False, verify=True)
        self.assertGreaterEqual(len(cfg.problems), 3, cfg.problems)

    def test_main_returns_2_and_touches_nothing(self):
        """exit 2 is the contract; the install and dist must not exist afterwards."""
        argv = ["--game-dir", str(self.tmp / "nowhere"), "--no-server", "--no-game"]
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub), \
                mock.patch.dict(os.environ, {"FUSIONRPG_GAME_POOL": str(self.pool)}), \
                mock.patch.dict(os.environ, {"TEMP": str(self.tmp)}):
            code = dp.main(argv)
        self.assertEqual(code, 2)
        self.assertEqual(self.calls, [])
        self.assertFalse((self.repo / "dist").exists(), "refused deploy created dist/")
        self.assertFalse((self.tmp / "nowhere").exists(), "refused deploy created the install")


# --------------------------------------------------------------------------------------
# 4. The cfg write: ALWAYS, and naming THIS deploy's server (the -NoGame regression).
# --------------------------------------------------------------------------------------
class TestCfgWrite(TempTreeMixin):

    def test_cfg_is_written_under_no_game_and_names_the_slot_url(self):
        """The retired PowerShell wrote the cfg inside `if (-not $NoGame)`, so every pooled
        deploy — which MUST pass -NoGame — left the slot pointed at the owner's server."""
        cfg = self.config(game_dir=str(self.slot), server_url=SLOT_URL,
                          no_game=True, no_server=True)
        self.assertEqual(cfg.problems, [], cfg.problems)
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        cfg_file = self.slot / "Mods" / "fusionrpg.cfg"
        self.assertTrue(cfg_file.exists(), "cfg must be written even with --no-game")
        self.assertIn(f"ServerUrl={SLOT_URL}", cfg_file.read_text(encoding="utf-8"))
        self.assertTrue(verdict["ok"], verdict["refusals"])

    def test_cfg_stage_precedes_server_start(self):
        cfg = self.config(game_dir=str(self.slot))
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        stages = verdict["stages"]
        self.assertIn("injector cfg (fusionrpg.cfg)", stages)
        self.assertIn("server start", stages)
        self.assertLess(stages.index("injector cfg (fusionrpg.cfg)"), stages.index("server start"))

    def test_cfg_naming_a_different_server_is_refused(self):
        """The verification is the point: a written cfg that names the wrong server is a refusal."""
        cfg = self.config(game_dir=str(self.slot))
        plugins = self.slot / "Mods"
        plugins.mkdir(parents=True, exist_ok=True)
        real_write = Path.write_text

        def sabotage(self_path, data, **kw):
            real_write(self_path, data, **kw)
            if self_path.name == "fusionrpg.cfg":
                real_write(self_path, data.replace(SLOT_URL, OWNER), **kw)
            return len(data)

        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub), \
                mock.patch.object(Path, "write_text", sabotage):
            verdict = dp.deploy(cfg)
        self.assertFalse(verdict["ok"], "a cfg naming the wrong server must not report ok")
        self.assertEqual(verdict["refusals"][0]["stage"], "cfg")


# --------------------------------------------------------------------------------------
# 5. Order: the deploy finishes before a server is started, so the server never owns a
#    half-written install. (Eight bogus `no such column` errors came from the reverse.)
# --------------------------------------------------------------------------------------
class TestStageOrder(TempTreeMixin):

    @staticmethod
    def index_of(stages: list[str], prefix: str) -> int:
        for i, name in enumerate(stages):
            if name == prefix or name.startswith(prefix):
                return i
        raise AssertionError(f"stage {prefix!r} never ran; stages were {stages}")

    def test_the_deploy_finishes_before_a_server_owns_the_install(self):
        """Order is the contract two defects came from. A server that starts before the
        publish/seed/cfg stages ship eight bogus `no such column` errors at the client."""
        cfg = self.config(game_dir=str(self.slot), no_game=False, no_server=False)
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub), \
                mock.patch.object(subprocess, "Popen", mock.Mock()) as popen:
            verdict = dp.deploy(cfg)
        self.assertTrue(verdict["ok"], verdict["refusals"])
        stages = verdict["stages"]
        start = self.index_of(stages, "server start")
        for before in ("injector build", "server publish", "seed import", "injector cfg"):
            self.assertLess(self.index_of(stages, before), start, before)
        self.assertEqual(stages[-1], "test scope", "the verdict must be the last stage")
        # server process + game process, and the server is the FIRST of the two
        self.assertEqual(popen.call_count, 2)
        launched = [Path(c.args[0][0]).name for c in popen.call_args_list]
        self.assertEqual(launched, ["FusionRpg.Server.exe", "PlantsVsZombiesRH.exe"], launched)


# --------------------------------------------------------------------------------------
# 6. A slow stage is KILLED and refused by name — never a hang, never a silent success.
# --------------------------------------------------------------------------------------
class TestStageBudget(TempTreeMixin):

    def test_slow_child_exceeds_its_budget_and_is_refused_by_name(self):
        started = time.monotonic()
        with self.assertRaises(dp.Refusal) as caught:
            dp.run([sys.executable, "-c", "import time; time.sleep(30)"],
                   stage="server_publish", timeout=1.0, log=dp.Log(None))
        elapsed = time.monotonic() - started
        self.assertEqual(caught.exception.stage, "server_publish")
        self.assertIn("budget", caught.exception.reason)
        self.assertLess(elapsed, 20, f"the budget was not enforced (took {elapsed:.1f}s)")

    def test_budget_refusal_propagates_out_of_deploy_as_a_named_refusal(self):
        cfg = self.config(game_dir=str(self.slot))
        with self.staged(fail_stage="server_publish"), \
                mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        self.assertFalse(verdict["ok"])
        self.assertEqual(verdict["refusals"][0]["stage"], "server_publish")


# --------------------------------------------------------------------------------------
# 7. "Build succeeded" is not evidence the DLL was rebuilt. A stale artifact is a refusal.
# --------------------------------------------------------------------------------------
class TestFreshness(TempTreeMixin):

    def _repo_with_sources(self, dll_age: int, cs_age: int):
        src = self.repo / "src" / "FusionRpg.Injector"
        src.mkdir(parents=True)
        cs = src / "Thing.cs"
        cs.write_text("// source", encoding="utf-8")
        now = time.time()
        os.utime(cs, (now - cs_age, now - cs_age))
        plugins = self.slot / "Mods"
        plugins.mkdir(parents=True, exist_ok=True)
        for dll in (dp.MELON39_INJECTOR_DLL, "FusionRpg.Core.dll"):
            target = plugins / dll
            target.write_bytes(b"dll")
            os.utime(target, (now - dll_age, now - dll_age))
        return plugins

    def test_stale_dll_against_newer_source_is_refused(self):
        self._repo_with_sources(dll_age=10_000, cs_age=0)
        cfg = self.config(game_dir=str(self.slot), skip_freshness=False)
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        self.assertFalse(verdict["ok"], "a stale DLL must not report ok")
        refusal = verdict["refusals"][0]
        self.assertEqual(refusal["stage"], "freshness")
        self.assertIn("STALE DEPLOY", refusal["reason"])
        self.assertIn(dp.MELON39_INJECTOR_DLL, refusal["reason"])

    def test_fresh_dll_passes(self):
        self._repo_with_sources(dll_age=0, cs_age=10_000)
        cfg = self.config(game_dir=str(self.slot), skip_freshness=False)
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        self.assertTrue(verdict["ok"], verdict["refusals"])

    def test_skip_freshness_actually_skips(self):
        self._repo_with_sources(dll_age=10_000, cs_age=0)
        cfg = self.config(game_dir=str(self.slot), skip_freshness=True)
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        self.assertTrue(verdict["ok"], "a pooled deploy of an unchanged build must not be blocked")


# --------------------------------------------------------------------------------------
# 8-9. The verdict contract.
# --------------------------------------------------------------------------------------
class TestVerdictContract(TempTreeMixin):

    def _snapshot(self, root: Path) -> dict[str, float]:
        return {str(p): p.stat().st_mtime_ns
                for p in sorted(root.rglob("*")) if p.is_file()}

    def test_dry_run_changes_no_file_mtime(self):
        cfg = self.config(game_dir=str(self.slot), dry_run=True)
        before = self._snapshot(self.tmp)
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        after = self._snapshot(self.tmp)
        self.assertEqual(before, after, "--dry-run modified the filesystem")
        self.assertEqual(self.calls, [], "--dry-run ran a child process")
        self.assertTrue(verdict["dryRun"])
        self.assertTrue(verdict["ok"])

    def test_json_verdict_shape_and_stage_seconds(self):
        cfg = self.config(game_dir=str(self.slot))
        with self.staged(), mock.patch.object(dp, "_mirror", self.mirror_stub):
            verdict = dp.deploy(cfg)
        for key in ("stages", "stageSeconds", "refusals", "ok", "injector", "server", "ui"):
            self.assertIn(key, verdict, f"verdict is missing {key}")
        # it must survive a JSON round-trip: a --json caller is another process
        self.assertEqual(json.loads(json.dumps(verdict))["ok"], True)
        self.assertEqual(set(verdict["stageSeconds"]), set(verdict["stages"]),
                         "every stage must be timed")
        for stage, seconds in verdict["stageSeconds"].items():
            self.assertIsInstance(seconds, (int, float), stage)
            self.assertGreaterEqual(seconds, 0.0, stage)

    def test_verify_without_a_scope_is_refused(self):
        cfg = self.config(verify=True)
        self.assertTrue(any("--verify needs" in p for p in cfg.problems), cfg.problems)

    def test_paths_without_verify_is_refused(self):
        """The deploy/verification split is mechanical, not advisory."""
        cfg = self.config(paths=["src/FusionRpg.Core/Foo.cs"])
        self.assertTrue(any("require --verify" in p for p in cfg.problems), cfg.problems)

    def test_non_loopback_server_url_is_refused(self):
        cfg = self.config(server_url="http://10.0.0.5:5088")
        self.assertTrue(any("loopback" in p for p in cfg.problems), cfg.problems)


class TestNoGuardSuite(unittest.TestCase):
    """The deploy's own contract with the guard runner (mirrors AssertDeployRunsNoGuardSuite)."""

    def test_deploy_invokes_the_runner_only_for_one_named_precondition(self):
        source = DEPLOY.read_text(encoding="utf-8")
        code = "\n".join(
            line for line in source.splitlines()
            if not line.lstrip().startswith("#")
        )
        code = code.split('"""', 2)[-1] if code.count('"""') >= 2 else code
        self.assertNotIn("-IncludeBacklog", code,
                         "a deploy must never run the widest guard tier")
        # BOTH spellings, and the reason is not symmetry. `--include-backlog` is the ported runner's own
        # flag, so it is the one that can actually appear; `-IncludeBacklog` is the retired PowerShell
        # spelling, which no longer exists to be written. Asserting only the retired one is a vacuous
        # check -- it passes for every deploy, including one that runs the widest tier.
        self.assertNotIn("--include-backlog", code,
                         "a deploy must never run the widest guard tier")
        for banned in ("test_fast.py --all-default", "test-fast.ps1", "guard-dal", "guard-actor-hub",
                       "guard-power", "guard-funnel-delta"):
            self.assertNotIn(banned, code, f"deploy references a guard: {banned}")


def _bind_lists(tree: ast.Module) -> dict[str, list[str]]:
    """Every simple list binding in the file, in SOURCE ORDER, so `x = [...]` then `x += [...]`
    composes and a rebinding replaces.

    Walked module-wide and sorted by line number rather than per function, because the bindings that
    matter are inside `main()` -- `verify_py_args = ["--paths", *paths]` lives in a function body, and a
    module-level walk read that call site as "no dialect passed". That is the THIRD time this helper has
    been probe-blind on a shape rather than a bug, which is the point worth recording: a probe that
    cannot see a shape must fail its case, and the safe direction is `None`, not a pass.

    Not resolved: comprehension-built lists, lists passed through a call, and the same name bound
    differently in two different functions. Those report `None` and therefore FAIL the dialect case,
    which is the direction a probe must fail in.
    """
    bindings: dict[str, list[str]] = {}
    statements = sorted(
        (node for node in ast.walk(tree)
         if isinstance(node, (ast.Assign, ast.AugAssign))),
        key=lambda node: (node.lineno, node.col_offset),
    )
    for node in statements:
        if isinstance(node, ast.Assign):
            if len(node.targets) != 1 or not isinstance(node.targets[0], ast.Name):
                continue
            targets, value, augmented = [node.targets[0]], node.value, False
        else:
            targets, value, augmented = [node.target], node.value, True
        if not (isinstance(targets[0], ast.Name) and isinstance(value, ast.List)):
            continue
        elements = [
            element.value if (isinstance(element, ast.Constant)
                              and isinstance(element.value, str)) else "<interpolated>"
            for element in value.elts
        ]
        name = targets[0].id
        if augmented and name in bindings:
            bindings[name] = bindings[name] + elements
        else:
            bindings[name] = elements
    return bindings


def _tool_argv_calls() -> list[tuple[str, list[str] | None, list[str] | None]]:
    """Every `tool_argv(...)` call in the tool, as (stem, tool_args, py_args), read from the AST.

    AST rather than a regex over the text: a regex cannot tell the `tool_args` list from the `py_args`
    one, nor a keyword argument from a positional string, and both distinctions ARE the contract here.

    An interpolated or starred ELEMENT becomes `<interpolated>` rather than discarding the list. The
    flags are static and the flags are the contract; `--local-arg`'s value is necessarily interpolated
    and its spelling is not. Returning `None` for a whole list because one element moved is
    PROBE-BLIND -- it makes a call site that DOES pass a dialect read as one that passes none, which is
    worse than not looking at all, because the case that consumed it reported a verdict.
    """
    tree = ast.parse(DEPLOY.read_text(encoding="utf-8"))
    bindings = _bind_lists(tree)

    def literal(node: ast.AST | None) -> list[str] | None:
        if node is None:
            return None
        if isinstance(node, ast.Name):
            return bindings.get(node.id)
        if isinstance(node, ast.List):
            return [
                element.value if (isinstance(element, ast.Constant)
                                  and isinstance(element.value, str)) else "<interpolated>"
                for element in node.elts
            ]
        try:
            value = ast.literal_eval(node)
        except (ValueError, SyntaxError):
            return None
        return [value] if isinstance(value, str) else None

    calls = []
    for node in ast.walk(tree):
        if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                and node.func.id == "tool_argv"):
            continue
        stem = literal(node.args[0]) if node.args else None
        keywords = {kw.arg: kw.value for kw in node.keywords}
        calls.append((stem[0] if stem else "",
                      literal(node.args[1]) if len(node.args) > 1 else None,
                      literal(keywords.get("py_args"))))
    return [c for c in calls if c[0]]


class TestToolDispatcher(unittest.TestCase):
    """A resolved tool must EXIST, and the arguments addressed to it must be ACCEPTED by it."""

    def test_there_are_call_sites_to_check(self):
        """The counterweight. A parse that silently found nothing would make every case below pass."""
        calls = _tool_argv_calls()
        self.assertGreaterEqual(len(calls), 3, f"found only {calls} tool_argv call site(s)")
        # A CLOSED set on purpose, and the reason it is kept honest is written here: a case that only
        # asserted "at least three" would pass with the WRONG three. Every port that adds a caller adds
        # a stem, and this is where that gets noticed.
        self.assertEqual({stem for stem, _, _ in calls},
                         {"game-lock", "run-guards", "test-fast", "verify-change"},
                         "a call site appeared or vanished; re-read it rather than trusting this list")

    def test_every_stem_RESOLVES_to_a_file_that_exists(self):
        """The `test-fast` defect, as a rule.

        `tool_argv` is the ONE place that learns a port renamed a file. A stem that resolves to nothing
        refuses at run time, inside a deploy, having touched an install first.
        """
        # Resolve against the roots the TOOL searches, not this file's own directory. `tool_argv`
        # searches TOOL_SEARCH_ROOTS across repositories and its refusal names every directory it
        # searched, because a message naming only one sent a reader to write a duplicate tool there
        # instead of finding the existing one in gk-core. This test carried the pre-fix assumption:
        # four stems that all resolve in gk-core read as missing because it looked only in
        # gk-fusion/scripts. Same assertion, against the roots that can satisfy it.
        for stem, _, _ in _tool_argv_calls():
            resolved = dp.TOOL_FILE_STEMS.get(stem, stem)
            searched = [str(root / f"{resolved}{ext}")
                        for root in dp.TOOL_SEARCH_ROOTS for ext in (".py", ".ps1")]
            found = [path for path in searched if Path(path).is_file()]
            self.assertTrue(found, f"tool_argv({stem!r}) resolves to {resolved}{{,.py,.ps1}}, and NO "
                                   f"repository that owns one has it; searched: " + ", ".join(searched))

    def test_a_python_tool_is_given_the_PYTHON_dialect(self):
        """The `verify-change` defect, as a rule.

        Resolving the FILE is not resolving the ARGUMENTS. When the resolved tool is Python, the
        arguments that reach it must be the Python spelling, or argparse exits 2 on
        `unrecognized arguments` and a working gate looks broken.
        """
        for stem, tool_args, py_args in _tool_argv_calls():
            resolved = dp.TOOL_FILE_STEMS.get(stem, stem)
            if not (SCRIPTS / f"{resolved}.py").is_file():
                continue
            self.assertIsNotNone(
                py_args,
                f"tool_argv({stem!r}) resolves to a PYTHON tool but passes no py_args dialect, so "
                f"the PowerShell spellings {tool_args} go to argparse")
            self.assertNotEqual(py_args, tool_args,
                                f"tool_argv({stem!r}) passes the same list as both dialects, so the "
                                f"dialect parameter is decorative")

    def test_each_dialect_is_ACCEPTED_by_the_tool_it_addresses(self):
        """The end-to-end proof, and the one that actually catches a wrong spelling.

        Probed for real by running the tool with `--help`. `--help` rather than a dry run because a
        wrong flag beats `--help` as an `unrecognized arguments` error, so an accepted `--help`
        separates "the dialect is right" from "the tool ignores the flag entirely".
        """
        for stem, _, py_args in _tool_argv_calls():
            resolved = dp.TOOL_FILE_STEMS.get(stem, stem)
            target = SCRIPTS / f"{resolved}.py"
            if not (py_args is not None and target.is_file()):
                continue
            proc = subprocess.run([sys.executable, str(target), *py_args, "--help"],
                                  capture_output=True, text=True, timeout=HELP_TIMEOUT)
            text = (proc.stdout + proc.stderr).lower()
            self.assertNotIn("unrecognized arguments", text,
                             f"tool_argv({stem!r}) addresses {target.name} with {py_args}, which it "
                             f"rejects: {text.strip()[-300:]}")
            for flag in (arg for arg in py_args if arg.startswith("--")):
                self.assertIn(flag, text, f"{target.name} does not declare {flag}, so the caller's "
                                          f"dialect is wrong for it")

    def test_the_rename_map_only_names_stems_the_callers_use(self):
        """A rename map entry nobody calls is a stale entry, and a stale entry is how a name stops
        meaning anything. Small, and it is a CLOSED set the callers above already enumerate."""
        used = {stem for stem, _, _ in _tool_argv_calls()}
        self.assertEqual(set(dp.TOOL_FILE_STEMS) - used, set())

    def test_the_resolver_does_not_accept_a_stem_it_cannot_resolve(self):
        """The refusal must stay a refusal with a NON-ZERO exit, not a silent success: a deploy that
        skipped a precondition step would otherwise report success for a gate nobody ran."""
        with self.assertRaises(dp.Refusal) as caught:
            dp.tool_argv("no-such-tool", [], stage="probe")
        self.assertEqual(caught.exception.reason, "TOOL-MISSING")
        self.assertIn("no-such-tool", caught.exception.detail,
                      "the refusal must name the LOGICAL name, which is what the caller passed")


if __name__ == "__main__":
    unittest.main(verbosity=2)
