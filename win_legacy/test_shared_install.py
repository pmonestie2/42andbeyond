"""Verify shared installation aliases and safe migration using disposable files."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch

from .shared_install import prepare_shared_install


class SharedInstallTests(unittest.TestCase):
    def setUp(self) -> None:
        """Create a disposable installed tree and a separate launch view."""
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source, self.view = self.root / 'installed', self.root / 'profile/game'
        self.contents = {
            'release/client.exe': b'client',
            'release/helper.dll': b'library',
            'Data/client/ini/auth.ini': b'AAIUrl=installed',
            'Data/client/ini/preferences.ini': b'preferences',
            'Data/common/network.ini': b'Name=installed',
            'Data/common/sector.ini': b'geometry',
            'Data/client/art/ship/model.w3d': b'model',
            'Data/client/output/cache.pld': b'old cache',
            'release/chat.log': b'live log',
            'release/crash.dmp': b'dump',
        }
        for relative, content in self.contents.items():
            path = self.source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        try:
            os.symlink(self.source, self.root / 'symlink-probe', target_is_directory=True)
        except OSError as error:
            self.skipTest(f'Directory symlink creation unavailable: {error}')

    def test_shares_installed_assets_without_walking_or_copying_them(self) -> None:
        """Link asset directories directly and copy only private settings/executable."""
        with patch('win_legacy.shared_install.os.walk', side_effect=AssertionError('Must not enumerate installed assets')):
            prepare_shared_install(self.source, self.view)
        art = self.view / 'Data/client/art'
        self.assertTrue(art.is_symlink())
        self.assertEqual(art.resolve(), (self.source / 'Data/client/art').resolve())
        self.assertTrue((self.view / 'release/helper.dll').is_symlink())
        self.assertTrue((self.view / 'Data/common/sector.ini').is_symlink())
        for relative in ('release/client.exe', 'Data/client/ini/auth.ini', 'Data/common/network.ini'):
            self.assertFalse(os.path.samefile(self.source / relative, self.view / relative))
            (self.view / relative).write_bytes(b'private change')
            self.assertEqual((self.source / relative).read_bytes(), self.contents[relative])
        self.assertFalse((self.view / 'release/chat.log').exists())
        self.assertFalse((self.view / 'release/crash.dmp').exists())
        self.assertEqual(list((self.view / 'Data/client/output').iterdir()), [])
        (self.source / 'Data/client/art/ship/model.w3d').write_bytes(b'installed update')
        self.assertEqual((art / 'ship/model.w3d').read_bytes(), b'installed update')

    def test_migrates_old_asset_copies_and_keeps_private_edits_and_output(self) -> None:
        """Replace only verified old assets, retaining preferences and client logs."""
        shutil.copytree(self.source, self.view)
        preferences = self.view / 'Data/client/ini/preferences.ini'
        preferences.write_bytes(b'my controls')
        prepare_shared_install(self.source, self.view)
        self.assertTrue((self.view / 'Data/client/art').is_symlink())
        self.assertEqual(preferences.read_bytes(), b'my controls')
        self.assertEqual((self.view / 'release/chat.log').read_bytes(), b'live log')
        self.assertEqual((self.view / 'Data/client/output/cache.pld').read_bytes(), b'old cache')
        for relative, content in self.contents.items():
            self.assertEqual((self.source / relative).read_bytes(), content)
        # A repeat preparation must accept an already patched private executable.
        (self.view / 'release/client.exe').write_bytes(b'private singleton')
        prepare_shared_install(self.source, self.view)
        self.assertEqual((self.view / 'release/client.exe').read_bytes(), b'private singleton')

    def test_backs_up_old_asset_changes_and_uses_installed_assets(self) -> None:
        """Preserve differing files while allowing the shared-install migration."""
        shutil.copytree(self.source, self.view)
        edited = self.view / 'Data/client/art/ship/model.w3d'
        edited.write_bytes(b'custom model')
        extra = edited.parent / 'global.th6'
        extra.write_bytes(b'old thumbnail data')
        prepare_shared_install(self.source, self.view)
        self.assertEqual(edited.read_bytes(), b'model')
        self.assertEqual((self.source / 'Data/client/art/ship/model.w3d').read_bytes(), b'model')
        backups = list((self.view.parent / 'migration-backups').glob('*'))
        self.assertEqual(len(backups), 1)
        self.assertEqual((backups[0] / 'Data/client/art/ship/model.w3d').read_bytes(), b'custom model')
        self.assertEqual((backups[0] / 'Data/client/art/ship/global.th6').read_bytes(), b'old thumbnail data')

    def test_rejects_private_directory_alias_to_installed_config(self) -> None:
        """Never write a profile's private settings through an installed-data alias."""
        (self.view / 'Data/client').mkdir(parents=True)
        os.symlink(self.source / 'Data/client/ini', self.view / 'Data/client/ini', target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, 'must not be a link'):
            prepare_shared_install(self.source, self.view)
        self.assertEqual((self.source / 'Data/client/ini/auth.ini').read_bytes(), b'AAIUrl=installed')
