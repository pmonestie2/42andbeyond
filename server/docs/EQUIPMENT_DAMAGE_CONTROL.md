# Equipped equipment damage control

Public `server/data/items.json` contains 245 equipped modifiers named
`STAT_EQUIPMENT_DAMAGE_CONTROL_{SHIELD,REACTOR,ENGINE,WEAPONS,DEVICES}`.
Their tooltips say that damage control improves the named category's ability
to sustain damage; damage magnification reduces it. Positive operation 2 and
negative operation 4 are percentages. Examples are Refuge Quinque (shield
+58%), Chariot Alpha (engine +38%), and Blue Dragon Laser M4 (reactor -20%).

The archived combat path does not supply an executable item-structure wear
formula. This server already chooses one mounted item after a hull-damaging
NPC hit and journals its structure loss. The new rule preserves that locally
tuned base wear, then multiplies it by `100 / clamp(100 + positive - negative,
25, 400)` for the selected item's category. The strongest intact equipped
modifier per operation applies, matching the existing equipment stat engine.
Shield, reactor, engine, weapons, and devices correspond to equipped slots
0, 1, 2, 3–8, and 9–19. Final loss remains bounded by the receipt's existing
0.03 maximum. These modifiers change item structure wear only; hull damage
control remains a separate health calculation.

The adjusted amount and selected slot are written into the durable NPC hit
receipt before inventory settlement. Retry, account replacement, gear changes,
or restart replay that frozen amount exactly once through
`Inventory.applied_equipment_wear`. Tests cover category selection, both
positive and negative effects, broken gear, and a torn inventory write.
