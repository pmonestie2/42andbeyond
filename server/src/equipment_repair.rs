//! Public Repair Equipment tiers and durable per-second repair intent.
//!
//! Archived PlayerClass.cpp lists the five native ability IDs and intended
//! EffectID 178, but the executable ARepairEquipment handler is absent. The
//! original Player Guide establishes toggle, targeted equipment and reactor
//! drain. Net-7's Repair Equipment page establishes tier multipliers, area
//! radii and rank-specific range. The 1% structure/5 energy base tick below is
//! local server tuning, not a recovered original balance value.
use serde::{Deserialize, Serialize};

pub const BASE_STRUCTURE_PER_TICK: f32 = 0.01;
pub const ENERGY_PER_TICK: f32 = 5.;
pub const BUFF_SLOT: usize = 10;

/// Returns:
///     Option<(u32, f32, f32, f32)>: Tier rank, target range, area radius, and primary multiplier.
pub fn tier(ability: u32, trained_rank: u32) -> Option<(u32, f32, f32, f32)> {
    let (rank, radius, multiplier) = match ability {
        90 => (1, 0., 1.),
        94 => (3, 0., 3.),
        30 => (5, 0., 15.),
        8 => (6, 1500., 30.),
        62 => (7, 3000., 60.),
        _ => return None,
    };
    if trained_rank < rank {
        return None;
    }
    let range = if rank == 1 {
        0.
    } else {
        3000. + (trained_rank.saturating_sub(3).min(4) as f32) * 250.
    };
    Some((rank, range, radius, multiplier))
}

/// Returns:
///     f32: Structure fraction repaired per tick for primary or area recipient.
pub fn structure_per_tick(rank: u32, primary: bool) -> f32 {
    let multiplier = match rank {
        1 => 1.,
        3 => 3.,
        5 => 15.,
        6 => {
            if primary {
                30.
            } else {
                6.
            }
        }
        7 => {
            if primary {
                60.
            } else {
                20.
            }
        }
        _ => 0.,
    };
    BASE_STRUCTURE_PER_TICK * multiplier
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ActiveEquipmentRepair {
    pub ability: u32,
    pub target: u32,
    pub rank: u32,
    pub effect: u32,
    pub next_due_ms: u64,
}

impl ActiveEquipmentRepair {
    /// Returns:
    ///     bool: Whether this saved toggle still describes a catalog tier.
    pub fn valid(&self, trained_rank: u32) -> bool {
        self.target > 0
            && self.effect > 0
            && self.next_due_ms > 0
            && tier(self.ability, trained_rank).is_some_and(|(rank, _, _, _)| rank == self.rank)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingEquipmentRepair {
    pub sequence: u32,
    pub target: u32,
    pub rank: u32,
    pub range: f32,
    pub radius: f32,
}

impl PendingEquipmentRepair {
    /// Returns:
    ///     bool: Whether a frozen tick can be safely replayed.
    pub fn valid(&self) -> bool {
        self.sequence > 0
            && self.target > 0
            && matches!(self.rank, 1 | 3 | 5 | 6 | 7)
            && self.range.is_finite()
            && (0. ..=4000.).contains(&self.range)
            && self.radius.is_finite()
            && (0. ..=3000.).contains(&self.radius)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_tiers_keep_primary_and_area_multipliers() {
        assert_eq!(tier(90, 1), Some((1, 0., 0., 1.)));
        assert_eq!(tier(94, 3), Some((3, 3000., 0., 3.)));
        assert_eq!(tier(30, 5), Some((5, 3500., 0., 15.)));
        assert_eq!(tier(8, 6), Some((6, 3750., 1500., 30.)));
        assert_eq!(tier(62, 7), Some((7, 4000., 3000., 60.)));
        assert_eq!(tier(62, 6), None);
        assert!((structure_per_tick(7, false) - 0.2).abs() < 0.0001);
    }
}
