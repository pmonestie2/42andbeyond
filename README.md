# 42 and Beyond

Standalone Unity client and Rust game server.

| Directory | Purpose |
| --- | --- |
| `client/` | Unity game, C# wire protocol, launcher and tests |
| `server/` | Rust server, game content, importers and tests |
| `protocol/` | Python protocol utilities used by server integration tests |
| `win_legacy/` | Optional original-client/Net7Proxy launcher and reversible configuration |
| `reference/cpp/` | Pinned archived C++ reference files for reimplementation |

Mac and Windows agents can exchange short messages through the private HTTP
relay documented in [tools/COORDINATION.md](tools/COORDINATION.md).

## Run locally

Install Rust and Unity Editor 6000.3.13f1. From this directory:

```sh
python3 server/tools/run_server.py --bind 127.0.0.1
```

Open `client/` in Unity Hub and press Play. Connect to `127.0.0.1` with the local
server account. The disposable demo account is `local` / `local`.
For the existing Windows VM, use its private LAN IP in the client login form.

Create an account with `python3 server/tools/create_account.py`; its password is
read interactively. Saved server accounts and characters belong in `server/state/`.
Existing VM state is not copied or changed by this source migration.

On macOS, `client/Play Local.command` builds and launches an owned local setup.
`client/Play 42 and Beyond.command` launches the installed app against the VM.

## Verify

```sh
cargo test --locked --manifest-path server/Cargo.toml
python3.13 -m unittest discover -s server/tools -p 'test_*.py'
python3.13 -m unittest discover -s client/Tests -p 'test_*.py'
python3.13 -m unittest discover -s win_legacy -t . -p 'test_*.py'
dotnet run --project client/Tests/ProtocolChecks.csproj
python3 client/Tests/run_local_smoke.py
```

Python protocol smoke tools require Python 3.11+ and
`python -m pip install -e ./protocol`. Windows launcher dependencies are in
`win_legacy/requirements.txt`. See that directory's README for launching the
original client with its proxy configuration.

The migration records source provenance and exclusions in `MIGRATION.md`.
