//! Movement-gated Progen Warrior Combat Trance and native status presentation.
//!
//! The original Player Guide and Net-7 skill description agree that Trance
//! starts at a complete stop and lingers three seconds per effective rank.
//! Archived PlayerSkills.cpp supplies the stat formulas but omits movement
//! gating. The installed native client's effect.ini names descriptors 669–673
//! for the five current Trance tiers; descriptor 407 is explicitly former.
use super::*;

pub(super) const COMBAT_TRANCE_SLOT: usize = 9;
const VISUAL_ID_BASE: u32 = 0x6b00_0000;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub(super) struct CombatTrance {
    pub active: bool,
    pub stationary: bool,
    pub linger_until_ms: u64,
    pub visual_descriptor: u16,
}

impl CombatTrance {
    /// Returns:
    ///     bool: Whether the server observes a living ship at a full stop in space.
    fn stopped(&self, motion: &Motion, eligible: bool) -> bool {
        eligible && motion.speed.abs() < 0.1 && motion.set_speed.abs() < 0.1
    }

    /// Drop an expired linger before restored stats and visuals are replayed.
    pub(super) fn reconcile_replacement(
        &mut self,
        now_ms: u64,
        eligible: bool,
        motion: &Motion,
        effective_rank: f32,
    ) {
        let was_stationary = self.stationary;
        self.stationary = self.stopped(motion, eligible);
        if self.active && was_stationary && !self.stationary && effective_rank > 0. {
            self.linger_until_ms = now_ms.saturating_add((effective_rank * 3000.).ceil() as u64);
        }
        if !self.stationary && now_ms >= self.linger_until_ms {
            self.active = false;
            self.visual_descriptor = 0;
        }
    }
}

/// Returns:
///     u16: Native effect.ini descriptor for the highest trained Trance tier.
fn tier_visual(trained_rank: u32) -> u16 {
    match trained_rank {
        7.. => 671,   // Regeneration Trance, BaseAssetID 1816
        6 => 669,     // Power Trance, BaseAssetID 1814
        5 => 673,     // Focus Trance, BaseAssetID 1818
        3..=4 => 670, // Resistance Trance, BaseAssetID 1815
        1..=2 => 672, // Accuracy Trance, BaseAssetID 1817
        _ => 0,
    }
}

impl PlayerSession {
    /// Returns:
    ///     u32: Stable native effect identity across short server replacements.
    pub(super) fn combat_trance_visual_id(&self) -> u32 {
        VISUAL_ID_BASE | (self.player_id() & 0x00ff_ffff)
    }

    /// Publish the current icon and tier visual after authenticated replacement.
    pub(super) fn publish_combat_trance_resume(&mut self) {
        if !self.combat_trance.active {
            return;
        }
        let removal_tick = if self.combat_trance.stationary {
            0
        } else {
            self.tick_ms().wrapping_add(
                self.combat_trance
                    .linger_until_ms
                    .saturating_sub(crate::resources::unix_ms()) as u32,
            )
        };
        self.pending.push(packets::ship_status_buff_slot(
            self.player_id(),
            COMBAT_TRANCE_SLOT,
            "Combat_Trance",
            self.combat_trance.stationary,
            removal_tick,
        ));
        if self.combat_trance.visual_descriptor != 0 {
            self.pending.push(packets::object_effect(
                self.player_id(),
                self.combat_trance_visual_id(),
                self.combat_trance.visual_descriptor,
                self.tick_ms(),
            ));
        }
    }

    /// Reconcile motion with Trance bonuses, linger, native icon and visual.
    pub(super) fn advance_combat_trance(&mut self) -> io::Result<()> {
        let now_ms = crate::resources::unix_ms();
        let available = self.sector.station.is_none() && self.hull > 0.;
        let stopped = self
            .combat_trance
            .stopped(&self.motion, available && !self.warp.active());
        let trained_rank = self.skill_catalog.level(13);
        let effective_rank = self.equipment.combat_trance_rank;
        let previous = self.combat_trance.clone();
        if !available || trained_rank == 0 || effective_rank <= 0. {
            self.combat_trance.active = false;
            self.combat_trance.stationary = false;
            self.combat_trance.linger_until_ms = 0;
            self.combat_trance.visual_descriptor = 0;
        } else {
            if previous.stationary && !stopped {
                self.combat_trance.linger_until_ms =
                    now_ms.saturating_add((effective_rank * 3000.).ceil() as u64);
            }
            self.combat_trance.stationary = stopped;
            self.combat_trance.active = stopped || now_ms < self.combat_trance.linger_until_ms;
            self.combat_trance.visual_descriptor = if self.combat_trance.active {
                tier_visual(trained_rank)
            } else {
                0
            };
        }
        if previous.active != self.combat_trance.active {
            self.refresh_equipment()?;
        }
        if previous.visual_descriptor != self.combat_trance.visual_descriptor
            && previous.visual_descriptor != 0
        {
            self.pending
                .push(packets::remove_effect(self.combat_trance_visual_id()));
        }
        if previous.active != self.combat_trance.active
            || previous.stationary != self.combat_trance.stationary
        {
            if self.combat_trance.active {
                self.pending.push(packets::ship_status_buff_slot(
                    self.player_id(),
                    COMBAT_TRANCE_SLOT,
                    "Combat_Trance",
                    stopped,
                    if stopped {
                        0
                    } else {
                        self.tick_ms().wrapping_add(
                            self.combat_trance.linger_until_ms.saturating_sub(now_ms) as u32,
                        )
                    },
                ));
            } else if previous.active {
                self.pending.push(packets::ship_active_buff_slot(
                    self.player_id(),
                    COMBAT_TRANCE_SLOT,
                    "",
                    0,
                ));
            }
        }
        if previous.visual_descriptor != self.combat_trance.visual_descriptor
            && self.combat_trance.visual_descriptor != 0
        {
            self.pending.push(packets::object_effect(
                self.player_id(),
                self.combat_trance_visual_id(),
                self.combat_trance.visual_descriptor,
                self.tick_ms(),
            ));
        }
        if previous.active != self.combat_trance.active
            || previous.stationary != self.combat_trance.stationary
            || previous.visual_descriptor != self.combat_trance.visual_descriptor
        {
            self.checkpoint_session(true)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Current effect.ini descriptors follow trained skill tiers, not gear-only levels.
    #[test]
    fn current_native_visual_tiers_and_full_stop() {
        for (rank, descriptor) in [
            (0, 0),
            (1, 672),
            (2, 672),
            (3, 670),
            (4, 670),
            (5, 673),
            (6, 669),
            (7, 671),
        ] {
            assert_eq!(tier_visual(rank), descriptor);
        }
        let mut motion = Motion {
            turn: 1.,
            ..Motion::default()
        };
        assert!(CombatTrance::default().stopped(&motion, true));
        motion.set_speed = 75.;
        assert!(!CombatTrance::default().stopped(&motion, true));
        motion.set_speed = 0.;
        motion.speed = 0.2;
        assert!(!CombatTrance::default().stopped(&motion, true));
    }

    /// A replacement does not revive expired moving Trance or lose a just-started linger.
    #[test]
    fn replacement_reconciles_linger_against_saved_motion() {
        let now = 100_000;
        let moving = Motion {
            speed: 75.,
            set_speed: 75.,
            ..Motion::default()
        };
        let mut expired = CombatTrance {
            active: true,
            stationary: false,
            linger_until_ms: now - 1,
            visual_descriptor: 671,
        };
        expired.reconcile_replacement(now, true, &moving, 7.);
        assert!(!expired.active);
        assert_eq!(expired.visual_descriptor, 0);
        let mut interrupted = CombatTrance {
            active: true,
            stationary: true,
            linger_until_ms: 0,
            visual_descriptor: 671,
        };
        interrupted.reconcile_replacement(now, true, &moving, 7.);
        assert!(interrupted.active);
        assert_eq!(interrupted.linger_until_ms, now + 21_000);
    }
}
