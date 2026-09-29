"""Budget + evidence envelope shapes (spec: Tool contracts, Testing Strategy).

The load-bearing rule: a capped list ALWAYS says so (truncated=true +
next_cursor). Silent truncation — fewer items than exist with no flag — is a
test failure, never a fallback.
"""
import budget
import evidence


def test_default_limit_is_20_and_cap_is_100():
    env = budget.paginate(list(range(250)))
    assert len(env["items"]) == 20
    assert env["truncated"] is True
    assert env["next_cursor"] is not None


def test_no_silent_truncation_at_any_size():
    for total in (0, 1, 19, 20, 21, 99, 100, 101, 500):
        env = budget.paginate(list(range(total)))
        if len(env["items"]) < total:
            assert env["truncated"] is True, f"silently truncated at {total}"
            assert env["next_cursor"] is not None
        else:
            assert env["truncated"] is False


def test_limit_over_cap_clamps_to_100():
    env = budget.paginate(list(range(500)), limit=10_000)
    assert len(env["items"]) == 100
    assert env["truncated"] is True


def test_cursor_roundtrip_pages_without_gap_or_overlap():
    first = budget.paginate(list(range(250)))
    second = budget.paginate(list(range(250)), cursor=first["next_cursor"])
    assert [i for i in first["items"]] + [i for i in second["items"]] == list(range(40))
    assert second["truncated"] is True


def test_fail_rung_without_evidence_is_rejected():
    try:
        evidence.rung(verdict="FAIL", detail="boom")
        raise AssertionError("FAIL rung without file:line must not build")
    except ValueError:
        pass


def test_pass_rung_carries_scope_and_citation():
    r = evidence.rung(verdict="PASS", file="src/FusionRpg.Server/DebugEndpoints.cs:173",
                      detail="route exists", scope="rpg-server-debug")
    assert r["verdict"] == "PASS"
    assert r["scope"] == "rpg-server-debug"
