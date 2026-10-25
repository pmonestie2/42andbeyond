//! Archived Progen Warrior Shield Sap rules, independent of transport state.
//! AShieldSap.cpp has executable cost, timing, range, drain and restore formulas.
//! Its area drain and group healing branches are stubs; the public skill table
//! defines their recipient geometry and intended distribution.
use serde::{Deserialize, Serialize};

pub const CHARGE_SECONDS: f32 = 3.;
pub const COOLDOWN_SECONDS: f32 = 0.;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SapTier {
    pub rank: u32,
    pub range: f32,
    pub radius: f32,
    pub energy: f32,
    pub group_heal: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingShieldSap {
    pub target: u32,
    pub target_generation: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
}

impl PendingShieldSap {
    /// Returns:
    ///     bool: Whether the saved cast has a bounded native tier and target.
    pub fn valid(&self) -> bool {
        (crate::sector_combat::MOB_ID..crate::sector_combat::MOB_ID + 256).contains(&self.target)
            && self.target_generation < 1_000_000_000
            && self.effect != 0
            && tier(self.ability, self.trained_rank).is_some()
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SapReceipt {
    pub epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub caster_shield_capacity: f32,
    pub drained: f32,
    pub targets: Vec<u32>,
    pub recipients: Vec<u32>,
}

impl SapReceipt {
    /// Returns:
    ///     bool: Whether this atomic combat receipt is safe to replay.
    pub fn valid(&self) -> bool {
        let Some(tier) = tier(self.ability, self.trained_rank) else {
            return false;
        };
        self.epoch > 0
            && self.effect > 0
            && self.drained.is_finite()
            && self.caster_shield_capacity.is_finite()
            && (1. ..=1_000_000_000.).contains(&self.caster_shield_capacity)
            && (0. ..=100_000_000.).contains(&self.drained)
            && !self.targets.is_empty()
            && self.targets.len() <= 256
            && self.targets.iter().all(|target| {
                (crate::sector_combat::MOB_ID..crate::sector_combat::MOB_ID + 256).contains(target)
            })
            && self
                .targets
                .iter()
                .collect::<std::collections::BTreeSet<_>>()
                .len()
                == self.targets.len()
            && self.recipients.len() <= 6
            && (tier.rank < 3 && self.recipients.is_empty()
                || tier.rank >= 3
                    && !self.recipients.is_empty()
                    && (tier.group_heal || self.recipients.len() == 1))
            && self
                .recipients
                .iter()
                .all(|target| (0x4000_0001..=0x4000_ffff).contains(target))
            && self
                .recipients
                .iter()
                .collect::<std::collections::BTreeSet<_>>()
                .len()
                == self.recipients.len()
    }
}

/// Returns:
///     Option<SapTier>: Archived ability cost and trained-level geometry.
pub fn tier(ability: u32, trained: u32) -> Option<SapTier> {
    let (rank, energy, group_heal) = match ability {
        112 => (1, 35., false),
        114 => (3, 75., false),
        54 => (5, 150., true),
        98 => (6, 300., false),
        55 => (7, 600., true),
        _ => return None,
    };
    (trained >= rank).then_some(SapTier {
        rank,
        range: 1000. + 250. * trained.saturating_sub(1) as f32,
        radius: if rank >= 6 {
            (rank - 5) as f32 * 1500.
        } else {
            0.
        },
        energy,
        group_heal,
    })
}

/// Returns:
///     f32: Shield points drained from one target, bounded by caster capacity.
pub fn drain_amount(target_shield: f32, caster_capacity: f32, trained: u32) -> f32 {
    (target_shield.max(0.) * (0.12 + 0.03 * trained as f32))
        .min(caster_capacity.max(0.) * 0.5)
        .min(target_shield.max(0.))
}

/// Returns:
///     f32: Total shield points available for self or group restoration.
pub fn restore_amount(drained: f32, trained: u32, tier_rank: u32) -> f32 {
    if tier_rank < 3 {
        return 0.;
    }
    drained.max(0.) * (0.75 + (trained as f32 - 3.) * 0.125).max(0.)
}

/// Returns:
///     f32: One group recipient's award, capped at half the caster's shield capacity.
pub fn group_share(total_restore: f32, recipients: usize, caster_capacity: f32) -> f32 {
    if recipients == 0 {
        return 0.;
    }
    (total_restore / recipients as f32).min(caster_capacity.max(0.) * 0.5)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn archived_tiers_and_formula_boundaries() {
        assert_eq!(tier(112, 1).unwrap().energy, 35.);
        assert_eq!(tier(114, 4).unwrap().range, 1750.);
        assert!(tier(54, 5).unwrap().group_heal);
        assert_eq!(tier(98, 6).unwrap().radius, 1500.);
        assert_eq!(tier(55, 7).unwrap().radius, 3000.);
        assert_eq!(tier(55, 6), None);
        assert!((drain_amount(1000., 1000., 1) - 150.).abs() < 0.001);
        assert_eq!(drain_amount(10000., 1000., 7), 500.);
        assert_eq!(restore_amount(100., 3, 1), 0.);
        assert_eq!(restore_amount(100., 3, 3), 75.);
        assert_eq!(restore_amount(100., 7, 7), 125.);
        assert_eq!(group_share(1000., 2, 1000.), 500.);
        let out_of_sector = PendingShieldSap {
            target: crate::sector_combat::MOB_ID + 256,
            target_generation: 0,
            effect: 1,
            ability: 112,
            trained_rank: 1,
        };
        assert!(!out_of_sector.valid());
    }
}
