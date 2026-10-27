# Windows x64 client build

The new Unity client is built from this repository's `client` project. It is a
standalone Windows game; it does not modify the original Windows Earth & Beyond
installation.

## Prerequisites

- Unity Editor **6000.3.13f1** with **Windows Build Support (Mono)** for the
  computer doing the build. The module is available for both macOS and Windows
  from [Unity's pinned editor release](https://unity.com/releases/editor/whats-new/6000.3.13f1).
- Python 3.9 or later.
- A clean project import with no other Unity Editor process using `client`.

## One-command build

From the repository root, run one of these scripts. Both call the same Python builder,
which checks the Unity build result, verifies the Windows x64 executable and
Unity data, then writes a ZIP and SHA-256 digest.

On macOS or another Unix shell:

```sh
./client/build_windows.sh
```

On Windows Command Prompt:

```bat
client\build_windows.cmd
```

If Unity is installed outside its standard Hub path, add
`--unity "/full/path/to/Unity"` (macOS) or
`--unity "C:\path\to\Unity.exe"` (Windows), or set `UNITY_EDITOR` to that path.
Run the command from a repository clone with all checked-in Unity assets.

The build appears in ignored `client/Builds/Windows/`; the distributable is
`client/Builds/42-and-Beyond-Windows-x64.zip`. Keep the executable next to its
`42 and Beyond_Data` directory and `UnityPlayer.dll` when extracting the ZIP.
The build log is `client/Builds/windows-build.log`. These outputs are excluded
from Git; a release ZIP can be attached to a GitHub Release.
