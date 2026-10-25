//! Progen Warrior Self Destruct tiers and a pure, frozen blast plan.
//!
//! Pinned Skills.xml grants skill 50 only to Warrior; PlayerSkills.h names
//! ability IDs 100–104 and PlayerClass.cpp identifies visual descriptor 206.
//! The executable ASelfDestruct handler is absent from the pinned tree.
//! Public damage/radius values: https://net7wiki.bmsite.net/index.php?title=Self_Destruct
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

pub const SKILL_ID: u32 = 50;
pub const VISUAL_DESCRIPTOR: u16 = 206;
pub const ABSOLUTE_DAMAGE_TYPE: usize = 6;
pub const ACTION_LOCK_MS: u64 = 10_000;
pub const CHARGE_SECONDS: f32 = 3.;
pub const COOLDOWN_SECONDS: f32 = 60.;

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct PendingSelfDestruct {
    pub source: u32,
    pub source_epoch: u64,
    pub sector: u32,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    #[serde(default)]
    pub damage_bonus_percent: f32,
    pub position: [f32; 3],
}

impl PendingSelfDestruct {
    /// Returns:
    ///     bool: Whether a saved blast has a bounded caster and source pose.
    pub fn valid(&self) -> bool {
        (0x4000_0001..=0x4000_ffff).contains(&self.source)
            && self.source_epoch > 0
            && self.sector > 0
            && self.effect > 0
            && tier(self.ability, self.trained_rank).is_some()
            && self.damage_bonus_percent.is_finite()
            && (0. ..=200.).contains(&self.damage_bonus_percent)
            && self
                .position
                .iter()
                .all(|axis| axis.is_finite() && axis.abs() <= 2_000_000.)
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct BlastReceipt {
    pub source: u32,
    pub source_epoch: u64,
    pub sector: u32,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    #[serde(default)]
    pub damage_bonus_percent: f32,
    pub targets: Vec<BlastTarget>,
}

impl BlastReceipt {
    /// Returns:
    ///     bool: Whether a committed blast can be replayed without another hit.
    pub fn valid(&self) -> bool {
        let Some(band) = tier(self.ability, self.trained_rank) else {
            return false;
        };
        let unique = self
            .targets
            .iter()
            .map(|target| target.object_id)
            .collect::<BTreeSet<_>>()
            .len()
            == self.targets.len();
        (0x4000_0001..=0x4000_ffff).contains(&self.source)
            && self.source_epoch > 0
            && self.sector > 0
            && self.effect > 0
            && self.damage_bonus_percent.is_finite()
            && (0. ..=200.).contains(&self.damage_bonus_percent)
            && self.targets.len() <= 256
            && unique
            && self.targets.iter().all(|target| {
                (0x1000_0001..0x1000_0101).contains(&target.object_id)
                    && target.generation < 1_000_000_000
                    && target.damage_type == ABSOLUTE_DAMAGE_TYPE
                    && target.damage
                        == band.damage(self.trained_rank) * (1. + self.damage_bonus_percent / 100.)
            })
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SelfDestructTier {
    pub rank: u32,
    pub damage_per_trained_rank: f32,
    pub radius: f32,
}

impl SelfDestructTier {
    /// Returns:
    ///     f32: Public absolute blast damage before unverified gear effects.
    pub fn damage(self, trained_rank: u32) -> f32 {
        self.damage_per_trained_rank * trained_rank as f32
    }
}

/// Returns:
///     Option<SelfDestructTier>: Public band and intermediate trained radius.
pub fn tier(ability: u32, trained_rank: u32) -> Option<SelfDestructTier> {
    let (rank, damage_per_trained_rank) = match ability {
        100 => (1, 126.),
        101 => (3, 252.),
        102 => (5, 810.),
        103 => (6, 2_520.),
        104 => (7, 6_000.),
        _ => return None,
    };
    if trained_rank < rank || trained_rank > 7 {
        return None;
    }
    Some(SelfDestructTier {
        rank,
        damage_per_trained_rank,
        radius: 1_000. + 250. * (trained_rank - 1) as f32,
    })
}

/// One hostile life frozen at blast settlement, before sector damage mutation.
#[derive(Clone, Copy, Debug)]
pub struct HostileLife {
    pub object_id: u32,
    pub generation: u64,
    pub position: [f32; 3],
    pub alive: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct BlastTarget {
    pub object_id: u32,
    pub generation: u64,
    pub damage: f32,
    pub damage_type: usize,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct BlastPlan {
    pub caster_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub shield_after: f32,
    pub reactor_after: f32,
    pub targets: Vec<BlastTarget>,
}

/// Returns:
///     Option<BlastPlan>: One bounded, duplicate-free blast against frozen MOB lives.
pub fn plan_blast(
    warrior: bool,
    caster_epoch: u64,
    effect: u32,
    ability: u32,
    trained_rank: u32,
    center: [f32; 3],
    hostiles: &[HostileLife],
) -> Option<BlastPlan> {
    let band = tier(ability, trained_rank)?;
    if !warrior
        || caster_epoch == 0
        || effect == 0
        || !center.into_iter().all(f32::is_finite)
        || hostiles.len() > 256
    {
        return None;
    }
    let mut identities = BTreeSet::new();
    let mut targets = Vec::new();
    for hostile in hostiles {
        if hostile.object_id == 0
            || !hostile.position.into_iter().all(f32::is_finite)
            || !identities.insert((hostile.object_id, hostile.generation))
        {
            return None;
        }
        if !hostile.alive {
            continue;
        }
        let distance_squared = center
            .into_iter()
            .zip(hostile.position)
            .map(|(left, right)| (left - right).powi(2))
            .sum::<f32>();
        if !distance_squared.is_finite() {
            return None;
        }
        if distance_squared <= band.radius * band.radius {
            targets.push(BlastTarget {
                object_id: hostile.object_id,
                generation: hostile.generation,
                damage: band.damage(trained_rank),
                damage_type: ABSOLUTE_DAMAGE_TYPE,
            });
        }
    }
    Some(BlastPlan {
        caster_epoch,
        effect,
        ability,
        trained_rank,
        shield_after: 0.,
        reactor_after: 0.,
        targets,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Only the Warrior's five IDs are admitted; intermediate ranks widen radius.
    #[test]
    fn native_tiers_and_profession_gate() {
        assert_eq!(tier(100, 1).unwrap().damage(1), 126.);
        assert_eq!(tier(100, 2).unwrap().radius, 1250.);
        assert_eq!(tier(101, 3).unwrap().damage(3), 756.);
        assert_eq!(tier(101, 4).unwrap().radius, 1750.);
        assert_eq!(tier(102, 5).unwrap().damage(5), 4050.);
        assert_eq!(tier(103, 6).unwrap().damage(6), 15_120.);
        assert_eq!(tier(104, 7).unwrap().damage(7), 42_000.);
        assert_eq!(tier(104, 7).unwrap().radius, 2500.);
        assert!(tier(104, 6).is_none());
        assert!(tier(100, 0).is_none());
        assert!(plan_blast(false, 1, 1, 100, 1, [0.; 3], &[]).is_none());
    }

    /// The sector receives only alive, in-radius identities frozen once.
    #[test]
    fn blast_is_bounded_and_generation_pinned() {
        let lives = [
            HostileLife {
                object_id: 10,
                generation: 2,
                position: [2500., 0., 0.],
                alive: true,
            },
            HostileLife {
                object_id: 11,
                generation: 4,
                position: [2501., 0., 0.],
                alive: true,
            },
            HostileLife {
                object_id: 12,
                generation: 0,
                position: [0., 0., 0.],
                alive: false,
            },
        ];
        let plan = plan_blast(true, 9, 7, 104, 7, [0.; 3], &lives).unwrap();
        assert_eq!(plan.targets.len(), 1);
        assert_eq!(
            plan.targets[0],
            BlastTarget {
                object_id: 10,
                generation: 2,
                damage: 42_000.,
                damage_type: ABSOLUTE_DAMAGE_TYPE
            }
        );
        assert_eq!((plan.shield_after, plan.reactor_after), (0., 0.));
        let encoded = serde_json::to_vec(&plan).unwrap();
        let replay: BlastPlan = serde_json::from_slice(&encoded).unwrap();
        assert_eq!(replay, plan);
        let duplicates = [lives[0], lives[0]];
        assert!(plan_blast(true, 9, 7, 104, 7, [0.; 3], &duplicates).is_none());
        assert!(plan_blast(true, 9, 7, 104, 7, [f32::NAN, 0., 0.], &lives).is_none());
    }
}
