#!/usr/bin/env python3
"""Capture the native Sharim mission offer and journal in a disposable station."""

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


def run(unity: Path, compact: bool) -> None:
    """Run one isolated native mission offer and verify three fresh screenshots."""
    binary = SERVER / "target" / "debug" / "beyond-server"
    if not binary.is_file() or not unity.is_file():
        raise FileNotFoundError("Build the disposable server and provide Unity Editor.")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    suffix = "-compact" if compact else ""
    captures = [OUTPUT / f"mission81-{scene}{suffix}.png"
                for scene in ("offer", "objective", "factions")]
    for capture in captures:
        capture.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="mission81-visual-") as temporary:
        state = Path(temporary)
        subprocess.run([str(binary), "--state-dir", str(state), "--create-account",
                        "local", "--character", "SeekerPilot", "--class", "js"],
                       input="local\n", text=True, check=True, stdout=subprocess.DEVNULL)
        sector = next(entry for entry in json.loads(
            (SERVER / "data" / "universe.json").read_text()) if entry["id"] == 1040)
        sector["mobs"] = []
        sector["resources"] = []
        sector["navpoints"] = [point for point in sector["navpoints"]
                               if point.get("source_id") == 3912]
        content = state / "ganymede.json"
        content.write_text(json.dumps(sector))
        (state / "player.json").write_text(json.dumps({
            "version": 1, "sector": 10401, "position": [0.0, 0.0, 0.0],
            "home_station": 10401,
        }))
        with (state / "server.log").open("w") as output:
            server = subprocess.Popen([str(binary), "--bind", "127.0.0.1",
                                       "--state-dir", str(state), "--content", str(content)],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / "server.local.json").exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Disposable mission server failed to start: " +
                                           (state / "server.log").read_text(errors="replace")[-2500:])
                    time.sleep(.05)
                info = json.loads((state / "server.local.json").read_text())
                if info["pid"] != server.pid:
                    raise RuntimeError("Disposable server ownership mismatch.")
                command = [str(unity), "-projectPath", str(CLIENT),
                           "-logFile", str(OUTPUT / "mission81-visual.log"),
                           "--42andbeyond-mission81-smoke"]
                if compact:
                    command.append("--42andbeyond-mission81-compact")
                subprocess.run(command, check=True, timeout=150)
            finally:
                if server.poll() is None:
                    (state / "stop.request").write_text("stop\n")
                stop_owned(server)
    missing = [str(path) for path in captures if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing native mission captures: " + ", ".join(missing))
    print("PASS: native Sharim offer, journal, and factions captured", suffix)


def main() -> None:
    """Parse the requested Editor and Game View size."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--compact", action="store_true")
    args = parser.parse_args()
    run(args.unity, args.compact)


if __name__ == "__main__":
    main()
