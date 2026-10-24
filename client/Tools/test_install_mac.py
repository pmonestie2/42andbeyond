"""Focused checks for content-based Mac app installation."""
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import install_mac


class InstallMacChecks(unittest.TestCase):
    """Exercise equal timestamps, changed resources, and the running-app guard."""

    def test_changed_bundle_with_equal_executable_timestamp(self) -> None:
        """Replace changed content while preserving an identical executable timestamp."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = root / 'project'
            source = project / 'Builds' / install_mac.APP_NAME
            executable = source / install_mac.EXECUTABLE
            resource = source / 'Contents' / 'Resources' / 'scene.dat'
            executable.parent.mkdir(parents=True)
            resource.parent.mkdir(parents=True)
            executable.write_bytes(b'game')
            resource.write_bytes(b'old-data')
            destination = root / 'Applications' / install_mac.APP_NAME
            with patch.object(install_mac, 'installed_bundle', return_value=destination), \
                    patch.object(install_mac, 'running_client', return_value=False):
                self.assertEqual(install_mac.install(project), destination)
            with patch.object(install_mac, 'installed_bundle', return_value=destination), \
                    patch.object(install_mac, 'running_client', side_effect=AssertionError('unneeded process check')):
                self.assertEqual(install_mac.install(project), destination)
            previous = destination / 'Contents' / 'Resources' / 'scene.dat'
            resource.write_bytes(b'new-data')
            installed_time = previous.stat()
            os.utime(resource, ns=(installed_time.st_atime_ns, installed_time.st_mtime_ns))
            self.assertEqual(executable.stat().st_mtime_ns,
                             (destination / install_mac.EXECUTABLE).stat().st_mtime_ns)
            with patch.object(install_mac, 'installed_bundle', return_value=destination), \
                    patch.object(install_mac, 'running_client', return_value=True):
                with self.assertRaisesRegex(RuntimeError, 'Close 42 and Beyond'):
                    install_mac.install(project)
            self.assertEqual(previous.read_bytes(), b'old-data')
            with patch.object(install_mac, 'installed_bundle', return_value=destination), \
                    patch.object(install_mac, 'running_client', return_value=False):
                self.assertEqual(install_mac.install(project), destination)
            self.assertEqual(previous.read_bytes(), b'new-data')
            self.assertFalse((destination.parent / '.42-and-beyond.previous').exists())


if __name__ == '__main__':
    unittest.main()
