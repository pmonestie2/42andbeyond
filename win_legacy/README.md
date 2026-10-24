# Original Windows client launcher

Run these commands from the repository root on Windows with Python 3.11+:

```powershell
C:\PYTHON\python.exe -m pip install -r win_legacy/requirements.txt
C:\PYTHON\python.exe -m win_legacy.start_proxy --server 192.168.47.1 --check
.\win_legacy\launch_original_client.cmd --server 192.168.47.1 --no-build
```

The example address is the Mac host on this VMware network. For another setup,
replace it with the server's private LAN IP and bind the server to that address.

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
`127.0.0.1` directly. No multi-instance executable or process-memory patching is
included. Tests mock configuration and process calls; live launch needs Windows.
