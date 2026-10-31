"""Verify original chat requests over two connections to an owned disposable server."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import time

from build import ROOT
from server_network import registered
from smoke_test import connect
from smoke_trade import pump


def request(owner: int, kind: int, recipient: str, channel: str, message: str, *, formatted: bool = False) -> bytes:
    """Returns:
        bytes: Native low-24-bit-owner request, optionally with the Windows formatted trailer.
    """
    body = struct.pack('<II', owner & 0x00ff_ffff, kind)
    for value in (recipient, channel, message):
        encoded = value.encode('utf-8')
        body += struct.pack('<H', len(encoded)) + encoded
    if formatted:
        text = message.encode('utf-8') + bytes(1)
        presentation = bytes([1, 0x49]) + struct.pack('<H', len(text)) + text + struct.pack('<I', 8)
        return body + struct.pack('<I', len(presentation)) + presentation
    return body + bytes(4)


def exercise(binary: Path) -> None:
    """Exercise subscriptions, channel delivery and private reply without accessing live accounts."""
    with tempfile.TemporaryDirectory(prefix='42-chat-smoke-') as temporary:
        state = Path(temporary) / 'state'
        for name in ('alice', 'bob'):
            subprocess.run([str(binary), '--state-dir', str(state), '--create-account', name,
                '--character', name.title(), '--native-progression'], input='example\n',
                text=True, capture_output=True, check=True)
            (state / 'accounts' / name / 'player.json').write_text(json.dumps({
                'version': 1, 'sector': 1076, 'position': [0., 0., 0.], 'home_station': 10761}))
        with (Path(temporary) / 'server.log').open('wb') as output:
            server = subprocess.Popen([str(binary), '--state-dir', str(state), '--bind', '127.0.0.1',
                '--content', str(ROOT / 'data/sector.json')], stdout=output, stderr=output)
            sessions = []
            try:
                deadline = time.monotonic() + 15
                while not registered('127.0.0.1'):
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable chat server did not start')
                    time.sleep(.1)
                sessions = [connect('127.0.0.1', name, 'example', name.title()) for name in ('alice', 'bob')]
                a, b = [session.ticket.player_id for session in sessions]
                observed = []
                sessions[1].send(0xa3, request(b, 6, '', 'General Chat', ''))
                # Sender echo provides a barrier for Bob's subscription before Alice sends.
                sessions[1].send(0xa3, request(b, 0, '', 'General Chat', 'Subscribed'))
                pump(sessions, observed, lambda: any(i == 1 and f.opcode == 0xa5 for i, f in observed))
                observed.clear()
                sessions[0].send(0xa3, request(a, 0, '', 'General Chat', 'Hello legacy', formatted=True))
                pump(sessions, observed, lambda: all(any(i == side and f.opcode == 0xa5 and b'Hello legacy' in f.body
                    for i, f in observed) for side in (0, 1)))
                print('PASS: native 0xa3 subscriptions and 0xa5 channel events reach both pilots', flush=True)
                observed.clear()
                sessions[0].send(0xa3, request(a, 1, 'Bob', '', 'Private hello', formatted=True))
                pump(sessions, observed, lambda: all(any(i == side and f.opcode == 0xa5 and b'Private hello' in f.body
                    for i, f in observed) for side in (0, 1)))
                observed.clear()
                text = b'/reply Hello back\0'
                sessions[1].send(0x33, struct.pack('<IBH', b, 4, len(text)) + text)
                pump(sessions, observed, lambda: any(i == 0 and f.opcode == 0xa5 and b'Hello back' in f.body for i, f in observed))
                print('PASS: native private messages and slash replies use channel-aware packets', flush=True)
            finally:
                for session in sessions:
                    session.close()
                if server.poll() is None:
                    (state / 'stop.request').write_text('stop\n')
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.terminate(); server.wait(timeout=5)


def main() -> None:
    """Select the already-built server and run the isolated chat check."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', required=True, type=Path)
    arguments = parser.parse_args()
    exercise(arguments.binary.resolve())


if __name__ == '__main__':
    main()
