"""Prepare a small launch view over the existing game installation."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import stat


PRIVATE_FILES = {
    'release/client.exe',
    'data/common/network.ini',
}
PRIVATE_DIRECTORIES = {'data/client/ini', 'data/client/output', 'logs'}
RUNTIME_SUFFIXES = {'.log', '.dmp'}


def is_directory_link(path: Path) -> bool:
    """Recognize symbolic links and Windows junctions without traversing them.

    Returns:
        bool: Whether the path is a link or directory reparse point.
    """
    if path.is_symlink():
        return True
    if not path.exists():
        return False
    return path.is_dir() and bool(getattr(path.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def share_directory(source: Path, target: Path, view: Path) -> None:
    """Link an installed folder, converting an unchanged former profile copy.

    Raises:
        RuntimeError: The old folder has local edits or an unsafe link boundary.
        OSError: Windows cannot remove old profile links or create the alias.
    """
    if is_directory_link(target):
        if target.resolve() != source.resolve():
            raise RuntimeError(f'Shared folder link changed; preserving {target}')
        return
    if target.exists():
        # Validate the complete absolute removal boundary before enumerating it.
        boundary = view.resolve()
        resolved = target.resolve()
        if resolved == boundary or not resolved.is_relative_to(boundary) or resolved == source.resolve():
            raise RuntimeError(f'Unsafe old profile folder; preserving {target}')
        files: list[tuple[Path, os.stat_result]] = []
        directories: list[Path] = []
        for directory, folders, names in os.walk(target):
            current = Path(directory)
            directories.append(current)
            for name in folders:
                child = current / name
                if is_directory_link(child):
                    raise RuntimeError(f'Old profile has an unexpected folder link; preserving {child}')
            for name in names:
                copied = current / name
                original = source / copied.relative_to(target)
                if (copied.is_symlink() or not original.is_file()
                        or (not os.path.samefile(copied, original) and copied.read_bytes() != original.read_bytes())):
                    raise RuntimeError(f'Old profile has local changes; preserving {copied}')
                files.append((copied, copied.stat()))
        # All files are verified installation copies/hard links. Unlinking these
        # names leaves the installed originals intact; never recurse into aliases.
        for copied, before in files:
            if not copied.resolve().is_relative_to(boundary):
                raise RuntimeError(f'Old profile removal boundary changed; preserving {copied}')
            current = copied.stat()
            if (current.st_ino, current.st_size, current.st_mtime_ns) != (before.st_ino, before.st_size, before.st_mtime_ns):
                raise RuntimeError(f'Old profile changed during conversion; preserving {copied}')
            copied.unlink()
        for directory in reversed(directories):
            if not directory.resolve().is_relative_to(boundary):
                raise RuntimeError(f'Old profile removal boundary changed; preserving {directory}')
            directory.rmdir()
    os.symlink(source.resolve(), target, target_is_directory=True)


def prepare_shared_install(source: Path, destination: Path) -> None:
    """Share installed assets, keeping only executable/configuration/output private.

    Raises:
        RuntimeError: An existing profile entry was changed or aliases a private path.
        OSError: A required file or alias cannot be prepared.
    """
    source = source.resolve()
    destination = destination.absolute()
    if (is_directory_link(destination) or destination.resolve().is_relative_to(source)
            or source.is_relative_to(destination.resolve())):
        raise RuntimeError('Launch view must be independent of the installed directory')
    prepare_view_directory(source, destination, destination)


def prepare_view_directory(source: Path, target: Path, view: Path) -> None:
    """Build only the directory skeleton needed around private configuration.

    Raises:
        RuntimeError: A private directory or file unexpectedly points into the installation.
        OSError: Windows cannot copy a required file or create a shared alias.
    """
    if is_directory_link(target):
        raise RuntimeError(f'Private launch directory must not be a link: {target}')
    target.mkdir(parents=True, exist_ok=True)
    for original in source.iterdir():
        relative = (target / original.name).relative_to(view).as_posix().lower()
        copied = target / original.name
        if original.suffix.lower() in RUNTIME_SUFFIXES:
            continue
        if original.is_dir():
            if relative in {'data/client/output', 'logs'}:
                if is_directory_link(copied):
                    raise RuntimeError(f'Runtime output must be private: {copied}')
                copied.mkdir(exist_ok=True)
            elif (relative.startswith('data/client/ini/') or any(
                    path == relative or path.startswith(relative + '/') for path in PRIVATE_FILES | PRIVATE_DIRECTORIES)):
                prepare_view_directory(original, copied, view)
            else:
                share_directory(original, copied, view)
            continue
        private = relative in PRIVATE_FILES or relative.startswith('data/client/ini/')
        if copied.is_symlink():
            if private or copied.resolve() != original.resolve():
                raise RuntimeError(f'Launch file link changed; preserving {copied}')
            continue
        if private:
            if copied.exists() and os.path.samefile(copied, original):
                raise RuntimeError(f'Private launch file aliases the installation: {copied}')
            if not copied.exists():
                shutil.copy2(original, copied)
        else:
            if copied.exists():
                if not os.path.samefile(copied, original) and copied.read_bytes() != original.read_bytes():
                    raise RuntimeError(f'Old profile has local changes; preserving {copied}')
                copied.unlink()
            os.symlink(original.resolve(), copied)
