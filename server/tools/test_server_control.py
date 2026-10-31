"""Exercise startup preflight without starting servers or touching player accounts."""
import errno
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from server_control import control, require_local_address


class ServerAddressTests(unittest.TestCase):
    """Missing interfaces fail before launch, retaining the saved configuration."""

    def test_real_loopback_address_accepts_ephemeral_probe(self) -> None:
        """A temporary UDP socket does not occupy the server's fixed ports."""
        require_local_address('127.0.0.1')

    def test_wildcard_and_public_addresses_are_rejected(self) -> None:
        """Prevent accidental public listening while offering local-only recovery."""
        for address in ('0.0.0.0', '8.8.8.8', '192.0.2.1', '::1'):
            with self.subTest(address=address), self.assertRaises(ValueError):
                require_local_address(address)

    def test_missing_interface_preserves_saved_bind_and_never_launches(self) -> None:
        """A stale VM network must not cause a fallback or account migration."""
        with tempfile.TemporaryDirectory() as temporary:
            state = Path(temporary)
            executable = state / 'beyond-server'
            executable.write_bytes(b'not executed')
            record = state / 'server.local.json'
            record.write_text(json.dumps(dict(pid=123, bind='192.168.47.1',
                command=[str(executable), '--bind', '192.168.47.1', '--state-dir', str(state)])))
            original = record.read_bytes()
            with patch('server_control.owned_identity', return_value=None), \
                    patch('server_control.socket.socket') as socket, \
                    patch('server_control.launch') as launch:
                socket.return_value.__enter__.return_value.bind.side_effect = OSError(
                    errno.EADDRNOTAVAIL, 'Cannot assign requested address')
                with self.assertRaisesRegex(RuntimeError, 'Restore its LAN/VM network'):
                    control('start', state, None, None, follow=False)
                launch.assert_not_called()
            self.assertEqual(record.read_bytes(), original)
            self.assertEqual(sorted(path.name for path in state.iterdir()),
                ['beyond-server', 'server.local.json'])


if __name__ == '__main__':
    unittest.main()
