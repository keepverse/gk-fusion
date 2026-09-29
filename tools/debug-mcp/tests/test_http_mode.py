"""HTTP mode + README (todo T8).

Non-loopback bind is refused before serving (Auth=None row: localhost
only). README walks every registered tool — the test enforces the sync so
the walkthrough cannot rot.
"""
import asyncio

import pytest

import server


def test_defaults_stdio_loopback():
    cfg = server.resolve_transport([])
    assert cfg["transport"] == "stdio"
    assert cfg["host"] == "127.0.0.1"
    assert cfg["port"] == 8899


def test_non_loopback_refused():
    with pytest.raises(ValueError, match="[Ll]oopback"):
        server.resolve_transport(["--transport", "http", "--host", "0.0.0.0"])


def test_loopback_variants_accepted():
    assert server.resolve_transport(["--host", "127.0.0.1"])["host"] == "127.0.0.1"
    assert server.resolve_transport(["--host", "localhost"])["host"] == "localhost"


def test_readme_walks_every_registered_tool():
    from pathlib import Path
    readme = (Path(__file__).resolve().parent.parent / "README.md").read_text()
    tools = asyncio.run(server.mcp.list_tools())
    names = [t.name for t in tools]
    assert len(names) == 20
    missing = [n for n in names if n not in readme]
    assert missing == [], f"README missing tools: {missing}"
