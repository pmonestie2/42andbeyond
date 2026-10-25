# Equipped hull integrity

The public `STAT_HULL_DAMAGE_CONTROL` modifiers change the effectiveness of
the ship's hull at sustaining damage. The catalog has a trailing space on
some copies of the stat name; equipped modifier parsing trims it. The
strongest intact increase and strongest intact decrease are applied as
percentage changes to effective hull integrity. Post-shield hull damage is
scaled by the reciprocal of that integrity, bounded between 25% and 400% of
ordinary damage. Existing trained Damage Control then applies separately.

For example, +23% integrity changes 100 post-shield damage to about 81.3
before the trained passive. Shield routing and damage text retain their
existing rules. MOB hit receipts continue to make a repeated hit
idempotent. This reciprocal interpretation and cap are local combat tuning;
the public item tooltip states the percentage but no damage equation.
