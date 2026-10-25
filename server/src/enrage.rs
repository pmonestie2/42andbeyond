//! Terran Enforcer and Progen Warrior Enrage rules. The archived class table
//! identifies the five abilities and effect 212 but has no executable handler.
use serde::{Deserialize, Serialize};

pub const DURATION_MS: u64 = 20_000;
pub const CHARGE_SECONDS: f32 = 1.5;
pub const COOLDOWN_SECONDS: f32 = 10.;
pub const VERIFIED_EFFECT: u16 = 212;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct EnrageTier {
    pub rank: u32,
    pub range: f32,
    pub group: bool,
    pub accuracy_multiplier: f32,
    pub energy: f32,
}

/// Returns:
///     f32: Locally tuned chance of angering one target, bounded to 5–95%.
pub fn success_chance(trained: u32, caster_combat: u32, mob_level: u32) -> f32 {
    let level_gap = mob_level as i64 - caster_combat as i64;
    (0.45 + 0.06 * trained.min(7) as f32 - 0.035 * level_gap as f32).clamp(0.05, 0.95)
}

/// Returns:
///     f32: Stable [0, 1) roll for this cast and MOB generation.
pub fn cast_roll(effect: u32, mob_source: u32, mob_generation: u64) -> f32 {
    let mut value =
        u64::from(effect) ^ (u64::from(mob_source) << 32) ^ mob_generation ^ 0xd1b5_4a32_d192_ed03;
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^= value >> 31;
    ((value >> 40) as u32) as f32 / 16_777_216.
}

/// Returns:
///     Option<EnrageTier>: Rules for one ability unlocked by trained rank.
pub fn tier(ability: u32, trained_rank: u32) -> Option<EnrageTier> {
    let (rank, group, accuracy_multiplier, energy) = match ability {
        6 => (1, false, 1., 35.),
        27 => (3, false, 1., 75.),
        41 => (5, false, 0.65, 150.),
        7 => (6, true, 1., 300.),
        42 => (7, true, 0.65, 600.),
        _ => return None,
    };
    if trained_rank < rank || trained_rank > 7 {
        return None;
    }
    Some(EnrageTier {
        rank,
        range: 5_000. + 250. * (trained_rank - 1) as f32,
        group,
        accuracy_multiplier,
        energy,
    })
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
pub struct EnrageEffect {
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub target_generation: u64,
    pub expires_at_ms: u64,
    pub accuracy_multiplier: f32,
}

impl EnrageEffect {
    /// Returns:
    ///     u32: Stable native visual identity unique to this caster and MOB.
    pub fn visual_id(&self, mob_id: u32) -> u32 {
        (self.source.rotate_left(11) ^ self.effect.rotate_left(3) ^ mob_id).max(1)
    }

    /// Returns:
    ///     bool: Whether this effect belongs to the current living target.
    pub fn active(&self, now_ms: u64, generation: u64) -> bool {
        self.valid()
            && self.target_generation == generation
            && self.expires_at_ms > now_ms
            && self.expires_at_ms <= now_ms.saturating_add(DURATION_MS)
    }

    /// Returns:
    ///     bool: Whether this stored effect has bounded identities and values.
    pub fn valid(&self) -> bool {
        (0x4000_0001..=0x4000_ffff).contains(&self.source)
            && self.source_epoch > 0
            && self.effect > 0
            && self.target_generation < 1_000_000_000
            && self.expires_at_ms > 0
            && self.accuracy_multiplier.is_finite()
            && (0.1..=1.).contains(&self.accuracy_multiplier)
    }
}

/// Returns:
///     bool: Whether an area target belongs to the selected enemy formation.
pub fn formation_member(
    selected: u32,
    candidate: u32,
    selected_group: u32,
    candidate_group: u32,
    distance_from_target: f32,
) -> bool {
    selected == candidate
        || selected_group != 0
            && candidate_group == selected_group
            && distance_from_target.is_finite()
            && distance_from_target <= 1500.
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_tiers_have_class_bounded_progression() {
        assert_eq!(tier(6, 1).unwrap().range, 5000.);
        assert_eq!(tier(27, 4).unwrap().range, 5750.);
        assert_eq!(tier(41, 5).unwrap().accuracy_multiplier, 0.65);
        assert!(tier(7, 6).unwrap().group);
        assert!(tier(42, 7).unwrap().group);
        assert!(tier(42, 6).is_none());
        assert!(tier(6, 8).is_none());
    }

    #[test]
    fn only_explicit_formation_members_are_included() {
        assert!(formation_member(1, 1, 0, 0, 0.));
        assert!(!formation_member(1, 2, 0, 0, 20.));
        assert!(formation_member(1, 2, 4, 4, 1400.));
        assert!(!formation_member(1, 2, 4, 4, 1600.));
        assert!(!formation_member(1, 2, 4, 5, 10.));
    }

    #[test]
    fn rank_and_target_level_change_deterministic_success() {
        assert!(success_chance(7, 20, 20) > success_chance(1, 20, 20));
        assert!(success_chance(7, 20, 20) > success_chance(7, 10, 30));
        assert_eq!(cast_roll(6, 4, 9), cast_roll(6, 4, 9));
        assert_ne!(cast_roll(6, 4, 9), cast_roll(7, 4, 9));
    }

    #[test]
    fn effect_expires_and_cannot_cross_a_mob_respawn() {
        let effect = EnrageEffect {
            source: 0x4000_0001,
            source_epoch: 2,
            effect: 3,
            target_generation: 4,
            expires_at_ms: 21_000,
            accuracy_multiplier: 0.65,
        };
        assert!(effect.active(1_000, 4));
        assert!(!effect.active(21_000, 4));
        assert!(!effect.active(1_000, 5));
        assert!(!effect.active(1, 4));
    }
}
