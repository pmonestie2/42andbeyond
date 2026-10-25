#!/usr/bin/env python3
"""Capture a new Terran Scout picker at standard or compact Game View size."""

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
    """Capture the native character list, hosted station, and first launch."""
    binary = SERVER / "target" / "debug" / "beyond-server"
    if not binary.is_file() or not unity.is_file():
        raise FileNotFoundError("Build the disposable server and provide Unity Editor.")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    suffix = "-compact" if compact else ""
    captures = [OUTPUT / f"new-pilot-{scene}{suffix}.png"
                for scene in ("roster", "station", "space")]
    for capture in captures:
        capture.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="roster-visual-") as temporary:
        state = Path(temporary)
        sector = next(entry for entry in json.loads(
            (SERVER / "data" / "universe.json").read_text()) if entry["id"] == 1020)
        sector["mobs"] = []
        sector["resources"] = []
        content = state / "high_earth.json"
        content.write_text(json.dumps(sector))
        subprocess.run([str(binary), "--state-dir", str(state), "--content", str(content),
                        "--create-account", "rookie", "--character", "Rookie",
                        "--class", "ts", "--native-progression"],
                       input="rookie\n", text=True, check=True, stdout=subprocess.DEVNULL)
        with (state / "server.log").open("w") as output:
            server = subprocess.Popen([str(binary), "--bind", "127.0.0.1",
                                       "--state-dir", str(state), "--content", str(content)],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / "server.local.json").exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Disposable roster server failed to start: " +
                                           (state / "server.log").read_text(errors="replace")[-2500:])
                    time.sleep(.05)
                info = json.loads((state / "server.local.json").read_text())
                if info["pid"] != server.pid:
                    raise RuntimeError("Disposable server ownership mismatch.")
                command = [str(unity), "-projectPath", str(CLIENT),
                           "-logFile", str(OUTPUT / "roster-visual.log"),
                           "--42andbeyond-roster-smoke"]
                if compact:
                    command.append("--42andbeyond-roster-compact")
                subprocess.run(command, check=True, timeout=150)
            finally:
                if server.poll() is None:
                    (state / "stop.request").write_text("stop\n")
                stop_owned(server)
    missing = [str(path) for path in captures if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing native roster captures: " + ", ".join(missing))
    print("PASS: native Scout roster, station and launch captured", suffix)


def main() -> None:
    """Parse the requested Editor and Game View size."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--compact", action="store_true")
    args = parser.parse_args()
    run(args.unity, args.compact)


if __name__ == "__main__":
    main()
