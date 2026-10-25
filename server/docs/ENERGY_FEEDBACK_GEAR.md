# Equipped Energy Feedback

Five public item templates contain the catalog key
`STAT_ENEREGY_FEEDBACK` (including its spelling). Their tooltips say that
incoming Energy damage returns 4.2–14.3% to the source. The Greater variant
explicitly says “up to 27.4” damage per return. The strongest intact equipped
effect is used, in line with the server's other nonstacking equipped modifiers.

On an authoritative NPC Energy hit, the server calculates the returned amount
from the same post-deflect damage value used by native damage text, bounded to
27.4 per hit. This ceiling is applied conservatively to both catalog variants
because the archived C++ has no executable Energy Feedback handler and the
ordinary variant does not specify its own cap. Other damage types receive no
Energy Feedback. Counterstrike, Repulsor Field, and Environment Shield retain
their separate behavior; their combined reflection shares the durable NPC-hit
receipt and its total validation bound.

The amount is frozen with player HP before the world acknowledges the NPC
shot. Retry or restart replays the same return once even if feedback gear is
damaged or removed. Reflection remains Energy damage against that NPC and
does not create a reciprocal reflection loop.
