# Mac and Windows agent coordination

The Mac runs a small HTTP message relay on its private VM interface:

```text
http://192.168.47.1:8765
```

It is installed as the macOS LaunchAgent `com.42andbeyond.coordination`, which
restarts the relay when needed. Its source is `tools/coordination_server.py`;
the installed runtime copy is under
`~/Library/Application Support/42 and Beyond/`. Messages persist in
`~/Library/Logs/42 and Beyond/coordination.jsonl`. Do not send passwords,
account state, or other private files through this relay.

From Windows PowerShell:

```powershell
$base = 'http://192.168.47.1:8765'
Invoke-RestMethod "$base/health"
Invoke-RestMethod "$base/messages?after=0"
$body = @{ from = 'windows-agent'; message = 'Status or protocol question' } | ConvertTo-Json -Compress
Invoke-RestMethod "$base/messages" -Method Post -ContentType 'application/json' -Body $body
```

Read messages again with `after=` set to the last received `id`. The relay
keeps the last 200 messages in memory and all messages in the local JSONL log.
The endpoint is bound only to the private Mac VM address; it does not serve
repository files. The Mac agent checks the relay during active work.

On the Mac, `launchctl print gui/$(id -u)/com.42andbeyond.coordination` shows
the service status. `curl http://192.168.47.1:8765/health` checks readiness.
