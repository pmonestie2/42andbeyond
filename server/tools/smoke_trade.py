"""Exercise two authenticated traders against an owned disposable server only."""
from __future__ import annotations

import argparse
from collections.abc import Callable
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import time

from build import ROOT
from server_network import registered, sandbox_address
from smoke_test import connect
from smoke_multiplayer import target_verb_enabled
from beyond_protocol.session import SpaceSession
from beyond_protocol.wire import Frame


def pump(sessions: list[SpaceSession], observed: list[tuple[int, Frame]],
         predicate: Callable[[], bool], timeout: float = 5.) -> None:
    """Poll both owned clients until a wire or durable state condition holds.

    Raises:
        TimeoutError: The expected trade transition did not arrive.
    """
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for index, session in enumerate(sessions):
            observed.extend((index, frame) for frame in session.poll())
        if predicate():
            return
    raise TimeoutError('Disposable player trade did not reach its expected state')


def action(session: SpaceSession, code: int, peer: int, credits: int = 0) -> None:
    """Send the original client Action packet without inventing a trade extension."""
    session.send(0x2c, struct.pack('<4I', session.ticket.player_id, code, peer, credits))


def control(observed: list[tuple[int, Frame]], recipient: int, code: int) -> bool:
    """Returns:
        bool: Whether this recipient received the native trade control code.
    """
    return any(index == recipient and frame.opcode == 0x1f and
               len(frame.body) == 5 and frame.body[4] == code
               for index, frame in observed)


def exercise(binary: Path, host: str) -> None:
    """Verify offers, mutual confirmation, durable values and cancellation over real sockets."""
    with tempfile.TemporaryDirectory(prefix='42-trade-smoke-') as folder:
        state = Path(folder) / 'state'
        for side, name in enumerate(('alice', 'bob')):
            subprocess.run([str(binary), '--state-dir', str(state), '--create-account', name,
                '--character', name.title(), '--class', 'je', '--native-progression'],
                input='example\n', text=True, capture_output=True, check=True)
            directory = state / 'accounts' / name
            inventory = json.loads((directory / 'inventory.json').read_text())
            inventory['cargo'] = [None] * len(inventory['cargo'])
            inventory['cargo'][0] = {'template': 5477 if side == 0 else 3818,
                'quantity': 10, 'structure': 1., 'quality': 1., 'builder_name': ''}
            inventory['credits'] = 1000
            (directory / 'inventory.json').write_text(json.dumps(inventory))
            (directory / 'player.json').write_text(json.dumps({'version': 1, 'sector': 1076,
                'position': [side * 100., 0., 0.], 'home_station': 10761}))
        content = json.loads((ROOT / 'data/sector.json').read_text())
        content['mobs'] = []
        content['resources'] = []
        content_path = Path(folder) / 'sector.json'
        content_path.write_text(json.dumps(content))
        sessions: list[SpaceSession] = []
        with (Path(folder) / 'server.log').open('wb') as log:
            process = subprocess.Popen([str(binary), '--bind', host, '--state-dir', str(state),
                '--content', str(content_path)], stdout=log, stderr=log)
            try:
                deadline = time.monotonic() + 15
                while not registered(host):
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Disposable trade server failed to start')
                    time.sleep(.1)
                sessions = [connect(host, name, 'example', name.title()) for name in ('alice', 'bob')]
                a, b = (session.ticket.player_id for session in sessions)
                observed: list[tuple[int, Frame]] = []
                sessions[0].send(0x17, struct.pack('<2I', a, b))
                pump(sessions, observed, lambda: any(index == 0 and target_verb_enabled(frame, b, 6)
                    for index, frame in observed))
                action(sessions[0], 20, b)
                pump(sessions, observed, lambda: control(observed, 0, 0) and control(observed, 1, 0))
                print('PASS: native Trade verb6 and both six-slot windows open', flush=True)
                for index, owner in enumerate((a, b)):
                    sessions[index].send(0x27, struct.pack('>6I', owner, 1, 0, 16, index, 4 - index))
                    action(sessions[index], 24, (b, a)[index], 100 if index == 0 else 25)
                # Finish both pilots' offer edits before approving either one.
                deadline = time.monotonic() + .4
                while time.monotonic() < deadline:
                    for index, session in enumerate(sessions):
                        observed.extend((index, frame) for frame in session.poll())
                observed.clear()
                action(sessions[0], 21, b)
                pump(sessions, observed, lambda: control(observed, 0, 3) and control(observed, 1, 5))
                assert json.loads((state / 'accounts/alice/inventory.json').read_text())['credits'] == 1000
                observed.clear()
                # The native UI sends action23 after window updates; replying
                # with reset4 makes it send23 again indefinitely.
                for _ in range(3):
                    action(sessions[0], 23, b)
                    action(sessions[1], 23, a)
                action(sessions[0], 24, b, 100)
                deadline = time.monotonic() + .3
                while time.monotonic() < deadline:
                    for index, session in enumerate(sessions):
                        observed.extend((index, frame) for frame in session.poll())
                assert not control(observed, 0, 4) and not control(observed, 1, 4)
                print('PASS: native keep-trading packets do not reset approvals or create a reply loop', flush=True)
                action(sessions[1], 24, a, 50)
                pump(sessions, observed, lambda: control(observed, 0, 4) and control(observed, 1, 4))
                action(sessions[1], 21, a)
                action(sessions[0], 21, b)
                pump(sessions, observed, lambda: control(observed, 0, 2) and control(observed, 1, 2))
                first = json.loads((state / 'accounts/alice/inventory.json').read_text())
                second = json.loads((state / 'accounts/bob/inventory.json').read_text())
                assert (first['credits'], second['credits']) == (950, 1050)
                assert first['trade_revision'] == second['trade_revision'] == 1
                assert sum(item['quantity'] for item in first['cargo'] if item and item['template'] == 3818) == 3
                assert sum(item['quantity'] for item in second['cargo'] if item and item['template'] == 5477) == 4
                print('PASS: edits reset approvals; mutual confirmation atomically exchanges stacks and credits', flush=True)
                observed.clear()
                action(sessions[0], 20, b)
                pump(sessions, observed, lambda: control(observed, 1, 0))
                observed.clear()
                action(sessions[1], 22, a)
                pump(sessions, observed, lambda: control(observed, 0, 1) and control(observed, 1, 1))
                assert json.loads((state / 'accounts/alice/inventory.json').read_text()) == first
                assert json.loads((state / 'accounts/bob/inventory.json').read_text()) == second
                print('PASS: native cancellation closes both windows without changing inventory', flush=True)
            except BaseException:
                print((Path(folder) / 'server.log').read_text(errors='replace'))
                raise
            finally:
                for session in sessions:
                    session.close()
                if process.poll() is None:
                    (state / 'stop.request').write_text('owned trade smoke complete\n')
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.terminate()
                        process.wait(timeout=5)


def main() -> None:
    """Start only an unused loopback address with temporary accounts."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    parser.add_argument('--server', default='127.0.0.1')
    arguments = parser.parse_args()
    host = sandbox_address(arguments.server)
    if not host.startswith('127.') or registered(host):
        raise SystemExit('Trade smoke requires an unused disposable loopback address')
    exercise(arguments.binary.resolve(), host)


if __name__ == '__main__':
    main()
