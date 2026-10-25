# Equipped Summon skill levels

Public items Black Peg Leg VIII (6173) and IX (6135) carry equipped `SKILL_SUMMON` operation 1, +3.0 and +3.5 levels. The item tooltip states that this improves Summon skill effectiveness. The [Net-7 Summon rank table](https://net7wiki.bmsite.net/index.php?title=Summon) increases selected-target range by 250 metres per trained level. The archived Summon handler is absent, so applying the same 250-metre increment per equipped level is an explicit local inference.

The server uses the strongest intact equipped modifier, capped at +5 levels. The trained rank alone unlocks abilities. Equipped bonus extends selected friend/enemy range at activation and completion. Group Summon's existing sector-wide rule is unchanged. The bonus is frozen in the authenticated cast and checkpointed with the energy charge, so changing gear or replacing the server during charge cannot change its range. Legacy in-flight cast checkpoints default to zero bonus.

Disposable tests exercise the actual public items, broken/untrained cases, native two-pilot friend cast at 7 km (beyond rank-seven base 6.5 km), gear removal after cast, server replacement, and delivery after the group is re-formed. Group membership itself is currently volatile across replacement; this gear change does not persist group formation. No new packet or client control is required.
