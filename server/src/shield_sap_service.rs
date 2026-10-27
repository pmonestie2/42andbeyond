//! Session half of the archived Progen Warrior Shield Sap cast.
use super::*;
use crate::shield_sap::PendingShieldSap;

impl PlayerSession {
    /// Returns:
    ///     u64: Current generation of a visible, living hostile MOB.
    pub(crate) fn shield_sap_target_generation(&self, target: u32) -> io::Result<u64> {
        let index = target
            .checked_sub(MOB_ID)
            .ok_or_else(|| invalid("Select a hostile MOB for Shield Sap."))?
            as usize;
        let combat = lock_combat(&self.combat)?;
        let mob = combat
            .actors
            .get(index)
            .ok_or_else(|| invalid("Shield Sap target is missing."))?;
        if !self.mob_visible_in_scan(index, mob) || mob.state == MobState::Dead {
            return Err(invalid("Shield Sap target is not visible or alive."));
        }
        Ok(mob.death_generation)
    }

    /// Returns:
    ///     Option<PendingShieldSap>: Completed cast awaiting shared-world settlement.
    pub(crate) fn pending_shield_sap(&self) -> Option<PendingShieldSap> {
        self.skills.pending_shield_sap.clone()
    }

    /// Returns:
    ///     bool: Whether this caster can still complete a shield drain.
    pub(crate) fn shield_sap_available(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && self.sector.station.is_none()
            && !self.warp.active()
            && self.gate_transit.is_none()
            && self.mining.is_none()
    }

    /// Returns:
    ///     f32: Equipped caster shield capacity used for the native drain cap.
    pub(crate) fn shield_sap_capacity(&self) -> f32 {
        self.equipment.ship.shield_capacity
    }

    /// Clear a settled cast only after the checkpoint has reached disk.
    pub(crate) fn clear_pending_shield_sap(&mut self) -> io::Result<()> {
        let previous = self.skills.pending_shield_sap.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_shield_sap = previous;
            return Err(error);
        }
        Ok(())
    }

    /// Publish the archived target beam and drain orb for each affected MOB.
    pub(crate) fn publish_shield_sap(&mut self, effect: u32, targets: &[u32]) {
        let tick = self.tick_ms();
        for (index, target) in targets.iter().enumerate() {
            let beam = effect.wrapping_add(1 + index as u32 * 2);
            let orb = beam.wrapping_add(1);
            self.pending.push(packets::weapon_effect(
                self.player_id(),
                *target,
                139,
                beam,
                tick,
                1000,
            ));
            self.pending.push(packets::weapon_effect(
                self.player_id(),
                *target,
                146,
                orb,
                tick,
                1000,
            ));
            self.pending.push(packets::remove_effect(beam));
            self.pending.push(packets::remove_effect(orb));
        }
    }

    /// Show one confirmed group shield award on the recipient's own ship.
    pub(crate) fn publish_shield_sap_award(&mut self, source: u32, effect: u32) {
        let target = self.player_id();
        let tick = self.tick_ms();
        self.pending.push(packets::weapon_effect(
            source, target, 146, effect, tick, 1000,
        ));
        self.pending.push(packets::remove_effect(effect));
    }
}
