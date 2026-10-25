#!/usr/bin/env python3
"""Capture station geometry from a disposable local Unity and server session."""

import argparse
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
CLIENT = REPO / "client"
SERVER = REPO / "server"
OUTPUT = CLIENT / "Builds" / "verification"
sys.path.insert(0, str(CLIENT / "Tools"))
from play_local import stop_owned  # noqa: E402


def run(unity: Path) -> None:
    """Capture the hangar and concourse, then stop only the owned server."""
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for name in ("station-hangar.png", "station-concourse.png", "station-loadout.png"):
        (OUTPUT / name).unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="station-art-") as temporary:
        state = Path(temporary)
        binary = SERVER / "target" / "debug" / "beyond-server"
        subprocess.run(
            [str(binary), "--state-dir", str(state), "--create-account", "local",
             "--character", "SandboxPilot", "--class", "je"],
            input="local\n", text=True, check=True, stdout=subprocess.DEVNULL,
        )
        (state / "player.json").write_text(json.dumps({
            "version": 1, "sector": 10761, "position": [0.0, 0.0, 0.0],
            "home_station": 10761,
        }))
        with (OUTPUT / "station-art-server.log").open("w") as log:
            server = subprocess.Popen(
                [str(binary), "--bind", "127.0.0.1", "--state-dir", str(state)],
                stdout=log, stderr=subprocess.STDOUT,
            )
            try:
                deadline = time.monotonic() + 15
                while not (state / "server.local.json").exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Disposable station server failed to start")
                    time.sleep(.05)
                info = json.loads((state / "server.local.json").read_text())
                if info["pid"] != server.pid:
                    raise RuntimeError("Disposable server ownership mismatch")
                subprocess.run(
                    [str(unity), "-projectPath", str(CLIENT),
                     "--42andbeyond-station-art-smoke", "-logFile",
                     str(OUTPUT / "station-art-visual.log")],
                    check=True, timeout=150,
                )
            finally:
                if server.poll() is None:
                    (state / "stop.request").write_text("stop\n")
                stop_owned(server)
    for name in ("station-hangar.png", "station-concourse.png", "station-loadout.png"):
        if not (OUTPUT / name).is_file():
            raise FileNotFoundError(name)
    print("PASS: disposable station hangar, concourse, and loadout captures")


def main() -> None:
    """Parse the Unity executable and run the isolated visual check."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    args = parser.parse_args()
    run(args.unity)


if __name__ == "__main__":
    main()
