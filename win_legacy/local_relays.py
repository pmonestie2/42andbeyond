"""Bridge legacy localhost galaxy redirects to one private proxy interface.

The old character-list format advertises 127.0.0.1 for the dynamic master port.
Game UDP remains direct: these bounded TCP relays cover only login/fallback links.
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes
import ipaddress
from datetime import datetime, timezone
from pathlib import Path
import socket
import threading
import time


def proxy_listener_ports(pid: int, address: str) -> list[int]:
    """Query this owned proxy's listeners without inspecting packet data.

    Returns:
        list[int]: Dynamic listener ports owned by this PID on this interface.

    Raises:
        OSError: Windows cannot enumerate the TCP listener table.
    """
    class Row(ctypes.Structure):
        _fields_ = [('state',wintypes.DWORD),('address',wintypes.DWORD),('port',wintypes.DWORD),
                    ('remote_address',wintypes.DWORD),('remote_port',wintypes.DWORD),('pid',wintypes.DWORD)]
    api = ctypes.WinDLL('iphlpapi', use_last_error=True).GetExtendedTcpTable
    api.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL,
                    wintypes.ULONG, ctypes.c_int, wintypes.ULONG]
    api.restype = wintypes.DWORD
    size = wintypes.DWORD()
    result = api(None, ctypes.byref(size), False, socket.AF_INET, 3, 0)
    if result not in (0,122): raise OSError(result,'Cannot enumerate proxy listeners')
    for _ in range(3):
        buffer = ctypes.create_string_buffer(size.value)
        result = api(buffer, ctypes.byref(size), False, socket.AF_INET, 3, 0)
        if result != 122:
            break
    if result: raise OSError(result,'Cannot enumerate proxy listeners')
    count = ctypes.cast(buffer, ctypes.POINTER(wintypes.DWORD))[0]
    rows = (Row * count).from_buffer(buffer, ctypes.sizeof(wintypes.DWORD))
    return sorted(socket.ntohs(row.port & 65535) for row in rows if row.pid == pid
        and socket.inet_ntoa(int(row.address).to_bytes(4,'little')) == address
        and socket.ntohs(row.port & 65535) not in (443,3805))


class RelayConnection:
    def __init__(self, client: socket.socket, target: socket.socket, *, log_path: Path | None = None) -> None:
        """Own the two sockets and their bounded forwarding workers."""
        self.sockets = (client,target)
        self.log_path = log_path
        self.stopped = threading.Event()
        self.lock = threading.Lock()
        self.remaining = 2
        self.threads = [threading.Thread(target=self.pump,args=(client,target),daemon=True),
                        threading.Thread(target=self.pump,args=(target,client),daemon=True)]

    def start(self) -> None:
        """Start both forwarding directions with interruptible socket timeouts."""
        for channel in self.sockets: channel.settimeout(.5)
        for thread in self.threads: thread.start()

    def report(self, direction: int, event: str, forwarded: int) -> None:
        """Append transport metadata only; login and gameplay payloads are never logged."""
        if self.log_path is None:return
        stamp=datetime.now(timezone.utc).isoformat(timespec='milliseconds')
        try:
            with self.log_path.open('a',encoding='utf-8') as output:
                output.write(f'[{stamp}] RELAY direction={direction} {event}; forwarded={forwarded}\n')
        except OSError:pass

    def pump(self, source: socket.socket, target: socket.socket) -> None:
        """Preserve every byte across loading pauses and half-close in either direction."""
        direction=self.sockets.index(source);forwarded=0;reported_backpressure=False
        try:
            while not self.stopped.is_set():
                try:data=source.recv(65536)
                except socket.timeout:continue
                if not data:
                    self.report(direction,'peer EOF',forwarded);break
                remaining=memoryview(data)
                while remaining and not self.stopped.is_set():
                    try:sent=target.send(remaining)
                    except socket.timeout:
                        if not reported_backpressure:
                            self.report(direction,'receiver paused; preserving pending bytes',forwarded)
                            reported_backpressure=True
                        continue
                    if sent==0:raise ConnectionResetError('Relay peer stopped accepting bytes')
                    forwarded+=sent;remaining=remaining[sent:]
        except OSError as error:
            if not self.stopped.is_set():self.report(direction,f'transport error {type(error).__name__}: {error}',forwarded)
            self.stopped.set()
        finally:
            try:target.shutdown(socket.SHUT_WR)
            except OSError:pass
            with self.lock:
                self.remaining-=1
                if self.remaining==0:
                    for channel in self.sockets:channel.close()

    def alive(self) -> bool:
        """Inspect whether either forwarding worker remains active.

        Returns:
            bool: Whether this connection still has a running worker.
        """
        return any(thread.is_alive() for thread in self.threads)

    def close(self) -> None:
        """Interrupt both sockets and join their workers."""
        self.stopped.set()
        for channel in self.sockets:
            try: channel.shutdown(socket.SHUT_RDWR)
            except OSError: pass
            channel.close()
        for thread in self.threads:
            if thread.ident is not None: thread.join(timeout=2)


class LoopbackRelay:
    def __init__(self, address: str, port: int, *, target_port: int | None = None, log_path: Path | None = None) -> None:
        """Create a relay targeting only a validated local IPv4 address.

        Raises:
            ValueError: The target is not an IPv4 loopback address.
        """
        if not ipaddress.IPv4Address(address).is_loopback:
            raise ValueError('Only loopback relay targets are supported')
        self.address = address
        self.port = port
        self.target_port = port if target_port is None else target_port
        self.log_path = log_path
        self.listener = socket.socket(socket.AF_INET,socket.SOCK_STREAM)
        self.stopped = threading.Event()
        self.thread = threading.Thread(target=self.accept,daemon=True)
        self.connections: list[RelayConnection] = []
        self.lock = threading.Lock()

    def start(self) -> None:
        """Bind the fallback listener and start accepting connections.

        Raises:
            OSError: The localhost port is unavailable.
        """
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        self.listener.bind(('127.0.0.1',self.port))
        self.port = self.listener.getsockname()[1]
        self.listener.listen(8)
        self.listener.settimeout(.25)
        self.thread.start()

    def accept(self) -> None:
        """Forward at most sixteen simultaneous streams to this proxy."""
        while not self.stopped.is_set():
            try: client,_ = self.listener.accept()
            except socket.timeout: continue
            except OSError: break
            try:
                with self.lock:
                    self.connections = [connection for connection in self.connections if connection.alive()]
                    if len(self.connections) >= 16:
                        client.close()
                        continue
                target = socket.create_connection((self.address,self.target_port),timeout=2)
                connection = RelayConnection(client,target,log_path=self.log_path)
                with self.lock:
                    if self.stopped.is_set():
                        client.close()
                        target.close()
                        break
                    connection.start()
                    self.connections.append(connection)
            except OSError:
                client.close()

    def close(self) -> None:
        """Stop accepting, interrupt streams, and join all owned workers."""
        self.stopped.set()
        self.listener.close()
        if self.thread.ident is not None: self.thread.join(timeout=3)
        with self.lock: connections = list(self.connections)
        for connection in connections: connection.close()


class ProfileRelays:
    def __init__(self, pid: int, address: str, *, log_path: Path | None = None) -> None:
        """Track fallback relays for one owned proxy and private interface."""
        self.pid = pid
        self.address = address
        self.log_path = log_path
        self.relays: list[LoopbackRelay] = []

    def start(self) -> None:
        """Discover both dynamic listeners and install their localhost relays.

        Raises:
            TimeoutError: The proxy does not create both listeners within ten seconds.
            OSError: A listener cannot be enumerated or a relay port is unavailable.
        """
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            ports = proxy_listener_ports(self.pid,self.address)
            if len(ports) == 2: break
            time.sleep(.1)
        else: raise TimeoutError('Private proxy master/sector listeners did not appear')
        try:
            for port in ports:
                relay = LoopbackRelay(self.address,port,log_path=self.log_path)
                self.relays.append(relay)
                relay.start()
        except BaseException:
            self.close()
            raise

    def close(self) -> None:
        """Close this profile's relays without touching another profile."""
        for relay in self.relays: relay.close()
        self.relays.clear()
