"""debug_restart_game: adapter over scripts/restart-game.ps1. Never reimplements the script's own
recovery/polling logic -- only invokes it and reports what happened.
"""
import subprocess

import pytest

from tools import debug_restart_game as restart_game


def _write_script(tmp_path):
    scripts = tmp_path / "scripts"
    scripts.mkdir()
    (scripts / "restart-game.ps1").write_text("# stub\n")
    return tmp_path


def test_success_reports_exit_zero(tmp_path):
    root = _write_script(tmp_path)

    def runner(args, timeout):
        assert "-TimeoutSec" in args
        assert "restart-game.ps1" in args[args.index("-File") + 1]
        return 0, "==> Game relaunched and injector reconnected after 3 check(s)\n", ""

    out = restart_game.restart(timeout_sec=90, root=root, runner=runner)
    assert out["ok"] is True
    assert out["exitCode"] == 0
    assert "relaunched" in out["stdout"]
    assert out["scope"] == "local-machine"


def test_nonzero_exit_reports_failure(tmp_path):
    root = _write_script(tmp_path)

    def runner(args, timeout):
        return 1, "", "==> TIMEOUT after 120s\n"

    out = restart_game.restart(root=root, runner=runner)
    assert out["ok"] is False
    assert out["exitCode"] == 1
    assert "TIMEOUT" in out["stderr"]


def test_missing_script_refuses_without_invoking_runner(tmp_path):
    called = []

    def runner(args, timeout):
        called.append(1)
        return 0, "", ""

    out = restart_game.restart(root=tmp_path, runner=runner)
    assert out["ok"] is False
    assert "missing" in out["error"]
    assert called == []


def test_subprocess_timeout_reports_clearly(tmp_path):
    root = _write_script(tmp_path)

    def runner(args, timeout):
        raise subprocess.TimeoutExpired(cmd=args, timeout=timeout)

    out = restart_game.restart(timeout_sec=10, root=root, runner=runner)
    assert out["ok"] is False
    assert "40s" in out["error"]  # timeout_sec (10) + 30s subprocess headroom


def test_invalid_timeout_raises():
    with pytest.raises(ValueError, match="timeout_sec"):
        restart_game.restart(timeout_sec=0)
    with pytest.raises(ValueError, match="timeout_sec"):
        restart_game.restart(timeout_sec="soon")


def test_targeting_params_reach_the_script(tmp_path):
    root = _write_script(tmp_path)
    seen = {}

    def runner(args, timeout):
        for flag in ("-GameDir", "-BaseUrl", "-Session"):
            seen[flag] = args[args.index(flag) + 1]
        return 0, "ok", ""

    out = restart_game.restart(root=root, runner=runner, game_dir="G:\\Clone",
                               base_url="http://127.0.0.1:5099/", session="probe-1")
    assert out["ok"] is True
    assert seen == {"-GameDir": "G:\\Clone", "-BaseUrl": "http://127.0.0.1:5099",
                    "-Session": "probe-1"}


def test_targeting_params_absent_by_default(tmp_path):
    root = _write_script(tmp_path)

    def runner(args, timeout):
        assert "-GameDir" not in args and "-BaseUrl" not in args and "-Session" not in args
        return 0, "ok", ""

    assert restart_game.restart(root=root, runner=runner)["ok"] is True


def test_non_loopback_base_url_raises():
    with pytest.raises(ValueError, match="loopback"):
        restart_game.restart(base_url="http://192.168.1.5:5088")


def test_empty_game_dir_and_session_raise():
    with pytest.raises(ValueError, match="game_dir"):
        restart_game.restart(game_dir="  ")
    with pytest.raises(ValueError, match="session"):
        restart_game.restart(session="")


def test_default_runner_returns_when_the_script_exits_even_if_a_child_keeps_its_handles():
    """L-N26: a grandchild that inherits the runner's output handles and outlives its parent must not keep the
    call waiting (the game did exactly that to debug_restart_game, for 30 minutes)."""
    import sys
    import time

    parent = ("import subprocess, sys; "
              "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(20)']); "
              "print('parent done')")
    started = time.monotonic()
    code, out, _ = restart_game._default_runner([sys.executable, "-c", parent], timeout=15)
    assert code == 0
    assert "parent done" in out
    assert time.monotonic() - started < 10

