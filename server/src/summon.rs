//! Jenquai Defender Summon geometry from the public Net-7 skill table.
//! Costs and cast timing are local tuning because the archived server has no
//! executable Summon handler.

/// Public Black Peg Leg equipped effects grant at most +3.5 levels today.
pub const MAX_EQUIPPED_BONUS: f32 = 5.;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SummonTarget {
    Enemy,
    Friend,
    Group,
    EnemyFormation,
    ReturnFriend,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SummonTier {
    pub rank: u32,
    pub target: SummonTarget,
    pub range: f32,
    pub energy: f32,
    pub charge_seconds: f32,
    pub cooldown_seconds: f32,
}

/// Returns:
///     Option<SummonTier>: Rules for an ability unlocked by the trained rank.
pub fn tier(ability: u32, trained_rank: u32) -> Option<SummonTier> {
    let (rank, target, energy) = match ability {
        117 => (1, SummonTarget::Enemy, 35.),
        119 => (3, SummonTarget::Friend, 75.),
        120 => (5, SummonTarget::Group, 150.),
        118 => (6, SummonTarget::EnemyFormation, 300.),
        97 => (7, SummonTarget::ReturnFriend, 600.),
        _ => return None,
    };
    if trained_rank < rank || trained_rank > 7 {
        return None;
    }
    let range = if target == SummonTarget::Group {
        250_000.
    } else {
        5_000. + 250. * (trained_rank - 1) as f32
    };
    Some(SummonTier {
        rank,
        target,
        range,
        energy,
        charge_seconds: 3.,
        cooldown_seconds: 5.,
    })
}

/// Returns:
///     f32: Trained range extended by frozen equipped skill levels.
pub fn effective_range(tier: SummonTier, equipped_bonus: f32) -> f32 {
    if tier.target == SummonTarget::Group {
        return tier.range;
    }
    let bonus = if equipped_bonus.is_finite() {
        equipped_bonus.clamp(0., MAX_EQUIPPED_BONUS)
    } else {
        0.
    };
    tier.range + 250. * bonus
}

/// Returns:
///     [f32; 3]: A bounded arrival point in front of the caster.
pub fn arrival_position(
    caster: [f32; 3],
    facing: [f32; 3],
    minimum: [f32; 3],
    maximum: [f32; 3],
) -> [f32; 3] {
    let length = facing.iter().map(|part| part * part).sum::<f32>().sqrt();
    let direction = if length > 0.001 && length.is_finite() {
        facing.map(|part| part / length)
    } else {
        [0., 0., 1.]
    };
    std::array::from_fn(|axis| {
        (caster[axis] + direction[axis] * 150.).clamp(minimum[axis], maximum[axis])
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_native_tiers_and_rank_progression() {
        assert_eq!(tier(117, 1).unwrap().range, 5000.);
        assert_eq!(tier(117, 7).unwrap().range, 6500.);
        assert_eq!(tier(119, 3).unwrap().target, SummonTarget::Friend);
        assert_eq!(tier(120, 5).unwrap().range, 250_000.);
        assert_eq!(tier(118, 6).unwrap().target, SummonTarget::EnemyFormation);
        assert_eq!(tier(97, 7).unwrap().target, SummonTarget::ReturnFriend);
        assert!(tier(97, 6).is_none());
        assert!(tier(117, 8).is_none());
        assert_eq!(effective_range(tier(117, 7).unwrap(), 3.5), 7_375.);
        assert_eq!(effective_range(tier(120, 7).unwrap(), 3.5), 250_000.);
        assert_eq!(
            effective_range(tier(119, 3).unwrap(), f32::INFINITY),
            5_500.
        );
    }

    #[test]
    fn arrival_stays_in_the_sector() {
        assert_eq!(
            arrival_position([990., 20., 20.], [1., 0., 0.], [0.; 3], [1000.; 3]),
            [1000., 20., 20.]
        );
        assert_eq!(
            arrival_position([1., 1., 1.], [0.; 3], [0.; 3], [1000.; 3]),
            [1., 1., 151.]
        );
    }
}
