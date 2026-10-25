# Terran Enforcer Biorepression

The pinned `PlayerSkills.h` and `PlayerClass.cpp` map skill 3 to ability IDs
22/24/23/25/21 at ranks 1/3/5/6/7. The `ABiorepression` executable handler
is absent from the pinned archive. The [Net-7 Biorepression page](https://net7wiki.bmsite.net/index.php?title=Biorepression)
describes organic-only targets, trained range 1,000–2,500, slower weapon fire,
less frequent skill use at ranks 3 and 6, area effects at ranks 5 and 6, and
one-weapon disable attempts at rank 7. Pinned `MOBClass.h` defines organic red
and green as MOB types 2 and 3. No native Biorepression visual descriptor is
verified, so the server sends no guessed ObjectEffect.

The server stores successful effects on the shared MOB world, pinned to its
life generation. A cast revalidates the selected living, visible, hostile
organic MOB when it completes. Area ranks affect eligible organics within
1,500 of that selected MOB. World persistence precedes cast success, and a
failed save retries without a duplicate effect. Active ranks 1–6 multiply the
MOB's authoritative reload time. Biocessation disables its single simulated
weapon on success. Rank 3/6 effects reduce native warning-call attempts, the
only nonweapon MOB action currently simulated. **Other MOB active skills are
not modeled by the existing AI**, so those cannot yet be suppressed. Effects
survive a process restart, expire by deadline, and clear on death/respawn.

Numeric values are **local tuning** because the executable handler and public
skill page do not supply them: reactor costs 25/50/80/100/130; charge 1.5
seconds; cooldown 15 seconds; lifetime 8 + 2 × trained rank seconds; reload
factor 1.2 + 0.1 × trained rank (up to 1.9); warning probability factor
max(0.4, 0.75 − 0.05 × trained rank); area radius 1,500. Biocessation has
35% base success against the one-weapon MOB simulation, or 65% for a future
multi-weapon MOB, adjusted −2% per target level above caster Combat level and
bounded 5–95%. Its deterministic roll survives a retry. It does not fall back
to a slowing effect on resistance.

The existing native client chooses a MOB with `0x17` and casts with `0x58`.
The server provides selection acknowledgement `0x19`, charge progress `0x79`,
cooldown and completion text. The client requires no new packet, but cannot
render a Biorepression target visual until an original descriptor is found.
