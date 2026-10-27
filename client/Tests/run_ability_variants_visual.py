"""Capture the grouped Skills and quickbar variant UI on a disposable PW server."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Launch an owned local server and Unity Editor for a focused UI capture."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    executable = root / 'server/target/debug/beyond-server'
    project = root / 'client'
    if not executable.is_file() or not options.unity.is_file():
        raise FileNotFoundError('Build the debug server and provide the Unity Editor executable.')
    with tempfile.TemporaryDirectory(prefix='ability-variants-') as temporary:
        state = Path(temporary)
        subprocess.run([str(executable), '--state-dir', str(state), '--seed-demo-accounts'],
                       check=True, stdout=subprocess.DEVNULL)
        (state / 'accounts/ppw/player.json').write_text(json.dumps({
            'version': 1, 'sector': 1076, 'position': [0., 0., 0.],
            'home_station': 10761,
        }))
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state)], stdout=output,
                                      stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable Skills server failed to bind.')
                    time.sleep(.05)
                record = json.loads((state / 'server.local.json').read_text())
                if record['pid'] != server.pid:
                    raise RuntimeError('Disposable server ownership mismatch.')
                unity_log = state / 'unity.log'
                subprocess.run([str(options.unity), '-projectPath', str(project),
                                '-logFile', str(unity_log),
                                '--42andbeyond-ability-variants-smoke'],
                               check=True, timeout=125)
                if 'PASS: native Anger variants grouped' not in unity_log.read_text(
                        errors='replace'):
                    raise RuntimeError('Unity did not confirm the Skills UI capture.')
            except BaseException:
                for name in ('server.log', 'unity.log'):
                    path = state / name
                    if path.exists():
                        print(f'{name}:\n{path.read_text(errors="replace")[-5000:]}')
                raise
            finally:
                (state / 'stop.request').write_text('stop\n')
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.terminate()
                    server.wait(timeout=5)
    screenshot = project / 'Builds/verification/ability-variants.png'
    if not screenshot.is_file():
        raise FileNotFoundError(screenshot)
    print(f'PASS: {screenshot}')


if __name__ == '__main__':
    main()
