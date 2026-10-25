# Equipped Power Down skill

The strongest intact equipped `SKILL_POWERDOWN` modifier adds at most four
effective skill levels. Trained rank still controls which Power Down tier can
be toggled. Each equipped level lowers activation energy by five percent and
raises the tier's base reactor and shield recharge fraction by five percentage
points, capped at full base recharge. These two conversion rates are local
tuning because the archived server has no executable Power Down handler.

The discounted energy is spent and checkpointed when the toggle begins. The
active tier and inventory survive a server replacement; recharge uses the
restored equipment. Starting a higher tier still requires the trained rank.
