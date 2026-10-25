#!/usr/bin/env python3
"""Capture a redesigned gate against a disposable local Unity and server session."""

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
    """Capture a near server gate and stop only the server started here."""
    OUTPUT.mkdir(parents=True, exist_ok=True)
    capture = OUTPUT / "gate-art.png"
    capture.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="gate-art-") as temporary:
        state = Path(temporary)
        binary = SERVER / "target" / "debug" / "beyond-server"
        subprocess.run(
            [str(binary), "--state-dir", str(state), "--create-account", "local",
             "--character", "SandboxPilot", "--class", "je"],
            input="local\n", text=True, check=True, stdout=subprocess.DEVNULL,
        )
        sector = json.loads((SERVER / "data" / "asteroid_belt_alpha.json").read_text())
        gate = next(item for item in sector["navpoints"] if item["object_type"] == 11)
        gate["position"] = [1800.0, 0.0, 0.0]
        sector["mobs"] = []
        sector["resources"] = []
        content = state / "sector.json"
        content.write_text(json.dumps(sector))
        (state / "player.json").write_text(json.dumps({
            "version": 1, "sector": sector["id"], "position": [0.0, 0.0, 0.0],
            "home_station": 10761,
        }))
        with (OUTPUT / "gate-art-server.log").open("w") as log:
            server = subprocess.Popen(
                [str(binary), "--bind", "127.0.0.1", "--state-dir", str(state),
                 "--content", str(content)],
                stdout=log, stderr=subprocess.STDOUT,
            )
            try:
                deadline = time.monotonic() + 15
                while not (state / "server.local.json").exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Disposable gate server failed to start")
                    time.sleep(.05)
                info = json.loads((state / "server.local.json").read_text())
                if info["pid"] != server.pid:
                    raise RuntimeError("Disposable server ownership mismatch")
                subprocess.run(
                    [str(unity), "-projectPath", str(CLIENT),
                     "--42andbeyond-gate-art-smoke", "-logFile",
                     str(OUTPUT / "gate-art-visual.log")],
                    check=True, timeout=150,
                )
            finally:
                if server.poll() is None:
                    (state / "stop.request").write_text("stop\n")
                stop_owned(server)
    if not capture.is_file():
        raise FileNotFoundError(capture)
    print("PASS: disposable gate art approach capture")


def main() -> None:
    """Parse the Unity path and run the isolated gate capture."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    args = parser.parse_args()
    run(args.unity)


if __name__ == "__main__":
    main()
