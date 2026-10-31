"""Verify group visibility and pilot Assist through a disposable two-client Unity scene."""

from __future__ import annotations

import argparse
from pathlib import Path
import subprocess
import tempfile
import time


def main() -> None:
    """Create two owned pilots, capture the UI, and stop only the disposable server."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    server_root = root / 'server'
    client_root = root / 'client'
    executable = server_root / 'target/debug/beyond-server'
    if not executable.is_file() or not options.unity.is_file():
        raise FileNotFoundError('Build the debug server and pass a Unity Editor executable.')
    capture = client_root / 'Builds/verification/group-assist.png'
    capture.unlink(missing_ok=True)
    target_capture = client_root / 'Builds/verification/group-target-assist.png'
    target_capture.unlink(missing_ok=True)
    compact_capture = client_root / 'Builds/verification/streamlined-flight-1280.png'
    compact_capture.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix='group-assist-visual-') as temporary:
        state = Path(temporary)
        for account, character, password in [('local', 'Alice', 'local'), ('peer', 'Bob', 'pass')]:
            subprocess.run([str(executable), '--state-dir', str(state), '--create-account',
                            account, '--character', character], input=password + '\n',
                           text=True, check=True, stdout=subprocess.DEVNULL)
        with (state / 'server.log').open('w') as output:
            server = subprocess.Popen([str(executable), '--bind', '127.0.0.1',
                                       '--state-dir', str(state), '--content',
                                       str(server_root / 'data/sector.json')],
                                      stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 15
                while not (state / 'server.local.json').exists():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable group server failed to bind.')
                    time.sleep(.05)
                subprocess.run([str(options.unity), '-projectPath', str(client_root),
                                '-logFile', str(state / 'unity.log'),
                                '--42andbeyond-group-assist-smoke'], check=True, timeout=150)
                if not capture.exists() or not target_capture.exists() or not compact_capture.exists():
                    raise RuntimeError('Unity did not capture compact flight and both Assist controls.')
                print('PASS: flight and group Assist screenshots', compact_capture, capture, target_capture)
            except BaseException:
                for name in ('unity.log', 'server.log'):
                    path = state / name
                    if path.exists():
                        print(name + ':\n' + path.read_text(errors='replace')[-5000:])
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
