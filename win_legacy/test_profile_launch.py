"""Check ownership and failure cleanup without starting a game or proxy."""
from __future__ import annotations

from contextlib import ExitStack, nullcontext
import hashlib
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from .local_profiles import ClientProfile, launch_profile


@unittest.skipUnless(os.name == 'nt', 'Windows launcher process mocks')
class ProfileLaunchTests(unittest.TestCase):
    def test_failing_relay_setup_closes_only_the_owned_job_and_relays(self) -> None:
        """Restore profile contexts after setup failure without touching other clients."""
        with tempfile.TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            client, proxy = root / 'client.exe', root / 'net7proxy.exe'
            client.write_bytes(b'client fixture')
            proxy.write_bytes(b'proxy fixture')
            profile = ClientProfile('pilot1', '127.0.0.2', root / 'state/clients/pilot1')
            stack.enter_context(patch('win_legacy.local_profiles.ROOT', root))
            job = Mock(pid=42)
            relays = Mock()
            relays.start.side_effect = OSError('relay port busy')
            for name, value in (
                ('win_legacy.local_profiles.CLIENT_SHA256', hashlib.sha256(client.read_bytes()).hexdigest()),
                ('win_legacy.proxy_loopback.PROXY_SHA256', hashlib.sha256(proxy.read_bytes()).hexdigest()),
            ):
                stack.enter_context(patch(name, value))
            stack.enter_context(patch('win_legacy.local_profiles.named_mutex', side_effect=lambda *args, **kwargs: nullcontext()))
            prepare = stack.enter_context(patch('win_legacy.local_profiles.prepare_profile', return_value=profile))
            stack.enter_context(patch('win_legacy.local_profiles.isolate_client_mutex'))
            stack.enter_context(patch('win_legacy.local_profiles.isolate_existing_windows'))
            stack.enter_context(patch('win_legacy.local_profiles.ProfileDns', return_value=nullcontext()))
            stack.enter_context(patch('win_legacy.proxy_certificate.LocalProxyCertificate', return_value=nullcontext()))
            scaling = stack.enter_context(patch('win_legacy.client_scaling.ClientScaling', return_value=nullcontext()))
            ensure = stack.enter_context(patch('win_legacy.local_windows.ensure_no_game'))
            stack.enter_context(patch('win_legacy.local_windows.ProxyJob', return_value=job))
            stack.enter_context(patch('win_legacy.local_relays.ProfileRelays', return_value=relays))
            stack.enter_context(patch('win_legacy.start_proxy.ensure_server'))
            with self.assertRaisesRegex(OSError, 'relay port busy'):
                launch_profile('pilot1', '192.168.47.1', client, proxy, no_build=True, scale='normal')
            ensure.assert_called_once_with(profile.client, profile.proxy)
            scaling.assert_called_once_with(profile.client, 'normal')
            self.assertEqual(job.start.call_args.kwargs['loopback'], '127.0.0.2')
            self.assertTrue(job.start.call_args.kwargs['log_path'].is_relative_to(profile.directory / 'logs'))
            job.close.assert_called_once()
            relays.close.assert_called_once()
            job.wait.assert_not_called()
            # A running profile must be rejected before any migration or INI edits.
            prepare.reset_mock()
            ensure.side_effect = RuntimeError('profile already active')
            with self.assertRaisesRegex(RuntimeError, 'profile already active'):
                launch_profile('pilot1', '192.168.47.1', client, proxy, no_build=True)
            prepare.assert_not_called()

    def test_failed_loopback_override_never_resumes_the_owned_proxy(self) -> None:
        """Terminate only the newly suspended proxy if its data override fails."""
        from .local_windows import ProxyJob

        with ExitStack() as stack:
            process, thread = Mock(), Mock()
            process.__int__ = Mock(return_value=123)
            stack.enter_context(patch('win32job.CreateJobObject'))
            stack.enter_context(patch('win32job.QueryInformationJobObject', return_value={'BasicLimitInformation': {'LimitFlags': 0}}))
            stack.enter_context(patch('win32job.SetInformationJobObject'))
            stack.enter_context(patch('win32process.CreateProcess', return_value=(process, thread, 42, 43)))
            stack.enter_context(patch('win_legacy.proxy_loopback.configure_proxy_loopback', side_effect=RuntimeError('unknown literal')))
            terminate = stack.enter_context(patch('win32api.TerminateProcess'))
            resume = stack.enter_context(patch('win32process.ResumeThread'))
            close = stack.enter_context(patch('win32api.CloseHandle'))
            job = ProxyJob()
            with self.assertRaisesRegex(RuntimeError, 'unknown literal'):
                job.start(['net7proxy.exe'], Path('.'), loopback='127.0.0.2')
            job.close()
            terminate.assert_called_once_with(process, 1)
            resume.assert_not_called()
            close.assert_called_once()
            process.Close.assert_called_once()
            thread.Close.assert_called_once()

    def test_proxy_output_is_written_to_private_log_with_scoped_handle_inheritance(self) -> None:
        """Capture native output without changing console handles or game processes."""
        import win32file
        import win32process
        from .local_windows import proxy_output

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'logs/proxy.log'
            startup = win32process.STARTUPINFO()
            with patch('os.set_handle_inheritable', wraps=os.set_handle_inheritable) as set_inherit:
                with proxy_output(startup, path) as inherit:
                    self.assertTrue(inherit)
                    self.assertTrue(os.get_handle_inheritable(int(startup.hStdOutput)))
                    win32file.WriteFile(startup.hStdOutput, b'proxy startup fixture\n')
                self.assertEqual([call.args[1] for call in set_inherit.call_args_list], [True, True, False, False])
            self.assertEqual(path.read_bytes(), b'proxy startup fixture\n')
