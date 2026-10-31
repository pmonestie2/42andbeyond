"""Verify native two-stack wreck collection in an owned disposable Unity session."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Run an isolated server and graphical Editor, then verify a fresh capture."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    parser.add_argument('--selective', action='store_true',
                        help='Click one native item icon and leave the second wreck stack untouched.')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    project = root / 'client'
    server_root = root / 'server'
    executable = server_root / 'target' / 'debug' / 'beyond-server'
    if not executable.is_file() or not args.unity.is_file():
        raise FileNotFoundError('Build the disposable server and provide Unity Editor.')
    screenshot = project / 'Builds' / 'verification' / (
        'loot-selective-after.png' if args.selective else 'loot-all-second.png')
    screenshot.unlink(missing_ok=True)
    if args.selective:
        (project / 'Builds' / 'verification' / 'loot-selective-before.png').unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix='client-collect-all-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state), '--create-account',
                        'local', '--character', 'SandboxPilot', '--class', 'je'],
                       input='local\n', text=True, check=True, stdout=subprocess.DEVNULL)
        sector = json.loads((server_root / 'data' / 'asteroid_belt_alpha.json').read_text())
        sector['mobs'] = []
        sector['resources'] = []
        content = state / 'sector.json'
        content.write_text(json.dumps(sector))
        (state / 'player.json').write_text(json.dumps({'version': 1, 'sector': sector['id'],
                                                       'position': [0.0, 0.0, 0.0],
                                                       'home_station': 10761}))
        created_ms = int(time.time() * 1000)
        stock = [None] * 6
        for slot, template, quantity in [(0, 5931, 2), (1, 5932, 3)]:
            stock[slot] = {'template': template, 'quantity': quantity,
                           'quality': 1.0, 'structure': 1.0}
        corpse_id = 0x30000000
        corpse = {'id': corpse_id, 'death': {'spawn': 10, 'generation': 1},
                  'name': 'Ishuan Revenant Young', 'level': 1, 'killer': 0x40000001,
                  'position': [550.0, 0.0, 0.0], 'created_ms': created_ms,
                  'released': False, 'initial': {'asset': 2322, 'items': stock, 'credits': 0}}
        loot_dir = state / 'loot'
        loot_dir.mkdir()
        (loot_dir / f"{sector['id']}.json").write_text(json.dumps({
            'version': 1, 'sector': sector['id'], 'next_id': corpse_id + 1,
            'deaths': {'10': {'generation': 1, 'killed_ms': created_ms}},
            'corpses': {str(corpse_id): corpse}, 'awards': []}))
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content', str(content)],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable server failed to bind 127.0.0.1.\n' +
                                           (state / 'server.log').read_text(errors='replace')[-4000:])
                    time.sleep(.05)
                info = json.loads((state / 'server.local.json').read_text())
                if info['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                editor = subprocess.Popen([str(args.unity), '-projectPath', str(project),
                                           '-logFile', str(state / 'unity.log'),
                                           '--42andbeyond-selective-loot-smoke' if args.selective else
                                           '--42andbeyond-collect-all-smoke'])
                finish_by = time.monotonic() + 150
                completed_at = None
                try:
                    while editor.poll() is None and time.monotonic() < finish_by:
                        log = (state / 'unity.log').read_text(errors='replace') if (
                            state / 'unity.log').exists() else ''
                        expected = 'PASS: selected native wreck stack claimed' if args.selective else \
                                   'PASS: two full wreck stacks collected'
                        if expected in log and screenshot.is_file():
                            if completed_at is None:
                                completed_at = time.monotonic()
                            elif time.monotonic() - completed_at > 3:
                                # Unity can linger during graphical Editor shutdown.
                                editor.terminate()
                        time.sleep(.2)
                    if editor.poll() is None:
                        editor.terminate()
                        editor.wait(timeout=5)
                    if completed_at is None or not screenshot.is_file():
                        raise RuntimeError('Unity did not confirm two native pickups.')
                finally:
                    if editor.poll() is None:
                        editor.terminate()
                        editor.wait(timeout=5)
                print('PASS: selective native loot pickup' if args.selective else
                      'PASS: two native wreck tractors completed', 'screenshot:', screenshot)
            except BaseException:
                for name in ['server.log', 'unity.log']:
                    path = state / name
                    if path.exists():
                        print(f'{name}:\n{path.read_text(errors="replace")[-7000:]}')
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
