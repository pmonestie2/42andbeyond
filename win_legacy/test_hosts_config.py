"""Check reversible DNS file edits without changing the operating system."""
import unittest

from .hosts_config import BOOTSTRAP_LINE, MARKER, local_hosts, restore_local_hosts, proxy_bootstrap_hosts


class HostsConfigurationTests(unittest.TestCase):
    """Verify preservation, ownership and concurrent-edit behavior."""

    def test_named_only_launch_installs_bootstrap_and_removes_it_on_last_exit(self) -> None:
        """Resolve the proxy's fixed name without needing a primary client."""
        original = b'127.0.0.1 localhost\r\n'
        alias = b'127.0.0.2 pilot.local.net-7.org # 42andbeyond-local-instance pilot\r\n'
        applied = proxy_bootstrap_hosts(original + alias)
        self.assertIn(BOOTSTRAP_LINE, applied)
        self.assertEqual(proxy_bootstrap_hosts(applied.replace(alias, b'')), original)

    def test_primary_exit_keeps_bootstrap_until_last_named_client_exits(self) -> None:
        """Keep startup resolution working across primary/named cleanup order."""
        original = b'127.0.0.1 localhost\r\n'
        primary = local_hosts(original, MARKER)
        alias = b'127.0.0.2 pilot.local.net-7.org # 42andbeyond-local-instance pilot\r\n'
        restored = restore_local_hosts(original, primary, primary + alias)
        self.assertIsNotNone(restored)
        assert restored is not None
        self.assertIn(BOOTSTRAP_LINE, restored)
        self.assertEqual(proxy_bootstrap_hosts(restored.replace(alias, b'')), original)

    def test_named_bootstrap_preserves_existing_or_externally_changed_user_mapping(self) -> None:
        """Do not overwrite a user-owned base hostname on setup or cleanup."""
        user = b'10.0.0.9 local.net-7.org other.local # keep\r\n'
        alias = b'127.0.0.2 pilot.local.net-7.org # 42andbeyond-local-instance pilot\r\n'
        self.assertEqual(proxy_bootstrap_hosts(user + alias), user + alias)
        changed = BOOTSTRAP_LINE.replace(b'127.0.0.1', b'127.0.0.8')
        self.assertEqual(proxy_bootstrap_hosts(changed), changed)

    def test_named_then_primary_cleanup_does_not_restore_a_stale_alias(self) -> None:
        """Remove shared setup once both launchers have exited in either order."""
        original = b'127.0.0.1 localhost\r\n'
        alias = b'127.0.0.2 pilot.local.net-7.org # 42andbeyond-local-instance pilot\r\n'
        named = proxy_bootstrap_hosts(original + alias)
        primary = local_hosts(named, MARKER)
        after_named_exit = proxy_bootstrap_hosts(primary.replace(alias, b''))
        self.assertEqual(restore_local_hosts(named, primary, after_named_exit), original)

    def test_preserves_other_aliases_and_comments(self) -> None:
        """Remove the old alias while preserving its other names and comment."""
        original = b'10.0.0.2 local.net-7.org other.local # keep this\r\n'
        applied = local_hosts(original, MARKER)
        self.assertIn(b'10.0.0.2\tother.local # keep this', applied)
        self.assertIn(b'127.0.0.1\tlocal.net-7.org', applied)
        self.assertEqual(restore_local_hosts(original, applied, applied), original)

    def test_refuses_an_existing_owned_override(self) -> None:
        """Do not overwrite another launcher's active DNS edit."""
        with self.assertRaises(RuntimeError):
            local_hosts(b'127.0.0.1 local.net-7.org # ' + MARKER, MARKER)

    def test_concurrent_edits_require_manual_merge(self) -> None:
        """Do not replace a hosts file changed by another program."""
        original = b'127.0.0.1 localhost\r\n'
        applied = local_hosts(original, MARKER)
        changed = applied + b'10.0.0.3 another.local\r\n'
        self.assertIsNone(restore_local_hosts(original, applied, changed))
