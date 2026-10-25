//! Jenquai Seeker Reactor Optimization rank rules and durable timed status.
//!
//! Net-7 documents each tier's reactor capacity/recharge percentages, the
//! ten-minute duration, self-only ranks 1–4, and selected friendly targets at
//! ranks 5–7. The archived Net7 tree has no executable handler. Charge,
//! reactor cost and cooldown below are local server tuning.
use serde::{Deserialize, Serialize};

pub const DURATION_MS: u64 = 600_000;
pub const CHARGE_SECONDS: f32 = 2.;
pub const COOLDOWN_SECONDS: f32 = 30.;
pub const BUFF_SLOT: usize = 11;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct OptimizationTier {
    pub rank: u32,
    pub capacity_percent: f32,
    pub recharge_percent: f32,
    pub range: f32,
    pub energy: f32,
}

/// Returns:
///     Option<OptimizationTier>: A trained native ability and its bounded bonuses.
pub fn tier(ability: u32, trained: u32) -> Option<OptimizationTier> {
    let (rank, capacity_percent, recharge_percent, range, energy) = match ability {
        82 => (1, if trained >= 2 { 10. } else { 5. }, 0., 0., 75.),
        83 => (3, if trained >= 4 { 15. } else { 10. }, 10., 0., 100.),
        84 => (5, 20., 0., 3500., 125.),
        85 => (6, 20., 10., 3750., 150.),
        86 => (7, 25., 20., 4000., 200.),
        _ => return None,
    };
    (trained >= rank).then_some(OptimizationTier {
        rank,
        capacity_percent,
        recharge_percent,
        range,
        energy,
    })
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ReactorOptimizationBuff {
    pub revision: u64,
    pub source: u32,
    pub target_epoch: u64,
    pub ability: u32,
    pub trained_rank: u32,
    pub expires_at_ms: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingReactorOptimization {
    pub target: u32,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
}

impl PendingReactorOptimization {
    /// Returns:
    ///     bool: Whether a saved cast can be settled against a live recipient.
    pub fn valid(&self) -> bool {
        self.target != 0 && self.effect != 0 && tier(self.ability, self.trained_rank).is_some()
    }
}

impl ReactorOptimizationBuff {
    /// Returns:
    ///     bool: Whether a recipient receipt still has a bounded active tier.
    pub fn valid(&self, now_ms: u64, epoch: u64) -> bool {
        self.revision > 0
            && self.source > 0
            && self.target_epoch == epoch
            && tier(self.ability, self.trained_rank).is_some()
            && self.expires_at_ms > now_ms
            && self.expires_at_ms <= now_ms.saturating_add(DURATION_MS)
    }

    /// Returns:
    ///     OptimizationTier: Frozen tier percentages validated at admission.
    pub fn bonuses(&self) -> OptimizationTier {
        tier(self.ability, self.trained_rank).expect("validated Reactor Optimization tier")
    }

    /// Apply the skill after ordinary equipment/device modifiers so it stacks.
    pub fn apply(&self, capacity: &mut f32, recharge: &mut f32) {
        let tier = self.bonuses();
        *capacity *= 1. + tier.capacity_percent / 100.;
        *recharge *= 1. + tier.recharge_percent / 100.;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_native_tiers_include_intermediate_training_and_target_range() {
        assert_eq!(tier(82, 1).unwrap().capacity_percent, 5.);
        assert_eq!(tier(82, 2).unwrap().capacity_percent, 10.);
        assert_eq!(tier(83, 3).unwrap().recharge_percent, 10.);
        assert_eq!(tier(83, 4).unwrap().capacity_percent, 15.);
        assert_eq!(tier(84, 5).unwrap().range, 3500.);
        assert_eq!(tier(85, 6).unwrap().range, 3750.);
        assert_eq!(tier(86, 7).unwrap().range, 4000.);
        assert_eq!(tier(86, 6), None);
        let buff = ReactorOptimizationBuff {
            revision: 1,
            source: 0x40000001,
            target_epoch: 2,
            ability: 86,
            trained_rank: 7,
            expires_at_ms: 600_001,
        };
        assert!(buff.valid(1, 2));
        let (mut capacity, mut recharge) = (100., 20.);
        buff.apply(&mut capacity, &mut recharge);
        assert_eq!((capacity, recharge), (125., 24.));
    }
}
