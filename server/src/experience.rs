//! Earned XP follows the archived fifty-level curve and skill-point bands.
//! Existing sandbox characters begin at level 50 on each track; their earned
//! totals still fill the level-50 bar and grant repeatable cap skill points.
use crate::protocol::invalid;
use std::io;

pub const MAX_LEVEL: u32 = 50;
pub const LEVEL_XP: [u64; 51] = [
    500, 1000, 1500, 2000, 3000, 4000, 5000, 7000, 9000, 11000, 13000, 15000, 20000, 25000, 30000,
    35000, 40000, 47500, 55000, 62500, 70000, 77500, 87500, 97500, 107500, 117500, 127500, 140000,
    152500, 165000, 177500, 190000, 205000, 220000, 235000, 250000, 265000, 285000, 305000, 325000,
    345000, 365000, 390000, 415000, 440000, 475000, 510000, 545000, 595000, 645000, 645000,
];

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TrackProgress {
    pub level: u32,
    pub fraction: f32,
    pub skill_points: u64,
}

/// Derive the current level, bar and earned points from durable total XP.
///
/// Returns:
///     TrackProgress: Current level, native fractional bar and earned points.
///
/// Raises:
///     io::Error: The starting level is outside the native range.
pub fn track(start_level: u32, earned_xp: u64) -> io::Result<TrackProgress> {
    if start_level > MAX_LEVEL {
        return Err(invalid("Invalid starting XP level."));
    }
    let mut level = start_level;
    let mut remaining = earned_xp;
    let mut skill_points = 0u64;
    while level < MAX_LEVEL && remaining >= LEVEL_XP[level as usize] {
        remaining -= LEVEL_XP[level as usize];
        skill_points += if level <= 19 {
            1
        } else if level <= 39 {
            2
        } else {
            3
        };
        level += 1;
    }
    if level == MAX_LEVEL {
        skill_points += remaining / LEVEL_XP[MAX_LEVEL as usize];
        remaining %= LEVEL_XP[MAX_LEVEL as usize];
    }
    Ok(TrackProgress {
        level,
        fraction: remaining as f32 / LEVEL_XP[level as usize] as f32,
        skill_points,
    })
}

/// Apply the archived MOB level-difference combat XP table.
///
/// Returns:
///     u64: Base kill award before future group-sharing rules.
pub fn mob_xp(mob_level: u32, combat_level: u32) -> u64 {
    let difference = mob_level as i32 - combat_level as i32;
    match difference {
        ..=-11 => 0,
        -10..=-4 => 100,
        -3 => 250,
        -2 => 500,
        -1 => 800,
        0 => 1000,
        1 => 1200,
        2 => 1500,
        3 => 1750,
        4 => 2000,
        5 => 3000,
        6 => 4000,
        7 => 5000,
        _ => 8000,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Compare threshold and point boundaries with archived level bands.
    #[test]
    fn archived_curve_awards_points_at_each_band_and_caps_levels() {
        assert_eq!(track(0, 499).unwrap().level, 0);
        assert_eq!(track(0, 500).unwrap().skill_points, 1);
        let to_twenty = LEVEL_XP[..20].iter().sum::<u64>();
        assert_eq!(track(0, to_twenty).unwrap().skill_points, 20);
        assert_eq!(track(0, to_twenty + LEVEL_XP[20]).unwrap().skill_points, 22);
        let to_forty = LEVEL_XP[..40].iter().sum::<u64>();
        assert_eq!(track(0, to_forty + LEVEL_XP[40]).unwrap().skill_points, 63);
        let to_fifty = LEVEL_XP[..50].iter().sum::<u64>();
        assert_eq!(track(0, to_fifty).unwrap().level, 50);
        assert_eq!(track(0, to_fifty).unwrap().skill_points, 90);
        assert_eq!(track(50, 2 * 645_000 + 322_500).unwrap().skill_points, 2);
        assert_eq!(track(50, 2 * 645_000 + 322_500).unwrap().fraction, 0.5);
        assert!(track(51, 0).is_err());
    }

    /// Existing inventory JSON gains the sandbox baseline without losing XP.
    #[test]
    fn legacy_inventory_experience_defaults_to_level_fifty() {
        let items = crate::items::ItemCatalog::load().unwrap();
        let mut value =
            serde_json::to_value(crate::inventory::Inventory::starter(&items).unwrap()).unwrap();
        let map = value.as_object_mut().unwrap();
        map.remove("xp_start_level");
        map.remove("combat_xp");
        map.insert("explore_xp".into(), serde_json::json!(1234));
        let inventory: crate::inventory::Inventory = serde_json::from_value(value).unwrap();
        assert_eq!(inventory.xp_start_level, MAX_LEVEL);
        assert_eq!(inventory.combat_xp, 0);
        assert_eq!(inventory.explore_xp, 1234);
        assert_eq!(
            track(inventory.xp_start_level, inventory.explore_xp)
                .unwrap()
                .level,
            50
        );
    }

    /// Preserve the archived relative-level combat XP breakpoints.
    #[test]
    fn mob_awards_follow_archived_relative_level_table() {
        assert_eq!(mob_xp(39, 50), 0);
        for (difference, expected) in [
            (-10, 100),
            (-4, 100),
            (-3, 250),
            (-2, 500),
            (-1, 800),
            (0, 1000),
            (1, 1200),
            (2, 1500),
            (3, 1750),
            (4, 2000),
            (5, 3000),
            (6, 4000),
            (7, 5000),
            (8, 8000),
            (20, 8000),
        ] {
            assert_eq!(mob_xp((50 + difference) as u32, 50), expected);
        }
    }
}
