# Equipped Cloaking Time

The strongest intact equipped `STAT_CLOAKING_TIME` modifier is frozen when a
trained Cloak engages. It adds one detection-strength point per rounded
second, making the ship harder for MOBs to see, and extends the five-second
post-Combat-Cloak Stealth Strike window by the listed seconds. The public
catalog supplies values up to 30 seconds; these conversions are local tuning
because the archived skill handler has no executable detection formula.

The same items describe shorter Cloak engage time with a three-second floor.
Current Cloak abilities already charge for three seconds, so that floor
prevents an engage-time reduction. The saved bonus survives a server
replacement while cloaked, and removing the item after engagement does not
shorten the pending Stealth Strike window.
