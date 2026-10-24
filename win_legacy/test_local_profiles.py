"""Private installation and alias ownership checks; no processes or live clients."""
from __future__ import annotations

from contextlib import nullcontext
import os
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from .local_profiles import ClientProfile, ProfileDns, copy_client, prepare_profile, isolate_client_mutex, validate_instance_name


class ProfileTests(unittest.TestCase):
    def test_names_are_normalized_and_cannot_escape_profile_directory(self) -> None:
        """Reject traversal and names that cannot become safe host aliases."""
        self.assertEqual(validate_instance_name('Pilot1'), 'pilot1')
        for name in ('../pilot', '', 'x' * 25, 'pilot.two', 'a/b'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                validate_instance_name(name)

    def test_two_dns_owners_close_in_either_order_preserving_user_edits(self) -> None:
        """Never undo another active profile alias or unrelated concurrent edits."""
        for order in ((0, 1), (1, 0)):
            with self.subTest(order=order), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                hosts = root / 'hosts'
                original = b'127.0.0.1 localhost\r\n'
                hosts.write_bytes(original)
                owners = []
                for index, name in enumerate(('first', 'second')):
                    directory = root / name
                    directory.mkdir()
                    owner = ProfileDns(ClientProfile(name, f'127.0.0.{index + 2}', directory))
                    owner.path = hosts
                    owners.append(owner)
                with patch('win_legacy.local_profiles.named_mutex', side_effect=lambda *args, **kwargs: nullcontext()), \
                     patch.object(ProfileDns, 'flush'):
                    for owner in owners:
                        owner.__enter__()
                    user_edit = b'10.0.0.4 user.local\r\n'
                    hosts.write_bytes(hosts.read_bytes() + user_edit)
                    owners[order[0]].__exit__(None, None, None)
                    self.assertIn(owners[order[1]].line, hosts.read_bytes())
                    owners[order[1]].__exit__(None, None, None)
                    self.assertEqual(hosts.read_bytes(), original + user_edit)

    @unittest.skipUnless(os.name == 'nt', 'Windows primary-client hosts manager')
    def test_primary_dns_cleanup_preserves_current_profiles_and_unknown_edits(self) -> None:
        """Keep current private aliases when the primary launcher restores DNS."""
        from .local_windows import MARKER, local_hosts, restore_local_hosts
        original = b'127.0.0.1 localhost\r\n192.168.1.1 local.net-7.org other\r\n'
        first = b'127.0.0.2 third.local.net-7.org # 42andbeyond-local-instance third\r\n'
        second = b'127.0.0.3 buddy.local.net-7.org # 42andbeyond-local-instance buddy\r\n'
        before = original + first
        applied = local_hosts(before, MARKER + b' fixture')
        current = applied.replace(first,b'') + second
        restored = restore_local_hosts(before,applied,current)
        self.assertIsNotNone(restored)
        assert restored is not None
        self.assertNotIn(first,restored)
        self.assertIn(second,restored)
        self.assertIn(b'192.168.1.1 local.net-7.org other',restored)
        self.assertNotIn(MARKER,restored)
        self.assertIsNone(restore_local_hosts(before,applied,current + b'1.2.3.4 user-edit\r\n'))

    def test_singleton_identity_is_private_repeatable_and_preserves_unknown_edits(self) -> None:
        """Patch only the known private singleton and reject unexpected edits."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            original = root / 'installed.exe'
            contents = b'fixture executable\0enb_mutex_lock\0unchanged instructions'
            original.write_bytes(contents)
            first, second = root / 'first.exe', root / 'second.exe'
            first.write_bytes(contents)
            second.write_bytes(contents)
            with patch('win_legacy.local_profiles.CLIENT_SHA256', hashlib.sha256(contents).hexdigest()):
                isolate_client_mutex(first, original, 'third')
                expected = first.read_bytes()
                isolate_client_mutex(first, original, 'third')
                self.assertEqual(first.read_bytes(), expected)
                isolate_client_mutex(second, original, 'buddy')
                self.assertNotEqual(first.read_bytes(), second.read_bytes())
                self.assertEqual(len(expected), len(contents))
                with self.assertRaisesRegex(RuntimeError, 'independent copy'):
                    isolate_client_mutex(original, original, 'third')
                first.write_bytes(b'changed externally')
                with self.assertRaisesRegex(RuntimeError, 'refusing to overwrite'):
                    isolate_client_mutex(first, original, 'third')
                self.assertEqual(first.read_bytes(), b'changed externally')
            self.assertEqual(original.read_bytes(), contents)

    def test_copies_settings_and_links_only_immutable_assets(self) -> None:
        """Copy writable files independently while sharing immutable assets."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, target = root / 'source', root / 'target'
            for relative, content in [('release/client.exe',b'client'),('Data/client/ini/auth.ini',b'original'),
                                      ('Data/client/art/model.w3d',b'model'),('Data/client/output/cache.pld',b'cache')]:
                path = source / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
            copy_client(source, target)
            (target / 'Data/client/ini/auth.ini').write_bytes(b'private')
            self.assertEqual((source / 'Data/client/ini/auth.ini').read_bytes(),b'original')
            self.assertTrue(os.path.samefile(source / 'Data/client/art/model.w3d',target / 'Data/client/art/model.w3d'))
            self.assertFalse(os.path.samefile(source / 'Data/client/output/cache.pld',target / 'Data/client/output/cache.pld'))

    def test_profiles_reserve_distinct_interfaces_and_keep_original_endpoints(self) -> None:
        """Allocate distinct aliases without modifying installed INI files."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            client, proxy = root / 'source/release/client.exe', root / 'proxy/net7proxy.exe'
            client.parent.mkdir(parents=True)
            proxy.parent.mkdir(parents=True)
            client.write_bytes(b'client')
            proxy.write_bytes(b'proxy')
            original = {
                'Data/client/ini/auth.ini': b'AAIUrl=original\nLKeyUrl=https://original/login\n',
                'Data/common/network.ini': b'Name=original\n',
                'Data/client/ini/rg_regdata.ini': b'regserverurl=https://original/registration\n'}
            for relative, contents in original.items():
                path = client.parent.parent / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(contents)
            with patch('win_legacy.local_profiles.ROOT',root / 'server'), patch('win_legacy.local_profiles.named_mutex',return_value=nullcontext()):
                first = prepare_profile('third',client,proxy)
                second = prepare_profile('buddy',client,proxy)
                self.assertNotEqual(first.address,second.address)
                self.assertIn(first.hostname.encode(),(first.directory / 'game/Data/client/ini/auth.ini').read_bytes())
                self.assertEqual(prepare_profile('third',client,proxy).address,first.address)
            for relative,contents in original.items():
                self.assertEqual((client.parent.parent / relative).read_bytes(),contents)

    def test_dns_cleanup_matches_full_profile_name(self) -> None:
        """Match exact profile aliases and preserve externally changed lines."""
        dns = ProfileDns(ClientProfile('third','127.0.0.2',Path('unused')))
        self.assertTrue(dns.owns_line(dns.line))
        self.assertFalse(dns.owns_line(dns.line.replace(b'127.0.0.2', b'127.0.0.9')))
        self.assertFalse(dns.owns_line(b'127.0.0.3 third2.local.net-7.org # 42andbeyond-local-instance third2\r\n'))
        self.assertFalse(dns.owns_line(b'127.0.0.2 third.local.net-7.org # user entry\r\n'))
