"""Overlay combat scenarios."""

from __future__ import annotations

from live_test.lawn import ensure_lawn
from live_test.report import Report
from live_test.scenarios import RunContext, scenario


def _pick_ptr(ctx: RunContext) -> str:
    if ctx.target_ptr:
        return ctx.target_ptr
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/shield/demo-all", {"amount": 50})
    demo = ctx.client.wait_kind(tip, "debug.shield.demo-all", timeout_sec=12)
    targets = list((ctx.client.payload(demo) or {}).get("targets") or [])
    if targets:
        return str(targets[-1].get("targetPtr") or "")
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/board-stats", {})
    ev = ctx.client.wait_kind(tip, "debug.board-stats", timeout_sec=10)
    p = ctx.client.payload(ev) or {}
    for key in ("zombies", "plants", "livingZombies", "livingPlants"):
        arr = p.get(key)
        if isinstance(arr, list) and arr:
            item = arr[0]
            if isinstance(item, dict):
                return str(item.get("ptr") or item.get("targetPtr") or "")
    raise RuntimeError("no target ptr — pass --target-ptr or run lab.overlay first")


@scenario("combat.silence")
def combat_silence(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/combat/silence-vanilla", {"enabled": True})
    ev = ctx.client.wait_kind(tip, "debug.combat.silence-vanilla", timeout_sec=10)
    report.require(ev is not None, "silence-vanilla event")


@scenario("combat.pin-element")
def combat_pin(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    ptr = _pick_ptr(ctx)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug(
        "/combat/pin-element",
        {"ptr": ptr, "targetPtr": ptr, "elementPrimary": "fire"},
    )
    ev = ctx.client.wait_kind(tip, "debug.combat.pin-element", timeout_sec=10)
    report.require(ev is not None, f"pin-element for {ptr}")


@scenario("combat.probe")
def combat_probe(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    ctx.client.post_cheat_toggle("OVERLAY-COMBAT", True)
    # Ensure something to hit
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/scenario/lab-overlay", {})
    ctx.client.wait_kind(tip, "debug.run-steps.done", timeout_sec=60)
    ptr = _pick_ptr(ctx)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug(
        "/combat/probe",
        {
            "targetPtr": ptr,
            "amount": ctx.amount,
            "forceHit": True,
            "elementPayload": {"primary": "fire"},
        },
    )
    ev = ctx.client.wait_kind(tip, "debug.combat.probe", timeout_sec=12)
    p = ctx.client.payload(ev) or {}
    report.require(ev is not None, "debug.combat.probe")
    report.check(bool(p.get("hit") or p.get("source")), f"probe payload keys={list(p.keys())[:10]}")
