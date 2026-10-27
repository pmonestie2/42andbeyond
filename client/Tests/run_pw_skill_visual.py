"""Check PW shield abilities in Unity against an owned disposable server."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Run the targeted Unity check without touching live state or processes."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    project = root / 'client'
    server_root = root / 'server'
    executable = server_root / 'target/debug/beyond-server'
    if not executable.is_file() or not options.unity.is_file():
        raise FileNotFoundError('Build the debug server and provide the Unity Editor executable.')
    screenshots = [project / 'Builds/verification' / name for name in
                   ('pw-sapping-sphere.png', 'pw-shield-nova.png')]
    for screenshot in screenshots:
        screenshot.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix='pw-skill-visual-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state),
                        '--seed-demo-accounts'], text=True,
                       check=True, stdout=subprocess.DEVNULL)
        account = state / 'accounts/ppw'
        inventory_path = account / 'inventory.json'
        inventory = json.loads(inventory_path.read_text())
        inventory['equipped'][1]['template'] = 3349
        inventory_path.write_text(json.dumps(inventory))
        (account / 'player.json').write_text(json.dumps({
            'version': 1, 'sector': 1076, 'position': [0., 0., 0.],
            'home_station': 10761,
        }))
        sector = json.loads((server_root / 'data/sector.json').read_text())
        mob = sector['mobs'][0]
        mob.update(spawn=[3_000., 0., 0.], radius=835., aggressive=False,
                   faction=0, respawn_seconds=120.)
        mob['combat_override'].update(hull=100_000., shield=100_000., dps=.1)
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
                        raise RuntimeError('Disposable PW server failed to bind.')
                    time.sleep(.05)
                record = json.loads((state / 'server.local.json').read_text())
                if record['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                subprocess.run([str(options.unity), '-projectPath', str(project),
                                '-logFile', str(state / 'unity.log'),
                                '--42andbeyond-pw-skill-smoke'], check=True, timeout=125)
                log = (state / 'unity.log').read_text(errors='replace')
                if 'PASS: PW Group Sapping Sphere' not in log:
                    raise RuntimeError('Unity did not confirm both PW skill visuals.')
                print('PASS: PW shield abilities reached the selected MOB in Unity.')
            except BaseException:
                for name in ('server.log', 'unity.log'):
                    path = state / name
                    if path.exists():
                        print(f'{name}:\n{path.read_text(errors="replace")[-4500:]}')
                raise
            finally:
                (state / 'stop.request').write_text('stop\n')
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.terminate()
                    server.wait(timeout=5)
    for screenshot in screenshots:
        if not screenshot.is_file():
            raise FileNotFoundError(screenshot)


if __name__ == '__main__':
    main()
