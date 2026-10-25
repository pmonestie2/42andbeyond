//! Progen Sentinel Repulsor Field tiers and pure hit-feedback planning.
//!
//! The archived PlayerClass.cpp maps ability IDs to ARepulsorField, but its
//! executable handler is absent. Net-7 supplies the rank, energy, reflection
//! and range table: https://net7wiki.bmsite.net/index.php?title=Repulsor_Field
//! Duration, charge and cooldown are explicit local tuning pending integration.
use serde::{Deserialize, Serialize};

pub const SKILL_ID: u32 = 48;
pub const CHARGE_SECONDS: f32 = 2.;
pub const COOLDOWN_SECONDS: f32 = 30.;
pub const MAX_EFFECTIVE_RANK: f32 = 20.;
pub const FEEDBACK_DAMAGE_TYPE: usize = 3; // Native Energy damage.
pub const MAX_TOTAL_REFLECTION: f32 = 4_850.; // Max tier/gear plus Environment.

/// Returns:
///     usize: Class-exclusive native status slot for this recipient.
pub fn buff_slot(recipient_class: &str) -> usize {
    if recipient_class == "pw" {
        12 // Power Down is Sentinel-only; preserve Warrior Combat Trance slot 9.
    } else {
        9 // Combat Trance is Warrior-only; preserve Sentinel Power Down slot 12.
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RepulsorTier {
    pub rank: u32,
    pub energy: f32,
    pub return_fraction: f32,
    pub cap_per_effective_rank: f32,
    pub range: f32,
    pub self_only: bool,
}

impl RepulsorTier {
    /// Returns:
    ///     f32: Maximum reflected Energy damage for a frozen effective rank.
    pub fn feedback_cap(self, effective_rank: f32) -> f32 {
        self.cap_per_effective_rank * effective_rank
    }
}

/// Returns:
///     Option<RepulsorTier>: Highest native ability band trained for this ID.
pub fn tier(ability: u32, trained_rank: u32) -> Option<RepulsorTier> {
    let (rank, energy, return_fraction, cap_per_effective_rank, self_only) = match ability {
        75 => (1, 50., 0.2, 15., true),
        70 => (3, 125., 0.4, 30., false),
        96 => (5, 250., 0.6, 60., false),
        50 => (6, 500., 0.6, 120., false),
        72 => (7, 1_000., 0.6, 240., false),
        _ => return None,
    };
    if trained_rank < rank || trained_rank > 7 {
        return None;
    }
    Some(RepulsorTier {
        rank,
        energy,
        return_fraction,
        cap_per_effective_rank,
        range: 2_500. + 250. * (trained_rank - 1) as f32,
        self_only,
    })
}

/// Returns:
///     Option<f32>: Bounded effective rank after catalog SKILL_REPULSOR_FIELD gear.
pub fn effective_rank(trained_rank: u32, gear_bonus: f32) -> Option<f32> {
    if !(1..=7).contains(&trained_rank) || !gear_bonus.is_finite() || gear_bonus < 0. {
        return None;
    }
    Some((trained_rank as f32 + gear_bonus).min(MAX_EFFECTIVE_RANK))
}

/// Returns:
///     u64: Locally tuned lifetime; higher effective rank lasts longer.
pub fn duration_ms(effective_rank: f32) -> u64 {
    (60_000. + (effective_rank - 1.).max(0.) * 15_000.) as u64
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RepulsorBuff {
    pub revision: u64,
    pub source: u32,
    pub target_epoch: u64,
    pub ability: u32,
    pub trained_rank: u32,
    pub effective_rank: f32,
    pub expires_at_ms: u64,
}

impl RepulsorBuff {
    /// Returns:
    ///     bool: Whether a recipient-bound active field remains in its lifetime.
    pub fn valid(&self, now_ms: u64, recipient_epoch: u64) -> bool {
        self.revision > 0
            && self.source > 0
            && self.target_epoch == recipient_epoch
            && tier(self.ability, self.trained_rank).is_some()
            && self.effective_rank.is_finite()
            && self.effective_rank >= self.trained_rank as f32
            && self.effective_rank <= MAX_EFFECTIVE_RANK
            && self.expires_at_ms > now_ms
            && self.expires_at_ms <= now_ms.saturating_add(duration_ms(self.effective_rank))
    }

    /// Returns:
    ///     Option<Feedback>: Frozen Energy reflection for one accepted NPC hit.
    pub fn feedback(
        &self,
        post_resist_damage: f32,
        damage_type: usize,
        now_ms: u64,
        recipient_epoch: u64,
    ) -> Option<Feedback> {
        if !post_resist_damage.is_finite() || post_resist_damage < 0. {
            return None;
        }
        let reflected = if self.valid(now_ms, recipient_epoch) && damage_type < 6 {
            let band = tier(self.ability, self.trained_rank)?;
            (post_resist_damage * band.return_fraction).min(band.feedback_cap(self.effective_rank))
        } else {
            0.
        };
        Some(Feedback {
            reflected,
            damage_type: FEEDBACK_DAMAGE_TYPE,
        })
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Feedback {
    pub reflected: f32,
    pub damage_type: usize,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingRepulsorField {
    pub target: u32,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub effective_rank: f32,
}

impl PendingRepulsorField {
    /// Returns:
    ///     bool: Whether an unfinished cast has a trained, bounded receipt.
    pub fn valid(&self) -> bool {
        self.target > 0
            && self.effect > 0
            && tier(self.ability, self.trained_rank).is_some()
            && self.effective_rank.is_finite()
            && (self.trained_rank as f32..=MAX_EFFECTIVE_RANK).contains(&self.effective_rank)
    }
}

/// Returns:
///     bool: Whether a visible alive recipient is allowed at this trained tier.
pub fn target_in_range(
    band: RepulsorTier,
    source_id: u32,
    recipient_id: u32,
    source: [f32; 3],
    recipient: [f32; 3],
    alive: bool,
    visible: bool,
) -> bool {
    if source_id == 0
        || recipient_id == 0
        || !alive
        || !visible
        || band.self_only && source_id != recipient_id
        || !source.into_iter().chain(recipient).all(f32::is_finite)
    {
        return false;
    }
    source
        .into_iter()
        .zip(recipient)
        .map(|(left, right)| (left - right).powi(2))
        .sum::<f32>()
        .sqrt()
        <= band.range
}

#[cfg(test)]
mod tests {
    use super::*;

    fn buff() -> RepulsorBuff {
        RepulsorBuff {
            revision: 3,
            source: 0x4000_0001,
            target_epoch: 7,
            ability: 72,
            trained_rank: 7,
            effective_rank: 10.33,
            expires_at_ms: 200_000,
        }
    }

    /// The native table, intermediate ranks and selected-player gates remain stable.
    #[test]
    fn rank_table_and_target_rules() {
        assert_eq!(tier(75, 1).unwrap().energy, 50.);
        assert_eq!(tier(75, 2).unwrap().range, 2750.);
        assert_eq!(tier(70, 3).unwrap().return_fraction, 0.4);
        assert_eq!(tier(70, 4).unwrap().range, 3250.);
        assert_eq!(tier(96, 5).unwrap().cap_per_effective_rank, 60.);
        assert_eq!(tier(50, 6).unwrap().cap_per_effective_rank, 120.);
        assert_eq!(tier(72, 7).unwrap().energy, 1_000.);
        assert_eq!(tier(72, 6), None);
        assert_eq!(tier(75, 0), None);
        let self_band = tier(75, 1).unwrap();
        assert!(target_in_range(
            self_band, 1, 1, [0.; 3], [0.; 3], true, true
        ));
        assert!(!target_in_range(
            self_band, 1, 2, [0.; 3], [0.; 3], true, true
        ));
        let ally_band = tier(70, 3).unwrap();
        assert!(target_in_range(
            ally_band,
            1,
            2,
            [0.; 3],
            [3000., 0., 0.],
            true,
            true
        ));
        assert!(!target_in_range(
            ally_band,
            1,
            2,
            [0.; 3],
            [3001., 0., 0.],
            true,
            true
        ));
        assert!(!target_in_range(
            ally_band, 1, 2, [0.; 3], [0.; 3], false, true
        ));
    }

    /// Catalog skill bonuses increase caps without unlocking later abilities.
    #[test]
    fn gear_rank_and_feedback_caps() {
        assert_eq!(effective_rank(7, 3.33), Some(10.33));
        assert_eq!(effective_rank(0, 3.33), None);
        assert_eq!(effective_rank(7, f32::NAN), None);
        assert_eq!(effective_rank(7, 99.), Some(MAX_EFFECTIVE_RANK));
        assert_eq!(tier(72, 6), None);
        let field = buff();
        assert!(field.valid(100, 7));
        assert!((field.feedback(100., 3, 100, 7).unwrap().reflected - 60.).abs() < 0.01);
        assert!((field.feedback(100_000., 3, 100, 7).unwrap().reflected - 2479.2).abs() < 0.01);
        assert_eq!(field.feedback(100., 8, 100, 7).unwrap().reflected, 0.);
        assert_eq!(field.feedback(100., 3, 100, 9).unwrap().reflected, 0.);
        assert_eq!(field.feedback(100., 3, 200_000, 7).unwrap().reflected, 0.);
        assert!(field.feedback(f32::NAN, 3, 100, 7).is_none());
    }

    /// A frozen, epoch-bound cast and status can survive an ordinary replacement.
    #[test]
    fn receipt_and_restart_bounds() {
        let field = buff();
        let encoded = serde_json::to_vec(&field).unwrap();
        let restored: RepulsorBuff = serde_json::from_slice(&encoded).unwrap();
        assert!(restored.valid(100, 7));
        assert!(!restored.valid(100, 8));
        assert_eq!(
            restored.feedback(50., 0, 100, 7).unwrap(),
            field.feedback(50., 0, 100, 7).unwrap()
        );
        let pending = PendingRepulsorField {
            target: 0x4000_0002,
            effect: 5,
            ability: 70,
            trained_rank: 3,
            effective_rank: 5.59,
        };
        assert!(pending.valid());
        assert!(!PendingRepulsorField {
            trained_rank: 2,
            ..pending
        }
        .valid());
    }

    /// PW and PS can each show Repulsor beside their own exclusive skill.
    #[test]
    fn native_status_slot_avoids_class_exclusive_conflicts() {
        assert_eq!(buff_slot("pw"), 12);
        assert_eq!(buff_slot("ps"), 9);
        assert_eq!(buff_slot("je"), 9);
    }
}
