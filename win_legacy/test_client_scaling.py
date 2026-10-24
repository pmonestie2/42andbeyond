"""Check DPI flag selection and restoration without changing Windows settings."""
from __future__ import annotations

from contextlib import ExitStack
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch

from .client_scaling import ClientScaling, scaling_flags


class ScalingFlagTests(unittest.TestCase):
    def test_modes_replace_only_dpi_flags(self) -> None:
        """Retain unrelated flags when switching in either direction."""
        self.assertEqual(scaling_flags('~ WIN7RTM DPIUNAWARE GDIDPISCALING', 'normal'),
                         '~ WIN7RTM HIGHDPIAWARE')
        self.assertEqual(scaling_flags('~ HIGHDPIAWARE RUNASADMIN', 'double'),
                         '~ RUNASADMIN DPIUNAWARE')
        with self.assertRaises(ValueError):
            scaling_flags('', 'invalid')

    def test_unspecified_scale_does_not_touch_windows(self) -> None:
        """Keep existing settings when no scaling option was supplied."""
        with ClientScaling(Path('client.exe'), None) as scaling:
            self.assertEqual(scaling.edits, [])


@unittest.skipUnless(os.name == 'nt', 'Windows registry API mocks')
class ScalingRegistryTests(unittest.TestCase):
    def setUp(self) -> None:
        """Provide in-memory registry values and a disposable backup location."""
        import winreg

        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        directory = self.stack.enter_context(tempfile.TemporaryDirectory())
        self.client = Path('C:/Game/release/client.exe')
        self.short = r'C:\GAME\RELEASE\CLIENT.EXE'
        self.values: dict[str, tuple[str, int]] = {str(self.client): ('~ WIN7RTM', winreg.REG_SZ)}
        self.stack.enter_context(patch('win32api.GetShortPathName', return_value=self.short))
        self.stack.enter_context(patch('winreg.CreateKeyEx', return_value=MagicMock()))
        self.stack.enter_context(patch('winreg.OpenKey', return_value=MagicMock()))
        self.stack.enter_context(patch('winreg.QueryValueEx', side_effect=self.query))
        self.stack.enter_context(patch('winreg.SetValueEx', side_effect=self.write))
        self.stack.enter_context(patch('winreg.DeleteValue', side_effect=self.delete))
        self.scaling = ClientScaling(self.client, 'double')
        self.scaling.backup = Path(directory) / 'dpi.json'

    def query(self, key: object, name: str) -> tuple[str, int]:
        """Read a fake registry value.

        Returns:
            tuple[str, int]: The stored text and registry type.

        Raises:
            FileNotFoundError: The value is absent.
        """
        if name not in self.values:
            raise FileNotFoundError(name)
        return self.values[name]

    def write(self, key: object, name: str, reserved: int, kind: int, value: str) -> None:
        """Write a fake registry value."""
        self.values[name] = value, kind

    def delete(self, key: object, name: str) -> None:
        """Remove a fake registry value."""
        del self.values[name]

    def test_restores_existing_and_removes_created_short_path_value(self) -> None:
        """Restore both path variants even when gameplay raises an exception."""
        import winreg

        before = dict(self.values)
        with self.assertRaisesRegex(RuntimeError, 'game failed'):
            with self.scaling:
                self.assertEqual(self.values[str(self.client)], ('~ WIN7RTM DPIUNAWARE', winreg.REG_SZ))
                self.assertEqual(self.values[self.short], ('~ DPIUNAWARE', winreg.REG_SZ))
                self.assertTrue(self.scaling.backup.is_file())
                raise RuntimeError('game failed')
        self.assertEqual(self.values, before)

    def test_preserves_concurrent_edits(self) -> None:
        """Retain externally changed settings and the recovery backup."""
        import winreg

        with self.scaling:
            self.values[str(self.client)] = ('~ WIN7RTM RUNASADMIN', winreg.REG_SZ)
        self.assertEqual(self.values[str(self.client)][0], '~ WIN7RTM RUNASADMIN')
        self.assertTrue(self.scaling.backup.is_file())
        self.assertNotIn(self.short, self.values)

    def test_rolls_back_first_value_when_second_write_fails(self) -> None:
        """Do not leave the first path changed after a partial setup failure."""
        before = dict(self.values)
        with patch('winreg.SetValueEx', side_effect=self.fail_short_write):
            with self.assertRaises(PermissionError):
                with self.scaling:
                    self.fail('The failing setup must not enter the context')
        self.assertEqual(self.values, before)

    def fail_short_write(self, key: object, name: str, reserved: int, kind: int, value: str) -> None:
        """Apply the first path but reject the second registry write.

        Raises:
            PermissionError: The short path is being written.
        """
        if name == self.short:
            raise PermissionError('denied')
        self.write(key, name, reserved, kind, value)
