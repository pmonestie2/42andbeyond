# Terran Tradesman Befriend

The pinned `reference/cpp/trunk/Net7/PlayerClass.cpp` maps skill 2 to ability
IDs 20/63/43/115/15 at ranks 1/3/5/6/7 and names native visual descriptor
221. The executable handler is absent from the archive. The
[Net-7 Befriend page](https://net7wiki.bmsite.net/index.php?title=Befriend)
describes the trained 5,000–6,500 range, rank-three calming, rank-five
psionic vulnerability, rank-six stronger calming, and rank-seven 3,000 area
around the selected MOB. The original numeric combat rules are unavailable.

The server implements those tier effects on the shared, durable MOB world.
Success makes the selected MOB treat the caster as friendly and clears its
current hate and warning state for that caster. Rank three reduces acquisition
range against other pilots to 50%; rank six reduces it to 30%. Rank five adds
20% typed Psionic damage against that MOB; rank six raises this to 40%. An
attack by the befriended caster breaks their friendship with the damaged MOB.
The effect expires, or clears on MOB death/respawn. Area Soothe applies an
independent success check to each current-generation MOB within the selected
target's 3,000 radius. A saved cast revalidates target generation, range and
visibility at completion. World save precedes cast acknowledgement, and a
failed save is retried without duplicating a successful reaction.

The following are **local tuning**, because neither pinned C++ nor public
descriptions gives numbers: rank costs 20/35/55/70/90 reactor energy, 1.5
second charge, 15 second cooldown, 14 + 3 × trained rank seconds duration,
and 50% + 5% × trained rank − 2.5% × (MOB level − caster Trade level)
success chance, bounded to 5–95%. A deterministic cast roll makes settlement
stable across retry. Only a successful effect publishes descriptor 221 via
native ObjectEffect `0x09`; expiry and visibility loss remove it via `0x0f`,
and a resumed session replays it. This is a MOB reaction and visual effect,
not a player buff icon.

The existing client sends selected MOB `0x17`, then ability `0x58`; the server
uses `0x19` selection acknowledgement, `0x79` charge progress, and native
cooldown/status messages. The client need not implement a new packet.
