"""Deploy Melon injector via deploy-play.py; optionally ensure server process."""

from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path

from live_test.client import LiveClient


def repo_root() -> Path:
    return Path(__file__).resolve().parents[3]


def ensure_server(client: LiveClient, start_if_down: bool = True) -> None:
    try:
        h = client.health()
        if h.get("ok"):
            print(f"  server ok injectorConnected={h.get('injectorConnected')}")
            return
    except RuntimeError:
        pass
    if not start_if_down:
        raise RuntimeError("server down and start_if_down=False")
    exe = repo_root() / "dist" / "FusionRpg.Server" / "FusionRpg.Server.exe"
    if not exe.is_file():
        raise RuntimeError(f"server exe missing: {exe} — publish first or start manually")
    print(f"  starting {exe}")
    subprocess.Popen(
        [str(exe)],
        cwd=str(exe.parent),
        creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
    )
    for _ in range(30):
        time.sleep(1)
        try:
            if client.health().get("ok"):
                print("  server up")
                return
        except RuntimeError:
            continue
    raise RuntimeError("server did not answer /health")


def deploy_melon(launch: bool = True, no_server: bool = True) -> int:
    root = repo_root()
    ml = os.environ.get("FUSIONRPG_ML_GAMEDIR")
    if not ml:
        default = r"H:\Games\PVZ-Fusion-3.9_MelonLoader"
        if Path(default).is_dir():
            os.environ["FUSIONRPG_ML_GAMEDIR"] = default
            ml = default
            print(f"  FUSIONRPG_ML_GAMEDIR defaulted to {ml}")
        else:
            raise RuntimeError("set FUSIONRPG_ML_GAMEDIR to Melon game pack")
    script = root / "scripts" / "deploy-play.py"
    args = [
        sys.executable,
        str(script),
        "--loader-host",
        "MelonLoader",
    ]
    if no_server:
        args.append("--no-server")
    if not launch:
        args.append("--no-game")
    print("  " + " ".join(args))
    proc = subprocess.run(args, cwd=str(root))
    return proc.returncode
