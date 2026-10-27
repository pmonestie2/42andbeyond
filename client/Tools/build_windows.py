"""Build and package the Unity Windows x64 client on macOS or Windows."""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

from package_windows import package_player


EDITOR_VERSION = "6000.3.13f1"
PROJECT = Path(__file__).resolve().parents[1]


def find_unity(explicit: Path | None) -> Path:
    """Find the requested Unity editor or this project's pinned installation.

    Returns:
        Path: Existing Unity editor executable.

    Raises:
        FileNotFoundError: No suitable Unity editor is installed.
    """
    env_path = os.environ.get("UNITY_EDITOR")
    candidates = [explicit, Path(env_path) if env_path else None]
    if sys.platform == "darwin":
        candidates.append(Path(f"/Applications/Unity/Hub/Editor/{EDITOR_VERSION}/Unity.app/Contents/MacOS/Unity"))
    elif sys.platform == "win32":
        for base in (os.environ.get("PROGRAMFILES"), os.environ.get("PROGRAMFILES(X86)")):
            if base:
                candidates.append(Path(base) / "Unity" / "Hub" / "Editor" / EDITOR_VERSION /
                                  "Editor" / "Unity.exe")
    for candidate in candidates:
        if candidate is not None and candidate.is_file():
            return candidate.resolve()
    raise FileNotFoundError(
        f"Unity {EDITOR_VERSION} was not found; pass --unity PATH or set UNITY_EDITOR"
    )


def build_windows(unity: Path) -> Path:
    """Run the pinned Unity editor and return its Windows build directory.

    Returns:
        Path: Directory containing the Windows x64 standalone player.

    Raises:
        RuntimeError: Unity exits unsuccessfully or does not report a successful build.
    """
    build_root = PROJECT / "Builds" / "Windows"
    log = PROJECT / "Builds" / "windows-build.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    command = [str(unity), "-batchmode", "-quit", "-projectPath", str(PROJECT),
               "-executeMethod", "FortyTwoAndBeyond.Editor.BuildWindowsClient.BuildWindows",
               "-logFile", str(log)]
    print(f"Building Windows x64 client with {unity}", flush=True)
    result = subprocess.run(command, check=False)
    content = log.read_text(errors="replace") if log.is_file() else ""
    if result.returncode != 0 or "Build Finished, Result: Success." not in content:
        raise RuntimeError(f"Unity Windows build failed (exit {result.returncode}); inspect {log}")
    return build_root


def main() -> None:
    """Build, validate, and archive one reproducible Windows release."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", type=Path, help="Path to this project's Unity 6000.3.13f1 executable")
    args = parser.parse_args()
    root = build_windows(find_unity(args.unity))
    output = PROJECT / "Builds" / "42-and-Beyond-Windows-x64.zip"
    size, digest = package_player(root, output)
    print(f"Windows x64 release: {output} ({size} bytes)\nSHA256 {digest}")


if __name__ == "__main__":
    main()
