# Equipped Counterstrike

The strongest intact equipped `STAT_COUNTERSTRIKE` modifier reflects the
listed percentage of an incoming instant MOB hit to that attacker. The
reflection uses the same post-deflect damage amount as native damage text and
the existing Repulsor Field feedback, capped at a 40% equipped value. It adds
to other reflection without reflecting a reflected hit again.

The amount is frozen in the durable MOB-hit receipt before the world
acknowledgment. Retries or a server replacement reuse the same amount even
if the item is damaged or removed. The public catalog supplies the percentage;
the post-deflect basis and cap are local combat tuning.
