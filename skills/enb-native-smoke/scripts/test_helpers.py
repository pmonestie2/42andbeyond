"""Offline regression checks; never open, drive, or restart a game/server."""
from __future__ import annotations

import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

import capture_proxy
from check_capture import check


class ObserverTests(unittest.TestCase):
    def test_fragmented_handshake_and_payload_exclude_authentication(self) -> None:
        """Decode split TCP receives while excluding master/outgoing credentials."""
        events: list[dict] = []
        key = b'12345678'
        public = (
            struct.pack('!I', 65) + b'\0' + capture_proxy.modulus.to_bytes(64, 'big')
            + struct.pack('!I', 1) + b'#'
        )
        encrypted_key = pow(
            int.from_bytes(bytes(56) + key[::-1], 'big'), 35, capture_proxy.modulus
        ).to_bytes(64, 'big')
        with patch.object(capture_proxy, 'report', events.append), patch.object(capture_proxy, 'sector_ports', {7001}):
            observer = capture_proxy.Observer(7001)
            for value in public:
                observer.accept(1, bytes([value]))
            for value in struct.pack('!I', 64) + encrypted_key:
                observer.accept(0, bytes([value]))
            observer.accept(0, b'OUTBOUND_AUTH_TICKET_MUST_NEVER_BE_SAVED')
            body = bytes(4) + b'\x06\x00\x01\x06\x04\x00\x00\x00'
            frames = struct.pack('<HH', len(body) + 4, 0x1b) + body + struct.pack('<HHI', 8, 5, 12)
            encrypted = capture_proxy.RC4(key).transform(frames)
            for value in encrypted:
                observer.accept(1, bytes([value]))
            self.assertFalse(observer.disabled)
        gameplay = [event for event in events if 'body_hex' in event]
        self.assertEqual([event['opcode'] for event in gameplay], [0x1b, 5])
        self.assertEqual(gameplay[0]['body_hex'], body.hex())
        self.assertNotIn('OUTBOUND_AUTH', str(events))
        self.assertNotIn(key.hex(), str(events))

    def test_master_response_is_not_persisted_as_gameplay(self) -> None:
        """A decoded master reply remains excluded without a validated sector port."""
        events: list[dict] = []
        with patch.object(capture_proxy, 'report', events.append), patch.object(capture_proxy, 'sector_ports', set()):
            observer = capture_proxy.Observer(7000)
            observer.public_done = True
            observer.cipher = capture_proxy.RC4(b'12345678')
            body = b'MASTER_TICKET_MUST_NEVER_BE_SAVED'
            encrypted = capture_proxy.RC4(b'12345678').transform(struct.pack('<HH', len(body) + 4, 0x70) + body)
            observer.accept(1, encrypted)
        self.assertFalse(any('body_hex' in event for event in events))
        self.assertNotIn('MASTER_TICKET', str(events))


class CaptureCheckTests(unittest.TestCase):
    def test_missing_initial_aux_fails_and_delivered_aux_matches_udp(self) -> None:
        """Reproduce the decisive missing-AUX failure without a native process."""
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            tcp = folder / 'tcp-delivered-frames.jsonl'
            start = {'opcode': 5, 'size': 8, 'body_hex': '01000000'}
            tcp.write_text(json.dumps(start) + '\n')
            with self.assertRaisesRegex(ValueError, 'Initial player AUX0'):
                check(folder)
            body = bytes(4) + b'\x06\x00\x01\x06\x04\x00\x00\x00'
            aux = {'opcode': 0x1b, 'size': len(body) + 4, 'body_hex': body.hex()}
            tcp.write_text(json.dumps(aux) + '\n' + json.dumps(start) + '\n')
            (folder / 'frames.jsonl').write_text(json.dumps(aux) + '\n')
            self.assertTrue(check(folder)['initial_aux_matches_udp'])


if __name__ == '__main__':
    unittest.main()
