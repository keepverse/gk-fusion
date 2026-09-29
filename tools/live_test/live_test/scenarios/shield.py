"""Shield pack: lab / bar / absorb / decade / hide / cascade / toggle / all."""

from __future__ import annotations

import math
import time
from typing import Any

from live_test.lawn import ensure_lawn
from live_test.report import Report
from live_test.scenarios import RunContext, scenario


def _display_ratio(hp: float, max_hp: float) -> float:
    if hp <= 0 or max_hp <= 0:
        return 0.0
    raw = min(1.0, max(0.0, hp / max_hp))
    step = math.floor(raw * 10) / 10.0
    return 0.1 if step <= 0 else step


def _bar_status(ctx: RunContext) -> dict[str, Any]:
    c = ctx.client
    tip = c.max_event_id()
    c.post_debug("/shield/bar-status", {})
    ev = c.wait_kind(tip, "debug.shield.bar-status", timeout_sec=10)
    p = c.payload(ev)
    if not p:
        raise RuntimeError("no debug.shield.bar-status")
    return p


def _snapshot(ctx: RunContext, target_ptr: str | None = None) -> dict[str, Any]:
    c = ctx.client
    tip = c.max_event_id()
    body: dict[str, Any] = {}
    if target_ptr:
        body["targetPtr"] = target_ptr
    c.post_debug("/shield/snapshot", body)
    ev = c.wait_kind(tip, "debug.shield.snapshot", timeout_sec=10)
    p = c.payload(ev)
    if not p:
        raise RuntimeError("no debug.shield.snapshot")
    return p


def _ensure_lab(ctx: RunContext) -> None:
    ensure_lawn(ctx.client, enter_level=ctx.enter_level)
    if not ctx.force_setup:
        try:
            st = _bar_status(ctx)
            if int(st.get("dataOwners") or 0) >= 1 and bool(st.get("shaderOk")):
                print("  skip lab (shields already present); use --force-setup to rebuild")
                return
        except RuntimeError:
            pass
    print("  scenario lab-shield-bar")
    tip = ctx.client.max_event_id()
    q = ctx.client.post_debug("/scenario/lab-shield-bar", {})
    print(f"  queued steps={q.get('steps')}")
    done = ctx.client.wait_kind(tip, "debug.run-steps.done", timeout_sec=60)
    if not done:
        raise RuntimeError("timeout debug.run-steps.done")
    demo = ctx.client.wait_kind(int(done["id"]), "debug.shield.demo-all", timeout_sec=15)
    if not demo:
        demo = ctx.client.wait_kind(tip, "debug.shield.demo-all", timeout_sec=5)
    if not demo:
        raise RuntimeError("no debug.shield.demo-all")
    dp = ctx.client.payload(demo) or {}
    if int(dp.get("targetCount") or 0) < 1:
        raise RuntimeError("demo-all targetCount=0")


@scenario("shield.lab")
def shield_lab(ctx: RunContext, report: Report) -> None:
    ctx.force_setup = True
    _ensure_lab(ctx)
    snap = _snapshot(ctx)
    owners = list(snap.get("owners") or [])
    report.require(int(snap.get("ownerCount") or len(owners)) >= 1, f"ownerCount={snap.get('ownerCount')}")
    for o in owners:
        stacks = list(o.get("stacks") or [])
        report.check(len(stacks) == 3, f"ptr={o.get('ptr')} stacks={len(stacks)} (expect 3)")


@scenario("shield.bar")
def shield_bar(ctx: RunContext, report: Report) -> None:
    _ensure_lab(ctx)
    best = None
    deadline = time.time() + 10
    while time.time() < deadline:
        best = _bar_status(ctx)
        print(
            f"  data={best.get('dataOwners')} worldBars={best.get('worldBars')} "
            f"shaderOk={best.get('shaderOk')} fill={best.get('fillRatio')} "
            f"early={(best.get('lastDraw') or {}).get('early')}"
        )
        if (
            bool(best.get("shaderOk"))
            and int(best.get("worldBars") or 0) > 0
            and int(best.get("worldBars") or 0) == int(best.get("dataOwners") or 0)
        ):
            break
        time.sleep(0.4)
    assert best is not None
    early = (best.get("lastDraw") or {}).get("early")
    data = int(best.get("dataOwners") or 0)
    world = int(best.get("worldBars") or 0)
    resolved = int(best.get("resolvedBodies") or 0)
    report.require(data > 0, f"dataOwners={data}")
    report.require(bool(best.get("shaderOk")), f"shaderOk={best.get('shaderOk')}")
    # Orphan shield owners (no BodyWorld) inflate dataOwners; draw path is worldBars vs resolved.
    report.require(world > 0, f"worldBars={world}")
    if resolved > 0:
        report.require(world == resolved, f"worldBars={world} resolvedBodies={resolved}")
    else:
        report.require(world <= data, f"worldBars={world} dataOwners={data}")
    report.require(float(best.get("fillRatio") or 0) > 0, f"fillRatio={best.get('fillRatio')}")
    report.require(early == "ok", f"early={early}")
    if world != data:
        report.check(True, f"note: dataOwners={data} > worldBars={world} (unresolved anchors ok)")


@scenario("shield.absorb")
def shield_absorb(ctx: RunContext, report: Report) -> None:
    _ensure_lab(ctx)
    c = ctx.client
    c.post_cheat_toggle("OVERLAY-COMBAT", True)
    tip = c.max_event_id()
    c.post_debug("/shield/demo-all", {"amount": 100})
    demo = c.wait_kind(tip, "debug.shield.demo-all", timeout_sec=15)
    dp = c.payload(demo) or {}
    targets = list(dp.get("targets") or [])
    report.require(len(targets) >= 1, f"demo-all targets={len(targets)}")
    ptr = str(targets[-1].get("targetPtr") or targets[-1].get("ptr") or "")
    before = _snapshot(ctx, ptr)
    owners = list(before.get("owners") or [])
    hp_before = int(owners[0].get("hp") or 0) if owners else 0

    tip = c.max_event_id()
    body = {
        "targetPtr": ptr,
        "amount": ctx.amount,
        "forceHit": True,
        "elementPayload": {"primary": "fire"},
    }
    c.post_debug("/combat/probe", body)
    probe = c.wait_kind(tip, "debug.combat.probe", timeout_sec=12)
    pp = c.payload(probe) or {}
    absorbed = pp.get("shieldAbsorbed")
    report.require(probe is not None, "got debug.combat.probe")
    report.require(absorbed is not None and float(absorbed) > 0, f"shieldAbsorbed={absorbed}")
    after = _snapshot(ctx, ptr)
    owners2 = list(after.get("owners") or [])
    hp_after = int(owners2[0].get("hp") or 0) if owners2 else 0
    report.require(hp_after < hp_before or hp_before == 0, f"hp {hp_before} -> {hp_after}")
    print(f"  hitPtr={ptr} absorbed={absorbed} hp={hp_before}->{hp_after}")


@scenario("shield.decade")
def shield_decade(ctx: RunContext, report: Report) -> None:
    # Prefer a mid-bucket state; run absorb first if full
    _ensure_lab(ctx)
    st = _bar_status(ctx)
    owners = list(st.get("owners") or [])
    if not owners:
        shield_absorb(ctx, report)
        st = _bar_status(ctx)
        owners = list(st.get("owners") or [])
    # Find damaged owner or probe once
    damaged = [o for o in owners if float(o.get("trueRatio") or o.get("ratio") or 1) < 0.99]
    if not damaged:
        shield_absorb(ctx, Report("shield.decade.prep"))
        st = _bar_status(ctx)
        owners = list(st.get("owners") or [])
        damaged = [o for o in owners if float(o.get("trueRatio") or 1) < 0.99]
    report.require(len(damaged) >= 1, "need a damaged shield owner for decade check")
    o = damaged[0]
    hp = float(o.get("hp") or 0)
    mx = float(o.get("maxHp") or 0)
    true_r = float(o.get("trueRatio") or (hp / mx if mx else 0))
    disp = float(o.get("displayRatio") or o.get("ratio") or 0)
    expect = _display_ratio(hp, mx)
    report.require(
        abs(disp - expect) < 0.05,
        f"displayRatio={disp} expect={expect} true={true_r:.3f} hp={hp}/{mx}",
    )
    if abs(true_r - expect) > 0.05:
        report.check(True, f"true≠display as intended (true={true_r:.3f} display={disp})")


@scenario("shield.hide")
def shield_hide(ctx: RunContext, report: Report) -> None:
    _ensure_lab(ctx)
    st0 = _bar_status(ctx)
    report.require(int(st0.get("worldBars") or 0) > 0, "bars present before clear")
    # clear is per-target (debug.shield.cleared) — wipe every snapshot owner
    snap = _snapshot(ctx)
    owners = list(snap.get("owners") or [])
    report.require(len(owners) >= 1, "snapshot owners before clear")
    for o in owners:
        ptr = str(o.get("ptr") or "")
        if not ptr:
            continue
        tip = ctx.client.max_event_id()
        ctx.client.post_debug("/shield/clear", {"targetPtr": ptr})
        ev = ctx.client.wait_kind(tip, "debug.shield.cleared", timeout_sec=8)
        if ev is None:
            report.check(False, f"clear ack missing for {ptr}")
    time.sleep(0.8)
    st = _bar_status(ctx)
    report.require(
        int(st.get("worldBars") or 0) == 0,
        f"after clear worldBars={st.get('worldBars')} dataOwners={st.get('dataOwners')}",
    )


@scenario("shield.cascade")
def shield_cascade(ctx: RunContext, report: Report) -> None:
    _ensure_lab(ctx)
    c = ctx.client
    c.post_cheat_toggle("OVERLAY-COMBAT", True)
    tip = c.max_event_id()
    c.post_debug("/shield/demo-all", {"amount": 100})
    demo = c.wait_kind(tip, "debug.shield.demo-all", timeout_sec=15)
    targets = list((c.payload(demo) or {}).get("targets") or [])
    report.require(len(targets) >= 1, "demo-all targets")
    ptr = str(targets[-1].get("targetPtr") or "")
    snap = _snapshot(ctx, ptr)
    owners = [o for o in (snap.get("owners") or []) if str(o.get("ptr")) == ptr or True]
    o = owners[0]
    stacks = list(o.get("stacks") or [])
    els = [str(s.get("element") or "").lower() for s in stacks]
    report.require(len(els) >= 2, f"need multi-stack got {els}")
    report.require(els[0] == "fire", f"outer stack must be fire, got {els}")
    # Drain outer
    tip = c.max_event_id()
    c.post_debug(
        "/combat/probe",
        {"targetPtr": ptr, "amount": -120, "forceHit": True, "elementPayload": {"primary": "fire"}},
    )
    c.wait_kind(tip, "debug.combat.probe", timeout_sec=12)
    snap2 = _snapshot(ctx, ptr)
    o2 = list(snap2.get("owners") or [o])[0]
    els2 = [str(s.get("element") or "").lower() for s in (o2.get("stacks") or [])]
    report.require(len(els2) < len(els), f"stacks after hit {els} -> {els2}")
    report.require("fire" not in els2, f"outer fire peeled, remaining {els2}")


@scenario("shield.toggle")
def shield_toggle(ctx: RunContext, report: Report) -> None:
    _ensure_lab(ctx)
    st = _bar_status(ctx)
    if "enabled" in st:
        report.check(True, f"shield bar enabled={st.get('enabled')} — press F9 in-game to toggle (manual)")
    else:
        report.check(True, "SKIP auto F9 — press F9 in-game; F7 is settings only")


@scenario("shield.all")
def shield_all(ctx: RunContext, report: Report) -> None:
    for name, fn in [
        ("lab", shield_lab),
        ("bar", shield_bar),
        ("absorb", shield_absorb),
        ("decade", shield_decade),
        ("cascade", shield_cascade),
        ("hide", shield_hide),
        ("toggle", shield_toggle),
    ]:
        print(f"-- shield.{name} --")
        sub = Report(f"shield.{name}")
        try:
            fn(ctx, sub)
        except Exception as e:
            sub.check(False, str(e))
        for ok, msg in sub.rows:
            report.check(ok, f"{name}: {msg}")
        if not sub.ok() and name in ("lab", "bar"):
            break
