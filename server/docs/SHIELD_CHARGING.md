# Shield Charging evidence and implemented server rules

The [Net-7 Shield Charging page](https://net7wiki.bmsite.net/index.php?title=Shield_Charging) states a ten-minute duration and one recipient per cast. Its five native ability IDs are recorded in archived `reference/cpp/trunk/Net7/PlayerSkills.h`: 121, 129, 122, 130, and 74. Archived `PlayerClass.cpp` lists `AShieldCharge` within a commented *not yet implemented* block, so the pinned executable tree provides no cast formula or effect ID.

| Ability | Trained rank | Recipient | Capacity | Recharge | Resistance |
| --- | ---: | --- | ---: | ---: | ---: |
| Supercharge Shields (121) | 1 | Self | +5%; +10% at rank 2 | 0 | 0 |
| Ultracharge Shields (129) | 3 | Self | +10%; +15% at rank 4 | +10% | 0 |
| Supercharge Target (122) | 5 | Self or friendly within 3,500 | +20% | 0 | 0 |
| Ultracharge Target (130) | 6 | Self or friendly within 3,750 | +20% | +10% | 0 |
| Megacharge Shields (74) | 7 | Self or friendly within 4,000 | +25% | +20% | +10% all damage resistances |

The implementation reserves native `ShipIndex.Buffs` slot 13 and validates a recipient status by revision, session epoch, trained rank, and deadline. It uses a two-second cast, 30-second cooldown, and tier reactor costs 75/100/125/150/200 as **local server tuning**, matching the related Reactor Optimization support ability. The source does not establish a visual descriptor; the server publishes the native timed buff status without guessing an `ObjectEffect` ID.

Caster energy and recipient status settle in one world ledger revision before success is published. A short replacement replays one timed status. Expiry removes the three bonuses, clamps shield points to the restored capacity, and clears only slot 13. These are server durability semantics, not claims of original balance.
