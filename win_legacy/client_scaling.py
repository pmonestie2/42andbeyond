"""Temporarily set the original client's Windows DPI compatibility flags."""
from __future__ import annotations

import json
from pathlib import Path
import uuid

from server.tools.build import ROOT


LAYERS = r'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
DPI_FLAGS = {'HIGHDPIAWARE', 'DPIUNAWARE', 'GDIDPISCALING'}


def scaling_flags(original: str, scale: str) -> str:
    """Replace DPI flags while retaining unrelated compatibility settings.

    Returns:
        str: Compatibility flags for the requested scaling mode.

    Raises:
        ValueError: The scaling mode is unknown.
    """
    if scale not in ('normal', 'double'):
        raise ValueError('Scale must be normal or double')
    kept = [flag for flag in original.split() if flag not in DPI_FLAGS | {'~'}]
    return ' '.join(['~', *kept, 'HIGHDPIAWARE' if scale == 'normal' else 'DPIUNAWARE'])


class ClientScaling:
    """Restore owned registry values on exit, preserving concurrent edits."""

    def __init__(self, client: Path, scale: str | None) -> None:
        """Track the client paths and recovery backup for an optional DPI override."""
        self.client = client
        self.scale = scale
        self.edits: list[tuple[str, str | None, str]] = []
        self.backup = ROOT / 'logs' / f'dpi-before-{uuid.uuid4().hex}.json'

    def __enter__(self) -> ClientScaling:
        """Apply scaling to both the long and legacy short executable paths.

        Returns:
            ClientScaling: The context owning the temporary compatibility flags.

        Raises:
            OSError: Windows could not read or write the compatibility settings.
            ValueError: A compatibility value has an unexpected registry type.
        """
        if self.scale is None:
            return self
        import win32api
        import winreg

        paths = list(dict.fromkeys([str(self.client), win32api.GetShortPathName(str(self.client))]))
        with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, LAYERS, access=winreg.KEY_READ | winreg.KEY_WRITE) as key:
            planned: list[tuple[str, str | None, str]] = []
            for path in paths:
                try:
                    original, kind = winreg.QueryValueEx(key, path)
                    if kind != winreg.REG_SZ or not isinstance(original, str):
                        raise ValueError(f'Unexpected compatibility value for {path}')
                except FileNotFoundError:
                    original = None
                planned.append((path, original, scaling_flags(original or '', self.scale)))
            self.backup.parent.mkdir(parents=True, exist_ok=True)
            self.backup.write_text(json.dumps(planned, indent=2), encoding='utf-8')
            try:
                for path, original, applied in planned:
                    winreg.SetValueEx(key, path, 0, winreg.REG_SZ, applied)
                    self.edits.append((path, original, applied))
            except BaseException:
                self.restore()
                raise
        return self

    def restore(self) -> None:
        """Restore unchanged values and retain backups for concurrent edits.

        Raises:
            OSError: Windows could not restore the compatibility settings.
        """
        if not self.edits:
            return
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, LAYERS, access=winreg.KEY_READ | winreg.KEY_WRITE) as key:
            for path, original, applied in reversed(self.edits):
                try:
                    current, kind = winreg.QueryValueEx(key, path)
                except FileNotFoundError:
                    continue
                if current != applied or kind != winreg.REG_SZ:
                    print(f'Client scaling changed while running; backup retained at {self.backup}', flush=True)
                    continue
                if original is None:
                    winreg.DeleteValue(key, path)
                else:
                    winreg.SetValueEx(key, path, 0, winreg.REG_SZ, original)
        self.edits.clear()

    def __exit__(self, error_type: object, error: object, traceback: object) -> None:
        """Restore scaling after the owned game and proxy have stopped."""
        self.restore()
