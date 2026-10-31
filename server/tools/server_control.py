"""Start, stop, or inspect the owned 42 and Beyond server."""
from __future__ import annotations

import argparse
import errno
import json
from pathlib import Path
import socket
import subprocess
import sys
import time

from restart_server import launch, process_identity, wait_ready
from build import ROOT
from server_network import sandbox_address


def print_banner() -> None:
    """Draw the Rust server's startup mark in red on an interactive terminal."""
    art = (
        '        ╭╮                  ╭╮',
        '    ╭───╯╰──────────────────╯╰───╮',
        '    │   ▄▄   ▄▄    4 2    ▄▄   ▄▄ │',
        '    ╰───╮╭──────────────────╮╭───╯',
        '        ╰╯                  ╰╯',
        '       42AndBeyond in Rust!!',
    )
    red = '\033[1;91m' if sys.stdout.isatty() else ''
    reset = '\033[0m' if red else ''
    print(red + '\n'.join(art) + reset)


def recorded_command(state: Path) -> tuple[list[str], dict] | None:
    """Returns:
        tuple[list[str], dict] | None: Saved command and process record, if present.
    """
    path = state / 'server.local.json'
    if not path.is_file():
        return None
    record = json.loads(path.read_text())
    command = record.get('command')
    if not isinstance(command, list) or not command or not all(isinstance(part, str) for part in command):
        raise RuntimeError('Invalid server process record; inspect server.local.json.')
    return command, record


def owned_identity(record: dict) -> tuple[Path, float] | None:
    """Returns:
        tuple[Path, float] | None: Verified running server image and birth time.
    """
    pid = int(record['pid'])
    identity = process_identity(pid)
    if identity is None:
        return None
    executable, started_at = identity
    if executable.name not in ('beyond-server', 'beyond-server.exe') or not executable.is_relative_to(ROOT / 'target') or abs(started_at - float(record['started_at'])) > 10:
        raise RuntimeError('Recorded PID belongs to another process; no action taken.')
    return identity


def follow_log() -> None:
    """Show recent server events, coloring its startup art in a terminal."""
    log = ROOT / 'logs/server.log'
    print(f'Following {log}  (Ctrl+C closes the log view; server keeps running)', flush=True)
    viewer = subprocess.Popen(['tail', '-n', '20', '-F', str(log)],
                              stdout=subprocess.PIPE, text=True, bufsize=1)
    try:
        for line in viewer.stdout:
            if sys.stdout.isatty() and any(mark in line for mark in ('╔', '║', '╚')):
                sys.stdout.write('\033[1;91m' + line.rstrip('\n') + '\033[0m\n')
            else:
                sys.stdout.write(line)
            sys.stdout.flush()
    except KeyboardInterrupt:
        print('\nLog view closed. Server remains running.')
    finally:
        viewer.terminate()
        viewer.wait()


def require_local_address(address: str) -> None:
    """Check an explicit bind before launching or changing saved server state.

    Raises:
        ValueError: The address is not loopback or private IPv4.
        RuntimeError: The selected address is absent from this machine.
        OSError: Another socket error prevents the availability check.
    """
    address = sandbox_address(address)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.bind((address, 0))
    except OSError as error:
        if error.errno == errno.EADDRNOTAVAIL or getattr(error, 'winerror', None) == 10049:
            raise RuntimeError(
                f'{address} is not assigned to this machine. Restore its LAN/VM network '
                'and retry, or choose an existing interface with --bind ADDRESS. '
                'For Mac-only play: ./serverctrl start --bind 127.0.0.1 '
                '(connect the Mac client to 127.0.0.1). '
                'No address was substituted; Windows clients need the same reachable server address.'
            ) from error
        raise


def control(action: str, state: Path, bind: str | None, binary: Path | None,
            follow: bool = True) -> None:
    """Perform one process-owned action using the server's durable record."""
    state = state.resolve()
    saved = recorded_command(state)
    record = saved[1] if saved else None
    identity = owned_identity(record) if record else None
    if action == 'status':
        if identity is None:
            print('42 and Beyond server is stopped.')
        else:
            print(f"42 and Beyond server is running: PID {record['pid']}, bind {record['bind']}")
        print(f'Log: {ROOT / "logs/server.log"}')
        return
    if action == 'stop':
        if identity is None:
            print('42 and Beyond server is already stopped.')
            return
        (state / 'stop.request').write_text('stop\n')
        deadline = time.monotonic() + 20
        while process_identity(int(record['pid'])) == identity:
            if time.monotonic() >= deadline:
                raise TimeoutError('Server did not stop gracefully; inspect server/logs/server.log.')
            time.sleep(.1)
        print('42 and Beyond server stopped; character state saved.')
        return
    if identity is not None:
        print(f"42 and Beyond server is already running: PID {record['pid']}, bind {record['bind']}")
        if follow:
            print_banner()
            follow_log()
        return
    default_binary = ROOT / 'target/release/beyond-server'
    if not default_binary.is_file():
        default_binary = ROOT / 'target/debug/beyond-server'
    executable = (binary or (Path(saved[0][0]) if saved else default_binary)).resolve()
    if not executable.is_file():
        raise FileNotFoundError(f'Build the server first: {executable}')
    if saved and binary is None:
        command = saved[0].copy()
        command[0] = str(executable)
        address = bind or str(record['bind'])
        if bind:
            command[command.index('--bind') + 1] = bind
    else:
        address = bind or '127.0.0.1'
        command = [str(executable), '--bind', address,
                   '--content', str(ROOT / 'data/asteroid_belt_alpha.json'),
                   '--state-dir', str(state)]
    require_local_address(address)
    process = launch(command, ROOT / 'logs')
    wait_ready(process, address)
    ready = recorded_command(state)
    if ready is None or int(ready[1]['pid']) != process.pid:
        raise RuntimeError('A different server answered the readiness probe; inspect the log.')
    print_banner()
    print(f'Ready on {address} (PID {process.pid})')
    print(f'Log: {ROOT / "logs/server.log"}')
    if follow:
        follow_log()


def main() -> None:
    """Parse the small command interface and report failures clearly."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('start', 'stop', 'status'))
    parser.add_argument('--state-dir', type=Path, default=ROOT / 'state')
    parser.add_argument('--bind', help='Address for a new or restarted server')
    parser.add_argument('--binary', type=Path, help='Existing server executable for start')
    parser.add_argument('--no-follow', action='store_true', help='Return after start without tailing the log')
    args = parser.parse_args()
    try:
        control(args.action, args.state_dir, args.bind, args.binary,
                follow=not args.no_follow)
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        print(f'42 and Beyond: {error}', file=sys.stderr)
        raise SystemExit(1) from None


if __name__ == '__main__':
    main()
