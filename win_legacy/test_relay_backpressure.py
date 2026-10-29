"""Owned socket-only regression; never starts a game, proxy or server."""
import socket
import threading
import time
import unittest
from .local_relays import RelayConnection

class RelayBackpressureTests(unittest.TestCase):
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
