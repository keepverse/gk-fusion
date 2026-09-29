"""Stress fill/clear scenarios."""

from __future__ import annotations

import time

from live_test.lawn import ensure_lawn
from live_test.report import Report
from live_test.scenarios import RunContext, scenario


def _clear_all_shields(ctx: RunContext) -> None:
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/shield/snapshot", {})
    snap_ev = ctx.client.wait_kind(tip, "debug.shield.snapshot", timeout_sec=10)
    owners = list((ctx.client.payload(snap_ev) or {}).get("owners") or [])
    for o in owners:
        ptr = str(o.get("ptr") or "")
        if not ptr:
            continue
        tip = ctx.client.max_event_id()
        ctx.client.post_debug("/shield/clear", {"targetPtr": ptr})
        ctx.client.wait_kind(tip, "debug.shield.cleared", timeout_sec=8)
    time.sleep(0.3)


@scenario("stress.fill")
def stress_fill(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/stress-fill", {})
    ev = ctx.client.wait_kind(tip, "debug.stress.fill", timeout_sec=30)
    report.require(ev is not None, "stress-fill event (debug.stress.fill)")
    h = ctx.client.health()
    report.require(bool(h.get("injectorConnected")), "injector still connected after fill")


@scenario("stress.clear")
def stress_clear(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/stress-clear", {})
    ev = ctx.client.wait_kind(tip, "debug.stress.clear", timeout_sec=20)
    report.require(ev is not None, "stress-clear event (debug.stress.clear)")


@scenario("stress.noshield")
def stress_noshield(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    _clear_all_shields(ctx)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/stress-fill", {})
    ev = ctx.client.wait_kind(tip, "debug.stress.fill", timeout_sec=30)
    report.require(ev is not None, "stress-fill with shields cleared")
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/stress-clear", {})
    cleared = ctx.client.wait_kind(tip, "debug.stress.clear", timeout_sec=20)
    report.require(cleared is not None, "stress-clear after noshield fill")
    report.check(True, "noshield stress completed (perf compare is operator-owned)")
