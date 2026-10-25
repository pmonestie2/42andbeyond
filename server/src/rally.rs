//! Terran Enforcer Rally rules. The archived ARally handler is missing; the
//! manual establishes group Critical Targeting and Net-7 describes the later
//! bonus families, ten-minute duration and rank-dependent range. Numerical
//! bonuses, reactor cost, charge and cooldown below are local balance tuning.
use crate::items::Modifier;
use serde::{Deserialize, Serialize};

pub const DURATION_MS: u64 = 600_000;
pub const CHARGE_SECONDS: f32 = 2.;
pub const COOLDOWN_SECONDS: f32 = 30.;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Tactic {
    Damage,
    Defense,
    Firing,
    Stealth,
}

impl Tactic {
    /// Returns:
    ///     &str: Legacy client buff icon token, separate from status prose.
    pub fn buff_type(self) -> &'static str {
        // Net-7's archived AUX buff example uses `Damage_Tactics`; the other
        // tier tokens follow that same client-facing naming convention.
        match self {
            Self::Damage => "Damage_Tactics",
            Self::Defense => "Defense_Tactics",
            Self::Firing => "Firing_Tactics",
            Self::Stealth => "Stealth_Tactics",
        }
    }

    /// Returns:
    ///     &str: Native status icon label for this Rally tier.
    pub fn label(self) -> &'static str {
        match self {
            Self::Damage => "Damage Tactics",
            Self::Defense => "Defense Tactics",
            Self::Firing => "Firing Tactics",
            Self::Stealth => "Stealth Tactics",
        }
    }

    /// Returns:
    ///     u32: Minimum trained Rally rank for this tactic.
    pub fn rank(self) -> u32 {
        match self {
            Self::Damage => 1,
            Self::Defense => 3,
            Self::Firing => 5,
            Self::Stealth => 7,
        }
    }

    /// Returns:
    ///     f32: Local reactor cost for this tier.
    pub fn energy(self) -> f32 {
        match self {
            Self::Damage => 75.,
            Self::Defense => 150.,
            Self::Firing => 250.,
            Self::Stealth => 400.,
        }
    }
}

/// Returns:
///     Option<Tactic>: Published ability ID's Rally tier.
pub fn tactic(id: u32) -> Option<Tactic> {
    match id {
        34 => Some(Tactic::Damage),
        35 => Some(Tactic::Defense),
        46 => Some(Tactic::Firing),
        116 => Some(Tactic::Stealth),
        _ => None,
    }
}

/// Returns:
///     f32: Group range in metres at this trained rank.
pub fn range(rank: u32) -> f32 {
    5000. + 250. * rank.saturating_sub(1).min(6) as f32
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RallyBuff {
    pub revision: u64,
    pub source: u32,
    pub target_epoch: u64,
    pub tactic: Tactic,
    pub skill_rank: u32,
    pub expires_at_ms: u64,
}

impl RallyBuff {
    /// Returns:
    ///     bool: Whether the saved Rally status is bounded and still active.
    pub fn valid(&self, now_ms: u64, epoch: u64) -> bool {
        self.revision > 0
            && self.source != 0
            && self.target_epoch == epoch
            && (self.tactic.rank()..=7).contains(&self.skill_rank)
            && self.expires_at_ms > now_ms
            && self.expires_at_ms <= now_ms.saturating_add(DURATION_MS)
    }

    /// Returns:
    ///     Vec<Modifier>: Authoritative weapon, resistance and signature bonuses.
    pub fn modifiers(&self) -> Vec<Modifier> {
        // Public skill documentation says intermediate training improves the
        // same bonuses; these bounded slopes are local tuning, not archive data.
        let mut result = vec![Modifier {
            stat: "STAT_CRITICAL_RATE".into(),
            operation: 1,
            value: 5. + self.skill_rank as f32 * 5.,
        }];
        if self.tactic.rank() >= 3 {
            for stat in [
                "STAT_IMPACT_DEFLECT",
                "STAT_EXPLOSIVE_DEFLECT",
                "STAT_PLASMA_DEFLECT",
                "STAT_ENERGY_DEFLECT",
                "STAT_EMP_DEFLECT",
                "STAT_CHEM_DEFLECT",
                "STAT_PSIONIC_DEFLECT",
            ] {
                result.push(Modifier {
                    stat: stat.into(),
                    operation: 1,
                    value: 7. + self.skill_rank as f32,
                });
            }
        }
        if self.tactic.rank() >= 5 {
            for stat in ["STAT_BEAM_ACCURACY", "STAT_PROJECTILES_ACCURACY"] {
                result.push(Modifier {
                    stat: stat.into(),
                    operation: 1,
                    value: 5. + self.skill_rank as f32,
                });
            }
        }
        if self.tactic.rank() >= 7 {
            result.push(Modifier {
                stat: "STAT_SIGNATURE".into(),
                operation: 4,
                value: 25.,
            });
        }
        result
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingRally {
    pub effect: u32,
    pub tactic: Tactic,
    pub skill_rank: u32,
}

impl PendingRally {
    /// Returns:
    ///     bool: Whether an interrupted or resumed cast can be settled.
    pub fn valid(&self) -> bool {
        self.effect != 0 && (self.tactic.rank()..=7).contains(&self.skill_rank)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn four_tiers_add_only_their_authored_bonus_families() {
        for (id, minimum, count) in [(34, 1, 1), (35, 3, 8), (46, 5, 10), (116, 7, 11)] {
            let tactic = tactic(id).unwrap();
            assert_eq!(tactic.rank(), minimum);
            let buff = RallyBuff {
                revision: 1,
                source: 0x40000001,
                target_epoch: 2,
                tactic,
                skill_rank: minimum,
                expires_at_ms: 1_000_001,
            };
            assert!(buff.valid(400_001, 2));
            assert_eq!(buff.modifiers().len(), count);
            assert!(!buff.valid(1_000_001, 2));
            assert!(!buff.valid(400_001, 3));
        }
        assert_eq!(range(1), 5000.);
        assert_eq!(range(7), 6500.);
        let trained = RallyBuff {
            revision: 1,
            source: 0x40000001,
            target_epoch: 2,
            tactic: Tactic::Damage,
            skill_rank: 2,
            expires_at_ms: 1_000_001,
        };
        assert_eq!(trained.modifiers()[0].value, 15.);
        assert_eq!(Tactic::Damage.buff_type(), "Damage_Tactics");
        assert_eq!(Tactic::Stealth.buff_type(), "Stealth_Tactics");
    }
}
