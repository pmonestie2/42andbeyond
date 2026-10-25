//! Public equipped damage-control effects alter structure wear after a hull hit.
//! The archived combat path has no executable wear formula, so the existing
//! locally tuned hull-hit wear remains the base. Catalog percentages adjust only
//! the matching equipment category, not hull or shield HP.

/// The public modifier families match these native equipped slot groups.
pub const STATS: [&str; 5] = [
    "STAT_EQUIPMENT_DAMAGE_CONTROL_SHIELD",
    "STAT_EQUIPMENT_DAMAGE_CONTROL_REACTOR",
    "STAT_EQUIPMENT_DAMAGE_CONTROL_ENGINE",
    "STAT_EQUIPMENT_DAMAGE_CONTROL_WEAPONS",
    "STAT_EQUIPMENT_DAMAGE_CONTROL_DEVICES",
];

/// Returns:
///     usize: Damage-control category for one native equipped slot.
pub fn category(slot: usize) -> usize {
    match slot {
        0..=2 => slot,
        3..=8 => 3,
        _ => 4,
    }
}

/// Limit catalog damage tolerance so structure wear stays finite and useful.
///
/// Returns:
///     f32: Multiplier applied to the frozen hull-hit equipment wear amount.
pub fn wear_multiplier(effectiveness_percent: f32) -> f32 {
    100. / effectiveness_percent.clamp(25., 400.)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_equipped_slots_have_distinct_damage_control_categories() {
        assert_eq!(
            (0..20).map(category).collect::<Vec<_>>(),
            [0, 1, 2, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4]
        );
    }

    #[test]
    fn public_percentages_adjust_only_bounded_wear() {
        assert_eq!(wear_multiplier(100.), 1.);
        assert!((wear_multiplier(158.) - 100. / 158.).abs() < 0.0001);
        assert_eq!(wear_multiplier(80.), 1.25);
        assert_eq!(wear_multiplier(475.), 0.25);
    }
}
