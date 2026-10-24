"""Check reversible DNS file edits without changing the operating system."""
import unittest

from .hosts_config import MARKER, local_hosts, restore_local_hosts


class HostsConfigurationTests(unittest.TestCase):
    """Verify preservation, ownership and concurrent-edit behavior."""

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
