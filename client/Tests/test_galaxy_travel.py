#!/usr/bin/env python3
"""Verify client-only galaxy planning and real flight using an owned loopback server."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "client/Builds/verification"
DOTNET = Path("/tmp/enb-dotnet/dotnet")
ADDRESS = "127.0.0.1"


def build_server_if_needed() -> Path:
    """Returns:
        Path: A debug server built from current baseline source, without starting it.

    Raises:
        CalledProcessError: The baseline Rust build fails.
    """
    server = ROOT / "server"
    binary = server / "target/debug/beyond-server"
    sources = [server / "Cargo.toml", server / "Cargo.lock", *server.joinpath("src").rglob("*.rs")]
    if not binary.is_file() or binary.stat().st_mtime < max(path.stat().st_mtime for path in sources):
        subprocess.run(["cargo", "build", "--manifest-path", str(server / "Cargo.toml"), "--quiet"], cwd=ROOT, check=True)
    return binary


def stop_owned(server: subprocess.Popen, state: Path) -> None:
    """Request shutdown and stop only the disposable child process created by this runner."""
    if server.poll() is not None:
        return
    (state / "stop.request").write_text("stop\n")
    try:
        server.wait(timeout=10)
    except subprocess.TimeoutExpired:
        server.terminate()
        try:
            server.wait(timeout=5)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait()


def run(pure_only: bool) -> None:
    """Run local planner checks and optionally prove normal Luna-to-Earth flight and durable docking."""
    OUTPUT.mkdir(parents=True, exist_ok=True)
    subprocess.run(["python3", "client/Tools/build_navigation_catalog.py", "--check"], cwd=ROOT, check=True)
    if not DOTNET.is_file():
        raise FileNotFoundError(f"Local .NET SDK is unavailable: {DOTNET}")
    command = [str(DOTNET), "run", "--project", str(ROOT / "client/Tests/ProtocolChecks.csproj"), "--"]
    if pure_only:
        subprocess.run([*command, "--galaxy-only"], cwd=ROOT, check=True)
        return
    binary = build_server_if_needed()
    protocol_log = OUTPUT / "galaxy-travel-protocol.log"
    server_log = OUTPUT / "galaxy-travel-server.log"
    with tempfile.TemporaryDirectory(prefix="galaxy-travel-") as temporary:
        state = Path(temporary)
        subprocess.run([str(binary), "--state-dir", str(state), "--create-account", "local",
                        "--character", "GalaxyTest", "--class", "te"],
                       input="local\n", text=True, check=True, stdout=subprocess.DEVNULL, cwd=ROOT)
        (state / "player.json").write_text(json.dumps({
            "version": 1, "sector": 10151, "position": [0.0, 0.0, 0.0], "home_station": 10151,
        }))
        with server_log.open("w") as log:
            server = subprocess.Popen([str(binary), "--bind", ADDRESS, "--state-dir", str(state), "--no-dev"],
                                      cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 20
                while not (state / "server.local.json").is_file():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError(f"Owned loopback server failed to start; see {server_log}")
                    time.sleep(.05)
                runtime = json.loads((state / "server.local.json").read_text())
                if runtime["pid"] != server.pid or server.poll() is not None:
                    raise RuntimeError("Disposable server ownership mismatch")
                with protocol_log.open("w") as output:
                    result = subprocess.run([*command, "--galaxy-server", ADDRESS], cwd=ROOT,
                                            stdout=output, stderr=subprocess.STDOUT, timeout=225)
                if result.returncode:
                    raise RuntimeError(f"Galaxy flight check failed; see {protocol_log}\n{protocol_log.read_text(errors='replace')[-6000:]}")
                final = json.loads((state / "player.json").read_text())
                if final["sector"] != 10601:
                    raise AssertionError(f"Pilot was not durably docked at Earth Station: {final}")
                (OUTPUT / "galaxy-travel-player.json").write_text(json.dumps(final, indent=2) + "\n")
            finally:
                stop_owned(server, state)
    traffic = server_log.read_text(errors="replace")
    for transition in ("10151 -> 1015", "1015 -> 1060", "1060 -> 10601"):
        if transition not in traffic:
            raise AssertionError(f"Missing normal sector transition: {transition}")
    for forbidden in ("0x203c", "0x203e", "Dev jump:", "Dev warp:"):
        if forbidden in traffic:
            raise AssertionError(f"Client-only travel used an unexpected server feature: {forbidden}")
    protocol = protocol_log.read_text(errors="replace")
    if "PASS: client-only automatic normal-flight journey arrived at Earth Station 10601" not in protocol:
        raise AssertionError("Client did not confirm automatic normal-flight completion")
    result = {"bind": ADDRESS, "owned_server_stopped": True, "arrival_sector": 10601,
              "server_galaxy_extensions": False, "developer_commands": False,
              "server_log": str(server_log), "protocol_log": str(protocol_log)}
    (OUTPUT / "galaxy-travel-result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(protocol)
    print(f"PASS: owned {ADDRESS} baseline server stopped; client-only route docked durably at 10601")


def main() -> None:
    """Parse verification scope; never connect to a VM or an externally owned server."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pure-only", action="store_true")
    run(parser.parse_args().pure_only)


if __name__ == "__main__":
    main()
