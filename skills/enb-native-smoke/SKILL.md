---
name: enb-native-smoke
description: Retest the original Earth and Beyond Windows client against the Mac Rust server, capture the game-facing bootstrap without credentials, and diagnose native startup crashes or proxy fragmentation regressions.
---

Use a disposable character and an owned Windows launcher instance. A successful
headless protocol test or START_ACK does not establish that the native client works.

## Coordinate and launch

- Follow the current session's authorization for driving an owned test UI. This
  skill does not authorize driving user clients or restarting a live server/VM.
- Read the active coordination endpoint's health and unread messages. ACK requests
  promptly, identify the server revision under test, and coordinate any deployment.
  The session endpoint was `http://192.168.47.1:8765`; rediscover if unavailable.
- Use a server-provisioned disposable fixture. The successful investigation used a
  minimal level-one character in an empty sector, followed by a geared TE clone.
  Fixture names and passwords are not permanent configuration. Never reuse a user
  account or reset user equipment/progression for a smoke test.
- Use the current repository's Windows launcher with `--no-build --scale normal`
  and a dedicated unused `--instance`. Preserve other instances and dirty files.
  Verify the server is already responding; do not let a test start a local server.
- Back up the installed `release/_except.txt` before launch. A named profile's
  `_except.txt` may be a link to this shared file, even if its link size is zero.

For a routine smoke, use the native UI and monitor at least 65 seconds after
START_ACK. For bootstrap/fragmentation changes, or any crash, use
[the capture procedure](references/windows-capture.md) and the bundled observer.
The capture helper observes only its own launcher process and routes its private
sector connection through existing Python relays; it changes no executable bytes.

## Enter and assess the game

Accept the owned startup **I Agree** button and complete login without asking the
user to log in manually when owned UI operation is authorized. Use the established
Pluto login method or the self-contained equivalent in the capture procedure.
Do not start a user's Pluto route/configuration. Stop input if exact ownership or
foreground focus cannot be established.

Verify the selected disposable character, enter space, and retain a screenshot
with a rendered ship and HUD. Observe for at least 65 seconds; a harmless inventory
panel toggle can check responsiveness. Avoid combat, trades, purchases, and gear
changes. Repeat with the geared fixture when an oversized bootstrap is relevant.

For captured runs, compare server-side UDP with actual proxy-to-game TCP when
available. Run `scripts/check_capture.py RUN_DIR` to require an initial player AUX
before START and report its size/hash, ship AUX sizes, and optional UDP equality.
Do not infer delivery from server logs or UDP receipt alone.

## Failure and completion

Preserve the full new crash report locally and record EIP/read address, frame
ordering, missing AUX records, packet sequences and continuation opcodes. Read
[the confirmed 2026-10-29 diagnosis](references/native-fragmentation.md) for the
`0x0074C150` / `RPGInfo.CombatLevel` crash. Report observed facts separately from
hypotheses. Stop after a reproducible failure and send evidence to the server
agent before another deployment; do not cycle through speculative restarts.

Close only the owned game/proxy after observation and verify their exact processes
exited. Request normal close first; if needed, use pinned PID, creation time, and
executable path to clean up the owned job. `/quit` can close locally while the
server rejects opcode `0x55`; verify process exit instead of assuming logout.

Send the server agent the result and request ACK: tested server revision, fixture
type, duration, rendered UI result, AUX delivery/fragmentation evidence, crash
report change, and owned cleanup. Keep credentials, tickets, session keys and
account files out of captures and coordination messages. Do not claim fixed,
installed, or running based only on a source edit.

This skill supports occasional requested retests; it creates no schedule or
standing permission to interrupt a live session.
