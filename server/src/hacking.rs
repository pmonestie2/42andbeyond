//! Terran Hacking tiers and deterministic MOB-system attempt planning.
//!
//! PlayerSkills.h and PlayerClass.cpp verify native IDs and effect descriptor
//! 193. The executable AHacking handler is absent from the pinned C++ tree.
//! https://net7wiki.bmsite.net/index.php?title=Hacking supplies targeting tiers.
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

pub const SKILL_ID: u32 = 26;
pub const VISUAL_DESCRIPTOR: u16 = 193;
pub const MAX_CANDIDATES: usize = 256;
pub const CHARGE_SECONDS: f32 = 2.;
pub const COOLDOWN_SECONDS: f32 = 15.;
pub const MAX_DURATION_MS: u64 = 22_000;

/// Returns:
///     f32: Locally tuned reactor cost for one native tier.
pub fn energy_cost(rank: u32) -> Option<f32> {
    match rank {
        1 => Some(30.),
        3 => Some(60.),
        5 => Some(120.),
        6 => Some(180.),
        7 => Some(240.),
        _ => None,
    }
}

/// Returns:
///     u64: Locally tuned successful hack lifetime.
pub fn duration_ms(trained: u32) -> u64 {
    8_000 + 2_000 * u64::from(trained.min(7))
}

/// Returns:
///     f32: Bounded, locally tuned chance after level and equipped gear.
pub fn success_chance(trained: u32, caster_level: u32, mob_level: u32, gear_bonus: f32) -> f32 {
    let level_gap = mob_level as i64 - caster_level as i64;
    (0.50 + 0.04 * trained.min(7) as f32 - 0.025 * level_gap as f32
        + gear_bonus.clamp(0., 50.) / 100.)
        .clamp(0.05, 0.95)
}

/// Returns:
///     f32: Stable [0,1) roll for a generation and subsystem attempt.
pub fn attempt_roll(effect: u32, mob_source: u32, generation: u64, salt: u64) -> f32 {
    let mut value = u64::from(effect)
        ^ (u64::from(mob_source) << 32)
        ^ generation
        ^ salt
        ^ 0x6861_636b_696e_6701;
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^= value >> 31;
    ((value >> 40) as u32) as f32 / 16_777_216.
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct HackEffect {
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub mob_generation: u64,
    pub system: Option<ShipSystem>,
    pub weapon_slot: Option<usize>,
    pub jam_communications: bool,
    pub deadline_ms: u64,
}

impl HackEffect {
    /// Returns:
    ///     bool: Whether saved identity, rank and subsystem fields are bounded.
    pub fn valid(&self) -> bool {
        self.source != 0
            && self.source_epoch != 0
            && self.effect != 0
            && self.deadline_ms != 0
            && self.mob_generation < 1_000_000_000
            && tier(self.ability, self.trained_rank, "te").is_some()
            && self.weapon_slot.is_none_or(|slot| slot < 20)
            && (self.system.is_some() || self.weapon_slot.is_some() || self.jam_communications)
    }

    /// Returns:
    ///     bool: Whether this generation still has an active hack.
    pub fn active(&self, now_ms: u64, generation: u64) -> bool {
        self.valid()
            && self.mob_generation == generation
            && self.deadline_ms > now_ms
            && self.deadline_ms <= now_ms.saturating_add(MAX_DURATION_MS)
    }

    /// Returns:
    ///     u32: Stable visual identity for native descriptor 193.
    pub fn visual_id(&self, object_id: u32) -> u32 {
        0x6c00_0000 | ((self.source ^ self.effect ^ object_id) & 0x00ff_ffff)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum HackMode {
    System,
    Weapon,
    Multi,
    AreaSystem,
    AreaMulti,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HackTier {
    pub rank: u32,
    pub trained_rank: u32,
    pub mode: HackMode,
    pub range: f32,
    pub radius: f32,
}

/// Returns:
///     Option<HackTier>: Native ability band for a trained Enforcer or Scout.
pub fn tier(ability: u32, trained_rank: u32, class_code: &str) -> Option<HackTier> {
    let max_rank = match class_code {
        "te" => 7,
        "ts" => 5,
        _ => return None,
    };
    let (rank, mode, radius) = match ability {
        57 => (1, HackMode::System, 0.),
        58 => (3, HackMode::Weapon, 0.),
        76 => (5, HackMode::Multi, 0.),
        16 => (6, HackMode::AreaSystem, 1_500.),
        13 => (7, HackMode::AreaMulti, 3_000.),
        _ => return None,
    };
    if trained_rank < rank || trained_rank > max_rank {
        return None;
    }
    Some(HackTier {
        rank,
        trained_rank,
        mode,
        range: 1_000. + 250. * (trained_rank - 1) as f32,
        radius,
    })
}

#[derive(Clone, Copy, Debug)]
pub struct HackCandidate {
    pub object_id: u32,
    pub generation: u64,
    pub position: [f32; 3],
    pub alive: bool,
    pub organic: bool,
    pub weapon_slots: usize,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum ShipSystem {
    Engine,
    Reactor,
    Shield,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct HackAttempt {
    pub object_id: u32,
    pub generation: u64,
    pub system: Option<ShipSystem>,
    pub weapon_slot: Option<usize>,
    pub jam_communications: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct HackPlan {
    pub sector: u32,
    pub caster: u32,
    pub caster_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub attempts: Vec<HackAttempt>,
}

#[derive(Clone, Copy, Debug)]
pub struct HackCast {
    pub sector: u32,
    pub caster: u32,
    pub caster_epoch: u64,
    pub effect: u32,
    pub source: [f32; 3],
    pub selected: u32,
    pub ability: u32,
}

/// Returns:
///     Option<HackPlan>: Frozen, duplicate-free targets and deterministic subsystem attempts.
pub fn plan(band: HackTier, cast: HackCast, candidates: &[HackCandidate]) -> Option<HackPlan> {
    if tier(cast.ability, band.trained_rank, "te") != Some(band)
        || cast.sector == 0
        || cast.caster == 0
        || cast.caster_epoch == 0
        || cast.effect == 0
        || cast.selected == 0
        || candidates.len() > MAX_CANDIDATES
        || !cast.source.into_iter().all(f32::is_finite)
    {
        return None;
    }
    let mut identities = BTreeSet::new();
    for candidate in candidates {
        if candidate.object_id == 0
            || candidate.weapon_slots > 20
            || !candidate.position.into_iter().all(f32::is_finite)
            || !identities.insert((candidate.object_id, candidate.generation))
        {
            return None;
        }
    }
    let primary = candidates
        .iter()
        .find(|mob| mob.object_id == cast.selected)?;
    if !primary.alive || primary.organic || distance(cast.source, primary.position)? > band.range {
        return None;
    }
    let mut attempts = Vec::new();
    for mob in candidates {
        if !mob.alive || mob.organic {
            continue;
        }
        if mob.object_id != cast.selected
            && (band.radius == 0. || distance(primary.position, mob.position)? > band.radius)
        {
            continue;
        }
        let seed = u64::from(cast.effect) ^ (u64::from(mob.object_id) << 1) ^ mob.generation;
        let system = match band.mode {
            HackMode::System | HackMode::Multi | HackMode::AreaSystem | HackMode::AreaMulti => {
                Some(match seed % 3 {
                    0 => ShipSystem::Engine,
                    1 => ShipSystem::Reactor,
                    _ => ShipSystem::Shield,
                })
            }
            HackMode::Weapon => None,
        };
        let weapon_slot = if matches!(
            band.mode,
            HackMode::Weapon | HackMode::Multi | HackMode::AreaMulti
        ) && mob.weapon_slots > 0
        {
            Some((seed as usize) % mob.weapon_slots)
        } else {
            None
        };
        attempts.push(HackAttempt {
            object_id: mob.object_id,
            generation: mob.generation,
            system,
            weapon_slot,
            jam_communications: matches!(band.mode, HackMode::Multi | HackMode::AreaMulti),
        });
    }
    Some(HackPlan {
        sector: cast.sector,
        caster: cast.caster,
        caster_epoch: cast.caster_epoch,
        effect: cast.effect,
        ability: cast.ability,
        attempts,
    })
}

/// Returns:
///     Option<f32>: Finite center distance for authoritative range checks.
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

    fn mob(object_id: u32, x: f32) -> HackCandidate {
        HackCandidate {
            object_id,
            generation: 0,
            position: [x, 0., 0.],
            alive: true,
            organic: false,
            weapon_slots: 2,
        }
    }

    /// Enforcers unlock area tiers; Scouts retain only the first three IDs.
    #[test]
    fn native_class_tiers_and_intermediate_range() {
        assert_eq!(tier(57, 1, "te").unwrap().range, 1_000.);
        assert_eq!(tier(57, 2, "ts").unwrap().range, 1_250.);
        assert_eq!(tier(58, 3, "te").unwrap().mode, HackMode::Weapon);
        assert_eq!(tier(76, 5, "ts").unwrap().mode, HackMode::Multi);
        assert_eq!(tier(16, 6, "te").unwrap().radius, 1_500.);
        assert_eq!(tier(13, 7, "te").unwrap().radius, 3_000.);
        assert!(tier(16, 6, "ts").is_none());
        assert!(tier(57, 1, "jd").is_none());
        assert!(tier(13, 6, "te").is_none());
    }

    /// Only the selected nonorganic MOB and valid area neighbors are frozen.
    #[test]
    fn targeting_and_deterministic_subsystems() {
        let band = tier(13, 7, "te").unwrap();
        let mut candidates = [
            mob(10, 2400.),
            mob(11, 5400.),
            mob(12, 5401.),
            mob(13, 2400.),
        ];
        candidates[3].organic = true;
        let cast = HackCast {
            sector: 1076,
            caster: 1,
            caster_epoch: 3,
            effect: 9,
            source: [0.; 3],
            selected: 10,
            ability: 13,
        };
        let plan = plan(band, cast, &candidates).unwrap();
        assert_eq!(plan.attempts.len(), 2);
        assert_eq!(
            (plan.attempts[0].object_id, plan.attempts[1].object_id),
            (10, 11)
        );
        assert!(plan.attempts.iter().all(|effect| effect.system.is_some()
            && effect.weapon_slot.is_some()
            && effect.jam_communications));
        assert_eq!(plan, self::plan(band, cast, &candidates).unwrap());
        let encoded = serde_json::to_vec(&plan).unwrap();
        assert_eq!(serde_json::from_slice::<HackPlan>(&encoded).unwrap(), plan);
        assert!(self::plan(
            band,
            HackCast {
                selected: 13,
                ..cast
            },
            &candidates
        )
        .is_none());
        assert!(self::plan(band, cast, &[candidates[0], candidates[0]]).is_none());
        assert!(self::plan(
            band,
            HackCast {
                source: [f32::NAN, 0., 0.],
                ..cast
            },
            &candidates
        )
        .is_none());
    }

    /// Local chance and duration remain bounded, with gear improving real outcomes.
    #[test]
    fn local_success_cost_and_effect_lifetime_are_bounded() {
        assert_eq!(energy_cost(1), Some(30.));
        assert_eq!(energy_cost(7), Some(240.));
        assert_eq!(energy_cost(2), None);
        assert_eq!(duration_ms(7), MAX_DURATION_MS);
        let base = success_chance(5, 30, 30, 0.);
        assert!(success_chance(5, 30, 30, 34.) > base);
        assert!(success_chance(1, 1, 66, 0.) >= 0.05);
        assert!(success_chance(7, 66, 1, 100.) <= 0.95);
        assert_eq!(attempt_roll(1, 2, 3, 4), attempt_roll(1, 2, 3, 4));
        let effect = HackEffect {
            source: 1,
            source_epoch: 2,
            effect: 3,
            ability: 57,
            trained_rank: 1,
            mob_generation: 0,
            system: Some(ShipSystem::Engine),
            weapon_slot: None,
            jam_communications: false,
            deadline_ms: 10_000,
        };
        assert!(effect.active(1, 0));
        assert!(!effect.active(10_000, 0));
        assert!(!effect.active(1, 1));
    }

    /// Single-target weapon hacking has no phantom slot on unarmed MOBs.
    #[test]
    fn weapon_attempt_requires_a_real_mount() {
        let band = tier(58, 3, "ts").unwrap();
        let armed = mob(10, 100.);
        let cast = HackCast {
            sector: 1,
            caster: 1,
            caster_epoch: 1,
            effect: 1,
            source: [0.; 3],
            selected: 10,
            ability: 58,
        };
        let attempt = plan(band, cast, &[armed]).unwrap();
        assert_eq!(attempt.attempts.len(), 1);
        assert_eq!(attempt.attempts[0].system, None);
        assert!(attempt.attempts[0].weapon_slot.is_some());
        let mut unarmed = armed;
        unarmed.weapon_slots = 0;
        let no_weapon = plan(band, cast, &[unarmed]).unwrap();
        assert_eq!(no_weapon.attempts[0].weapon_slot, None);
    }
}
