# Confirmed native startup failure

On 2026-10-29, START_ACK succeeded and the client then crashed reading address
`0x84` at EIP `0x0074C150`. Static disassembly identified a failed
`RPGInfo.CombatLevel` lookup. The initial theory that a player AUX update deleted
RPGInfo was disproved: the initial player AUX never reached the game.

The decisive minimal-fixture capture showed:

| Location | Initial player AUX |
| --- | --- |
| Server gameplay UDP | Complete 1,754-byte AUX, sequence 1; valid size/checksum |
| Proxy-to-game TCP | Absent; surrounding frames and later reputation AUX present |
| After server fix | Complete 1,754-byte AUX before START; byte-matched UDP |

The geared fixture subsequently delivered the complete 2,361-byte player AUX and
1,771-byte ship AUX, remained rendered/alive for over 150 seconds, and produced no
new `_except.txt`. The minimal fixture survived over 65 seconds.

## Exact proxy contract

Observed installed Net7Proxy SHA-256:
`dbcece6433882234e842696713fef5454da91b90d3fa199e327e3301cd7faf87`.
These binary addresses describe that build only; recheck a different executable.

- `0x412F10`: native ordered packet processing.
- `0x412F25`: default fragment payload limit 512 bytes.
- `0x412F2E`: `/POPT` limit `0x578`, or 1,400 bytes.
- `0x413299` / `0x4132A0`: compare the first inner frame length against this limit.
- `0x4132B6`–`0x4132D3`: larger frames enter continuation assembly; only the
  first fragment's payload is retained, even if the UDP datagram contains more.
- `0x2016` dispatches via `0x40CAC3` with continuation flag 0; `0x201A` via
  `0x40CAD3` with flag 1. Both call `0x4135E0`.

Sending an entire oversized AUX in one UDP datagram violates that contract. The
proxy waits for continuations that were never sent, and the following ordinary
packet resets its pending assembly. Its socket receive buffer is 65,536 bytes;
this is not a socket receive-buffer or OS MTU explanation.

The server already had `split_frames` support, but the native path invoked it only
above 4,096 bytes. The fix was to fragment at `MTU_PAYLOAD` (1,400 for `/POPT`):
first `0x2016`, then `0x201A` continuations, preserving the inner frame length and
content. Do not split an AUX into arbitrary independent records as a substitute
for correct native transport framing.

## Other confirmed defects found in the investigation

- Reputation snapshots omitted fifteen required `0x05` markers for empty nested
  faction entries. The affiliation string was parsed at the wrong boundary.
- Archived nested-record initialization retains the second flag bit even when
  a nested value is present. Treating presence/default bits as mutually exclusive
  for every field was incorrect for nested container records.
- Weapon AUX updates preceded ClientType/player AUX/object creation during login.
  Resetting weapon flags silently removed that startup-order violation.

Those fixes were valid, but each alone left the native crash reproducible. The
TCP before/after capture established the transport defect as the decisive one.

## Evidence pitfalls

- Game-facing TCP uses the repository's public legacy RSA/RC4 protocol. Plain
  frame parsing of the handshake produces misleading results.
- The private master's sector redirect can bypass the localhost fallback relay.
  The IP and port in the observed redirect were little endian; the address
  `127.0.0.6` appeared as `06 00 00 7f`.
- Derive the transient RC4 observer state before forwarding the client RSA
  handshake, otherwise the proxy can return its redirect before the observer is
  ready. Never persist the key or subsequent outbound authentication payloads.
- Only inspect/drive the owned process. Use physical-pixel screenshots for this
  old D3D client; `PrintWindow` can return black even when the game renders.
- Pluto's actual `pyautogui.typewrite` worked. The attempted mapped-scan-code
  `keybd_event` sequence did not populate the fields. Empty fields are not proof
  of server authentication failure.
- A proxy registration can expire while diagnostics sit at login; the observed
  server validity was 600 seconds. Restart only the owned launcher/client pair
  and promptly complete login if the server reports registration is missing.
