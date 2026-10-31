"""Verify direct rank-6/7 recharge casts against an owned disposable Rust server."""

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Provision disposable pilots with sufficient reactor capacity and check support targeting."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path, required=True)
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    binary = root / "server/target/debug/beyond-server"
    if not binary.is_file() or not options.dotnet.is_file():
        raise FileNotFoundError("Build the debug server and provide the dotnet executable.")
    with tempfile.TemporaryDirectory(prefix="42-area-recharge-") as temporary:
        state = Path(temporary)
        for account, kind in [("ptt", "tt"), ("pje", "je")]:
            subprocess.run(
                [str(binary), "--state-dir", str(state), "--create-account", account,
                 "--character", account, "--class", kind],
                input=account + "\n", text=True, check=True, stdout=subprocess.DEVNULL,
            )
        (state / "sandbox_loadout.json").write_bytes(
            (root / "server/data/sandbox_loadout.json").read_bytes())
        with (state / "server.log").open("w") as output:
            server = subprocess.Popen(
                [str(binary), "--bind", "127.0.0.1", "--state-dir", str(state)],
                stdout=output, stderr=subprocess.STDOUT,
            )
            try:
                deadline = time.monotonic() + 15
                while not (state / "server.local.json").exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Owned server failed to publish its endpoint.")
                    time.sleep(.05)
                if json.loads((state / "server.local.json").read_text())["pid"] != server.pid:
                    raise RuntimeError("Local endpoint belongs to another server.")
                subprocess.run(
                    [str(options.dotnet), "run", "--project",
                     str(root / "client/Tests/ProtocolChecks.csproj"), "--no-restore",
                     "--", "--area-recharge-server", "127.0.0.1"],
                    check=True, timeout=110,
                )
            except BaseException:
                print((state / "server.log").read_text(errors="replace")[-4500:])
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
