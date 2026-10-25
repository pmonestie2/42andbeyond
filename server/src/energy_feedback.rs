//! The public catalog spells this equipped stat `STAT_ENEREGY_FEEDBACK`.
//! It reflects a fraction of incoming Energy damage to the attacker. The
//! Greater tooltip explicitly limits one return to 27.4 damage; use that
//! conservative ceiling for both public variants in the absence of an
//! executable archived handler.

pub const STAT: &str = "STAT_ENEREGY_FEEDBACK";
pub const MAX_PERCENT: f32 = 30.;
pub const MAX_REFLECTION: f32 = 27.4;

/// Returns:
///     f32: Bounded Energy reflection from one post-deflect NPC hit.
pub fn reflected(post_deflect_damage: f32, damage_type: usize, percent: f32) -> f32 {
    if damage_type != crate::combat_damage::DamageType::Energy.index()
        || !post_deflect_damage.is_finite()
        || post_deflect_damage <= 0.
        || !percent.is_finite()
    {
        return 0.;
    }
    (post_deflect_damage * percent.clamp(0., MAX_PERCENT) / 100.).min(MAX_REFLECTION)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn feedback_is_energy_only_and_bounded_by_catalog_greater_limit() {
        assert!((reflected(100., 3, 14.3) - 14.3).abs() < 0.0001);
        assert_eq!(reflected(100., 2, 14.3), 0.);
        assert_eq!(reflected(1000., 3, 14.3), 27.4);
        assert_eq!(reflected(f32::NAN, 3, 14.3), 0.);
        assert_eq!(reflected(100., 3, f32::NAN), 0.);
    }
}
