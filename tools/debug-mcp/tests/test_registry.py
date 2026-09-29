"""Registry mapping harness (spec: adapter-only, enforced by test).

Every tool maps to an existing endpoint/service/CLI/file. An orphan tool —
logic of its own with no source — fails the suite. Empty roster passes
vacuously (T1 skeleton state).
"""
import registry


def test_empty_roster_passes_vacuously():
    assert registry.check_mapped([]) == []


def test_orphan_tool_fails():
    orphans = registry.check_mapped([{"name": "debug_invented", "source": None}])
    assert orphans == ["debug_invented"]


def test_mapped_tool_passes():
    orphans = registry.check_mapped([
        {"name": "debug_call", "source": "route:DebugEndpoints.cs"},
    ])
    assert orphans == []


def test_scope_labels_come_from_the_closed_set():
    assert set(registry.SCOPES) == {"game-injector-debug", "rpg-server-debug"}
