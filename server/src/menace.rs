//! Progen Menace tiers and a durable MOB fear window.
//!
//! The official Player Guide establishes fleeing and higher-rank area effects.
//! Net-7 documents tier names, ranges, radii, and weapon/ability suppression:
//! https://net7wiki.bmsite.net/index.php?title=Menace
//! Archived PlayerClass.cpp maps the native ability IDs, but the AMenace
//! implementation is absent. Duration, cost, charge, cooldown, and chance
//! below are explicit local server tuning rather than recovered game balance.
use serde::{Deserialize, Serialize};

pub const SKILL_ID: u32 = 33;
pub const CHARGE_SECONDS: f32 = 1.5;
pub const COOLDOWN_SECONDS: f32 = 15.;
pub const MAX_DURATION_MS: u64 = 22_000;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MenaceTier {
    pub rank: u32,
    pub range: f32,
    pub radius: f32,
    pub energy: f32,
    pub duration_ms: u64,
    pub suppress_abilities: bool,
    pub suppress_weapons: bool,
    /// Archived PlayerClass.cpp names only Intimidate and Scare effects.
    pub verified_effect: Option<u16>,
}

/// Returns:
///     Option<MenaceTier>: A trained native tier and bounded combat behavior.
pub fn tier(ability: u32, trained: u32) -> Option<MenaceTier> {
    let (rank, radius, energy, suppress_abilities, suppress_weapons, verified_effect) =
        match ability {
            64 => (1, 0., 25., false, false, Some(198)),
            99 => (3, 0., 45., true, false, Some(199)),
            127 => (5, 0., 65., true, true, None),
            11 => (6, 1500., 90., false, false, None),
            18 => (7, 3000., 120., true, true, None),
            _ => return None,
        };
    if trained < rank || trained > 7 {
        return None;
    }
    Some(MenaceTier {
        rank,
        range: 2500. + 250. * (trained - 1) as f32,
        radius,
        energy,
        duration_ms: 8_000 + 2_000 * trained as u64,
        suppress_abilities,
        suppress_weapons,
        verified_effect,
    })
}

/// Returns:
///     f32: Locally tuned chance of frightening one target, bounded to 5–90%.
pub fn success_chance(trained: u32, caster_combat: u32, mob_level: u32) -> f32 {
    let level_gap = mob_level as i64 - caster_combat as i64;
    (0.25 + 0.06 * trained.min(7) as f32 - 0.04 * level_gap as f32).clamp(0.05, 0.90)
}

/// Returns:
///     bool: Whether a normalized authoritative roll frightens this target.
pub fn succeeds(chance: f32, roll: f32) -> bool {
    chance.is_finite()
        && roll.is_finite()
        && (0. ..=1.).contains(&chance)
        && (0. ..1.).contains(&roll)
        && roll < chance
}

/// Returns:
///     f32: Stable [0, 1) roll for a cast and one MOB death generation.
pub fn cast_roll(effect: u32, mob_source: u32, mob_generation: u64) -> f32 {
    // Retry of a completed cast must make the same success decision without
    // consuming another world RNG value or rerolling each area recipient.
    let mut value =
        u64::from(effect) ^ (u64::from(mob_source) << 32) ^ mob_generation ^ 0x9e37_79b9_7f4a_7c15;
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^= value >> 31;
    ((value >> 40) as u32) as f32 / 16_777_216.
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct MenaceFear {
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub mob_generation: u64,
    pub origin: [f32; 3],
    pub deadline_ms: u64,
}

impl MenaceFear {
    /// Returns:
    ///     bool: Whether this saved effect has valid identity and finite origin.
    pub fn valid(&self) -> bool {
        self.source != 0
            && self.source_epoch != 0
            && self.effect != 0
            && self.deadline_ms != 0
            && tier(self.ability, self.trained_rank).is_some()
            && self
                .origin
                .iter()
                .all(|coordinate| coordinate.is_finite() && coordinate.abs() <= 2_000_000.)
    }

    /// Returns:
    ///     bool: Whether fear still applies to this living MOB generation.
    pub fn active(&self, now_ms: u64, mob_generation: u64) -> bool {
        self.valid()
            && self.mob_generation == mob_generation
            && now_ms < self.deadline_ms
            && self.deadline_ms <= now_ms.saturating_add(MAX_DURATION_MS)
    }

    /// Returns:
    ///     bool: Whether this tier prevents the MOB's special ability use.
    pub fn suppress_abilities(&self) -> bool {
        tier(self.ability, self.trained_rank).is_some_and(|tier| tier.suppress_abilities)
    }

    /// Returns:
    ///     bool: Whether this tier prevents the MOB's weapon launches.
    pub fn suppress_weapons(&self) -> bool {
        tier(self.ability, self.trained_rank).is_some_and(|tier| tier.suppress_weapons)
    }

    /// Returns:
    ///     [f32; 3]: A stable unit vector away from the caster's cast position.
    pub fn away_direction(&self, position: [f32; 3]) -> [f32; 3] {
        let delta = [
            position[0] - self.origin[0],
            position[1] - self.origin[1],
            position[2] - self.origin[2],
        ];
        let length = delta.iter().map(|axis| axis * axis).sum::<f32>().sqrt();
        if !length.is_finite() || length < 0.001 {
            // Coincident positions should still yield deterministic flight.
            return [1., 0., 0.];
        }
        [delta[0] / length, delta[1] / length, delta[2] / length]
    }

    /// Returns:
    ///     [f32; 3]: Bounded flight position after an active simulation step.
    pub fn flee_position(&self, position: [f32; 3], speed: f32, dt: f32) -> [f32; 3] {
        if !self.valid()
            || !position
                .iter()
                .all(|axis| axis.is_finite() && axis.abs() <= 2_000_000.)
            || !speed.is_finite()
            || !dt.is_finite()
            || !(0. ..5.).contains(&dt)
        {
            return position;
        }
        let step = speed.clamp(0., 1000.) * dt;
        let direction = self.away_direction(position);
        std::array::from_fn(|index| {
            (position[index] + direction[index] * step).clamp(-2_000_000., 2_000_000.)
        })
    }
}

/// Returns:
///     bool: Whether a selected/area MOB may receive this fear effect now.
pub fn eligible_target(
    tier: MenaceTier,
    caster: [f32; 3],
    center: [f32; 3],
    mob: [f32; 3],
    alive: bool,
) -> bool {
    let finite = caster
        .into_iter()
        .chain(center)
        .chain(mob)
        .all(f32::is_finite);
    finite
        && alive
        && distance(caster, center) <= tier.range
        && distance(center, mob) <= tier.radius
}

/// Returns:
///     f32: Center distance, or infinity for coordinates that cannot be used.
fn distance(a: [f32; 3], b: [f32; 3]) -> f32 {
    a.into_iter()
        .zip(b)
        .map(|(left, right)| (left - right).powi(2))
        .sum::<f32>()
        .sqrt()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Public tier gates, ranges and radii remain distinct for PS and PP.
    #[test]
    fn native_tiers_and_target_boundaries() {
        assert_eq!(tier(64, 1).unwrap().range, 2500.);
        assert_eq!(tier(64, 2).unwrap().range, 2750.);
        assert_eq!(tier(99, 3).unwrap().range, 3000.);
        assert_eq!(tier(64, 1).unwrap().verified_effect, Some(198));
        assert_eq!(tier(99, 3).unwrap().verified_effect, Some(199));
        assert_eq!(tier(127, 5).unwrap().range, 3500.);
        assert_eq!(tier(127, 5).unwrap().verified_effect, None);
        assert_eq!(tier(11, 6).unwrap().radius, 1500.);
        assert_eq!(tier(18, 7).unwrap().radius, 3000.);
        assert_eq!(tier(18, 5), None);
        assert_eq!(tier(64, 8), None);
        assert_eq!(tier(999, 7), None);
        let area = tier(11, 6).unwrap();
        assert!(eligible_target(
            area,
            [0., 0., 0.],
            [3750., 0., 0.],
            [5250., 0., 0.],
            true,
        ));
        assert!(!eligible_target(
            area,
            [0., 0., 0.],
            [3751., 0., 0.],
            [3750., 0., 0.],
            true,
        ));
        assert!(!eligible_target(
            area,
            [0., 0., 0.],
            [3750., 0., 0.],
            [5251., 0., 0.],
            true,
        ));
    }

    /// Scare blocks abilities, Terrify also blocks fire, and fear expires.
    #[test]
    fn fear_suppression_flight_and_respawn() {
        let mut fear = MenaceFear {
            source: 0x4000_0001,
            source_epoch: 3,
            effect: 72,
            ability: 99,
            trained_rank: 3,
            mob_generation: 8,
            origin: [0., 0., 0.],
            deadline_ms: 15_000,
        };
        assert!(fear.active(14_999, 8));
        assert!(!fear.active(15_000, 8));
        assert!(!fear.active(14_999, 9));
        assert!(fear.suppress_abilities());
        assert!(!fear.suppress_weapons());
        assert_eq!(fear.away_direction([0., 300., 400.]), [0., 0.6, 0.8]);
        assert_eq!(fear.away_direction([0., 0., 0.]), [1., 0., 0.]);
        assert_eq!(
            fear.flee_position([0., 300., 400.], 200., 1.),
            [0., 420., 560.]
        );
        assert_eq!(
            fear.flee_position([1_999_999., 0., 0.], 200., 1.),
            [2_000_000., 0., 0.]
        );
        assert_eq!(fear.flee_position([0., 0., 0.], 200., 5.), [0., 0., 0.]);
        fear.ability = 127;
        fear.trained_rank = 5;
        assert!(fear.suppress_weapons());
        fear.origin[0] = f32::NAN;
        assert!(!fear.valid());
    }

    /// High-level targets resist more often, but valid rolls remain bounded.
    #[test]
    fn success_roll_is_deterministic_and_level_sensitive() {
        let even = success_chance(5, 30, 30);
        assert!(even > success_chance(5, 30, 45));
        assert!(even < success_chance(7, 30, 30));
        assert!(succeeds(even, 0.));
        assert!(!succeeds(even, 0.95));
        assert!(!succeeds(even, f32::NAN));
        assert_eq!(cast_roll(72, 9, 3), cast_roll(72, 9, 3));
        assert_ne!(cast_roll(72, 9, 3), cast_roll(72, 9, 4));
    }
}
