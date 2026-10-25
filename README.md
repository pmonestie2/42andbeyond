# 42 and Beyond - A re-implementation of Earth And Beyond.

Standalone Unity client and Rust game server - Still work in progress but the server supports Earth And Beyond "normal" clients - Eg it's a back port of the "regular" ENB server (Net7) 

Why: I wanted to explore making a new client - For that you need a server to play with - the Net7 server being close source (I really wonder why) I remade a server.

A quick demo here: https://www.youtube.com/watch?v=TjWSGhOvHgU

| Directory | Purpose |
| --- | --- |
| `client/` | Unity game, C# wire protocol, launcher and tests |
| `server/` | Rust server, game content, importers and tests |
| `protocol/` | Python protocol utilities used by server integration tests |
| `win_legacy/` | Optional original-client/Net7Proxy launcher and reversible configuration |
| `reference/cpp/` | Pinned archived C++ reference files for reimplementation |


## Run locally

The simplest is probably to sping up codex or claude and ask it to run the server.

Install Rust and Unity Editor 6000.3.13f1. From this directory:

```sh
python3 server/tools/run_server.py --bind 127.0.0.1
```

Open `client/` in Unity Hub and press Play. Connect to `127.0.0.1` with the local
server account. There is 1 account per race: ppw/ppw pte/pte you get the idea.

I'm running this on Mac with legacy clients on winVM - Should be no problem to run all on windows.


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
