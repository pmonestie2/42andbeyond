"""Capture server-owned stage-zero cargo capacity in the Unity loadout panel."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Run only an owned temporary scout server and Editor, then release both."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    client_root = root / 'client'
    server_root = root / 'server'
    executable = server_root / 'target' / 'debug' / 'beyond-server'
    if not executable.is_file() or not args.unity.is_file():
        raise FileNotFoundError('Build the current debug server and provide the Unity Editor.')
    screenshot = client_root / 'Builds' / 'verification' / 'hull-capacity.png'
    screenshot.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix='client-hull-capacity-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state), '--create-account',
                        'local', '--character', 'ScoutPilot', '--class', 'ts'],
                       input='local\n', text=True, check=True, stdout=subprocess.DEVNULL)
        (state / 'inventory.json').write_text(json.dumps({
            'cargo': [None] * 40, 'equipped': [None] * 20,
            'hull_upgrade': 0}))
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content',
                                       str(server_root / 'data' / 'sector.json')],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable hull server failed to bind.')
                    time.sleep(.05)
                info = json.loads((state / 'server.local.json').read_text())
                if info['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                subprocess.run([str(args.unity), '-projectPath', str(client_root),
                                '-logFile', str(state / 'unity.log'),
                                '--42andbeyond-hull-capacity-smoke'], check=True, timeout=120)
            except BaseException:
                if (state / 'unity.log').exists():
                    print((state / 'unity.log').read_text(errors='replace')[-6000:])
                print((state / 'server.log').read_text(errors='replace')[-3000:])
                raise
            finally:
                (state / 'stop.request').write_text('stop\n')
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.terminate()
                    server.wait(timeout=5)
    if not screenshot.is_file():
        raise FileNotFoundError('hull-capacity.png')


if __name__ == '__main__':
    main()
