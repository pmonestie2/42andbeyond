# Authoritative launcher ammunition options

The native `0x25` item template is unchanged. Modern clients opt in to launcher
options using `0x203d`; legacy clients receive no extra launcher-options packets.
All fields below are little-endian `u32`. Mounts use native equipment indexes
`3..8`, not weapon-list ordinals. Requests do not depend on the selected target.

Client request `0x203d` is exactly 12 bytes:

| Field | Meaning |
| --- | --- |
| owner | Authenticated full player ID |
| mount | Equipped ML/PL mount, or `0xffffffff` for query/subscribe |
| enabled | `0`/`1` for a preference change; `0` for query |

Server snapshot `0x203c` is a complete replacement:

- Owner ID, launcher count.
- For each equipped missile/projectile launcher: mount, Auto Reload enabled,
  remembered ammunition template (`0` if unknown), compatible cargo-slot count,
  followed by that many cargo-slot IDs.

Compatibility comes from the server's actual launcher/ammunition catalog rules.
The client can display quantities, names and damage variants using its existing
cargo and item-template snapshots. Manual loading uses native inventory move
`0x27`: cargo container `1` to equipment mount container `2`; this loads the
magazine while preserving the launcher. Container `8` remains magazine storage
for unloading and magazine transfers.

A query returns a full snapshot, and later snapshots are sent only when content
changes, following inventory updates. Subscription survives transport checkpoints
and sector handoffs and is reset for a new authenticated character selection.
Fresh queries after reconnect are supported. Preferences themselves are durable
inventory state (`auto_reload`, `preferred_ammunition`) and are never copied back
from an older transport checkpoint.

Automatic reload is enabled by default for pre-existing inventories. Explicit
loading remembers the exact template, including its damage variant. Disabled
reload leaves an exhausted magazine empty. Enabled reload takes an owned cargo
stack of that exact remembered template; it does not switch to another compatible
variant. Enabling an empty mount or firing it again after cargo arrives attempts
a reload. No available matching stack means no inventory write. Explicit unloading
retains the remembered selection and does not immediately reload until enabled or
fired again. Quantity, quality, structure and ownership of transferred stacks are
preserved; rejected inventory writes cannot change the authoritative preference
or ammunition.
