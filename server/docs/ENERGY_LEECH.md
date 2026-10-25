# Energy Leech implementation notes

The archived `PlayerClass.cpp` maps Energy Drain 38, Energy Leech 39,
Render Energy 92, Energy Leeching Sphere 40, and Render Energy Sphere 93 to
the missing `AEnergyLeech` handler. The [Net-7 Energy Leech page](https://net7wiki.bmsite.net/index.php?title=Energy_Leech)
provides Defender-only ranks 1/3/5/6/7, trained range 1,000–2,500, area
radii 1,500 and 3,000, and which tiers return power to the caster or group.
Its overview centers the area on the selected MOB; its rank table says
"surrounding the player." The server anchors on the selected target,
matching other targeted area abilities. This source ambiguity remains.

The sources do not supply a numeric drain, conversion percentage, energy
cost, charge, cooldown, or visual ID. The server uses explicit
**local tuning**: 15% of MOB reactor capacity at trained rank 1, +5 percentage
points per rank through 45% at rank 7, capped by available MOB energy; 50% of
drained power becomes a transferable pool at tiers 3 and higher. The two
group tiers share that conserved pool across eligible nearby pilots. Casts
charge for 2 seconds with a 10 second cooldown. Reactor costs are 35, 75,
150, 300, and 600 for the five tiers. The native effect visual remains
unverified, so this implementation does not emit a guessed descriptor.

The MOB reactor balance persists with its sector combat snapshot. A weapon
shot spends reactor power, so a drain can delay actual fire. The completed
cast checkpoints its target generation before the sector persists all MOB
drains with one receipt. A separate group energy ledger settles the charged
caster and recipients once. Retry after a torn ledger write reads the sector
receipt without redraining MOBs; failed sector writes roll the drains back.

`mob_energy.rs` implements the associated local reactor model: capacity scales
with existing MOB DPS, a shot spends 80% of capacity, and recharge over that
weapon's reload period restores the same amount. At an undrained balance this
preserves the current firing cadence; an Energy Leech cast can delay a shot
until power recovers. Legacy MOB snapshots without a reactor field resume at
full capacity. All validation uses disposable accounts and worlds; the live
server and client are untouched.
