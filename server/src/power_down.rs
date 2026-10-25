//! Progen Sentinel Power Down tier rules from the public skill reference:
//! https://net7wiki.bmsite.net/index.php?title=Power_Down
//! The archived PlayerClass.cpp names the abilities but has no executable
//! Power Down handler. Ranked success probabilities and gear's STAT_POWERDOWN
//! bonuses are not quantified there; an active ship is fully hidden from MOB
//! targeting rather than inventing an unverified random failure rate.

pub const COOLDOWN_SECONDS: f32 = 3.;
pub const STATUS_SLOT: usize = 12;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PowerDownTier {
    pub rank: u32,
    pub energy: f32,
    pub shield_recharge_fraction: f32,
    pub reactor_recharge_fraction: f32,
}

/// Returns:
///     Option<PowerDownTier>: Trained native toggle cost and recharge rates.
pub fn tier(ability: u32, trained: u32) -> Option<PowerDownTier> {
    let (rank, energy, shield_recharge_fraction, reactor_recharge_fraction) = match ability {
        78 => (1, 35., 0., 0.),
        1 => (3, 75., 0.5, 0.),
        2 => (5, 150., 0.5, 0.5),
        3 => (6, 300., 1., 0.5),
        4 => (7, 600., 1., 1.),
        _ => return None,
    };
    (trained >= rank).then_some(PowerDownTier {
        rank,
        energy,
        shield_recharge_fraction,
        reactor_recharge_fraction,
    })
}

/// Returns:
///     f32: Native base recharge scaled by the tier, plus other equipped bonuses.
pub fn recharge_rate(ordinary: f32, base: f32, fraction: f32) -> f32 {
    (ordinary - base + base * fraction).max(0.)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn five_native_tiers_have_documented_cost_and_recharge() {
        assert_eq!(tier(78, 1).unwrap().energy, 35.);
        assert_eq!(tier(1, 3).unwrap().shield_recharge_fraction, 0.5);
        assert_eq!(tier(2, 5).unwrap().reactor_recharge_fraction, 0.5);
        assert_eq!(tier(3, 6).unwrap().shield_recharge_fraction, 1.);
        assert_eq!(tier(4, 7).unwrap().reactor_recharge_fraction, 1.);
        assert_eq!(tier(4, 6), None);
        assert_eq!(recharge_rate(150., 100., 0.), 50.);
        assert_eq!(recharge_rate(150., 100., 0.5), 100.);
    }
}
