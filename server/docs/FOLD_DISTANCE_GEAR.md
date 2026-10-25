# Equipped Fold Space distance

Thirty-eight public items have equipped `STAT_FOLD_SPACE_DISTANCE` operation 1. Their tooltips explicitly increase Fold Space travel distance. Lepus Prime IFSS 9 (item 2492) grants **983 metres**; the weakest catalog item grants 217. The strongest intact equipped modifier wins, as with the other public item stat effects. Broken gear and untrained Fold Space grant no distance bonus. The server caps the modifier at 1,000 metres when loading a cast checkpoint.

The modifier extends rank-one self travel, rank-three enemy push, rank-five friend travel, and Defender rank-six Directional/rank-seven Area Teleport. Target acquisition range, trained rank gates, group radius, reactor cost and cooldown are unchanged. The equipped value is frozen in the cast. High-tier settlement checkpoints the value with its pending request and then freezes each recipient's final bounded destination before moving anyone; a restarted server cannot recalculate travel from changed gear.

Disposable tests cover catalog gear and breakage, low-tier self/enemy casts after gear removal, high-tier pilot/MOB travel, and a partial group delivery that restarts without its equipped device before retrying the saved plan. No client packet changes or live server actions are required.
