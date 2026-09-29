"""Adventure lawn wait + optional gated enter-level."""

from __future__ import annotations

import time
from typing import Any

from live_test.client import LiveClient

BAD_LEVEL_TYPES = frozenset({"Explore", "TravelAdvanture", "Travel", "IZ"})


def _scan_kind_near_tip(client: LiveClient, kind: str, window: int = 200) -> list[dict[str, Any]]:
    """Newest-last hits for kind near the tip (never afterId=0+kinds — that is oldest page)."""
    tip = client.max_event_id()
    after = max(0, tip - window)
    items = client.events(after_id=after, limit=min(window, 500))
    return [e for e in items if e.get("kind") == kind]


def latest_board_start(client: LiveClient) -> dict[str, Any] | None:
    """Find newest board.start by walking back from tip (not afterId=0+kinds)."""
    tip = client.max_event_id()
    if tip <= 0:
        return None
    window = 400
    after = tip
    best: dict[str, Any] | None = None
    # Page backward up to ~20k events
    for _ in range(50):
        start = max(0, after - window)
        items = client.events(after_id=start, limit=window)
        if not items:
            if start == 0:
                break
            after = start
            continue
        hits = [e for e in items if e.get("kind") == "board.start"]
        if hits:
            # Within this page newest last; keep the overall newest id
            cand = hits[-1]
            if best is None or int(cand["id"]) > int(best["id"]):
                best = cand
            # If this page includes events near tip, we're done searching newer
            if int(items[-1]["id"]) >= tip - 1:
                break
            # Found a start; still check if a newer one exists closer to tip
            if int(items[-1]["id"]) >= after - 1:
                break
        after = start
        if start == 0:
            break
        window = min(500, window + 100)
    return best


def board_still_live(client: LiveClient, board_start: dict[str, Any]) -> bool:
    start_id = int(board_start["id"])
    ends = _scan_kind_near_tip(client, "board.end", window=400)
    for e in ends:
        if int(e["id"]) > start_id:
            return False
    return True


def assert_adventure_lawn(client: LiveClient) -> dict[str, Any]:
    bs = latest_board_start(client)
    if not bs:
        raise RuntimeError(
            "No board.start — open Adventure day lawn (main menu → Adventure → day), leave it running."
        )
    if not board_still_live(client, bs):
        raise RuntimeError("Last board already ended — enter a lawn again.")
    payload = client.payload(bs) or {}
    level_type = str(payload.get("levelType") or "")
    if level_type in BAD_LEVEL_TYPES:
        raise RuntimeError(f"Refusing lab on levelType={level_type} — use Adventure/Challenge day.")
    print(f"  lawn live levelType={level_type} boardLevel={payload.get('boardLevel')}")
    return payload


def try_enter_level(client: LiveClient, level_number: int = 1) -> None:
    print("  enabling DEBUG-LEVEL-ENTRY + enter-level")
    client.post_cheat_toggle("DEBUG-LEVEL-ENTRY", True)
    tip = client.max_event_id()
    client.post_debug(
        "/enter-level",
        {"levelType": 0, "levelNumber": level_number, "id": 0, "name": ""},
    )
    ev = client.wait_kind(tip, "debug.level.enter", timeout_sec=20)
    p = client.payload(ev) or {}
    if p.get("ok"):
        deadline = time.time() + 30
        while time.time() < deadline:
            items = client.events(after_id=tip, limit=50)
            for e in items:
                if e.get("kind") == "board.start":
                    print("  board.start after enter-level")
                    return
            time.sleep(0.4)
        raise RuntimeError("enter-level ok but no board.start yet — check main menu / force")
    err = str(p.get("error") or "")
    if "board already live" in err:
        print("  enter-level: board already live — using existing lawn")
        return
    raise RuntimeError(
        f"enter-level rejected: {p} — set FUSIONRPG_LEVEL_ENTRY=1 or leave gate on; see level-entry.md"
    )


def ensure_lawn(client: LiveClient, enter_level: bool = False, wait_sec: float = 180.0) -> None:
    if enter_level:
        try:
            assert_adventure_lawn(client)
            return
        except RuntimeError:
            try_enter_level(client)
            assert_adventure_lawn(client)
            return

    deadline = time.time() + wait_sec
    last_err = "waiting for Adventure lawn"
    while time.time() < deadline:
        try:
            h = client.health()
            if not h.get("injectorConnected"):
                last_err = "injector not connected"
                print(f"  {last_err}…")
                time.sleep(2)
                continue
            assert_adventure_lawn(client)
            return
        except RuntimeError as e:
            last_err = str(e)
            print(f"  {last_err}")
            time.sleep(3)
    raise RuntimeError(f"timeout waiting for lawn: {last_err}")
