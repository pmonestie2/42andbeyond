"""Run the Rust server in the current terminal; Ctrl+C saves and exits."""
import argparse
import os
from pathlib import Path
import subprocess

from build import ROOT, build
from server_network import sandbox_address


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bind', default='127.0.0.1', help='Loopback or private LAN server IP')
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    address = sandbox_address(args.bind)
    binary: Path = ROOT / 'target/debug' / ('beyond-server.exe' if os.name == 'nt' else 'beyond-server') if args.no_build else build()
    process = subprocess.Popen([str(binary), '--bind', address, '--content', str(ROOT / 'data/asteroid_belt_alpha.json'), '--state-dir', str(ROOT / 'state')])
    try:
        raise SystemExit(process.wait())
    except KeyboardInterrupt:
        # The child shares this foreground console and receives Ctrl+C too.
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=5)


if __name__ == '__main__':
    main()
