"""VFX probe scenarios."""

from __future__ import annotations

from live_test.lawn import ensure_lawn
from live_test.report import Report
from live_test.scenarios import RunContext, scenario


@scenario("vfx.shaders")
def vfx_shaders(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/fx/probe-shaders", {})
    ev = ctx.client.wait_kind(tip, "debug.fx.shader-probe", timeout_sec=12)
    if ev is None:
        ev = ctx.client.wait_kind(tip, "debug.fx.probe-shaders", timeout_sec=5)
    report.require(ev is not None, "fx shader probe event")


@scenario("vfx.list")
def vfx_list(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/fx/list", {})
    ev = ctx.client.wait_kind(tip, "debug.fx.list", timeout_sec=12)
    p = ctx.client.payload(ev) or {}
    report.require(ev is not None, "fx.list event")
    cues = p.get("cues") or p.get("items") or p.get("ids") or []
    report.require(isinstance(cues, list) and len(cues) > 0, f"non-empty cues count={len(cues) if isinstance(cues, list) else 'n/a'}")


@scenario("vfx.play")
def vfx_play(ctx: RunContext, report: Report) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/fx/list", {})
    listing = ctx.client.wait_kind(tip, "debug.fx.list", timeout_sec=12)
    p = ctx.client.payload(listing) or {}
    cues = p.get("cues") or p.get("items") or p.get("ids") or []
    cue = None
    if isinstance(cues, list) and cues:
        first = cues[0]
        cue = first if isinstance(first, str) else (first.get("id") if isinstance(first, dict) else None)
    if not cue:
        cue = "hit.default"
    tip = ctx.client.max_event_id()
    ctx.client.post_debug("/fx/play", {"cueId": cue, "id": cue})
    ev = ctx.client.wait_kind(tip, "debug.fx.play", timeout_sec=12)
    report.require(ev is not None, f"fx.play cue={cue}")
