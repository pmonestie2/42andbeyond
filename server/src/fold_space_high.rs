//! The two Jenquai Defender Fold Space travel tiers missing from runtime.
//!
//! Native IDs come from PlayerSkills.h/PlayerClass.cpp. Public travel distances
//! and rank-seven group radius come from:
//! https://net7wiki.bmsite.net/index.php?title=Fold_Space
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

pub const SKILL_ID: u32 = 24;
pub const GROUP_RADIUS: f32 = 6_000.;
pub const MAX_GROUP_RECIPIENTS: usize = 6;
pub const RECEIPT_LIMIT: usize = 32;
pub const MAX_DISTANCE_BONUS: f32 = 1_000.;
pub const CHARGE_SECONDS: f32 = 3.;
pub const COOLDOWN_SECONDS: f32 = 5.;

/// Returns:
///     Option<f32>: Locally tuned reactor cost for a high Fold tier.
pub fn energy_cost(ability: u32) -> Option<f32> {
    match ability {
        36 => Some(180.),
        17 => Some(300.),
        _ => None,
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct PendingHighFold {
    pub ability: u32,
    pub trained_rank: u32,
    pub source: u32,
    pub source_epoch: u64,
    pub sector: u32,
    pub effect: u32,
    pub target: u32,
    #[serde(default)]
    pub distance_bonus: f32,
    pub plan: Option<FoldTravelPlan>,
}

impl PendingHighFold {
    /// Returns:
    ///     bool: Whether a saved, possibly frozen cast is structurally valid.
    pub fn valid(&self) -> bool {
        self.source != 0
            && self.source_epoch != 0
            && self.sector != 0
            && self.effect != 0
            && self.target != 0
            && self.distance_bonus.is_finite()
            && (0. ..=MAX_DISTANCE_BONUS).contains(&self.distance_bonus)
            && tier(self.ability, self.trained_rank, "jd").is_some()
            && self.plan.as_ref().is_none_or(|plan| {
                plan.valid()
                    && plan.caster == self.source
                    && plan.caster_epoch == self.source_epoch
                    && plan.sector == self.sector
                    && plan.effect == self.effect
                    && plan.ability == self.ability
            })
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct FoldReceipt {
    pub caster: u32,
    pub caster_epoch: u64,
    pub effect: u32,
    pub destination: [f32; 3],
}

impl FoldReceipt {
    /// Returns:
    ///     bool: Whether the persisted recipient receipt is bounded.
    pub fn valid(&self) -> bool {
        self.caster != 0
            && self.caster_epoch != 0
            && self.effect != 0
            && self.destination.into_iter().all(f32::is_finite)
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct MobFoldReceipt {
    pub caster: u32,
    pub caster_epoch: u64,
    pub effect: u32,
    pub generation: u64,
    pub destination: [f32; 3],
}

impl MobFoldReceipt {
    /// Returns:
    ///     bool: Whether a MOB movement receipt belongs to a valid life.
    pub fn valid(&self, generation: u64) -> bool {
        self.caster != 0
            && self.caster_epoch != 0
            && self.effect != 0
            && self.generation == generation
            && self.destination.into_iter().all(f32::is_finite)
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HighFoldTier {
    pub rank: u32,
    pub travel_distance: f32,
    pub group: bool,
}

/// Returns:
///     Option<HighFoldTier>: Trained Defender-only native travel tier.
pub fn tier(ability: u32, trained_rank: u32, class_code: &str) -> Option<HighFoldTier> {
    if class_code != "jd" || trained_rank > 7 {
        return None;
    }
    match ability {
        36 if trained_rank >= 6 => Some(HighFoldTier {
            rank: 6,
            travel_distance: 6_500.,
            group: false,
        }),
        17 if trained_rank >= 7 => Some(HighFoldTier {
            rank: 7,
            travel_distance: 6_800.,
            group: true,
        }),
        _ => None,
    }
}

/// Returns:
///     HighFoldTier: Travel distance with a bounded equipped modifier.
pub fn with_distance_bonus(mut band: HighFoldTier, bonus: f32) -> HighFoldTier {
    let bonus = if bonus.is_finite() {
        bonus.clamp(0., MAX_DISTANCE_BONUS)
    } else {
        0.
    };
    band.travel_distance += bonus;
    band
}

#[derive(Clone, Copy, Debug)]
pub struct FoldRecipient {
    pub player_id: u32,
    pub session_epoch: u64,
    pub sector: u32,
    pub position: [f32; 3],
    pub alive: bool,
    pub group_member: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct FoldDestination {
    pub player_id: u32,
    pub session_epoch: u64,
    pub destination: [f32; 3],
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct FoldTravelPlan {
    pub caster: u32,
    pub caster_epoch: u64,
    pub sector: u32,
    pub effect: u32,
    pub ability: u32,
    pub recipients: Vec<FoldDestination>,
}

#[derive(Clone, Copy, Debug)]
pub struct FoldCast {
    pub caster: u32,
    pub caster_epoch: u64,
    pub sector: u32,
    pub effect: u32,
    pub bounds: ([f32; 3], [f32; 3]),
}

/// Returns:
///     Option<FoldTravelPlan>: One epoch-pinned recipient moved along its own facing.
pub fn directional(
    band: HighFoldTier,
    cast: FoldCast,
    recipient: FoldRecipient,
    recipient_facing: [f32; 3],
) -> Option<FoldTravelPlan> {
    if band.rank != 6 || band.group || !valid_subject(recipient, cast.sector) {
        return None;
    }
    let destination = advance(
        recipient.position,
        recipient_facing,
        band.travel_distance,
        cast.bounds,
    )?;
    Some(FoldTravelPlan {
        caster: cast.caster,
        caster_epoch: cast.caster_epoch,
        sector: cast.sector,
        effect: cast.effect,
        ability: 36,
        recipients: vec![FoldDestination {
            player_id: recipient.player_id,
            session_epoch: recipient.session_epoch,
            destination,
        }],
    })
    .filter(FoldTravelPlan::valid)
}

/// Returns:
///     Option<[f32; 3]>: Bounded MOB destination along its current facing.
pub fn directional_mob(
    band: HighFoldTier,
    position: [f32; 3],
    facing: [f32; 3],
    bounds: ([f32; 3], [f32; 3]),
) -> Option<[f32; 3]> {
    (band.rank == 6 && !band.group)
        .then(|| advance(position, facing, band.travel_distance, bounds))
        .flatten()
}

/// Returns:
///     Option<FoldTravelPlan>: Frozen same-sector group members within 6 km.
pub fn area(
    band: HighFoldTier,
    cast: FoldCast,
    caster_position: [f32; 3],
    caster_facing: [f32; 3],
    candidates: &[FoldRecipient],
) -> Option<FoldTravelPlan> {
    if band.rank != 7
        || !band.group
        || candidates.is_empty()
        || candidates.len() > MAX_GROUP_RECIPIENTS
        || !caster_position.into_iter().all(f32::is_finite)
    {
        return None;
    }
    let mut identities = BTreeSet::new();
    let mut recipients = Vec::new();
    for candidate in candidates {
        if !identities.insert(candidate.player_id) || !valid_subject(*candidate, cast.sector) {
            return None;
        }
        if candidate.player_id != cast.caster && !candidate.group_member {
            continue;
        }
        if distance(caster_position, candidate.position)? > GROUP_RADIUS {
            continue;
        }
        recipients.push(FoldDestination {
            player_id: candidate.player_id,
            session_epoch: candidate.session_epoch,
            destination: advance(
                candidate.position,
                caster_facing,
                band.travel_distance,
                cast.bounds,
            )?,
        });
    }
    let plan = FoldTravelPlan {
        caster: cast.caster,
        caster_epoch: cast.caster_epoch,
        sector: cast.sector,
        effect: cast.effect,
        ability: 17,
        recipients,
    };
    plan.valid().then_some(plan)
}

impl FoldTravelPlan {
    /// Returns:
    ///     bool: Whether this frozen cross-session plan has valid identities.
    pub fn valid(&self) -> bool {
        if self.caster == 0
            || self.caster_epoch == 0
            || self.sector == 0
            || self.effect == 0
            || self.recipients.is_empty()
            || self.recipients.len() > MAX_GROUP_RECIPIENTS
            || self.recipients.iter().any(|entry| {
                entry.player_id == 0
                    || entry.session_epoch == 0
                    || !entry.destination.into_iter().all(f32::is_finite)
            })
        {
            return false;
        }
        let unique = self
            .recipients
            .iter()
            .map(|entry| entry.player_id)
            .collect::<BTreeSet<_>>()
            .len()
            == self.recipients.len();
        unique
            && match self.ability {
                36 => self.recipients.len() == 1,
                17 => self
                    .recipients
                    .iter()
                    .any(|entry| entry.player_id == self.caster),
                _ => false,
            }
    }
}

/// Returns:
///     bool: Whether this recipient can safely join the frozen sector plan.
fn valid_subject(recipient: FoldRecipient, sector: u32) -> bool {
    recipient.player_id != 0
        && recipient.session_epoch != 0
        && recipient.sector == sector
        && recipient.alive
        && recipient.position.into_iter().all(f32::is_finite)
}

/// Returns:
///     Option<[f32; 3]>: A bounded destination along normalized facing.
fn advance(
    start: [f32; 3],
    facing: [f32; 3],
    distance: f32,
    bounds: ([f32; 3], [f32; 3]),
) -> Option<[f32; 3]> {
    if !start.into_iter().chain(facing).all(f32::is_finite)
        || !distance.is_finite()
        || distance <= 0.
        || !bounds.0.into_iter().chain(bounds.1).all(f32::is_finite)
    {
        return None;
    }
    let length = facing.iter().map(|value| value * value).sum::<f32>().sqrt();
    if !length.is_finite() || length <= 0. {
        return None;
    }
    let mut destination = [0.; 3];
    for axis in 0..3 {
        if bounds.0[axis] > bounds.1[axis] {
            return None;
        }
        destination[axis] =
            (start[axis] + facing[axis] / length * distance).clamp(bounds.0[axis], bounds.1[axis]);
    }
    Some(destination)
}

/// Returns:
///     Option<f32>: Finite center distance between two authoritative poses.
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

    fn member(id: u32, x: f32) -> FoldRecipient {
        FoldRecipient {
            player_id: id,
            session_epoch: u64::from(id),
            sector: 1076,
            position: [x, 0., 0.],
            alive: true,
            group_member: true,
        }
    }

    /// JD alone gains the source-backed 6.5/6.8 km bands.
    #[test]
    fn native_high_rank_gates() {
        assert_eq!(tier(36, 6, "jd").unwrap().travel_distance, 6500.);
        assert_eq!(tier(17, 7, "jd").unwrap().travel_distance, 6800.);
        assert!(tier(36, 5, "jd").is_none());
        assert!(tier(17, 6, "jd").is_none());
        assert!(tier(17, 7, "je").is_none());
        assert_eq!(
            with_distance_bonus(tier(17, 7, "jd").unwrap(), 983.).travel_distance,
            7_783.
        );
        assert_eq!(
            with_distance_bonus(tier(36, 6, "jd").unwrap(), f32::INFINITY).travel_distance,
            6_500.
        );
    }

    /// Directional move follows the recipient facing and respects world bounds.
    #[test]
    fn directional_destination_is_frozen() {
        let cast = FoldCast {
            caster: 1,
            caster_epoch: 1,
            sector: 1076,
            effect: 9,
            bounds: ([-10_000.; 3], [10_000.; 3]),
        };
        let plan = directional(
            tier(36, 6, "jd").unwrap(),
            cast,
            member(2, 500.),
            [0., 2., 0.],
        )
        .unwrap();
        assert_eq!(plan.recipients[0].destination, [500., 6500., 0.]);
        assert!(directional(tier(36, 6, "jd").unwrap(), cast, member(2, 500.), [0.; 3],).is_none());
        let encoded = serde_json::to_vec(&plan).unwrap();
        assert_eq!(
            serde_json::from_slice::<FoldTravelPlan>(&encoded).unwrap(),
            plan
        );
    }

    /// Area fold includes group pilots at exactly 6 km and omits outsiders.
    #[test]
    fn area_group_radius_and_epoch_replay() {
        let cast = FoldCast {
            caster: 1,
            caster_epoch: 1,
            sector: 1076,
            effect: 9,
            bounds: ([-20_000.; 3], [20_000.; 3]),
        };
        let mut outsider = member(4, 100.);
        outsider.group_member = false;
        let members = [member(1, 0.), member(2, 6000.), member(3, 6001.), outsider];
        let plan = area(
            tier(17, 7, "jd").unwrap(),
            cast,
            [0.; 3],
            [1., 0., 0.],
            &members,
        )
        .unwrap();
        assert_eq!(plan.recipients.len(), 2);
        assert_eq!(plan.recipients[0].destination, [6800., 0., 0.]);
        assert_eq!(plan.recipients[1].destination, [12_800., 0., 0.]);
        let duplicate = [member(1, 0.), member(1, 0.)];
        assert!(area(
            tier(17, 7, "jd").unwrap(),
            cast,
            [0.; 3],
            [1., 0., 0.],
            &duplicate,
        )
        .is_none());
    }
}
