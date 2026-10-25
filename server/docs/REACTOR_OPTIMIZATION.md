# Reactor Optimization implementation notes

Skill 43 belongs to the Jenquai Seeker. The public ability IDs are 82, 83,
84, 85, and 86 at ranks 1, 3, 5, 6, and 7. The archived Net7 tree does not
contain an executable handler for this newer class.

The [Net-7 skill page](https://net7wiki.bmsite.net/index.php?title=Reactor_Optimization)
documents a ten-minute duration and these rank effects:

| Ability | Trained rank | Reactor capacity | Reactor recharge | Recipient |
| --- | ---: | ---: | ---: | --- |
| Reactor Boost (82) | 1–2 | +5%, then +10% | — | Self |
| Reactor Surge (83) | 3–4 | +10%, then +15% | +10% | Self |
| Reactor Extension (84) | 5 | +20% | — | Self or selected friend within 3500 m |
| Reactor Augmentation (85) | 6 | +20% | +10% | Self or selected friend within 3750 m |
| Reactor Optimization (86) | 7 | +25% | +20% | Self or selected friend within 4000 m |

The page explicitly says the skill must be applied to each group member
individually. There is no group-wide cast. Reactor capacity and recharge should
stack with equipment and device effects, so the recipient's final derived
ship stats receive the additional multiplier. The current energy amount is
preserved when capacity increases and clamped if a buff expires below it.

Energy cost (75/100/125/150/200 by tier), two-second charge and 30-second
cooldown are local server tuning because neither the archived C++ nor the
public skill page supplies these numbers. Native ShipIndex.Buffs slot 11 is
reserved for the recipient's ten-minute status. The `Reactor_Optimization`
BuffType follows the server's existing underscore naming convention; exact
legacy icon artwork for that token has not been verified. No ObjectEffect
descriptor has been verified from the installed client, so the server does
not guess one. Cast progress, authoritative ship AUX stats, status duration,
expiry, and replacement replay remain visible without an ObjectEffect.
