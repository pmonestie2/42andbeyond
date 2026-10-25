# Enrage implementation notes

The [Net-7 Enrage skill page](https://net7wiki.bmsite.net/index.php?title=Enrage)
describes target aggression, the Enrage weapon-accuracy penalty, the group
tiers, and the 5,000–6,500-unit range progression. The archived
`PlayerClass.cpp` maps ability IDs 6/27/41/7/42 and effect 212, but its
executable Enrage handler is absent.

The 20-second duration, 1.5-second cast, 10-second cooldown,
35/75/150/300/600 energy costs, success chance by rank and level, 0.65
beam/projectile accuracy multiplier, and nonzero spawn-group geometry within
1,500 units are local tuning. The per-MOB success roll is stable across cast
retries. Casting does not change a pilot's permanent faction reaction.
An active effect is tied to the authenticated caster and a MOB death generation,
so respawn and reused object IDs cannot inherit it.
