//! Durable per-pilot half of the archived Recharge Shields ability.
use super::*;
use crate::energy_ledger::ShieldBalance;
use crate::skills::PendingShieldRecharge;

impl PlayerSession {
    /// Returns:
    ///     Option<PendingShieldRecharge>: Completed cast awaiting world settlement.
    pub(crate) fn pending_shield_recharge(&self) -> Option<PendingShieldRecharge> {
        self.skills.pending_shield_recharge.clone()
    }

    /// Returns:
    ///     u64: Authenticated lifetime of the cast's owner.
    pub(crate) fn shield_recharge_epoch(&self) -> u64 {
        self.session_epoch
    }

    /// Returns:
    ///     (f32, f32): Current shield points and equipped capacity.
    pub(crate) fn shield_balance(&self) -> (f32, f32) {
        (self.shield, self.equipment.ship.shield_capacity)
    }

    /// Returns:
    ///     bool: Whether this selected pilot remains in the caster's peer view.
    pub(crate) fn can_recharge_peer(&self, target: u32) -> bool {
        target == self.player_id() || self.peer_ids.contains(&target)
    }

    /// Returns:
    ///     bool: Whether this pilot can give or receive a shield recharge now.
    pub(crate) fn shield_recharge_available(&self) -> bool {
        self.logged_in
            && self.hull > 0.
            && self.sector.station.is_none()
            && !self.warp.active()
            && self.gate_transit.is_none()
    }

    /// Returns:
    ///     u64: Last world shield revision applied to this pilot.
    pub(crate) fn shield_recharge_revision(&self) -> u64 {
        self.shield_revision
    }

    /// Apply a committed shield amount before publishing its AUX update.
    pub(crate) fn apply_shield_balance(&mut self, balance: ShieldBalance) -> io::Result<()> {
        if balance.revision <= self.shield_revision {
            return Ok(());
        }
        self.shield = balance.shield.min(self.equipment.ship.shield_capacity);
        self.shield_revision = balance.revision;
        let checkpoint = self.checkpoint_session(true);
        if self.logged_in {
            self.pending.push(packets::ship_resources(
                self.player_id(),
                &self.equipment.ship,
                None,
                Some(self.shield),
                None,
            ));
        }
        checkpoint
    }

    /// Remove a settled or invalid cast only after its checkpoint is updated.
    pub(crate) fn clear_shield_recharge(&mut self) -> io::Result<()> {
        let pending = self.skills.pending_shield_recharge.take();
        if let Err(error) = self.checkpoint_session(true) {
            self.skills.pending_shield_recharge = pending;
            return Err(error);
        }
        Ok(())
    }

    /// Publish the archived self flash or target beam and shield orb.
    pub(crate) fn publish_shield_recharge(&mut self, target: u32, effect: u32, self_rank: bool) {
        let tick = self.tick_ms();
        if self_rank {
            let flash = effect.wrapping_add(1);
            self.pending
                .push(packets::object_effect(target, flash, 136, tick));
            self.pending.push(packets::remove_effect(flash));
        } else {
            self.pending
                .push(packets::beam(self.player_id(), target, 139, effect, tick));
            self.pending.push(packets::beam(
                self.player_id(),
                target,
                166,
                effect.wrapping_add(1),
                tick,
            ));
        }
        self.message("Recharge Shields completed.");
    }

    /// Set a disposable pilot's shield points before a two-session ability test.
    #[cfg(test)]
    pub(crate) fn set_shield_for_test(&mut self, shield: f32) {
        assert!((0. ..=self.equipment.ship.shield_capacity).contains(&shield));
        self.shield = shield;
    }
}
