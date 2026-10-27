//! Terran Tradesman Shield Charging tiers and durable recipient status.
//!
//! https://net7wiki.bmsite.net/index.php?title=Shield_Charging supplies the
//! ten-minute duration, target/range gates and all capacity, recharge and
//! resistance percentages. The pinned PlayerClass.cpp names AShieldCharge
//! inside its not-yet-implemented comment block, so reactor cost, cast time
//! and cooldown below are explicit local tuning, matching the parallel
//! Reactor Optimization support family.
use serde::{Deserialize, Serialize};

pub const DURATION_MS: u64 = 600_000;
pub const CHARGE_SECONDS: f32 = 2.;
pub const COOLDOWN_SECONDS: f32 = 30.;
pub const BUFF_SLOT: usize = 13;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ShieldChargingTier {
    pub rank: u32,
    pub capacity_percent: f32,
    pub recharge_percent: f32,
    pub resistance_percent: f32,
    /// Zero means self-only. Later tiers permit a selected friendly pilot.
    pub range: f32,
    pub energy: f32,
}

/// Returns:
///     Option<ShieldChargingTier>: Trained native tier and its bounded bonuses.
pub fn tier(ability: u32, trained: u32) -> Option<ShieldChargingTier> {
    let (rank, capacity_percent, recharge_percent, resistance_percent, range, energy) =
        match ability {
            121 => (1, if trained >= 2 { 10. } else { 5. }, 0., 0., 0., 75.),
            129 => (3, if trained >= 4 { 15. } else { 10. }, 10., 0., 0., 100.),
            122 => (5, 20., 0., 0., 3500., 125.),
            130 => (6, 20., 10., 0., 3750., 150.),
            74 => (7, 25., 20., 10., 4000., 200.),
            _ => return None,
        };
    (trained >= rank).then_some(ShieldChargingTier {
        rank,
        capacity_percent,
        recharge_percent,
        resistance_percent,
        range,
        energy,
    })
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ShieldChargingBuff {
    pub revision: u64,
    pub source: u32,
    pub target_epoch: u64,
    pub ability: u32,
    pub trained_rank: u32,
    pub expires_at_ms: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingShieldCharging {
    pub target: u32,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
}

impl PendingShieldCharging {
    /// Returns:
    ///     bool: Whether the saved cast has a trained tier and target identity.
    pub fn valid(&self) -> bool {
        self.target != 0 && self.effect != 0 && tier(self.ability, self.trained_rank).is_some()
    }
}

impl ShieldChargingBuff {
    /// Returns:
    ///     &'static str: Native status name for the applied Shield Charging tier.
    pub fn status_name(&self) -> &'static str {
        match self.ability {
            121 => "Supercharge_Shields",
            129 => "Ultracharge_Shields",
            122 => "Supercharge_Target",
            130 => "Ultracharge_Target",
            74 => "Megacharge_Shields",
            _ => unreachable!("validated Shield Charging tier"),
        }
    }

    /// Returns:
    ///     u32: Former native visual identity, retained to remove its old effect.
    pub fn visual_id(&self, target: u32) -> u32 {
        target.rotate_left(7) ^ (self.revision as u32) ^ 0x5c00_0000
    }

    /// Returns:
    ///     bool: Whether the recipient receipt remains in its ten-minute window.
    pub fn valid(&self, now_ms: u64, epoch: u64) -> bool {
        self.revision > 0
            && self.source > 0
            && self.target_epoch == epoch
            && tier(self.ability, self.trained_rank).is_some()
            && self.expires_at_ms > now_ms
            && self.expires_at_ms <= now_ms.saturating_add(DURATION_MS)
    }

    /// Returns:
    ///     ShieldChargingTier: Frozen tier percentages validated on receipt.
    pub fn bonuses(&self) -> ShieldChargingTier {
        tier(self.ability, self.trained_rank).expect("validated Shield Charging tier")
    }

    /// Apply the capacity, regeneration and typed resistance after gear effects.
    pub fn apply(&self, capacity: &mut f32, recharge: &mut f32, deflects: &mut [f32; 9]) {
        let bonuses = self.bonuses();
        *capacity *= 1. + bonuses.capacity_percent / 100.;
        *recharge *= 1. + bonuses.recharge_percent / 100.;
        for value in deflects {
            *value = (*value + bonuses.resistance_percent).min(50.);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Verify each native ID and the rank-two/rank-four training improvements.
    #[test]
    fn all_tiers_and_intermediate_training_match_public_table() {
        assert_eq!(tier(121, 1).unwrap().capacity_percent, 5.);
        assert_eq!(tier(121, 2).unwrap().capacity_percent, 10.);
        assert_eq!(tier(129, 3).unwrap().recharge_percent, 10.);
        assert_eq!(tier(129, 4).unwrap().capacity_percent, 15.);
        assert_eq!(tier(122, 5).unwrap().range, 3500.);
        assert_eq!(tier(130, 6).unwrap().range, 3750.);
        assert_eq!(tier(74, 7).unwrap().range, 4000.);
        assert_eq!(tier(74, 7).unwrap().resistance_percent, 10.);
        assert_eq!(tier(74, 6), None);
        assert_eq!(tier(121, 0), None);
        for (ability, rank, name) in [
            (121, 1, "Supercharge_Shields"),
            (129, 3, "Ultracharge_Shields"),
            (122, 5, "Supercharge_Target"),
            (130, 6, "Ultracharge_Target"),
            (74, 7, "Megacharge_Shields"),
        ] {
            let buff = ShieldChargingBuff {
                revision: 1,
                source: 1,
                target_epoch: 1,
                ability,
                trained_rank: rank,
                expires_at_ms: DURATION_MS,
            };
            assert_eq!(buff.status_name(), name);
        }
    }

    /// Recipient state must be bounded, epoch-pinned, and apply all three bonuses.
    #[test]
    fn recipient_receipt_applies_bonuses_and_rejects_expired_or_wrong_epoch() {
        let buff = ShieldChargingBuff {
            revision: 1,
            source: 0x4000_0001,
            target_epoch: 9,
            ability: 74,
            trained_rank: 7,
            expires_at_ms: DURATION_MS + 100,
        };
        assert!(buff.valid(100, 9));
        assert!(!buff.valid(100, 10));
        assert!(!buff.valid(DURATION_MS + 100, 9));
        let (mut capacity, mut recharge, mut deflects) = (100., 20., [45.; 9]);
        buff.apply(&mut capacity, &mut recharge, &mut deflects);
        assert_eq!((capacity, recharge), (125., 24.));
        assert_eq!(deflects, [50.; 9]);
        assert!(PendingShieldCharging {
            target: 0x4000_0002,
            effect: 33,
            ability: 122,
            trained_rank: 5,
        }
        .valid());
    }
}
