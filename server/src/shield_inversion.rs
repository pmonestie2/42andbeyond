//! Progen Shield Inversion: shield power becomes timed energy damage.
//! The public Net-7 table supplies drain and conversion by rank; the archived
//! class map supplies IDs and Shield Nova effect 98, without an executable
//! timing handler. Duration and cadence below are local server tuning.
use serde::{Deserialize, Serialize};

pub const DURATION_MS: u64 = 15_000;
pub const MAX_BONUS_SECONDS: f32 = 60.;
pub const PULSE_MS: u64 = 1_000;
pub const CHARGE_SECONDS: f32 = 2.;
pub const COOLDOWN_SECONDS: f32 = 20.;
pub const SHIELD_NOVA_EFFECT: u16 = 98;

/// Returns:
///     u64: Active duration including the strongest equipped duration bonus.
pub fn duration_ms(equipped_bonus_seconds: f32) -> u64 {
    let bonus = if equipped_bonus_seconds.is_finite() {
        equipped_bonus_seconds.clamp(0., MAX_BONUS_SECONDS)
    } else {
        0.
    };
    DURATION_MS + (bonus * 1000.).round() as u64
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct InversionTier {
    pub rank: u32,
    pub range: f32,
    pub radius: f32,
    pub drain_per_second: f32,
    pub damage_efficiency: f32,
}

/// Returns:
///     Option<InversionTier>: Published shield drain and damage conversion.
pub fn tier(ability: u32, trained_rank: u32) -> Option<InversionTier> {
    let (rank, drain_per_level, damage_efficiency, radius) = match ability {
        111 => (1, 3., 0.9, 0.),
        113 => (3, 9., 1., 0.),
        105 => (5, 15., 1.1, 0.),
        107 => (6, 18., 1.2, 0.),
        110 => (7, 21., 1.3, 2500.),
        _ => return None,
    };
    if trained_rank < rank || trained_rank > 7 {
        return None;
    }
    Some(InversionTier {
        rank,
        range: 1_000. + 250. * (trained_rank - 1) as f32,
        radius,
        drain_per_second: drain_per_level * trained_rank as f32,
        damage_efficiency,
    })
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Pulse {
    pub shield_spent: f32,
    pub damage_per_target: f32,
}

/// Returns:
///     Pulse: Actual shield spent and energy damage to each eligible target.
pub fn pulse(tier: InversionTier, shield: f32, milliseconds: u64) -> Pulse {
    if !shield.is_finite() || shield <= 0. || milliseconds == 0 || milliseconds > PULSE_MS {
        return Pulse {
            shield_spent: 0.,
            damage_per_target: 0.,
        };
    }
    let spent = (tier.drain_per_second * milliseconds as f32 / 1000.).min(shield);
    Pulse {
        shield_spent: spent,
        damage_per_target: spent * tier.damage_efficiency,
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
pub struct ActiveShieldInversion {
    pub source: u32,
    pub sector: u32,
    pub ability: u32,
    pub trained_rank: u32,
    pub target: u32,
    pub target_generation: u64,
    pub source_epoch: u64,
    pub effect: u32,
    pub expires_at_ms: u64,
    pub next_pulse_ms: u64,
    pub pulse_index: u32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct InversionReceipt {
    pub source: u32,
    pub sector: u32,
    pub source_epoch: u64,
    pub effect: u32,
    pub pulse_index: u32,
    pub target_generation: u64,
    pub shield_spent: f32,
    pub damage_per_target: f32,
    pub targets: Vec<u32>,
}

impl InversionReceipt {
    /// Returns:
    ///     bool: Whether a saved world pulse has bounded identity and values.
    pub fn valid(&self) -> bool {
        (0x4000_0001..=0x4000_ffff).contains(&self.source)
            && self.sector > 0
            && self.source_epoch > 0
            && self.effect > 0
            && self.pulse_index < duration_ms(MAX_BONUS_SECONDS) as u32 / PULSE_MS as u32
            && self.target_generation < 1_000_000_000
            && self.shield_spent.is_finite()
            && (0. ..=100_000.).contains(&self.shield_spent)
            && self.damage_per_target.is_finite()
            && (0. ..=130_000.).contains(&self.damage_per_target)
            && self.targets.len() <= 256
            && self.targets.iter().all(|target| {
                (crate::sector_combat::MOB_ID..crate::sector_combat::MOB_ID + 256).contains(target)
            })
            && self
                .targets
                .iter()
                .collect::<std::collections::BTreeSet<_>>()
                .len()
                == self.targets.len()
            && (self.shield_spent == 0.) == self.targets.is_empty()
            && (self.damage_per_target == 0.) == self.targets.is_empty()
    }
}

impl ActiveShieldInversion {
    /// Returns:
    ///     bool: Whether a saved inversion has bounded identity and cadence.
    pub fn valid(&self) -> bool {
        tier(self.ability, self.trained_rank).is_some()
            && (0x4000_0001..=0x4000_ffff).contains(&self.source)
            && self.sector > 0
            && (crate::sector_combat::MOB_ID..crate::sector_combat::MOB_ID + 256)
                .contains(&self.target)
            && self.target_generation < 1_000_000_000
            && self.source_epoch > 0
            && self.effect > 0
            && self.expires_at_ms > 0
            && self.next_pulse_ms <= self.expires_at_ms
            && self.pulse_index <= duration_ms(MAX_BONUS_SECONDS) as u32 / PULSE_MS as u32
    }

    /// Returns:
    ///     bool: Whether this saved effect can emit its next one-second pulse.
    pub fn due(&self, now_ms: u64) -> bool {
        self.valid()
            && now_ms >= self.next_pulse_ms
            && now_ms <= self.expires_at_ms
            && self.expires_at_ms <= now_ms.saturating_add(duration_ms(MAX_BONUS_SECONDS))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn published_tiers_keep_ranges_drain_and_conversion() {
        assert_eq!(tier(111, 1).unwrap().drain_per_second, 3.);
        assert_eq!(tier(113, 4).unwrap().drain_per_second, 36.);
        assert_eq!(tier(105, 5).unwrap().range, 2000.);
        assert_eq!(tier(107, 6).unwrap().damage_efficiency, 1.2);
        assert_eq!(tier(110, 7).unwrap().radius, 2500.);
        assert_eq!(tier(110, 7).unwrap().drain_per_second, 147.);
        assert!(tier(110, 6).is_none());
    }

    #[test]
    fn partial_shield_pulse_spends_only_available_power() {
        let rank = tier(110, 7).unwrap();
        assert_eq!(pulse(rank, 10., 1000).shield_spent, 10.);
        assert_eq!(pulse(rank, 10., 1000).damage_per_target, 13.);
        assert_eq!(pulse(rank, 500., 500).shield_spent, 73.5);
        assert_eq!(pulse(rank, 500., 1001).shield_spent, 0.);
    }

    #[test]
    fn equipped_duration_is_bounded_and_never_shortens_cast() {
        assert_eq!(duration_ms(0.), 15_000);
        assert_eq!(duration_ms(5.4), 20_400);
        assert_eq!(duration_ms(f32::NAN), 15_000);
        assert_eq!(duration_ms(90.), 75_000);
    }

    #[test]
    fn pulse_receipt_rejects_duplicate_targets_and_forged_amounts() {
        let mut receipt = InversionReceipt {
            source: 0x4000_0001,
            sector: 1,
            source_epoch: 2,
            effect: 3,
            pulse_index: 0,
            target_generation: 4,
            shield_spent: 147.,
            damage_per_target: 191.1,
            targets: vec![crate::sector_combat::MOB_ID],
        };
        assert!(receipt.valid());
        receipt.targets.push(crate::sector_combat::MOB_ID);
        assert!(!receipt.valid());
        receipt.targets.pop();
        receipt.shield_spent = f32::NAN;
        assert!(!receipt.valid());
    }

    #[test]
    fn saved_cast_emits_only_within_its_bounded_window() {
        let active = ActiveShieldInversion {
            source: 0x4000_0001,
            sector: 1,
            ability: 110,
            trained_rank: 7,
            target: crate::sector_combat::MOB_ID,
            target_generation: 2,
            source_epoch: 3,
            effect: 4,
            expires_at_ms: 16_000,
            next_pulse_ms: 2_000,
            pulse_index: 0,
        };
        assert!(!active.due(1_999));
        assert!(active.due(2_000));
        assert!(active.due(16_000));
        assert!(!active.due(16_001));
        assert!(!ActiveShieldInversion {
            target_generation: 1_000_000_000,
            ..active
        }
        .valid());
    }
}
