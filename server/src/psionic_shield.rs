//! Jenquai Defender Psionic Shield tiers and durable absorption balance.
//!
//! Official manual: barrier absorbs damage before dissipating.
//! https://downloads.cncfps.com/Westwood/earthandbeyond/manual/EBMANUAL.PDF
//! Net-7 gives five absorption multipliers, +30 psionic resistance and range:
//! https://net7wiki.bmsite.net/index.php?title=Psionic_Shield
//! Archived PlayerClass.cpp maps the five ability IDs and names effect 214,
//! but APsionicShield implementation is absent. Timing/energy are local tuning.
use serde::{Deserialize, Serialize};

pub const SKILL_ID: u32 = 42;
pub const BUFF_SLOT: usize = 14;
pub const VISUAL_DESCRIPTOR: u16 = 214;
pub const DURATION_MS: u64 = 300_000;
pub const CHARGE_SECONDS: f32 = 2.;
pub const COOLDOWN_SECONDS: f32 = 30.;
pub const PSIONIC_DEFLECT_BONUS: f32 = 30.;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PsionicTier {
    pub rank: u32,
    pub range: f32,
    pub energy: f32,
    pub absorb_per_skill: f32,
}

impl PsionicTier {
    /// Returns:
    ///     f32: Native absorption budget at this trained skill rank.
    pub fn capacity(self, trained: u32) -> f32 {
        self.absorb_per_skill * trained as f32
    }
}

/// Returns:
///     Option<PsionicTier>: Trained native tier and selected-player range.
pub fn tier(ability: u32, trained: u32) -> Option<PsionicTier> {
    let (rank, absorb_per_skill, energy) = match ability {
        79 => (1, 30., 50.),
        69 => (3, 90., 100.),
        81 => (5, 360., 150.),
        49 => (6, 720., 225.),
        80 => (7, 1440., 300.),
        _ => return None,
    };
    if trained < rank || trained > 7 {
        return None;
    }
    Some(PsionicTier {
        rank,
        range: 2500. + 250. * (trained - 1) as f32,
        energy,
        absorb_per_skill,
    })
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PsionicShieldBuff {
    pub revision: u64,
    pub source: u32,
    pub target_epoch: u64,
    pub ability: u32,
    pub trained_rank: u32,
    pub effect: u32,
    pub expires_at_ms: u64,
    pub remaining: f32,
}

impl PsionicShieldBuff {
    /// Returns:
    ///     bool: Whether this receipt is a bounded, unspent active shield.
    pub fn valid(&self, now_ms: u64, epoch: u64) -> bool {
        let Some(tier) = tier(self.ability, self.trained_rank) else {
            return false;
        };
        self.revision > 0
            && self.source > 0
            && self.target_epoch == epoch
            && self.effect > 0
            && self.expires_at_ms > now_ms
            && self.expires_at_ms <= now_ms.saturating_add(DURATION_MS)
            && self.remaining.is_finite()
            && self.remaining > 0.
            && self.remaining <= tier.capacity(self.trained_rank)
    }

    /// Returns:
    ///     Absorption: Frozen post-hit budget and damage passed to ship armor.
    pub fn absorb(&self, incoming: f32) -> Option<Absorption> {
        if !incoming.is_finite() || incoming < 0. || !self.remaining.is_finite() {
            return None;
        }
        let absorbed = incoming.min(self.remaining);
        Some(Absorption {
            absorbed,
            passed: incoming - absorbed,
            remaining: self.remaining - absorbed,
        })
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Absorption {
    pub absorbed: f32,
    pub passed: f32,
    pub remaining: f32,
}

#[derive(Clone, Debug)]
pub struct PsionicHitPlan {
    pub passed: f32,
    pub absorbed: f32,
    pub remaining_buff: Option<PsionicShieldBuff>,
}

/// Returns:
///     Option<PsionicHitPlan>: Damage after a valid active barrier and its frozen balance.
pub fn intercept(
    buff: Option<&PsionicShieldBuff>,
    incoming: f32,
    now_ms: u64,
    recipient_epoch: u64,
) -> Option<PsionicHitPlan> {
    if !incoming.is_finite() || incoming < 0. {
        return None;
    }
    let active = buff.filter(|buff| buff.valid(now_ms, recipient_epoch));
    let Some(active) = active else {
        return Some(PsionicHitPlan {
            passed: incoming,
            absorbed: 0.,
            remaining_buff: None,
        });
    };
    let absorption = active.absorb(incoming)?;
    let remaining_buff = (absorption.remaining > 0.).then(|| {
        let mut next = active.clone();
        next.remaining = absorption.remaining;
        next
    });
    Some(PsionicHitPlan {
        passed: absorption.passed,
        absorbed: absorption.absorbed,
        remaining_buff,
    })
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingPsionicShield {
    pub target: u32,
    pub effect: u32,
    pub ability: u32,
    pub trained_rank: u32,
}

impl PendingPsionicShield {
    /// Returns:
    ///     bool: Whether the completed cast can safely settle once.
    pub fn valid(&self) -> bool {
        self.target != 0 && self.effect != 0 && tier(self.ability, self.trained_rank).is_some()
    }
}

/// Returns:
///     bool: Whether an alive selected player is within the rank's cast range.
pub fn target_in_range(tier: PsionicTier, source: [f32; 3], target: [f32; 3], alive: bool) -> bool {
    if !alive || !source.into_iter().chain(target).all(f32::is_finite) {
        return false;
    }
    let distance = source
        .into_iter()
        .zip(target)
        .map(|(left, right)| (left - right).powi(2))
        .sum::<f32>()
        .sqrt();
    distance <= tier.range
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The public rank table and intermediate range upgrades are exact.
    #[test]
    fn native_tiers_and_intermediate_training() {
        assert_eq!(tier(79, 1).unwrap().capacity(1), 30.);
        assert_eq!(tier(79, 2).unwrap().capacity(2), 60.);
        assert_eq!(tier(69, 3).unwrap().capacity(3), 270.);
        assert_eq!(tier(69, 4).unwrap().capacity(4), 360.);
        assert_eq!(tier(81, 5).unwrap().capacity(5), 1800.);
        assert_eq!(tier(49, 6).unwrap().capacity(6), 4320.);
        assert_eq!(tier(80, 7).unwrap().capacity(7), 10_080.);
        assert_eq!(tier(79, 2).unwrap().range, 2750.);
        assert_eq!(tier(80, 7).unwrap().range, 4000.);
        assert_eq!(tier(80, 6), None);
        assert_eq!(tier(79, 0), None);
    }

    /// Damage drains barrier first, preserving any excess for ship defenses.
    #[test]
    fn absorb_partial_break_and_retry_without_double_spend() {
        let buff = PsionicShieldBuff {
            revision: 4,
            source: 0x4000_0001,
            target_epoch: 5,
            ability: 69,
            trained_rank: 3,
            effect: 99,
            expires_at_ms: 300_100,
            remaining: 270.,
        };
        assert!(buff.valid(100, 5));
        assert!(!buff.valid(100, 6));
        let first = buff.absorb(100.).unwrap();
        assert_eq!(
            first,
            Absorption {
                absorbed: 100.,
                passed: 0.,
                remaining: 170.
            }
        );
        assert_eq!(buff.absorb(100.).unwrap(), first); // planning is pure until receipt commits
        let mut spent = buff.clone();
        spent.remaining = first.remaining;
        let second = spent.absorb(200.).unwrap();
        assert_eq!(
            second,
            Absorption {
                absorbed: 170.,
                passed: 30.,
                remaining: 0.
            }
        );
        assert!(spent.absorb(f32::NAN).is_none());
        spent.remaining = 0.;
        assert!(!spent.valid(100, 5));
        let planned = intercept(Some(&buff), 300., 100, 5).unwrap();
        assert_eq!(planned.absorbed, 270.);
        assert_eq!(planned.passed, 30.);
        assert!(planned.remaining_buff.is_none());
        assert_eq!(intercept(Some(&buff), 300., 100, 6).unwrap().passed, 300.);
    }

    /// Dead, distant or nonfinite recipients cannot receive a new barrier.
    #[test]
    fn target_and_cast_receipt_validation() {
        let tier = tier(80, 7).unwrap();
        assert!(target_in_range(tier, [0., 0., 0.], [4000., 0., 0.], true));
        assert!(!target_in_range(tier, [0., 0., 0.], [4001., 0., 0.], true));
        assert!(!target_in_range(tier, [0., 0., 0.], [0., 0., 0.], false));
        assert!(!target_in_range(
            tier,
            [f32::NAN, 0., 0.],
            [0., 0., 0.],
            true
        ));
        assert!(PendingPsionicShield {
            target: 1,
            effect: 2,
            ability: 79,
            trained_rank: 1
        }
        .valid());
        assert!(!PendingPsionicShield {
            target: 1,
            effect: 2,
            ability: 80,
            trained_rank: 1
        }
        .valid());
    }
}
