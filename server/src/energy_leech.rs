//! Jenquai Defender Energy Leech target and resource rules.
//!
//! PlayerClass.cpp verifies IDs 38/39/92/40/93. Net-7 documents target bands,
//! range and group behavior. The pinned executable handler is absent, so the
//! drain and conversion percentages below are explicit local server tuning.
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

pub const SKILL_ID: u32 = 19;
pub const DRAIN_START: f32 = 0.15;
pub const DRAIN_PER_RANK: f32 = 0.05;
pub const CONVERSION: f32 = 0.5;
pub const MAX_TARGETS: usize = 256;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct EnergyLeechTier {
    pub rank: u32,
    pub trained_rank: u32,
    pub range: f32,
    pub radius: f32,
    pub transfers_energy: bool,
    pub shares_group: bool,
}

/// Returns:
///     Option<EnergyLeechTier>: A trained Defender's native ability band.
pub fn tier(ability: u32, trained_rank: u32, class_code: &str) -> Option<EnergyLeechTier> {
    if class_code != "jd" || trained_rank > 7 {
        return None;
    }
    let (rank, radius, transfers_energy, shares_group) = match ability {
        38 => (1, 0., false, false),
        39 => (3, 0., true, false),
        92 => (5, 0., true, true),
        40 => (6, 1_500., true, false),
        93 => (7, 3_000., true, true),
        _ => return None,
    };
    (trained_rank >= rank).then_some(EnergyLeechTier {
        rank,
        trained_rank,
        range: 1_000. + 250. * trained_rank.saturating_sub(1) as f32,
        radius,
        transfers_energy,
        shares_group,
    })
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct PendingEnergyLeech {
    pub target: u32,
    pub target_generation: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
}

impl PendingEnergyLeech {
    /// Returns:
    ///     bool: Whether a saved cast names a trained tier and bounded MOB life.
    pub fn valid(&self) -> bool {
        (0x1000_0001..0x1000_0101).contains(&self.target)
            && self.target_generation < 1_000_000_000
            && self.effect > 0
            && tier(self.ability, self.trained_rank, "jd").is_some()
    }
}

/// Returns:
///     f32: Reactor power drained from an authoritative MOB balance.
pub fn drained_energy(current: f32, capacity: f32, trained_rank: u32) -> f32 {
    if !current.is_finite()
        || !capacity.is_finite()
        || current < 0.
        || capacity <= 0.
        || trained_rank == 0
        || trained_rank > 7
    {
        return 0.;
    }
    let fraction = DRAIN_START + DRAIN_PER_RANK * (trained_rank - 1) as f32;
    current.min(capacity * fraction)
}

/// Returns:
///     f32: Conserved reactor power available for caster or group transfer.
pub fn converted_energy(total_drained: f32, tier: EnergyLeechTier) -> f32 {
    if !tier.transfers_energy || !total_drained.is_finite() || total_drained < 0. {
        0.
    } else {
        total_drained * CONVERSION
    }
}

#[derive(Clone, Copy, Debug)]
pub struct LeechCandidate {
    pub object_id: u32,
    pub generation: u64,
    pub position: [f32; 3],
    pub alive: bool,
    pub hostile: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct LeechTarget {
    pub object_id: u32,
    pub generation: u64,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct LeechPlan {
    pub sector: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub targets: Vec<LeechTarget>,
}

#[derive(Clone, Copy, Debug)]
pub struct LeechSource {
    pub sector: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub origin: [f32; 3],
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct LeechDrain {
    pub target: LeechTarget,
    pub amount: f32,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct LeechReceipt {
    pub sector: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub drains: Vec<LeechDrain>,
    pub recipients: Vec<u32>,
}

impl LeechReceipt {
    /// Returns:
    ///     bool: Whether a saved sector drain and friendly award are bounded.
    pub fn valid(&self) -> bool {
        let Some(band) = tier(self.ability, self.trained_rank, "jd") else {
            return false;
        };
        let unique_targets = self
            .drains
            .iter()
            .map(|drain| drain.target.object_id)
            .collect::<BTreeSet<_>>()
            .len()
            == self.drains.len();
        let unique_recipients = self
            .recipients
            .iter()
            .copied()
            .collect::<BTreeSet<_>>()
            .len()
            == self.recipients.len();
        self.sector != 0
            && (0x4000_0001..=0x4000_ffff).contains(&self.source)
            && self.source_epoch > 0
            && self.effect > 0
            && !self.drains.is_empty()
            && self.drains.len() <= MAX_TARGETS
            && unique_targets
            && self.drains.iter().all(|drain| {
                (0x1000_0001..0x1000_0101).contains(&drain.target.object_id)
                    && drain.target.generation < 1_000_000_000
                    && drain.amount.is_finite()
                    && (0. ..=MAX_CAPACITY_PER_MOB).contains(&drain.amount)
            })
            && self.recipients.len() <= 6
            && unique_recipients
            && self
                .recipients
                .iter()
                .all(|recipient| (0x4000_0001..=0x4000_ffff).contains(recipient))
            && if band.transfers_energy {
                self.recipients.contains(&self.source)
                    && (band.shares_group || self.recipients.len() == 1)
            } else {
                self.recipients.is_empty()
            }
    }

    /// Returns:
    ///     f32: Power conserved across all frozen MOB drains.
    pub fn total_drained(&self) -> f32 {
        self.drains.iter().map(|drain| drain.amount).sum()
    }
}

pub const MAX_CAPACITY_PER_MOB: f32 = 10_000_000.;

/// Returns:
///     Option<LeechPlan>: Pinned hostile lives inside the selected area.
pub fn plan(
    tier: EnergyLeechTier,
    caster: LeechSource,
    selected: u32,
    candidates: &[LeechCandidate],
) -> Option<LeechPlan> {
    if caster.sector == 0
        || caster.source == 0
        || caster.source_epoch == 0
        || caster.effect == 0
        || candidates.len() > MAX_TARGETS
        || !caster.origin.into_iter().all(f32::is_finite)
    {
        return None;
    }
    let primary = candidates
        .iter()
        .find(|candidate| candidate.object_id == selected)?;
    if !primary.alive
        || !primary.hostile
        || !primary.position.into_iter().all(f32::is_finite)
        || distance(caster.origin, primary.position)? > tier.range
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
            && candidate.hostile
            && (candidate.object_id == selected
                || tier.radius > 0.
                    && distance(primary.position, candidate.position)? <= tier.radius)
        {
            targets.push(LeechTarget {
                object_id: candidate.object_id,
                generation: candidate.generation,
            });
        }
    }
    Some(LeechPlan {
        sector: caster.sector,
        source: caster.source,
        source_epoch: caster.source_epoch,
        effect: caster.effect,
        ability: match tier.rank {
            1 => 38,
            3 => 39,
            5 => 92,
            6 => 40,
            7 => 93,
            _ => return None,
        },
        targets,
    })
}

/// Returns:
///     Option<f32>: Distance between finite ship and MOB positions.
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

    /// Native tier access includes rank-six area and rank-seven group transfer.
    #[test]
    fn defender_tiers_and_local_drain_are_bounded() {
        assert_eq!(tier(38, 1, "jd").unwrap().range, 1_000.);
        assert_eq!(tier(39, 4, "jd").unwrap().range, 1_750.);
        assert_eq!(tier(40, 6, "jd").unwrap().radius, 1_500.);
        assert!(tier(93, 7, "jd").unwrap().shares_group);
        assert!(tier(93, 7, "je").is_none());
        assert!(tier(93, 6, "jd").is_none());
        assert!((drained_energy(100., 200., 1) - 30.).abs() < 0.001);
        assert!((drained_energy(100., 200., 7) - 90.).abs() < 0.001);
        assert_eq!(drained_energy(20., 200., 7), 20.);
        assert_eq!(drained_energy(f32::NAN, 200., 7), 0.);
        assert_eq!(converted_energy(100., tier(38, 1, "jd").unwrap()), 0.);
        assert_eq!(converted_energy(100., tier(93, 7, "jd").unwrap()), 50.);
    }

    /// Only selected, alive hostile MOB generations enter the area plan.
    #[test]
    fn target_plan_is_generation_pinned_and_duplicate_free() {
        let candidates = [
            LeechCandidate {
                object_id: 10,
                generation: 2,
                position: [2_000., 0., 0.],
                alive: true,
                hostile: true,
            },
            LeechCandidate {
                object_id: 11,
                generation: 3,
                position: [3_500., 0., 0.],
                alive: true,
                hostile: true,
            },
            LeechCandidate {
                object_id: 12,
                generation: 4,
                position: [3_501., 0., 0.],
                alive: true,
                hostile: true,
            },
            LeechCandidate {
                object_id: 13,
                generation: 5,
                position: [2_000., 0., 0.],
                alive: true,
                hostile: false,
            },
        ];
        let band = tier(40, 6, "jd").unwrap();
        let caster = LeechSource {
            sector: 1076,
            source: 1,
            source_epoch: 9,
            effect: 20,
            origin: [0.; 3],
        };
        let result = plan(band, caster, 10, &candidates).unwrap();
        assert_eq!(
            result.targets,
            vec![
                LeechTarget {
                    object_id: 10,
                    generation: 2
                },
                LeechTarget {
                    object_id: 11,
                    generation: 3
                }
            ]
        );
        assert_eq!(result.ability, 40);
        let encoded = serde_json::to_vec(&result).unwrap();
        assert_eq!(
            serde_json::from_slice::<LeechPlan>(&encoded).unwrap(),
            result
        );
        assert!(plan(band, caster, 13, &candidates).is_none());
        assert!(plan(band, caster, 10, &[candidates[0], candidates[0]]).is_none());
    }

    #[test]
    fn receipt_freezes_energy_and_group_without_duplicate_awards() {
        let mut receipt = LeechReceipt {
            sector: 1076,
            source: 0x4000_0001,
            source_epoch: 4,
            effect: 8,
            ability: 93,
            trained_rank: 7,
            drains: vec![LeechDrain {
                target: LeechTarget {
                    object_id: 0x1000_0001,
                    generation: 2,
                },
                amount: 90.,
            }],
            recipients: vec![0x4000_0001, 0x4000_0002],
        };
        assert!(receipt.valid());
        assert_eq!(receipt.total_drained(), 90.);
        receipt.recipients.push(0x4000_0002);
        assert!(!receipt.valid());
        receipt.recipients.pop();
        receipt.drains[0].amount = f32::NAN;
        assert!(!receipt.valid());
    }
}
