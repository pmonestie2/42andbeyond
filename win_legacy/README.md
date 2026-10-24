# Original Windows client launcher

Run these commands from the repository root on Windows with Python 3.11+:

```powershell
C:\PYTHON\python.exe -m pip install -r win_legacy/requirements.txt
C:\PYTHON\python.exe -m win_legacy.start_proxy --server 192.168.47.1 --check
.\win_legacy\launch_original_client.cmd --server 192.168.47.1 --no-build --scale normal
```

The example address is the Mac host on this VMware network. For another setup,
replace it with the server's private LAN IP and bind the server to that address.

Use `--scale normal` for native pixels or `--scale double` for Windows DPI
scaling (2x at a 200% Windows display setting). Omit `--scale` to keep the
existing settings. Only the game executable receives this override; its previous
compatibility settings are restored on exit. Concurrent changes are preserved,
with recovery backups retained in `server/logs/dpi-before-*.json`.

Run the launcher from an elevated console so it can temporarily update the hosts
file. Supply `--client` and `--proxy` if the installed executable paths differ
from the defaults shown by `--help`. OpenSSL is required; Git for Windows includes
it. `--check` prints the plan without starting a game or changing configuration.

The launcher temporarily configures the original client's authentication,
registration and network INIs, maps `local.net-7.org` to its local Net7Proxy, and
installs a reusable development certificate for that proxy. The certificate is
trusted for the current Windows user. Configuration and certificate files are
restored when the owned game/proxy process tree exits; backups are retained when
another program changes a file concurrently. Keep the console open while playing.

Use a private LAN server address for Net7Proxy. The Unity client can connect to
`127.0.0.1` directly. Tests use disposable files, socket listeners and mocked
process/configuration calls; a full game launch needs Windows.

To run multiple clients, open a separate elevated console for each instance:

```powershell
.\win_legacy\launch_original_client.cmd --server 192.168.47.1 --no-build --scale normal --instance pilot1
.\win_legacy\launch_original_client.cmd --server 192.168.47.1 --no-build --scale double --instance pilot2
```

Use a different server account in each client; logging into the same account
replaces its existing session. Finish one client's startup dialog before opening
the next. Keep each launcher console open. The original installed client may
stay open while named instances run.

Each name reserves one of eight private loopback addresses (`127.0.0.2` through
`127.0.0.9`). All instances use the installed game assets directly through
directory/file links. The small launch view under `server/state/clients/<name>`
keeps only the singleton-specific executable, writable configuration, runtime
output and proxy state private. No game asset tree is copied or hard-linked.
Run elevated so Windows can create the links and update the hosts file.

Closing an instance stops only its owned process tree and relays and removes only
its unchanged hosts entry. Existing profiles convert on their next launch, after
their game and proxy have exited. Conversion verifies old asset copies before
removing their profile links; installed files, private settings and logs are
preserved. Differing or extra old asset files are saved under the profile's
`migration-backups/<id>/` directory before switching to the installed assets.
Concurrent changes or unsafe links still stop conversion. Running clients are
never converted.

Named instances require the known client and Net7Proxy 2.46 builds. Their hashes
are validated before setup. Only the private client's singleton-name string is
patched; the installed executables remain unchanged. The suspended owned proxy
gets a verified loopback data override in memory before it starts. A separate
wildcard development certificate covers instance hostnames. Unsupported builds
or unexpected private executable edits are rejected.
