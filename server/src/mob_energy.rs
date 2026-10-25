//! Local MOB reactor model needed to make Energy Leech affect weapon fire.
//!
//! The archived MOB sim has no numeric reactor capacity or per-shot cost. These
//! functions are local server tuning, not source-backed native constants.

pub const SHOT_COST_FRACTION: f32 = 0.8;
pub const MAX_CAPACITY: f32 = 10_000_000.;

/// Returns:
///     f32: Finite MOB reactor capacity scaled to its existing damage curve.
pub fn capacity(dps: f32) -> f32 {
    if !dps.is_finite() || dps <= 0. {
        100.
    } else {
        (dps * 8.).clamp(100., MAX_CAPACITY)
    }
}

/// Returns:
///     f32: Energy needed for one authoritative NPC weapon launch.
pub fn shot_cost(capacity: f32) -> f32 {
    if !capacity.is_finite() || capacity <= 0. || capacity > MAX_CAPACITY {
        0.
    } else {
        capacity * SHOT_COST_FRACTION
    }
}

/// Returns:
///     f32: Reactor power recovered during one bounded simulation step.
pub fn recharge(current: f32, capacity: f32, reload_seconds: f32, dt: f32) -> f32 {
    if !current.is_finite()
        || !capacity.is_finite()
        || capacity <= 0.
        || capacity > MAX_CAPACITY
        || current < 0.
        || !reload_seconds.is_finite()
        || reload_seconds <= 0.
        || !dt.is_finite()
        || dt < 0.
    {
        return 0.;
    }
    let rate = shot_cost(capacity) / reload_seconds.max(0.1);
    (current + rate * dt).min(capacity)
}

/// Returns:
///     Option<f32>: Remaining power if the NPC can afford one weapon shot.
pub fn spend_shot(current: f32, capacity: f32) -> Option<f32> {
    let cost = shot_cost(capacity);
    if cost <= 0. || !current.is_finite() || current < cost {
        None
    } else {
        Some((current - cost).max(0.))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn undrained_mob_keeps_native_reload_cadence() {
        let cap = capacity(100.);
        let after = spend_shot(cap, cap).unwrap();
        assert!((after - 160.).abs() < 0.001);
        assert!((recharge(after, cap, 4., 4.) - cap).abs() < 0.001);
    }

    #[test]
    fn leech_defers_a_ready_shot_until_power_recovers() {
        let cap = capacity(100.);
        let drained = cap - cap * 0.45;
        assert!(spend_shot(drained, cap).is_none());
        assert!(spend_shot(recharge(drained, cap, 4., 1.), cap).is_none());
        assert!(spend_shot(recharge(drained, cap, 4., 1.25), cap).is_some());
        assert_eq!(capacity(f32::NAN), 100.);
        assert!(spend_shot(f32::NAN, cap).is_none());
    }
}
