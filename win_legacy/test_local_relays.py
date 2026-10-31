"""Socket-level relay checks; these never launch or drive a game client."""
from __future__ import annotations

import os
import socket
import threading
import time
import unittest

from .local_relays import LoopbackRelay, RelayConnection, proxy_listener_ports


class RelayTests(unittest.TestCase):
    def test_bidirectional_stream_survives_client_half_close(self) -> None:
        """Allow a response after the client finishes sending its request."""
        with socket.socket() as server:
            server.bind(('127.0.0.1', 0))
            server.listen(1)
            server.settimeout(3)
            relay = LoopbackRelay('127.0.0.1', 0, target_port=server.getsockname()[1])
            try:
                relay.start()
                with socket.create_connection(('127.0.0.1', relay.port), timeout=3) as client:
                    with server.accept()[0] as upstream:
                        upstream.settimeout(3)
                        client.sendall(b'login request')
                        client.shutdown(socket.SHUT_WR)
                        self.assertEqual(upstream.recv(1024), b'login request')
                        self.assertEqual(upstream.recv(1024), b'')
                        upstream.sendall(b'login response')
                        upstream.shutdown(socket.SHUT_WR)
                        self.assertEqual(client.recv(1024), b'login response')
                        self.assertEqual(client.recv(1024), b'')
            finally:
                relay.close()
            self.assertFalse(relay.thread.is_alive())
            self.assertTrue(all(not connection.alive() for connection in relay.connections))

    def test_close_interrupts_connected_idle_streams(self) -> None:
        """Join all workers even when the connected sockets are idle."""
        with socket.socket() as server:
            server.bind(('127.0.0.1', 0))
            server.listen(1)
            server.settimeout(3)
            relay = LoopbackRelay('127.0.0.1', 0, target_port=server.getsockname()[1])
            try:
                relay.start()
                with socket.create_connection(('127.0.0.1', relay.port), timeout=3) as client:
                    with server.accept()[0] as upstream:
                        upstream.settimeout(3)
                        # A round trip proves the connection is registered before close.
                        client.sendall(b'ready')
                        self.assertEqual(upstream.recv(1024), b'ready')
                        relay.close()
                        self.assertEqual(client.recv(1024), b'')
                        self.assertEqual(upstream.recv(1024), b'')
            finally:
                relay.close()
            self.assertFalse(relay.thread.is_alive())
            self.assertTrue(all(not connection.alive() for connection in relay.connections))

    def test_slow_receiver_does_not_truncate_the_stream(self) -> None:
        """Keep exact bytes through partial sends and longer-than-timeout loading pauses."""
        sender,inbound=socket.socketpair();outbound,receiver=socket.socketpair()
        outbound.setsockopt(socket.SOL_SOCKET,socket.SO_SNDBUF,4096)
        receiver.setsockopt(socket.SOL_SOCKET,socket.SO_RCVBUF,4096)
        payload=bytes(range(256))*16384
        errors=[]
        def produce() -> None:
            """Send a disposable bootstrap burst while its reader initially pauses."""
            try:sender.sendall(payload);sender.shutdown(socket.SHUT_WR)
            except OSError as error:errors.append(error)
        connection=RelayConnection(inbound,outbound);producer=threading.Thread(target=produce)
        try:
            sender.settimeout(6);receiver.settimeout(4);connection.start();producer.start()
            time.sleep(1.2)
            received=bytearray()
            while data:=receiver.recv(65536):received.extend(data)
            producer.join(timeout=6)
            self.assertFalse(producer.is_alive());self.assertEqual(errors,[])
            self.assertEqual(received,payload)
        finally:
            connection.close();sender.close();receiver.close();producer.join(timeout=2)

    @unittest.skipUnless(os.name == 'nt', 'Windows listener ownership API')
    def test_listener_snapshot_filters_interface_and_process(self) -> None:
        """Discover only listeners belonging to the requested PID and address."""
        with socket.socket() as server:
            server.bind(('127.0.0.2', 0))
            server.listen(1)
            port = server.getsockname()[1]
            self.assertIn(port, proxy_listener_ports(os.getpid(), '127.0.0.2'))
            self.assertNotIn(port, proxy_listener_ports(os.getpid(), '127.0.0.1'))
            self.assertNotIn(port, proxy_listener_ports(0, '127.0.0.2'))
