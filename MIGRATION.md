# Source migration

Extracted from `acmacro` commit `3095d9c5b8cb2bcbc1c9c850519c8b936331b077`.
That snapshot includes server commit `207e2a0` and the last client visual pass.
The original workspace remains intact as a migration backup.

- `enb-unity` became `client`; Unity assemblies/namespaces and asset paths now use
  `FortyTwoAndBeyond`. Unity asset GUIDs were retained.
- `enb-server` became `server`; Cargo package/executable is `beyond-server` and the
  Rust library identifier is `beyond_server`.
- `enb-protocol` became `protocol`; Python distribution is `beyond-protocol` and
  its import package is `beyond_protocol`.
- Optional Windows original-client launch, INI, DNS, certificate and owned-process
  utilities live in `tools/windows`. Multi-instance executable/memory patching
  and relays were omitted. Native packet compatibility remains in the server.
- Build, import, account, restart and disposable test tools were retained.
- Macro controllers, scripted gameplay, packet-capture automation, the old web
  client, agent coordination server, caches, build outputs, private account state,
  raw captures and Unity recovery files were not copied.
- The archived C++ reference set is restored under `reference/cpp`, verified by
  Git blob hashes against `reference/manifest.json`.

This is an independent local Git repository. A remote has not been assigned.

## Migration verification

- Unity macOS build succeeded from `client/` with renamed assets and assemblies.
- Rust: 269 tests passed with package and binary `beyond-server`.
- C# protocol checks and complete disposable client/server smoke passed.
- Python server tools: 37 tests, with 3 Windows-only cases skipped on macOS.
- Python client launcher: 6 tests passed.
- Windows utility configuration tests: 13 passed using mocks/temporary files.
- Python protocol smoke passed login, inventory, impulse, warp and saved position.
  Its checkpoint assertion now waits for asynchronous UDP logout persistence.
- All 2,313 archived reference files matched their pinned Git blob hashes.

Live Windows original-client launch has not been exercised from the new tree.
The built macOS app is in `client/Builds`; the installed app and VM server were
not replaced or restarted during migration.
