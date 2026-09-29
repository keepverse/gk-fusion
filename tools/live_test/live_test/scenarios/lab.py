"""Lab board setup scenarios."""

from __future__ import annotations

from live_test.lawn import ensure_lawn
from live_test.report import Report
from live_test.scenarios import RunContext, scenario


def _count_side(p: dict, *keys: str) -> int | None:
    for key in keys:
        val = p.get(key)
        if val is None:
            continue
        if isinstance(val, int):
            return val
        if isinstance(val, list):
            return len(val)
    return None


@scenario("lab.overlay")
def lab_overlay(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    q = ctx.client.post_debug("/scenario/lab-overlay", {})
    print(f"  queued steps={q.get('steps')}")
    done = ctx.client.wait_kind(tip, "debug.run-steps.done", timeout_sec=60)
    report.require(done is not None, "debug.run-steps.done")
    tip2 = ctx.client.max_event_id()
    ctx.client.post_debug("/board-stats", {})
    ev = ctx.client.wait_kind(tip2, "debug.board-stats", timeout_sec=15)
    p = ctx.client.payload(ev) or {}
    n_p = _count_side(p, "plants", "plantCount", "livingPlants")
    n_z = _count_side(p, "zombies", "zombieCount", "livingZombies")
    if n_p is not None and n_z is not None:
        report.require(n_p >= 1 and n_z >= 1, f"plants={n_p} zombies={n_z}")
    else:
        report.check(ev is not None, f"board-stats payload keys={list(p.keys())[:12]}")


@scenario("lab.empty")
def lab_empty(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/scenario/lab-empty", {})
    done = ctx.client.wait_kind(tip, "debug.run-steps.done", timeout_sec=60)
    report.require(done is not None, "debug.run-steps.done for lab-empty")
    tip2 = ctx.client.max_event_id()
    ctx.client.post_debug("/board-stats", {})
    ev = ctx.client.wait_kind(tip2, "debug.board-stats", timeout_sec=15)
    p = ctx.client.payload(ev) or {}
    n_p = _count_side(p, "plants", "plantCount", "livingPlants")
    n_z = _count_side(p, "zombies", "zombieCount", "livingZombies")
    report.require(ev is not None, "board-stats after lab-empty")
    if n_p is not None and n_z is not None:
        report.require(n_p == 0 and n_z == 0, f"empty board plants={n_p} zombies={n_z}")
    else:
        report.check(False, f"cannot read census from board-stats keys={list(p.keys())[:12]}")


@scenario("lab.freeze")
def lab_freeze(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/wave-freeze", {"enabled": True})
    ev = ctx.client.wait_kind(tip, "debug.wave-freeze", timeout_sec=10)
    report.require(ev is not None, "debug.wave-freeze event")
    p = ctx.client.payload(ev) or {}
    report.check(p.get("enabled") in (True, None) or "enabled" not in p, f"payload={p}")
