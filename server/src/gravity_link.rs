//! Gravity Link rank rules and the durable hostile-MOB debuff.
//!
//! PlayerClass.cpp names the five native ability IDs, but its AGravityLink
//! implementation is missing. The original manual establishes slowing and
//! area effects; Net-7 documents ranges, radii and the speed formula. Energy,
//! charge, cooldown and duration are local server tuning, not recovered values.
use serde::{Deserialize, Serialize};

pub const DURATION_MS: u64 = 30_000;
pub const CHARGE_SECONDS: f32 = 1.0;
pub const COOLDOWN_SECONDS: f32 = 10.0;

/// Returns:
///     Option<(u32, f32, f32)>: Tier rank, target range and area radius.
pub fn tier(ability: u32, trained: u32) -> Option<(u32, f32, f32)> {
    let (rank, radius) = match ability {
        73 => (1, 0.),
        47 => (3, 0.),
        59 => (5, 0.),
        12 => (6, 1500.),
        10 => (7, 3000.),
        _ => return None,
    };
    (trained >= rank).then_some((
        rank,
        5000. + 250. * trained.saturating_sub(1) as f32,
        radius,
    ))
}

/// Returns:
///     f32: Locally tuned reactor cost for one completed cast.
pub fn energy_cost(rank: u32) -> f32 {
    20. + 5. * rank as f32
}

/// Returns:
///     f32: Target top-speed fraction from Net-7's level and skill formula.
pub fn speed_fraction(rank: u32, skill: f32, combat_level: u32, mob_level: u32) -> f32 {
    let offset = if matches!(rank, 3 | 5 | 7) {
        4.
    } else if rank == 1 && skill >= 2. {
        2.
    } else {
        0.
    };
    (((mob_level as f32 - combat_level as f32 + 35.) / 50.) - (skill + offset) / 20.).clamp(0., 1.)
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct GravityDebuff {
    pub source: u32,
    pub epoch: u64,
    pub effect: u32,
    pub deadline_ms: u64,
    pub speed_fraction: f32,
    pub impact_explosive_bonus: f32,
}

impl GravityDebuff {
    /// Returns:
    ///     bool: Whether a saved effect has safe identity, timing and modifiers.
    pub fn valid(&self) -> bool {
        self.source != 0
            && self.epoch != 0
            && self.effect != 0
            && self.deadline_ms > 0
            && self.speed_fraction.is_finite()
            && (0. ..=1.).contains(&self.speed_fraction)
            && matches!(self.impact_explosive_bonus, 0. | 0.3)
    }

    /// Returns:
    ///     bool: Whether this effect still changes the target at the given wall time.
    pub fn active(&self, now_ms: u64) -> bool {
        self.valid() && now_ms < self.deadline_ms
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_ids_ranges_and_speed_formula_remain_bounded() {
        assert_eq!(tier(73, 1), Some((1, 5000., 0.)));
        assert_eq!(tier(47, 3), Some((3, 5500., 0.)));
        assert_eq!(tier(59, 5), Some((5, 6000., 0.)));
        assert_eq!(tier(12, 6), Some((6, 6250., 1500.)));
        assert_eq!(tier(10, 7), Some((7, 6500., 3000.)));
        assert_eq!(tier(10, 5), None);
        assert!((speed_fraction(7, 7., 50, 50) - 0.15).abs() < 0.0001);
        assert_eq!(speed_fraction(7, 7., 50, 1), 0.);
        assert_eq!(speed_fraction(1, 1., 1, 100), 1.);
    }
}
