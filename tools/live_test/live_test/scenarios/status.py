"""Status apply/clear catalog — Unity CC smoke + StatusRuntime L2 paths."""

from __future__ import annotations

from live_test.lawn import ensure_lawn
from live_test.report import Report
from live_test.scenarios import RunContext, scenario
from live_test.status_apply import (
    apply_status_until_started,
    clear_status_target,
    ensure_lab_board,
    poll_events_after,
)

# Unity CC smoke subset (vanilla debuffs — not custom identity VFX)
UNITY_CC_SUBSET = ("butter", "freeze", "cold")


def _lab_ptr(ctx: RunContext) -> str:
    if ctx.target_ptr:
        return ctx.target_ptr
    return ensure_lab_board(ctx.client).target_ptr


@scenario("status.apply")
def status_apply(ctx: RunContext, report: Report) -> None:
    """Unity CC bypass — debug.apply-status only (no custom sustained VFX)."""
    lab = ensure_lab_board(ctx.client)
    ptr = ctx.target_ptr or lab.target_ptr
    tip = ctx.client.max_event_id()
    body: dict = {"statusId": "butter", "duration": 2.0, "targetPtr": ptr}
    ctx.client.post_debug("/apply-status", body)
    ev = ctx.client.wait_kind(tip, "debug.apply-status", timeout_sec=10)
    report.require(ev is not None, "debug.apply-status event (Unity CC path)")


@scenario("status.clear")
def status_clear(ctx: RunContext, report: Report) -> None:
    ensure_lab_board(ctx.client)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/clear-status", {})
    ev = ctx.client.wait_kind(tip, "debug.clear-status", timeout_sec=10)
    if ev is None:
        ev = ctx.client.wait_kind(tip, "debug.status.cleared", timeout_sec=5)
    report.require(ev is not None, "clear-status event")


@scenario("status.catalog")
def status_catalog(ctx: RunContext, report: Report) -> None:
    """Unity CC subset — not StatusRuntime L2 (see status.l2.catalog)."""
    lab = ensure_lab_board(ctx.client)
    ptr = ctx.target_ptr or lab.target_ptr
    for sid in UNITY_CC_SUBSET:
        tip = ctx.client.max_event_id()
        body: dict = {"statusId": sid, "duration": 1.0, "targetPtr": ptr}
        ctx.client.post_debug("/apply-status", body)
        ev = ctx.client.wait_kind(tip, "debug.apply-status", timeout_sec=8)
        report.check(ev is not None, f"apply-status {sid}")
        clear_status_target(ctx.client, ptr)


@scenario("status.l2.apply")
def status_l2_apply(ctx: RunContext, report: Report) -> None:
    """StatusRuntime apply → custom sustained VFX (wither smoke). All-in-one lab setup."""
    lab = ensure_lab_board(ctx.client)
    ptr = ctx.target_ptr or lab.target_ptr
    started = apply_status_until_started(ctx.client, "wither", ptr, duration_ms=4000)
    report.require(started, "debug.fx.state.started for wither")
    tip = ctx.client.max_event_id()
    ctx.client.post_debug(
        "/status/apply",
        {"statusId": "wither", "hostPtr": ptr, "amount": 20, "durationMs": 4000},
    )
    ev = ctx.client.wait_kind(tip, "debug.status.apply", timeout_sec=8)
    report.check(ev is not None, "debug.status.apply ack event")


@scenario("status.l2.catalog")
def status_l2_catalog(ctx: RunContext, report: Report) -> None:
    """Subset of 13 custom identity statuses via /status/apply + retry."""
    lab = ensure_lab_board(ctx.client)
    ptr = ctx.target_ptr or lab.target_ptr
    subset = ("wither", "rot", "pact_mark", "spark", "charm_pulse")
    for sid in subset:
        ok = apply_status_until_started(ctx.client, sid, ptr, duration_ms=4000)
        report.require(ok, f"fx.state.started {sid}")
        clear_status_target(ctx.client, ptr)


@scenario("status.l2.organic")
def status_l2_organic(ctx: RunContext, report: Report) -> None:
    """Organic apply via status-l2-wither (fx.overlay_damage + fire-synthetic)."""
    ensure_lab_board(ctx.client)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/scenario/status-l2-wither", {})
    done = ctx.client.wait_kind(tip, "debug.run-steps.done", timeout_sec=90)
    report.require(done is not None, "status-l2-wither run-steps.done")
    hits = poll_events_after(
        ctx.client,
        tip,
        {"debug.status.apply", "debug.fx.state.started", "debug.status"},
        timeout_sec=20.0,
    )
    kinds = {ev.get("kind") for ev in hits}
    report.require(
        "debug.fx.state.started" in kinds or "debug.status.apply" in kinds,
        f"organic status apply events kinds={sorted(kinds)}",
    )
