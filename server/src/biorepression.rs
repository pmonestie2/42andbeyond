//! Terran Enforcer Biorepression bands and organic-target planning.
//!
//! Native ability IDs are pinned in PlayerSkills.h/PlayerClass.cpp. Net-7
//! documents suppression modes and range; area radii and numeric slow/success
//! values are intentionally supplied as bounded local integration inputs.
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

pub const SKILL_ID: u32 = 3;
pub const MAX_TARGETS: usize = 256;
pub const AREA_RADIUS: f32 = 1_500.;
pub const CHARGE_SECONDS: f32 = 1.5;
pub const COOLDOWN_SECONDS: f32 = 15.;
pub const MAX_DURATION_MS: u64 = 24_000;

/// Returns:
///     Option<f32>: Locally tuned reactor cost for one native tier.
pub fn energy_cost(rank: u32) -> Option<f32> {
    match rank {
        1 => Some(25.),
        3 => Some(50.),
        5 => Some(80.),
        6 => Some(100.),
        7 => Some(130.),
        _ => None,
    }
}

/// Returns:
///     u64: Locally tuned lifetime that grows with trained rank.
pub fn duration_ms(trained_rank: u32) -> u64 {
    8_000 + 2_000 * u64::from(trained_rank.min(7))
}

/// Returns:
///     f32: Locally tuned weapon reload multiplier for slow tiers.
pub fn reload_factor(rank: u32, trained_rank: u32) -> f32 {
    if rank == 7 {
        return 1.;
    }
    (1.2 + 0.1 * trained_rank.min(7) as f32).min(1.9)
}

/// Returns:
///     f32: Locally tuned remaining probability of a MOB warning call.
pub fn skill_use_fraction(rank: u32, trained_rank: u32) -> f32 {
    if matches!(rank, 3 | 6) {
        (0.75 - 0.05 * trained_rank.min(7) as f32).max(0.4)
    } else {
        1.
    }
}

/// Returns:
///     f32: Bounded local one-weapon disable chance, harder for one mount.
pub fn disable_chance(weapon_slots: usize, combat_level: u32, mob_level: u32) -> f32 {
    if weapon_slots == 0 {
        return 0.;
    }
    let base = if weapon_slots == 1 { 0.35 } else { 0.65 };
    let gap = mob_level as i64 - combat_level as i64;
    (base - 0.02 * gap as f32).clamp(0.05, 0.95)
}

/// Returns:
///     f32: Stable [0,1) roll across a failed settlement retry.
pub fn disable_roll(effect: u32, mob_source: u32, generation: u64) -> f32 {
    let mut value =
        u64::from(effect) ^ (u64::from(mob_source) << 32) ^ generation ^ 0x6269_6f72_6570_7201;
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^= value >> 31;
    ((value >> 40) as u32) as f32 / 16_777_216.
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct BioEffect {
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub mob_generation: u64,
    pub weapon_slot: Option<usize>,
    pub deadline_ms: u64,
}

impl BioEffect {
    /// Returns:
    ///     bool: Whether persisted IDs, tier, slot and deadline are bounded.
    pub fn valid(&self) -> bool {
        self.source != 0
            && self.source_epoch != 0
            && self.effect != 0
            && self.mob_generation < 1_000_000_000
            && self.deadline_ms != 0
            && tier(self.ability, self.trained_rank, "te").is_some()
            && self.weapon_slot.is_none_or(|slot| slot == 0)
            && (self.ability == 21) == self.weapon_slot.is_some()
    }

    /// Returns:
    ///     bool: Whether this receipt still affects the current MOB life.
    pub fn active(&self, now_ms: u64, generation: u64) -> bool {
        self.valid()
            && self.mob_generation == generation
            && self.deadline_ms > now_ms
            && self.deadline_ms <= now_ms.saturating_add(MAX_DURATION_MS)
    }

    /// Returns:
    ///     f32: Effective reload multiplier for this saved native tier.
    pub fn reload_factor(&self) -> f32 {
        reload_factor(
            tier(self.ability, self.trained_rank, "te").unwrap().rank,
            self.trained_rank,
        )
    }

    /// Returns:
    ///     f32: Effective nonweapon action probability for this tier.
    pub fn skill_use_fraction(&self) -> f32 {
        skill_use_fraction(
            tier(self.ability, self.trained_rank, "te").unwrap().rank,
            self.trained_rank,
        )
    }
}

/// Returns:
///     bool: Whether the pinned MOB_TYPE enum marks an organic red/green MOB.
pub fn is_organic(mob_type: i8) -> bool {
    matches!(mob_type, 2 | 3)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BiorepressionMode {
    SlowWeapons,
    SlowWeaponsAndSkills,
    AreaSlowWeapons,
    AreaSlowWeaponsAndSkills,
    DisableWeapon,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BiorepressionTier {
    pub rank: u32,
    pub trained_rank: u32,
    pub range: f32,
    pub mode: BiorepressionMode,
}

/// Returns:
///     Option<BiorepressionTier>: A trained Enforcer-only native mode.
pub fn tier(ability: u32, trained_rank: u32, class_code: &str) -> Option<BiorepressionTier> {
    if class_code != "te" || trained_rank > 7 {
        return None;
    }
    let (rank, mode) = match ability {
        22 => (1, BiorepressionMode::SlowWeapons),
        24 => (3, BiorepressionMode::SlowWeaponsAndSkills),
        23 => (5, BiorepressionMode::AreaSlowWeapons),
        25 => (6, BiorepressionMode::AreaSlowWeaponsAndSkills),
        21 => (7, BiorepressionMode::DisableWeapon),
        _ => return None,
    };
    (trained_rank >= rank).then_some(BiorepressionTier {
        rank,
        trained_rank,
        range: 1_000. + 250. * (trained_rank - 1) as f32,
        mode,
    })
}

#[derive(Clone, Copy, Debug)]
pub struct BioCandidate {
    pub object_id: u32,
    pub generation: u64,
    pub position: [f32; 3],
    pub alive: bool,
    pub organic: bool,
    pub weapon_slots: usize,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct BioTarget {
    pub object_id: u32,
    pub generation: u64,
    pub weapon_slot: Option<usize>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct BioPlan {
    pub sector: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub targets: Vec<BioTarget>,
}

#[derive(Clone, Copy, Debug)]
pub struct BioCast {
    pub band: BiorepressionTier,
    pub sector: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub origin: [f32; 3],
    pub selected: u32,
}

/// Returns:
///     Option<BioPlan>: Eligible organic lives and one pinned disable slot.
pub fn plan(cast: BioCast, candidates: &[BioCandidate]) -> Option<BioPlan> {
    let BioCast {
        band,
        sector,
        source,
        source_epoch,
        effect,
        origin,
        selected,
    } = cast;
    let local_area_radius = if matches!(
        band.mode,
        BiorepressionMode::AreaSlowWeapons | BiorepressionMode::AreaSlowWeaponsAndSkills
    ) {
        AREA_RADIUS
    } else {
        0.
    };
    let area = matches!(
        band.mode,
        BiorepressionMode::AreaSlowWeapons | BiorepressionMode::AreaSlowWeaponsAndSkills
    );
    if sector == 0
        || source == 0
        || source_epoch == 0
        || effect == 0
        || candidates.len() > MAX_TARGETS
        || !origin.into_iter().all(f32::is_finite)
        || !local_area_radius.is_finite()
        || area && !(0. < local_area_radius && local_area_radius <= 3_000.)
        || !area && local_area_radius != 0.
    {
        return None;
    }
    let primary = candidates
        .iter()
        .find(|candidate| candidate.object_id == selected)?;
    if !primary.alive
        || !primary.organic
        || !primary.position.into_iter().all(f32::is_finite)
        || distance(origin, primary.position)? > band.range
    {
        return None;
    }
    let mut identities = BTreeSet::new();
    let mut targets = Vec::new();
    for candidate in candidates {
        if candidate.object_id == 0
            || candidate.weapon_slots > 20
            || !candidate.position.into_iter().all(f32::is_finite)
            || !identities.insert(candidate.object_id)
        {
            return None;
        }
        if !candidate.alive
            || !candidate.organic
            || candidate.object_id != selected
                && (!area || distance(primary.position, candidate.position)? > local_area_radius)
        {
            continue;
        }
        let weapon_slot = if band.mode == BiorepressionMode::DisableWeapon {
            if candidate.weapon_slots == 0 {
                continue;
            }
            Some(
                (u64::from(effect) ^ u64::from(candidate.object_id) ^ candidate.generation)
                    as usize
                    % candidate.weapon_slots,
            )
        } else {
            None
        };
        targets.push(BioTarget {
            object_id: candidate.object_id,
            generation: candidate.generation,
            weapon_slot,
        });
    }
    if targets.is_empty() {
        return None;
    }
    Some(BioPlan {
        sector,
        source,
        source_epoch,
        effect,
        ability: match band.mode {
            BiorepressionMode::SlowWeapons => 22,
            BiorepressionMode::SlowWeaponsAndSkills => 24,
            BiorepressionMode::AreaSlowWeapons => 23,
            BiorepressionMode::AreaSlowWeaponsAndSkills => 25,
            BiorepressionMode::DisableWeapon => 21,
        },
        targets,
    })
}

/// Returns:
///     Option<f32>: Finite distance between two authoritative positions.
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
    fn enforcer_tiers_follow_native_rank_and_range() {
        assert_eq!(tier(22, 1, "te").unwrap().range, 1_000.);
        assert_eq!(tier(24, 3, "te").unwrap().range, 1_500.);
        assert_eq!(tier(23, 5, "te").unwrap().range, 2_000.);
        assert_eq!(tier(25, 6, "te").unwrap().range, 2_250.);
        assert_eq!(
            tier(21, 7, "te").unwrap().mode,
            BiorepressionMode::DisableWeapon
        );
        assert!(tier(21, 6, "te").is_none());
        assert!(tier(22, 1, "ts").is_none());
        assert!(is_organic(2) && is_organic(3));
        assert!(!is_organic(0) && !is_organic(4) && !is_organic(5));
    }

    #[test]
    fn area_targets_only_organic_lives_and_disable_pins_one_weapon() {
        let candidates = [
            BioCandidate {
                object_id: 10,
                generation: 1,
                position: [2_000., 0., 0.],
                alive: true,
                organic: true,
                weapon_slots: 2,
            },
            BioCandidate {
                object_id: 11,
                generation: 2,
                position: [3_500., 0., 0.],
                alive: true,
                organic: true,
                weapon_slots: 1,
            },
            BioCandidate {
                object_id: 12,
                generation: 3,
                position: [3_500., 0., 0.],
                alive: true,
                organic: false,
                weapon_slots: 1,
            },
        ];
        let area = plan(
            BioCast {
                band: tier(23, 5, "te").unwrap(),
                sector: 1,
                source: 7,
                source_epoch: 8,
                effect: 9,
                origin: [0.; 3],
                selected: 10,
            },
            &candidates,
        )
        .unwrap();
        assert_eq!(area.targets.len(), 2);
        assert_eq!(area.targets[1].generation, 2);
        let disabled = plan(
            BioCast {
                band: tier(21, 7, "te").unwrap(),
                sector: 1,
                source: 7,
                source_epoch: 8,
                effect: 9,
                origin: [0.; 3],
                selected: 10,
            },
            &candidates,
        )
        .unwrap();
        assert_eq!(disabled.targets.len(), 1);
        assert!(disabled.targets[0].weapon_slot.is_some());
        assert!(plan(
            BioCast {
                band: tier(21, 7, "te").unwrap(),
                sector: 1,
                source: 7,
                source_epoch: 8,
                effect: 9,
                origin: [0.; 3],
                selected: 12
            },
            &candidates
        )
        .is_none());
    }
}
