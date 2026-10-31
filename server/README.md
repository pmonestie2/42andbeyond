# 42 and Beyond server

A portable Rust server with authoritative space movement, groups and formations,
combat, inventory, loot, mining, station services, persistence and native protocol
compatibility. The embedded public content catalogs are in `data/`.

Hosted stations preserve the original native rooms, terminal locations and NPC
placements. Legacy clients walk to each contact's actual room. The Unity client
can negotiate an aggregate station presentation with `0x203a` (player ID and a
zero/one flag, both little-endian u32); `0x203b` acknowledges those same fields.
This permits access to every contact in the current station while retaining all
station ownership and avatar-presence checks. The capability survives handoffs
and brief server restarts and resets on fresh authenticated character selection.
Aggregate recipients also see avatars from every room in the current station;
native recipients continue to see only avatars in their own room.

Large station metadata updates are paced to eight UDP packets per simulation
tick. Clients that acknowledge reliable output use a 64-packet flight window;
unacknowledged packets and the unsent suffix survive server replacement. Legacy
proxies without ACK support retain the paced rolling 128-packet replay contract.
Native manufacturing books use complete AUX diffs of twenty formula slots each,
and custom extensions are isolated from native lab initialization messages.
Unchanged formula books remain in place during quote refreshes; analysis and
dismantling do not resend the manufacturing catalog.

In space, `/d_mob_cl 10` sets all living MOBs whose centers are within 20 km to
combat level 10. Supported levels are 1 through 66. It uses the same native stat
derivation as `/d_spawn ... level=10`, preserves damage fractions, and saves the
explicit level override in the shared world. Every observing client receives the
updated level and resources. Dead MOBs and actors beyond 20 km are unchanged.

`/d_warp` instantly moves the ship near its currently selected target. An optional
native object ID, `/d_warp 100002`, selects a same-sector nav, station, resource,
living MOB, or online pilot directly. Arrival stays outside the target radius
with 545 m clearance, stops warp/follow/fire, and commits before broadcasting the
new position. All station exteriors are advertised as navigation destinations,
including those marked hidden in the public source content.

Sector handoffs retry their unacknowledged UDP departure window once per second,
using the original packet sequences. This repairs a lost final redirect or an
earlier ordered-packet gap even while sector simulation is paused. Retries stop
when the destination handshake clears that window. Station departures also clear
the native manufacture binding before redirect, including checkpoint recovery.

With the server stopped, grant every existing character all recipes supported by
its currently trained build skills:

```sh
server/target/release/beyond-server --state-dir server/state --grant-build-recipes --dry-run
server/target/release/beyond-server --state-dir server/state --grant-build-recipes
```

The command refuses to run while that state directory is owned by a server. It
preserves existing recipes and all other inventory JSON fields, writes an original
`inventory.before-recipe-grant.json` backup once, and is safe to rerun. Native
characters retain their purchased skill ranks. Recipe books can contain up to
16,384 entries; the manufacturing list uses 500-slot pages so every learned recipe
remains accessible. Changing category or level filters returns to page one.

With the server stopped, refill every pilot's cargo with compatible ammunition:

```sh
server/target/release/beyond-server --state-dir server/state --refill-cargo-ammo --dry-run
server/target/release/beyond-server --state-dir server/state --refill-cargo-ammo
```

This additive maintenance command targets five full cargo stacks per equipped
launcher, preferring its loaded ammunition. It preserves fitted gear, magazines,
credits, unrelated cargo and unknown inventory fields, writes each original
`inventory.before-ammo-refill.json` backup once, and reports any cargo-space
shortfall. Beam-only ships need no ammunition. Unsettled launch journals are
reported and deferred. Transport checkpoints never replace the newer cargo on
resume. Repeating the command does not add more when the target stock is present.

Corpse loot is available immediately to the credited killer's current group.
Existing first-open, public-timeout and killer-logout release rules still apply.
Group membership never bypasses tractor range, cargo capacity, or another pilot's
active collection claim; credits and item receipts still settle exactly once.

The component market lists each station's purchasable component stock and looks
up actual vendor offers for components and manufacturing ingredients. Quotes use
the pilot's negotiation skill; buying and selling require a vendor in the current
station and commit cargo and credits together. These transactions preserve open
NPC conversations and manufacturing selections. Source lookup returns up to 128
vendors, prioritizing the current station and vendors that sell the item.

The private sandbox enables developer commands and market fast travel by default.
Pass `--no-dev` to disable both; the market then advertises fast travel as disabled.
Fast travel only accepts hosted stations and uses the normal persisted sector
handoff. Client market messages use opcodes `0x2034` through `0x2039` for source
lookup, local transactions, station travel and current-station stock metadata.

From the repository root:

```sh
cargo build --locked --manifest-path server/Cargo.toml
python3 server/tools/run_server.py --bind 127.0.0.1
python3 server/tools/create_account.py
```

For the installed Mac server, use `./serverctl status`, `./serverctl stop`, and
`./serverctl start` (`./serverctrl` is an alias). Start shows the red banner and
follows the timestamped server log. Ctrl+C closes the log view while the server
keeps running. Use `./serverctl start --no-follow` to return immediately. Start
reuses the last recorded bind address, content, state, and executable. The
default first start binds to loopback; use
`./serverctl start --bind 192.168.47.1` for the local Windows VM. The command
waits for readiness, prints the PID and log path, and never stops an unrelated
process. The server log is `server/logs/server.log`.

An error such as `Can't assign requested address` means the chosen IP is absent
from the host, often because the VM's virtual network is stopped. Restore that
network before retrying the saved VM address. For Mac-only play, explicitly use
`./serverctrl start --bind 127.0.0.1` and connect the client to `127.0.0.1`; for
remote clients choose an existing private LAN interface instead. Startup checks
the address before provisioning devices and never silently substitutes an IP,
because clients and sector handoffs need the advertised address to be reachable.

For a stopped server, seed the nine public class demos with
`server/target/debug/beyond-server --seed-demo-accounts --state-dir server/state`.
The accounts are `pte`, `ptt`, `pts`, `pjd`, `pjs`, `pje`, `ppw`, `ppp`, `pps`; each password matches its
username. Rerunning the command preserves their inventory and credits. See
`docs/DEMO_ACCOUNTS.md` for the class list and roster protocol.
The editable account and gear table is `data/demo_accounts.json`; the server
reads it at startup. Each account's credentials, inventory and character state
remain separate JSON files under `state/accounts/`.

`target/debug/beyond-server` is the executable (`.exe` on Windows). It accepts
`--bind`, `--content`, `--state-dir`, `--check`, and `--create-account`; run
`--help` for details. Default state is `server/state`, outside version control.
Add `--native-progression` to `--create-account NAME --character NAME --class je`
to create a level-zero pilot with stage-zero hull and rank-one core skills.
Existing sandbox and demo accounts keep their maxed training; this option never
converts an existing character. Native skill upgrades use packet 0x57 and save
their ranks and spent points with the character inventory.
Server stdout/stderr messages use UTC timestamps in square brackets.
Use a private LAN bind address when connecting from another machine or Net7Proxy.

`tools/restart_server.py` validates the recorded process identity, builds a staged
replacement and requests an owned graceful restart. It preserves session state.
The root `start_server.cmd` and `restart_server.cmd` wrappers support Windows.

The optional original-client proxy launcher is documented in
`../win_legacy/README.md`. Server runtime does not require it.

`RUST_GUIDE.md` explains the Rust implementation. `docs/IMPLEMENTATION.md` and
`docs/ROADMAP.md` describe current behavior and remaining work. Packet and content
provenance is documented in `../reference/NOTES.md`; restore/verify the pinned C++
files with `python3 server/tools/fetch_reference.py`.

Run `cargo test --locked --manifest-path server/Cargo.toml` for the Rust suite.
Python tests live beside the tools. Protocol smoke tools also need the
`beyond_protocol` package from `../protocol` and use disposable server state.


Create Wormhole lands the caster and eligible nearby group members at the native
weft in the destination sector (asset 1946), including same-sector casts. Arrival
coordinates are part of the durable handoff journal. A missing weft rejects the
cast before reactor energy is spent; gates and station launch retain their own
arrival rules. Focused regression checks: `cargo test --manifest-path server/Cargo.toml wormhole`.


`/d_clear` (or `/d_clear mobs`) removes living MOBs within 20 km of the pilot.
All observers receive native despawn packets and selected targets are released.
Removal persists without respawning, loot, or XP; stable actor slots preserve
other MOB identities and combat receipts. `/d_spawn` creates fresh actors.
Use `/d_clear buffs` for the former all-player buff/device-effect clearing action.

`/d_aspawn [LEVEL 1..9] [COUNT 1..32]` creates real mineable asteroids 1 km ahead (default: one level-one asteroid). It uses imported asteroid assets and ore tables, broadcasts the nodes to nearby pilots, and persists them with depletion/respawn receipts across reconnects and restarts. Example: `/d_aspawn 5 3`.

`/d_wh SECTOR_NAME_OR_ID` travels instantly to any hosted sector or station,
without class, skill, reactor or discovery requirements. Names accept the same
partial matches and abbreviations as `/d_travel` and `/d_jump` (which remain
aliases). Examples: `/d_wh Saturn`, `/d_wh BBW`, `/d_wh 17051`.
`/d_help` lists every developer command and its arguments in separate chat lines.

Public MOB kills retain their authored equipment drop probabilities and always leave an item: if all rolls miss or a table is empty, the corpse contains ordinary salvage (Drone Scrap for mechanical MOBs, Chitin' Bits otherwise). Existing corpses retain their saved contents.

Legacy startup retains the full initial reliable stream until START_ACK, including streams beyond 128 packets. Automatic startup retries skip sequence zero because the archived proxy treats it as a destructive reset; explicit client resend requests may still request zero. The server's TCP bridge closes on restart, and old Net7Proxy builds may crash on that closure; coordinate replacement with Windows users.

Net7Proxy 2.46 with the launcher's `/POPT` option requires frames above 1,400
bytes to use its native fragmentation contract: `0x2016` carries the first
1,400 bytes, and `0x201a` carries successive fragments. Sending an entire larger
frame in one UDP datagram silently loses its tail inside the proxy, even when
the datagram reaches Windows intact. A missing initial player AUX then crashes
the game while reading `RPGInfo.CombatLevel`. `reliable_transport.rs` applies
this contract to startup and subsequent inventory updates; keep custom extension
frames isolated. The installed binary supports these continuations even though
the older archived proxy source does not describe them.

Player-to-player Trade is available on living peers within 1 km of their hull
surface, or visible avatars in the same station. Native verb6 opens both trade
windows; six item offers and credit offers use the original client packets.
Both pilots must confirm, and any offer edit clears both confirmations. Offered
cargo is reserved in its original slots until the exchange commits. Disconnects,
movement out of range, cargo changes, cancellation and expiry release the offers.
No-trade items and trade goods cannot be offered; insufficient receiving cargo
space rejects the whole exchange. A synced two-account decision journal recovers
interrupted inventory-file publication without repeating the exchange.
Use `PYTHONPATH=protocol python3 server/tools/smoke_trade.py --binary PATH_TO_SERVER`
with Python3.10 or newer for a disposable two-client native protocol check.

JSON persistence buffers serialization writes before flushing, syncing and
renaming, preserving the commit boundary while reducing stalls under the game
lock. The copied 54-mob benchmark reduced 34,783 file writes to two.
