"""Capture native prospect and ore tractor overlap on an owned Unity client."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time


def main() -> None:
    """Seed a temporary ore field and stop only this check's Editor and server."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    client_root = root / 'client'
    server_root = root / 'server'
    executable = server_root / 'target' / 'debug' / 'beyond-server'
    if not executable.is_file() or not args.unity.is_file():
        raise FileNotFoundError('Build the debug server and provide the Unity Editor executable.')
    with tempfile.TemporaryDirectory(prefix='client-mining-overlap-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state), '--create-account',
                        'local', '--character', 'SandboxPilot', '--class', 'je'],
                       input='local\n', text=True, check=True, stdout=subprocess.DEVNULL)
        shutil.copy2(server_root / 'data' / 'sandbox_loadout.json', state / 'sandbox_loadout.json')
        sector = json.loads((server_root / 'data' / 'sector.json').read_text())
        resource_sector = json.loads((server_root / 'data' / 'asteroid_belt_alpha.json').read_text())
        resource = next(node for node in resource_sector['resources']
                        if node['level'] == 1 and len(node['ore_items']) >= 2)
        resource['position'] = [650.0, 0.0, 0.0]
        sector['resources'] = [resource]
        sector['mobs'] = []
        content = state / 'sector.json'
        content.write_text(json.dumps(sector))
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content', str(content)],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable mining server failed to bind 127.0.0.1.')
                    time.sleep(.05)
                info = json.loads((state / 'server.local.json').read_text())
                if info['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                subprocess.run([str(args.unity), '-projectPath', str(client_root),
                                '-logFile', str(state / 'unity.log'),
                                '--42andbeyond-mining-overlap-smoke'], check=True, timeout=140)
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
    if not (client_root / 'Builds' / 'verification' / 'mining-overlap.png').is_file():
        raise FileNotFoundError('mining-overlap.png')


if __name__ == '__main__':
    main()
