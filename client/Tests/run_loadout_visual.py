"""Exercise server-owned loadout transfers and missile consumption in Unity."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Run one owned local server and graphical Editor, then check its capture."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    parser.add_argument('--batchmode', action='store_true',
                        help='Run the owned Unity Editor in batch mode while preserving rendering.')
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    project = root / 'client'
    server_root = root / 'server'
    executable = server_root / 'target/debug/beyond-server'
    if not executable.is_file() or not options.unity.is_file():
        raise FileNotFoundError('Build the debug server and provide the Unity Editor executable.')
    with tempfile.TemporaryDirectory(prefix='loadout-visual-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state), '--create-account',
                        'local', '--character', 'SandboxPilot', '--class', 'te'],
                       input='local\n', text=True, check=True, stdout=subprocess.DEVNULL)
        universe = json.loads((server_root / 'data/universe.json').read_text())
        mob = next(actor.copy() for sector in universe for actor in sector['mobs']
                   if actor['template_id'] == 2010)
        mob.update(source_id=10, spawn=[650.0, -230.0, 0.0], spawn_region=None,
                   aggressive=True, aggression=1, respawn_seconds=120.0,
                   combat_override={'hull': 100_000.0, 'shield': 100_000.0,
                                    'dps': .1, 'reload': 2.0, 'range': 1000.0,
                                    'effect': 35, 'damage_type': 3, 'variance': False})
        sector = json.loads((server_root / 'data/asteroid_belt_alpha.json').read_text())
        sector['mobs'] = [mob]
        sector['resources'] = []
        content = state / 'sector.json'
        content.write_text(json.dumps(sector))

        def stock(template: int, quantity: int = 1) -> dict[str, float | int]:
            """Returns:
                dict[str, float | int]: One disposable native inventory item.
            """
            return {'template': template, 'quantity': quantity,
                    'quality': 1.0, 'structure': 1.0}

        cargo = [None] * 22
        cargo[0], cargo[1] = stock(507, 20), stock(5931, 3)
        mounted = [None] * 20
        for slot, template in ((0, 3628), (1, 3184), (2, 2507), (3, 2656)):
            mounted[slot] = stock(template)
        (state / 'inventory.json').write_text(json.dumps({
            'cargo': cargo, 'equipped': mounted, 'ammunition': [None] * 20}))
        (state / 'player.json').write_text(json.dumps({
            'version': 1, 'sector': sector['id'], 'position': [0.0, 0.0, 0.0],
            'home_station': 10761}))
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content', str(content)],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable loadout server failed to bind.')
                    time.sleep(.05)
                info = json.loads((state / 'server.local.json').read_text())
                if info['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                unity_log = state / 'unity.log'
                subprocess.run([str(options.unity), '-projectPath', str(project),
                                '-logFile', str(unity_log),
                                '--42andbeyond-loadout-smoke'] +
                               (['-batchmode'] if options.batchmode else []),
                               check=True, timeout=140)
                (project / 'Builds/verification/loadout-ui.log').write_text(unity_log.read_text(errors='replace'))
                if 'PASS: cargo reordered, magazine loaded' not in unity_log.read_text(
                        errors='replace'):
                    raise RuntimeError('Unity did not confirm authoritative ammo consumption.')
            except BaseException:
                for name in ('server.log', 'unity.log'):
                    path = state / name
                    if path.exists():
                        text = path.read_text(errors='replace')
                        (project / 'Builds/verification' / ('loadout-' + name)).write_text(text)
                        print(f'{name}:\n{text[-5000:]}')
                raise
            finally:
                (state / 'stop.request').write_text('stop\n')
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.terminate()
                    server.wait(timeout=5)
    screenshot = project / 'Builds/verification/loadout-ammo.png'
    if not screenshot.is_file():
        raise FileNotFoundError(screenshot)
    flight = project / 'Builds/verification/loadout-flight.png'
    if not flight.is_file():
        raise FileNotFoundError(flight)
    for name in ('loadout-drag.png', 'loadout-drag-invalid.png', 'galaxy-travel.png'):
        capture = project / 'Builds/verification' / name
        if not capture.is_file():
            raise FileNotFoundError(capture)
    print(f'PASS: {screenshot}')


if __name__ == '__main__':
    main()
