"""StatusRuntime apply helpers — POST /api/debug/status/apply (RPG + custom VFX)."""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any

from live_test.client import LiveClient

# Custom status ids with identity VFX (batch 1–5)
CUSTOM_STATUS_IDS = (
    "wither",
    "blight",
    "rot",
    "spark",
    "spore",
    "pact_mark",
    "leech",
    "expose",
    "shatter",
    "bond",
    "rally",
    "command",
    "charm_pulse",
)


@dataclass
class LabBoard:
    target_ptr: str
    plant_ptr: str | None
    level_type: str
    entered: bool
    scenario: str


def _wait_board_snapshot(client: LiveClient, after_id: int, timeout_sec: float = 15.0) -> dict[str, Any] | None:
    deadline = time.time() + timeout_sec
    cursor = after_id
    while time.time() < deadline:
        client.post_debug("/effect/board-snapshot", {})
        time.sleep(0.4)
        items = client.events(after_id=cursor, limit=100)
        hits = [e for e in items if e.get("kind") == "debug.effect.board-snapshot"]
        if hits:
            p = client.payload(hits[-1])
            if isinstance(p, dict):
                return p
        if items:
            cursor = int(items[-1]["id"])
    return None


def _entities_from_snapshot(snapshot: dict[str, Any] | None) -> tuple[list[dict], list[dict]]:
    if not snapshot:
        return [], []
    entities = snapshot.get("entities") or []
    if not isinstance(entities, list):
        return [], []
    plants = [e for e in entities if isinstance(e, dict) and e.get("side") == "plant" and e.get("living")]
    zombies = [e for e in entities if isinstance(e, dict) and e.get("side") == "zombie" and e.get("living")]
    return plants, zombies


def _recent_debug_errors(client: LiveClient, after_id: int) -> list[str]:
    lines: list[str] = []
    for ev in client.events(after_id=after_id, limit=200):
        if ev.get("kind") not in ("cheat.error", "debug.effect.error"):
            continue
        p = client.payload(ev) or {}
        msg = p.get("error") or p.get("message") or ev.get("payload")
        if msg:
            lines.append(f"{ev.get('kind')}: {msg}")
    return lines


def ensure_lab_board(
    client: LiveClient,
    *,
    scenario: str = "lab-overlay",
    level_number: int = 1,
    timeout_sec: int = 60,
    skip_setup: bool = False,
) -> LabBoard:
    """All-in-one: enter level 1 if needed, run lab scenario, require living zombie ptr."""
    h = client.health()
    if not h.get("ok"):
        raise RuntimeError("server health.ok=false")
    if not h.get("injectorConnected"):
        raise RuntimeError("injector not connected — see live-lawn-quick-start skill")

    entered = False
    level_type = ""
    target_ptr: str | None = None
    plant_ptr: str | None = None
    cursor = client.max_event_id()

    if not skip_setup:
        resp = client.post_debug(
            "/lawn/quick-start",
            {"scenario": scenario, "levelNumber": level_number, "timeoutSec": timeout_sec},
        )
        if not isinstance(resp, dict) or not resp.get("ok", True):
            raise RuntimeError(f"lawn/quick-start failed: {resp}")
        entered = bool(resp.get("entered"))
        level_type = str(resp.get("levelType") or "")
        if resp.get("targetPtr"):
            target_ptr = str(resp["targetPtr"])
        if resp.get("plantPtr"):
            plant_ptr = str(resp["plantPtr"])
        cursor = client.max_event_id()

    if not target_ptr:
        snap = _wait_board_snapshot(client, cursor)
        plants, zombies = _entities_from_snapshot(snap)
        if plants:
            plant_ptr = str(plants[0].get("ptr") or "")
        if zombies:
            target_ptr = str(zombies[0].get("ptr") or "")

    if scenario == "lab-overlay" and not target_ptr:
        errs = _recent_debug_errors(client, cursor)
        detail = "\n  ".join(errs) if errs else "(no cheat.error in recent events)"
        raise RuntimeError(f"lab board has no living zombie ptr — setup failed.\nRecent errors:\n  {detail}")

    if not target_ptr:
        raise RuntimeError(f"ensure_lab_board: no target ptr (scenario={scenario} skip_setup={skip_setup})")

    return LabBoard(
        target_ptr=target_ptr,
        plant_ptr=plant_ptr,
        level_type=level_type,
        entered=entered,
        scenario=scenario,
    )


def resolve_target_ptr(client: LiveClient, scenario: str = "lab-overlay") -> str | None:
    """Thin wrapper — prefer ensure_lab_board for hard fail."""
    try:
        return ensure_lab_board(client, scenario=scenario).target_ptr
    except RuntimeError:
        return None


def wait_fx_started(
    client: LiveClient,
    after_id: int,
    status_id: str,
    timeout_sec: float = 2.5,
) -> bool:
    deadline = time.time() + timeout_sec
    cursor = after_id
    while time.time() < deadline:
        items = client.events(after_id=cursor, limit=200)
        for ev in items:
            if ev.get("kind") != "debug.fx.state.started":
                continue
            pl = client.payload(ev) or {}
            if pl.get("statusId") == status_id:
                return True
        if items:
            cursor = int(items[-1]["id"])
        time.sleep(0.25)
    return False


def apply_status_until_started(
    client: LiveClient,
    status_id: str,
    host_ptr: str,
    *,
    duration_ms: int = 6000,
    amount: int = 20,
    max_tries: int = 6,
) -> bool:
    """Retry apply-roll until sustained VFX starts (neutral derived can resist)."""
    for _ in range(max_tries):
        tip = client.max_event_id()
        client.post_debug(
            "/status/apply",
            {
                "statusId": status_id,
                "hostPtr": host_ptr,
                "amount": amount,
                "durationMs": duration_ms,
            },
        )
        if wait_fx_started(client, tip, status_id):
            return True
    return False


def clear_status_target(client: LiveClient, host_ptr: str) -> None:
    client.post_debug("/clear-status", {"ptr": host_ptr})


def poll_events_after(
    client: LiveClient,
    after_id: int,
    kinds: set[str],
    timeout_sec: float = 15.0,
) -> list[dict[str, Any]]:
    """Collect events with kind in kinds after after_id."""
    deadline = time.time() + timeout_sec
    cursor = after_id
    found: list[dict[str, Any]] = []
    while time.time() < deadline:
        items = client.events(after_id=cursor, limit=100)
        for ev in items:
            if ev.get("kind") in kinds:
                found.append(ev)
        if items:
            cursor = int(items[-1]["id"])
        if found:
            return found
        time.sleep(0.3)
    return found
