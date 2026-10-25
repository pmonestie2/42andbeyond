//! Terran Tradesman Befriend target bands and replay-safe selection.
//!
//! PlayerClass.cpp verifies IDs 20/63/43/115/15 and visual descriptor 221.
//! Net-7 documents range, calming, resistance and area bands but not numeric
//! success chance, duration or psionic resistance reduction.
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

pub const SKILL_ID: u32 = 2;
pub const VISUAL_DESCRIPTOR: u16 = 221;
pub const MAX_TARGETS: usize = 256;
pub const CHARGE_SECONDS: f32 = 1.5;
pub const COOLDOWN_SECONDS: f32 = 15.;
pub const MAX_DURATION_MS: u64 = 44_000;

/// Returns:
///     Option<f32>: Locally tuned reactor cost for the native tier.
pub fn energy_cost(rank: u32) -> Option<f32> {
    match rank {
        1 => Some(20.),
        3 => Some(35.),
        5 => Some(55.),
        6 => Some(70.),
        7 => Some(90.),
        _ => None,
    }
}

/// Returns:
///     u64: Locally tuned successful reaction lifetime.
pub fn duration_ms(rank: u32) -> u64 {
    duration_ms_with_bonus(rank, 0.)
}

/// Returns:
///     u64: Reaction lifetime with a bounded equipped skill modifier.
pub fn duration_ms_with_bonus(rank: u32, bonus: f32) -> u64 {
    let bonus = if bonus.is_finite() {
        bonus.clamp(0., 3.)
    } else {
        0.
    };
    14_000 + (3_000. * (rank.min(7) as f32 + bonus)) as u64
}

/// Returns:
///     f32: Locally tuned and bounded chance against a target MOB level.
pub fn success_chance(rank: u32, caster_level: u32, mob_level: u32) -> f32 {
    success_chance_with_bonus(rank, 0., caster_level, mob_level)
}

/// Returns:
///     f32: Attempt chance after a bounded equipped Befriend skill bonus.
pub fn success_chance_with_bonus(rank: u32, bonus: f32, caster_level: u32, mob_level: u32) -> f32 {
    let bonus = if bonus.is_finite() {
        bonus.clamp(0., 3.)
    } else {
        0.
    };
    let gap = mob_level as i64 - caster_level as i64;
    (0.5 + 0.05 * (rank.min(7) as f32 + bonus) - 0.025 * gap as f32).clamp(0.05, 0.95)
}

/// Returns:
///     f32: Stable [0,1) per-cast roll, independent of world RNG order.
pub fn cast_roll(effect: u32, mob_source: u32, generation: u64) -> f32 {
    let mut value =
        u64::from(effect) ^ (u64::from(mob_source) << 32) ^ generation ^ 0x6265_6672_6965_6e64;
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^= value >> 31;
    ((value >> 40) as u32) as f32 / 16_777_216.
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct BefriendEffect {
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub mob_generation: u64,
    pub deadline_ms: u64,
}

impl BefriendEffect {
    /// Returns:
    ///     bool: Whether saved rank, identity and deadline are bounded.
    pub fn valid(&self) -> bool {
        self.source != 0
            && self.source_epoch != 0
            && self.effect != 0
            && self.mob_generation < 1_000_000_000
            && self.deadline_ms != 0
            && tier(self.ability, self.trained_rank, "tt").is_some()
    }

    /// Returns:
    ///     bool: Whether the current MOB generation remains affected.
    pub fn active(&self, now_ms: u64, generation: u64) -> bool {
        self.valid()
            && self.mob_generation == generation
            && self.deadline_ms > now_ms
            && self.deadline_ms <= now_ms.saturating_add(MAX_DURATION_MS)
    }

    /// Returns:
    ///     bool: Whether this player is the still-current befriended caster.
    pub fn befriends(&self, player: u32, epoch: u64, now_ms: u64, generation: u64) -> bool {
        self.source == player && self.source_epoch == epoch && self.active(now_ms, generation)
    }

    /// Returns:
    ///     f32: Attack acquisition range fraction for ranks three and above.
    pub fn calm_fraction(&self) -> f32 {
        match tier(self.ability, self.trained_rank, "tt").map(|band| band.rank) {
            Some(6 | 7) => 0.3,
            Some(3 | 5) => 0.5,
            _ => 1.,
        }
    }

    /// Returns:
    ///     f32: Extra typed Psionic damage fraction for Entrance/Soothe.
    pub fn psionic_vulnerability(&self) -> f32 {
        match tier(self.ability, self.trained_rank, "tt").map(|band| band.rank) {
            Some(6 | 7) => 0.4,
            Some(5) => 0.2,
            _ => 0.,
        }
    }

    /// Returns:
    ///     u32: Stable native visual identity for descriptor 221.
    pub fn visual_id(&self, object_id: u32) -> u32 {
        0x6d00_0000 | ((self.source ^ self.effect ^ object_id) & 0x00ff_ffff)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BefriendMode {
    Friendly,
    Calm,
    Entrance,
    Soothe,
    AreaSoothe,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BefriendTier {
    pub rank: u32,
    pub trained_rank: u32,
    pub mode: BefriendMode,
    pub range: f32,
    pub radius: f32,
}

/// Returns:
///     Option<BefriendTier>: A trained Tradesman's native targeting band.
pub fn tier(ability: u32, trained_rank: u32, class_code: &str) -> Option<BefriendTier> {
    if class_code != "tt" || trained_rank > 7 {
        return None;
    }
    let (rank, mode, radius) = match ability {
        20 => (1, BefriendMode::Friendly, 0.),
        63 => (3, BefriendMode::Calm, 0.),
        43 => (5, BefriendMode::Entrance, 0.),
        115 => (6, BefriendMode::Soothe, 0.),
        15 => (7, BefriendMode::AreaSoothe, 3_000.),
        _ => return None,
    };
    (trained_rank >= rank).then_some(BefriendTier {
        rank,
        trained_rank,
        mode,
        range: 5_000. + 250. * (trained_rank - 1) as f32,
        radius,
    })
}

#[derive(Clone, Copy, Debug)]
pub struct BefriendCandidate {
    pub object_id: u32,
    pub generation: u64,
    pub position: [f32; 3],
    pub alive: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct BefriendTarget {
    pub object_id: u32,
    pub generation: u64,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct BefriendPlan {
    pub sector: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub targets: Vec<BefriendTarget>,
}

#[derive(Clone, Copy, Debug)]
pub struct BefriendOrigin {
    pub band: BefriendTier,
    pub sector: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub position: [f32; 3],
    pub selected: u32,
}

/// Returns:
///     Option<BefriendPlan>: Alive MOB lives around one selected anchor.
pub fn plan(cast: BefriendOrigin, candidates: &[BefriendCandidate]) -> Option<BefriendPlan> {
    let BefriendOrigin {
        band,
        sector,
        source,
        source_epoch,
        effect,
        position: origin,
        selected,
    } = cast;
    if sector == 0
        || source == 0
        || source_epoch == 0
        || effect == 0
        || candidates.len() > MAX_TARGETS
        || !origin.into_iter().all(f32::is_finite)
    {
        return None;
    }
    let primary = candidates
        .iter()
        .find(|candidate| candidate.object_id == selected)?;
    if !primary.alive
        || !primary.position.into_iter().all(f32::is_finite)
        || distance(origin, primary.position)? > band.range
    {
        return None;
    }
    let mut identities = BTreeSet::new();
    let mut targets = Vec::new();
    for candidate in candidates {
        if candidate.object_id == 0
            || !candidate.position.into_iter().all(f32::is_finite)
            || !identities.insert(candidate.object_id)
        {
            return None;
        }
        if candidate.alive
            && (candidate.object_id == selected
                || band.radius > 0.
                    && distance(primary.position, candidate.position)? <= band.radius)
        {
            targets.push(BefriendTarget {
                object_id: candidate.object_id,
                generation: candidate.generation,
            });
        }
    }
    Some(BefriendPlan {
        sector,
        source,
        source_epoch,
        effect,
        ability: match band.mode {
            BefriendMode::Friendly => 20,
            BefriendMode::Calm => 63,
            BefriendMode::Entrance => 43,
            BefriendMode::Soothe => 115,
            BefriendMode::AreaSoothe => 15,
        },
        targets,
    })
}

/// Returns:
///     Option<f32>: Finite authoritative distance between two positions.
fn distance(left: [f32; 3], right: [f32; 3]) -> Option<f32> {
    let squared = left
        .into_iter()
        .zip(right)
        .map(|(a, b)| (a - b).powi(2))
        .sum::<f32>();
    squared.is_finite().then_some(squared.sqrt())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tradesman_only_bands_match_native_ranges_and_area() {
        assert_eq!(tier(20, 1, "tt").unwrap().range, 5_000.);
        assert_eq!(tier(63, 3, "tt").unwrap().range, 5_500.);
        assert_eq!(tier(43, 5, "tt").unwrap().mode, BefriendMode::Entrance);
        assert_eq!(tier(15, 7, "tt").unwrap().radius, 3_000.);
        assert!(tier(15, 6, "tt").is_none());
        assert!(tier(20, 1, "te").is_none());
    }

    #[test]
    fn area_selection_pins_only_alive_mob_generations() {
        let candidates = [
            BefriendCandidate {
                object_id: 10,
                generation: 1,
                position: [6_500., 0., 0.],
                alive: true,
            },
            BefriendCandidate {
                object_id: 11,
                generation: 2,
                position: [9_500., 0., 0.],
                alive: true,
            },
            BefriendCandidate {
                object_id: 12,
                generation: 3,
                position: [9_501., 0., 0.],
                alive: true,
            },
            BefriendCandidate {
                object_id: 13,
                generation: 4,
                position: [6_500., 0., 0.],
                alive: false,
            },
        ];
        let result = plan(
            BefriendOrigin {
                band: tier(15, 7, "tt").unwrap(),
                sector: 1076,
                source: 1,
                source_epoch: 2,
                effect: 3,
                position: [0.; 3],
                selected: 10,
            },
            &candidates,
        )
        .unwrap();
        assert_eq!(
            result.targets,
            vec![
                BefriendTarget {
                    object_id: 10,
                    generation: 1
                },
                BefriendTarget {
                    object_id: 11,
                    generation: 2
                }
            ]
        );
        assert!(plan(
            BefriendOrigin {
                band: tier(15, 7, "tt").unwrap(),
                sector: 1076,
                source: 1,
                source_epoch: 2,
                effect: 3,
                position: [0.; 3],
                selected: 13
            },
            &candidates
        )
        .is_none());
        assert!(plan(
            BefriendOrigin {
                band: tier(15, 7, "tt").unwrap(),
                sector: 1076,
                source: 1,
                source_epoch: 2,
                effect: 3,
                position: [0.; 3],
                selected: 10
            },
            &[candidates[0], candidates[0]]
        )
        .is_none());
    }

    #[test]
    fn tuned_effect_requires_matching_generation_and_expires() {
        let now = 10_000;
        let effect = BefriendEffect {
            source: 0x4000_0001,
            source_epoch: 7,
            effect: 91,
            ability: 43,
            trained_rank: 5,
            mob_generation: 12,
            deadline_ms: now + duration_ms(5),
        };
        assert!(effect.active(now, 12));
        assert!(effect.befriends(effect.source, 7, now, 12));
        assert!(!effect.befriends(effect.source, 8, now, 12));
        assert!(!effect.active(now, 13));
        assert!(!effect.active(effect.deadline_ms, 12));
        assert_eq!(effect.calm_fraction(), 0.5);
        assert_eq!(effect.psionic_vulnerability(), 0.2);
        assert_eq!(energy_cost(5), Some(55.));
        assert_eq!(success_chance(7, 50, 1), 0.95);
    }
}
