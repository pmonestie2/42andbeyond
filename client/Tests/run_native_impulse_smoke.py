"""Check Unity W/S/brake flight against one owned, disposable native server."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Create a disposable pilot and run the opt-in Editor flight check."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", type=Path, required=True)
    parser.add_argument("--experience-ui", action="store_true", help="Validate and capture the experience gauges.")
    parser.add_argument("--floating-map", action="store_true", help="Check the map alongside live flight.")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    server_root = root / "server"
    project = root / "client"
    server_binary = server_root / "target/debug/beyond-server"
    if not server_binary.is_file() or not options.unity.is_file():
        raise FileNotFoundError("Build the debug server and provide the Unity Editor executable.")
    with tempfile.TemporaryDirectory(prefix="42-native-impulse-") as temporary:
        state = Path(temporary)
        subprocess.run(
            [str(server_binary), "--state-dir", str(state), "--create-account",
             "local", "--character", "ImpulsePilot", "--class", "te"],
            input="local\n", text=True, check=True, stdout=subprocess.DEVNULL,
        )
        sector = json.loads((server_root / "data/asteroid_belt_alpha.json").read_text())
        sector["mobs"] = []
        sector["resources"] = []
        content = state / "sector.json"
        content.write_text(json.dumps(sector))
        (state / "player.json").write_text(json.dumps({
            "version": 1, "sector": sector["id"], "position": [0.0, 0.0, 0.0],
            "home_station": 10761,
        }))
        with (state / "server.log").open("w") as server_output:
            server = subprocess.Popen(
                [str(server_binary), "--bind", "127.0.0.1", "--state-dir", str(state),
                 "--content", str(content)], stdout=server_output,
                stderr=subprocess.STDOUT,
            )
            try:
                deadline = time.monotonic() + 15
                while not (state / "server.local.json").exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Owned native server did not publish its local endpoint.")
                    time.sleep(.05)
                info = json.loads((state / "server.local.json").read_text())
                if info["pid"] != server.pid:
                    raise RuntimeError("Local endpoint belongs to another server process.")
                unity_log = state / "unity.log"
                subprocess.run(
                    [str(options.unity), "-projectPath", str(project), "-logFile", str(unity_log),
                     "-batchmode", "--42andbeyond-local", "--42andbeyond-native-impulse-smoke"] +
                    (["--42andbeyond-experience-ui-smoke"] if options.experience_ui else []) +
                    (["--42andbeyond-floating-map-smoke"] if options.floating_map else []),
                    check=True, timeout=110,
                )
                report = unity_log.read_text(errors="replace")
                if "PASS: native impulse W/S/brake" not in report:
                    raise RuntimeError("Editor exited without confirming live impulse movement.")
                if options.floating_map:
                    marker = "PASS: floating map preserves"
                    if marker not in report:
                        raise RuntimeError("Editor did not confirm floating map behavior.")
                    print(next(line for line in report.splitlines() if marker in line))
                if options.experience_ui:
                    marker = "PASS: three native XP gauges"
                    if marker not in report:
                        raise RuntimeError("Editor exited without confirming experience HUD telemetry and bounds.")
                    print(next(line for line in report.splitlines() if marker in line))
                print(next(line for line in report.splitlines() if "PASS: native impulse W/S/brake" in line))
            except BaseException:
                for name in ("server.log", "unity.log"):
                    path = state / name
                    if path.exists():
                        print(f"{name}:\n{path.read_text(errors='replace')[-4000:]}")
                raise
            finally:
                (state / "stop.request").write_text("stop\n")
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.terminate()
                    server.wait(timeout=5)


if __name__ == "__main__":
    main()
