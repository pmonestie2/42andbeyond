# Python protocol utilities

The `beyond_protocol` package provides packet framing, login, direct TCP/UDP
sessions and world decoding for disposable server integration tests. It has no
macro-controller, web-app or Windows dependency.

Install using Python 3.11 or later from the repository root:

```sh
python -m pip install -e ./protocol
```

For example, run `python server/tools/smoke_test.py --no-build --server 127.0.0.1`
after building `server/Cargo.toml`. The smoke tool owns temporary accounts and
server state and refuses to reuse an already-responding server.
