# Equipped Build skill modifiers

The public catalog contains equipped `SKILL_BUILD_DEVICES`,
`SKILL_BUILD_ENGINES`, `SKILL_BUILD_REACTORS`, `SKILL_BUILD_SHIELDS`, and
`SKILL_BUILD_WEAPONS` modifiers. For analysis and dismantling, the strongest
intact matching modifier adds ten percentage points per effective skill level
to the prototype success chance, rounded to a whole percentage point and
capped at 100%. The public modifier values are fractional, generally below
half a level. Broken equipment gives no bonus.

Trained rank continues to determine item level eligibility and manufacturing
access. Manufacturing itself remains deterministic at normal quality, so this
bonus applies to the two production outcomes that already have a success roll.
The conversion of effective skill levels to chance is local tuning; the
archived analyzer has no executable build algorithm. The roll and recovered
components are chosen before the production job is saved, so a restart does
not reroll them.
