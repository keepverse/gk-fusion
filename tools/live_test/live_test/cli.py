"""CLI: doctor | deploy | list | run | monitor."""

from __future__ import annotations

import argparse
import sys
import time

from live_test.client import LiveClient
from live_test.deploy import deploy_melon, ensure_server
from live_test.lawn import ensure_lawn
from live_test.report import Report
from live_test.scenarios import REGISTRY, RunContext, list_scenarios

# Register packs
import live_test.scenarios.shield  # noqa: F401
import live_test.scenarios.lab  # noqa: F401
import live_test.scenarios.combat  # noqa: F401
import live_test.scenarios.status  # noqa: F401
import live_test.scenarios.vfx  # noqa: F401
import live_test.scenarios.stress  # noqa: F401


def cmd_doctor(args: argparse.Namespace) -> int:
    c = LiveClient(args.base_url)
    print("== doctor ==")
    try:
        h = c.health()
    except RuntimeError as e:
        print(f"  FAIL server: {e}")
        return 1
    print(f"  ok={h.get('ok')} injectorConnected={h.get('injectorConnected')} sim={h.get('simEnabled')} source={h.get('source')}")
    tip = c.max_event_id()
    print(f"  event tip={tip}")
    if h.get("simEnabled"):
        print("  WARN simEnabled=true — LIVE prefers SIM off")
    if not h.get("injectorConnected"):
        print("  WARN injector not connected — deploy --launch and enter Adventure lawn")
        return 2
    return 0


def cmd_deploy(args: argparse.Namespace) -> int:
    c = LiveClient(args.base_url)
    print("== deploy ==")
    ensure_server(c, start_if_down=True)
    rc = deploy_melon(launch=args.launch, no_server=True)
    if rc != 0:
        return rc
    if args.launch:
        print("  waiting for injector…")
        for _ in range(60):
            try:
                if c.health().get("injectorConnected"):
                    print("  injectorConnected=true")
                    break
            except RuntimeError:
                pass
            time.sleep(1)
        else:
            print("  WARN injector not connected yet — finish Melon boot / enter lawn")
    return 0


def cmd_list(_: argparse.Namespace) -> int:
    for name in list_scenarios():
        print(name)
    return 0


def cmd_run(args: argparse.Namespace) -> int:
    c = LiveClient(args.base_url)
    name = args.scenario
    if name not in REGISTRY:
        print(f"unknown scenario: {name}")
        print("known:", ", ".join(list_scenarios()))
        return 2
    print(f"== run {name} ==")
    try:
        h = c.health()
        if not h.get("injectorConnected"):
            print("injector not connected")
            return 2
    except RuntimeError as e:
        print(e)
        return 1
    ctx = RunContext(
        client=c,
        force_setup=args.force_setup,
        amount=args.amount,
        enter_level=args.enter_level,
        target_ptr=args.target_ptr,
    )
    report = Report(name)
    try:
        if args.wait_lawn and not name.startswith("stress."):
            ensure_lawn(c, enter_level=args.enter_level, wait_sec=args.wait_sec)
        REGISTRY[name](ctx, report)
    except Exception as e:
        report.check(False, str(e))
    return report.summary()


def cmd_monitor(args: argparse.Namespace) -> int:
    c = LiveClient(args.base_url)
    print(f"== monitor {args.what} every {args.interval}s (Ctrl+C) ==")
    try:
        while True:
            if args.what == "bar-status":
                tip = c.max_event_id()
                c.post_debug("/shield/bar-status", {})
                ev = c.wait_kind(tip, "debug.shield.bar-status", timeout_sec=8)
                p = c.payload(ev) or {}
                print(
                    f"  data={p.get('dataOwners')} world={p.get('worldBars')} "
                    f"fill={p.get('fillRatio')} true={p.get('trueRatio')} "
                    f"display={p.get('displayRatio')} early={(p.get('lastDraw') or {}).get('early')}"
                )
            elif args.what == "health":
                h = c.health()
                print(f"  injector={h.get('injectorConnected')} tip={c.max_event_id()}")
            else:
                print(f"unknown monitor target: {args.what}")
                return 2
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\nstopped")
        return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="live_test", description="FusionRpg LIVE harness")
    p.add_argument("--base-url", default="http://127.0.0.1:5088")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("doctor", help="Health + injector + event tip")
    d = sub.add_parser("deploy", help="Melon deploy-play -NoServer")
    d.add_argument("--launch", action="store_true", help="Launch game if not running")
    d.add_argument("--no-launch", action="store_true", help="Injector only")
    sub.add_parser("list", help="List scenario ids")

    r = sub.add_parser("run", help="Run a named scenario")
    r.add_argument("scenario")
    r.add_argument("--force-setup", action="store_true")
    r.add_argument("--enter-level", action="store_true")
    r.add_argument("--wait-lawn", action="store_true", default=True)
    r.add_argument("--no-wait-lawn", action="store_false", dest="wait_lawn")
    r.add_argument("--wait-sec", type=float, default=180.0)
    r.add_argument("--amount", type=int, default=-150)
    r.add_argument("--target-ptr", default=None)

    m = sub.add_parser("monitor", help="Poll bar-status or health")
    m.add_argument("what", nargs="?", default="bar-status", choices=["bar-status", "health"])
    m.add_argument("--interval", type=float, default=1.0)
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.cmd == "doctor":
        return cmd_doctor(args)
    if args.cmd == "deploy":
        if args.no_launch:
            args.launch = False
        elif not hasattr(args, "launch") or args.launch is False:
            # default: launch when deploy
            args.launch = True
        return cmd_deploy(args)
    if args.cmd == "list":
        return cmd_list(args)
    if args.cmd == "run":
        return cmd_run(args)
    if args.cmd == "monitor":
        return cmd_monitor(args)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
