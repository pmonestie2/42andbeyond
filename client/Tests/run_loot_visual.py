"""Graphically verify a disposable MOB kill through native wreck item pickup."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Start only owned test processes and verify both tractor and cargo screenshots."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    project = root / 'client'
    server_root = root / 'server'
    executable = server_root / 'target' / 'debug' / 'beyond-server'
    if not executable.is_file() or not args.unity.is_file():
        raise FileNotFoundError('Build the disposable server and provide a Unity Editor executable.')
    with tempfile.TemporaryDirectory(prefix='client-loot-visual-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state), '--create-account',
                        'local', '--character', 'SandboxPilot', '--class', 'je'],
                       input='local\n', text=True, check=True, stdout=subprocess.DEVNULL)
        universe = json.loads((server_root / 'data' / 'universe.json').read_text())
        mob = next(mob for sector in universe for mob in sector['mobs']
                   if mob['template_id'] == 2010)
        mob.update(source_id=10, spawn=[550.0, 0.0, 0.0], spawn_region=None,
                   aggressive=False, respawn_seconds=120.0)
        sector = json.loads((server_root / 'data' / 'asteroid_belt_alpha.json').read_text())
        sector['mobs'] = [mob]
        sector['resources'] = []
        content = state / 'sector.json'
        content.write_text(json.dumps(sector))
        equipped = [None] * 20
        for item in json.loads((server_root / 'data' / 'je_loadout.json').read_text()):
            template = 3340 if item['slot'] == 1 else 1158 if item['slot'] == 3 else item['template']
            equipped[item['slot']] = {'template': template, 'quantity': 1,
                                      'quality': 1.0, 'structure': 1.0}
        (state / 'inventory.json').write_text(json.dumps({'cargo': [None] * 22,
                                                         'equipped': equipped}))
        (state / 'player.json').write_text(json.dumps({'version': 1, 'sector': sector['id'],
                                                       'position': [0.0, 0.0, 0.0],
                                                       'home_station': 10761}))
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content', str(content)],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable loot server failed to bind 127.0.0.1.')
                    time.sleep(.05)
                info = json.loads((state / 'server.local.json').read_text())
                if info['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                subprocess.run([str(args.unity), '-projectPath', str(project),
                                '-logFile', str(state / 'unity.log'),
                                '--42andbeyond-loot-effects-smoke'], check=True, timeout=150)
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
    for name in ['loot-effects.png', 'loot-collected.png']:
        if not (project / 'Builds' / 'verification' / name).is_file():
            raise FileNotFoundError(name)


if __name__ == '__main__':
    main()
