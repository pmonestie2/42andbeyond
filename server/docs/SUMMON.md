# Summon implementation notes

The five client ability IDs for Jenquai Defender skill 56 are in the archived
skill database and `server/data/class_skills.json`: Summon Enemy (117), Summon
Friend (119), Summon Group (120), Summon Enemy Group (118), Return Friend (97).

The [Net-7 Summon skill page](https://net7wiki.bmsite.net/index.php?title=Summon)
defines the target types, a 5,000-unit hostile range at rank one growing by
250 units per rank, a 250,000-unit group range, and Return Friend's registered
port destination. The archived C++ server has no executable Summon handler.
The 150-unit arrival offset, three-second cast, five-second cooldown, and
35/75/150/300/600 energy costs are local tuning, not claims about the original
game. Enemy formation means mobs in the selected target's nonzero spawn group.

At delivery time the server checks membership, sector and range again. Enemy
casts also recheck the MOB death generation and persist relocation before
broadcast. A recipient's movement is checkpointed before broadcast. Return
Friend uses the existing journaled station transfer. The source is not a
live-server deployment.
