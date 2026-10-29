# Owned Windows native smoke and bootstrap capture

Run Windows commands from this repository root with its normal Python dependencies
(pywin32, Pillow, PyAutoGUI). A Mac agent can use the existing Windows coding agent
or its already-authorized VM guest command channel. Do not start/reboot the VM or
drive an unrelated foreground window. These scripts need no acmacro checkout.

The helper installs observers only in its own Python launcher process. It starts
the normal named-instance launcher, pins the proxy's PID/birth/path, and records
`owner.json`. A private master redirect is changed to `127.0.0.1` only after its
destination port is verified as another listener of that exact proxy. This makes
the sector connection traverse the existing fallback relay. RSA/RC4 session keys
are used transiently in memory; only decrypted server gameplay frames are saved.
Outbound login/ticket frames, master replies, TLS authentication and keys are
never written to capture files.

## Launch

First get a disposable fixture and verify the remote server's health. Back up
the installed crash report into a separate diagnostic folder. Use a fresh run
directory and a dedicated instance name that has no running game/proxy.

```powershell
$env:ENB_NATIVE_SMOKE_RUN_DIR = Join-Path (Get-Location) ('server/logs/native-smoke-' + (Get-Date -Format yyyyMMdd-HHmmss))
$env:PYTHONPATH = (Get-Location).Path
C:\PYTHON\python.exe skills/enb-native-smoke/scripts/capture_proxy.py --server 192.168.47.1 --no-build --scale normal --instance native_smoke
```

Keep the launcher alive. Through a second owned command session, use the same
`ENB_NATIVE_SMOKE_RUN_DIR`. The UI helper discovers only a `client.exe` child of
the pinned proxy, checks its exact expected executable, pins its creation time,
and checks those identities again before every action. Each input action also
requires foreground ownership. It stops if that check fails.

```powershell
C:\PYTHON\python.exe skills/enb-native-smoke/scripts/drive_client.py agree
# Wait for the intro/main game window (the EULA HWND is replaced).
C:\PYTHON\python.exe skills/enb-native-smoke/scripts/drive_client.py skip-intro
# Wait until the login screen is visible. Populate these environment variables
# from the current disposable fixture, without printing them or saving a script.
C:\PYTHON\python.exe skills/enb-native-smoke/scripts/drive_client.py login
```

`login` reads `ENB_NATIVE_PROBE_USERNAME` and `ENB_NATIVE_PROBE_PASSWORD` from its
environment. It uses Pluto's demonstrated click/type sequence, clears a remembered
username, and submits Enter. It saves no credential-form screenshot. Remove the
password environment variable from the driving shell after this step.

Wait for `Got the avatar list packet` in the **owned profile's** `enb_client.log`.
The fixture must have its test character in slot zero. Verify the character
selection screenshot/fixture before proceeding:

```powershell
C:\PYTHON\python.exe skills/enb-native-smoke/scripts/drive_client.py screenshot
C:\PYTHON\python.exe skills/enb-native-smoke/scripts/drive_client.py select
```

`select` uses Pluto's first-slot `(220,52)`, waits seven seconds, then Enter at
`(1207,713)`. Normal mode assumes the known 1280x768 game layout; inspect the owned
screenshot and adapt to a changed layout rather than clicking blindly. Screenshot
capture is DPI-aware; avoid `PrintWindow` for this D3D application.

## Verify and close

- Save an in-space screenshot. Record START_ACK time from server diagnostics or
  an optional gameplay-only UDP capture. Monitor at least 65 seconds from ACK.
- Run `python skills/enb-native-smoke/scripts/check_capture.py RUN_DIR`. It must
  find the full PlayerIndex AUX (ID zero, including RPGInfo) before START. For the
  historical fixtures, player/ship sizes were 1754/1103 and 2361/1771; fixture
  changes can alter these sizes, so do not hard-code them as universal assertions.
- If a UDP `frames.jsonl` capture is available, place it in the run directory for
  byte equality checking. Actual TCP delivery is the essential evidence; UDP
  receipt alone is insufficient. Observe `0x2016` plus `0x201A` continuations for
  oversized frames when inspecting the server wire stream.
- `drive_client.py inventory` is an optional harmless UI response check. Do not
  fight the user's foreground activity: omit it if focus cannot be acquired.
- Compare installed `_except.txt` with the pre-launch backup by bytes and
  timestamp. A fresh crash report must be preserved in full locally.
- Request `drive_client.py quit`, or `close` (WM_CLOSE without stealing focus).
  Confirm both the pinned game and proxy exited. If cleanup needs force, validate
  the recorded PID/birth/path again and stop only the owned launcher job. Never
  use `Stop-Process -Name client` or blanket proxy termination.

Retain `owner.json`, `ui-owner.json`, `tcp-metadata.jsonl`,
`tcp-delivered-frames.jsonl`, screenshots, the crash-report backup and the result
summary. Keep them under ignored `server/logs`, not in Git. If the helper cannot
observe a verified sector connection, report that limitation instead of calling a
missing capture proof of missing network traffic.

## Install the skill on the Mac

The tracked package is `skills/enb-native-smoke`. Copy that directory to
`$CODEX_HOME/skills/enb-native-smoke` (or `~/.codex/skills/enb-native-smoke`) on the
Mac after pulling the published commit. Preserve any locally edited existing
skill before updating it. Its scripts still run on Windows from the repository
root; the Mac agent coordinates and reads the results. Invoke `$enb-native-smoke`
when a native retest is wanted. No recurring automation is installed.
