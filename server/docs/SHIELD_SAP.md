# Shield Sap rules and durability

The archived `reference/cpp/trunk/Net7/Abilities/AShieldSap.cpp` supplies the
five PW ability IDs, three-second charge, zero cooldown, reactor costs,
target ranges, per-rank drain fraction, per-cast drain cap, and restoration
fraction. The [Net-7 skill page](https://net7wiki.bmsite.net/index.php?title=Shield_Sap)
also describes the area and group tiers. The C++ area drain function returns
zero, and its group-heal branch is a TODO. This server completes those two
branches using the documented tier radii and equal group distribution.

| ID | Rank | Energy | Range | Area radius | Restore recipient |
| --- | ---: | ---: | ---: | ---: | --- |
| 112 | 1 | 35 | 1000 | — | None |
| 114 | 3 | 75 | 1500 | — | Self |
| 54 | 5 | 150 | 2000 | — | Nearby group |
| 98 | 6 | 300 | 2250 | 1500 | Self |
| 55 | 7 | 600 | 2500 | 3000 | Nearby group |

Each living MOB in the target-centered area loses `(12% + 3% × trained rank)`
of its current shield, capped separately at half the caster's equipped shield
capacity. Rank 3 and above restores `(75% + 12.5% × (trained rank − 3))` of
the total drained amount. For group tiers, this total is divided among the
caster and eligible group members, with each share capped at half the caster's
shield capacity and the recipient's missing shield points. The archive gives
no group-recipient distance, so this server conservatively requires the same
sector and the tier's target range from the caster. Only current, alive group
members receive the award; ungrouped nearby pilots do not.

The sector combat snapshot commits drained MOB shields and a cast receipt
together. A retry returns that receipt without damaging the MOB again. The
world energy ledger then commits the caster's post-charge energy and all
frozen recipient shield values in one atomic file. Session checkpoints can
lag either file; restart replays the receipt and ledger revision without a
second drain or heal. A corrupt state with a settled ledger cast but no
combat receipt is rejected.

The archived handler identifies ObjectEffect 733 for charge, followed by
ObjectToObjectEffect 139 (beam) and 146 (steal orb) to each drained MOB. These
are short-lived effects; Shield Sap has no persistent buff icon. A native
client screenshot of all three effects remains to be verified.
