"""Verify the bounded proxy override using mocked Windows memory APIs."""
from __future__ import annotations

import ctypes
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch

from .proxy_loopback import configure_proxy_loopback


class ProxyLoopbackTests(unittest.TestCase):
    def test_rejects_unknown_build_and_nonreserved_addresses(self) -> None:
        """Do not access process memory for unsupported builds or destinations."""
        with tempfile.TemporaryDirectory() as temporary:
            image = Path(temporary) / 'net7proxy.exe'
            image.write_bytes(b'unknown build')
            with self.assertRaisesRegex(RuntimeError, 'Unsupported proxy build'):
                configure_proxy_loopback(123, image, '127.0.0.2')
            for address in ('127.0.0.1', '127.0.0.10', '192.168.47.1'):
                with self.subTest(address=address), self.assertRaises(ValueError):
                    configure_proxy_loopback(123, image, address)

    def test_writes_only_expected_literal_and_restores_page_protection(self) -> None:
        """Validate original bytes, exact write and readback before accepting the override."""
        with tempfile.TemporaryDirectory() as temporary:
            image = Path(temporary) / 'net7proxy.exe'
            image.write_bytes(b'known fixture')
            kernel = MagicMock()
            kernel.ReadProcessMemory.side_effect = self.read_literal
            kernel.WriteProcessMemory.side_effect = self.write_literal
            with patch('win_legacy.proxy_loopback.PROXY_SHA256', hashlib.sha256(image.read_bytes()).hexdigest()), \
                 patch('ctypes.WinDLL', return_value=kernel, create=True):
                configure_proxy_loopback(123, image, '127.0.0.2')
            write = kernel.WriteProcessMemory.call_args.args
            self.assertEqual(write[:2], (123, 0x4402e8))
            self.assertEqual(write[2].raw, b'127.0.0.2\0')
            self.assertEqual(write[3], 10)
            self.assertEqual(kernel.VirtualProtectEx.call_count, 2)

    def read_literal(self, process: int, address: int, buffer: ctypes.Array, size: int, count: object) -> bool:
        """Populate the fixture's initial value followed by the expected readback.

        Returns:
            bool: A successful mocked memory read.
        """
        literal = b'127.0.0.1\0' if not getattr(self, 'has_read', False) else b'127.0.0.2\0'
        self.has_read = True
        ctypes.memmove(buffer, literal, size)
        count._obj.value = size
        return True

    def write_literal(self, process: int, address: int, buffer: ctypes.Array, size: int, count: object) -> bool:
        """Report a complete write without accessing a process.

        Returns:
            bool: A successful mocked write.
        """
        count._obj.value = size
        return True
