"""Windows-only isolation for the optional original game/proxy launcher.

The Rust server never imports or depends on this module. A job object owns only
the proxy launched here and its descendants. Closing it ends those processes
before the temporary local-certificate DNS override is restored.
"""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import uuid

import win32api
import win32con
import win32event
import win32job
import win32process

from server.tools.build import ROOT
from server.tools.process_info import process_path

from .hosts_config import MARKER, local_hosts, restore_local_hosts




class LocalCertificateDns:
    def __init__(self) -> None:
        self.path = Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/drivers/etc/hosts'
        self.original = b''
        self.applied = b''
        self.backup = ROOT / 'logs' / f'hosts-before-{uuid.uuid4().hex}.bin'

    def __enter__(self) -> 'LocalCertificateDns':
        from .windows_sync import named_mutex
        with named_mutex('Global\\FortyTwoAndBeyondHosts'):
            return self.install()

    def install(self) -> 'LocalCertificateDns':
        self.original = self.path.read_bytes()
        self.applied = local_hosts(self.original, MARKER + b' ' + self.backup.stem.encode('ascii'))
        self.backup.parent.mkdir(exist_ok=True)
        self.backup.write_bytes(self.original)
        try:
            self.path.write_bytes(self.applied)
            self.flush()
        except PermissionError:
            raise PermissionError('Run win_legacy/launch_original_client.cmd as administrator so its temporary local certificate DNS entry can be installed') from None
        except BaseException:
            if self.path.read_bytes() == self.applied:
                self.path.write_bytes(self.original)
            raise
        return self

    def flush(self) -> None:
        subprocess.run([str(Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/ipconfig.exe'), '/flushdns'],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW, check=True)

    def __exit__(self, error_type: object, error: object, traceback: object) -> None:
        from .windows_sync import named_mutex
        with named_mutex('Global\\FortyTwoAndBeyondHosts'):
            self.restore()

    def restore(self) -> None:
        current = self.path.read_bytes()
        restored = restore_local_hosts(self.original, self.applied, current)
        if restored is not None:
            self.path.write_bytes(restored)
            self.flush()
        else:
            # Do not overwrite another program's concurrent hosts-file edits.
            # Retain the backup and identify the recovery path explicitly.
            print(f'Hosts file changed while the sandbox ran; restore its local.net-7.org entry using {self.backup}', flush=True)



class ProxyJob:
    def __init__(self) -> None:
        self.job: int | None = None
        self.process: int | None = None
        self.pid: int | None = None

    def start(self, arguments: list[str], directory: Path) -> None:
        job = win32job.CreateJobObject(None, '')
        settings = win32job.QueryInformationJobObject(job, win32job.JobObjectExtendedLimitInformation)
        settings['BasicLimitInformation']['LimitFlags'] |= win32job.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        win32job.SetInformationJobObject(job, win32job.JobObjectExtendedLimitInformation, settings)
        self.job = job.Detach()
        startup = win32process.STARTUPINFO()
        startup.dwFlags = win32con.STARTF_USESHOWWINDOW
        startup.wShowWindow = win32con.SW_HIDE
        # Suspend before assigning the job so the game cannot spawn outside our
        # owned process tree between CreateProcess and AssignProcessToJobObject.
        process, thread, pid, _ = win32process.CreateProcess(arguments[0], subprocess.list2cmdline(arguments), None, None, False,
            win32con.CREATE_SUSPENDED | win32con.CREATE_NO_WINDOW, None, str(directory), startup)
        try:
            win32job.AssignProcessToJobObject(self.job, process)
            win32process.ResumeThread(thread)
            self.process, self.pid = process.Detach(), pid
        except BaseException:
            win32api.TerminateProcess(process, 1)
            process.Close()
            raise
        finally:
            thread.Close()

    def wait(self) -> int:
        if self.process is None:
            raise RuntimeError('No owned local proxy process')
        while win32event.WaitForSingleObject(self.process, 250) == win32event.WAIT_TIMEOUT:
            pass
        return win32process.GetExitCodeProcess(self.process)

    def close(self) -> None:
        if self.job is not None:
            win32api.CloseHandle(self.job)
            self.job = None
        if self.process is not None:
            win32event.WaitForSingleObject(self.process, 5000)
            win32api.CloseHandle(self.process)
            self.process = None



def ensure_no_game(client: Path, proxy: Path) -> None:
    expected = {os.path.normcase(str(client)), os.path.normcase(str(proxy))}
    for pid in win32process.EnumProcesses():
        try:
            handle = win32api.OpenProcess(0x1000, False, pid)
        except (OSError, win32api.error):
            continue
        try:
            path = process_path(int(handle))
            if os.path.normcase(path) in expected:
                raise RuntimeError('Close existing game clients and proxies before starting the isolated local sandbox')
        except (OSError, win32api.error):
            continue
        finally:
            handle.Close()
