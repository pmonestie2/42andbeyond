"""Capture four native ally buffs together on one owned disposable Unity ship."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Seed three disposable pilots and verify a fresh graphical buff capture."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    server_root, project = root / 'server', root / 'client'
    executable = server_root / 'target' / 'debug' / 'beyond-server'
    if not executable.is_file() or not args.unity.is_file():
        raise FileNotFoundError('Build the disposable server and provide Unity Editor.')
    screenshot = project / 'Builds' / 'verification' / 'concurrent-buffs.png'
    screenshot.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix='client-concurrent-buffs-') as temporary:
        state = Path(temporary)
        for account, character, class_code in [('local', 'Recipient', 'je'),
                                               ('peer', 'Explorer', 'je'),
                                               ('shield', 'Sentinel', 'ps')]:
            subprocess.run([str(executable), '--state-dir', str(state),
                            '--create-account', account, '--character', character,
                            '--class', class_code], input=account + '\n', text=True,
                           check=True, stdout=subprocess.DEVNULL)
        loadout = json.loads((server_root / 'data' / 'sandbox_loadout.json').read_text())
        for item in loadout['by_class']['je']:
            if item['slot'] == 12:
                item['template'] = 1813
            if item['slot'] == 13:
                item['template'] = 2172
            if item['slot'] == 14:
                item['template'] = 1945
        for item in loadout['by_class']['ps']:
            if item['slot'] == 12:
                item['template'] = 1805
        (state / 'sandbox_loadout.json').write_text(json.dumps(loadout))
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content',
                                       str(server_root / 'data' / 'sector.json')],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable buff server failed to bind 127.0.0.1.')
                    time.sleep(.05)
                if json.loads((state / 'server.local.json').read_text())['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                editor = subprocess.Popen([str(args.unity), '-projectPath', str(project),
                                           '-logFile', str(state / 'unity.log'),
                                           '--42andbeyond-concurrent-buff-smoke'])
                completed_at = None
                try:
                    finish_by = time.monotonic() + 170
                    while editor.poll() is None and time.monotonic() < finish_by:
                        log = (state / 'unity.log').read_text(errors='replace') if (
                            state / 'unity.log').exists() else ''
                        if 'PASS: four recipient casts and one self cast' in log and screenshot.is_file():
                            if completed_at is None:
                                completed_at = time.monotonic()
                            elif time.monotonic() - completed_at > 3:
                                editor.terminate()
                        time.sleep(.2)
                    if editor.poll() is None:
                        editor.terminate()
                        editor.wait(timeout=5)
                    if completed_at is None or not screenshot.is_file():
                        raise RuntimeError('Unity did not capture four concurrent recipient buffs.')
                finally:
                    if editor.poll() is None:
                        editor.terminate()
                        editor.wait(timeout=5)
                print('PASS: native four-buff capture:', screenshot)
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
