"""Observe decrypted gameplay output of only this owned named test launch."""
from __future__ import annotations
import json
import os
import re
import socket
import struct
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path.cwd()))
from win_legacy import local_relays
from win_legacy.start_proxy import main
folder = Path(os.environ.get('ENB_NATIVE_SMOKE_RUN_DIR', '.')).resolve()
source = Path('server/src/crypto.rs').read_text()
modulus = int(re.search('const MODULUS: &str = "(\\d+)"', source)[1])
exponent = int(re.search('const PRIVATE_EXPONENT: &str = "(\\d+)"', source)[1])
output_lock = threading.Lock()
sector_ports: set[int] = set()

def report(event: dict) -> None:
    """Persist recognized gameplay or non-sensitive connection metadata."""
    name = 'tcp-delivered-frames.jsonl' if 'body_hex' in event else 'tcp-metadata.jsonl'
    with output_lock:
        with (folder / name).open('a') as out:
            out.write(json.dumps(event) + '\n')

class RC4:
    """Decode the owned legacy connection without exposing its session key."""

    def __init__(self, key: bytes) -> None:
        """Initialize the standard legacy RC4 state from a transient key."""
        self.s = list(range(256))
        self.i = self.j = 0
        j = 0
        for i in range(256):
            j = j + self.s[i] + key[i % len(key)] & 255
            self.s[i], self.s[j] = (self.s[j], self.s[i])

    def transform(self, data: bytes) -> bytes:
        """Advance the stream cipher.

        Returns:
            bytes: Transformed bytes, with no key material logged.
        """
        out = bytearray()
        for v in data:
            self.i = self.i + 1 & 255
            self.j = self.j + self.s[self.i] & 255
            self.s[self.i], self.s[self.j] = (self.s[self.j], self.s[self.i])
            out.append(v ^ self.s[self.s[self.i] + self.s[self.j] & 255])
        return bytes(out)

    def clone(self) -> RC4:
        """Copy the stream position for a non-consuming redirect preview.

        Returns:
            RC4: Independent cipher at the same stream position.
        """
        copied = RC4(b'\0')
        copied.s = self.s.copy()
        copied.i, copied.j = self.i, self.j
        return copied

class Observer:
    """Discard all outbound payloads except the transient RSA session handshake."""

    def __init__(self, port: int) -> None:
        """Observe one relay and classify gameplay only by its owned sector port."""
        self.port = port
        self.lock = threading.Lock()
        self.public = b''
        self.client = b''
        self.pending = b''
        self.buffer = b''
        self.public_done = False
        self.cipher: RC4 | None = None
        self.gameplay = port in sector_ports
        self.index = 0
        self.disabled = False
        report(dict(event='relay connection', port=port))

    def reroute(self, data: bytes) -> bytes:
        """Redirect only the owned master handoff through its existing loopback relay.

        Returns:
            bytes: Same-length ciphertext, with only the validated local IP changed.

        Raises:
            RuntimeError: The proxy identity or redirect listener is not owned.
        """
        with self.lock:
            if self.disabled or self.gameplay or self.cipher is None or (not self.public_done):
                return data
            copy = self.cipher.clone()
            decoded = copy.transform(data)
            joined = self.buffer + decoded
            offset = len(self.buffer)
            out = bytearray(data)
            pos = 0
            while pos + 4 <= len(joined):
                size, opcode = struct.unpack_from('<HH', joined, pos)
                if not 4 <= size <= 32767:
                    break
                if opcode == 0x36 and pos + 14 > len(joined):
                    self.disabled = True
                    report(dict(event='fragmented master redirect; sector capture unavailable',
                                port=self.port))
                    return data
                last = pos + 11
                if opcode == 54 and size >= 14 and (last < len(joined)) and (last >= offset):
                    address = joined[pos + 8:pos + 12]
                    report(dict(event='master redirect address', relay_port=self.port,
                                address=socket.inet_ntoa(address[::-1]),
                                sector_port=struct.unpack_from('<H', joined, pos + 12)[0]))
                    if address not in (socket.inet_aton(json.loads((folder / 'owner.json').read_text())['loopback'])[::-1], socket.inet_aton(socket.gethostbyname(socket.gethostname()))[::-1]):
                        break
                    identity = json.loads((folder / 'owner.json').read_text())
                    import win32api, win32process
                    from server.tools.process_info import process_path
                    handle = win32api.OpenProcess(4096, False, identity['proxy_pid'])
                    try:
                        if win32process.GetProcessTimes(handle)['CreationTime'].timestamp() != identity['proxy_birth']:
                            raise RuntimeError('Owned proxy birth changed')
                        if Path(process_path(int(handle))).resolve() != Path(identity['proxy_path']).resolve():
                            raise RuntimeError('Owned proxy path changed')
                        ports = local_relays.proxy_listener_ports(identity['proxy_pid'], identity['loopback'])
                        candidates = [struct.unpack_from(order + 'H', joined, pos + 12)[0] for order in ('<', '>')]
                        valid = [port for port in candidates if port in ports and port != self.port]
                        if not valid:
                            raise RuntimeError('Redirect is not owned sector listener')
                        sector_ports.update(valid)
                    finally:
                        handle.Close()
                    replacement = socket.inet_aton('127.0.0.1')[::-1]
                    for n in range(4):
                        at = pos + 8 + n
                        if at >= offset:
                            out[at - offset] ^= joined[at] ^ replacement[n]
                    report(dict(event='owned sector redirect routed through existing localhost relay', port=self.port))
                if pos + size > len(joined):
                    break
                pos += size
            return bytes(out)

    def accept(self, direction: int, data: bytes) -> None:
        """Decode server frames after RSA; persist no auth, tickets, or session keys."""
        with self.lock:
            if self.disabled:
                return
            if direction == 0:
                if self.cipher is not None:
                    return
                self.client += data
                if len(self.client) < 4:
                    return
                n = struct.unpack_from('!I', self.client)[0]
                if n not in (64, 65):
                    self.disabled = True
                    return
                if len(self.client) < 4 + n:
                    return
                block = pow(int.from_bytes(self.client[4:4 + n], 'big'), exponent, modulus).to_bytes(64, 'big')
                self.cipher = RC4(block[-8:][::-1])
                self.client = b''
            else:
                if not self.public_done:
                    self.public += data
                    p = 0
                    values = []
                    for _ in range(2):
                        if len(self.public) < p + 4:
                            return
                        n = struct.unpack_from('!I', self.public, p)[0]
                        if not 1 <= n <= 256:
                            self.disabled = True
                            return
                        if len(self.public) < p + 4 + n:
                            return
                        values.append(int.from_bytes(self.public[p + 4:p + 4 + n], 'big'))
                        p += 4 + n
                    if values[0] != modulus:
                        self.disabled = True
                        report(dict(event='unknown public modulus', port=self.port))
                        return
                    data = self.public[p:]
                    self.public = b''
                    self.public_done = True
                self.pending += data
            if self.cipher is None or not self.pending:
                return
            self.buffer += self.cipher.transform(self.pending)
            self.pending = b''
            while len(self.buffer) >= 4:
                size, opcode = struct.unpack_from('<HH', self.buffer)
                if not 4 <= size <= 32767:
                    self.disabled = True
                    report(dict(event='invalid decrypted frame', port=self.port))
                    return
                if len(self.buffer) < size:
                    return
                body = self.buffer[4:size]
                self.buffer = self.buffer[size:]
                if self.port in sector_ports:
                    self.gameplay = True
                if self.gameplay:
                    report(dict(at=time.time(), port=self.port, index=self.index, opcode=opcode, size=size, body_hex=body.hex()))
                self.index += 1

class ObservedSocket:
    """Delegate every socket operation and observe only successful sends."""

    def __init__(self, channel: socket.socket, observer: Observer, direction: int) -> None:
        """Wrap one concrete socket without changing its transport operations."""
        self.channel = channel
        self.observer = observer
        self.direction = direction

    def __getattr__(self, name: str) -> object:
        """Delegate socket operations.

        Returns:
            object: The underlying socket attribute.
        """
        return getattr(self.channel, name)

    def send(self, data: bytes) -> int:
        """Forward bytes and observe successful server sends.

        Returns:
            int: Bytes accepted by the underlying socket.
        """
        original = bytes(data)
        if self.direction == 0:
            self.observer.accept(0, original)
        if self.direction == 1:
            data = self.observer.reroute(original)
        n = self.channel.send(data)
        try:
            if self.direction == 1:
                self.observer.accept(1, bytes(data[:n]))
        except Exception:
            self.observer.disabled = True
            report(dict(event='observer failed; transport preserved', port=self.observer.port))
        return n
original_init = local_relays.RelayConnection.__init__

def observed_init(self: local_relays.RelayConnection, client: socket.socket, target: socket.socket, *, log_path: Path | None=None) -> None:
    """Observe only this diagnostic launch's existing private fallback relays."""
    original_init(self, client, target, log_path=log_path)
    observer = Observer(target.getpeername()[1])
    a = ObservedSocket(client, observer, 1)
    b = ObservedSocket(target, observer, 0)
    self.sockets = (a, b)
    self.threads = [threading.Thread(target=self.pump, args=(a, b), daemon=True), threading.Thread(target=self.pump, args=(b, a), daemon=True)]

def run() -> None:
    """Install observers only in this owned launcher and create a fresh capture directory.

    Raises:
        RuntimeError: Capture directory or named-instance arguments are missing.
    """
    if 'ENB_NATIVE_SMOKE_RUN_DIR' not in os.environ:
        raise RuntimeError('Set ENB_NATIVE_SMOKE_RUN_DIR to a fresh artifact directory')
    if '--instance' not in sys.argv or '--no-build' not in sys.argv:
        raise RuntimeError('Use a dedicated --instance and --no-build for the existing remote server')
    folder.mkdir(parents=True, exist_ok=False)
    from win_legacy.local_windows import ProxyJob
    import win32process
    original_start = ProxyJob.start

    def pinned_start(job: ProxyJob, arguments: list[str], directory: Path, *, loopback: str | None=None, log_path: Path | None=None) -> None:
        """Pin only the proxy started by this launcher before any relay accepts traffic."""
        original_start(job, arguments, directory, loopback=loopback, log_path=log_path)
        client = next((value[len('/CLIENT:'):] for value in arguments if value.startswith('/CLIENT:')))
        owner = dict(launcher_pid=os.getpid(), proxy_pid=job.pid, proxy_birth=win32process.GetProcessTimes(job.process)['CreationTime'].timestamp(), proxy_path=str(Path(arguments[0]).resolve()), client_path=str(Path(client).resolve()), loopback=loopback)
        (folder / 'owner.json').write_text(json.dumps(owner, indent=2))
    ProxyJob.start = pinned_start
    local_relays.RelayConnection.__init__ = observed_init
    try:
        main()
    finally:
        ProxyJob.start = original_start
        local_relays.RelayConnection.__init__ = original_init
if __name__ == '__main__':
    run()
