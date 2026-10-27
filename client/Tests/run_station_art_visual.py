#!/usr/bin/env python3
"""Verify one client concourse aggregates the station's intact native rooms."""

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


def run(unity: Path, compact: bool = False, motion: bool = False, batchmode: bool = False, recipes: bool = False, market: bool = False) -> None:
    """Capture the shared concourse and stop only the disposable server started here."""
    OUTPUT.mkdir(parents=True, exist_ok=True)
    suffix = "-compact" if compact else ""
    names = ["station-hangar.png", "station-concourse.png", "station-loadout.png"]
    if market:
        names.append("station-market.png")
    if motion:
        names.append("station-dialogue.png")
    for name in names:
        (OUTPUT / name.replace(".png", f"{suffix}.png")).unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="station-art-") as temporary:
        state = Path(temporary)
        binary = SERVER / "target" / "debug" / "beyond-server"
        subprocess.run(
            [str(binary), "--state-dir", str(state), "--create-account", "local",
             "--character", "SandboxPilot", "--class", "je"],
            input="local\n", text=True, check=True, stdout=subprocess.DEVNULL,
        )
        if recipes:
            subprocess.run([str(binary), "--state-dir", str(state), "--grant-build-recipes"], check=True)
        (state / "player.json").write_text(json.dumps({
            "version": 1, "sector": 17051 if market else 10761, "position": [0.0, 0.0, 0.0],
            "home_station": 17051 if market else 10761,
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
                command = [str(unity), "-projectPath", str(CLIENT),
                           "--42andbeyond-station-art-smoke"]
                if batchmode:
                    command.append("-batchmode")
                if compact:
                    command.append("--42andbeyond-station-compact")
                if motion:
                    command.append("--42andbeyond-station-motion")
                if market:
                    command.append("--42andbeyond-station-market")
                if recipes:
                    command.append("--42andbeyond-station-recipes")
                command.extend(["-logFile", str(OUTPUT / "station-art-visual.log")])
                subprocess.run(
                    command,
                    check=True, timeout=150,
                )
                if "PASS: aggregated station concourse" not in (OUTPUT / "station-art-visual.log").read_text(errors="replace"):
                    raise RuntimeError("Unity did not confirm native rooms and the aggregated client directory")
            finally:
                if server.poll() is None:
                    (state / "stop.request").write_text("stop\n")
                stop_owned(server)
    for name in names:
        capture = name.replace(".png", f"{suffix}.png")
        if not (OUTPUT / capture).is_file():
            raise FileNotFoundError(capture)
    print("PASS: native station rooms preserved, aggregated service/contact directory, service replies, and inventory capture")


def main() -> None:
    """Parse the Unity executable and run the isolated visual check."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--compact", action="store_true", help="Capture a smaller Game View")
    parser.add_argument("--motion", action="store_true", help="Capture contact dialogue response")
    parser.add_argument("--batchmode", action="store_true", help="Run Unity without its interactive Editor window")
    parser.add_argument("--recipes", action="store_true", help="Grant eligible recipes and check page two through the client")
    parser.add_argument("--market", action="store_true", help="Verify local component stock, durable buy/sell and source overlay")
    args = parser.parse_args()
    run(args.unity, compact=args.compact, motion=args.motion, batchmode=args.batchmode, recipes=args.recipes, market=args.market)


if __name__ == "__main__":
    main()
