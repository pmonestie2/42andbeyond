"""Capture an isolated native missile, shield hit, hull hit, and wreck breakup."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Run only owned local test processes against disposable combat state."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    project = root / 'client'
    server_root = root / 'server'
    executable = server_root / 'target/debug/beyond-server'
    if not executable.is_file() or not args.unity.is_file():
        raise FileNotFoundError('Build the disposable server and provide a Unity Editor.')
    names = ('combat-missile.png', 'combat-shield.png', 'combat-hull.png',
             'combat-breakup.png')
    screenshots = [project / 'Builds/verification' / name for name in names]
    for screenshot in screenshots:
        screenshot.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix='client-combat-moment-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state), '--create-account',
                        'local', '--character', 'SandboxPilot', '--class', 'te'],
                       input='local\n', text=True, check=True, stdout=subprocess.DEVNULL)
        universe = json.loads((server_root / 'data/universe.json').read_text())
        mob = next(actor.copy() for sector in universe for actor in sector['mobs']
                   if actor['template_id'] == 2010)
        mob.update(source_id=10, spawn=[650.0, -230.0, 0.0], spawn_region=None,
                   aggressive=False, aggression=0, respawn_seconds=120.0,
                   combat_override={'hull': 180.0, 'shield': 100.0, 'dps': 2.5,
                                    'reload': 2.0, 'range': 1000.0, 'effect': 35,
                                    'damage_type': 3, 'variance': False})
        sector = json.loads((server_root / 'data/asteroid_belt_alpha.json').read_text())
        sector['mobs'] = [mob]
        sector['resources'] = []
        content = state / 'sector.json'
        content.write_text(json.dumps(sector))
        def stock(template: int, quantity: int = 1) -> dict[str, float | int]:
            """Returns:
                dict[str, float | int]: One durable test item with native quality fields.
            """
            return {'template': template, 'quantity': quantity,
                    'quality': 1.0, 'structure': 1.0}
        mounted = [None] * 20
        for slot, template in ((0, 3628), (1, 3184), (2, 2507), (3, 2656)):
            mounted[slot] = stock(template)
        ammunition = [None] * 20
        ammunition[3] = stock(507, 20)
        (state / 'inventory.json').write_text(json.dumps({
            'cargo': [None] * 22, 'equipped': mounted, 'ammunition': ammunition}))
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
                        raise RuntimeError('Disposable combat server failed to bind.')
                    time.sleep(.05)
                info = json.loads((state / 'server.local.json').read_text())
                if info['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                subprocess.run([str(args.unity), '-projectPath', str(project),
                                '-logFile', str(state / 'unity.log'),
                                '--42andbeyond-combat-moment-smoke'],
                               check=True, timeout=200)
            except BaseException:
                if (state / 'unity.log').exists():
                    print((state / 'unity.log').read_text(errors='replace')[-6000:])
                if (state / 'server.log').exists():
                    print((state / 'server.log').read_text(errors='replace')[-2500:])
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
            raise FileNotFoundError(screenshot.name)


if __name__ == '__main__':
    main()
