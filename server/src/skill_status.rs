//! Native ship status presentation for sustained class abilities. Equipment,
//! peer-device and Rally slots occupy 0–5; class statuses occupy 6–12.
use super::*;

pub(super) const CLOAK_SLOT: usize = 6;
pub(super) const ENVIRONMENT_SLOT: usize = 7;
pub(super) const COMBAT_CLOAK_SLOT: usize = 8;
pub(super) const SHIELD_INVERSION_SLOT: usize = 15;

impl PlayerSession {
    /// Publish current cloak status; replay its existing visual after replacement.
    pub(super) fn publish_cloak_status(&mut self, replay_visual: bool) {
        if self.skills.cloak_rank == 0 {
            return;
        }
        self.pending.push(packets::ship_status_buff_slot(
            self.player_id(),
            CLOAK_SLOT,
            "Cloak",
            true,
            0,
        ));
        if replay_visual {
            if let Some(effect) = self.skills.cloak_effect {
                let asset = match self.skills.cloak_rank {
                    1 => 180,
                    3 => 181,
                    5 => 182,
                    6 => 183,
                    _ => 184,
                };
                self.pending.push(packets::object_effect(
                    self.player_id(),
                    effect,
                    asset,
                    self.tick_ms(),
                ));
            }
        }
    }

    /// Publish current Environment Shield duration and optional resumed visual.
    pub(super) fn publish_environment_status(&mut self, replay_visual: bool) {
        if self.skills.environment_rank == 0 || self.skills.environment_seconds <= 0. {
            return;
        }
        self.pending.push(packets::ship_active_buff_slot(
            self.player_id(),
            ENVIRONMENT_SLOT,
            "Environment_Shield",
            self.tick_ms()
                .wrapping_add((self.skills.environment_seconds * 1000.) as u32),
        ));
        if replay_visual {
            if let Some(effect) = self.skills.environment_effect {
                self.pending.push(packets::object_effect(
                    self.player_id(),
                    effect,
                    216,
                    self.tick_ms(),
                ));
            }
        }
    }

    /// Publish the real five-second post-cloak weapon damage window.
    pub(super) fn publish_combat_cloak_status(&mut self) {
        if self.skills.combat_cloak_seconds > 0. {
            self.pending.push(packets::ship_active_buff_slot(
                self.player_id(),
                COMBAT_CLOAK_SLOT,
                "Combat_Cloak",
                self.tick_ms()
                    .wrapping_add((self.skills.combat_cloak_seconds * 1000.) as u32),
            ));
        }
    }

    /// Replay all sustained class statuses after a short session replacement.
    pub(super) fn publish_skill_status_resume(&mut self) {
        self.publish_cloak_status(true);
        self.publish_environment_status(true);
        self.publish_combat_cloak_status();
        self.publish_equipment_repair_resume();
        self.publish_power_down_resume();
        self.publish_inversion_resume();
    }
}
