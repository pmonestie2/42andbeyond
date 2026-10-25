# Shield Inversion implementation notes

The [Net-7 Shield Inversion skill page](https://net7wiki.bmsite.net/index.php?title=Shield_Inversion)
lists shield drain per second per trained skill level, energy-damage
conversion, rank ranges and the 2,500-unit Shield Nova area. The archived
`PlayerClass.cpp` maps native ability IDs 111/113/105/107/110 and explicitly
names effect 98 for Shield Nova, but its executable ability handler is absent.

The source-backed tiers are:

| Rank | Ability | Shield drain per second per level | Energy damage conversion |
| --- | --- | ---: | ---: |
| 1 | Shield Ram | 3 | 90% |
| 3 | Shield Spike | 9 | 100% |
| 5 | Shield Burn | 15 | 110% |
| 6 | Shield Flare | 18 | 120% |
| 7 | Shield Nova | 21 | 130% to each eligible MOB |

The 15-second base duration, one-second pulse cadence, two-second cast and
20-second cooldown are local tuning. Equipped
`STAT_SHIELD_INVERSION_DURATION` bonuses add seconds; the equipment stat engine
chooses the strongest item modifier of each operation, following its existing
rules. Only Shield Nova has a verified native visual ID.
An active cast is bound to the selected MOB death generation and caster session
epoch; a sector or target change must end it. Item text sometimes calls the
duration bonus a damage increase, but the archived item modifier is explicitly
named `STAT_SHIELD_INVERSION_DURATION`; no unsourced damage multiplier is added.
Each one-second pulse commits MOB damage and one receipt in the sector snapshot
before the caster's shield checkpoint. A restart replays only the latest
unsettled receipt; a new pulse replaces that caster's previous receipt.
