# Nine class demo pilots

With the server stopped, run the built binary against the intended state
directory:

```sh
server/target/debug/beyond-server --seed-demo-accounts --state-dir server/state
```

The command creates only missing demo accounts. Reruns preserve existing
inventory, credits and character state. A conflicting preexisting class-code
account stops provisioning without overwriting it. The roster is published
only after all nine accounts are ready; ordinary local accounts are never
advertised.

| Account and password | Class |
| --- | --- |
| `pte` | Terran Enforcer |
| `ptt` | Terran Trader |
| `pts` | Terran Scout |
| `pjd` | Jenquai Defender |
| `pjs` | Jenquai Seeker |
| `pje` | Jenquai Explorer |
| `ppw` | Progen Warrior |
| `ppp` | Progen Privateer |
| `pps` | Progen Sentinel |

Each pilot has the current sandbox maximum of level 50 in combat, trade and
exploration, hull upgrade 6, maximum class skill ranks, 100 million credits,
all class weapon and device mounts occupied, and high-end reactor, shield and
engine gear. Terran and Progen mounts use level-nine missile/projectile
launchers with compatible full ammo stacks plus two reserve cargo stacks.
Jenquai mounts use high-end beams, which consume no ammunition. Gear is
durable owned inventory; the demo provisioner does not refill spent resources.
The separate full XP progression system remains under development.

The server advertises only the published class codes over an unauthenticated
read-only request: UDP port 3806, opcode `0x1002`, empty body, player and
sequence zero. The `0x1003` reply body is byte `1` (version), one count byte,
then that many two-byte lowercase class codes in the order above. No account
record, password hash, inventory or private character data is sent.
