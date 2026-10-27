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
