#!/usr/bin/env python3
r"""The contract suite for gk-fusion/scripts/guard-workflow-paths.py — no network, no repository.

    python gk-fusion/scripts/test_workflow_paths.py     # or: python -m unittest discover -s scripts

The tool under test exists because a release workflow named nine paths that resolve in none of the
two checkouts its job declared, and nothing said so. A test that only proved the tool runs would
not have caught that, and would not catch its return either: this suite is written so that REMOVING
a rule makes a case fail. Each case below states the failure it closes, and the three marked
`REGRESSION` are defects this tool actually had while being written — all three reported green over
a block or a rule it had not examined.

The temp substrate rule from docs/contributing/testing-standard.md applies: everything runs under
tempfile.mkdtemp and is removed in tearDown, and a failed temp delete FAILS the test. A leaked temp
directory here would be the 65.5 GB incident's shape, not a smaller version of it.
"""

from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOL = Path(__file__).resolve().parent / "guard-workflow-paths.py"

_spec = importlib.util.spec_from_file_location("guard_workflow_paths", TOOL)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_workflow_paths"] = guard  # @dataclass resolves its module through sys.modules
_spec.loader.exec_module(guard)


def workflow(body: str, *, jobs_indent: str = "  ") -> str:
    """A minimal workflow with the boilerplate filled in, so a case states only what it is about.

    The returned text's line numbers are what the tool reports, so a case that asserts a line reads
    it from `preamble_length` rather than hard-coding a number that a boilerplate change would move.
    """
    preamble = (
        "name: T\n"
        "on:\n"
        "  push:\n"
        "    branches: [main]\n"
        "jobs:\n"
        f"{jobs_indent}build:\n"
        f"{jobs_indent}  runs-on: windows-latest\n"
        f"{jobs_indent}  steps:\n"
    )
    return preamble + body


def line_of(body: str, needle: str) -> int:
    """The file line a body line lands on, measured from the assembled workflow.

    Measured rather than declared: a test that hard-codes a line offset is one boilerplate edit
    away from asserting the wrong number, and a test asserting the wrong number is worse than no
    test because it fails for a reason that has nothing to do with the tool.
    """
    return PREAMBLE_LINES + workflow(body).splitlines()[PREAMBLE_LINES:].index(
        next(line for line in body.splitlines() if needle in line)) + 1


PREAMBLE_LINES = 8


class TempTreeMixin(unittest.TestCase):
    """A disposable repo + workspace pair. A failed temp delete is a FAILURE, never swallowed."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="workflow-paths-test-"))
        self.addCleanup(self._rmtree, self.tmp)
        self.repo = self.tmp / "ws" / "gk-fusion"
        self.workspace = self.tmp / "ws"
        (self.repo / ".github" / "workflows").mkdir(parents=True)
        self.add_file("FusionRpg.slnx")

    def _rmtree(self, path: Path) -> None:
        if path.exists():
            shutil.rmtree(path)
        self.assertFalse(path.exists(), f"temp dir survived teardown: {path}")

    def add_file(self, relative: str, text: str = "# fixture\n") -> Path:
        path = self.repo / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def add_sibling(self, name: str, relative: str) -> Path:
        """A second repository in the workspace, the way a runner sees it."""
        path = self.workspace / name / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("# fixture\n", encoding="utf-8")
        return path

    def add_workflow(self, body: str, name: str = "ci.yml") -> Path:
        path = self.repo / ".github" / "workflows" / name
        path.write_text(workflow(body), encoding="utf-8")
        return path

    def run_tool(self, *args: str) -> tuple[int, str]:
        proc = subprocess.run([sys.executable, str(TOOL), "--repo", str(self.repo),
                               "--workspace", str(self.workspace), *args],
                              capture_output=True, text=True)
        return proc.returncode, proc.stdout + proc.stderr

    def findings(self, *args: str) -> list[dict]:
        code, out = self.run_tool(*args, "--json")
        self.assertIn(code, (0, 1), f"tool refused instead of reporting: {out}")
        return json.loads(out)["findings"]

    def paths(self, *args: str) -> list[str]:
        return [finding["path"] for finding in self.findings(*args)]


class TestTheReportedDefect(TempTreeMixin):
    """The shape this tool was written for, as a fixture rather than as git history."""

    def test_a_script_in_a_repository_the_job_never_checked_out_is_reported(self):
        # gk-core's guard runner, named from a job that checked out only itself. The file is
        # deliberately NOT created in this repository -- creating it in setUp would make the very
        # case the tool exists for report green.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: guards\n"
            "        shell: pwsh\n"
            "        run: |\n"
            "          python scripts/run_guards.py --tier ci\n")
        found = self.findings()
        self.assertEqual([f["path"] for f in found], ["scripts/run_guards.py"])
        self.assertEqual(found[0]["class"], "PATH-NOT-IN-DECLARED-CHECKOUT")

    def test_the_same_script_is_silent_once_the_job_checks_that_repository_out(self):
        self.add_sibling("gk-core", "scripts/run_guards.py")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-core\n"
            "          path: gk-core\n"
            "      - name: guards\n"
            "        shell: pwsh\n"
            "        working-directory: gk-core\n"
            "        run: |\n"
            "          python scripts/run_guards.py --tier ci\n")
        self.assertEqual(self.findings(), [])

    def test_the_finding_names_the_line_so_a_reader_can_open_it(self):
        body = (
            "      - uses: actions/checkout@v4\n"
            "      - name: guards\n"
            "        run: |\n"
            "          Write-Host hi\n"
            "          python scripts/missing.py\n")
        self.add_workflow(body)
        self.assertEqual(self.findings()[0]["line"], line_of(body, "python scripts/missing.py"))


class TestNoFalsePositives(TempTreeMixin):
    """A red that cries wolf trains its reader to skip the row that matters."""

    def test_an_argument_after_the_script_belongs_to_the_script(self):
        # `--pack-dir dist/FusionRpg` is the release's own OUTPUT directory. Reading it as an input
        # is how a scan starts reporting a build's output as a missing file.
        self.add_file("scripts/smoke.py")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: smoke\n"
            "        run: |\n"
            "          python scripts/smoke.py --pack-dir dist/FusionRpg\n")
        self.assertEqual(self.findings(), [])

    def test_a_module_name_is_not_a_path(self):
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: scan\n"
            "        run: |\n"
            "          python -m ipcensor.report scan --format json\n"
            "          python -c \"print(1)\"\n")
        self.assertEqual(self.findings(), [])

    def test_a_pip_requirements_and_editable_target_IS_a_path(self):
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: install\n"
            "        run: |\n"
            "          python -m pip install -r tools/tool/requirements.lock\n"
            "          python -m pip install -e tools/tool --no-deps\n")
        self.assertEqual(sorted(self.paths()), ["tools/tool", "tools/tool/requirements.lock"])

    def test_a_read_back_of_an_earlier_steps_output_is_not_a_missing_input(self):
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: zip\n"
            "        run: |\n"
            "          Get-Content artifacts/player-pack-smoke.json -Raw\n")
        self.assertEqual(self.findings(), [])

    def test_an_absolute_path_is_out_of_scope_rather_than_reported(self):
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: run\n"
            "        run: |\n"
            "          python C:/somewhere/tool.py\n")
        self.assertEqual(self.findings(), [])

    def test_a_checkout_path_is_a_declaration_not_a_reference(self):
        # Reading a checkout's own `path:` reported every cross-repository checkout as a missing
        # input, which is the opposite of the finding.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-core\n"
            "          path: gk-core\n")
        self.assertEqual(self.findings(), [])

    def test_a_cache_key_is_not_a_path(self):
        # `cache: npm` is a key. Treating it as one is how a guard learns to be ignored.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/setup-node@v4\n"
            "        with:\n"
            "          cache: npm\n")
        self.assertEqual(self.findings(), [])


class TestRegressions(TempTreeMixin):
    """Defects this tool actually had. Each one reported GREEN over what it had not examined."""

    def test_regression_a_comment_first_line_of_a_run_block_does_not_discard_the_block(self):
        # THE bug. Clearing the run-block flag on a comment line silently emptied every run block
        # whose first body line was a comment, and the tool then reported green having examined
        # nothing -- the exact blindness it was written to remove.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: guards\n"
            "        run: |\n"
            "          # THIRD SPELLING, NOW ONE.\n"
            "          python scripts/missing.py\n")
        self.assertEqual(self.paths(), ["scripts/missing.py"])

    def test_regression_a_with_input_of_a_uses_step_resolves_against_the_workspace(self):
        # `working-directory:` applies only to a `run:` step. Applying it to an action's input
        # reported gk-core's and gk-web's correct `cache-dependency-path` as broken.
        self.add_sibling("gk-web", "web/app/package-lock.json")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-web\n"
            "          path: gk-web\n"
            "      - defaults:\n"
            "        run:\n"
            "          working-directory: gk-fusion\n"
            "      - uses: actions/setup-node@v4\n"
            "        with:\n"
            "          cache-dependency-path: gk-web/web/app/package-lock.json\n")
        self.assertEqual(self.findings(), [])

    def test_regression_the_on_block_is_not_parsed_as_jobs(self):
        # `push:` at indent 2 under `on:` is a trigger, not a job named "push".
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: run\n"
            "        run: |\n"
            "          python scripts/missing.py\n")
        _, out = self.run_tool("--json")
        self.assertEqual(json.loads(out)["counts"]["jobs"], 1)


class TestTheOverlay(TempTreeMixin):
    """The trap the brief names: correct in a clone, wrong in the workspace."""

    def test_a_pathless_checkout_makes_the_workspace_root_this_repository(self):
        # On a runner a checkout with no `path:` puts the repository AT the workspace root, so
        # `dotnet build X` resolves against the repository and not against its parent.
        self.add_file("FusionRpg.slnx")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: build\n"
            "        run: dotnet build FusionRpg.slnx\n")
        self.assertEqual(self.findings(), [])

    def test_a_sibling_checkout_owns_the_first_segment(self):
        # `gk-web/x` must resolve into the gk-web checkout, not into this repository.
        self.add_sibling("gk-web", "web/app/package-lock.json")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-web\n"
            "          path: gk-web\n"
            "      - name: node\n"
            "        uses: actions/setup-node@v4\n"
            "        with:\n"
            "          cache-dependency-path: gk-web/web/app/package-lock.json\n")
        self.assertEqual(self.findings(), [])

    def test_below_the_boundary_a_sibling_cannot_capture_a_same_named_segment(self):
        # Once a `working-directory:` is applied the step is inside one tree, so ordinary joining
        # applies: a `scripts/` directory inside gk-core is not the other repository's.
        self.add_sibling("gk-core", "scripts/run_guards.py")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-core\n"
            "          path: gk-core\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-web\n"
            "          path: gk-web\n"
            "      - name: guards\n"
            "        working-directory: gk-core\n"
            "        run: |\n"
            "          python scripts/run_guards.py\n")
        self.assertEqual(self.findings(), [])

    def test_a_file_that_EXISTS_outside_every_declared_tree_is_still_reported(self):
        # Existence is not the contract; containment is. A `..` that escapes the job's own tree into
        # a directory no checkout declared resolves to a real file and still cannot run, so the
        # check must refuse it rather than pass it because the file is there.
        self.add_sibling("gk-core", "scripts/run_guards.py")
        (self.workspace / "elsewhere").mkdir()
        (self.workspace / "elsewhere" / "tool.py").write_text("# fixture\n", encoding="utf-8")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-core\n"
            "          path: gk-core\n"
            "      - name: stray\n"
            "        working-directory: gk-core\n"
            "        run: |\n"
            "          python ../elsewhere/tool.py\n")
        found = self.findings()
        self.assertEqual([f["path"] for f in found], ["../elsewhere/tool.py"])
        self.assertIn("OUTSIDE", found[0]["detail"])

    def test_a_dot_slash_script_as_a_command_word_is_a_reference(self):
        # The `./scripts/prepare-injector-refs.ps1` spelling: the third name for one script.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: prepare\n"
            "        run: |\n"
            "          ./scripts/prepare-injector-refs.ps1\n")
        self.assertEqual(self.paths(), ["./scripts/prepare-injector-refs.ps1"])

    def test_a_dot_slash_script_inside_a_declared_sibling_is_silent(self):
        self.add_sibling("gk-core", "scripts/prepare_injector_refs.py")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-core\n"
            "          path: gk-core\n"
            "      - name: prepare\n"
            "        working-directory: gk-core\n"
            "        run: |\n"
            "          ./scripts/prepare_injector_refs.py\n")
        self.assertEqual(self.findings(), [])


class TestDeclarationsAndRefusals(TempTreeMixin):
    def test_a_checkout_path_that_does_not_name_its_repository_is_reported(self):
        # `path: gk-core` for `repository: keepverse/gk-web` resolves against the wrong tree, and no
        # filesystem lookup can detect it.
        self.add_sibling("gk-core", "scripts/run_guards.py")
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-web\n"
            "          path: gk-core\n"
            "      - name: guards\n"
            "        working-directory: gk-core\n"
            "        run: |\n"
            "          python scripts/run_guards.py\n")
        found = self.findings()
        self.assertTrue(any(f["form"] == "checkout path does not name its repository"
                            for f in found), found)

    def test_a_repository_checkout_without_a_path_is_reported(self):
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: actions/checkout@v4\n"
            "        with:\n"
            "          repository: keepverse/gk-core\n")
        found = self.findings()
        self.assertTrue(any(f["form"] == "checkout without path:" for f in found), found)

    def test_an_empty_selection_is_a_refusal_and_not_a_pass(self):
        code, out = self.run_tool("--workflow", "no-such-workflow")
        self.assertEqual(code, guard.EXIT_REFUSED)
        self.assertIn("NO-WORKFLOWS", out)

    def test_a_repository_that_owns_no_workflows_is_a_measurement_not_a_refusal(self):
        # A sweep has to be able to pass over gk-data without pretending it was checked, and without
        # reading "nothing to check" as "checked, all clear". A SECOND, empty repository, so this
        # case does not depend on tearing the fixture down.
        empty = self.workspace / "gk-data"
        empty.mkdir(parents=True)
        proc = subprocess.run([sys.executable, str(TOOL), "--repo", str(empty),
                               "--workspace", str(self.workspace)],
                              capture_output=True, text=True)
        self.assertEqual(proc.returncode, guard.EXIT_OK)
        self.assertIn("owns no", proc.stdout)
        proc = subprocess.run([sys.executable, str(TOOL), "--repo", str(empty),
                               "--workspace", str(self.workspace), "--workflow", "ci.yml"],
                              capture_output=True, text=True)
        self.assertEqual(proc.returncode, guard.EXIT_REFUSED)
        self.assertIn("WORKFLOWS-MISSING", proc.stderr)

    def test_a_path_that_does_not_exist_is_a_refusal(self):
        proc = subprocess.run([sys.executable, str(TOOL), "--repo", str(self.tmp / "nope")],
                              capture_output=True, text=True)
        self.assertEqual(proc.returncode, guard.EXIT_REFUSED)
        self.assertIn("REPO-MISSING", proc.stderr)


class TestTheRedirectClass(TempTreeMixin):
    """A gate that cannot run and cannot say so is worse than a gate that cannot run."""

    def test_a_redirect_whose_parent_nobody_creates_is_reported(self):
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: scan\n"
            "        run: |\n"
            "          python -m ipcensor.report scan --format json > tasks/ipc/scan.json\n")
        found = [f for f in self.findings() if f["class"] == "REDIRECT-PARENT-UNDECLARED"]
        self.assertEqual(len(found), 1)
        self.assertIn("$LASTEXITCODE", found[0]["detail"])

    def test_a_redirect_whose_parent_the_same_step_creates_is_silent(self):
        # gk-core/ci.yml's measured shape: New-Item on the line before the redirect.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: scan\n"
            "        run: |\n"
            "          New-Item -ItemType Directory -Force -Path tasks/ipc | Out-Null\n"
            "          python -m ipcensor.report scan --format json > tasks/ipc/scan.json\n")
        self.assertEqual([f for f in self.findings()
                          if f["class"] == "REDIRECT-PARENT-UNDECLARED"], [])

    def test_a_get_content_line_is_not_a_redirect(self):
        # The `>` in the REDIRECT pattern is MANDATORY. With it optional, any path-shaped token on
        # any line matched, and a `Get-Content x.json` line was reported as a write.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - name: findings\n"
            "        run: |\n"
            "          Get-Content tasks/ipc/scan.json -Raw | ConvertFrom-Json\n")
        self.assertEqual(self.findings(), [])


class TestThisRepository(TempTreeMixin):
    """The check on the real tree, so a future workflow edit cannot pass unnoticed."""

    def setUp(self) -> None:
        super().setUp()
        self.real = TOOL.parent.parent
        self.assertTrue((self.real / ".github" / "workflows").is_dir(),
                        "the fixture repository is gk-fusion itself")
        # These assertions need a WORKSPACE: release.yml names paths in five sibling repositories,
        # and whether they resolve is only decidable where all five trees are on disk. In a CI job
        # that checked out one repository, there is nothing to resolve against, and a run that
        # reported on that basis would be reporting on its own absence.
        #
        # So they SKIP, with the reason stated, and the same assertion runs for real in the release
        # job -- which has declared every checkout and runs the check with --workspace pointed at
        # the runner's own layout. A skip that says why, next to a gate that runs, is a scope
        # statement; a skip that hid a red would be a weakened guard.
        self.workspace = self.real.parent
        if not (self.workspace / "gk-core" / ".github" / "workflows").is_dir():
            self.skipTest(f"no sibling workspace beside {self.real}; the release job runs this for real")

    def _scan_real(self, *args: str) -> list[dict]:
        proc = subprocess.run([sys.executable, str(TOOL), *args, "--json"],
                              capture_output=True, text=True, cwd=str(self.real))
        self.assertIn(proc.returncode, (0, 1), proc.stdout + proc.stderr)
        return json.loads(proc.stdout)["findings"]

    def test_no_workflow_in_this_repository_names_an_absent_path(self):
        # ci.yml's contract step calls gk-core's ci.yml as a reusable workflow, so it is one
        # repository and one checkout -- which is exactly the case the tool can decide, and exactly
        # the case that runs in CI without a sibling workspace.
        findings = self._scan_real("--workflow", "ci.yml")
        self.assertEqual(findings, [], findings)

    def test_the_release_workflow_resolves_every_path_it_names(self):
        findings = self._scan_real("--workflow", "release.yml")
        self.assertEqual(findings, [], findings)

    def test_every_repository_with_workflows_passes_the_check(self):
        # The guard is offered to gk-core's registry, so it must not report a red in a repository
        # that is correct. A false positive there would be refused at the landing, not adopted.
        workspace = self.workspace
        for name in ("gk-core", "gk-web", "gk-forge"):
            repository = workspace / name
            if not (repository / ".github" / "workflows").is_dir():
                continue
            proc = subprocess.run([sys.executable, str(TOOL), "--repo", str(repository),
                                   "--workspace", str(workspace), "--quiet"],
                                  capture_output=True, text=True)
            self.assertEqual(proc.returncode, 0,
                             f"{name}:\n{proc.stdout}{proc.stderr}")


class TestThisRepositoryWithoutAWorkspace(TempTreeMixin):
    """The part of the real-tree check that a single-checkout CI job CAN decide."""

    def test_ci_yml_resolves_every_path_it_names(self):
        # gk-fusion's ci.yml checks out only this repository, so every path it names must resolve
        # here. This is the assertion that runs in CI, and it is not vacuous: it is the same check
        # the release job runs, asked a question CI can answer.
        real = TOOL.parent.parent
        proc = subprocess.run([sys.executable, str(TOOL), "--workflow", "ci.yml", "--json"],
                              capture_output=True, text=True, cwd=str(real))
        self.assertIn(proc.returncode, (0, 1), proc.stdout + proc.stderr)
        self.assertEqual(json.loads(proc.stdout)["findings"], [])


class TestSurface(TempTreeMixin):
    def test_the_report_states_what_it_examines_and_what_it_does_not(self):
        self.add_workflow("      - uses: actions/checkout@v4\n")
        code, out = self.run_tool()
        self.assertEqual(code, 0)
        self.assertIn("not examined", out)
        self.assertIn("vocabulary", out)

    def test_json_carries_the_unexamined_with_inputs_by_name(self):
        # A reader must be able to see the gap rather than assume there is none.
        self.add_workflow(
            "      - uses: actions/checkout@v4\n"
            "      - uses: softprops/action-gh-release@v2\n"
            "        with:\n"
            "          generate_release_notes: true\n")
        code, out = self.run_tool("--json")
        self.assertEqual(code, 0)
        self.assertIn("generate_release_notes", json.loads(out)["notExamined"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
