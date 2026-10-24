"""Small native launch profiles sharing the existing game installation.

Each owns its writable configuration, proxy files, loopback listeners and job.
Closing one launcher cannot close another client's process tree or undo its INIs.
"""
from __future__ import annotations

from dataclasses import dataclass
import json
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
from typing import TYPE_CHECKING, TypedDict
import uuid

from server.tools.build import ROOT
from .client_config import local_configuration
from .windows_sync import named_mutex
from .shared_install import prepare_shared_install

if TYPE_CHECKING:
    from .local_windows import ProxyJob

CLIENT_SHA256 = '249f852496f341c00361be95b66e7c0becc2c39e7cba026f9227e38b602651c4'


class ProfileRecord(TypedDict):
    version: int
    address: str
    source_client: str
    source_proxy: str
    prepared: bool


def isolate_client_mutex(client: Path, original: Path, name: str) -> None:
    """Give a private, known client build its own singleton identity.

    The installed executable is read-only. Only the 14-byte CreateMutexA name
    changes in its private copy; executable instructions remain identical.
    Refuse an unknown build or a private executable edited by another program.

    Raises:
        RuntimeError: The build is unknown, shared with the original, or externally edited.
    """
    if os.path.samefile(client, original):
        raise RuntimeError('Client instance executable must be an independent copy')
    source = original.read_bytes()
    marker = b'enb_mutex_lock\0'
    if hashlib.sha256(source).hexdigest() != CLIENT_SHA256 or source.count(marker) != 1:
        raise RuntimeError('Unsupported client build; no instance executable changed')
    identity = b'enb_' + hashlib.sha256(name.lower().encode('ascii')).hexdigest()[:10].encode('ascii') + b'\0'
    applied = source.replace(marker, identity)
    current = client.read_bytes()
    if current == applied:
        return
    if current != source:
        raise RuntimeError('Private client executable changed; refusing to overwrite it')
    client.write_bytes(applied)


def validate_instance_name(name: str) -> str:
    """Normalize a name for its private directory, mutex and hostname.

    Returns:
        str: The lowercase instance name.

    Raises:
        ValueError: The name contains unsupported characters or is too long.
    """
    name = name.lower()
    if re.fullmatch(r'[a-z0-9_]{1,24}', name) is None:
        raise ValueError('Instance name must contain 1-24 letters, digits or underscores')
    return name


@dataclass(frozen=True)
class ClientProfile:
    name: str
    address: str
    directory: Path

    @property
    def client(self) -> Path:
        """Return the private game executable.

        Returns:
            Path: The executable copied independently of the installation.
        """
        return self.directory / 'game/release/client.exe'

    @property
    def proxy(self) -> Path:
        """Return the private proxy executable.

        Returns:
            Path: The private proxy path.
        """
        return self.directory / 'proxy/net7proxy.exe'

    @property
    def hostname(self) -> str:
        """Return the instance's local authentication hostname.

        Returns:
            str: The hostname covered by the instance wildcard certificate.
        """
        return f'{self.name}.local.net-7.org'


def prepare_profile(name: str, client: Path, proxy: Path) -> ClientProfile:
    """Prepare private mutable files and allocate a persistent loopback address.

    Returns:
        ClientProfile: The prepared profile, with installed files unchanged.

    Raises:
        ValueError: The name, saved address or source installation is incompatible.
        RuntimeError: All supported loopback addresses are already reserved.
        OSError: A private file cannot be prepared.
    """
    name = validate_instance_name(name)
    parent = ROOT / 'state/clients'
    with named_mutex('Global\\FortyTwoAndBeyondClientProfiles'):
        parent.mkdir(parents=True, exist_ok=True)
        directory = parent / name
        metadata = directory / 'profile.local.json'
        if metadata.exists():
            record: ProfileRecord = json.loads(metadata.read_text())
            address = str(record['address'])
            if address not in {f'127.0.0.{slot}' for slot in range(2, 10)}:
                raise ValueError('Invalid saved instance loopback address')
            if str(record['source_client']) != str(client) or str(record['source_proxy']) != str(proxy):
                raise ValueError('This instance was prepared from a different installation')
        else:
            reserved = {str(json.loads(path.read_text())['address']) for path in parent.glob('*/profile.local.json')}
            address = next((f'127.0.0.{slot}' for slot in range(2,10) if f'127.0.0.{slot}' not in reserved), '')
            if not address: raise RuntimeError('All eight additional native-client profiles are allocated')
            directory.mkdir(parents=True, exist_ok=True)
            record = {'version': 1, 'address': address, 'source_client': str(client),
                      'source_proxy': str(proxy), 'prepared': False}
            metadata.write_text(json.dumps(record, indent=2))
        profile = ClientProfile(name, address, directory)
        if not record['prepared'] or record['version'] < 2:
            print(f'Preparing shared-install launch view for {name}', flush=True)
            prepare_shared_install(client.parent.parent, directory / 'game')
            profile.proxy.parent.mkdir(parents=True, exist_ok=True)
            for path in proxy.parent.iterdir():
                copied = profile.proxy.parent / path.name
                if not copied.exists() and path.is_file() and path.suffix.lower() in {'.exe','.dll','.dat','.cer','.pem','.ini','.cfg'}:
                    shutil.copy2(path, copied)
            record['version'] = 2
            record['prepared'] = True
            metadata.write_text(json.dumps(record, indent=2))
        for relative, kind in [('Data/client/ini/auth.ini','auth'), ('Data/common/network.ini','network'),
                               ('Data/client/ini/rg_regdata.ini','registration')]:
            path = directory / 'game' / relative
            path.write_bytes(local_configuration(path.read_bytes(), kind, hostname=profile.hostname))
        return profile


class ProfileDns:
    def __init__(self, profile: ClientProfile) -> None:
        """Own one exact hosts-file line for this profile's lifetime."""
        self.profile = profile
        self.path = Path(os.environ.get('SystemRoot','C:/Windows')) / 'System32/drivers/etc/hosts'
        self.marker = f'# 42andbeyond-local-instance {profile.name}'.encode('ascii')
        self.line = f'{profile.address}\t{profile.hostname}\t'.encode('ascii') + self.marker + b'\r\n'
        self.before = b''
        self.applied = b''

    def __enter__(self) -> 'ProfileDns':
        """Install only this profile's alias while preserving other launches.

        Returns:
            ProfileDns: The installed alias owner.

        Raises:
            RuntimeError: An unowned entry already uses the instance hostname.
            OSError: The hosts file or DNS cache cannot be updated.
        """
        with named_mutex('Global\\FortyTwoAndBeyondHosts'):
            original = self.path.read_bytes()
            # The profile lifetime mutex proves an old same-profile entry has
            # no active launcher. Never remove an unowned user hosts entry.
            lines = [line for line in original.splitlines(keepends=True) if not self.owns_line(line)]
            for line in lines:
                if self.profile.hostname.encode('ascii') in line.partition(b'#')[0].lower().split():
                    raise RuntimeError('Instance hostname already has an unowned hosts entry')
            (self.profile.directory / f'hosts-before-{uuid.uuid4().hex}.bin').write_bytes(original)
            self.before = b''.join(lines)
            separator = b'\r\n' if self.before and not self.before.endswith(b'\n') else b''
            self.applied = self.before + separator + self.line
            try:
                self.path.write_bytes(self.applied)
                self.flush()
            except BaseException:
                self.restore()
                raise
        return self

    def owns_line(self, line: bytes) -> bool:
        """Match only the complete installed alias, including its address.

        Returns:
            bool: Whether this line can be removed without losing an external edit.
        """
        return line.rstrip(b'\r\n') == self.line.rstrip(b'\r\n')

    def flush(self) -> None:
        """Flush Windows DNS after editing the profile alias.

        Raises:
            subprocess.CalledProcessError: Windows cannot flush DNS.
        """
        subprocess.run([str(Path(os.environ.get('SystemRoot','C:/Windows')) / 'System32/ipconfig.exe'), '/flushdns'],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       creationflags=subprocess.CREATE_NO_WINDOW, check=True)

    def __exit__(self, error_type: object, error: object, traceback: object) -> None:
        """Remove only this profile's unchanged alias."""
        with named_mutex('Global\\FortyTwoAndBeyondHosts'):
            self.restore()
            self.flush()

    def restore(self) -> None:
        """Undo our exact edit, merging only an unchanged owned line after other edits."""
        current = self.path.read_bytes()
        restored = self.before if current == self.applied else b''.join(
            line for line in current.splitlines(keepends=True) if not self.owns_line(line))
        if restored != current:
            self.path.write_bytes(restored)


def launch_profile(name: str, server: str, client: Path, proxy: Path, *, no_build: bool, scale: str | None = None) -> None:
    """Run a named client independently of other profiles and installed clients.

    Raises:
        RuntimeError: The profile is active or the Windows/build requirements fail.
        OSError: Private configuration, processes or local relays cannot be started.
    """
    if os.name != 'nt': raise RuntimeError('Native game profiles require Windows')
    name = validate_instance_name(name)
    from .local_windows import ProxyJob, ensure_no_game
    from .proxy_certificate import LocalProxyCertificate
    from .local_relays import ProfileRelays
    from .start_proxy import ProxyPlan, ensure_server
    from .client_scaling import ClientScaling
    from .proxy_loopback import PROXY_SHA256
    # Validate before preparing launch aliases or editing any configuration.
    if hashlib.sha256(client.read_bytes()).hexdigest() != CLIENT_SHA256:
        raise RuntimeError('Unsupported client build; no instance files changed')
    if hashlib.sha256(proxy.read_bytes()).hexdigest() != PROXY_SHA256:
        raise RuntimeError('Unsupported proxy build; no instance files changed')
    with named_mutex('Global\\FortyTwoAndBeyondClient_' + name.lower(), timeout=0):
        pending = ClientProfile(name, '', ROOT / 'state/clients' / name)
        ensure_no_game(pending.client, pending.proxy)
        profile = prepare_profile(name, client, proxy)
        isolate_client_mutex(profile.client, client, profile.name)
        ensure_server(server, no_build)
        plan = ProxyPlan(server, profile.client, profile.proxy)
        with ProfileDns(profile), LocalProxyCertificate(profile.proxy, instances=True), ClientScaling(profile.client, scale):
            job = ProxyJob()
            relays: ProfileRelays | None = None
            try:
                with named_mutex('Global\\FortyTwoAndBeyondClientStartup', timeout=0):
                    isolate_existing_windows(client)
                    job.start(plan.arguments(), profile.proxy.parent, loopback=profile.address)
                    if job.pid is None: raise RuntimeError('Private proxy process was not created')
                    relays = ProfileRelays(job.pid,profile.address)
                    relays.start()
                    wait_for_client_window(job)
                (profile.directory / 'launch.local.json').write_text(json.dumps({'proxy_pid': job.pid, 'server': server}))
                print(f'Client {profile.name}: proxy PID {job.pid}, local interface {profile.address}. Log in with that account; keep this launcher open.', flush=True)
                job.wait()
            finally:
                try:
                    job.close()
                finally:
                    if relays is not None: relays.close()


def wait_for_client_window(job: ProxyJob) -> None:
    """Keep startup serialized until the owned client finishes its startup dialog.

    Raises:
        RuntimeError: The owned proxy exits before its game is ready.
        TimeoutError: Startup does not finish within two minutes.
    """
    import win32api
    import win32event
    import win32gui
    import win32job
    import win32process
    if job.job is None or job.process is None:
        raise RuntimeError('No owned proxy job to inspect')
    deadline = time.monotonic() + 120
    print('Waiting for this client to open; finish its startup dialog before launching the next instance.', flush=True)
    while time.monotonic() < deadline:
        if win32event.WaitForSingleObject(job.process, 0) != win32event.WAIT_TIMEOUT:
            raise RuntimeError('Private proxy exited before its game opened')
        window = win32gui.FindWindow(None, 'Earth & Beyond')
        if window:
            pid = win32process.GetWindowThreadProcessId(window)[1]
            handle = win32api.OpenProcess(0x1000, False, pid)
            try:
                if win32job.IsProcessInJob(handle, job.job):
                    children: list[int] = []
                    win32gui.EnumChildWindows(window, lambda child, extra: children.append(child), None)
                    if not any(win32gui.GetWindowText(child) == 'I Agree' for child in children):
                        return
            finally:
                handle.Close()
        time.sleep(.1)
    raise TimeoutError('Finish the existing client startup before launching another instance')


def isolate_existing_windows(original_client: Path) -> None:
    """Prevent the legacy proxy attaching to somebody else's canonical window.

    This only changes titles, without focus/input or game state. The process
    handle and repeated window PID check pin each rename to the inspected game.
    """
    import win32api
    import win32gui
    import win32process
    from .local_windows import process_path

    def rename(hwnd: int, context: object) -> None:
        """Rename only verified game windows without sending input."""
        if win32gui.GetWindowText(hwnd) != 'Earth & Beyond': return
        pid = win32process.GetWindowThreadProcessId(hwnd)[1]
        try: handle = win32api.OpenProcess(0x1000, False, pid)
        except (OSError, win32api.error): return
        try:
            path = Path(process_path(int(handle))).resolve()
            if path != original_client and not path.is_relative_to(ROOT / 'state/clients'):
                raise RuntimeError('Another unowned Earth & Beyond window is starting; no proxy launched')
            children: list[int] = []
            win32gui.EnumChildWindows(hwnd, lambda child, extra: children.append(child), None)
            if any(win32gui.GetWindowText(child) == 'I Agree' for child in children):
                raise RuntimeError('Finish the existing client startup dialog before launching another profile')
            if win32process.GetWindowThreadProcessId(hwnd)[1] == pid:
                win32gui.SetWindowText(hwnd, f'ENB_local_{pid}')
        finally: handle.Close()

    win32gui.EnumWindows(rename, None)
