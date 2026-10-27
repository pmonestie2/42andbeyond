# 42 and Beyond coding standard

- Use ordinary typed classes and concrete dependencies. Prefer explicit named
  parameters; introduce interfaces only for interchangeable implementations.
- Add short docstrings to new functions. Document meaningful return values in an
  indented Returns section, and relevant exceptions in a Raises section.
- Keep network state off Unity's main thread and publish immutable snapshots.
  Scene objects and input belong on the main thread. Join workers during cleanup.
- Treat the server as authoritative for movement, inventory and combat.
- Preserve process ownership and account state. Tests use disposable servers and
  must never drive or restart the live VM or user client.
- For user-requested client deployments, the user authorizes stopping all running
  instances of the installed 42 and Beyond app before replacing it. Match the
  exact installed executable path and preserve other applications and VM clients.
- Keep optional Windows launcher configuration reversible and preserve concurrent
  file edits. Do not add executable patching, memory injection or macro automation.
- Check callers and dependencies when renaming components. Verify Rust, C# and
  affected Python tools from this repository without requiring the old workspace.
- Preserve the archived C++ reference files byte-for-byte; use the pinned manifest
  to verify their provenance. Implement new behavior in client/ or server/.
- Report checks accurately, including platform limitations. Do not claim a source
  change is installed or running unless that deployment was verified.
