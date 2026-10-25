# Equipped device energy cost

Seven public items carry `STAT_DEVICE_ENERGY` in `server/data/items.json`.
Six Yellow/Orange Dragon lasers have operation 2 with a tooltip that increases
device energy cost by 10%. Reinforced Solar Sail (2628) has operation 4 with a
tooltip that reduces device energy cost by 39%. The existing equipment stat
engine chooses the strongest intact equipped modifier per operation. It now
derives a bounded activation multiplier from a 100% base; these examples
yield 110% and 61%, or 71% when both are equipped.

The modifier applies to all server-supported activated item costs: self buffs,
Reroute Shield Energy, selected friendly buffs, and Transfer Reactor Energy.
It does not alter trained player skill costs or the amount transferred to a
recipient. The target offers apply the multiplier before admission and the
atomic world-ledger write; ledger retries replay the frozen resulting balance
without re-reading gear. Self activations checkpoint the adjusted energy and
effect together. Mounted gear with zero structure contributes nothing.

The public catalog establishes cost direction and value, but does not specify
a global minimum or maximum for arbitrary future items. This implementation
locally bounds the derived multiplier to 10–400% to keep costs finite and
nonzero for paid devices. Current catalog values do not reach those bounds.
