"""Capture a native two-pilot Scan Boost on an owned disposable Unity client."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Create two test pilots, run the graphical check, and stop only owned processes."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    server_root = root / 'server'
    client_root = root / 'client'
    executable = server_root / 'target' / 'debug' / 'beyond-server'
    if not executable.is_file() or not args.unity.is_file():
        raise FileNotFoundError('Build the disposable server and provide a Unity Editor executable.')
    with tempfile.TemporaryDirectory(prefix='peer-scan-visual-') as temporary:
        state = Path(temporary)
        for account, character, password in [('local', 'SandboxPilot', 'local'),
                                             ('peer', 'Visitor', 'pass')]:
            subprocess.run([str(executable), '--state-dir', str(state),
                            '--create-account', account, '--character', character],
                           input=password + '\n', text=True, check=True)
        loadout = json.loads((server_root / 'data' / 'sandbox_loadout.json').read_text())
        for equipment in (loadout['equipment'], loadout['by_class']['je']):
            for item in equipment:
                if item['slot'] == 12:
                    item['template'] = 1813
        (state / 'sandbox_loadout.json').write_text(json.dumps(loadout))
        server_log = state / 'server.log'
        with server_log.open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content',
                                       str(server_root / 'data' / 'sector.json')],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable server failed to bind 127.0.0.1.')
                    time.sleep(.05)
                subprocess.run([str(args.unity), '-projectPath', str(client_root),
                                '-logFile', str(state / 'unity.log'),
                                '--42andbeyond-peer-scan-smoke'], check=True, timeout=130)
            except BaseException:
                if (state / 'unity.log').exists():
                    print((state / 'unity.log').read_text(errors='replace')[-6000:])
                raise
            finally:
                (state / 'stop.request').write_text('stop\n')
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.terminate()
                    server.wait(timeout=5)


if __name__ == '__main__':
    main()
