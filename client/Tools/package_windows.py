"""Validate and archive the Windows x64 Unity player for distribution."""

from __future__ import annotations

import argparse
import hashlib
import struct
import zipfile
from pathlib import Path


def verify_player(root: Path) -> list[Path]:
    """Return distributable files after checking the x64 executable and Unity data.

    Returns:
        list[Path]: Files to include, sorted by relative path.

    Raises:
        ValueError: The expected Windows x64 Unity player is incomplete.
    """
    executable = root / "42 and Beyond.exe"
    if not executable.is_file():
        raise ValueError(f"Missing Windows executable: {executable}")
    with executable.open("rb") as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:2] != b"MZ":
            raise ValueError("Unity player is not a Windows PE executable")
        pe_offset = struct.unpack_from("<I", header, 0x3C)[0]
        stream.seek(pe_offset)
        if stream.read(4) != b"PE\0\0" or struct.unpack("<H", stream.read(2))[0] != 0x8664:
            raise ValueError("Unity player is not a Windows x64 executable")
    required = (root / "UnityPlayer.dll", root / "42 and Beyond_Data" / "globalgamemanagers")
    for path in required:
        if not path.is_file():
            raise ValueError(f"Missing Unity player component: {path}")
    files = sorted(path for path in root.rglob("*") if path.is_file())
    if not files:
        raise ValueError(f"Windows player directory is empty: {root}")
    return files


def package_player(root: Path, output: Path) -> tuple[int, str]:
    """Create a reproducible archive from the verified player directory.

    Returns:
        tuple[int, str]: Archive byte size and SHA-256 digest.

    Raises:
        ValueError: The player is incomplete or the output lies inside it.
    """
    root = root.resolve()
    output = output.resolve()
    if output.is_relative_to(root):
        raise ValueError("Archive output must be outside the player directory")
    files = verify_player(root)
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in files:
            info = zipfile.ZipInfo(path.relative_to(root).as_posix(), (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED,
                             compresslevel=6)
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    return output.stat().st_size, digest


def main() -> None:
    """Validate and package the ignored Unity build output."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-root", type=Path, default=Path("client/Builds/Windows"))
    parser.add_argument("--output", type=Path, default=Path("client/Builds/42-and-Beyond-Windows-x64.zip"))
    args = parser.parse_args()
    size, digest = package_player(args.build_root, args.output)
    print(f"{args.output}: {size} bytes, SHA256 {digest}")


if __name__ == "__main__":
    main()
