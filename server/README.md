# 42 and Beyond server

A portable Rust server with authoritative space movement, groups and formations,
combat, inventory, loot, mining, station services, persistence and native protocol
compatibility. The embedded public content catalogs are in `data/`.

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
