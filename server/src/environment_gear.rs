//! Equipped Environment Shield effect 50: skill strength and duration.
//!
//! The public item tooltip provides the bonus skill levels and seconds. The
//! original AEnvironmentalShield handler is absent, so the extra mitigation,
//! reflection, and reactor feedback per effective rank are local tuning.

pub const MAX_BONUS: f32 = 5.;
pub const MAX_DURATION_BONUS: f32 = 30.;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Strength {
    pub skill_bonus: f32,
    pub duration_bonus: f32,
}

impl Strength {
    /// Returns:
    ///     Self: Finite, bounded catalog effect for one cast.
    pub fn bounded(skill_bonus: f32, duration_bonus: f32) -> Self {
        Self {
            skill_bonus: if skill_bonus.is_finite() {
                skill_bonus.clamp(0., MAX_BONUS)
            } else {
                0.
            },
            duration_bonus: if duration_bonus.is_finite() {
                duration_bonus.clamp(0., MAX_DURATION_BONUS)
            } else {
                0.
            },
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Protection {
    pub damage: f32,
    pub reflection: f32,
    pub reactor_gain: f32,
}

/// Returns:
///     Protection: Trained-tier defenses strengthened by frozen equipped gear.
pub fn protect(trained_rank: u32, skill_bonus: f32, incoming: f32) -> Protection {
    if trained_rank == 0 || !incoming.is_finite() || incoming <= 0. {
        return Protection {
            damage: incoming,
            reflection: 0.,
            reactor_gain: 0.,
        };
    }
    let bonus = Strength::bounded(skill_bonus, 0.).skill_bonus;
    let base_reduction = if trained_rank >= 3 { 0.10 } else { 0.05 };
    let reduction = (base_reduction + 0.005 * bonus).min(0.20);
    let damage = incoming * (1. - reduction);
    let reflection = if trained_rank >= 5 {
        let base = match trained_rank {
            5 => 10.,
            6 => 25.,
            _ => 50.,
        };
        damage.min(base * (1. + bonus * 0.1))
    } else {
        0.
    };
    let reactor_gain = if trained_rank >= 6 {
        (damage * (0.05 + bonus * 0.005)).min(10. * (1. + bonus * 0.1))
    } else {
        0.
    };
    Protection {
        damage,
        reflection,
        reactor_gain,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn trained_tier_controls_families_while_gear_improves_strength() {
        let baseline = protect(3, 0., 100.);
        let geared = protect(3, 3.33, 100.);
        assert_eq!(baseline.damage, 90.);
        assert!(geared.damage < baseline.damage);
        assert_eq!(geared.reflection, 0.);
        assert_eq!(geared.reactor_gain, 0.);
        let high = protect(7, 3.33, 100.);
        assert!(high.reflection > protect(7, 0., 100.).reflection);
        assert!(high.reactor_gain > protect(7, 0., 100.).reactor_gain);
        assert_eq!(protect(0, 5., 100.).damage, 100.);
        assert_eq!(Strength::bounded(f32::INFINITY, 100.).duration_bonus, 30.);
    }
}
