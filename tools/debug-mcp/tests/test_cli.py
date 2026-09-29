"""CLI parity: the same 19 adapters, callable without MCP (spec: Project Structure).

Hermetic: dispatch is tested with stub tools injected into cli.TOOLS, never
the live server. Only --list touches the real catalogue (pure data).
"""
import json

import cli
from cli import parse_params


def test_list_names_all_twenty_adapters(capsys):
    assert cli.main(["--list"]) == 0
    names = sorted(item["name"] for item in json.loads(capsys.readouterr().out))
    assert names == sorted(cli.TOOLS) and len(names) == 20


def test_list_matches_registered_mcp_tools():
    import asyncio
    from server import mcp
    assert sorted(t.name for t in asyncio.run(mcp.list_tools())) == sorted(cli.TOOLS)


def test_json_and_param_merge_with_json_types():
    out = parse_params('{"limit": 5, "kind": "board.start"}', ["limit=10", "click=true", "ref=c0"])
    assert out == {"limit": 10, "kind": "board.start", "click": True, "ref": "c0"}


def test_bad_json_is_usage_error_not_traceback(capsys):
    assert cli.main(["debug_events", "--json", "{nope"]) == 2
    assert json.loads(capsys.readouterr().out)["ok"] is False


def test_unknown_tool_names_list(capsys):
    assert cli.main(["nope"]) == 2
    assert "see --list" in capsys.readouterr().out


def test_dispatch_passes_kwargs_and_prints_envelope(capsys, monkeypatch):
    seen = {}

    def stub(**params):
        seen.update(params)
        return {"ok": True, "echo": params, "scope": "unit"}

    monkeypatch.setitem(cli.TOOLS, "unit_probe", (stub, "test only"))
    assert cli.main(["unit_probe", "--json", '{"a": 1}', "--param", "b=x"]) == 0
    assert seen == {"a": 1, "b": "x"}
    assert json.loads(capsys.readouterr().out)["echo"] == {"a": 1, "b": "x"}


def test_bad_arguments_are_usage_error(capsys, monkeypatch):
    monkeypatch.setitem(cli.TOOLS, "needs_one", (lambda *, required: {}, "test only"))
    assert cli.main(["needs_one", "--param", "wrong=1"]) == 2
    assert json.loads(capsys.readouterr().out)["ok"] is False
